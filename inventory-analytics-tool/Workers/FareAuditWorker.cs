using inventory_analytics_tool.Helpers;
using inventory_analytics_tool.Models;
using inventory_analytics_tool.Repositories;
using Microsoft.Extensions.Caching.Memory;
using System.Text.Json;

namespace inventory_analytics_tool.Workers
{
    public class FareAuditWorker : BackgroundService
    {
        private readonly ILogger<FareAuditWorker> _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IMemoryCache _cache;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _config;
        private readonly TimeSpan _refreshInterval = TimeSpan.FromHours(4);

        public FareAuditWorker(ILogger<FareAuditWorker> logger, IServiceScopeFactory scopeFactory, IMemoryCache cache, IHttpClientFactory httpClientFactory, IConfiguration config)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;
            _cache = cache;
            _httpClientFactory = httpClientFactory;
            _config = config;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    _logger.LogInformation("Audit Worker running... Fetching 1 year of FUTURE travel data in 2-day batches.");

                    _cache.TryGetValue("FDestList", out List<FDestData> cachedFDest);

                    if (cachedFDest == null || !cachedFDest.Any())
                    {
                        _logger.LogInformation("FDest cache is empty. FareAuditWorker is fetching it directly...");
                        try
                        {
                            string systemToken = _config["AzureApi:SystemToken"] ?? "YOUR_PERMANENT_SYSTEM_TOKEN";
                            if (systemToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                            {
                                systemToken = systemToken.Substring("Bearer ".Length).Trim();
                            }

                            var httpClient = _httpClientFactory.CreateClient();
                            httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", systemToken);

                            var destResponse = await httpClient.GetAsync("https://shivent.azurewebsites.net/fdestination-list", stoppingToken);

                            if (destResponse.IsSuccessStatusCode)
                            {
                                var destJson = await destResponse.Content.ReadAsStringAsync();
                                var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

                                var destData = JsonSerializer.Deserialize<FDestApiResponse>(destJson, jsonOptions);

                                if (destData?.Data != null && destData.Data.Any())
                                {
                                    var destCacheOptions = new MemoryCacheEntryOptions()
                                        .SetAbsoluteExpiration(TimeSpan.FromDays(30))
                                        .SetPriority(CacheItemPriority.NeverRemove);

                                    _cache.Set("FDestList", destData.Data, destCacheOptions);
                                    cachedFDest = destData.Data;

                                    _logger.LogInformation($"FareAuditWorker successfully cached {cachedFDest.Count} destinations.");
                                }
                            }
                            else
                            {
                                _logger.LogWarning($"FareAuditWorker destination fetch failed with status: {destResponse.StatusCode}");
                            }
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError($"FareAuditWorker failed to fetch fallback FDest data: {ex.Message}");
                        }
                    }

                    if (cachedFDest == null)
                    {
                        cachedFDest = new List<FDestData>();
                    }

                    using (var scope = _scopeFactory.CreateScope())
                    {
                        var repository = scope.ServiceProvider.GetRequiredService<IVendorPurchaseRepository>();
                        var dbHelper = scope.ServiceProvider.GetRequiredService<IDatabaseHelper>();

                        // ==========================================
                        // CRITICAL CHANGE: Fetch 1 Year into the Future
                        // ==========================================
                        DateTime currentStart = DateTime.Today;
                        DateTime finalEnd = DateTime.Today.AddMonths(7);

                        List<PnrData> allPnrData = new List<PnrData>();

                        while (currentStart <= finalEnd)
                        {
                            // Create a 2-day batch
                            DateTime currentEnd = currentStart.AddDays(2);
                            if (currentEnd > finalEnd) currentEnd = finalEnd;

                            string fromStr = currentStart.ToString("yyyy/MM/dd", System.Globalization.CultureInfo.InvariantCulture);
                            string toStr = currentEnd.ToString("yyyy/MM/dd", System.Globalization.CultureInfo.InvariantCulture);

                            _logger.LogInformation($"[DEBUG] Attempting to fetch Vendor API Travel Data for: {fromStr} to {toStr}");

                            try
                            {
                                var batchData = await repository.GetPnrDetailsAsync(fromStr, toStr);

                                if (batchData != null && batchData.Any())
                                {
                                    allPnrData.AddRange(batchData);
                                    _logger.LogInformation($"[DEBUG] Successfully fetched {batchData.Count()} PNRs for {fromStr} to {toStr}");
                                }
                            }
                            catch (Exception batchEx)
                            {
                                var rootCause = batchEx.InnerException != null ? batchEx.InnerException.Message : batchEx.Message;
                                _logger.LogError($"[DEBUG] Vendor API failed for dates {fromStr} to {toStr}. Error: {rootCause}");
                            }

                            // Move currentStart to the exact next day after currentEnd
                            currentStart = currentEnd.AddDays(1);

                            // Give the production server a breather
                            await Task.Delay(2000, stoppingToken);
                        }

                        if (allPnrData.Any())
                        {
                            var liveFaresCache = await BuildLiveFaresCacheAsync(allPnrData, dbHelper);
                            var leadDashboardData = ProcessLeadLevelData(allPnrData, cachedFDest, liveFaresCache, _cache);
                            var sectorDashboardData = ProcessSectorLevelData(allPnrData, cachedFDest, liveFaresCache);

                            var cacheOptions = new MemoryCacheEntryOptions().SetPriority(CacheItemPriority.NeverRemove);
                            _cache.Set("LeadLevelAuditData", leadDashboardData, cacheOptions);
                            _cache.Set("SectorLevelAuditData", sectorDashboardData, cacheOptions);

                            _logger.LogInformation($"Level 1 & 2 Caches updated: {leadDashboardData.Count} leads & {sectorDashboardData.Count} sectors from {allPnrData.Count} future travel PNRs.");
                        }
                        else
                        {
                            _logger.LogWarning("No PNR data fetched from Vendor API. Skipping Level 1 & 2 cache updates.");
                        }

                        // ==========================================
                        // DECOUPLED LEVEL 3: Sector Details Database
                        // ==========================================
                        try
                        {
                            // Note: We might need to update this DB query if you want future DB logs too!
                            var rawDbLogs = await dbHelper.GetRecentFareAuditLogsAsync();
                            if (rawDbLogs != null && rawDbLogs.Any())
                            {
                                var detailedAuditData = rawDbLogs
                                     .Where(x => !string.IsNullOrWhiteSpace(x.flightSector))
                                     .GroupBy(x => x.flightSector.Replace(" ", "").Replace("-", "").Replace("/", "").Trim().ToUpper())
                                     .ToDictionary(g => g.Key, g => g.ToList());

                                var cacheOptions = new MemoryCacheEntryOptions().SetPriority(CacheItemPriority.NeverRemove);
                                _cache.Set("DetailedPnrAuditData", detailedAuditData, cacheOptions);

                                _logger.LogInformation($"Level 3 Cache updated with {rawDbLogs.Count} detailed DB logs.");
                            }
                            else
                            {
                                _logger.LogWarning("No FareAudit logs found in the database.");
                            }
                        }
                        catch (Exception dbEx)
                        {
                            _logger.LogError($"Failed to fetch Sector Details from DB: {dbEx.Message}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    var rootCause = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
                    _logger.LogError($"Audit Worker Error: {ex.GetType().Name} - Root Cause: {rootCause}");
                    _logger.LogError($"Stack Trace: {ex.StackTrace}");
                }

                await Task.Delay(_refreshInterval, stoppingToken);
            }
        }

        private async Task<Dictionary<string, double>> BuildLiveFaresCacheAsync(List<PnrData> rawPnrData, IDatabaseHelper dbHelper)
        {
            var uniqueSectors = rawPnrData
                .Where(p => p.OnlineSearch != null && p.TravelDateTime.HasValue)
                .Select(p => $"{p.OnlineSearch.SectorFromCode.Trim()}-{p.OnlineSearch.SectorToCode.Trim()}")
                .Distinct().ToList();

            var liveFaresCache = new Dictionary<string, double>();

            foreach (var sector in uniqueSectors)
            {
                var sectorPnrs = rawPnrData.Where(p => p.TravelDateTime.HasValue && $"{p.OnlineSearch.SectorFromCode.Trim()}-{p.OnlineSearch.SectorToCode.Trim()}" == sector).ToList();
                if (!sectorPnrs.Any()) continue;

                var minDate = sectorPnrs.Min(p => p.TravelDateTime.Value);
                var maxDate = sectorPnrs.Max(p => p.TravelDateTime.Value);

                var dbFares = await dbHelper.GetBulkFlightLatestFaresAsync(sector, minDate, maxDate);

                var lowestFaresByDate = dbFares
                    .GroupBy(f => f.TravelDate.Date)
                    .ToDictionary(g => g.Key, g => g.Min(f => f.LatestFare));

                foreach (var kvp in lowestFaresByDate)
                {
                    liveFaresCache[$"{sector}_{kvp.Key:yyyy-MM-dd}"] = kvp.Value;
                }
            }
            return liveFaresCache;
        }

        private string GetAssignedLead(PnrData pnr, List<FDestData> cachedFDest)
        {
            // 1. Direct priority mapping from the Vendor API Payload
            if (!string.IsNullOrWhiteSpace(pnr.StationOwner))
            {
                return pnr.StationOwner.Trim();
            }

            // 2. Fallback to destination master cache matching
            string sectorFrom = pnr.OnlineSearch?.SectorFromCode?.Trim() ?? "";
            string sectorTo = pnr.OnlineSearch?.SectorToCode?.Trim() ?? "";

            var destMatch = cachedFDest.FirstOrDefault(f =>
                string.Equals(f.FromCode?.Trim(), sectorFrom, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(f.ToCode?.Trim(), sectorTo, StringComparison.OrdinalIgnoreCase));

            return !string.IsNullOrEmpty(destMatch?.InternalOwner) ? destMatch.InternalOwner : "Unassigned";
        }


        //private List<LeadLevelDashboardModel> ProcessLeadLevelData(List<PnrData> rawPnrData, List<FDestData> cachedFDest, Dictionary<string, double> liveFaresCache, IMemoryCache cache)
        //{
        //    var leadTrackers = new Dictionary<string, LeadDataTracker>();

        //    // 1. PRE-POPULATE ASSIGNED SECTORS
        //    // We must do this first because a Lead might be assigned a sector that has 0 PNRs (tickets) right now.
        //    foreach (var fDest in cachedFDest.Where(f => !string.IsNullOrEmpty(f.InternalOwner)))
        //    {
        //        string leadName = fDest.InternalOwner.Trim();
        //        string sectorStr = $"{fDest.FromCode?.Trim() ?? ""}-{fDest.ToCode?.Trim() ?? ""}";

        //        if (!leadTrackers.ContainsKey(leadName))
        //            leadTrackers[leadName] = new LeadDataTracker { LeadName = leadName };

        //        var tracker = leadTrackers[leadName];
        //        tracker.AssignedSectorsSet.Add(sectorStr);

        //        if (!tracker.SectorAggregators.ContainsKey(sectorStr))
        //        {
        //            // Assuming your FDestData model has a "StationName" property as requested
        //            tracker.SectorAggregators[sectorStr] = new SectorDateAggregator
        //            {
        //                Sector = sectorStr,
        //                Station = fDest.StationName ?? "N/A"
        //            };
        //        }
        //    }

        //    // 2. PROCESS PNR DATA & DATES
        //    foreach (var pnr in rawPnrData)
        //    {
        //        if (pnr.OnlineSearch == null) continue;

        //        string sectorStr = $"{pnr.OnlineSearch.SectorFromCode?.Trim() ?? ""}-{pnr.OnlineSearch.SectorToCode?.Trim() ?? ""}";
        //        string leadName = GetAssignedLead(pnr, cachedFDest);

        //        if (!leadTrackers.ContainsKey(leadName))
        //            leadTrackers[leadName] = new LeadDataTracker { LeadName = leadName };

        //        var tracker = leadTrackers[leadName];

        //        // Ensure aggregator exists for unassigned sectors that suddenly have tickets
        //        if (!tracker.SectorAggregators.ContainsKey(sectorStr))
        //        {
        //            tracker.SectorAggregators[sectorStr] = new SectorDateAggregator { Sector = sectorStr, Station = "Unknown" };
        //        }
        //        var agg = tracker.SectorAggregators[sectorStr];

        //        tracker.PurchasedSectorsSet.Add(sectorStr);

        //        bool isLive = string.Equals(pnr.OnlineSearch.VisibleType, "Normal", StringComparison.OrdinalIgnoreCase) && pnr.Qty > 0;
        //        bool isNotLive = string.Equals(pnr.OnlineSearch.VisibleType, "Hidden100%", StringComparison.OrdinalIgnoreCase) && pnr.OnlineSearch.Blocked > 0;

        //        if (isLive) tracker.LiveSectorsSet.Add(sectorStr);

        //        double competitorFare = pnr.OnlineSearch.Rate;
        //        if (pnr.TravelDateTime.HasValue && liveFaresCache.TryGetValue($"{sectorStr}_{pnr.TravelDateTime.Value:yyyy-MM-dd}", out double liveDbFare) && liveDbFare > 0)
        //        {
        //            competitorFare = liveDbFare;
        //        }

        //        if ((pnr.Price * 1.10) <= competitorFare && isNotLive)
        //        {
        //            tracker.FareSupportiveSectorsSet.Add(sectorStr);
        //        }

        //        // --- AGGREGATE DATES ---
        //        if (pnr.TravelDateTime.HasValue)
        //        {
        //            DateTime tDate = pnr.TravelDateTime.Value.Date;

        //            // 1. Available From: Earliest travel date strictly AFTER today
        //            if (tDate > DateTime.Today)
        //            {
        //                if (!agg.AvailableFrom.HasValue || tDate < agg.AvailableFrom.Value)
        //                    agg.AvailableFrom = tDate;
        //            }

        //            // 2. Open Till: Latest travel date ONLY if ticket is "Live"
        //            if (isLive)
        //            {
        //                if (!agg.OpenTill.HasValue || tDate > agg.OpenTill.Value)
        //                    agg.OpenTill = tDate;
        //            }

        //            // 3. Inventory Till: Absolute latest travel date regardless of VisibleType
        //            if (!agg.InventoryTill.HasValue || tDate > agg.InventoryTill.Value)
        //            {
        //                agg.InventoryTill = tDate;
        //            }
        //        }

        //        // 4. Last Update: Most recent FareLogLastUpdateDate (Unchanged)
        //        if (pnr.FareLogLastUpdateDate.HasValue)
        //        {
        //            if (!agg.LastUpdate.HasValue || pnr.FareLogLastUpdateDate.Value > agg.LastUpdate.Value)
        //                agg.LastUpdate = pnr.FareLogLastUpdateDate.Value;
        //        }



        //        // --- REMAINING COUNTERS ---
        //        if (pnr.Price > competitorFare) tracker.SellingAtLossCount += 1;
        //        else if (pnr.Price < competitorFare) tracker.SellingAtProfitCount += 1;

        //        if (pnr.TravelDateTime.HasValue)
        //        {
        //            var daysToTravel = (pnr.TravelDateTime.Value.Date - DateTime.Today).TotalDays;
        //            if (daysToTravel <= 7 && daysToTravel >= 0 && !isLive) tracker.D7NotLiveCount += 1;
        //        }

        //        if (pnr.OnlineSearch.Blocked > 0) tracker.BlockSectorCount += 1;

        //        if (pnr.OnlineSearch.IsNewSector == true || (pnr.OnlineSearch.EndDateOfNewSector.HasValue && pnr.OnlineSearch.EndDateOfNewSector.Value >= DateTime.Today))
        //        {
        //            tracker.NewlyAddedCount += 1;
        //        }
        //    }

        //    // 3. BUILD FINAL CACHES & DASHBOARD MODELS
        //    var finalDashboardData = new List<LeadLevelDashboardModel>();

        //    // Master Dictionary structure: [LeadName] -> [Category] -> List of Details
        //    var detailedCategoryCache = new Dictionary<string, Dictionary<string, List<SectorCategoryDetailModel>>>(StringComparer.OrdinalIgnoreCase);

        //    foreach (var tracker in leadTrackers.Values)
        //    {
        //        var leadCategories = new Dictionary<string, List<SectorCategoryDetailModel>>(StringComparer.OrdinalIgnoreCase);

        //        // Helper to map a HashSet of sectors to the final string-based Model you requested
        //        List<SectorCategoryDetailModel> MapSectorsToModel(HashSet<string> sectorSet)
        //        {
        //            return sectorSet.Where(s => tracker.SectorAggregators.ContainsKey(s)).Select(s => {
        //                var agg = tracker.SectorAggregators[s];
        //                return new SectorCategoryDetailModel
        //                {
        //                    Station = agg.Station,
        //                    Sector = agg.Sector,
        //                    AvailableFrom = agg.AvailableFrom?.ToString("dd MMM yyyy") ?? "-",
        //                    OpenTill = agg.OpenTill?.ToString("dd MMM yyyy") ?? "-",
        //                    InventoryTill = agg.InventoryTill?.ToString("dd MMM yyyy") ?? "-",
        //                    LastUpdate = agg.LastUpdate?.ToString("dd MMM yyyy HH:mm") ?? "-"
        //                };
        //            }).ToList();
        //        }

        //        // Save detailed mapped lists for the new page
        //        leadCategories["Assigned"] = MapSectorsToModel(tracker.AssignedSectorsSet);
        //        leadCategories["Purchased"] = MapSectorsToModel(tracker.PurchasedSectorsSet);
        //        leadCategories["Live"] = MapSectorsToModel(tracker.LiveSectorsSet);
        //        leadCategories["FareSupportive"] = MapSectorsToModel(tracker.FareSupportiveSectorsSet);

        //        detailedCategoryCache[tracker.LeadName] = leadCategories;

        //        // Build the standard summary row for the main table
        //        finalDashboardData.Add(new LeadLevelDashboardModel
        //        {
        //            LeadName = tracker.LeadName,
        //            AssignedSectors = tracker.AssignedSectorsSet.Count,
        //            PurchasedSectors = tracker.PurchasedSectorsSet.Count,
        //            LiveSectors = tracker.LiveSectorsSet.Count,
        //            FareSupportiveCount = tracker.FareSupportiveSectorsSet.Count,
        //            SellingAtLossCount = tracker.SellingAtLossCount,
        //            SellingAtProfitCount = tracker.SellingAtProfitCount,
        //            D7NotLiveCount = tracker.D7NotLiveCount,
        //            BlockSectorCount = tracker.BlockSectorCount,
        //            NewlyAddedCount = tracker.NewlyAddedCount
        //        });
        //    }

        //    // Save the detailed breakdown to cache securely
        //    cache.Set("LeadCategoryDetailsCache", detailedCategoryCache, new MemoryCacheEntryOptions().SetPriority(CacheItemPriority.NeverRemove));

        //    return finalDashboardData;
        //}

        private List<LeadLevelDashboardModel> ProcessLeadLevelData(List<PnrData> rawPnrData, List<FDestData> cachedFDest, Dictionary<string, double> liveFaresCache, IMemoryCache cache)
        {
            var leadTrackers = new Dictionary<string, LeadDataTracker>();

            var liveSectorDates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pnr in rawPnrData)
            {
                if (pnr.OnlineSearch != null && pnr.TravelDateTime.HasValue)
                {
                    bool isLiveFlight = string.Equals(pnr.OnlineSearch.VisibleType, "Normal", StringComparison.OrdinalIgnoreCase) && pnr.OnlineSearch.Qty > 0;
                    if (isLiveFlight)
                    {
                        string sStr = $"{pnr.OnlineSearch.SectorFromCode?.Trim() ?? ""}-{pnr.OnlineSearch.SectorToCode?.Trim() ?? ""}";
                        string sectorDateKey = $"{sStr}_{pnr.TravelDateTime.Value:yyyy-MM-dd}";
                        liveSectorDates.Add(sectorDateKey);
                    }
                }
            }

            // 1. PRE-POPULATE ASSIGNED SECTORS
            foreach (var fDest in cachedFDest.Where(f => !string.IsNullOrEmpty(f.InternalOwner)))
            {
                string leadName = fDest.InternalOwner.Trim();
                string sectorStr = $"{fDest.FromCode?.Trim() ?? ""}-{fDest.ToCode?.Trim() ?? ""}";

                if (!leadTrackers.ContainsKey(leadName))
                    leadTrackers[leadName] = new LeadDataTracker { LeadName = leadName };

                var tracker = leadTrackers[leadName];
                tracker.AssignedSectorsSet.Add(sectorStr);

                if (!tracker.SectorAggregators.ContainsKey(sectorStr))
                {
                    tracker.SectorAggregators[sectorStr] = new SectorDateAggregator
                    {
                        Sector = sectorStr,
                        Station = fDest.StationName ?? "N/A"
                    };
                }
            }

            // 2. PROCESS PNR DATA & DATES
            //foreach (var pnr in rawPnrData)
            //{
            //    if (pnr.OnlineSearch == null) continue;
            //    if (pnr.OnlineSearch.Qty == 0 && pnr.OnlineSearch.Blocked == 0) continue;

            //    string sectorStr = $"{pnr.OnlineSearch.SectorFromCode?.Trim() ?? ""}-{pnr.OnlineSearch.SectorToCode?.Trim() ?? ""}";
            //    string leadName = GetAssignedLead(pnr, cachedFDest);

            //    if (!leadTrackers.ContainsKey(leadName))
            //        leadTrackers[leadName] = new LeadDataTracker { LeadName = leadName };

            //    var tracker = leadTrackers[leadName];

            //    if (!tracker.SectorAggregators.ContainsKey(sectorStr))
            //    {
            //        tracker.SectorAggregators[sectorStr] = new SectorDateAggregator { Sector = sectorStr, Station = "Unknown" };
            //    }
            //    var agg = tracker.SectorAggregators[sectorStr];

            //    tracker.PurchasedSectorsSet.Add(sectorStr);

            //    bool isLive = string.Equals(pnr.OnlineSearch.VisibleType, "Normal", StringComparison.OrdinalIgnoreCase) && pnr.Qty > 0;
            //    bool isNotLive = string.Equals(pnr.OnlineSearch.VisibleType, "Hidden100%", StringComparison.OrdinalIgnoreCase) && pnr.OnlineSearch.Blocked > 0;

            //    // Track unique sectors AND specific flight details
            //    if (isLive)
            //    {
            //        tracker.LiveSectorsSet.Add(sectorStr);
            //        tracker.LiveFlightsDetails.Add(pnr);
            //    }

            //    double competitorFare = pnr.OnlineSearch.Rate;
            //    if (pnr.TravelDateTime.HasValue && liveFaresCache.TryGetValue($"{sectorStr}_{pnr.TravelDateTime.Value:yyyy-MM-dd}", out double liveDbFare) && liveDbFare > 0)
            //    {
            //        competitorFare = liveDbFare;
            //    }

            //    if ((pnr.Price * 1.10) <= competitorFare && isNotLive)
            //    {
            //        tracker.FareSupportiveSectorsSet.Add(sectorStr);
            //        tracker.FareSupportiveFlightsDetails.Add(pnr);
            //    }

            //    // --- AGGREGATE DATES ---
            //    if (pnr.TravelDateTime.HasValue)
            //    {
            //        DateTime tDate = pnr.TravelDateTime.Value.Date;

            //        if (tDate > DateTime.Today)
            //        {
            //            if (!agg.AvailableFrom.HasValue || tDate < agg.AvailableFrom.Value)
            //                agg.AvailableFrom = tDate;
            //        }

            //        if (isLive)
            //        {
            //            if (!agg.OpenTill.HasValue || tDate > agg.OpenTill.Value)
            //                agg.OpenTill = tDate;
            //        }

            //        if (!agg.InventoryTill.HasValue || tDate > agg.InventoryTill.Value)
            //        {
            //            agg.InventoryTill = tDate;
            //        }
            //    }

            //    if (pnr.FareLogLastUpdateDate.HasValue)
            //    {
            //        if (!agg.LastUpdate.HasValue || pnr.FareLogLastUpdateDate.Value > agg.LastUpdate.Value)
            //            agg.LastUpdate = pnr.FareLogLastUpdateDate.Value;
            //    }

            //    // --- REMAINING COUNTERS ---
            //    if (pnr.Price > competitorFare) tracker.SellingAtLossSectorsSet.Add(sectorStr);
            //    else if (pnr.Price < competitorFare) tracker.SellingAtProfitSectorsSet.Add(sectorStr);

            //    if (pnr.TravelDateTime.HasValue)
            //    {
            //        var daysToTravel = (pnr.TravelDateTime.Value.Date - DateTime.Today).TotalDays;

            //        bool isD7Hidden = string.Equals(pnr.OnlineSearch.VisibleType, "Hidden100%", StringComparison.OrdinalIgnoreCase)
            //                          && pnr.OnlineSearch.Qty == 0
            //                          && pnr.OnlineSearch.Blocked > 0;

            //        if (isD7Hidden && daysToTravel >= 0 && daysToTravel <= 7)
            //        {
            //            tracker.D7NotLiveSectorsSet.Add(sectorStr);
            //        }
            //    }

            //    if (pnr.Qty == pnr.OnlineSearch.Blocked && pnr.Qty > 0)
            //    {
            //        tracker.BlockSectorsSet.Add(sectorStr);
            //    }

            //    if (pnr.OnlineSearch.IsNewSector == true || (pnr.OnlineSearch.EndDateOfNewSector.HasValue && pnr.OnlineSearch.EndDateOfNewSector.Value >= DateTime.Today))
            //    {
            //        tracker.NewlyAddedSectorsSet.Add(sectorStr);
            //    }
            //}
            // 2. PROCESS PNR DATA & DATES
            foreach (var pnr in rawPnrData)
            {
                if (pnr.OnlineSearch == null) continue;

                // Qty Fix: Ignore completely dead/sold-out flights so they don't corrupt the dates
                if (pnr.OnlineSearch.Qty == 0 && pnr.OnlineSearch.Blocked == 0) continue;

                string sectorStr = $"{pnr.OnlineSearch.SectorFromCode?.Trim() ?? ""}-{pnr.OnlineSearch.SectorToCode?.Trim() ?? ""}";
                string leadName = GetAssignedLead(pnr, cachedFDest);

                if (!leadTrackers.ContainsKey(leadName))
                    leadTrackers[leadName] = new LeadDataTracker { LeadName = leadName };

                var tracker = leadTrackers[leadName];

                if (!tracker.SectorAggregators.ContainsKey(sectorStr))
                {
                    tracker.SectorAggregators[sectorStr] = new SectorDateAggregator { Sector = sectorStr, Station = "Unknown" };
                }
                var agg = tracker.SectorAggregators[sectorStr];

                tracker.PurchasedSectorsSet.Add(sectorStr);

                // Qty Fix: Use OnlineSearch.Qty for available seats!
                bool isLive = string.Equals(pnr.OnlineSearch.VisibleType, "Normal", StringComparison.OrdinalIgnoreCase) && pnr.OnlineSearch.Qty > 0;

                // STRICT CHECK: Isolate flights that are explicitly hidden
                bool isHidden = string.Equals(pnr.OnlineSearch.VisibleType, "Hidden100%", StringComparison.OrdinalIgnoreCase);

                // Track unique sectors AND specific flight details
                if (isLive)
                {
                    tracker.LiveSectorsSet.Add(sectorStr);
                    tracker.LiveFlightsDetails.Add(pnr);
                }

                double competitorFare = pnr.OnlineSearch.Rate;
                //if (pnr.TravelDateTime.HasValue && liveFaresCache.TryGetValue($"{sectorStr}_{pnr.TravelDateTime.Value:yyyy-MM-dd}", out double liveDbFare) && liveDbFare > 0)
                //{
                //    competitorFare = liveDbFare;
                //}

                // Fare Supportive, No Seat Opening (Cost + 10% <= Online Fare AND 0 available seats to sell)
                // Fare Supportive: Must be strictly Hidden100% AND (Cost + 10% <= Online Fare)
                if (pnr.TravelDateTime.HasValue)
                {
                    string sectorDateKey = $"{sectorStr}_{pnr.TravelDateTime.Value:yyyy-MM-dd}";

                    // NEW LOGIC: Check our Pre-Scan list! If ANY flight for this Sector+Date is Live, 
                    // it completely blocks this hidden flight from becoming Fare Supportive.
                    if (isHidden && (pnr.Price * 1.10) <= competitorFare && !liveSectorDates.Contains(sectorDateKey))
                    {
                        tracker.FareSupportiveSectorsSet.Add(sectorStr);
                        tracker.FareSupportiveFlightsDetails.Add(pnr);
                    }
                }

                // --- AGGREGATE DATES (CORRECTED) ---
                if (pnr.TravelDateTime.HasValue)
                {
                    DateTime tDate = pnr.TravelDateTime.Value.Date;

                    // 1. Available From: Earliest travel date strictly AFTER today
                    if (tDate > DateTime.Today)
                    {
                        if (!agg.AvailableFrom.HasValue || tDate < agg.AvailableFrom.Value)
                            agg.AvailableFrom = tDate;
                    }

                    // 2. Open Till: The latest date ONLY for "Live" tickets
                    if (isLive)
                    {
                        if (!agg.OpenTill.HasValue || tDate > agg.OpenTill.Value)
                            agg.OpenTill = tDate;
                    }

                    // 3. Inventory Till: The absolute latest date overall
                    if (!agg.InventoryTill.HasValue || tDate > agg.InventoryTill.Value)
                    {
                        agg.InventoryTill = tDate;
                    }
                }

                if (pnr.FareLogLastUpdateDate.HasValue)
                {
                    if (!agg.LastUpdate.HasValue || pnr.FareLogLastUpdateDate.Value > agg.LastUpdate.Value)
                        agg.LastUpdate = pnr.FareLogLastUpdateDate.Value;
                }

                // --- REMAINING COUNTERS ---
                if (pnr.Price > competitorFare)
                {
                    tracker.SellingAtLossSectorsSet.Add(sectorStr);
                    tracker.SellingAtLossFlightsDetails.Add(pnr); // Add the specific flight details
                }
                else if (pnr.Price < competitorFare)
                {
                    tracker.SellingAtProfitSectorsSet.Add(sectorStr);
                }

                if (pnr.TravelDateTime.HasValue)
                {
                    string sectorDateKey = $"{sectorStr}_{pnr.TravelDateTime.Value:yyyy-MM-dd}";
                    var daysToTravel = (pnr.TravelDateTime.Value.Date - DateTime.Today).TotalDays;

                    // D-7 Not Live Logic
                    bool isD7Hidden = isHidden && pnr.OnlineSearch.Qty == 0 && pnr.OnlineSearch.Blocked > 0;
                    if (isD7Hidden && daysToTravel >= 0 && daysToTravel <= 7)
                    {
                        // CRITICAL: Only count if there isn't another Live flight on this exact Date/Sector
                        if (!liveSectorDates.Contains(sectorDateKey))
                        {
                            tracker.D7NotLiveSectorsSet.Add(sectorStr);
                            tracker.D7NotLiveFlightsDetails.Add(pnr);
                        }
                    }

                    // Block Sector Logic
                    bool isBlocked = isHidden && pnr.OnlineSearch.Blocked > 0;
                    if (isBlocked)
                    {
                        // CRITICAL: Only count if there isn't another Live flight on this exact Date/Sector
                        if (!liveSectorDates.Contains(sectorDateKey))
                        {
                            tracker.BlockSectorsSet.Add(sectorStr);
                            tracker.BlockFlightsDetails.Add(pnr);
                        }
                    }
                }

                if (pnr.OnlineSearch.IsNewSector == true || (pnr.OnlineSearch.EndDateOfNewSector.HasValue && pnr.OnlineSearch.EndDateOfNewSector.Value >= DateTime.Today))
                {
                    tracker.NewlyAddedSectorsSet.Add(sectorStr);
                }
            }

            // 3. BUILD FINAL CACHES & DASHBOARD MODELS
            var finalDashboardData = new List<LeadLevelDashboardModel>();
            var detailedCategoryCache = new Dictionary<string, Dictionary<string, List<SectorCategoryDetailModel>>>(StringComparer.OrdinalIgnoreCase);

            foreach (var tracker in leadTrackers.Values)
            {
                var leadCategories = new Dictionary<string, List<SectorCategoryDetailModel>>(StringComparer.OrdinalIgnoreCase);

                // MAPPER 1: For Sector-Level Only (Assigned & Purchased)
                List<SectorCategoryDetailModel> MapSectorsToModel(HashSet<string> sectorSet)
                {
                    return sectorSet.Where(s => tracker.SectorAggregators.ContainsKey(s)).Select(s => {
                        var agg = tracker.SectorAggregators[s];
                        return new SectorCategoryDetailModel
                        {
                            Station = agg.Station,
                            Sector = agg.Sector,
                            AvailableFrom = agg.AvailableFrom?.ToString("dd MMM yyyy") ?? "-",
                            OpenTill = agg.OpenTill?.ToString("dd MMM yyyy") ?? "-",
                            InventoryTill = agg.InventoryTill?.ToString("dd MMM yyyy") ?? "-",
                            LastUpdate = agg.LastUpdate?.ToString("dd MMM yyyy HH:mm") ?? "-"
                        };
                    }).ToList();
                }

                // MAPPER 2: For Flight-Level Details (Live & Fare Supportive)
                List<SectorCategoryDetailModel> MapFlightsToModel(List<PnrData> flightList)
                {
                    return flightList.Select(pnr => {
                        string sCode = $"{pnr.OnlineSearch.SectorFromCode?.Trim() ?? ""}-{pnr.OnlineSearch.SectorToCode?.Trim() ?? ""}";
                        var agg = tracker.SectorAggregators.ContainsKey(sCode) ? tracker.SectorAggregators[sCode] : new SectorDateAggregator { Station = "Unknown" };

                        double onlinePrice = pnr.OnlineSearch.Rate;
                        //if (pnr.TravelDateTime.HasValue && liveFaresCache.TryGetValue($"{sCode}_{pnr.TravelDateTime.Value:yyyy-MM-dd}", out double liveDbFare) && liveDbFare > 0)
                        //{
                        //    onlinePrice = liveDbFare;
                        //}

                        return new SectorCategoryDetailModel
                        {
                            Station = agg.Station,
                            Sector = sCode,
                            AvailableFrom = agg.AvailableFrom?.ToString("dd MMM yyyy") ?? "-",
                            OpenTill = agg.OpenTill?.ToString("dd MMM yyyy") ?? "-",
                            InventoryTill = agg.InventoryTill?.ToString("dd MMM yyyy") ?? "-",
                            LastUpdate = agg.LastUpdate?.ToString("dd MMM yyyy HH:mm") ?? "-",

                            // Specific Flight Data
                            TravelDate = pnr.TravelDateTime?.ToString("dd MMM yyyy HH:mm") ?? "-",
                            FlightNo = pnr.OnlineSearch.FlightNo2 ?? "-",
                            OurPrice = pnr.Price,
                            OnlinePrice = onlinePrice
                        };
                    }).OrderBy(f => f.TravelDate).ToList(); // Sorting naturally by travel date
                }

                leadCategories["Assigned"] = MapSectorsToModel(tracker.AssignedSectorsSet);
                leadCategories["Purchased"] = MapSectorsToModel(tracker.PurchasedSectorsSet);
                leadCategories["Live"] = MapSectorsToModel(tracker.LiveSectorsSet);

                leadCategories["FareSupportive"] = MapFlightsToModel(tracker.FareSupportiveFlightsDetails);
                leadCategories["D7NotLive"] = MapFlightsToModel(tracker.D7NotLiveFlightsDetails);
                leadCategories["BlockSector"] = MapFlightsToModel(tracker.BlockFlightsDetails);
                leadCategories["SellingAtLoss"] = MapFlightsToModel(tracker.SellingAtLossFlightsDetails);
                leadCategories["NewlyAdded"] = MapSectorsToModel(tracker.NewlyAddedSectorsSet);

                detailedCategoryCache[tracker.LeadName] = leadCategories;

                finalDashboardData.Add(new LeadLevelDashboardModel
                {
                    LeadName = tracker.LeadName,
                    AssignedSectors = tracker.AssignedSectorsSet.Count,
                    PurchasedSectors = tracker.PurchasedSectorsSet.Count,
                    LiveSectors = tracker.LiveSectorsSet.Count,
                    FareSupportiveCount = tracker.FareSupportiveSectorsSet.Count,

                    // Extract Unique Counts here:
                    SellingAtLossCount = tracker.SellingAtLossSectorsSet.Count,
                    SellingAtProfitCount = tracker.SellingAtProfitSectorsSet.Count,
                    D7NotLiveCount = tracker.D7NotLiveSectorsSet.Count,
                    BlockSectorCount = tracker.BlockSectorsSet.Count,
                    NewlyAddedCount = tracker.NewlyAddedSectorsSet.Count
                });
            }

            cache.Set("LeadCategoryDetailsCache", detailedCategoryCache, new MemoryCacheEntryOptions().SetPriority(CacheItemPriority.NeverRemove));
            cache.Set("LeadTrackersCache", leadTrackers, new MemoryCacheEntryOptions().SetPriority(CacheItemPriority.NeverRemove));
            return finalDashboardData;
        }

        private List<SectorLevelDashboardModel> ProcessSectorLevelData(List<PnrData> rawPnrData, List<FDestData> cachedFDest, Dictionary<string, double> liveFaresCache)
        {
            var sectorDataMap = new Dictionary<string, SectorLevelDashboardModel>();

            var assignedSectorsByLead = cachedFDest
                .Where(f => !string.IsNullOrEmpty(f.InternalOwner))
                .GroupBy(f => f.InternalOwner)
                .ToDictionary(g => g.Key, g => g.Count());

            foreach (var pnr in rawPnrData)
            {
                if (pnr.OnlineSearch == null) continue;
                if (pnr.OnlineSearch.Qty == 0 && pnr.OnlineSearch.Blocked == 0) continue;

                string sectorFrom = pnr.OnlineSearch.SectorFromCode?.Trim() ?? "";
                string sectorTo = pnr.OnlineSearch.SectorToCode?.Trim() ?? "";
                string sectorStr = $"{sectorFrom}-{sectorTo}";

                if (string.IsNullOrEmpty(sectorFrom) || string.IsNullOrEmpty(sectorTo)) continue;

                string leadName = GetAssignedLead(pnr, cachedFDest);

                if (!sectorDataMap.ContainsKey(sectorStr))
                {
                    sectorDataMap[sectorStr] = new SectorLevelDashboardModel
                    {
                        SectorCode = sectorStr,
                        LeadName = leadName,
                        AssignedSectors = assignedSectorsByLead.ContainsKey(leadName) ? assignedSectorsByLead[leadName] : 0
                    };
                }

                var sectorModel = sectorDataMap[sectorStr];
                sectorModel.PurchasedSectors += 1;
                sectorModel.TotalPnrs += 1;

                bool isLive = pnr.Qty > 0 || pnr.OnlineSearch.aStatus;

                // EXACT MAPPING: Using Vendor API Visibility Flag
                bool isHidden = string.Equals(pnr.OnlineSearch.VisibleType, "Hidden", StringComparison.OrdinalIgnoreCase);

                if (isLive) sectorModel.LiveSectors += 1;

                if (isLive && !isHidden)
                {
                    sectorModel.LivePnrCount += 1;
                }

                if (isHidden)
                {
                    sectorModel.HiddenPnrCount += 1;
                }

                double competitorFare = pnr.OnlineSearch.Rate;
                bool hasManagedFare = false;

                if (pnr.TravelDateTime.HasValue)
                {
                    if (!sectorModel.MaxTravelDate.HasValue || pnr.TravelDateTime.Value > sectorModel.MaxTravelDate.Value)
                    {
                        sectorModel.MaxTravelDate = pnr.TravelDateTime.Value;
                    }

                    if (liveFaresCache.TryGetValue($"{sectorStr}_{pnr.TravelDateTime.Value:yyyy-MM-dd}", out double liveDbFare) && liveDbFare > 0)
                    {
                        //competitorFare = liveDbFare;
                        hasManagedFare = true;
                    }

                    var daysToTravel = (pnr.TravelDateTime.Value.Date - DateTime.Today).TotalDays;
                    if (daysToTravel <= 7 && daysToTravel >= 0 && !isLive) sectorModel.D7NotLiveCount += 1;
                }

                if (hasManagedFare) sectorModel.ManagedFares += 1;
                if (pnr.Price > competitorFare) sectorModel.FareErrorPnrs += 1;
                if (pnr.Price <= competitorFare) sectorModel.FareSupportiveCount += 1;
            }

            foreach (var sectorModel in sectorDataMap.Values)
            {
                sectorModel.HiddenPercentage = sectorModel.TotalPnrs > 0 ? Math.Round(((double)sectorModel.HiddenPnrCount / sectorModel.TotalPnrs) * 100, 1) : 0;

                sectorModel.AvgPnrPerDay = Math.Round(sectorModel.TotalPnrs / 30.0, 1);

                if (sectorModel.MaxTravelDate.HasValue)
                {
                    sectorModel.OpenTillDate = sectorModel.MaxTravelDate.Value.ToString("dd MMM yyyy");
                    sectorModel.DaysOpen = (sectorModel.MaxTravelDate.Value.Date - DateTime.Today).Days;
                    if (sectorModel.DaysOpen < 0) sectorModel.DaysOpen = 0;

                    sectorModel.AvgPnrOpenPerDay = sectorModel.DaysOpen > 0 ? Math.Round((double)sectorModel.LivePnrCount / sectorModel.DaysOpen, 1) : sectorModel.LivePnrCount;
                }
            }

            return sectorDataMap.Values.ToList();
        }
        public class SectorDateAggregator
        {
            public string Station { get; set; }
            public string Sector { get; set; }
            public DateTime? InventoryTill { get; set; }
            public DateTime? AvailableFrom { get; set; }
            public DateTime? OpenTill { get; set; }
            public DateTime? LastUpdate { get; set; }
        }

        public class LeadDataTracker
        {
            public string LeadName { get; set; }

            // Sets to track the specific sectors in each category
            public HashSet<string> AssignedSectorsSet { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> PurchasedSectorsSet { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> LiveSectorsSet { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> FareSupportiveSectorsSet { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            public HashSet<string> SellingAtLossSectorsSet { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> SellingAtProfitSectorsSet { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> D7NotLiveSectorsSet { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> BlockSectorsSet { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public HashSet<string> NewlyAddedSectorsSet { get; set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Flight Detail Lists (Used to populate the detailed page rows)
            public List<PnrData> LiveFlightsDetails { get; set; } = new List<PnrData>();
            public List<PnrData> FareSupportiveFlightsDetails { get; set; } = new List<PnrData>();
            public List<PnrData> D7NotLiveFlightsDetails { get; set; } = new List<PnrData>();
            public List<PnrData> BlockFlightsDetails { get; set; } = new List<PnrData>();
            public List<PnrData> NewlyAddedSectors { get; set; } = new List<PnrData>();
            public List<PnrData> SellingAtLossFlightsDetails { get; set; } = new List<PnrData>();

            // Dictionary to hold the date calculations for every sector this lead touches
            public Dictionary<string, SectorDateAggregator> SectorAggregators { get; set; } = new Dictionary<string, SectorDateAggregator>(StringComparer.OrdinalIgnoreCase);

            public string LastUpdatedTime { get; set; }
        }
    }
}