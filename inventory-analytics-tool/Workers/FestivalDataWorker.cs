//using inventory_analytics_tool.Models;
//using Microsoft.Extensions.Caching.Memory;
//using System.Text;
//using System.Text.Json;

//namespace inventory_analytics_tool.Workers
//{
//    public class FestivalDataWorker : BackgroundService
//    {
//        private readonly IMemoryCache _cache;
//        private readonly ILogger<FestivalDataWorker> _logger;
//        private readonly IConfiguration _config;
//        private readonly IHttpClientFactory _httpClientFactory;
//        private readonly IWebHostEnvironment _env;

//        private readonly TimeSpan _refreshInterval = TimeSpan.FromMinutes(30);
//        private readonly int _primaryYear = 2026;

//        public FestivalDataWorker(IMemoryCache cache, ILogger<FestivalDataWorker> logger, IConfiguration config, IHttpClientFactory httpClientFactory, IWebHostEnvironment env)
//        {
//            _cache = cache;
//            _logger = logger;
//            _config = config;
//            _httpClientFactory = httpClientFactory;
//            _env = env;
//        }

//        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
//        {
//            _logger.LogInformation("Festival Data Worker started.");

//            while (!stoppingToken.IsCancellationRequested)
//            {
//                bool fetchSuccess = false;
//                try
//                {
//                    // Now checks if the fetch was completely successful
//                    fetchSuccess = await FetchAndCacheDataAsync();
//                }
//                catch (Exception ex)
//                {
//                    _logger.LogError($"Worker failed to fetch data: {ex.Message}");
//                }

//                // THE QUICK-RETRY FIX: If the cache is empty AND the fetch failed, retry in 1 minute!
//                if (!fetchSuccess && !_cache.TryGetValue("DashboardMasterData", out _))
//                {
//                    _logger.LogWarning("Cache is empty and API failed. Retrying aggressively in 1 minute...");
//                    await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
//                }
//                else
//                {
//                    // Otherwise, safely wait 30 minutes for the next background refresh
//                    await Task.Delay(_refreshInterval, stoppingToken);
//                }
//            }
//        }

//        // Changed to Task<bool> so the ExecuteAsync loop knows if it succeeded
//        private async Task<bool> FetchAndCacheDataAsync()
//        {
//            var watch = System.Diagnostics.Stopwatch.StartNew();
//            _logger.LogInformation("Worker: Fetching MASTER data from Azure API...");

//            string systemToken = _config["AzureApi:SystemToken"] ?? "YOUR_PERMANENT_SYSTEM_TOKEN";
//            if (systemToken.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
//                systemToken = systemToken.Substring("Bearer ".Length).Trim();

//            var httpClient = _httpClientFactory.CreateClient();
//            httpClient.Timeout = TimeSpan.FromMinutes(4);

//            httpClient.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", systemToken);
//            httpClient.DefaultRequestHeaders.Add("Accept", "*/*");
//            httpClient.DefaultRequestHeaders.Add("User-Agent", "PostmanRuntime/7.36.0");

//            var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

//            var masterResponse = await httpClient.GetAsync("https://shivent.azurewebsites.net/festival-master");
//            if (!masterResponse.IsSuccessStatusCode)
//            {
//                _logger.LogError($"Master API Failed with status: {masterResponse.StatusCode}");
//                return false; // Tells the worker the fetch failed
//            }

//            var masterJson = await masterResponse.Content.ReadAsStringAsync();
//            var masterData = JsonSerializer.Deserialize<FestivalMasterResponse>(masterJson, jsonOptions);
//            if (masterData?.Data == null) return false;

//            var cumulativeProcessedEvents = new List<DashboardUiEvent>();
//            var cumulativeRawEvents = new List<EventListData>();

//            _logger.LogInformation($"PHASE 1: Fetching Essential Priority Year ({_primaryYear}) - Future Dates Only...");
//            await ProcessSpecificYearAsync(_primaryYear, isEssentialPhase: true, masterData, httpClient, jsonOptions, cumulativeProcessedEvents, cumulativeRawEvents);

//            await UpdateCacheSafelyAsync(cumulativeProcessedEvents, cumulativeRawEvents);
//            _logger.LogInformation($"PHASE 1 COMPLETE: Website is now unlocked! ({watch.Elapsed.TotalSeconds:F1}s)");

//            // --- FETCH DESTINATION CACHE ---
//            try
//            {
//                var destResponse = await httpClient.GetAsync("https://shivent.azurewebsites.net/fdestination-list");
//                if (destResponse.IsSuccessStatusCode)
//                {
//                    var destJson = await destResponse.Content.ReadAsStringAsync();
//                    var destData = JsonSerializer.Deserialize<FDestApiResponse>(destJson, jsonOptions);

//                    if (destData?.Data != null && destData.Data.Any())
//                    {
//                        var destCacheOptions = new MemoryCacheEntryOptions()
//                            .SetAbsoluteExpiration(TimeSpan.FromDays(30))
//                            .SetPriority(CacheItemPriority.NeverRemove);

//                        _cache.Set("FDestList", destData.Data, destCacheOptions);
//                    }
//                }
//            }
//            catch (Exception ex) { _logger.LogError($"Failed to fetch FDest data: {ex.Message}"); }

//            // PHASE 2
//            _logger.LogInformation("PHASE 2: Fetching full historical background data...");
//            int[] phase2Years = _env.IsDevelopment() ? new[] { 2026 } : new[] { 2024, 2025, 2026, 2027, 2028 };

//            foreach (var year in phase2Years)
//            {
//                await ProcessSpecificYearAsync(year, isEssentialPhase: false, masterData, httpClient, jsonOptions, cumulativeProcessedEvents, cumulativeRawEvents);
//                await UpdateCacheSafelyAsync(cumulativeProcessedEvents, cumulativeRawEvents);
//                _logger.LogInformation($"Background Fetch Complete for {year}. Cache updated.");
//            }

//            return true; // Tells the worker everything succeeded!
//        }

//        private async Task ProcessSpecificYearAsync(
//            int year, bool isEssentialPhase, FestivalMasterResponse masterData, HttpClient httpClient,
//            JsonSerializerOptions jsonOptions, List<DashboardUiEvent> cumulativeProcessedEvents, List<EventListData> cumulativeRawEvents)
//        {
//            var festivalKeywords = new List<string> { "diwali", "puja", "eid", "christmas", "holi", "navratri" };

//            var yearStart = new DateTime(year - 1, 12, 15);
//            var yearEnd = new DateTime(year + 1, 1, 15);

//            if (isEssentialPhase && yearStart < DateTime.Today)
//            {
//                yearStart = DateTime.Today;
//            }

//            var yearFilters = masterData.Data
//                .Select(fm => new {
//                    blackoutTypeId = fm.blackoutTypeId,
//                    validCalendars = fm.calendars?.Where(c =>
//                        (c.date >= yearStart && c.date <= yearEnd) ||
//                        (c.blackoutDates != null && c.blackoutDates.Any(d => d >= yearStart && d <= yearEnd))
//                    ).ToList()
//                })
//                .Where(fm => fm.validCalendars != null && fm.validCalendars.Any())
//                .Select(fm => new {
//                    blackoutTypeId = fm.blackoutTypeId,
//                    calendarIds = fm.validCalendars!.Select(c => c.calendarId).ToList(),
//                    bdates = fm.validCalendars!.SelectMany(c => c.blackoutDates ?? new List<DateTime>())
//                                               .Where(d => d >= yearStart && d <= yearEnd)
//                                               .Select(d => d.ToString("yyyy-MM-ddTHH:mm:ss"))
//                                               .Distinct().ToList()
//                })
//                .Where(f => f.calendarIds.Any() && f.bdates.Any())
//                .ToList();

//            if (!yearFilters.Any()) return;

//            var apiEventsForThisYear = new List<EventListData>();
//            using var semaphore = new SemaphoreSlim(1);

//            var fetchTasks = yearFilters.Select(async filter =>
//            {
//                await semaphore.WaitAsync();
//                try
//                {
//                    var localEvents = new List<EventListData>();
//                    int batchSize = 15;

//                    for (int i = 0; i < filter.bdates.Count; i += batchSize)
//                    {
//                        var batchDates = filter.bdates.Skip(i).Take(batchSize).ToList();
//                        int maxRetries = 3;
//                        bool success = false;
//                        int attempts = 0;

//                        while (!success && attempts < maxRetries)
//                        {
//                            attempts++;
//                            try
//                            {
//                                var batchFilter = new { blackoutTypeId = filter.blackoutTypeId, calendarIds = filter.calendarIds, bdates = batchDates };
//                                var content = new StringContent(JsonSerializer.Serialize(new { filters = new[] { batchFilter } }, jsonOptions), Encoding.UTF8, "application/json");

//                                var eventResponse = await httpClient.PostAsync("https://shivent.azurewebsites.net/EventLists", content);

//                                if (eventResponse.IsSuccessStatusCode)
//                                {
//                                    var eventJson = await eventResponse.Content.ReadAsStringAsync();
//                                    var eventData = JsonSerializer.Deserialize<EventListResponse>(eventJson, jsonOptions);
//                                    if (eventData?.Data != null) localEvents.AddRange(eventData.Data);

//                                    success = true;
//                                    await Task.Delay(300);
//                                }
//                                else
//                                {
//                                    if ((int)eventResponse.StatusCode >= 500) await Task.Delay(2000);
//                                    else break;
//                                }
//                            }
//                            catch (TaskCanceledException) { await Task.Delay(2000); }
//                        }
//                    }
//                    return localEvents;
//                }
//                finally { semaphore.Release(); }
//            });

//            var results = await Task.WhenAll(fetchTasks);
//            foreach (var batch in results) apiEventsForThisYear.AddRange(batch);

//            var mergedApiEventsForThisYear = apiEventsForThisYear
//                .GroupBy(e => e.EventId)
//                .Select(g => {
//                    var merged = g.First();
//                    merged.Details = g.Where(e => e.Details != null).SelectMany(e => e.Details)
//                                      .GroupBy(s => $"{s.FromCode}-{s.ToCode}")
//                                      .Select(sg => {
//                                          var ms = sg.First();
//                                          ms.Details = sg.Where(s => s.Details != null).SelectMany(s => s.Details)
//                                                         .GroupBy(d => d.TravelDate.Date).Select(dg => dg.Last()).ToList();
//                                          return ms;
//                                      }).ToList();
//                    return merged;
//                }).ToList();

//            cumulativeRawEvents.AddRange(mergedApiEventsForThisYear);

//            await Task.Run(() =>
//            {
//                foreach (var apiEvent in mergedApiEventsForThisYear)
//                {
//                    var allTdd = apiEvent.Details?.SelectMany(s => s.Details ?? new List<EventTravelDateData>()).ToList() ?? new List<EventTravelDateData>();
//                    var matchingMaster = masterData.Data.FirstOrDefault(m => m.blackoutTypeId == apiEvent.EventId);
//                    var validCals = matchingMaster?.calendars?.Where(c =>
//                        (c.date >= yearStart && c.date <= yearEnd) || (c.blackoutDates != null && c.blackoutDates.Any(d => d >= yearStart && d <= yearEnd))
//                    ).ToList();

//                    if (validCals == null || !validCals.Any()) continue;

//                    bool isFestival = festivalKeywords.Any(k => (apiEvent.Event ?? "").Contains(k, StringComparison.OrdinalIgnoreCase));
//                    var calendarsByYear = validCals.GroupBy(c => c.date.Year).ToList();

//                    foreach (var yearGroup in calendarsByYear)
//                    {
//                        var primaryEventDate = yearGroup.OrderBy(c => c.date).First().date;

//                        var combinedCalDates = yearGroup.SelectMany(c => c.blackoutDates ?? new List<DateTime>())
//                                                        .Where(d => Math.Abs((d.Date - primaryEventDate.Date).TotalDays) <= 120)
//                                                        .Select(d => d.Date).ToHashSet();

//                        var calTdd = allTdd.Where(d => combinedCalDates.Contains(d.TravelDate.Date)).ToList();

//                        var groupedDaily = calTdd.Where(d => d.TravelDate >= yearStart && d.TravelDate <= yearEnd)
//                            .GroupBy(d => d.TravelDate.Date)
//                            .Select(g => new DailyStat
//                            {
//                                Date = g.Key.ToString("yyyy-MM-dd"),
//                                TargetSeat = g.Sum(x => x.TargetSeat),
//                                Achieved = g.Sum(x => x.Achieved),
//                                FlightCount = g.Sum(x => x.FlightCount),
//                                Priority = g.FirstOrDefault(x => !string.IsNullOrEmpty(x.BlackoutPriority))?.BlackoutPriority ?? "Normal"
//                            }).OrderBy(d => d.Date).ToList();

//                        if (!groupedDaily.Any()) continue;



//                        var airlinesBreakdown = new List<string>();
//                        var airlineAchieved = new Dictionary<string, int>(); // NEW
//                        if (apiEvent.Airlines != null && apiEvent.Flights != null)
//                        {
//                            foreach (var airline in apiEvent.Airlines)
//                            {
//                                airlinesBreakdown.Add($"{airline.AirlineName} ({apiEvent.Flights.Count(f => f.AirlineCode == airline.AirlineCode)})");
//                                string code = !string.IsNullOrEmpty(airline.AirlineCode) ? airline.AirlineCode : "OAL";
//                                airlineAchieved[code] = airline.Achieved;
//                            }
//                        }

//                        cumulativeProcessedEvents.Add(new DashboardUiEvent
//                        {
//                            Id = apiEvent.EventId,
//                            Name = apiEvent.Event!,
//                            EventDate = primaryEventDate.ToString("yyyy-MM-dd"),
//                            SectorsCovered = apiEvent.Details?.Count ?? 0,
//                            IsImportant = isFestival,
//                            AirlinesBreakdown = airlinesBreakdown,
//                            AirlineAchieved = airlineAchieved,
//                            DailyStats = groupedDaily
//                        });
//                    }
//                }
//            });
//        }

//        private async Task UpdateCacheSafelyAsync(List<DashboardUiEvent> rawProcessed, List<EventListData> rawEvents)
//        {
//            if (rawProcessed == null || !rawProcessed.Any() || rawEvents == null || !rawEvents.Any()) return;

//            await Task.Run(() =>
//            {
//                try
//                {
//                    var jsonOpts = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
//                    var clonedRawEvents = JsonSerializer.Deserialize<List<EventListData>>(JsonSerializer.Serialize(rawEvents, jsonOpts), jsonOpts);
//                    var clonedProcessedEvents = JsonSerializer.Deserialize<List<DashboardUiEvent>>(JsonSerializer.Serialize(rawProcessed, jsonOpts), jsonOpts);

//                    var uniqueProcessedEvents = clonedProcessedEvents!.GroupBy(e => $"{e.Id}_{e.EventDate}").Select(g => g.Last()).ToList();
//                    var uniqueRawEvents = clonedRawEvents!.GroupBy(e => e.EventId).Select(g =>
//                    {
//                        var mergedEvent = g.First();
//                        mergedEvent.Details = g.Where(e => e.Details != null).SelectMany(e => e.Details)
//                            .GroupBy(s => $"{s.FromCode}-{s.ToCode}")
//                            .Select(sg => {
//                                var ms = sg.First();
//                                ms.Details = sg.Where(s => s.Details != null).SelectMany(s => s.Details)
//                                               .GroupBy(d => d.TravelDate.Date).Select(dg => dg.Last()).ToList();
//                                return ms;
//                            }).ToList();
//                        return mergedEvent;
//                    }).ToList();

//                    var indestructibleOptions = new MemoryCacheEntryOptions().SetAbsoluteExpiration(TimeSpan.FromDays(30)).SetPriority(CacheItemPriority.NeverRemove);
//                    _cache.Set("DashboardMasterData", uniqueProcessedEvents, indestructibleOptions);
//                    _cache.Set("RawEventListData", uniqueRawEvents, indestructibleOptions);
//                }
//                catch (Exception ex) { _logger.LogError($"Cache swap error: {ex.Message}"); }
//            });
//        }
//    }
//}

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

        private readonly TimeSpan _refreshInterval = TimeSpan.FromMinutes(30);
        private readonly int _primaryYear = 2026;

        public FestivalDataWorker(IMemoryCache cache, ILogger<FestivalDataWorker> logger, IConfiguration config, IHttpClientFactory httpClientFactory, IWebHostEnvironment env)
        {
            _cache = cache;
            _logger = logger;
            _config = config;
            _httpClientFactory = httpClientFactory;
            _env = env;
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
            int[] phase2Years = _env.IsDevelopment() ? new[] { 2026 } : new[] { 2024, 2025, 2026, 2027, 2028 };

            var historicalProcessedEvents = new List<DashboardUiEvent>();
            var historicalRawEvents = new List<EventListData>();

            foreach (var year in phase2Years)
            {
                await ProcessSpecificYearAsync(year, isEssentialPhase: false, masterData, httpClient, jsonOptions, historicalProcessedEvents, historicalRawEvents);

                // Saves strictly to the Historical Cache so it never breaks the live Phase 1 data!
                await UpdateCacheSafelyAsync(historicalProcessedEvents, historicalRawEvents, "HistoricalDashboardMasterData", "HistoricalRawEventListData");
                _logger.LogInformation($"Background Fetch Complete for {year}. Cache updated.");
            }

            return true;
        }

        private async Task ProcessSpecificYearAsync(
            int year, bool isEssentialPhase, FestivalMasterResponse masterData, HttpClient httpClient,
            JsonSerializerOptions jsonOptions, List<DashboardUiEvent> targetProcessedList, List<EventListData> targetRawList)
        {
            var festivalKeywords = new List<string> { "diwali", "puja", "eid", "christmas", "holi", "navratri" };

            var yearStart = new DateTime(year - 1, 12, 15);
            var yearEnd = new DateTime(year + 1, 1, 15);

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
    }
}