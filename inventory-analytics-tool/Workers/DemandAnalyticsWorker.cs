using inventory_analytics_tool.Helpers;
using inventory_analytics_tool.Models;
using Microsoft.Extensions.Caching.Memory;
using System.Diagnostics;
using System.Text.Json;
using System.Text;

namespace inventory_analytics_tool.Workers
{
    public class DemandAnalyticsWorker : BackgroundService
    {
        private readonly ILogger<DemandAnalyticsWorker> _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IMemoryCache _cache;

        public DemandAnalyticsWorker(ILogger<DemandAnalyticsWorker> logger, IServiceScopeFactory scopeFactory, IMemoryCache cache)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;
            _cache = cache;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            await Task.Delay(TimeSpan.FromSeconds(10), stoppingToken);
            bool isFirstRun = true;

            while (!stoppingToken.IsCancellationRequested)
            {
                if (!isFirstRun)
                {
                    var now = DateTime.Now;
                    var nextRun = new DateTime(now.Year, now.Month, now.Day, 12, 0, 0);
                    if (now >= nextRun)
                    {
                        nextRun = nextRun.AddDays(1);
                    }
                    var delay = nextRun - now;
                    _logger.LogInformation($"[{now:HH:mm tt}] DemandWorker sleeping until {nextRun:HH:mm tt} (in {delay.TotalHours:F2} hours)...");
                    await Task.Delay(delay, stoppingToken);
                }
                isFirstRun = false;

                if (stoppingToken.IsCancellationRequested) break;

                _logger.LogInformation($"[{DateTime.Now:HH:mm tt}] Demand Analytics Worker computing daily scores...");
                var watch = Stopwatch.StartNew();

                using (var scope = _scopeFactory.CreateScope())
                {
                    var dbHelper = scope.ServiceProvider.GetRequiredService<IDatabaseHelper>();
                    var chHelper = scope.ServiceProvider.GetRequiredService<IClickHouseHelper>();
                    var clientFactory = scope.ServiceProvider.GetRequiredService<IHttpClientFactory>();

                    try
                    {
                        var searchDemands = await chHelper.GetAggregatedSearchDemandAsync();

                        var activeSectors = searchDemands
                            .Where(k => k.Value.TodaySearches > 0 || k.Value.Avg7DaySearches > 0)
                            .OrderByDescending(k => k.Value.TodaySearches)
                            .ThenByDescending(k => k.Value.Avg7DaySearches)
                            .ToList();

                        // 🛑 TEMPORARY LIMIT FOR TESTING
                        var sectorStrings = activeSectors.Take(1000).Select(x => x.Key.Split('|')[0]).Distinct().ToList();

                        DateTime minDate = DateTime.Today.AddDays(-1);
                        DateTime maxDate = DateTime.Today.AddDays(90);

                        // 1. Fetch DB Data as our Baseline Fallback
                        var faresBySector = await dbHelper.GetBulkDemandFareStatsAsync(string.Join(",", sectorStrings), minDate, maxDate);

                        var rawList = new List<RawDemandData>();

                        foreach (var kvp in activeSectors)
                        {
                            string[] keyParts = kvp.Key.Split('|');
                            string sector = keyParts[0].Trim().ToUpper();
                            DateTime targetDate = DateTime.Parse(keyParts[1]);

                            int searchToday = kvp.Value.TodaySearches;
                            int search7Day = kvp.Value.Avg7DaySearches > 0 ? kvp.Value.Avg7DaySearches : 1;
                            int capacity = 150;
                            int daysToTravel = (targetDate.Date - DateTime.Today).Days;
                            if (daysToTravel < 0) daysToTravel = 0;

                            double? fareToday = null;
                            double? fare7Day = null;

                            string fareKey = $"{sector}|{targetDate:yyyy-MM-dd}";

                            // CRITICAL FIX: Load the DB data FIRST as a safety net!
                            if (faresBySector.TryGetValue(fareKey, out var stats))
                            {
                                fareToday = stats.LatestFare > 0 ? stats.LatestFare : null;
                                fare7Day = stats.Avg7DayFare > 0 ? stats.Avg7DayFare : null;
                                if (stats.FlightCount > 0) capacity = stats.FlightCount * 150;
                            }

                            rawList.Add(new RawDemandData
                            {
                                Sector = sector,
                                TargetDate = targetDate,
                                SearchToday = searchToday,
                                Search7Day = search7Day,
                                Capacity = capacity,
                                DaysToTravel = daysToTravel,
                                FareToday = fareToday,
                                Fare7Day = fare7Day
                            });
                        }

                        // 2. HIT THE SERP API FOR LIVE OVERRIDES
                        if (rawList.Any())
                        {
                            _logger.LogInformation($"DemandWorker: Triggering SERP API for ALL {rawList.Count} rows to calculate live capacity...");
                            await FetchLiveFaresAndCapacityAsync(rawList, dbHelper, clientFactory, _logger);
                        }

                        // 3. COMPUTE FINAL EXCEL DASHBOARD DATA
                        var dashboardData = new List<DemandCalculatorRow>();

                        foreach (var raw in rawList)
                        {
                            double maturity = 1.0;
                            if (raw.DaysToTravel >= 60) maturity = 0.20;
                            else if (raw.DaysToTravel >= 30) maturity = 0.40;
                            else if (raw.DaysToTravel >= 14) maturity = 0.65;
                            else if (raw.DaysToTravel >= 7) maturity = 0.85;
                            else maturity = 1.00;

                            double? searchDelta = null;
                            double? fareDelta = null;
                            double fareMultiplier = 1.0;

                            if (raw.Search7Day > 0)
                            {
                                searchDelta = (double)(raw.SearchToday - raw.Search7Day) / raw.Search7Day;
                            }

                            if (raw.FareToday.HasValue && raw.Fare7Day.HasValue && raw.Fare7Day.Value > 0)
                            {
                                fareDelta = (raw.FareToday.Value - raw.Fare7Day.Value) / raw.Fare7Day.Value;
                                fareMultiplier = 1.0 + Math.Max(-0.30, Math.Min(0.30, fareDelta.Value));
                            }

                            double scoreToday = ((double)raw.SearchToday / raw.Capacity) * fareMultiplier;
                            double score7Day = ((double)raw.Search7Day / raw.Capacity) * fareMultiplier;

                            string GetBand(double score, double mat)
                            {
                                if (score > 2.50 * mat) return "VERY HIGH";
                                if (score > 1.50 * mat) return "HIGH";
                                if (score > 0.80 * mat) return "MODERATE";
                                if (score > 0.40 * mat) return "LOW";
                                return "VERY LOW";
                            }

                            dashboardData.Add(new DemandCalculatorRow
                            {
                                Sector = raw.Sector,
                                TravelDate = raw.TargetDate.ToString("dd MMM yyyy"),
                                Capacity = raw.Capacity,
                                DaysToTravel = raw.DaysToTravel,
                                Maturity = maturity,
                                SearchToday = raw.SearchToday,
                                Search7DayAvg = raw.Search7Day,
                                FareToday = raw.FareToday.HasValue ? (object)Math.Round(raw.FareToday.Value, 0) : "NA",
                                Fare7DayAvg = raw.Fare7Day.HasValue ? (object)Math.Round(raw.Fare7Day.Value, 0) : "NA",
                                SearchDelta = searchDelta.HasValue ? (object)Math.Round(searchDelta.Value, 4) : "NA",
                                FareDelta = fareDelta.HasValue ? (object)Math.Round(fareDelta.Value, 4) : "NA",
                                FareMultiplier = Math.Round(fareMultiplier, 3),
                                ScoreToday = Math.Round(scoreToday, 3),
                                BandToday = GetBand(scoreToday, maturity),
                                Score7Day = Math.Round(score7Day, 3),
                                Band7Day = GetBand(score7Day, maturity)
                            });
                        }

                        // 🛑 TEMPORARY LIMIT FOR TESTING
                        _cache.Set("DemandCalculatorData", dashboardData.Take(1000).ToList(), new MemoryCacheEntryOptions().SetPriority(CacheItemPriority.NeverRemove));
                        watch.Stop();
                        _logger.LogInformation($"DemandWorker: Processed {dashboardData.Count} rows in {watch.Elapsed.TotalSeconds} sec.");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError($"Demand Worker Critical Failure: {ex.Message}");
                    }
                }
            }
        }

        // ==========================================
        // BULLETPROOF LIVE CAPACITY & FARE SCRAPER
        // ==========================================

        private async Task FetchLiveFaresAndCapacityAsync(List<RawDemandData> targetRows, IDatabaseHelper dbHelper, IHttpClientFactory clientFactory, ILogger logger)
        {
            var client = clientFactory.CreateClient();
            client.Timeout = TimeSpan.FromMinutes(2); // Reduced from 5 to prevent hanging

            // 1. Prepare all batches upfront
            var apiBatches = new List<(DateTime Date, string DateStr, string SectorCsv)>();

            foreach (var dateGroup in targetRows.GroupBy(r => r.TargetDate))
            {
                var sectors = dateGroup.Select(r => r.Sector).Distinct().ToList();
                string flightDateStr = dateGroup.Key.ToString("MM'/'dd'/'yyyy", System.Globalization.CultureInfo.InvariantCulture);

                for (int i = 0; i < sectors.Count; i += 10)
                {
                    apiBatches.Add((dateGroup.Key, flightDateStr, string.Join(",", sectors.Skip(i).Take(10))));
                }
            }

            logger.LogInformation($"[WORKER] Firing {apiBatches.Count} SERP API batches concurrently...");

            // MANAGER REQ 3: Speed up processing using concurrency. 15 requests at a time.
            var semaphore = new SemaphoreSlim(15);
            var tasks = apiBatches.Select(async batch =>
            {
                await semaphore.WaitAsync();
                try
                {
                    var payload = new { Sectors = batch.SectorCsv, FlightDate = batch.DateStr };
                    var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
                    var response = await client.PostAsync("http://13.203.38.118:8080/ccloop", content);

                    if (response.IsSuccessStatusCode)
                    {
                        var responseStr = await response.Content.ReadAsStringAsync();
                        using var doc = JsonDocument.Parse(responseStr);

                        IEnumerable<JsonElement> rootElements = doc.RootElement.ValueKind == JsonValueKind.Array
                            ? (IEnumerable<JsonElement>)doc.RootElement.EnumerateArray()
                            : new[] { doc.RootElement };

                        foreach (var rootItem in rootElements)
                        {
                            if (rootItem.TryGetProperty("errors", out _)) continue;

                            var flightsArrProp = GetCaseInsensitiveProperty(rootItem, "flights");
                            if (flightsArrProp.HasValue && flightsArrProp.Value.ValueKind == JsonValueKind.Array)
                            {
                                var flightsBySector = new Dictionary<string, List<JsonElement>>(StringComparer.OrdinalIgnoreCase);

                                foreach (var flight in flightsArrProp.Value.EnumerateArray())
                                {
                                    string sector = $"{GetStringVal(flight, "Origin")}-{GetStringVal(flight, "Destination")}".ToUpper();
                                    if (!flightsBySector.ContainsKey(sector)) flightsBySector[sector] = new List<JsonElement>();
                                    flightsBySector[sector].Add(flight);
                                }

                                foreach (var kvp in flightsBySector)
                                {
                                    string sector = kvp.Key;
                                    var sectorFlights = kvp.Value;
                                    int directCount = sectorFlights.Count(f => !GetStringVal(f, "FlightNumber").Contains("/"));
                                    int calculatedCapacity = directCount > 0 ? (directCount * 150) : 150;

                                    decimal lowestFare = decimal.MaxValue;
                                    JsonElement? bestFlight = null;

                                    foreach (var flight in sectorFlights)
                                    {
                                        decimal totalFare = GetDecimalVal(flight, "BaseAmount") + GetDecimalVal(flight, "TotalTaxAmount");
                                        if (totalFare > 0 && totalFare < lowestFare)
                                        {
                                            lowestFare = totalFare;
                                            bestFlight = flight;
                                        }
                                    }

                                    if (bestFlight.HasValue && lowestFare != decimal.MaxValue)
                                    {
                                        string depTime = "00:00:00", arrTime = "00:00:00";
                                        if (DateTime.TryParse(GetStringVal(bestFlight.Value, "DepartureDateTime"), out var dtDep)) depTime = dtDep.ToString("HH:mm:ss");
                                        if (DateTime.TryParse(GetStringVal(bestFlight.Value, "ArrivalDateTime"), out var dtArr)) arrTime = dtArr.ToString("HH:mm:ss");

                                        // Database calls create their own isolated connections, making them thread-safe
                                        await dbHelper.GetOrInsertBlackoutFareFeedIdAsync(sector, batch.Date, GetStringVal(bestFlight.Value, "AirlineCode"), GetStringVal(bestFlight.Value, "FlightNumber"), lowestFare, arrTime, depTime);

                                        // THREAD SAFETY: Lock the memory list before updating it
                                        lock (targetRows)
                                        {
                                            var rowsToUpdate = targetRows.Where(r => r.Sector == sector && r.TargetDate == batch.Date);
                                            foreach (var row in rowsToUpdate)
                                            {
                                                row.FareToday = (double)lowestFare;
                                                row.Capacity = calculatedCapacity;
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError($"Batch {batch.SectorCsv} failed: {ex.Message}");
                }
                finally
                {
                    semaphore.Release();
                }
            });

            await Task.WhenAll(tasks);
        }

        // --- JSON Parsing Helpers (Defeats Case-Sensitivity & String/Number mismatched types) ---
        private JsonElement? GetCaseInsensitiveProperty(JsonElement element, string propertyName)
        {
            if (element.ValueKind != JsonValueKind.Object) return null;
            if (element.TryGetProperty(propertyName, out var directProp)) return directProp;

            foreach (var prop in element.EnumerateObject())
            {
                if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase)) return prop.Value;
            }
            return null;
        }

        private decimal GetDecimalVal(JsonElement element, string propName)
        {
            var prop = GetCaseInsensitiveProperty(element, propName);
            if (!prop.HasValue) return 0;
            if (prop.Value.ValueKind == JsonValueKind.Number) return prop.Value.GetDecimal();
            if (prop.Value.ValueKind == JsonValueKind.String && decimal.TryParse(prop.Value.GetString(), out var val)) return val;
            return 0;
        }

        private string GetStringVal(JsonElement element, string propName)
        {
            var prop = GetCaseInsensitiveProperty(element, propName);
            if (!prop.HasValue) return "";
            if (prop.Value.ValueKind == JsonValueKind.String) return prop.Value.GetString() ?? "";
            if (prop.Value.ValueKind == JsonValueKind.Number) return prop.Value.GetRawText() ?? "";
            return "";
        }
    }

    public class RawDemandData
    {
        public string Sector { get; set; }
        public DateTime TargetDate { get; set; }
        public int SearchToday { get; set; }
        public int Search7Day { get; set; }
        public int Capacity { get; set; }
        public int DaysToTravel { get; set; }
        public double? FareToday { get; set; }
        public double? Fare7Day { get; set; }
    }

    public class DemandCalculatorRow
    {
        public string Sector { get; set; }
        public string TravelDate { get; set; }
        public int Capacity { get; set; }
        public int DaysToTravel { get; set; }
        public double Maturity { get; set; }
        public int SearchToday { get; set; }
        public int Search7DayAvg { get; set; }
        public object FareToday { get; set; }
        public object Fare7DayAvg { get; set; }
        public object SearchDelta { get; set; }
        public object FareDelta { get; set; }
        public object FareMultiplier { get; set; }
        public object ScoreToday { get; set; }
        public string BandToday { get; set; }
        public object Score7Day { get; set; }
        public string Band7Day { get; set; }
    }
}
