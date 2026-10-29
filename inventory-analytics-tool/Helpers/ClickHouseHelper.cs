//using System.Text.Json;
//using System.Text;

//namespace inventory_analytics_tool.Helpers
//{
//    public interface IClickHouseHelper
//    {
//        Task<Dictionary<string, (int TodaySearches, int Avg7DaySearches)>> GetAggregatedSearchDemandAsync();
//    }

//    public class ClickHouseHelper : IClickHouseHelper
//    {
//        private readonly IHttpClientFactory _httpClientFactory;
//        private readonly IConfiguration _config;
//        private readonly ILogger<ClickHouseHelper> _logger;

//        public ClickHouseHelper(IHttpClientFactory httpClientFactory, IConfiguration config, ILogger<ClickHouseHelper> logger)
//        {
//            _httpClientFactory = httpClientFactory;
//            _config = config;
//            _logger = logger;
//        }

//        public async Task<Dictionary<string, (int TodaySearches, int Avg7DaySearches)>> GetAggregatedSearchDemandAsync()
//        {
//            var results = new Dictionary<string, (int, int)>();

//            string chHost = _config["ClickHouse:Host"] ?? "";
//            string chUser = _config["ClickHouse:User"] ?? "";
//            string chPass = _config["ClickHouse:Password"] ?? "";

//            string query = @"
//                WITH RawLegs AS (
//                    -- 1. Skyscanner Redirects (Replaces internal search events)
//                    -- Data is pre-filtered for India domains by the Python pipeline
//                    SELECT 
//                        CONCAT(OriginAirport, '-', DestinationAirport) AS Sector, 
//                        toDate(DepartureDate) AS TravelDate,
//                        toDate(Timestamp) AS SearchDate,
//                        (coalesce(PassengerAdult, 1) + coalesce(PassengerChild, 0)) AS Pax
//                    FROM analytics.skyscanner_redirects_eventsTemp
//                    WHERE Timestamp >= now() - INTERVAL 8 DAY 
//                      AND OriginAirport != '' AND DestinationAirport != ''

//                    UNION ALL

//                    -- 2. Skyscanner Leg 0 (Outbound - strictly touching India)
//                    SELECT 
//                        CONCAT(Leg_0_OriginIATA, '-', Leg_0_DestinationIATA) AS Sector, 
//                        toDate(Leg_0_Date) AS TravelDate,
//                        toDate(Timestamp) AS SearchDate,
//                        (coalesce(PassengerAdult, 1) + coalesce(PassengerChild, 0)) AS Pax
//                    FROM analytics.skyscanner_search_eventsTemp
//                    WHERE Timestamp >= now() - INTERVAL 8 DAY 
//                      AND Leg_0_OriginIATA IS NOT NULL AND Leg_0_OriginIATA != '' 
//                      AND Leg_0_DestinationIATA IS NOT NULL AND Leg_0_DestinationIATA != ''
//                      AND (OriginCountry = 'IN' OR DestinationCountry = 'IN')

//                    UNION ALL

//                    -- 3. Skyscanner Leg 1 (Return - strictly touching India)
//                    SELECT 
//                        CONCAT(Leg_1_OriginIATA, '-', Leg_1_DestinationIATA) AS Sector, 
//                        toDate(Leg_1_Date) AS TravelDate,
//                        toDate(Timestamp) AS SearchDate,
//                        (coalesce(PassengerAdult, 1) + coalesce(PassengerChild, 0)) AS Pax
//                    FROM analytics.skyscanner_search_eventsTemp
//                    WHERE Timestamp >= now() - INTERVAL 8 DAY 
//                      AND Leg_1_OriginIATA IS NOT NULL AND Leg_1_OriginIATA != '' 
//                      AND Leg_1_DestinationIATA IS NOT NULL AND Leg_1_DestinationIATA != ''
//                      AND (OriginCountry = 'IN' OR DestinationCountry = 'IN')
//                ),
//                MaxDates AS (
//                    -- Find the absolute latest SearchDate in the entire dataset
//                    SELECT MAX(SearchDate) AS LatestDate FROM RawLegs
//                )
//                -- 4. Aggregate by Exact Seat Volume (Relative to the Latest Data Available)
//                SELECT 
//                    CONCAT(Sector, '|', toString(TravelDate)) AS SectorDateKey, 
//                    Sector,
//                    TravelDate,
//                    CAST(SUM(IF(SearchDate = (SELECT LatestDate FROM MaxDates), Pax, 0)) AS Int32) AS SearchesToday,
//                    CAST(ROUND(SUM(IF(SearchDate >= (SELECT LatestDate FROM MaxDates) - 7 AND SearchDate < (SELECT LatestDate FROM MaxDates), Pax, 0)) / 7) AS Int32) AS Searches7DayAvg
//                FROM RawLegs
//                WHERE TravelDate >= today() 
//                GROUP BY Sector, TravelDate
//                HAVING SearchesToday > 0 OR Searches7DayAvg > 0

//                ORDER BY SearchesToday DESC, Searches7DayAvg DESC
//                LIMIT 1000 

//                FORMAT JSONEachRow";

//            try
//            {
//                var client = _httpClientFactory.CreateClient();
//                client.Timeout = TimeSpan.FromMinutes(10); // 10 minutes!

//                var authString = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{chUser}:{chPass}"));

//                // 1. Create a raw HttpRequestMessage
//                using var request = new HttpRequestMessage(HttpMethod.Post, chHost);
//                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authString);

//                // 2. CRITICAL: Put the massive query in the BODY, not the URL, to prevent Azure/Proxy blocks
//                request.Content = new StringContent(query, Encoding.UTF8, "text/plain");

//                _logger.LogInformation("ClickHouseHelper: Sending streaming aggregation query to ClickHouse via HTTP Body...");

//                // 3. Actually use ResponseHeadersRead to stream data instantly
//                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

//                if (response.IsSuccessStatusCode)
//                {
//                    using var stream = await response.Content.ReadAsStreamAsync();
//                    using var streamReader = new StreamReader(stream);

//                    string? line;
//                    while ((line = await streamReader.ReadLineAsync()) != null)
//                    {
//                        if (string.IsNullOrWhiteSpace(line)) continue;

//                        try
//                        {
//                            using var doc = JsonDocument.Parse(line);
//                            var row = doc.RootElement;

//                            string sectorDateKey = row.GetProperty("SectorDateKey").GetString() ?? "";
//                            if (string.IsNullOrEmpty(sectorDateKey)) continue;

//                            int today = 0;
//                            var todayProp = row.GetProperty("SearchesToday");
//                            if (todayProp.ValueKind == JsonValueKind.Number) today = todayProp.GetInt32();
//                            else if (todayProp.ValueKind == JsonValueKind.String) int.TryParse(todayProp.GetString(), out today);

//                            int avg7 = 0;
//                            var avgProp = row.GetProperty("Searches7DayAvg");
//                            if (avgProp.ValueKind == JsonValueKind.Number) avg7 = (int)avgProp.GetDouble();
//                            else if (avgProp.ValueKind == JsonValueKind.String)
//                            {
//                                if (double.TryParse(avgProp.GetString(), out double tempAvg)) avg7 = (int)tempAvg;
//                            }

//                            results[sectorDateKey] = (today, avg7);
//                        }
//                        catch (Exception) { }
//                    }

//                    _logger.LogInformation($"ClickHouseHelper: Successfully aggregated {results.Count} unique Sector/Date combinations.");
//                }
//                else
//                {
//                    string errorResponse = await response.Content.ReadAsStringAsync();
//                    _logger.LogError($"ClickHouseHelper HTTP Error: {response.StatusCode}. Details: {errorResponse}");
//                }
//            }
//            catch (TaskCanceledException ex)
//            {
//                _logger.LogError($"ClickHouseHelper Error: The connection timed out. Details: {ex.Message}");
//            }
//            catch (Exception ex)
//            {
//                _logger.LogError($"ClickHouseHelper Critical Exception: {ex.Message}");
//            }

//            return results;
//        }
//    }
//}
using System.Text.Json;
using System.Text;

namespace inventory_analytics_tool.Helpers
{
    public interface IClickHouseHelper
    {
        Task<Dictionary<string, (int TodaySearches, int Avg7DaySearches)>> GetAggregatedSearchDemandAsync();
    }

    public class ClickHouseHelper : IClickHouseHelper
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _config;
        private readonly ILogger<ClickHouseHelper> _logger;

        public ClickHouseHelper(IHttpClientFactory httpClientFactory, IConfiguration config, ILogger<ClickHouseHelper> logger)
        {
            _httpClientFactory = httpClientFactory;
            _config = config;
            _logger = logger;
        }

        public async Task<Dictionary<string, (int TodaySearches, int Avg7DaySearches)>> GetAggregatedSearchDemandAsync()
        {
            var results = new Dictionary<string, (int, int)>();

            string chHost = _config["ClickHouse:Host"] ?? "";
            string chUser = _config["ClickHouse:User"] ?? "";
            string chPass = _config["ClickHouse:Password"] ?? "";

            string query = @"
                WITH RawLegs AS (
                    -- 1. Skyscanner Redirects (Replaces internal search events)
                    -- Data is pre-filtered for India domains by the Python pipeline
                    SELECT 
                        CONCAT(OriginAirport, '-', DestinationAirport) AS Sector, 
                        toDate(DepartureDate) AS TravelDate,
                        toDate(Timestamp) AS SearchDate,
                        (coalesce(PassengerAdult, 1) + coalesce(PassengerChild, 0)) AS Pax
                    FROM analytics.skyscanner_redirects_eventsTemp
                    WHERE Timestamp >= now() - INTERVAL 8 DAY 
                      AND OriginAirport != '' AND DestinationAirport != ''
                      AND (OriginCountry = 'IN' OR DestinationCountry = 'IN')

                    UNION ALL

                    -- 2. Skyscanner Leg 0 (Outbound - strictly touching India)
                    SELECT 
                        CONCAT(Leg_0_OriginIATA, '-', Leg_0_DestinationIATA) AS Sector, 
                        toDate(Leg_0_Date) AS TravelDate,
                        toDate(Timestamp) AS SearchDate,
                        (coalesce(PassengerAdult, 1) + coalesce(PassengerChild, 0)) AS Pax
                    FROM analytics.skyscanner_search_eventsTemp
                    WHERE Timestamp >= now() - INTERVAL 8 DAY 
                      AND Leg_0_OriginIATA IS NOT NULL AND Leg_0_OriginIATA != '' 
                      AND Leg_0_DestinationIATA IS NOT NULL AND Leg_0_DestinationIATA != ''
                      AND (OriginCountry = 'IN' OR DestinationCountry = 'IN')

                    UNION ALL

                    -- 3. Skyscanner Leg 1 (Return - strictly touching India)
                    SELECT 
                        CONCAT(Leg_1_OriginIATA, '-', Leg_1_DestinationIATA) AS Sector, 
                        toDate(Leg_1_Date) AS TravelDate,
                        toDate(Timestamp) AS SearchDate,
                        (coalesce(PassengerAdult, 1) + coalesce(PassengerChild, 0)) AS Pax
                    FROM analytics.skyscanner_search_eventsTemp
                    WHERE Timestamp >= now() - INTERVAL 8 DAY 
                      AND Leg_1_OriginIATA IS NOT NULL AND Leg_1_OriginIATA != '' 0*0
                      AND Leg_1_DestinationIATA IS NOT NULL AND Leg_1_DestinationIATA != ''
                      AND (OriginCountry = 'IN' OR DestinationCountry = 'IN')
                ),
                MaxDates AS (
                    -- Find the absolute latest SearchDate in the entire dataset
                    SELECT MAX(SearchDate) AS LatestDate FROM RawLegs
                )
                -- 4. Aggregate by Exact Seat Volume (Relative to the Latest Data Available)
                SELECT 
                    CONCAT(Sector, '|', toString(TravelDate)) AS SectorDateKey, 
                    Sector,
                    TravelDate,
                    CAST(SUM(IF(SearchDate = (SELECT LatestDate FROM MaxDates), Pax, 0)) AS Int32) AS SearchesToday,
                    CAST(ROUND(SUM(IF(SearchDate >= (SELECT LatestDate FROM MaxDates) - 6 AND SearchDate <= (SELECT LatestDate FROM MaxDates), Pax, 0)) / 7) AS Int32) AS Searches7DayAvg
                FROM RawLegs
                WHERE TravelDate >= addDays(today(), 3) 
                GROUP BY Sector, TravelDate
                HAVING SearchesToday > 0 OR Searches7DayAvg > 0

                ORDER BY SearchesToday DESC, Searches7DayAvg DESC
                LIMIT 1000 

                FORMAT JSONEachRow";

            try
            {
                var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromMinutes(10); // 10 minutes!

                var authString = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{chUser}:{chPass}"));

                // 1. Create a raw HttpRequestMessage
                using var request = new HttpRequestMessage(HttpMethod.Post, chHost);
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authString);

                // 2. CRITICAL: Put the massive query in the BODY, not the URL, to prevent Azure/Proxy blocks
                request.Content = new StringContent(query, Encoding.UTF8, "text/plain");

                _logger.LogInformation("ClickHouseHelper: Sending streaming aggregation query to ClickHouse via HTTP Body...");

                // 3. Actually use ResponseHeadersRead to stream data instantly
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

                if (response.IsSuccessStatusCode)
                {
                    using var stream = await response.Content.ReadAsStreamAsync();
                    using var streamReader = new StreamReader(stream);

                    string? line;
                    while ((line = await streamReader.ReadLineAsync()) != null)
                    {
                        if (string.IsNullOrWhiteSpace(line)) continue;

                        try
                        {
                            using var doc = JsonDocument.Parse(line);
                            var row = doc.RootElement;

                            string sectorDateKey = row.GetProperty("SectorDateKey").GetString() ?? "";
                            if (string.IsNullOrEmpty(sectorDateKey)) continue;

                            int today = 0;
                            var todayProp = row.GetProperty("SearchesToday");
                            if (todayProp.ValueKind == JsonValueKind.Number) today = todayProp.GetInt32();
                            else if (todayProp.ValueKind == JsonValueKind.String) int.TryParse(todayProp.GetString(), out today);

                            int avg7 = 0;
                            var avgProp = row.GetProperty("Searches7DayAvg");
                            if (avgProp.ValueKind == JsonValueKind.Number) avg7 = (int)avgProp.GetDouble();
                            else if (avgProp.ValueKind == JsonValueKind.String)
                            {
                                if (double.TryParse(avgProp.GetString(), out double tempAvg)) avg7 = (int)tempAvg;
                            }

                            results[sectorDateKey] = (today, avg7);
                        }
                        catch (Exception ex) 
                        {
                            _logger.LogError($"ClickHouseHelper Critical Exception: {ex.Message}");
                        }
                    }

                    _logger.LogInformation($"ClickHouseHelper: Successfully aggregated {results.Count} unique Sector/Date combinations.");
                }
                else
                {
                    string errorResponse = await response.Content.ReadAsStringAsync();
                    _logger.LogError($"ClickHouseHelper HTTP Error: {response.StatusCode}. Details: {errorResponse}");
                }
            }
            catch (TaskCanceledException ex)
            {
                _logger.LogError($"ClickHouseHelper Error: The connection timed out. Details: {ex.Message}");
            }
            catch (Exception ex)
            {
                _logger.LogError($"ClickHouseHelper Critical Exception: {ex.Message}");
            }

            return results;
        }
    }
}