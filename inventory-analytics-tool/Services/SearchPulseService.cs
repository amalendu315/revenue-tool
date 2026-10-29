using System.IO.Compression;
using System.Text;
using System.Text.Json;
using inventory_analytics_tool.Models;

namespace inventory_analytics_tool.Services
{
    public interface ISearchPulseService
    {
        Task<List<AgentDto>> GetAgentListAsync();
        Task<object> GetAgentAnalyticsAsync(int accountId, DateTime start, DateTime end, int thresholdMs, string? origin, string? destination, string journeyType);
        Task<object> GetAgentSkyscannerMatchAsync(int accountId, DateTime start, DateTime end);
        Task<object> GetAgentSkyscannerMatchDetailAsync(int accountId, DateTime bucketStart, int bucketMinutes);
        Task<object> GetTopAgentSearchCountAsync(DateTime start, DateTime end, string? origin, string? destination, DateTime? journeyStart, DateTime? journeyEnd);
        Task<object> GetRouteAnalyticsAsync(string origin, string destination, DateTime start, DateTime end);
        Task<object> GetOverviewAnalyticsAsync(string? route, DateTime start, DateTime end, string journeyType);
        Task<object> GetRouteContributionAsync(string date, string source, string journeyType);
        Task<object> GetSectorPressureRankingAsync(DateTime start, DateTime end);
        Task<object> GetSectorTravelDatePressureAsync(string origin, string destination, DateTime start, DateTime end, DateTime calendarStart);
    }

    public class SearchPulseService : ISearchPulseService
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _config;
        private readonly ILogger<SearchPulseService> _logger;

        private const string TABLE = "\"analytics\".\"search_eventsTemp\"";
        private const string AGENT_HISTORY_TABLE = "\"analytics\".\"agent_search_history\"";
        private const string ACCOUNT_TABLE = "\"analytics\".\"account_master\"";
        private const string DESTINATIONS_TABLE = "\"analytics\".\"destinations\"";
        private const string SKYSCANNER_TABLE = "\"analytics\".\"skyscanner_search_eventsTemp\"";
        private const string BLACKOUT_TABLE = "\"analytics\".\"sector_blackout_calendar\"";

        private const string HISTORY_DEST_JOIN = " LEFT JOIN (SELECT FDestID, aCode, aCode2, IsInternationalNew FROM analytics.destinations) fd ON h.FDestID = fd.FDestID";

        // Throttles concurrent ClickHouse queries to prevent DB CPU thrashing
        private static readonly SemaphoreSlim _dbSemaphore = new SemaphoreSlim(3, 3);

        public SearchPulseService(IHttpClientFactory httpClientFactory, IConfiguration config, ILogger<SearchPulseService> logger)
        {
            _httpClientFactory = httpClientFactory;
            _config = config;
            _logger = logger;
        }

        // --- EXTREME PERFORMANCE NETWORK EXECUTOR ---
        private async Task<List<JsonElement>> ExecuteQueryAsync(string query, Dictionary<string, string>? parameters = null)
        {
            await _dbSemaphore.WaitAsync();
            try
            {
                var results = new List<JsonElement>();
                string chHost = _config["ClickHouse:Host"] ?? "";
                string chUser = _config["ClickHouse:User"] ?? "";
                string chPass = _config["ClickHouse:Password"] ?? "";
                var authString = Convert.ToBase64String(Encoding.ASCII.GetBytes($"{chUser}:{chPass}"));

                string requestUri = chHost;
                if (parameters != null && parameters.Any())
                {
                    var qs = string.Join("&", parameters.Select(kvp => $"param_{kvp.Key}={Uri.EscapeDataString(kvp.Value)}"));
                    requestUri = requestUri.Contains("?") ? $"{requestUri}&{qs}" : $"{requestUri}?{qs}";
                }

                // GZIP Compression for massive network speed boost
                requestUri = requestUri.Contains("?") ? $"{requestUri}&enable_http_compression=1" : $"{requestUri}?enable_http_compression=1";

                if (!query.TrimEnd().EndsWith("JSON", StringComparison.OrdinalIgnoreCase))
                    query += "\nFORMAT JSON";

                var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromMinutes(2);

                using var request = new HttpRequestMessage(HttpMethod.Post, requestUri);
                request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", authString);
                request.Headers.AcceptEncoding.Add(new System.Net.Http.Headers.StringWithQualityHeaderValue("gzip"));
                request.Headers.Connection.Add("keep-alive");
                request.Content = new StringContent(query, Encoding.UTF8, "text/plain");

                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode)
                {
                    var error = await response.Content.ReadAsStringAsync();
                    throw new Exception($"ClickHouse HTTP {response.StatusCode}: {error}");
                }

                using var responseStream = await response.Content.ReadAsStreamAsync();
                Stream streamToRead = responseStream;

                if (response.Content.Headers.ContentEncoding.Any(x => x.Equals("gzip", StringComparison.OrdinalIgnoreCase)))
                {
                    streamToRead = new GZipStream(responseStream, CompressionMode.Decompress);
                }

                // High-speed JSON block parser
                using var doc = await JsonDocument.ParseAsync(streamToRead);
                if (doc.RootElement.TryGetProperty("data", out var dataArray))
                {
                    foreach (var row in dataArray.EnumerateArray())
                    {
                        results.Add(row.Clone());
                    }
                }

                return results;
            }
            finally
            {
                _dbSemaphore.Release();
            }
        }

        // --- SQL HELPERS ---
        private string GetAccountJoinSql() => $"(SELECT AccountID, argMax(AcName, LoadedAt) AS AcName FROM {ACCOUNT_TABLE} GROUP BY AccountID) am";

        private (string Join, string Filter) GetTypeFilterClause(string journeyType, string tableAlias = "s")
        {
            if (journeyType == "all" || string.IsNullOrEmpty(journeyType)) return ("", "");
            string join = $" LEFT JOIN (SELECT aCode, aCode2, argMax(IsInternationalNew, LoadedAt) AS is_intl FROM {DESTINATIONS_TABLE} GROUP BY aCode, aCode2) d ON {tableAlias}.Origin = d.aCode AND {tableAlias}.Destination = d.aCode2";
            if (journeyType == "domestic") return (join, " AND d.is_intl = 0 AND d.aCode != ''");
            if (journeyType == "international") return (join, " AND d.is_intl = 1");
            return (join, " AND (d.aCode IS NULL OR d.aCode = '')");
        }

        private string GetHistoryTypeFilter(string journeyType)
        {
            if (journeyType == "domestic") return " AND fd.IsInternationalNew = 0 AND fd.FDestID IS NOT NULL";
            if (journeyType == "international") return " AND fd.IsInternationalNew = 1";
            if (journeyType == "unknown") return " AND fd.FDestID IS NULL";
            return "";
        }

        private string GetSkyscannerLegsSubquery(string start, string end)
        {
            var branches = new List<string>();
            for (int i = 0; i < 6; i++)
            {
                string oCol = $"Leg_{i}_OriginIATA";
                string dCol = $"Leg_{i}_DestinationIATA";
                string dtCol = $"Leg_{i}_Date";
                string nullGuard = i > 0 ? $"\"{oCol}\" IS NOT NULL AND " : "";
                branches.Add($"SELECT DISTINCT Timestamp AS ts, {oCol} AS leg_origin, {dCol} AS leg_destination, toDate({dtCol}) AS leg_date FROM {SKYSCANNER_TABLE} WHERE Timestamp >= '{start}' AND Timestamp <= '{end}' AND {nullGuard}{oCol} != ''");
            }
            return $"(SELECT DISTINCT ts, leg_origin, leg_destination, leg_date FROM ({string.Join(" UNION ALL ", branches)}))";
        }

        private string GetSkyscannerMatchJoin(string alias, string start, string end)
        {
            string legs = GetSkyscannerLegsSubquery(start, end);
            return $" ASOF LEFT JOIN {legs} AS sky ON {alias}.Origin = sky.leg_origin AND {alias}.Destination = sky.leg_destination AND toDate({alias}.JourneyDate) = sky.leg_date AND sky.ts <= {alias}.EventTimeUtc";
        }

        // =========================================================
        // OVERVIEW DASHBOARD
        // =========================================================
        public async Task<object> GetOverviewAnalyticsAsync(string? route, DateTime start, DateTime end, string journeyType)
        {
            string? origin = null, destination = null;
            if (!string.IsNullOrEmpty(route) && route.Contains("-"))
            {
                var parts = route.Split('-');
                origin = parts[0].Trim().ToUpper();
                destination = parts[1].Trim().ToUpper();
            }
            journeyType = (journeyType ?? "all").ToLower();

            int bucketMinutes = (end - start).TotalHours <= 1 ? 1 : 5;
            string sStart = start.ToString("yyyy-MM-dd HH:mm:ss");
            string sEnd = end.ToString("yyyy-MM-dd HH:mm:ss");
            string chartStartStr = Math.Max(start.Ticks, end.AddDays(-2).Ticks) == start.Ticks ? sStart : end.AddDays(-2).ToString("yyyy-MM-dd HH:mm:ss");
            string journeyStartStr = end.AddDays(-30).ToString("yyyy-MM-dd");
            string journeyEndStr = end.AddDays(200).ToString("yyyy-MM-dd");

            var (typeJoin, typeFilter) = GetTypeFilterClause(journeyType);
            string hTypeExtra = GetHistoryTypeFilter(journeyType);
            string hJoin = (origin != null || journeyType != "all") ? HISTORY_DEST_JOIN : "";

            string seWhere = $"EventTimeUtc >= '{sStart}' AND EventTimeUtc <= '{sEnd}'";
            string histWhere = $"EntryDate >= '{sStart}' AND EntryDate <= '{sEnd}'";
            string skyWhere = $"Timestamp >= '{sStart}' AND Timestamp <= '{sEnd}'";
            string timeSeWhere = seWhere;
            string timeHistWhere = histWhere;

            if (origin != null)
            {
                seWhere += $" AND Origin = '{origin}' AND Destination = '{destination}'";
                histWhere += $" AND fd.aCode = '{origin}' AND fd.aCode2 = '{destination}'";
                skyWhere += $" AND OriginAirport = '{origin}' AND DestinationAirport = '{destination}'";
            }

            var tKpiSe = ExecuteQueryAsync($"SELECT count() AS api_current, uniqExact(AccountId) AS total_active_agents, countIf(Success = 1) AS successful_searches, max(ResponseTimeMs) AS max_response_time_ms, round(avg(ResponseTimeMs), 2) AS average_response_time_ms, quantileTDigest(0.50)(ResponseTimeMs) AS response_time_p50_ms, quantileTDigest(0.85)(ResponseTimeMs) AS response_time_p85_ms, quantileTDigest(0.99)(ResponseTimeMs) AS response_time_p99_ms FROM {TABLE} s {typeJoin} WHERE {seWhere} {typeFilter}");
            var tKpiHist = ExecuteQueryAsync($"SELECT countIf(IsApiSearch = 1) AS api_count, countIf(IsApiSearch IS NULL OR IsApiSearch = 0) AS portal_count FROM {AGENT_HISTORY_TABLE} h {hJoin} WHERE {histWhere} {hTypeExtra}");
            var tTopRoutesSe = ExecuteQueryAsync($"SELECT concat(Origin, '-', Destination) AS route, count() AS request_count FROM {TABLE} s {typeJoin} WHERE {timeSeWhere} {typeFilter} GROUP BY Origin, Destination");
            var tTopRoutesHist = ExecuteQueryAsync($"SELECT concat(fd.aCode, '-', fd.aCode2) AS route, count() AS request_count FROM {AGENT_HISTORY_TABLE} h {HISTORY_DEST_JOIN} WHERE {timeHistWhere} {hTypeExtra} AND fd.FDestID IS NOT NULL GROUP BY route");
            var tContribSe = ExecuteQueryAsync($"SELECT s.AccountId AS account_id, am.AcName AS account_name, count() AS request_count, max(s.ResponseTimeMs) AS max_response_time_ms, round(avg(s.ResponseTimeMs), 2) AS average_response_time_ms, countIf(s.ResponseTimeMs > 6000) AS high_response_time_requests FROM {TABLE} s LEFT JOIN {GetAccountJoinSql()} ON s.AccountId = am.AccountID {typeJoin} WHERE {seWhere} {typeFilter} GROUP BY s.AccountId, am.AcName");
            var tContribHist = ExecuteQueryAsync($"SELECT h.AccountID AS account_id, am.AcName AS account_name, countIf(h.IsApiSearch = 1) AS api_count, countIf(h.IsApiSearch IS NULL OR h.IsApiSearch = 0) AS portal_count, count() AS total_requests FROM {AGENT_HISTORY_TABLE} h LEFT JOIN {GetAccountJoinSql()} ON h.AccountID = am.AccountID {hJoin} WHERE {histWhere} AND h.AccountID IS NOT NULL {hTypeExtra} GROUP BY h.AccountID, am.AcName");
            var tApiTimeSe = ExecuteQueryAsync($"SELECT toStartOfInterval(EventTimeUtc, INTERVAL {bucketMinutes} MINUTE) AS bucket_start, concat(Origin, '-', Destination) AS route, count() AS search_count FROM {TABLE} s {typeJoin} WHERE {seWhere} {typeFilter} GROUP BY bucket_start, route");
            var tApiTimeHist = ExecuteQueryAsync($"SELECT toStartOfInterval(EntryDate, INTERVAL {bucketMinutes} MINUTE) AS bucket_start, concat(fd.aCode, '-', fd.aCode2) AS route, count() AS search_count FROM {AGENT_HISTORY_TABLE} h {HISTORY_DEST_JOIN} WHERE {histWhere} {hTypeExtra} AND h.IsApiSearch = 1 AND fd.FDestID IS NOT NULL GROUP BY bucket_start, route");

            string skyJourneyTypeSql = journeyType == "domestic" ? " AND OriginCountry = DestinationCountry" : (journeyType == "international" ? " AND OriginCountry != DestinationCountry" : "");

            var tJourneySe = ExecuteQueryAsync($"SELECT toDate(JourneyDate) AS journey_date, count() AS search_count FROM {TABLE} s {typeJoin} WHERE {seWhere} {typeFilter} AND JourneyDate >= '{journeyStartStr}' AND JourneyDate <= '{journeyEndStr}' GROUP BY journey_date");
            var tJourneyHist = ExecuteQueryAsync($"SELECT toDate(TravelDate) AS journey_date, countIf(IsApiSearch = 1) AS api_count, countIf(IsApiSearch IS NULL OR IsApiSearch = 0) AS portal_count FROM {AGENT_HISTORY_TABLE} h {hJoin} WHERE {histWhere} {hTypeExtra} AND TravelDate >= '{journeyStartStr}' AND TravelDate <= '{journeyEndStr}' AND TravelDate IS NOT NULL GROUP BY journey_date");
            var tJourneySky = journeyType == "unknown" ? Task.FromResult(new List<JsonElement>()) : ExecuteQueryAsync($"SELECT toDate(DepartureDate) AS journey_date, count() AS search_count FROM {SKYSCANNER_TABLE} WHERE {skyWhere} {skyJourneyTypeSql} AND DepartureDate >= '{journeyStartStr}' AND DepartureDate <= '{journeyEndStr}' GROUP BY journey_date");
            var tTopAgentsRows = ExecuteQueryAsync($"SELECT s.AccountId AS account_id, am.AcName AS account_name, max(s.ResponseTimeMs) AS max_response_time_ms, count() AS request_count FROM {TABLE} s LEFT JOIN {GetAccountJoinSql()} ON s.AccountId = am.AccountID {typeJoin} WHERE EventTimeUtc >= '{chartStartStr}' AND EventTimeUtc <= '{sEnd}' {(!string.IsNullOrEmpty(origin) ? $" AND Origin = '{origin}' AND Destination = '{destination}'" : "")} {typeFilter} GROUP BY s.AccountId, am.AcName ORDER BY max_response_time_ms DESC, request_count DESC LIMIT 10");

            await Task.WhenAll(tKpiSe, tKpiHist, tTopRoutesSe, tTopRoutesHist, tContribSe, tContribHist, tApiTimeSe, tApiTimeHist, tJourneySe, tJourneyHist, tJourneySky, tTopAgentsRows);

            var kpiSeRow = tKpiSe.Result.FirstOrDefault();
            var kpiHistRow = tKpiHist.Result.FirstOrDefault();
            int apiCurrent = GetIntOrZero(kpiSeRow, "api_current");
            int successfulSearches = GetIntOrZero(kpiSeRow, "successful_searches");
            int histApi = GetIntOrZero(kpiHistRow, "api_count");
            int histPortal = GetIntOrZero(kpiHistRow, "portal_count");

            var routeDict = new Dictionary<string, int>();
            foreach (var r in tTopRoutesSe.Result.Concat(tTopRoutesHist.Result))
            {
                string rName = GetStringOrNull(r, "route") ?? "Unknown";
                if (!routeDict.ContainsKey(rName)) routeDict[rName] = 0;
                routeDict[rName] += GetIntOrZero(r, "request_count");
            }
            var topRoute = routeDict.OrderByDescending(x => x.Value).Select(x => new { route = x.Key, request_count = x.Value }).FirstOrDefault();
            var top5RoutesNames = routeDict.OrderByDescending(x => x.Value).Take(5).Select(x => x.Key).ToList();

            var routeRequestSeries = new List<object>();
            if (top5RoutesNames.Any())
            {
                string routeInClause = string.Join(",", top5RoutesNames.Select(r => $"'{r}'"));
                var tRouteSeriesSe = ExecuteQueryAsync($"SELECT toStartOfInterval(EventTimeUtc, INTERVAL {bucketMinutes} MINUTE) AS bucket_start, concat(Origin, '-', Destination) AS route, count() AS request_count FROM {TABLE} s {typeJoin} WHERE EventTimeUtc >= '{chartStartStr}' AND EventTimeUtc <= '{sEnd}' AND concat(Origin, '-', Destination) IN ({routeInClause}) {typeFilter} GROUP BY bucket_start, route");
                var tRouteSeriesHist = ExecuteQueryAsync($"SELECT toStartOfInterval(EntryDate, INTERVAL {bucketMinutes} MINUTE) AS bucket_start, concat(fd.aCode, '-', fd.aCode2) AS route, count() AS request_count FROM {AGENT_HISTORY_TABLE} h {HISTORY_DEST_JOIN} WHERE EntryDate >= '{chartStartStr}' AND EntryDate <= '{sEnd}' AND concat(fd.aCode, '-', fd.aCode2) IN ({routeInClause}) {hTypeExtra} GROUP BY bucket_start, route");

                await Task.WhenAll(tRouteSeriesSe, tRouteSeriesHist);
                var bucketRouteCounts = new Dictionary<string, int>();
                foreach (var r in tRouteSeriesSe.Result.Concat(tRouteSeriesHist.Result))
                {
                    string key = $"{GetStringOrNull(r, "bucket_start")}|{GetStringOrNull(r, "route")}";
                    if (!bucketRouteCounts.ContainsKey(key)) bucketRouteCounts[key] = 0;
                    bucketRouteCounts[key] += GetIntOrZero(r, "request_count");
                }
                routeRequestSeries = bucketRouteCounts.Select(kvp => new { bucket_start = kvp.Key.Split('|')[0], route = kvp.Key.Split('|')[1], request_count = kvp.Value }).OrderBy(x => x.bucket_start).ToList<object>();
            }

            var agentDict = new Dictionary<int, dynamic>();
            foreach (var r in tContribSe.Result)
            {
                int accId = GetIntOrZero(r, "account_id");
                agentDict[accId] = new { account_id = accId, account_name = GetStringOrNull(r, "account_name"), api_count = GetIntOrZero(r, "request_count"), portal_count = 0, max_response_time_ms = GetDoubleOrNull(r, "max_response_time_ms"), average_response_time_ms = GetDoubleOrNull(r, "average_response_time_ms"), total_requests = GetIntOrZero(r, "request_count"), high_response_time_requests = GetIntOrZero(r, "high_response_time_requests") };
            }
            foreach (var r in tContribHist.Result)
            {
                int accId = GetIntOrZero(r, "account_id");
                if (agentDict.ContainsKey(accId))
                {
                    var existing = agentDict[accId];
                    agentDict[accId] = new { account_id = accId, account_name = existing.account_name ?? GetStringOrNull(r, "account_name"), api_count = existing.api_count + GetIntOrZero(r, "api_count"), portal_count = existing.portal_count + GetIntOrZero(r, "portal_count"), max_response_time_ms = existing.max_response_time_ms, average_response_time_ms = existing.average_response_time_ms, total_requests = existing.total_requests + GetIntOrZero(r, "total_requests"), high_response_time_requests = existing.high_response_time_requests };
                }
                else
                {
                    agentDict[accId] = new { account_id = accId, account_name = GetStringOrNull(r, "account_name"), api_count = GetIntOrZero(r, "api_count"), portal_count = GetIntOrZero(r, "portal_count"), max_response_time_ms = (double?)null, average_response_time_ms = (double?)null, total_requests = GetIntOrZero(r, "total_requests"), high_response_time_requests = 0 };
                }
            }

            var totalAll = agentDict.Values.Sum(x => (int)(x.api_count + x.portal_count));
            if (totalAll == 0) totalAll = 1;
            var contributions = agentDict.Values.Select(x => new { x.account_id, x.account_name, request_count = x.api_count + x.portal_count, api_count = x.api_count, portal_count = x.portal_count, traffic_percentage = Math.Round(100.0 * (x.api_count + x.portal_count) / totalAll, 2) }).OrderByDescending(x => x.request_count).ToList();
            var agentSummary = agentDict.Values.OrderByDescending(x => x.max_response_time_ms ?? 0).ToList();

            var apiBuckets = new Dictionary<string, (int search_count, HashSet<string> routes)>();
            foreach (var r in tApiTimeSe.Result.Concat(tApiTimeHist.Result))
            {
                string b = GetStringOrNull(r, "bucket_start") ?? "";
                if (!apiBuckets.ContainsKey(b)) apiBuckets[b] = (0, new HashSet<string>());
                var current = apiBuckets[b];
                current.routes.Add(GetStringOrNull(r, "route") ?? "");
                apiBuckets[b] = (current.search_count + GetIntOrZero(r, "search_count"), current.routes);
            }
            var apiSearchTimeSeries = apiBuckets.OrderBy(x => x.Key).Select(x => new { bucket_start = x.Key, api_search_count = x.Value.search_count, distinct_route_count = x.Value.routes.Count }).ToList();

            var journeyDates = new Dictionary<string, (int api, int portal, int sky)>();
            foreach (var r in tJourneySe.Result) { string d = GetStringOrNull(r, "journey_date") ?? ""; if (!journeyDates.ContainsKey(d)) journeyDates[d] = (0, 0, 0); journeyDates[d] = (journeyDates[d].api + GetIntOrZero(r, "search_count"), journeyDates[d].portal, journeyDates[d].sky); }
            foreach (var r in tJourneyHist.Result) { string d = GetStringOrNull(r, "journey_date") ?? ""; if (!journeyDates.ContainsKey(d)) journeyDates[d] = (0, 0, 0); journeyDates[d] = (journeyDates[d].api + GetIntOrZero(r, "api_count"), journeyDates[d].portal + GetIntOrZero(r, "portal_count"), journeyDates[d].sky); }
            foreach (var r in tJourneySky.Result) { string d = GetStringOrNull(r, "journey_date") ?? ""; if (!journeyDates.ContainsKey(d)) journeyDates[d] = (0, 0, 0); journeyDates[d] = (journeyDates[d].api, journeyDates[d].portal, journeyDates[d].sky + GetIntOrZero(r, "search_count")); }
            var journeyDateSeries = journeyDates.OrderBy(x => x.Key).Select(x => new { journey_date = x.Key, api_count = x.Value.api, portal_count = x.Value.portal, skyscanner_count = x.Value.sky, source = "ours" }).ToList();

            var responseTimeSeries = new List<object>();
            if (tTopAgentsRows.Result.Any())
            {
                string agentInClause = string.Join(",", tTopAgentsRows.Result.Select(r => GetIntOrZero(r, "account_id")));
                var agentTimeSeries = await ExecuteQueryAsync($"SELECT toStartOfInterval(EventTimeUtc, INTERVAL {bucketMinutes} MINUTE) AS bucket_start, AccountId AS account_id, max(ResponseTimeMs) AS max_response_time_ms, count() AS request_count FROM {TABLE} s {typeJoin} WHERE EventTimeUtc >= '{chartStartStr}' AND EventTimeUtc <= '{sEnd}' {(!string.IsNullOrEmpty(origin) ? $" AND Origin = '{origin}' AND Destination = '{destination}'" : "")} {typeFilter} AND AccountId IN ({agentInClause}) GROUP BY bucket_start, AccountId ORDER BY bucket_start ASC");
                responseTimeSeries = agentTimeSeries.Select(r => new { bucket_start = GetStringOrNull(r, "bucket_start"), account_id = GetIntOrZero(r, "account_id"), max_response_time_ms = GetDoubleOrNull(r, "max_response_time_ms") }).ToList<object>();
            }

            return new
            {
                filters = new { from_date = start.ToString("o"), to_date = end.ToString("o"), route = route, type = journeyType },
                response_time_chart_window = new { from_date = chartStartStr, to_date = sEnd },
                chart_aggregation_minutes = bucketMinutes,
                kpis = new
                {
                    total_requests = apiCurrent + histApi + histPortal,
                    total_active_agents = GetIntOrZero(kpiSeRow, "total_active_agents"),
                    search_to_result_ratio = new { total_searches = apiCurrent, successful_searches = successfulSearches, percentage = CalcPct(successfulSearches, apiCurrent) },
                    average_response_time_ms = GetDoubleOrNull(kpiSeRow, "average_response_time_ms"),
                    response_time_p50_ms = GetDoubleOrNull(kpiSeRow, "response_time_p50_ms"),
                    response_time_p85_ms = GetDoubleOrNull(kpiSeRow, "response_time_p85_ms"),
                    response_time_p99_ms = GetDoubleOrNull(kpiSeRow, "response_time_p99_ms"),
                    max_response_time_ms = GetDoubleOrNull(kpiSeRow, "max_response_time_ms"),
                    max_response_time_event_time = GetStringOrNull(kpiSeRow, "max_response_time_event_time"),
                    most_searched_route = topRoute,
                    api_search_count = apiCurrent + histApi,
                    portal_search_count = histPortal
                },
                agent_contributions = contributions,
                response_time_top_agents = tTopAgentsRows.Result.Select(r => new { account_id = GetIntOrZero(r, "account_id"), account_name = GetStringOrNull(r, "account_name") }),
                response_time_series = responseTimeSeries,
                api_search_time_series = apiSearchTimeSeries,
                top_routes = top5RoutesNames.Select(r => new { route = r }),
                route_request_series = routeRequestSeries,
                agent_summary = agentSummary,
                journey_date_series = journeyDateSeries
            };
        }

        public async Task<object> GetRouteContributionAsync(string date, string source, string journeyType)
        {
            var (typeJoin, typeFilter) = GetTypeFilterClause(journeyType);
            var results = new Dictionary<string, int>();

            if (source == "ours")
            {
                string hTypeExtra = GetHistoryTypeFilter(journeyType);
                var tSe = ExecuteQueryAsync($"SELECT concat(s.Origin, '-', s.Destination) AS route, count() AS search_count FROM {TABLE} s {typeJoin} WHERE toDate(s.JourneyDate) = '{date}' {typeFilter} GROUP BY route");
                var tHist = ExecuteQueryAsync($"SELECT concat(fd.aCode, '-', fd.aCode2) AS route, count() AS search_count FROM {AGENT_HISTORY_TABLE} h {HISTORY_DEST_JOIN} WHERE toDate(h.TravelDate) = '{date}' AND fd.FDestID IS NOT NULL {hTypeExtra} GROUP BY route");

                await Task.WhenAll(tSe, tHist);
                foreach (var r in tSe.Result.Concat(tHist.Result))
                {
                    string rStr = GetStringOrNull(r, "route") ?? "Unknown";
                    if (!results.ContainsKey(rStr)) results[rStr] = 0;
                    results[rStr] += GetIntOrZero(r, "search_count");
                }
            }
            else if (source == "skyscanner" && journeyType != "unknown")
            {
                string skyFilter = journeyType == "domestic" ? " AND OriginCountry = DestinationCountry" : (journeyType == "international" ? " AND OriginCountry != DestinationCountry" : "");
                var skyRows = await ExecuteQueryAsync($"SELECT concat(OriginAirport, '-', DestinationAirport) AS route, count() AS search_count FROM {SKYSCANNER_TABLE} WHERE toDate(DepartureDate) = '{date}' {skyFilter} GROUP BY route ORDER BY search_count DESC");
                foreach (var r in skyRows)
                {
                    results[GetStringOrNull(r, "route") ?? "Unknown"] = GetIntOrZero(r, "search_count");
                }
            }

            return new { date = date, source = source, routes = results.OrderByDescending(x => x.Value).Select(x => new { route = x.Key, search_count = x.Value }) };
        }

        // =========================================================
        // AGENT ANALYTICS DASHBOARD
        // =========================================================

        public async Task<List<AgentDto>> GetAgentListAsync()
        {
            string query = $@"SELECT AccountID, argMax(AcName, LoadedAt) AS AcName FROM {ACCOUNT_TABLE} WHERE Active = 'Active' GROUP BY AccountID ORDER BY AcName";
            var rows = await ExecuteQueryAsync(query);
            return rows.Select(r => new AgentDto { AccountId = GetIntOrZero(r, "AccountID"), AccountName = GetStringOrNull(r, "AcName") }).ToList();
        }

        public async Task<object> GetAgentAnalyticsAsync(int accountId, DateTime start, DateTime end, int thresholdMs, string? origin, string? destination, string journeyType)
        {
            int intervalMinutes = (end - start).TotalHours <= 1 ? 1 : 5;
            string sStart = start.ToString("yyyy-MM-dd HH:mm:ss");
            string sEnd = end.ToString("yyyy-MM-dd HH:mm:ss");

            var (typeJoin, typeFilter) = GetTypeFilterClause(journeyType);
            string seWhere = $"AccountId = {accountId} AND EventTimeUtc >= '{sStart}' AND EventTimeUtc < '{sEnd}'";

            if (origin != null)
            {
                seWhere += $" AND Origin = '{origin}' AND Destination = '{destination}'";
            }

            var tAvail = await ExecuteQueryAsync($"SELECT min(EventTimeUtc) AS data_start_datetime, max(EventTimeUtc) AS data_end_datetime FROM {TABLE} s {typeJoin} WHERE {seWhere} {typeFilter}");
            var availability = tAvail.FirstOrDefault();
            bool hasData = availability.TryGetProperty("data_start_datetime", out var ds) && ds.ValueKind != JsonValueKind.Null;

            if (!hasData) return GenerateEmptyAgentResponse(accountId, start, end, thresholdMs, origin, destination, journeyType, intervalMinutes);

            var tKpi = ExecuteQueryAsync($@"
                SELECT count() AS total_search_count, max(ResponseTimeMs) AS max_response_time_ms, argMax(EventTimeUtc, ResponseTimeMs) AS max_response_time_event_time, 
                       countIf(Success = 1) AS successful_search_count, round(countIf(Success = 1) * 100.0 / nullIf(count(), 0), 2) AS success_percentage, 
                       round(quantile(0.50)(ResponseTimeMs), 2) AS p50_response_time_ms, round(quantile(0.99)(ResponseTimeMs), 2) AS p99_response_time_ms, 
                       countIf(ResponseTimeMs > {thresholdMs}) AS threshold_exceeded_count 
                FROM {TABLE} s {typeJoin} WHERE {seWhere} {typeFilter}");

            var tMaxRoute = ExecuteQueryAsync($"SELECT concat(Origin, '-', Destination) AS route, count() AS search_count FROM {TABLE} s {typeJoin} WHERE {seWhere} {typeFilter} GROUP BY Origin, Destination ORDER BY search_count DESC LIMIT 1");

            var tDist = ExecuteQueryAsync($@"
                SELECT countIf(ResponseTimeMs < 1000) AS under_1s, countIf(ResponseTimeMs >= 1000 AND ResponseTimeMs < 2000) AS between_1_2s, 
                       countIf(ResponseTimeMs >= 2000 AND ResponseTimeMs < 3000) AS between_2_3s, countIf(ResponseTimeMs >= 3000 AND ResponseTimeMs < 4000) AS between_3_4s, 
                       countIf(ResponseTimeMs >= 4000 AND ResponseTimeMs < 5000) AS between_4_5s, countIf(ResponseTimeMs >= 5000) AS over_5s 
                FROM {TABLE} s {typeJoin} WHERE {seWhere} {typeFilter}");

            var tArea = ExecuteQueryAsync($@"
                SELECT event_bucket, max(account_max_response_time_ms) AS max_response_time_ms, avg(all_accounts_avg_response_time_ms) AS all_accounts_avg_response_time_ms 
                FROM (
                    SELECT toStartOfInterval(EventTimeUtc, INTERVAL {intervalMinutes} MINUTE) AS event_bucket, max(ResponseTimeMs) AS account_max_response_time_ms, CAST(0 AS Float64) AS all_accounts_avg_response_time_ms 
                    FROM {TABLE} s {typeJoin} WHERE {seWhere} {typeFilter} GROUP BY event_bucket
                    UNION ALL 
                    SELECT toStartOfInterval(EventTimeUtc, INTERVAL {intervalMinutes} MINUTE) AS event_bucket, CAST(0 AS Float64) AS account_max_response_time_ms, avg(ResponseTimeMs) AS all_accounts_avg_response_time_ms 
                    FROM {TABLE} s {typeJoin} WHERE EventTimeUtc >= '{sStart}' AND EventTimeUtc < '{sEnd}' {typeFilter} GROUP BY event_bucket
                ) GROUP BY event_bucket ORDER BY event_bucket");

            var tSearchCount = ExecuteQueryAsync($"SELECT toStartOfInterval(EventTimeUtc, INTERVAL {intervalMinutes} MINUTE) AS event_bucket, count() AS search_count FROM {TABLE} s {typeJoin} WHERE {seWhere} {typeFilter} GROUP BY event_bucket ORDER BY event_bucket");

            var tBubble = ExecuteQueryAsync($"SELECT toDate(JourneyDate) AS departure_date, count() AS search_count, round(avg(ResponseTimeMs) / 1000.0, 2) AS avg_response_time_seconds, round(count() * (avg(ResponseTimeMs) / 1000.0), 2) AS bubble_size FROM {TABLE} s {typeJoin} WHERE {seWhere} {typeFilter} GROUP BY departure_date ORDER BY departure_date");

            await Task.WhenAll(tKpi, tMaxRoute, tDist, tArea, tSearchCount, tBubble);

            var responseTimeSeries = tArea.Result.Select(r => new {
                event_time = GetStringOrNull(r, "event_bucket"),
                max_response_time_ms = GetDoubleOrNull(r, "max_response_time_ms"),
                all_accounts_avg_response_time_ms = Math.Round(GetDoubleOrNull(r, "all_accounts_avg_response_time_ms") ?? 0, 2)
            }).ToList();

            var searchCountSeries = tSearchCount.Result.Select(r => new {
                event_time = GetStringOrNull(r, "event_bucket"),
                search_count = GetIntOrZero(r, "search_count")
            }).ToList();

            var bubbleSeries = tBubble.Result.Select(r => new {
                departure_date = GetStringOrNull(r, "departure_date"),
                search_count = GetIntOrZero(r, "search_count"),
                avg_response_time_seconds = GetDoubleOrNull(r, "avg_response_time_seconds"),
                bubble_size = GetDoubleOrNull(r, "bubble_size")
            }).ToList();

            var formattedResponse = FormatAgentResponse(accountId, start, end, thresholdMs, origin, destination, journeyType, intervalMinutes, availability, tKpi.Result.First(), tMaxRoute.Result.FirstOrDefault(), tDist.Result.First());

            var dictResponse = JsonSerializer.Deserialize<Dictionary<string, object>>(JsonSerializer.Serialize(formattedResponse))!;
            dictResponse["response_time_series"] = responseTimeSeries;
            dictResponse["search_count_series"] = searchCountSeries;
            dictResponse["departure_date_series"] = bubbleSeries;

            return dictResponse;
        }

        public async Task<object> GetAgentSkyscannerMatchAsync(int accountId, DateTime start, DateTime end)
        {
            int bucketMinutes = (end - start).TotalHours <= 1 ? 1 : 5;
            string sStart = start.ToString("yyyy-MM-dd HH:mm:ss");
            string sEnd = end.ToString("yyyy-MM-dd HH:mm:ss");

            string joinSql = GetSkyscannerMatchJoin("s", sStart, sEnd);
            string matchExpr = $"sky.ts != toDateTime(0) AND dateDiff('millisecond', sky.ts, s.EventTimeUtc) BETWEEN 0 AND 40";

            string query = $@"
                SELECT toStartOfInterval(s.EventTimeUtc, INTERVAL {bucketMinutes} MINUTE) AS bucket_start, count() AS total_searches, countIf({matchExpr}) AS matched_count 
                FROM {TABLE} s {joinSql} 
                WHERE s.AccountId = {accountId} AND s.EventTimeUtc >= '{sStart}' AND s.EventTimeUtc <= '{sEnd}' 
                GROUP BY bucket_start ORDER BY bucket_start";

            var rows = await ExecuteQueryAsync(query);
            var buckets = rows.Select(r => new {
                bucket_start = GetStringOrNull(r, "bucket_start"),
                total_searches = GetIntOrZero(r, "total_searches"),
                matched_count = GetIntOrZero(r, "matched_count"),
                match_percentage = CalcPct(GetIntOrZero(r, "matched_count"), GetIntOrZero(r, "total_searches"))
            }).ToList();

            int totalSearches = buckets.Sum(b => b.total_searches);
            int totalMatched = buckets.Sum(b => b.matched_count);

            return new
            {
                account_id = accountId,
                filters = new { from_date = start.ToString("o"), to_date = end.ToString("o") },
                match_window_ms = 40,
                chart_aggregation = new { interval_minutes = bucketMinutes },
                totals = new { total_searches = totalSearches, matched_count = totalMatched, match_percentage = CalcPct(totalMatched, totalSearches) },
                buckets = buckets
            };
        }

        public async Task<object> GetAgentSkyscannerMatchDetailAsync(int accountId, DateTime bucketStart, int bucketMinutes)
        {
            DateTime bucketEnd = bucketStart.AddMinutes(bucketMinutes);
            string sStart = bucketStart.ToString("yyyy-MM-dd HH:mm:ss");
            string sEnd = bucketEnd.ToString("yyyy-MM-dd HH:mm:ss");

            string joinSql = GetSkyscannerMatchJoin("s", sStart, sEnd);
            string matchExpr = $"sky.ts != toDateTime(0) AND dateDiff('millisecond', sky.ts, s.EventTimeUtc) BETWEEN 0 AND 40";

            var tRows = ExecuteQueryAsync($@"
                SELECT s.EventTimeUtc AS event_time, concat(s.Origin, '-', s.Destination) AS route, s.JourneyDate AS departure_date, 
                       {matchExpr} AS is_skyscanner_matched, sky.ts AS skyscanner_search_time, dateDiff('millisecond', sky.ts, s.EventTimeUtc) AS diff_ms 
                FROM {TABLE} s {joinSql} 
                WHERE s.AccountId = {accountId} AND s.EventTimeUtc >= '{sStart}' AND s.EventTimeUtc < '{sEnd}' 
                ORDER BY is_skyscanner_matched DESC, s.EventTimeUtc LIMIT 5000");

            var tCount = ExecuteQueryAsync($"SELECT count() AS total FROM {TABLE} s WHERE s.AccountId = {accountId} AND s.EventTimeUtc >= '{sStart}' AND s.EventTimeUtc < '{sEnd}'");

            await Task.WhenAll(tRows, tCount);
            int totalCount = GetIntOrZero(tCount.Result.First(), "total");

            var events = tRows.Result.Select(r => {
                string skyTime = GetStringOrNull(r, "skyscanner_search_time") ?? "";
                bool noMatch = skyTime.StartsWith("1970-01-01") || string.IsNullOrEmpty(skyTime);
                return new
                {
                    event_time = GetStringOrNull(r, "event_time"),
                    route = GetStringOrNull(r, "route"),
                    departure_date = GetStringOrNull(r, "departure_date"),
                    is_skyscanner_matched = GetIntOrZero(r, "is_skyscanner_matched") == 1,
                    skyscanner_search_time = noMatch ? null : skyTime,
                    diff_ms = noMatch ? (int?)null : GetIntOrZero(r, "diff_ms")
                };
            }).ToList();

            return new { account_id = accountId, bucket_start = bucketStart.ToString("o"), bucket_minutes = bucketMinutes, total_count = totalCount, truncated = totalCount > events.Count, events = events };
        }

        public async Task<object> GetTopAgentSearchCountAsync(DateTime start, DateTime end, string? origin, string? destination, DateTime? journeyStart, DateTime? journeyEnd)
        {
            string sStart = start.ToString("yyyy-MM-dd HH:mm:ss");
            string sEnd = end.ToString("yyyy-MM-dd HH:mm:ss");

            string seWhere = $"EventTimeUtc >= '{sStart}' AND EventTimeUtc <= '{sEnd}'";
            string histWhere = $"EntryDate >= '{sStart}' AND EntryDate <= '{sEnd}'";
            string skyWhere = $"Timestamp >= '{sStart}' AND Timestamp <= '{sEnd}'";

            if (origin != null)
            {
                seWhere += $" AND Origin = '{origin}' AND Destination = '{destination}'";
                histWhere += $" AND fd.aCode = '{origin}' AND fd.aCode2 = '{destination}'";
                skyWhere += $" AND OriginAirport = '{origin}' AND DestinationAirport = '{destination}'";
            }
            if (journeyStart.HasValue)
            {
                seWhere += $" AND JourneyDate >= '{journeyStart.Value:yyyy-MM-dd HH:mm:ss}' AND JourneyDate <= '{journeyEnd.Value:yyyy-MM-dd HH:mm:ss}'";
                histWhere += $" AND TravelDate >= '{journeyStart.Value:yyyy-MM-dd HH:mm:ss}' AND TravelDate <= '{journeyEnd.Value:yyyy-MM-dd HH:mm:ss}'";
                skyWhere += $" AND DepartureDate >= '{journeyStart.Value:yyyy-MM-dd HH:mm:ss}' AND DepartureDate <= '{journeyEnd.Value:yyyy-MM-dd HH:mm:ss}'";
            }

            var tTopSe = ExecuteQueryAsync($"SELECT s.AccountId AS account_id, am.AcName AS account_name, count() AS search_count FROM {TABLE} s LEFT JOIN {GetAccountJoinSql()} ON s.AccountId = am.AccountID WHERE {seWhere} GROUP BY s.AccountId, am.AcName");
            var tTopHist = ExecuteQueryAsync($"SELECT h.AccountID AS account_id, am.AcName AS account_name, count() AS search_count FROM {AGENT_HISTORY_TABLE} h LEFT JOIN {GetAccountJoinSql()} ON h.AccountID = am.AccountID {HISTORY_DEST_JOIN} WHERE {histWhere} AND h.AccountID IS NOT NULL GROUP BY h.AccountID, am.AcName");

            await Task.WhenAll(tTopSe, tTopHist);

            var combinedTotals = new Dictionary<int, (string name, int count)>();
            foreach (var r in tTopSe.Result.Concat(tTopHist.Result))
            {
                int id = GetIntOrZero(r, "account_id");
                if (id == 0) continue;
                if (!combinedTotals.ContainsKey(id)) combinedTotals[id] = (GetStringOrNull(r, "account_name") ?? "", 0);
                combinedTotals[id] = (combinedTotals[id].name != "" ? combinedTotals[id].name : GetStringOrNull(r, "account_name") ?? "", combinedTotals[id].count + GetIntOrZero(r, "search_count"));
            }

            var topAgents = combinedTotals.OrderByDescending(x => x.Value.count).Take(10).Select(x => new { account_id = x.Key, account_name = x.Value.name, search_count = x.Value.count }).ToList();

            var series = new List<object>();
            var skySeries = new List<object>();

            if (topAgents.Any())
            {
                string ids = string.Join(",", topAgents.Select(x => x.account_id));
                var tSeriesSe = ExecuteQueryAsync($"SELECT toStartOfInterval(EventTimeUtc, INTERVAL 1 MINUTE) AS bucket_start, AccountId AS account_id, count() AS search_count FROM {TABLE} s WHERE {seWhere} AND AccountId IN ({ids}) GROUP BY bucket_start, AccountId");
                var tSeriesHist = ExecuteQueryAsync($"SELECT toStartOfInterval(EntryDate, INTERVAL 1 MINUTE) AS bucket_start, h.AccountID AS account_id, count() AS search_count FROM {AGENT_HISTORY_TABLE} h {HISTORY_DEST_JOIN} WHERE {histWhere} AND h.AccountID IN ({ids}) GROUP BY bucket_start, h.AccountID");
                var tSky = ExecuteQueryAsync($"SELECT toStartOfInterval(Timestamp, INTERVAL 1 MINUTE) AS bucket_start, count() AS search_count FROM {SKYSCANNER_TABLE} WHERE {skyWhere} GROUP BY bucket_start ORDER BY bucket_start");

                await Task.WhenAll(tSeriesSe, tSeriesHist, tSky);

                var bucketCounts = new Dictionary<string, int>();
                foreach (var r in tSeriesSe.Result.Concat(tSeriesHist.Result))
                {
                    string key = $"{GetStringOrNull(r, "bucket_start")}|{GetIntOrZero(r, "account_id")}";
                    if (!bucketCounts.ContainsKey(key)) bucketCounts[key] = 0;
                    bucketCounts[key] += GetIntOrZero(r, "search_count");
                }
                series = bucketCounts.Select(x => new { bucket_start = x.Key.Split('|')[0], account_id = int.Parse(x.Key.Split('|')[1]), search_count = x.Value }).OrderBy(x => x.bucket_start).ToList<object>();

                skySeries = tSky.Result.Select(r => new { bucket_start = GetStringOrNull(r, "bucket_start"), search_count = GetIntOrZero(r, "search_count") }).ToList<object>();
            }

            return new
            {
                filters = new { from_date = start.ToString("o"), to_date = end.ToString("o"), route = origin != null ? $"{origin}-{destination}" : null, journey_from = journeyStart?.ToString("o"), journey_to = journeyEnd?.ToString("o") },
                bucket_minutes = 1,
                top_agents = topAgents,
                series = series,
                skyscanner_series = skySeries
            };
        }

        public async Task<object> GetRouteAnalyticsAsync(string origin, string destination, DateTime start, DateTime end)
        {
            string sStart = start.ToString("yyyy-MM-dd HH:mm:ss");
            string sEnd = end.ToString("yyyy-MM-dd HH:mm:ss");
            string routeKey = $"{origin}-{destination}";

            var p = new Dictionary<string, string> {
                { "start", sStart }, { "end", sEnd }, { "origin", origin }, { "dest", destination }
            };

            // 1. Base Counts & Momentum Check
            DateTime midpoint = start + TimeSpan.FromTicks((end - start).Ticks / 2);
            p.Add("midpoint", midpoint.ToString("yyyy-MM-dd HH:mm:ss"));

            var tBaseCounts = ExecuteQueryAsync($@"
                SELECT 
                    (SELECT count() FROM {TABLE} WHERE EventTimeUtc >= {{start:DateTime64(7)}} AND EventTimeUtc <= {{end:DateTime64(7)}} AND Origin = {{origin:String}} AND Destination = {{dest:String}}) AS se_count,
                    (SELECT count() FROM {AGENT_HISTORY_TABLE} h {HISTORY_DEST_JOIN} WHERE EntryDate >= {{start:DateTime64(7)}} AND EntryDate <= {{end:DateTime64(7)}} AND fd.aCode = {{origin:String}} AND fd.aCode2 = {{dest:String}}) AS hist_count,
                    (SELECT count() FROM {SKYSCANNER_TABLE} WHERE Timestamp >= {{start:DateTime64(7)}} AND Timestamp <= {{end:DateTime64(7)}} AND OriginAirport = {{origin:String}} AND DestinationAirport = {{dest:String}}) AS sky_count,
                    (SELECT count() FROM {TABLE} WHERE EventTimeUtc >= {{start:DateTime64(7)}} AND EventTimeUtc <= {{midpoint:DateTime64(7)}} AND Origin = {{origin:String}} AND Destination = {{dest:String}}) AS se_mid,
                    (SELECT count() FROM {SKYSCANNER_TABLE} WHERE Timestamp >= {{start:DateTime64(7)}} AND Timestamp <= {{midpoint:DateTime64(7)}} AND OriginAirport = {{origin:String}} AND DestinationAirport = {{dest:String}}) AS sky_mid
                ", p);

            var countsRow = (await tBaseCounts).FirstOrDefault();
            int ourCount = GetIntOrZero(countsRow, "se_count") + GetIntOrZero(countsRow, "hist_count");
            int skyCount = GetIntOrZero(countsRow, "sky_count");

            if (ourCount == 0 && skyCount == 0)
                return new { route = routeKey, found = false };

            // Momentum Math
            int firstHalf = GetIntOrZero(countsRow, "se_mid") + GetIntOrZero(countsRow, "sky_mid");
            int secondHalf = (ourCount + skyCount) - firstHalf;
            int momentum = 50;
            if (firstHalf == 0) momentum = secondHalf > 0 ? 100 : 50;
            else momentum = (int)Math.Round(Math.Max(0, Math.Min(100, 50 + ((secondHalf - firstHalf) / (double)firstHalf) * 100)));

            // 2. Blackout Score
            string bEnd = end.Date.AddDays(90).ToString("yyyy-MM-dd");
            p.Add("bStart", end.Date.ToString("yyyy-MM-dd"));
            p.Add("bEnd", bEnd);
            var tBlackout = ExecuteQueryAsync($@"
                SELECT countIf(IsBlackout = 1 AND BlackoutLoad = 'High') AS high_days, countIf(IsBlackout = 1 AND BlackoutLoad != 'High') AS normal_days, count() AS total_days
                FROM {BLACKOUT_TABLE} WHERE aCode = {{origin:String}} AND aCode2 = {{dest:String}} AND CalendarDate >= {{bStart:Date}} AND CalendarDate <= {{bEnd:Date}}", p);

            // 3. All Route Totals (For Route Strength Percentile & Correlation Top 20)
            var tAllSe = ExecuteQueryAsync($"SELECT concat(Origin, '-', Destination) AS route, count() AS c FROM {TABLE} WHERE EventTimeUtc >= {{start:DateTime64(7)}} AND EventTimeUtc <= {{end:DateTime64(7)}} GROUP BY Origin, Destination", p);
            var tAllHist = ExecuteQueryAsync($"SELECT concat(fd.aCode, '-', fd.aCode2) AS route, count() AS c FROM {AGENT_HISTORY_TABLE} h {HISTORY_DEST_JOIN} WHERE EntryDate >= {{start:DateTime64(7)}} AND EntryDate <= {{end:DateTime64(7)}} AND fd.FDestID IS NOT NULL GROUP BY route", p);
            var tAllSky = ExecuteQueryAsync($"SELECT concat(OriginAirport, '-', DestinationAirport) AS route, count() AS c FROM {SKYSCANNER_TABLE} WHERE Timestamp >= {{start:DateTime64(7)}} AND Timestamp <= {{end:DateTime64(7)}} GROUP BY route", p);

            // 4. Area Series (Last 60 Days, 5m buckets)
            string areaStart = end.AddDays(-60).ToString("yyyy-MM-dd HH:mm:ss");
            p.Add("areaStart", areaStart);
            var tAreaSe = ExecuteQueryAsync($"SELECT toStartOfInterval(EventTimeUtc, INTERVAL 5 MINUTE) AS bucket_start, count() AS c FROM {TABLE} WHERE EventTimeUtc >= {{areaStart:DateTime64(7)}} AND EventTimeUtc <= {{end:DateTime64(7)}} AND Origin = {{origin:String}} AND Destination = {{dest:String}} GROUP BY bucket_start", p);
            var tAreaHist = ExecuteQueryAsync($"SELECT toStartOfInterval(EntryDate, INTERVAL 5 MINUTE) AS bucket_start, countIf(IsApiSearch = 1) AS api_count, countIf(IsApiSearch IS NULL OR IsApiSearch = 0) AS portal_count FROM {AGENT_HISTORY_TABLE} h {HISTORY_DEST_JOIN} WHERE EntryDate >= {{areaStart:DateTime64(7)}} AND EntryDate <= {{end:DateTime64(7)}} AND fd.aCode = {{origin:String}} AND fd.aCode2 = {{dest:String}} GROUP BY bucket_start", p);

            // 5. Travel Date Bar Chart
            var tTravelSe = ExecuteQueryAsync($"SELECT toDate(JourneyDate) AS travel_date, count() AS c FROM {TABLE} WHERE EventTimeUtc >= {{start:DateTime64(7)}} AND EventTimeUtc <= {{end:DateTime64(7)}} AND Origin = {{origin:String}} AND Destination = {{dest:String}} GROUP BY travel_date", p);
            var tTravelHist = ExecuteQueryAsync($"SELECT toDate(TravelDate) AS travel_date, countIf(IsApiSearch = 1) AS api_count, countIf(IsApiSearch IS NULL OR IsApiSearch = 0) AS portal_count FROM {AGENT_HISTORY_TABLE} h {HISTORY_DEST_JOIN} WHERE EntryDate >= {{start:DateTime64(7)}} AND EntryDate <= {{end:DateTime64(7)}} AND fd.aCode = {{origin:String}} AND fd.aCode2 = {{dest:String}} AND TravelDate IS NOT NULL GROUP BY travel_date", p);
            var tTravelSky = ExecuteQueryAsync($"SELECT toDate(DepartureDate) AS travel_date, count() AS c FROM {SKYSCANNER_TABLE} WHERE Timestamp >= {{start:DateTime64(7)}} AND Timestamp <= {{end:DateTime64(7)}} AND OriginAirport = {{origin:String}} AND DestinationAirport = {{dest:String}} GROUP BY travel_date", p);

            // 6. Cumulative Line (40 days back)
            string cumStart = end.AddDays(-40).ToString("yyyy-MM-dd HH:mm:ss");
            p.Add("cumStart", cumStart);
            var tCumSe = ExecuteQueryAsync($"SELECT toDate(EventTimeUtc) AS d, count() AS c FROM {TABLE} WHERE EventTimeUtc >= {{cumStart:DateTime64(7)}} AND EventTimeUtc <= {{end:DateTime64(7)}} AND Origin = {{origin:String}} AND Destination = {{dest:String}} GROUP BY d", p);
            var tCumHist = ExecuteQueryAsync($"SELECT toDate(EntryDate) AS d, count() AS c FROM {AGENT_HISTORY_TABLE} h {HISTORY_DEST_JOIN} WHERE EntryDate >= {{cumStart:DateTime64(7)}} AND EntryDate <= {{end:DateTime64(7)}} AND fd.aCode = {{origin:String}} AND fd.aCode2 = {{dest:String}} GROUP BY d", p);

            await Task.WhenAll(tBlackout, tAllSe, tAllHist, tAllSky, tAreaSe, tAreaHist, tTravelSe, tTravelHist, tTravelSky, tCumSe, tCumHist);

            // Process Blackout
            var boRow = tBlackout.Result.FirstOrDefault();
            int bTotal = GetIntOrZero(boRow, "total_days");
            int blackout = bTotal == 0 ? 0 : (int)Math.Round(Math.Min(100.0, (100.0 * GetIntOrZero(boRow, "high_days") + 60.0 * GetIntOrZero(boRow, "normal_days")) / bTotal));
            int demandPressure = (int)Math.Round(0.5 * momentum + 0.5 * blackout);

            // Process Route Strength & Top 20 Correlation Targets
            var allTotals = new Dictionary<string, int>();
            foreach (var r in tAllSe.Result.Concat(tAllHist.Result))
            {
                string rt = GetStringOrNull(r, "route") ?? "";
                if (!allTotals.ContainsKey(rt)) allTotals[rt] = 0;
                allTotals[rt] += GetIntOrZero(r, "c");
            }
            var ourTotals = new Dictionary<string, int>(allTotals); // Copy for top 20
            foreach (var r in tAllSky.Result)
            {
                string rt = GetStringOrNull(r, "route") ?? "";
                if (!allTotals.ContainsKey(rt)) allTotals[rt] = 0;
                allTotals[rt] += GetIntOrZero(r, "c");
            }

            int targetVol = allTotals.ContainsKey(routeKey) ? allTotals[routeKey] : 0;
            int countLe = allTotals.Values.Count(v => v <= targetVol);
            int routeStrength = allTotals.Count == 0 ? 0 : (int)Math.Round(100.0 * countLe / allTotals.Count);

            // Process Area Series
            var areaBuckets = new Dictionary<string, (int api, int portal)>();
            foreach (var r in tAreaSe.Result)
            {
                string b = GetStringOrNull(r, "bucket_start") ?? "";
                if (!areaBuckets.ContainsKey(b)) areaBuckets[b] = (0, 0);
                areaBuckets[b] = (areaBuckets[b].api + GetIntOrZero(r, "c"), areaBuckets[b].portal);
            }
            foreach (var r in tAreaHist.Result)
            {
                string b = GetStringOrNull(r, "bucket_start") ?? "";
                if (!areaBuckets.ContainsKey(b)) areaBuckets[b] = (0, 0);
                areaBuckets[b] = (areaBuckets[b].api + GetIntOrZero(r, "api_count"), areaBuckets[b].portal + GetIntOrZero(r, "portal_count"));
            }
            var areaSeries = areaBuckets.OrderBy(x => x.Key).Select(x => new {
                bucket_start = x.Key,
                search_count = x.Value.api + x.Value.portal,
                api_count = x.Value.api,
                portal_count = x.Value.portal
            }).ToList();

            // Process Travel Date Bar
            var travelDates = new Dictionary<string, (int api, int portal, int sky)>();
            foreach (var r in tTravelSe.Result)
            {
                string d = GetStringOrNull(r, "travel_date") ?? "";
                if (!travelDates.ContainsKey(d)) travelDates[d] = (0, 0, 0);
                travelDates[d] = (travelDates[d].api + GetIntOrZero(r, "c"), travelDates[d].portal, travelDates[d].sky);
            }
            foreach (var r in tTravelHist.Result)
            {
                string d = GetStringOrNull(r, "travel_date") ?? "";
                if (!travelDates.ContainsKey(d)) travelDates[d] = (0, 0, 0);
                travelDates[d] = (travelDates[d].api + GetIntOrZero(r, "api_count"), travelDates[d].portal + GetIntOrZero(r, "portal_count"), travelDates[d].sky);
            }
            foreach (var r in tTravelSky.Result)
            {
                string d = GetStringOrNull(r, "travel_date") ?? "";
                if (!travelDates.ContainsKey(d)) travelDates[d] = (0, 0, 0);
                travelDates[d] = (travelDates[d].api, travelDates[d].portal, travelDates[d].sky + GetIntOrZero(r, "c"));
            }
            var travelDateBar = travelDates.OrderBy(x => x.Key).Select(x => new {
                travel_date = x.Key,
                api_count = x.Value.api,
                portal_count = x.Value.portal,
                skyscanner_count = x.Value.sky
            }).ToList();

            // Process Cumulative Line
            var dailyCounts = new Dictionary<string, int>();
            foreach (var r in tCumSe.Result.Concat(tCumHist.Result))
            {
                string d = GetStringOrNull(r, "d") ?? "";
                if (!dailyCounts.ContainsKey(d)) dailyCounts[d] = 0;
                dailyCounts[d] += GetIntOrZero(r, "c");
            }
            var cumulativeLine = new List<object>();
            var loopDate = end.Date.AddDays(-30);
            while (loopDate <= end.Date)
            {
                var w10 = Enumerable.Range(0, 10).Select(i => dailyCounts.ContainsKey(loopDate.AddDays(-i).ToString("yyyy-MM-dd")) ? dailyCounts[loopDate.AddDays(-i).ToString("yyyy-MM-dd")] : 0).ToList();
                var w3 = Enumerable.Range(0, 3).Select(i => dailyCounts.ContainsKey(loopDate.AddDays(-i).ToString("yyyy-MM-dd")) ? dailyCounts[loopDate.AddDays(-i).ToString("yyyy-MM-dd")] : 0).ToList();
                cumulativeLine.Add(new
                {
                    date = loopDate.ToString("yyyy-MM-dd"),
                    avg_10_day = w10.Any() ? Math.Round(w10.Average(), 2) : 0,
                    avg_3_day = w3.Any() ? Math.Round(w3.Average(), 2) : 0
                });
                loopDate = loopDate.AddDays(1);
            }

            // CORRELATION QUERY (Top 20 + Route)
            var top20Routes = ourTotals.OrderByDescending(x => x.Value).Take(20).Select(x => x.Key).ToList();
            if (!top20Routes.Contains(routeKey)) top20Routes.Add(routeKey);

            string corrIn = string.Join(",", top20Routes.Select(r => $"'{r}'"));
            var tCorrSe = ExecuteQueryAsync($"SELECT toStartOfInterval(EventTimeUtc, INTERVAL 1 HOUR) AS bucket_start, concat(Origin, '-', Destination) AS route, count() AS c FROM {TABLE} WHERE EventTimeUtc >= {{start:DateTime64(7)}} AND EventTimeUtc <= {{end:DateTime64(7)}} AND concat(Origin, '-', Destination) IN ({corrIn}) GROUP BY bucket_start, route", p);
            var tCorrHist = ExecuteQueryAsync($"SELECT toStartOfInterval(EntryDate, INTERVAL 1 HOUR) AS bucket_start, concat(fd.aCode, '-', fd.aCode2) AS route, count() AS c FROM {AGENT_HISTORY_TABLE} h {HISTORY_DEST_JOIN} WHERE EntryDate >= {{start:DateTime64(7)}} AND EntryDate <= {{end:DateTime64(7)}} AND concat(fd.aCode, '-', fd.aCode2) IN ({corrIn}) GROUP BY bucket_start, route", p);

            await Task.WhenAll(tCorrSe, tCorrHist);

            var corrCounts = new Dictionary<string, int>();
            foreach (var r in tCorrSe.Result.Concat(tCorrHist.Result))
            {
                string key = $"{GetStringOrNull(r, "bucket_start")}|{GetStringOrNull(r, "route")}";
                if (!corrCounts.ContainsKey(key)) corrCounts[key] = 0;
                corrCounts[key] += GetIntOrZero(r, "c");
            }
            var correlationSeries = corrCounts.OrderBy(x => x.Key).Select(x => new {
                bucket_start = x.Key.Split('|')[0],
                route = x.Key.Split('|')[1],
                search_count = x.Value
            }).ToList();

            return new
            {
                route = routeKey,
                found = true,
                filters = new { from_date = start.ToString("o"), to_date = end.ToString("o") },
                gauges = new { route_strength = routeStrength, demand_pressure = demandPressure, demand_pressure_components = new { momentum, blackout_load = blackout } },
                area_series = areaSeries,
                correlation = new { routes = top20Routes, series = correlationSeries },
                travel_date_bar = travelDateBar,
                cumulative_line = cumulativeLine
            };
        }

        public async Task<object> GetSectorPressureRankingAsync(DateTime start, DateTime end)
        {
            string sStart = start.ToString("yyyy-MM-dd HH:mm:ss");
            string sEnd = end.ToString("yyyy-MM-dd HH:mm:ss");
            var p = new Dictionary<string, string> { { "start", sStart }, { "end", sEnd } };

            var tAllSe = ExecuteQueryAsync($"SELECT concat(Origin, '-', Destination) AS route, count() AS c FROM {TABLE} WHERE EventTimeUtc >= {{start:DateTime64(7)}} AND EventTimeUtc <= {{end:DateTime64(7)}} GROUP BY Origin, Destination", p);
            var tAllHist = ExecuteQueryAsync($"SELECT concat(fd.aCode, '-', fd.aCode2) AS route, count() AS c FROM {AGENT_HISTORY_TABLE} h {HISTORY_DEST_JOIN} WHERE EntryDate >= {{start:DateTime64(7)}} AND EntryDate <= {{end:DateTime64(7)}} AND fd.FDestID IS NOT NULL GROUP BY route", p);
            await Task.WhenAll(tAllSe, tAllHist);

            var ourTotals = new Dictionary<string, int>();
            foreach (var r in tAllSe.Result.Concat(tAllHist.Result))
            {
                string rt = GetStringOrNull(r, "route") ?? "";
                if (!ourTotals.ContainsKey(rt)) ourTotals[rt] = 0;
                ourTotals[rt] += GetIntOrZero(r, "c");
            }

            var top20Routes = ourTotals.OrderByDescending(x => x.Value).Take(20).Select(x => x.Key).ToList();
            if (!top20Routes.Any()) return new { ranking = new List<object>() };

            DateTime latestStart = end.AddMinutes(-30);
            DateTime prevStart = end.AddMinutes(-60);
            string routeIn = string.Join(",", top20Routes.Select(r => $"'{r}'"));

            var pWin = new Dictionary<string, string> {
                { "LStart", latestStart.ToString("yyyy-MM-dd HH:mm:ss") }, { "LEnd", sEnd },
                { "PStart", prevStart.ToString("yyyy-MM-dd HH:mm:ss") }, { "PEnd", latestStart.ToString("yyyy-MM-dd HH:mm:ss") }
            };

            var tLSe = ExecuteQueryAsync($"SELECT concat(Origin, '-', Destination) AS route, count() AS c FROM {TABLE} WHERE EventTimeUtc >= {{LStart:DateTime64(7)}} AND EventTimeUtc <= {{LEnd:DateTime64(7)}} AND concat(Origin, '-', Destination) IN ({routeIn}) GROUP BY Origin, Destination", pWin);
            var tLHist = ExecuteQueryAsync($"SELECT concat(fd.aCode, '-', fd.aCode2) AS route, count() AS c FROM {AGENT_HISTORY_TABLE} h {HISTORY_DEST_JOIN} WHERE EntryDate >= {{LStart:DateTime64(7)}} AND EntryDate <= {{LEnd:DateTime64(7)}} AND concat(fd.aCode, '-', fd.aCode2) IN ({routeIn}) GROUP BY route", pWin);
            var tPSe = ExecuteQueryAsync($"SELECT concat(Origin, '-', Destination) AS route, count() AS c FROM {TABLE} WHERE EventTimeUtc >= {{PStart:DateTime64(7)}} AND EventTimeUtc <= {{PEnd:DateTime64(7)}} AND concat(Origin, '-', Destination) IN ({routeIn}) GROUP BY Origin, Destination", pWin);
            var tPHist = ExecuteQueryAsync($"SELECT concat(fd.aCode, '-', fd.aCode2) AS route, count() AS c FROM {AGENT_HISTORY_TABLE} h {HISTORY_DEST_JOIN} WHERE EntryDate >= {{PStart:DateTime64(7)}} AND EntryDate <= {{PEnd:DateTime64(7)}} AND concat(fd.aCode, '-', fd.aCode2) IN ({routeIn}) GROUP BY route", pWin);

            await Task.WhenAll(tLSe, tLHist, tPSe, tPHist);

            var latestTotals = new Dictionary<string, int>();
            foreach (var r in tLSe.Result.Concat(tLHist.Result)) { string rt = GetStringOrNull(r, "route") ?? ""; if (!latestTotals.ContainsKey(rt)) latestTotals[rt] = 0; latestTotals[rt] += GetIntOrZero(r, "c"); }

            var prevTotals = new Dictionary<string, int>();
            foreach (var r in tPSe.Result.Concat(tPHist.Result)) { string rt = GetStringOrNull(r, "route") ?? ""; if (!prevTotals.ContainsKey(rt)) prevTotals[rt] = 0; prevTotals[rt] += GetIntOrZero(r, "c"); }

            var rankPrev = top20Routes.OrderByDescending(r => prevTotals.ContainsKey(r) ? prevTotals[r] : 0).ThenBy(r => r).ToList();
            var prevPosition = rankPrev.Select((r, i) => new { r, i }).ToDictionary(x => x.r, x => x.i);

            var rankNow = top20Routes.OrderByDescending(r => latestTotals.ContainsKey(r) ? latestTotals[r] : 0).ThenBy(r => r).ToList();

            var ranking = rankNow.Select((route, index) => new {
                rank = index + 1,
                route = route,
                latest_window_count = latestTotals.ContainsKey(route) ? latestTotals[route] : 0,
                previous_window_count = prevTotals.ContainsKey(route) ? prevTotals[route] : 0,
                total_count = ourTotals.ContainsKey(route) ? ourTotals[route] : 0,
                rank_delta = prevPosition[route] - index
            }).ToList();

            return new { ranking = ranking };
        }

        public async Task<object> GetSectorTravelDatePressureAsync(string origin, string destination, DateTime start, DateTime end, DateTime calendarStart)
        {
            DateTime calendarEnd = calendarStart.AddDays(29);
            DateTime fetchStart = calendarStart.AddDays(-40);
            string routeKey = $"{origin}-{destination}";

            var p = new Dictionary<string, string> {
                { "start", start.ToString("yyyy-MM-dd HH:mm:ss") }, { "end", end.ToString("yyyy-MM-dd HH:mm:ss") },
                { "origin", origin }, { "dest", destination },
                { "jStart", fetchStart.ToString("yyyy-MM-dd HH:mm:ss") }, { "jEnd", calendarEnd.ToString("yyyy-MM-dd HH:mm:ss") },
                { "cStart", calendarStart.ToString("yyyy-MM-dd") }, { "cEnd", calendarEnd.ToString("yyyy-MM-dd") }
            };

            var tSe = ExecuteQueryAsync($"SELECT toDate(JourneyDate) AS travel_date, count() AS c FROM {TABLE} WHERE EventTimeUtc >= {{start:DateTime64(7)}} AND EventTimeUtc <= {{end:DateTime64(7)}} AND Origin = {{origin:String}} AND Destination = {{dest:String}} AND JourneyDate >= {{jStart:DateTime64(7)}} AND JourneyDate <= {{jEnd:DateTime64(7)}} GROUP BY travel_date", p);
            var tHist = ExecuteQueryAsync($"SELECT toDate(TravelDate) AS travel_date, count() AS c FROM {AGENT_HISTORY_TABLE} h {HISTORY_DEST_JOIN} WHERE EntryDate >= {{start:DateTime64(7)}} AND EntryDate <= {{end:DateTime64(7)}} AND fd.aCode = {{origin:String}} AND fd.aCode2 = {{dest:String}} AND TravelDate >= {{jStart:DateTime64(7)}} AND TravelDate <= {{jEnd:DateTime64(7)}} GROUP BY travel_date", p);
            var tBlackout = ExecuteQueryAsync($"SELECT CalendarDate AS d, IsBlackout AS is_blackout, BlackoutLoad AS load FROM {BLACKOUT_TABLE} WHERE aCode = {{origin:String}} AND aCode2 = {{dest:String}} AND CalendarDate >= {{cStart:Date}} AND CalendarDate <= {{cEnd:Date}}", p);

            await Task.WhenAll(tSe, tHist, tBlackout);

            var countsByDate = new Dictionary<string, int>();
            foreach (var r in tSe.Result.Concat(tHist.Result))
            {
                string d = GetStringOrNull(r, "travel_date") ?? "";
                if (!countsByDate.ContainsKey(d)) countsByDate[d] = 0;
                countsByDate[d] += GetIntOrZero(r, "c");
            }

            var blackoutByDate = new Dictionary<string, string>();
            foreach (var r in tBlackout.Result)
            {
                string d = GetStringOrNull(r, "d") ?? "";
                bool isB = GetIntOrZero(r, "is_blackout") == 1;
                blackoutByDate[d] = isB ? (GetStringOrNull(r, "load") ?? "Normal") : "Normal";
            }

            var days = new List<object>();
            var loopDate = calendarStart;
            while (loopDate <= calendarEnd)
            {
                string ldStr = loopDate.ToString("yyyy-MM-dd");
                int todaySearch = countsByDate.ContainsKey(ldStr) ? countsByDate[ldStr] : 0;

                var l5d = Enumerable.Range(1, 5).Select(i => countsByDate.ContainsKey(loopDate.AddDays(-i).ToString("yyyy-MM-dd")) ? countsByDate[loopDate.AddDays(-i).ToString("yyyy-MM-dd")] : 0).ToList();
                double l5dAvg = Math.Round(l5d.Average(), 2);

                var l5w = Enumerable.Range(1, 5).Select(i => countsByDate.ContainsKey(loopDate.AddDays(-7 * i).ToString("yyyy-MM-dd")) ? countsByDate[loopDate.AddDays(-7 * i).ToString("yyyy-MM-dd")] : 0).ToList();
                double l5wAvg = Math.Round(l5w.Average(), 2);

                double spike = l5dAvg > 0 ? Math.Round((todaySearch - l5dAvg) * 100 / l5dAvg, 2) : (todaySearch > 0 ? 100.0 : 0.0);

                days.Add(new
                {
                    date = ldStr,
                    search_count = todaySearch,
                    today_search = todaySearch,
                    last_5_day_avg = l5dAvg,
                    last_5_weekday_avg = l5wAvg,
                    spike_percentage = spike,
                    blackout_status = blackoutByDate.ContainsKey(ldStr) ? blackoutByDate[ldStr] : "Normal"
                });
                loopDate = loopDate.AddDays(1);
            }

            var allCounts = days.Select(d => (int)((dynamic)d).search_count).ToList();
            return new
            {
                route = routeKey,
                calendar_start = calendarStart.ToString("yyyy-MM-dd"),
                calendar_end = calendarEnd.ToString("yyyy-MM-dd"),
                color_scale = new { min = allCounts.Any() ? allCounts.Min() : 0, max = allCounts.Any() ? allCounts.Max() : 0 },
                days = days
            };
        }

        // =========================================================
        // SAFE JSON PARSING UTILITIES & FORMATTERS
        // =========================================================
        private int GetIntOrZero(JsonElement element, string prop)
        {
            if (element.ValueKind == JsonValueKind.Undefined || element.ValueKind == JsonValueKind.Null) return 0;
            if (element.TryGetProperty(prop, out var val))
            {
                if (val.ValueKind == JsonValueKind.Number) return val.GetInt32();
                if (val.ValueKind == JsonValueKind.String && int.TryParse(val.GetString(), out int p)) return p;
            }
            return 0;
        }

        private double? GetDoubleOrNull(JsonElement element, string prop)
        {
            if (element.ValueKind == JsonValueKind.Undefined || element.ValueKind == JsonValueKind.Null) return null;
            if (element.TryGetProperty(prop, out var val) && val.ValueKind != JsonValueKind.Null)
            {
                if (val.ValueKind == JsonValueKind.Number) return val.GetDouble();
                if (val.ValueKind == JsonValueKind.String && double.TryParse(val.GetString(), out double p)) return p;
            }
            return null;
        }

        private string? GetStringOrNull(JsonElement element, string prop)
        {
            if (element.ValueKind == JsonValueKind.Undefined || element.ValueKind == JsonValueKind.Null) return null;
            if (element.TryGetProperty(prop, out var val) && val.ValueKind != JsonValueKind.Null)
                return val.GetString();
            return null;
        }

        private double CalcPct(int count, int total) => total == 0 ? 0 : Math.Round(count * 100.0 / total, 2);

        private object GenerateEmptyAgentResponse(int accountId, DateTime start, DateTime end, int thresholdMs, string? origin, string? destination, string journeyType, int intervalMinutes)
        {
            return new
            {
                account_id = accountId,
                filters = new { from_date = start.ToString("o"), to_date = end.ToString("o"), threshold_ms = thresholdMs, origin = origin, destination = destination, type = journeyType },
                data_availability = new { data_start_datetime = (string?)null, data_end_datetime = (string?)null, has_data = false },
                chart_aggregation = new { interval_minutes = intervalMinutes, interval = intervalMinutes == 1 ? "1 minute" : "5 minutes" },
                kpis = new { total_search_count = 0, api_search_count = 0, portal_search_count = 0, max_response_time_ms = (double?)null, max_response_time_event_time = (string?)null, successful_search_count = 0, success_percentage = 0.0, p50_response_time_ms = (double?)null, p99_response_time_ms = (double?)null, max_searched_route = (string?)null, threshold_exceeded_count = 0 },
                response_time_distribution = new List<object>(),
                response_time_series = new List<object>(),
                search_count_series = new List<object>(),
                departure_date_series = new List<object>()
            };
        }

        private object FormatAgentResponse(int accountId, DateTime start, DateTime end, int thresholdMs, string? origin, string? destination, string journeyType, int intervalMinutes, JsonElement availability, JsonElement kpiRow, JsonElement maxRouteRow, JsonElement distRow)
        {
            string? dataStart = GetStringOrNull(availability, "data_start_datetime");
            string? dataEnd = GetStringOrNull(availability, "data_end_datetime");
            int totalSearchCount = GetIntOrZero(kpiRow, "total_search_count");

            var distribution = new List<object>
            {
                new { bucket = "<1s", count = GetIntOrZero(distRow, "under_1s"), percentage = CalcPct(GetIntOrZero(distRow, "under_1s"), totalSearchCount) },
                new { bucket = "1-2s", count = GetIntOrZero(distRow, "between_1_2s"), percentage = CalcPct(GetIntOrZero(distRow, "between_1_2s"), totalSearchCount) },
                new { bucket = "2-3s", count = GetIntOrZero(distRow, "between_2_3s"), percentage = CalcPct(GetIntOrZero(distRow, "between_2_3s"), totalSearchCount) },
                new { bucket = "3-4s", count = GetIntOrZero(distRow, "between_3_4s"), percentage = CalcPct(GetIntOrZero(distRow, "between_3_4s"), totalSearchCount) },
                new { bucket = "4-5s", count = GetIntOrZero(distRow, "between_4_5s"), percentage = CalcPct(GetIntOrZero(distRow, "between_4_5s"), totalSearchCount) },
                new { bucket = "5+s", count = GetIntOrZero(distRow, "over_5s"), percentage = CalcPct(GetIntOrZero(distRow, "over_5s"), totalSearchCount) }
            };

            return new
            {
                account_id = accountId,
                filters = new { from_date = start.ToString("o"), to_date = end.ToString("o"), threshold_ms = thresholdMs, origin = origin, destination = destination, type = journeyType },
                data_availability = new { data_start_datetime = dataStart, data_end_datetime = dataEnd, has_data = true },
                chart_aggregation = new { interval_minutes = intervalMinutes, interval = intervalMinutes == 1 ? "1 minute" : "5 minutes" },
                kpis = new { total_search_count = totalSearchCount, api_search_count = totalSearchCount, portal_search_count = 0, max_response_time_ms = GetDoubleOrNull(kpiRow, "max_response_time_ms"), max_response_time_event_time = GetStringOrNull(kpiRow, "max_response_time_event_time"), successful_search_count = GetIntOrZero(kpiRow, "successful_search_count"), success_percentage = GetDoubleOrNull(kpiRow, "success_percentage") ?? 0, p50_response_time_ms = GetDoubleOrNull(kpiRow, "p50_response_time_ms"), p99_response_time_ms = GetDoubleOrNull(kpiRow, "p99_response_time_ms"), max_searched_route = GetStringOrNull(maxRouteRow, "route"), threshold_exceeded_count = GetIntOrZero(kpiRow, "threshold_exceeded_count") },
                response_time_distribution = distribution,
                response_time_series = new List<object>(),
                search_count_series = new List<object>(),
                departure_date_series = new List<object>()
            };
        }
    }

    public class AgentDto
    {
        public int AccountId { get; set; }
        public string? AccountName { get; set; }
    }
}