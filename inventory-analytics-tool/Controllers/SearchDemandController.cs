using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using inventory_analytics_tool.Services;

namespace inventory_analytics_tool.Controllers
{
    [Authorize(Roles = "Admin")]
    [Route("SearchPulse")]
    public class SearchDemandController : Controller
    {
        private readonly ISearchPulseService _analyticsService;
        private readonly ILogger<SearchDemandController> _logger;

        public SearchDemandController(ISearchPulseService analyticsService, ILogger<SearchDemandController> logger)
        {
            _analyticsService = analyticsService;
            _logger = logger;
        }

        // --- RAZOR VIEWS ---

        [HttpGet("")]
        [HttpGet("Overview")]
        public IActionResult Overview() => View();

        [HttpGet("Agents")]
        public IActionResult AgentAnalytics() => View();

        [HttpGet("Routes")]
        public IActionResult RouteAnalytics() => View();

        // --- AJAX DATA ENDPOINTS (Replaces Django APIs) ---


        [HttpGet("api/overview")]
        public async Task<IActionResult> GetOverviewAnalytics(string? route, DateTime? from_date, DateTime? to_date, string journeyType = "all")
        {
            var end = to_date ?? DateTime.UtcNow;
            var start = from_date ?? end.AddDays(-2);

            try
            {
                var data = await _analyticsService.GetOverviewAnalyticsAsync(route, start, end, journeyType);
                return Json(data);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch overview analytics");
                return StatusCode(502, new { detail = "Unable to read overview analytics." });
            }
        }

        [HttpGet("api/route-contribution")]
        public async Task<IActionResult> GetRouteContribution(string date, string source, string journeyType = "all")
        {
            if (string.IsNullOrEmpty(date)) return BadRequest(new { detail = "date is required." });
            if (source != "ours" && source != "skyscanner") return BadRequest(new { detail = "source must be ours or skyscanner." });

            try
            {
                var data = await _analyticsService.GetRouteContributionAsync(date, source, journeyType);
                return Json(data);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch route contribution");
                return StatusCode(502, new { detail = "Unable to read route contribution." });
            }
        }

        [HttpGet("api/agents")]
        public async Task<IActionResult> GetAgentList()
        {
            try
            {
                var agents = await _analyticsService.GetAgentListAsync();
                return Json(new { accounts = agents, all_client_ids = agents.Select(a => a.AccountId) });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch agent list");
                return StatusCode(500, new { detail = "Failed to execute ClickHouse query" });
            }
        }

        [HttpGet("api/agents/{agentId}/kpis")]
        public async Task<IActionResult> GetAgentKpis(int agentId, DateTime? from_date, DateTime? to_date, int threshold = 3000, string? origin = null, string? destination = null, string journey_type = "All")
        {
            // Replicates AgentResponseAPIView parsing and validation
            var end = to_date ?? DateTime.UtcNow;
            var start = from_date ?? end.AddDays(-2);

            if (start >= end) return BadRequest(new { detail = "from_date must be earlier than to_date" });
            if ((end - start).TotalDays > 7) return BadRequest(new { detail = "Maximum allowed date range is 7 days" });
            if (threshold < 0) return BadRequest(new { detail = "threshold cannot be negative" });

            try
            {
                var data = await _analyticsService.GetAgentAnalyticsAsync(agentId, start, end, threshold, origin, destination, journey_type);
                return Json(data);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to fetch KPIs for agent {agentId}");
                return StatusCode(502, new { detail = "Unable to compute agent analytics." });
            }
        }

        // --- ADD THESE TO SearchDemandController.cs ---

        [HttpGet("api/agents/{agentId}/analytics")]
        public async Task<IActionResult> GetAgentAnalytics(int agentId, DateTime? from_date, DateTime? to_date, int threshold = 3000, string? origin = null, string? destination = null, string journeyType = "all")
        {
            var end = to_date ?? DateTime.UtcNow;
            var start = from_date ?? end.AddDays(-2);

            if (start >= end) return BadRequest(new { detail = "from_date must be earlier than to_date" });
            if ((end - start).TotalDays > 7) return BadRequest(new { detail = "Maximum allowed date range is 7 days" });
            if (threshold < 0) return BadRequest(new { detail = "threshold cannot be negative" });

            try
            {
                var data = await _analyticsService.GetAgentAnalyticsAsync(agentId, start, end, threshold, origin, destination, journeyType);
                return Json(data);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to fetch analytics for agent {agentId}");
                return StatusCode(502, new { detail = "Unable to compute agent analytics." });
            }
        }

        [HttpGet("api/agents/{agentId}/skyscanner-match")]
        public async Task<IActionResult> GetAgentSkyscannerMatch(int agentId, DateTime? from_date, DateTime? to_date)
        {
            var end = to_date ?? DateTime.UtcNow;
            var start = from_date ?? end.AddDays(-2);

            try
            {
                var data = await _analyticsService.GetAgentSkyscannerMatchAsync(agentId, start, end);
                return Json(data);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to fetch Skyscanner match for agent {agentId}");
                return StatusCode(502, new { detail = "Unable to compute Skyscanner match data." });
            }
        }

        [HttpGet("api/agents/{agentId}/skyscanner-match/detail")]
        public async Task<IActionResult> GetAgentSkyscannerMatchDetail(int agentId, DateTime bucket_start, int bucket_minutes)
        {
            try
            {
                var data = await _analyticsService.GetAgentSkyscannerMatchDetailAsync(agentId, bucket_start, bucket_minutes);
                return Json(data);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to fetch Skyscanner match detail for agent {agentId}");
                return StatusCode(502, new { detail = "Unable to load Skyscanner match detail." });
            }
        }

        [HttpGet("api/agents/top-search-count")]
        public async Task<IActionResult> GetTopAgentSearchCount(DateTime? from_date, DateTime? to_date, string? origin = null, string? destination = null, DateTime? journey_from = null, DateTime? journey_to = null)
        {
            if (from_date == null || to_date == null) return BadRequest(new { detail = "from_date and to_date are required." });
            if (from_date >= to_date) return BadRequest(new { detail = "from_date must be earlier than to_date." });
            if ((to_date.Value - from_date.Value).TotalDays > 10) return BadRequest(new { detail = "Maximum allowed date range is 10 days." });

            try
            {
                var data = await _analyticsService.GetTopAgentSearchCountAsync(from_date.Value, to_date.Value, origin, destination, journey_from, journey_to);
                return Json(data);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch top agent search count");
                return StatusCode(502, new { detail = "Unable to load top agent search counts." });
            }
        }

        // --- ADD THESE TO SearchDemandController.cs (Inside the class) ---

        [HttpGet("api/routes/analytics")]
        public async Task<IActionResult> GetRouteAnalytics(string origin, string destination, DateTime? from_date, DateTime? to_date)
        {
            if (string.IsNullOrEmpty(origin) || string.IsNullOrEmpty(destination))
                return BadRequest(new { detail = "route is required, e.g. CCU-IXB." });

            var end = to_date ?? DateTime.UtcNow;
            var start = from_date ?? end.AddDays(-2);

            if (start >= end) return BadRequest(new { detail = "from_date must be earlier than to_date." });
            if ((end - start).TotalDays > 7) return BadRequest(new { detail = "Maximum allowed date range is 7 days." });

            try
            {
                var data = await _analyticsService.GetRouteAnalyticsAsync(origin, destination, start, end);
                return Json(data);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to fetch route analytics for {origin}-{destination}");
                return StatusCode(502, new { detail = "Unable to read route analytics." });
            }
        }

        [HttpGet("api/routes/sector-pressure-ranking")]
        public async Task<IActionResult> GetSectorPressureRanking(DateTime? from_date, DateTime? to_date)
        {
            var end = to_date ?? DateTime.UtcNow;
            var start = from_date ?? end.AddDays(-2);

            try
            {
                var data = await _analyticsService.GetSectorPressureRankingAsync(start, end);
                return Json(data);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to fetch sector pressure ranking");
                return StatusCode(502, new { detail = "Unable to read sector pressure ranking." });
            }
        }

        [HttpGet("api/routes/sector-travel-date-pressure")]
        public async Task<IActionResult> GetSectorTravelDatePressure(string origin, string destination, DateTime? from_date, DateTime? to_date, DateTime? calendar_start)
        {
            if (string.IsNullOrEmpty(origin) || string.IsNullOrEmpty(destination))
                return BadRequest(new { detail = "route is required, e.g. CCU-IXB." });

            var end = to_date ?? DateTime.UtcNow;
            var start = from_date ?? end.AddDays(-2);
            var calStart = calendar_start ?? end.Date;

            try
            {
                var data = await _analyticsService.GetSectorTravelDatePressureAsync(origin, destination, start, end, calStart);
                return Json(data);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to fetch sector travel date pressure for {origin}-{destination}");
                return StatusCode(502, new { detail = "Unable to read sector travel-date pressure." });
            }
        }

        [HttpGet("api/agents/download")]
        public async Task<IActionResult> DownloadAgentLogs(int? account_id, DateTime from_date, DateTime to_date, string? origin, string? destination)
        {
            if (from_date > to_date) return BadRequest("from_date must be earlier than to_date.");

            // To replace Django's openpyxl, use ClosedXML or MiniExcel here.
            // MiniExcel.SaveAs(memoryStream, _analyticsService.GetRawLogsStream(account_id, ...))
            // return File(memoryStream, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"search-log-{account_id ?? 0}.xlsx");
            throw new NotImplementedException("Excel export to be implemented in Phase 5 via ClosedXML/MiniExcel");
        }
    }
}