using inventory_analytics_tool.Helpers;
using inventory_analytics_tool.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace inventory_analytics_tool.Controllers;

[Authorize(Roles = "Admin, Revenue")]
public class DashboardController : Controller
{
    private readonly ILogger<DashboardController> _logger;
    private readonly IMemoryCache _cache;
    private readonly IDatabaseHelper _dbHelper;

    public DashboardController(ILogger<DashboardController> logger, IMemoryCache cache, IDatabaseHelper dbHelper)
    {
        _logger = logger;
        _cache = cache;
        _dbHelper = dbHelper;
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


    public IActionResult FestivalCards()
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
        bool usePrimaryCache = (year == 2026 && !includePast);
        string targetDashKey = usePrimaryCache ? "DashboardMasterData" : $"HistoricalDashboardMasterData_{year}";
        string targetRawKey = usePrimaryCache ? "RawEventListData" : $"HistoricalRawEventListData_{year}";

        if (!_cache.TryGetValue(targetDashKey, out List<DashboardUiEvent>? dashEvents) || dashEvents == null)
            return Json(new { success = false, isUpdating = true, message = "System is updating background data." });

        var destList = _cache.Get<List<FDestData>>("FDestList") ?? new List<FDestData>();
        var intlSectors = destList.Where(d => d.IsInternationalNew)
                                  .Select(d => $"{d.FromCode}-{d.ToCode}")
                                  .ToHashSet(StringComparer.OrdinalIgnoreCase);

        _cache.TryGetValue(targetRawKey, out List<EventListData>? rawEvents);

        var today = DateTime.Today;
        var yearStart = new DateTime(year, 1, 1).AddDays(-40);
        var yearEnd = new DateTime(year, 12, 31).AddDays(40);

        var filteredEvents = new List<object>();

        foreach (var ev in dashEvents)
        {
            if (!DateTime.TryParse(ev.EventDate, out DateTime occasionDate)) continue;

            bool occasionInYearWindow = occasionDate >= yearStart && occasionDate <= yearEnd;
            if (occasionDate.Year != year) continue;

            var validDailyStats = ev.DailyStats.Where(d =>
            {
                var statDate = DateTime.Parse(d.Date);
                bool inYear = statDate >= yearStart && statDate <= yearEnd;
                bool isFutureOrPresent = statDate >= today;

                return includePast ? inYear : (inYear && isFutureOrPresent);
            }).ToList();

            if (validDailyStats.Any() || (includePast && occasionInYearWindow))
            {
                var rawEvent = rawEvents?.FirstOrDefault(r => r.EventId == ev.Id);
                var eventLeads = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var eventSectors = new List<object>();

                if (rawEvent != null && rawEvent.Details != null)
                {
                    foreach (var s in rawEvent.Details)
                    {
                        bool isIntl = intlSectors.Contains($"{s.FromCode}-{s.ToCode}");
                        var destMatch = destList.FirstOrDefault(d =>
                            string.Equals(d.FromCode, s.FromCode, StringComparison.OrdinalIgnoreCase) &&
                            string.Equals(d.ToCode, s.ToCode, StringComparison.OrdinalIgnoreCase));

                        string leadName = !string.IsNullOrEmpty(destMatch?.InternalOwner) ? destMatch.InternalOwner.Trim() : "Unassigned";
                        eventLeads.Add(leadName);

                        var sDays = new List<object>();
                        var validDatesSet = new HashSet<string>();

                        // Capture specific dates where this sector is active
                        if (s.DateRange != null)
                        {
                            foreach (var dRangeDate in s.DateRange)
                            {
                                validDatesSet.Add(dRangeDate.ToString("yyyy-MM-dd"));
                            }
                        }

                        if (s.Details != null)
                        {
                            foreach (var d in s.Details)
                            {
                                validDatesSet.Add(d.TravelDate.ToString("yyyy-MM-dd"));
                                sDays.Add(new
                                {
                                    date = d.TravelDate.ToString("yyyy-MM-dd"),
                                    targetSeat = d.TargetSeat,
                                    achieved = d.Achieved,
                                    flightCount = d.FlightCount,
                                    priority = !string.IsNullOrEmpty(d.BlackoutPriority) ? d.BlackoutPriority : "Normal"
                                });
                            }
                        }

                        var sAirlines = new Dictionary<string, int>();
                        if (s.Airlines != null)
                        {
                            foreach (var a in s.Airlines)
                            {
                                string code = !string.IsNullOrEmpty(a.AirlineCode) ? a.AirlineCode : "OAL";
                                sAirlines[code] = sAirlines.GetValueOrDefault(code, 0) + a.Achieved;
                            }
                        }

                        eventSectors.Add(new
                        {
                            sectorCode = $"{s.FromCode}-{s.ToCode}",
                            isIntl = isIntl,
                            lead = leadName,
                            days = sDays,
                            validDates = validDatesSet.ToList(),
                            airlineAchieved = sAirlines
                        });
                    }
                }

                // Map base timeline priorities safely
                var baseDailyStats = validDailyStats.Select(d => new
                {
                    date = DateTime.Parse(d.Date).ToString("yyyy-MM-dd"),
                    priority = d.Priority
                }).ToList();

                filteredEvents.Add(new
                {
                    id = ev.Id,
                    name = ev.Name,
                    eventDate = ev.EventDate,
                    sectorsCovered = rawEvent?.Details?.Count ?? 0,
                    isImportant = ev.IsImportant,
                    leads = eventLeads.ToList(),
                    dailyStats = baseDailyStats,
                    eventSectors = eventSectors // Passes the detailed sectors for JS to calculate
                });
            }
        }

        return Json(new { success = true, data = filteredEvents });
    }

    public IActionResult AllSectors()
    {
        return View();
    }

    [HttpGet]
    public async Task<IActionResult> GetAllSectorsData(int year = 2026, bool includePast = false)
    {
        try
        {
            bool usePrimaryCache = (year == 2026 && !includePast);
            // FIX: Dynamically fetch the year-specific cache key
            string targetDashKey = usePrimaryCache ? "DashboardMasterData" : $"HistoricalDashboardMasterData_{year}";
            string targetRawKey = usePrimaryCache ? "RawEventListData" : $"HistoricalRawEventListData_{year}";

            if (!_cache.TryGetValue(targetRawKey, out List<EventListData> rawEvents) || rawEvents == null)
                return Json(new { success = false, message = "System is updating data." });

            if (!_cache.TryGetValue(targetDashKey, out List<DashboardUiEvent> dashEvents) || dashEvents == null)
                return Json(new { success = false, message = "System is updating data." });

            _cache.TryGetValue("FDestList", out List<FDestData> cachedFDest);
            cachedFDest ??= new List<FDestData>();

            var intlSectors = cachedFDest.Where(d => d.IsInternationalNew)
                                         .Select(d => $"{d.FromCode}-{d.ToCode}")
                                         .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var today = DateTime.Today;
            var yearStart = new DateTime(year, 1, 1).AddDays(-15);
            var yearEnd = new DateTime(year, 12, 31).AddDays(15);
            var masterList = new List<object>();

            // Loop through ALL events in the dashboard
            foreach (var dashEvent in dashEvents)
            {
                if (!DateTime.TryParse(dashEvent.EventDate, out DateTime occasionDate)) continue;
                if (occasionDate.Year != year) continue; // Stops year bleeding

                var rawEvent = rawEvents.FirstOrDefault(e => e.EventId == dashEvent.Id);
                if (rawEvent == null || rawEvent.Details == null) continue;

                foreach (var d in rawEvent.Details)
                {
                    string sectorString = $"{d.FromCode}-{d.ToCode}";

                    string typeOfSector = intlSectors.Contains(sectorString) ? "International" : "Domestic";

                    var actualTravelDates = d.DateRange?.Distinct().ToList() ?? new List<DateTime>();
                    if (!actualTravelDates.Any() && d.Details != null)
                    {
                        actualTravelDates = d.Details.Select(x => x.TravelDate.Date).Distinct().ToList();
                    }

                    var cleanDates = actualTravelDates.Where(dt => dt >= yearStart && dt <= yearEnd).OrderBy(dt => dt).ToList();
                    if (!cleanDates.Any()) cleanDates.Add(d.Date);

                    bool isFutureOrPresent = cleanDates.Max().Date >= today;
                    if (!includePast && !isFutureOrPresent) continue;

                    var chunks = new List<(DateTime start, DateTime end)>();
                    DateTime chunkStart = cleanDates.First();
                    DateTime chunkEnd = chunkStart;

                    for (int i = 1; i < cleanDates.Count; i++)
                    {
                        if ((cleanDates[i] - chunkEnd).TotalDays <= 8.0)
                        {
                            chunkEnd = cleanDates[i];
                        }
                        else
                        {
                            chunks.Add((chunkStart, chunkEnd));
                            chunkStart = cleanDates[i];
                            chunkEnd = chunkStart;
                        }
                    }
                    chunks.Add((chunkStart, chunkEnd));

                    var chunkStrings = chunks.Select(c =>
                    {
                        if (c.start == c.end) return $"{c.start:d MMM yyyy}";
                        if (c.start.Year == c.end.Year) return $"{c.start:d MMM} – {c.end:d MMM yyyy}";
                        return $"{c.start:d MMM yyyy} – {c.end:d MMM yyyy}";
                    });

                    string dRange = string.Join(", ", chunkStrings);
                    var airlines = d.Airlines?.Select(a => $"{a.AirlineName} ({d.Flights?.Count(f => f.AirlineCode == a.AirlineCode) ?? 0})").ToList() ?? new List<string>();

                    var destMatch = cachedFDest.FirstOrDefault(f =>
                        string.Equals(f.FromCode, d.FromCode, StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(f.ToCode, d.ToCode, StringComparison.OrdinalIgnoreCase));

                    string stationName = !string.IsNullOrEmpty(destMatch?.StationName) ? destMatch.StationName : (destMatch?.FDestName ?? "Unassigned");
                    string leadName = !string.IsNullOrEmpty(destMatch?.InternalOwner) ? destMatch.InternalOwner : "Unassigned";

                    var absoluteMin = cleanDates.First();
                    var absoluteMax = cleanDates.Last();

                    //int liveAttemptCount = await _dbHelper.GetAttemptCountAsync(sectorString, absoluteMin, absoluteMax);
                    var airlineAchieved = new Dictionary<string, int>();

                    if (d.Airlines != null)
                    {
                        foreach (var a in d.Airlines)
                        {
                            string code = !string.IsNullOrEmpty(a.AirlineCode) ? a.AirlineCode : "OAL";
                            airlineAchieved[code] = airlineAchieved.GetValueOrDefault(code, 0) + a.Achieved;
                        }
                    }

                    DateTime.TryParse(dashEvent.EventDate, out DateTime parsedDashDate);

                    masterList.Add(new
                    {
                        eventId = dashEvent.Id,
                        eventName = dashEvent.Name,
                        eventDateString = parsedDashDate != DateTime.MinValue ? parsedDashDate.ToString("dd MMM yyyy") : dashEvent.EventDate,
                        sector = sectorString,
                        sectorType = typeOfSector,
                        dateRange = dRange,
                        travelDate = d.Date,
                        airlinesCount = d.Airlines?.Count ?? 0,
                        flightCount = d.FlightCount,
                        totalStrength = d.TotalStrength,
                        target = d.TargetSeat,
                        achieved = d.Achieved,
                        //attemptCount = d.Achieved,
                        airlinesBreakdown = airlines,
                        airlineAchieved = airlineAchieved,
                        station = stationName,
                        lead = leadName
                    });
                }
            }

            return Json(new { success = true, data = masterList });
        }
        catch (Exception ex)
        {
            _logger.LogError($"All Sectors Load Error: {ex.Message}");
            return Json(new { success = false, message = "Error: " + ex.Message });
        }
    }

    public IActionResult FareDropAlerts()
    {
        return View();
    }

    [HttpGet]
    public IActionResult GetAllUniqueSectors()
    {
        // Quickly pull every available sector from the destination list to populate the popup UI
        _cache.TryGetValue("FDestList", out List<FDestData> cachedFDest);
        cachedFDest ??= new List<FDestData>();

        var sectors = cachedFDest.Select(f => $"{f.FromCode}-{f.ToCode}")
                                 .Distinct()
                                 .OrderBy(s => s)
                                 .ToList();

        return Json(new { success = true, data = sectors });
    }

    [HttpGet]
    public async Task<IActionResult> GetAlertPreferences()
    {
        try
        {
            string userName = User.Identity?.Name ?? "Guest";
            var sectors = await _dbHelper.GetUserSectorAlertPreferencesAsync(userName);
            return Json(new { success = true, data = sectors });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = ex.Message });
        }
    }

    [HttpPost]
    public async Task<IActionResult> SaveAlertPreferences([FromBody] List<string> sectors)
    {
        try
        {
            string userName = User.Identity?.Name ?? "Guest";
            if (userName == "Guest") return Json(new { success = false, message = "User not authenticated." });

            bool result = await _dbHelper.SaveUserSectorAlertPreferencesAsync(userName, sectors ?? new List<string>());
            return Json(new { success = result });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = ex.Message });
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetActiveAlerts()
    {
        try
        {
            string userName = User.Identity?.Name ?? "Guest";

            // 1. Ask the DB what this specific user is subscribed to
            var userSectors = await _dbHelper.GetUserSectorAlertPreferencesAsync(userName);

            // 2. If they have 0 subscriptions, send a special flag to trigger the "Select Sectors" UI
            if (userSectors == null || !userSectors.Any())
            {
                return Json(new { success = true, notSubscribed = true, data = new List<object>() });
            }

            // 3. If they are subscribed, filter the global cached alerts!
            if (_cache.TryGetValue("ActiveFareAlerts", out List<object> cachedAlerts) && cachedAlerts != null)
            {
                var userSectorSet = userSectors.ToHashSet(StringComparer.OrdinalIgnoreCase);

                var filteredAlerts = cachedAlerts.Where(a =>
                {
                    // Extract the sector from the cached anonymous object
                    string sec = ((dynamic)a).sector;
                    return userSectorSet.Contains(sec);
                }).ToList();

                return Json(new { success = true, notSubscribed = false, data = filteredAlerts });
            }

            return Json(new { success = true, notSubscribed = false, data = new List<object>() });
        }
        catch (Exception ex)
        {
            _logger.LogError($"Alerts UI Load Error: {ex.Message}");
            return Json(new { success = false, message = ex.Message });
        }
    }

    [HttpGet]
    public IActionResult IndexV2()
    {
        return View();
    }

    // Add this inside FareAuditController.cs

    [HttpGet]
    public IActionResult FestivalCardsV2()
    {
        return View();
    }
}