//using inventory_analytics_tool.Workers;
//using Microsoft.AspNetCore.Authorization;
//using Microsoft.AspNetCore.Mvc;
//using Microsoft.Extensions.Caching.Memory;
//using System.Text.Json;
//using System.Linq;

//namespace inventory_analytics_tool.Controllers
//{
//    [Authorize(Roles = "Admin")]
//    public class DemandController : Controller
//    {
//        private readonly IMemoryCache _cache;
//        private readonly ILogger<DemandController> _logger;

//        public DemandController(IMemoryCache cache, ILogger<DemandController> logger)
//        {
//            _cache = cache;
//            _logger = logger;
//        }

//        public IActionResult Index()
//        {
//            return View();
//        }

//        [HttpGet]
//        public IActionResult GetDemandData()
//        {
//            try
//            {
//                if (_cache.TryGetValue("DemandCalculatorData", out List<DemandCalculatorRow> demandData) && demandData != null)
//                {
//                    // SAFE SORTING: ScoreToday is an object (can be double or "NA"). 
//                    // Direct sorting crashes C#. We safely parse it, pushing "NA" (-1) to the bottom.
//                    var safeBrowserData = demandData
//                        .OrderByDescending(d => d.ScoreToday is double score ? score : -1)
//                        .Take(15000) // Maximum safe limit for a web browser before Chrome crashes
//                        .ToList();

//                    var jsonOptions = new JsonSerializerOptions
//                    {
//                        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
//                    };

//                    return Json(new { success = true, data = safeBrowserData }, jsonOptions);
//                }

//                return Json(new { success = false, message = "Demand data is currently being calculated. Please check back in a few minutes." });
//            }
//            catch (Exception ex)
//            {
//                _logger.LogError($"DemandController Error: {ex.Message}");
//                // If it fails, send the real error to the UI instead of the default loading message
//                return Json(new { success = false, message = $"API Error: {ex.Message}" });
//            }
//        }
//    }
//}
using inventory_analytics_tool.Workers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using System.Text.Json;

namespace inventory_analytics_tool.Controllers
{
    [Authorize(Roles = "Admin")]
    public class DemandController : Controller
    {
        private readonly IMemoryCache _cache;
        private readonly ILogger<DemandController> _logger;

        public DemandController(IMemoryCache cache, ILogger<DemandController> logger)
        {
            _cache = cache;
            _logger = logger;
        }

        public IActionResult Index()
        {
            return View();
        }

        [HttpGet]
        public IActionResult GetDemandData(DateTime? startDate, DateTime? endDate)
        {
            try
            {
                if (_cache.TryGetValue("DemandCalculatorData", out List<DemandCalculatorRow> demandData) && demandData != null)
                {
                    var query = demandData.AsEnumerable();

                    // MANAGER REQ 4: Slicing the massive dataset based on UI input
                    if (startDate.HasValue)
                    {
                        query = query.Where(d => DateTime.TryParse(d.TravelDate, out DateTime td) && td.Date >= startDate.Value.Date);
                    }
                    if (endDate.HasValue)
                    {
                        query = query.Where(d => DateTime.TryParse(d.TravelDate, out DateTime td) && td.Date <= endDate.Value.Date);
                    }

                    var safeBrowserData = query
                        .OrderByDescending(d => d.ScoreToday is double score ? score : -1)
                        .ToList();

                    var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
                    return Json(new { success = true, data = safeBrowserData }, jsonOptions);
                }

                return Json(new { success = false, message = "Demand data is currently being calculated. Please check back in a few minutes." });
            }
            catch (Exception ex)
            {
                _logger.LogError($"DemandController Error: {ex.Message}");
                return Json(new { success = false, message = $"API Error: {ex.Message}" });
            }
        }
    }
}