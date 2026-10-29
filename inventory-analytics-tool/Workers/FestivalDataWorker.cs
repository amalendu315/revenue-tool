using inventory_analytics_tool.Models;
using Microsoft.Extensions.Caching.Memory;
using System.Text;
using System.Text.Json;

namespace inventory_analytics_tool.Workers
{
    public class FestivalDataWorker : BackgroundService
    {
        private readonly IMemoryCache _cache;
        private readonly ILogger<FestivalDataWorker> _logger;
        private readonly IConfiguration _config;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IWebHostEnvironment _env;
        private readonly IServiceScopeFactory _scopeFactory;

        private readonly TimeSpan _refreshInterval = TimeSpan.FromMinutes(120);
        private readonly int _primaryYear = 2026;

        public FestivalDataWorker(IMemoryCache cache, ILogger<FestivalDataWorker> logger, IConfiguration config, IHttpClientFactory httpClientFactory, IWebHostEnvironment env, IServiceScopeFactory scopeFactory)
        {
            _cache = cache;
            _logger = logger;
            _config = config;
            _httpClientFactory = httpClientFactory;
            _env = env;
            _scopeFactory = scopeFactory;        
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Festival Data Worker started.");

            while (!stoppingToken.IsCancellationRequested)
            {
                bool fetchSuccess = false;
                try
                {
                    fetchSuccess = await FetchAndCacheDataAsync();
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Worker failed to fetch data: {ex.Message}");
                }

                if (!fetchSuccess && !_cache.TryGetValue("DashboardMasterData", out _))
                {
                    _logger.LogWarning("Cache is empty and API failed. Retrying aggressively in 1 minute...");
                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
                }
                else
                {
                     await CalculateFareDropAlertsAsync();
                    await Task.Delay(_refreshInterval, stoppingToken);
                }
            }
        }

        private async Task<bool> FetchAndCacheDataAsync()
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            _logger.LogInformation("Worker: Fetching MASTER data from Azure API...");

            string systemToken = _config["AzureApi:SystemToken"] ?? "YOUR_PERMANENT_SYSTEM_TOKEN";
            if (systemToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                systemToken = systemToken.Substring("Bearer ".Length).Trim();

            var httpClient = _httpClientFactory.CreateClient();
            httpClient.Timeout = TimeSpan.FromMinutes(4);

            httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", systemToken);
            httpClient.DefaultRequestHeaders.Add("Accept", "*/*");
            httpClient.DefaultRequestHeaders.Add("User-Agent", "PostmanRuntime/7.36.0");

            var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

            var masterResponse = await httpClient.GetAsync("https://shivent.azurewebsites.net/festival-master");
            if (!masterResponse.IsSuccessStatusCode)
            {
                _logger.LogError($"Master API Failed with status: {masterResponse.StatusCode}");
                return false;
            }

            var masterJson = await masterResponse.Content.ReadAsStringAsync();
            var masterData = JsonSerializer.Deserialize<FestivalMasterResponse>(masterJson, jsonOptions);
            if (masterData?.Data == null) return false;

            // ======================================================================
            // PHASE 1: Current Year (Strictly separated into its own cache)
            // ======================================================================
            var currentProcessedEvents = new List<DashboardUiEvent>();
            var currentRawEvents = new List<EventListData>();

            _logger.LogInformation($"PHASE 1: Fetching Essential Priority Year ({_primaryYear}) - Future Dates Only...");
            await ProcessSpecificYearAsync(_primaryYear, isEssentialPhase: true, masterData, httpClient, jsonOptions, currentProcessedEvents, currentRawEvents);

            // Saves strictly to the Primary Cache
            await UpdateCacheSafelyAsync(currentProcessedEvents, currentRawEvents, "DashboardMasterData", "RawEventListData");
            _logger.LogInformation($"PHASE 1 COMPLETE: Website is now unlocked! ({watch.Elapsed.TotalSeconds:F1}s)");


            // --- FETCH DESTINATION CACHE ---
            try
            {
                var destResponse = await httpClient.GetAsync("https://shivent.azurewebsites.net/fdestination-list");
                if (destResponse.IsSuccessStatusCode)
                {
                    var destJson = await destResponse.Content.ReadAsStringAsync();
                    var destData = JsonSerializer.Deserialize<FDestApiResponse>(destJson, jsonOptions);

                    if (destData?.Data != null && destData.Data.Any())
                    {
                        var destCacheOptions = new MemoryCacheEntryOptions()
                            .SetAbsoluteExpiration(TimeSpan.FromDays(30))
                            .SetPriority(CacheItemPriority.NeverRemove);

                        _cache.Set("FDestList", destData.Data, destCacheOptions);
                    }
                }
            }
            catch (Exception ex) { _logger.LogError($"Failed to fetch FDest data: {ex.Message}"); }


            // ======================================================================
            // PHASE 2: Historical / Future Data (Separated into Historical Cache)
            // ======================================================================
            _logger.LogInformation("PHASE 2: Fetching full historical background data...");
            int[] phase2Years = new[] { 2024, 2025, 2026, 2027, 2028 };

            foreach (var year in phase2Years)
            {
                // FIX: Moved INSIDE the loop so each year starts with a fresh list
                var historicalProcessedEvents = new List<DashboardUiEvent>();
                var historicalRawEvents = new List<EventListData>();

                await ProcessSpecificYearAsync(year, isEssentialPhase: false, masterData, httpClient, jsonOptions, historicalProcessedEvents, historicalRawEvents);

                // FIX: Save to a dynamically named, year-specific cache key!
                await UpdateCacheSafelyAsync(historicalProcessedEvents, historicalRawEvents, $"HistoricalDashboardMasterData_{year}", $"HistoricalRawEventListData_{year}");

                _logger.LogInformation($"Background Fetch Complete for {year}. Cache updated.");
            }

            return true;    
        }

        private async Task ProcessSpecificYearAsync(
            int year, bool isEssentialPhase, FestivalMasterResponse masterData, HttpClient httpClient,
            JsonSerializerOptions jsonOptions, List<DashboardUiEvent> targetProcessedList, List<EventListData> targetRawList)
        {
            var festivalKeywords = new List<string> { "diwali", "puja", "eid", "christmas", "holi", "navratri" };

            var yearStart = new DateTime(year - 1, 11, 20);
            var yearEnd = new DateTime(year + 1, 2, 10);

            if (isEssentialPhase && yearStart < DateTime.Today)
            {
                yearStart = DateTime.Today;
            }

            var yearFilters = masterData.Data
                .Select(fm => new {
                    blackoutTypeId = fm.blackoutTypeId,
                    validCalendars = fm.calendars?.Where(c =>
                        (c.date >= yearStart && c.date <= yearEnd) ||
                        (c.blackoutDates != null && c.blackoutDates.Any(d => d >= yearStart && d <= yearEnd))
                    ).ToList()
                })
                .Where(fm => fm.validCalendars != null && fm.validCalendars.Any())
                .Select(fm => new {
                    blackoutTypeId = fm.blackoutTypeId,
                    calendarIds = fm.validCalendars!.Select(c => c.calendarId).ToList(),
                    bdates = fm.validCalendars!.SelectMany(c => c.blackoutDates ?? new List<DateTime>())
                                               .Where(d => d >= yearStart && d <= yearEnd)
                                               .Select(d => d.ToString("yyyy-MM-ddTHH:mm:ss"))
                                               .Distinct().ToList()
                })
                .Where(f => f.calendarIds.Any() && f.bdates.Any())
                .ToList();

            if (!yearFilters.Any()) return;

            var apiEventsForThisYear = new List<EventListData>();
            using var semaphore = new SemaphoreSlim(1);

            var fetchTasks = yearFilters.Select(async filter =>
            {
                await semaphore.WaitAsync();
                try
                {
                    var localEvents = new List<EventListData>();
                    int batchSize = 15;

                    for (int i = 0; i < filter.bdates.Count; i += batchSize)
                    {
                        var batchDates = filter.bdates.Skip(i).Take(batchSize).ToList();
                        int maxRetries = 3;
                        bool success = false;
                        int attempts = 0;

                        while (!success && attempts < maxRetries)
                        {
                            attempts++;
                            try
                            {
                                var batchFilter = new { blackoutTypeId = filter.blackoutTypeId, calendarIds = filter.calendarIds, bdates = batchDates };
                                var content = new StringContent(JsonSerializer.Serialize(new { filters = new[] { batchFilter } }, jsonOptions), Encoding.UTF8, "application/json");

                                var eventResponse = await httpClient.PostAsync("https://shivent.azurewebsites.net/EventLists", content);

                                if (eventResponse.IsSuccessStatusCode)
                                {
                                    var eventJson = await eventResponse.Content.ReadAsStringAsync();
                                    var eventData = JsonSerializer.Deserialize<EventListResponse>(eventJson, jsonOptions);
                                    if (eventData?.Data != null) localEvents.AddRange(eventData.Data);

                                    success = true;
                                    await Task.Delay(300);
                                }
                                else
                                {
                                    if ((int)eventResponse.StatusCode >= 500) await Task.Delay(2000);
                                    else break;
                                }
                            }
                            catch (TaskCanceledException) { await Task.Delay(2000); }
                        }
                    }
                    return localEvents;
                }
                finally { semaphore.Release(); }
            });

            var results = await Task.WhenAll(fetchTasks);
            foreach (var batch in results) apiEventsForThisYear.AddRange(batch);

            var mergedApiEventsForThisYear = apiEventsForThisYear
                .GroupBy(e => e.EventId)
                .Select(g => {
                    var merged = g.First();
                    merged.Details = g.Where(e => e.Details != null).SelectMany(e => e.Details)
                                      .GroupBy(s => $"{s.FromCode}-{s.ToCode}")
                                      .Select(sg => {
                                          var ms = sg.First();

                                          ms.Details = sg.Where(s => s.Details != null)
                                                         .SelectMany(s => s.Details)
                                                         .GroupBy(d => d.TravelDate.Date)
                                                         .Select(dg => dg.Last())
                                                         .OrderBy(d => d.TravelDate)
                                                         .ToList();

                                          // Important: merge DateRange here also, not only later in UpdateCacheSafelyAsync.
                                          ms.DateRange = sg.Where(s => s.DateRange != null)
                                                           .SelectMany(s => s.DateRange)
                                                           .Select(d => d.Date)
                                                           .Distinct()
                                                           .OrderBy(d => d)
                                                           .ToList();

                                          return ms;
                                      }).ToList();
                    return merged;
                }).ToList();

            targetRawList.AddRange(mergedApiEventsForThisYear);

            await Task.Run(() =>
            {
                foreach (var apiEvent in mergedApiEventsForThisYear)
                {
                    var allTdd = apiEvent.Details?.SelectMany(s => s.Details ?? new List<EventTravelDateData>()).ToList() ?? new List<EventTravelDateData>();
                    var matchingMaster = masterData.Data.FirstOrDefault(m => m.blackoutTypeId == apiEvent.EventId);
                    var validCals = matchingMaster?.calendars?.Where(c =>
                        (c.date >= yearStart && c.date <= yearEnd) || (c.blackoutDates != null && c.blackoutDates.Any(d => d >= yearStart && d <= yearEnd))
                    ).ToList();

                    if (validCals == null || !validCals.Any()) continue;

                    bool isFestival = festivalKeywords.Any(k => (apiEvent.Event ?? "").Contains(k, StringComparison.OrdinalIgnoreCase));
                    var calendarsByYear = validCals.GroupBy(c => c.date.Year).ToList();

                    foreach (var yearGroup in calendarsByYear)
                    {
                        var primaryEventDate = yearGroup.OrderBy(c => c.date).First().date;

                        var combinedCalDates = yearGroup.SelectMany(c => c.blackoutDates ?? new List<DateTime>())
                                                        .Where(d => Math.Abs((d.Date - primaryEventDate.Date).TotalDays) <= 250)
                                                        .Select(d => d.Date).ToHashSet();

                        var calTdd = allTdd.Where(d => combinedCalDates.Contains(d.TravelDate.Date)).ToList();

                        var groupedDaily = new List<DailyStat>();
                        foreach (var calDate in combinedCalDates.Where(d => d >= yearStart && d <= yearEnd).OrderBy(d => d))
                        {
                            var dayFlights = calTdd.Where(d => d.TravelDate.Date == calDate).ToList();
                            if (dayFlights.Any())
                            {
                                groupedDaily.Add(new DailyStat
                                {
                                    Date = calDate.ToString("yyyy-MM-dd"),
                                    TargetSeat = dayFlights.Sum(x => x.TargetSeat),
                                    Achieved = dayFlights.Sum(x => x.Achieved),
                                    FlightCount = dayFlights.Sum(x => x.FlightCount),
                                    Priority = dayFlights.FirstOrDefault(x => !string.IsNullOrEmpty(x.BlackoutPriority))?.BlackoutPriority ?? "Normal"
                                });
                            }
                            else
                            {
                                groupedDaily.Add(new DailyStat
                                {
                                    Date = calDate.ToString("yyyy-MM-dd"),
                                    TargetSeat = 0,
                                    Achieved = 0,
                                    FlightCount = 0,
                                    Priority = "Normal"
                                });
                            }
                        }

                        if (!groupedDaily.Any()) continue;

                        var airlinesBreakdown = new List<string>();
                        var airlineAchieved = new Dictionary<string, int>();
                        if (apiEvent.Airlines != null && apiEvent.Flights != null)
                        {
                            foreach (var airline in apiEvent.Airlines)
                            {
                                airlinesBreakdown.Add($"{airline.AirlineName} ({apiEvent.Flights.Count(f => f.AirlineCode == airline.AirlineCode)})");
                                string code = !string.IsNullOrEmpty(airline.AirlineCode) ? airline.AirlineCode : "OAL";
                                airlineAchieved[code] = airline.Achieved;
                            }
                        }

                        targetProcessedList.Add(new DashboardUiEvent
                        {
                            Id = apiEvent.EventId,
                            Name = apiEvent.Event!,
                            EventDate = primaryEventDate.ToString("yyyy-MM-dd"),
                            SectorsCovered = apiEvent.Details?.Count ?? 0,
                            IsImportant = isFestival,
                            AirlinesBreakdown = airlinesBreakdown,
                            AirlineAchieved = airlineAchieved,
                            DailyStats = groupedDaily
                        });
                    }
                }
            });
        }

        // Updated to accept dynamic Cache Keys!
        private async Task UpdateCacheSafelyAsync(List<DashboardUiEvent> rawProcessed, List<EventListData> rawEvents, string dashKey, string rawKey)
        {
            if (rawProcessed == null || !rawProcessed.Any() || rawEvents == null || !rawEvents.Any()) return;

            await Task.Run(() =>
            {
                try
                {
                    var jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                    var clonedRawEvents = JsonSerializer.Deserialize<List<EventListData>>(JsonSerializer.Serialize(rawEvents, jsonOpts), jsonOpts);
                    var clonedProcessedEvents = JsonSerializer.Deserialize<List<DashboardUiEvent>>(JsonSerializer.Serialize(rawProcessed, jsonOpts), jsonOpts);

                    var uniqueProcessedEvents = clonedProcessedEvents!.GroupBy(e => $"{e.Id}_{e.EventDate}").Select(g => g.Last()).ToList();
                    var uniqueRawEvents = clonedRawEvents!.GroupBy(e => e.EventId).Select(g =>
                    {
                        var mergedEvent = g.First();
                        mergedEvent.Details = g.Where(e => e.Details != null).SelectMany(e => e.Details)
                            .GroupBy(s => $"{s.FromCode}-{s.ToCode}")
                            .Select(sg => {
                                var ms = sg.First();
                                ms.Details = sg.Where(s => s.Details != null).SelectMany(s => s.Details)
                                               .GroupBy(d => d.TravelDate.Date).Select(dg => dg.Last()).ToList();

                                // CRITICAL FIX: Merge the DateRange arrays so the master calendar isn't truncated!
                                ms.DateRange = sg.Where(s => s.DateRange != null).SelectMany(s => s.DateRange)
                                                 .Distinct().OrderBy(d => d).ToList();
                                return ms;
                            }).ToList();
                        return mergedEvent;
                    }).ToList();

                    var indestructibleOptions = new MemoryCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromDays(30)).SetPriority(CacheItemPriority.NeverRemove);

                    // Saves to the specific dynamic bucket requested
                    _cache.Set(dashKey, uniqueProcessedEvents, indestructibleOptions);
                    _cache.Set(rawKey, uniqueRawEvents, indestructibleOptions);
                }
                catch (Exception ex) { _logger.LogError($"Cache swap error: {ex.Message}"); }
            });
        }

        
        private async Task CalculateFareDropAlertsAsync()
        {
            _logger.LogInformation("Worker: Scanning Live DB for Fare Drop Alerts (All Years)...");
            try
            {
                using (var scope = _scopeFactory.CreateScope())
                {
                    var dbHelper = scope.ServiceProvider.GetRequiredService<inventory_analytics_tool.Helpers.IDatabaseHelper>();

                    _cache.TryGetValue("FDestList", out List<FDestData> cachedFDest);
                    cachedFDest ??= new List<FDestData>();

                    var alerts = new List<object>();
                    var today = DateTime.Today;

                    // Define the years you want to scan for active alerts (Current Year + Next 2 Years)
                    int[] yearsToScan = new[] { _primaryYear, _primaryYear + 1, _primaryYear + 2 };

                    foreach (var year in yearsToScan)
                    {
                        // Replicate the Controller's dynamic cache key logic
                        string rawKey = (year == _primaryYear) ? "RawEventListData" : $"HistoricalRawEventListData_{year}";
                        string dashKey = (year == _primaryYear) ? "DashboardMasterData" : $"HistoricalDashboardMasterData_{year}";

                        if (!_cache.TryGetValue(rawKey, out List<EventListData> rawEvents) || rawEvents == null) continue;
                        if (!_cache.TryGetValue(dashKey, out List<DashboardUiEvent> dashEvents) || dashEvents == null) continue;

                        foreach (var rawEvent in rawEvents)
                        {
                            var dashEvent = dashEvents.FirstOrDefault(e => e.Id == rawEvent.EventId);
                            if (dashEvent == null || rawEvent.Details == null) continue;

                            foreach (var sectorData in rawEvent.Details)
                            {
                                if (sectorData.Details == null || !sectorData.Details.Any()) continue;
                                string sectorString = $"{sectorData.FromCode}-{sectorData.ToCode}";

                                var destMatch = cachedFDest.FirstOrDefault(f =>
                                    string.Equals(f.FromCode, sectorData.FromCode, StringComparison.OrdinalIgnoreCase) &&
                                    string.Equals(f.ToCode, sectorData.ToCode, StringComparison.OrdinalIgnoreCase));
                                string leadName = !string.IsNullOrEmpty(destMatch?.InternalOwner) ? destMatch.InternalOwner : "Unassigned";

                                // SMART BATCHING: Find the date range
                                var minDate = sectorData.Details.Min(d => d.TravelDate).Date;
                                var maxDate = sectorData.Details.Max(d => d.TravelDate).Date;

                                // Fetch the live lowest fares
                                var dbLowestFares = await dbHelper.GetLowestLatestFaresAsync(sectorString, minDate, maxDate);

                                foreach (var tdd in sectorData.Details)
                                {
                                    if (tdd.TravelDate.Date < today || tdd.TargetFare <= 0) continue;

                                    if (tdd.TravelDate.Year != year) continue;

                                    if (dbLowestFares.TryGetValue(tdd.TravelDate.Date, out var fareData))
                                    {
                                        double currentOnlineFare = fareData.LowestFare;
                                        DateTime fareEntryDate = fareData.EntryDate;
                                        string lowestFareAirline = fareData.Airline;
                                        string flightNumber = fareData.FlightNo;

                                        // THE TRIGGER
                                        if (currentOnlineFare > 0 && currentOnlineFare < tdd.TargetFare)
                                        {
                                            alerts.Add(new
                                            {
                                                eventId = rawEvent.EventId,
                                                eventName = dashEvent.Name,
                                                sector = sectorString,
                                                travelDate = tdd.TravelDate.ToString("dd MMM yyyy"),
                                                rawDate = tdd.TravelDate,
                                                targetFare = (int)Math.Round(tdd.TargetFare, MidpointRounding.AwayFromZero),
                                                lastOnlineFare = (int)Math.Round(currentOnlineFare, MidpointRounding.AwayFromZero),
                                                acheivedPax = tdd.Achieved,
                                                entryDate = fareEntryDate.ToString("dd MMM yyyy HH:mm:ss"),
                                                lead = leadName,
                                                airline = lowestFareAirline,
                                                flightNo = flightNumber
                                            });
                                        }
                                    }
                                }
                            }
                        }
                    } // End of Year Loop

                    // Sort ALL aggregated alerts by date
                    var sortedAlerts = alerts
                        .GroupBy(a => $"{((dynamic)a).eventId}_{((dynamic)a).sector}_{((dynamic)a).airline}_{((dynamic)a).flightNo}_{((dynamic)a).rawDate:yyyyMMdd}")
                        .Select(g => g.First())
                        .OrderBy(a => ((dynamic)a).rawDate)
                        .ToList();

                    // Save the combined, deduplicated list to the cache permanently
                    var cacheOptions = new MemoryCacheEntryOptions().SetPriority(CacheItemPriority.NeverRemove);
                    _cache.Set("ActiveFareAlerts", sortedAlerts, cacheOptions);

                    _logger.LogInformation($"Worker: Found {sortedAlerts.Count} Fare Drop Alerts using live DB (Spanning {yearsToScan.Length} years).");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Worker Alerts Error: {ex.Message}");
            }
        }
    }
}