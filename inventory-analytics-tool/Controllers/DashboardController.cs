//using inventory_analytics_tool.Models;
//using Microsoft.AspNetCore.Authorization;
//using Microsoft.AspNetCore.Mvc;
//using Microsoft.Extensions.Caching.Memory;

//namespace inventory_analytics_tool.Controllers;

//[Authorize]
//public class DashboardController : Controller
//{
//    private readonly ILogger<DashboardController> _logger;
//    private readonly IMemoryCache _cache;

//    public DashboardController(ILogger<DashboardController> logger, IMemoryCache cache)
//    {
//        _logger = logger;
//        _cache = cache;
//    }

//    public IActionResult Index()
//    {
//        var upcomingEvents = new List<DashboardUiEvent>();

//        // Always use Primary Phase 1 cache for initial dashboard load
//        if (_cache.TryGetValue("DashboardMasterData", out List<DashboardUiEvent> dashEvents) && dashEvents != null)
//        {
//            var today = DateTime.Today;
//            upcomingEvents = dashEvents
//                .Where(e => DateTime.TryParse(e.EventDate, out DateTime parsedDate) && parsedDate.Date >= today)
//                .OrderBy(e => DateTime.Parse(e.EventDate))
//                .ToList();
//        }

//        return View(upcomingEvents);
//    }

//    [HttpGet]
//    public IActionResult GetDashboardData(int year = 2026, bool includePast = false)
//    {
//        // ====================================================================
//        // THE SMART SWITCH: Pick cache dynamically based on UI params!
//        // ====================================================================
//        bool usePrimaryCache = (year == 2026 && !includePast);
//        string targetDashKey = usePrimaryCache ? "DashboardMasterData" : "HistoricalDashboardMasterData";
//        string targetRawKey = usePrimaryCache ? "RawEventListData" : "HistoricalRawEventListData";

//        // Pull from the requested cache bucket
//        if (!_cache.TryGetValue(targetDashKey, out List<DashboardUiEvent>? dashEvents) || dashEvents == null)
//            return Json(new { success = false, message = "Updating background data..." });

//        // Fetch International Sectors from Cache
//        var destList = _cache.Get<List<FDestData>>("FDestList") ?? new List<FDestData>();
//        var intlSectors = destList.Where(d => d.IsInternationalNew)
//                                  .Select(d => $"{d.FromCode}-{d.ToCode}")
//                                  .ToHashSet(StringComparer.OrdinalIgnoreCase);

//        // Fetch Raw Events strictly from the same correlated cache tier
//        _cache.TryGetValue(targetRawKey, out List<EventListData>? rawEvents);

//        var today = DateTime.Today;
//        var yearStart = new DateTime(year, 1, 1).AddDays(-15);
//        var yearEnd = new DateTime(year, 12, 31).AddDays(15);

//        var filteredEvents = new List<object>();

//        foreach (var ev in dashEvents)
//        {
//            if (!DateTime.TryParse(ev.EventDate, out DateTime occasionDate)) continue;

//            bool occasionInYearWindow = occasionDate >= yearStart && occasionDate <= yearEnd;
//            if (!occasionInYearWindow) continue;

//            var validDailyStats = ev.DailyStats.Where(d => {
//                var statDate = DateTime.Parse(d.Date);
//                bool inYear = statDate >= yearStart && statDate <= yearEnd;
//                bool isFutureOrPresent = statDate >= today;

//                return includePast ? inYear : (inYear && isFutureOrPresent);
//            }).ToList();

//            // Keep it if there are active travel dates!
//            if (validDailyStats.Any() || (includePast && occasionInYearWindow))
//            {
//                // Calculate Domestic and International counts for this specific event using matching Raw cache
//                var rawEvent = rawEvents?.FirstOrDefault(r => r.EventId == ev.Id);
//                int domSectorsCount = 0;
//                int intlSectorsCount = 0;

//                if (rawEvent != null && rawEvent.Details != null)
//                {
//                    domSectorsCount = rawEvent.Details.Count(s => !intlSectors.Contains($"{s.FromCode}-{s.ToCode}"));
//                    intlSectorsCount = rawEvent.Details.Count(s => intlSectors.Contains($"{s.FromCode}-{s.ToCode}"));
//                }

//                filteredEvents.Add(new
//                {
//                    id = ev.Id,
//                    name = ev.Name,
//                    eventDate = ev.EventDate,
//                    sectorsCovered = ev.SectorsCovered,
//                    totalDomSectors = domSectorsCount,
//                    totalIntlSectors = intlSectorsCount,
//                    isImportant = ev.IsImportant,
//                    airlinesBreakdown = ev.AirlinesBreakdown,
//                    airlineAchieved = ev.AirlineAchieved,
//                    dailyStats = validDailyStats
//                });
//            }
//        }

//        return Json(new { success = true, data = filteredEvents });
//    }
//}

using inventory_analytics_tool.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace inventory_analytics_tool.Controllers;

[Authorize]
public class DashboardController : Controller
{
    private readonly ILogger<DashboardController> _logger;
    private readonly IMemoryCache _cache;

    public DashboardController(ILogger<DashboardController> logger, IMemoryCache cache)
    {
        _logger = logger;
        _cache = cache;
    }

    public IActionResult Index()
    {
        var upcomingEvents = new List<DashboardUiEvent>();

        // Always use Primary Phase 1 cache for initial dashboard load
        if (_cache.TryGetValue("DashboardMasterData", out List<DashboardUiEvent> dashEvents) && dashEvents != null)
        {
            var today = DateTime.Today;
            upcomingEvents = dashEvents
                .Where(e => DateTime.TryParse(e.EventDate, out DateTime parsedDate) && parsedDate.Date >= today)
                .OrderBy(e => DateTime.Parse(e.EventDate))
                .ToList();
        }

        return View(upcomingEvents);
    }

    [HttpGet]
    public IActionResult GetDashboardData(int year = 2026, bool includePast = false)
    {
        // ====================================================================
        // THE SMART SWITCH: Pick cache dynamically based on UI params!
        // ====================================================================
        bool usePrimaryCache = (year == 2026 && !includePast);
        string targetDashKey = usePrimaryCache ? "DashboardMasterData" : "HistoricalDashboardMasterData";
        string targetRawKey = usePrimaryCache ? "RawEventListData" : "HistoricalRawEventListData";

        // Pull from the requested cache bucket
        if (!_cache.TryGetValue(targetDashKey, out List<DashboardUiEvent>? dashEvents) || dashEvents == null)
            return Json(new { success = false, message = "Updating background data..." });

        // Fetch International Sectors from Cache
        var destList = _cache.Get<List<FDestData>>("FDestList") ?? new List<FDestData>();
        var intlSectors = destList.Where(d => d.IsInternationalNew)
                                  .Select(d => $"{d.FromCode}-{d.ToCode}")
                                  .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Fetch Raw Events strictly from the same correlated cache tier
        _cache.TryGetValue(targetRawKey, out List<EventListData>? rawEvents);

        var today = DateTime.Today;
        var yearStart = new DateTime(year, 1, 1).AddDays(-15);
        var yearEnd = new DateTime(year, 12, 31).AddDays(15);

        var filteredEvents = new List<object>();

        foreach (var ev in dashEvents)
        {
            if (!DateTime.TryParse(ev.EventDate, out DateTime occasionDate)) continue;

            bool occasionInYearWindow = occasionDate >= yearStart && occasionDate <= yearEnd;
            if (!occasionInYearWindow) continue;

            var validDailyStats = ev.DailyStats.Where(d => {
                var statDate = DateTime.Parse(d.Date);
                bool inYear = statDate >= yearStart && statDate <= yearEnd;
                bool isFutureOrPresent = statDate >= today;

                return includePast ? inYear : (inYear && isFutureOrPresent);
            }).ToList();

            // Keep it if there are active travel dates!
            if (validDailyStats.Any() || (includePast && occasionInYearWindow))
            {
                var rawEvent = rawEvents?.FirstOrDefault(r => r.EventId == ev.Id);
                int domSectorsCount = 0;
                int intlSectorsCount = 0;

                // Dictionaries to hold the aggregated day-by-day stats for Dom vs Intl
                var domDailyDict = new Dictionary<DateTime, (double t, int a, int f)>();
                var intlDailyDict = new Dictionary<DateTime, (double t, int a, int f)>();

                if (rawEvent != null && rawEvent.Details != null)
                {
                    foreach (var s in rawEvent.Details)
                    {
                        bool isIntl = intlSectors.Contains($"{s.FromCode}-{s.ToCode}");
                        if (isIntl) intlSectorsCount++; else domSectorsCount++;

                        var targetDict = isIntl ? intlDailyDict : domDailyDict;

                        // CRITICAL FIX: Pre-fill targetDict using DateRange so zero-flight days aren't dropped!
                        if (s.DateRange != null)
                        {
                            foreach (var dDate in s.DateRange)
                            {
                                if (!targetDict.ContainsKey(dDate.Date)) targetDict[dDate.Date] = (0, 0, 0);
                            }
                        }

                        if (s.Details != null)
                        {
                            foreach (var d in s.Details)
                            {
                                var date = d.TravelDate.Date;
                                if (!targetDict.ContainsKey(date)) targetDict[date] = (0, 0, 0);

                                var curr = targetDict[date];
                                targetDict[date] = (curr.t + d.TargetSeat, curr.a + d.Achieved, curr.f + d.FlightCount);
                            }
                        }
                    }
                }

                // Map the base daily stats ensuring clean JSON serialization
                var baseDailyStats = validDailyStats.Select(d => new {
                    date = d.Date,
                    priority = d.Priority,
                    targetSeat = d.TargetSeat,
                    achieved = d.Achieved,
                    flightCount = d.FlightCount
                }).ToList();

                var domDailyStats = new List<object>();
                var intlDailyStats = new List<object>();

                // Build specific arrays strictly mapping to the valid timeline
                foreach (var d in validDailyStats)
                {
                    var pDate = DateTime.Parse(d.Date).Date;

                    if (domDailyDict.TryGetValue(pDate, out var domVals))
                    {
                        domDailyStats.Add(new { date = d.Date, priority = d.Priority, targetSeat = domVals.t, achieved = domVals.a, flightCount = domVals.f });
                    }

                    if (intlDailyDict.TryGetValue(pDate, out var intlVals))
                    {
                        intlDailyStats.Add(new { date = d.Date, priority = d.Priority, targetSeat = intlVals.t, achieved = intlVals.a, flightCount = intlVals.f });
                    }
                }

                filteredEvents.Add(new
                {
                    id = ev.Id,
                    name = ev.Name,
                    eventDate = ev.EventDate,
                    sectorsCovered = ev.SectorsCovered,
                    totalDomSectors = domSectorsCount,
                    totalIntlSectors = intlSectorsCount,
                    isImportant = ev.IsImportant,
                    airlinesBreakdown = ev.AirlinesBreakdown,
                    airlineAchieved = ev.AirlineAchieved,
                    dailyStats = baseDailyStats,
                    domDailyStats = domDailyStats,     // Extracted Domestic Day ranges
                    intlDailyStats = intlDailyStats    // Extracted Intl Day ranges
                });
            }
        }

        return Json(new { success = true, data = filteredEvents });
    }
}