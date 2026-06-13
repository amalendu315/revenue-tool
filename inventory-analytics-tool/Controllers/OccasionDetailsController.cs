using ClosedXML.Excel;
using CsvHelper;
using CsvHelper.Configuration;
using ExcelDataReader;
using inventory_analytics_tool.Helpers;
using inventory_analytics_tool.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq; // Required for our new ID digit extractor
using System.Text.Json;

namespace purchase_analytics.Controllers;

// --- DYNAMIC CSV HEADER MAPPER ---
public sealed class UploadAttemptCsvRecordMap : ClassMap<UploadAttemptCsvRecord>
{
    public UploadAttemptCsvRecordMap()
    {
        // By adding Index(0), it guarantees CsvHelper grabs the ID even if the header text is corrupted
        Map(m => m.AqId).Name("AQ ID", "AqId").Index(0).Optional();
        Map(m => m.GroupRaisedRefNo).Name("Request Id", "Group Raised Ref Number", "GroupRaisedRefNo").Optional();
        Map(m => m.Origin).Name("Org", "Origin").Optional();
        Map(m => m.Destination).Name("Des", "Destination").Optional();
        Map(m => m.TravelDate).Name("Travel Date", "TravelDate").Optional();
        Map(m => m.AirlineCode).Name("Airline Code", "AirlineCode").Optional();
        Map(m => m.FlightNumber).Name("Flight Number", "FlightNo").Optional();
        Map(m => m.Pax).Name("No of Pax", "Pax", "RaisedPax").Optional();
        Map(m => m.AutoQuote).Name("Auto Quote", "AutoQuote").Optional();
        Map(m => m.Status).Name("Status").Optional();
        Map(m => m.NegotiatedFare).Name("Negotiated Fare", "NegotiatedFare").Optional();
        Map(m => m.ConfirmedStatus).Name("Confirmation Status", "Confirmed Status", "ConfirmedStatus").Optional();
        Map(m => m.Remarks).Name("Remarks").Optional();
        Map(m => m.PNR).Name("PNR").Optional();
        Map(m => m.OpenRemarks).Name("Open Remarks", "OpenRemarks").Optional();
        Map(m => m.Lead).Name("Lead").Optional();
    }
}

[Authorize]
public class OccasionDetailsController : Controller
{
    private readonly ILogger<OccasionDetailsController> _logger;
    private readonly IDatabaseHelper _dbHelper;
    private readonly IMemoryCache _cache;

    public OccasionDetailsController(ILogger<OccasionDetailsController> logger, IDatabaseHelper dbHelper, IMemoryCache cache)
    {
        _logger = logger;
        _dbHelper = dbHelper;
        _cache = cache;
    }

    public IActionResult Index(int id, string sectorType = "All")
    {
        if (id == 0) return RedirectToAction("Index", "Dashboard");

        ViewBag.EventId = id;
        ViewBag.SectorType = sectorType;
        ViewBag.EventName = "Details";

        if (_cache.TryGetValue("DashboardMasterData", out List<DashboardUiEvent> dashEvents) && dashEvents != null)
        {
            var ev = dashEvents.FirstOrDefault(e => e.Id == id);
            if (ev != null)
            {
                ViewBag.EventName = ev.Name;

                // FIX: Safely try to parse the string into a DateTime object
                if (DateTime.TryParse(ev.EventDate, out DateTime parsedDate))
                {
                    ViewBag.EventDate = parsedDate.ToString("MMM dd, yyyy");
                    ViewBag.EventDay = parsedDate.ToString("dddd");
                }
                else
                {
                    // Fallback just in case the string isn't a valid date format
                    ViewBag.EventDate = ev.EventDate;
                    ViewBag.EventDay = string.Empty;
                }
            }
        }

        return View();
    }

    [HttpGet]
    public async Task<IActionResult> GetSectorData(int id, string sectorType = "All", int year = 2026, bool includePast = false)
    {
        try
        {
            var allCachedEvents = new List<EventListData>();
            if (_cache.TryGetValue("RawEventListData", out List<EventListData> primary)) allCachedEvents.AddRange(primary);
            if (_cache.TryGetValue("HistoricalRawEventListData", out List<EventListData> historical)) allCachedEvents.AddRange(historical);

            var apiEvent = allCachedEvents.FirstOrDefault(e => e.EventId == id);
            if (apiEvent == null || apiEvent.Details == null) return Json(new { success = true, data = new List<object>() });

            _cache.TryGetValue("FDestList", out List<FDestData> cachedFDest);
            cachedFDest ??= new List<FDestData>();

            // Isolate International Sectors
            var intlSectors = cachedFDest.Where(d => d.IsInternationalNew)
                                         .Select(d => $"{d.FromCode}-{d.ToCode}")
                                         .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var today = DateTime.Today;
            var yearStart = new DateTime(year, 1, 1).AddDays(-15);
            var yearEnd = new DateTime(year, 12, 31).AddDays(15);

            var sectorList = new List<object>();

            foreach (var d in apiEvent.Details)
            {
                string sectorString = $"{d.FromCode}-{d.ToCode}";
                bool isIntl = intlSectors.Contains(sectorString);

                // --- NEW FILTERING: Drop sector natively if it doesn't match the passed SectorType! ---
                if (string.Equals(sectorType, "Domestic", StringComparison.OrdinalIgnoreCase) && isIntl) continue;
                if (string.Equals(sectorType, "International", StringComparison.OrdinalIgnoreCase) && !isIntl) continue;

                // CRITICAL FIX 1: Ignore the stale DateRange array. Parse actual travel details natively!
                var actualTravelDates = d.Details?.Select(x => x.TravelDate.Date).Distinct().ToList() ?? new List<DateTime>();
                var cleanDates = actualTravelDates.Where(dt => dt >= yearStart && dt <= yearEnd).OrderBy(dt => dt).ToList();

                if (!cleanDates.Any()) cleanDates.Add(d.Date);

                bool isFutureOrPresent = cleanDates.Max().Date >= today;
                if (!includePast && !isFutureOrPresent) continue;

                // CRITICAL FIX 2: Chunk the dates so disjointed gaps don't create false ranges
                // Replace your existing chunking for-loop logic with this:
                var chunks = new List<(DateTime start, DateTime end)>();
                DateTime chunkStart = cleanDates.First();
                DateTime chunkEnd = chunkStart;

                for (int i = 1; i < cleanDates.Count; i++)
                {
                    // Use the calendar's blackout range as the source of truth for the gap, not the missing flight data
                    if ((cleanDates[i] - chunkEnd).TotalDays <= 8.0) // Increased slightly to be safe for weekly schedules
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

                // Get the absolute min and max bounds for the entire event timeline
                var absoluteMin = cleanDates.First();
                var absoluteMax = cleanDates.Last();

                int liveAttemptCount = await _dbHelper.GetAttemptCountAsync(sectorString, absoluteMin, absoluteMax);
                var airlineAchieved = new Dictionary<string, int>();
                if (d.Airlines != null)
                {
                    foreach (var a in d.Airlines)
                    {
                        string code = !string.IsNullOrEmpty(a.AirlineCode) ? a.AirlineCode : "OAL";
                        airlineAchieved[code] = airlineAchieved.GetValueOrDefault(code, 0) + a.Achieved;
                    }
                }

                sectorList.Add(new
                {
                    festivalId = id,
                    sector = sectorString,
                    dateRange = dRange,
                    travelDate = d.Date,
                    airlinesCount = d.Airlines?.Count ?? 0,
                    flightCount = d.FlightCount,
                    totalStrength = d.TotalStrength,
                    target = d.TargetSeat,
                    achieved = d.Achieved,
                    attemptCount = liveAttemptCount,
                    airlinesBreakdown = airlines,
                    airlineAchieved = airlineAchieved,
                    station = stationName,
                    lead = leadName
                });
            }

            return Json(new { success = true, data = sectorList });
        }
        catch (Exception ex)
        {
            _logger.LogError($"Sector Load Error: {ex.Message}");
            return Json(new { success = false, message = ex.Message });
        }
    }

    //[HttpGet]
    //public async Task<IActionResult> GetFareData(int eventId, string sector, int year = 2026, bool includePast = false)
    //{
    //    try
    //    {
    //        if (!_cache.TryGetValue("RawEventListData", out List<EventListData> cachedEvents) || cachedEvents == null)
    //            return Json(new { success = false, message = "System is updating data." });

    //        var apiEvent = cachedEvents.FirstOrDefault(e => e.EventId == eventId);
    //        if (apiEvent == null || apiEvent.Details == null) return Json(new { success = true, data = new List<object>() });

    //        var sectorData = apiEvent.Details.FirstOrDefault(s => $"{s.FromCode}-{s.ToCode}" == sector);
    //        if (sectorData == null || sectorData.Details == null) return Json(new { success = true, data = new List<object>() });

    //        var today = DateTime.Today;
    //        var yearStart = new DateTime(year - 1, 12, 15);
    //        var yearEnd = new DateTime(year + 1, 1, 15);

    //        var fareList = new List<object>();

    //        var allBlackoutDates = sectorData.DateRange?.OrderBy(d => d).ToList() ?? new List<DateTime>();

    //        foreach (var date in allBlackoutDates)
    //        {
    //            if (date < yearStart || date > yearEnd) continue;
    //            if (!includePast && date.Date < today) continue;

    //            var tdd = sectorData.Details?.FirstOrDefault(d => d.TravelDate.Date == date.Date);
    //            if (tdd.TravelDate < yearStart || tdd.TravelDate > yearEnd) continue;
    //            if (!includePast && tdd.TravelDate.Date < today) continue;

    //            var dbFares = await _dbHelper.GetFlightLatestFaresAsync(sector, tdd.TravelDate);

    //            double finalOnlineFare = 0;
    //            int activeFlightCount = 0;
    //            int activeAirlinesCount = 0;
    //            int activeTotalStrength = 0;

    //            if (tdd.Flights != null && tdd.Flights.Any())
    //            {
    //                var thresholdDate = DateTime.Today.AddDays(-3);

    //                var validDbFares = dbFares
    //                    .Where(df => df.EntryDate >= thresholdDate)
    //                    .ToList();

    //                var validFlights = tdd.Flights
    //                    .Where(f => validDbFares.Any(df => df.AirlineCode == f.AirlineCode && df.FlightNo == f.FlightNo))
    //                    .GroupBy(f => new { f.AirlineCode, f.FlightNo })
    //                    .Select(g => g.First())
    //                    .ToList();

    //                activeFlightCount = validFlights.Count;
    //                activeAirlinesCount = validFlights.Select(f => f.AirlineCode).Distinct().Count();
    //                activeTotalStrength = activeFlightCount * 150;

    //                var matchingFares = validDbFares
    //                    .Where(df => validFlights.Any(f => f.AirlineCode == df.AirlineCode && df.FlightNo == f.FlightNo))
    //                    .Select(df => df.LatestFare)
    //                    .Where(fare => fare > 0)
    //                    .ToList();

    //                if (matchingFares.Any()) finalOnlineFare = matchingFares.Min();
    //            }

    //            var airlineAchieved = new Dictionary<string, int>();
    //            if (tdd.Airlines != null)
    //            {
    //                foreach (var a in tdd.Airlines)
    //                {
    //                    string code = !string.IsNullOrEmpty(a.AirlineCode) ? a.AirlineCode : "OAL";
    //                    airlineAchieved[code] = airlineAchieved.GetValueOrDefault(code, 0) + a.Achieved;
    //                }
    //            }

    //            fareList.Add(new
    //            {
    //                sector = sector,
    //                doj = date.ToString("dd MMM yyyy"),
    //                travelDate = date,
    //                airlinesCount = (tdd != null) ? activeAirlinesCount : 0,
    //                flightCount = (tdd != null) ? activeFlightCount : 0,
    //                totalStrength = (tdd != null) ? activeTotalStrength : 0,
    //                target = tdd?.TargetSeat ?? 0,
    //                achieved = tdd?.Achieved ?? 0,
    //                attemptCount = tdd?.AttemptCount ?? 0,
    //                targetFare = tdd != null ? (int)Math.Round(tdd.TargetFare, MidpointRounding.AwayFromZero) : 0,
    //                avgPurFare = tdd != null ? (int)Math.Round(tdd.AvgPurchaseFare, MidpointRounding.AwayFromZero) : 0,
    //                lastOnlineFare = (int)Math.Round(finalOnlineFare, MidpointRounding.AwayFromZero),
    //                flights = tdd?.Flights ?? new List<EventFlight>(),
    //                airlineAchieved = airlineAchieved
    //            });
    //        }

    //        return Json(new { success = true, data = fareList });
    //    }
    //    catch (Exception ex)
    //    {
    //        return Json(new { success = false, message = ex.Message });
    //    }
    //}

    [HttpGet]
    public async Task<IActionResult> GetFareData(int eventId, string sector, int year = 2026, bool includePast = false)
    {
        try
        {
            var allCachedEvents = new List<EventListData>();
            if (_cache.TryGetValue("RawEventListData", out List<EventListData> primaryRaw)) allCachedEvents.AddRange(primaryRaw);
            if (_cache.TryGetValue("HistoricalRawEventListData", out List<EventListData> histRaw)) allCachedEvents.AddRange(histRaw);

            var apiEvent = allCachedEvents.FirstOrDefault(e => e.EventId == eventId);
            if (apiEvent == null || apiEvent.Details == null) return Json(new { success = true, data = new List<object>() });

            var sectorData = apiEvent.Details.FirstOrDefault(s =>
                string.Equals($"{s.FromCode}-{s.ToCode}", sector, StringComparison.OrdinalIgnoreCase));

            if (sectorData == null)
                return Json(new { success = true, data = new List<object>() });

            var today = DateTime.Today;
            var yearStart = new DateTime(year - 1, 12, 15);
            var yearEnd = new DateTime(year + 1, 1, 15);

            var fareList = new List<object>();

            var allDashEvents = new List<DashboardUiEvent>();
            if (_cache.TryGetValue("DashboardMasterData", out List<DashboardUiEvent> primaryDash)) allDashEvents.AddRange(primaryDash);
            if (_cache.TryGetValue("HistoricalDashboardMasterData", out List<DashboardUiEvent> histDash)) allDashEvents.AddRange(histDash);

            var masterDates = new List<DateTime>();
            var dashEvent = allDashEvents.FirstOrDefault(e => e.Id == eventId);
            if (dashEvent != null && dashEvent.DailyStats != null)
            {
                masterDates = dashEvent.DailyStats.Select(ds => DateTime.Parse(ds.Date)).Where(dt => dt >= yearStart && dt <= yearEnd).OrderBy(d => d).ToList();
            }

            // IMPORTANT:
            // Use DateRange as the main calendar source,
            // but also merge TravelDate from Details as fallback.
            var blackoutDates = sectorData.DateRange?
                .Select(d => d.Date)
                .ToList() ?? new List<DateTime>();

            var detailDates = sectorData.Details?
                .Select(d => d.TravelDate.Date)
                .ToList() ?? new List<DateTime>();

            var allDates = blackoutDates
                .Concat(detailDates)
                .Where(d => d >= yearStart.Date && d <= yearEnd.Date)
                .Distinct()
                .OrderBy(d => d)
                .ToList();

            if (!allDates.Any() && sectorData.Date != default)
            {
                allDates.Add(sectorData.Date.Date);
            }

            foreach (var date in allDates)
            {
                if (!includePast && date.Date < today)
                    continue;

                var tdd = sectorData.Details?
                    .FirstOrDefault(d => d.TravelDate.Date == date.Date);

                double finalOnlineFare = 0;
                int activeFlightCount = 0;
                int activeAirlinesCount = 0;
                int activeTotalStrength = 0;

                var airlineAchieved = new Dictionary<string, int>();

                // Only fetch DB fare and calculate active flights when API has flight data for this date.
                if (tdd != null)
                {
                    var dbFares = await _dbHelper.GetFlightLatestFaresAsync(sector, date);

                    if (tdd.Flights != null && tdd.Flights.Any())
                    {
                        var thresholdDate = DateTime.Today.AddDays(-3);

                        var validDbFares = dbFares
                            .Where(df => df.EntryDate >= thresholdDate)
                            .ToList();

                        var validFlights = tdd.Flights
                            .Where(f => validDbFares.Any(df =>
                                df.AirlineCode == f.AirlineCode &&
                                df.FlightNo == f.FlightNo))
                            .GroupBy(f => new { f.AirlineCode, f.FlightNo })
                            .Select(g => g.First())
                            .ToList();

                        activeFlightCount = validFlights.Count;
                        activeAirlinesCount = validFlights
                            .Select(f => f.AirlineCode)
                            .Distinct()
                            .Count();

                        activeTotalStrength = activeFlightCount * 150;

                        var matchingFares = validDbFares
                            .Where(df => validFlights.Any(f =>
                                f.AirlineCode == df.AirlineCode &&
                                f.FlightNo == df.FlightNo))
                            .Select(df => df.LatestFare)
                            .Where(fare => fare > 0)
                            .ToList();

                        if (matchingFares.Any())
                            finalOnlineFare = matchingFares.Min();
                    }

                    if (tdd.Airlines != null)
                    {
                        foreach (var a in tdd.Airlines)
                        {
                            string code = !string.IsNullOrEmpty(a.AirlineCode) ? a.AirlineCode : "OAL";
                            airlineAchieved[code] = airlineAchieved.GetValueOrDefault(code, 0) + a.Achieved;
                        }
                    }
                }

                fareList.Add(new
                {
                    sector = sector,
                    doj = date.ToString("dd MMM yyyy"),
                    travelDate = date,

                    // These will be 0 when no flight data exists for that date.
                    airlinesCount = tdd != null ? activeAirlinesCount : 0,
                    flightCount = tdd != null ? activeFlightCount : 0,
                    totalStrength = tdd != null ? activeTotalStrength : 0,

                    target = tdd?.TargetSeat ?? 0,
                    achieved = tdd?.Achieved ?? 0,
                    attemptCount = tdd?.AttemptCount ?? 0,
                    targetFare = tdd != null ? (int)Math.Round(tdd.TargetFare, MidpointRounding.AwayFromZero) : 0,
                    avgPurFare = tdd != null ? (int)Math.Round(tdd.AvgPurchaseFare, MidpointRounding.AwayFromZero) : 0,
                    lastOnlineFare = (int)Math.Round(finalOnlineFare, MidpointRounding.AwayFromZero),

                    // Helpful flag for UI.
                    hasFlightData = tdd != null,

                    flights = tdd?.Flights ?? new List<EventFlight>(),
                    airlineAchieved = airlineAchieved
                });
            }

            return Json(new { success = true, data = fareList });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = ex.Message });
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetFareTrendChartData(string sector, string date, string airline, string flightNo)
    {
        if (string.IsNullOrEmpty(sector) || string.IsNullOrEmpty(date) || string.IsNullOrEmpty(airline) || string.IsNullOrEmpty(flightNo))
            return Json(new { success = false, message = "Missing specific flight parameters." });

        if (!DateTime.TryParse(date, out DateTime parsedDate))
            return Json(new { success = false, message = "Invalid travel date format." });

        try
        {
            var trendData = await _dbHelper.GetFareTrendAsync(sector, parsedDate, airline, flightNo);
            return Json(new { success = true, data = trendData });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = ex.Message });
        }
    }

    [HttpGet]
    public async Task<IActionResult> GetFlightLevelFares(int eventId, string sector, string date, int year = 2026, bool includePast = false)
    {
        if (!DateTime.TryParse(date, out DateTime parsedDate)) return Json(new { success = false });

        double targetFare = 0;
        var apiFlights = new List<EventFlight>();

        if (_cache.TryGetValue("RawEventListData", out List<EventListData> cachedEvents) && cachedEvents != null)
        {
            var apiEvent = cachedEvents.FirstOrDefault(e => e.EventId == eventId);
            if (apiEvent != null && apiEvent.Details != null)
            {
                var sectorData = apiEvent.Details.FirstOrDefault(s => $"{s.FromCode}-{s.ToCode}" == sector);
                if (sectorData != null && sectorData.Details != null)
                {
                    var dayData = sectorData.Details.FirstOrDefault(d => d.TravelDate.Date == parsedDate.Date);
                    if (dayData != null)
                    {
                        targetFare = dayData.TargetFare;
                        if (dayData.Flights != null) apiFlights = dayData.Flights;
                    }
                }
            }
        }

        var dbFares = await _dbHelper.GetFlightLatestFaresAsync(sector, parsedDate);

        var thresholdDate = DateTime.Today.AddDays(-3);
        var validDbFares = dbFares.Where(df => df.EntryDate >= thresholdDate).ToList();

        var results = new List<FlightFareTrendRow>();

        var uniqueApiFlights = apiFlights
            .GroupBy(f => new { f.AirlineCode, f.FlightNo })
            .Select(g => g.First())
            .ToList();

        foreach (var f in uniqueApiFlights)
        {
            var dbMatch = validDbFares.FirstOrDefault(df => df.AirlineCode == f.AirlineCode && df.FlightNo == f.FlightNo);

            if (dbMatch == null) continue;

            results.Add(new FlightFareTrendRow
            {
                Sector = sector,
                DOJ = parsedDate.ToString("dd MMM yyyy"),
                AirlineCode = f.AirlineCode,
                FlightNo = f.FlightNo,
                TargetFare = Math.Round(targetFare, MidpointRounding.AwayFromZero),
                LatestFare = Math.Round(dbMatch.LatestFare, MidpointRounding.AwayFromZero),
                EntryDate = dbMatch.EntryDate.ToString("dd MMM yyyy hh:mm tt")
            });
        }

        return Json(new { success = true, data = results });
    }

    public IActionResult FareDetails(int eventId, string sector, string sectorType = "All")
    {
        if (eventId == 0 || string.IsNullOrEmpty(sector)) return RedirectToAction("Index", "Dashboard");

        ViewBag.EventId = eventId;
        ViewBag.Sector = sector;
        ViewBag.SectorType = sectorType; 
        ViewBag.EventName = "Details"; // Default

        // NEW: Fetch the actual event name from the Dashboard cache
        if (_cache.TryGetValue("DashboardMasterData", out List<DashboardUiEvent> dashEvents) && dashEvents != null)
        {
            var ev = dashEvents.FirstOrDefault(e => e.Id == eventId);
            if (ev != null) ViewBag.EventName = ev.Name;
        }
        return View();
    }

    public IActionResult FareTrend(int eventId, string sector, string date, string sectorType = "All")
    {
        if (eventId == 0 || string.IsNullOrEmpty(sector) || string.IsNullOrEmpty(date))
            return RedirectToAction("Index", "Dashboard");

        ViewBag.EventId = eventId;
        ViewBag.Sector = sector;
        ViewBag.TravelDate = date;
        ViewBag.SectorType = sectorType;

        return View();
    }

    [HttpGet]
    [HttpGet]
    public async Task<IActionResult> AttemptDetails(int eventId, string sector, string travelDate = null, int year = 2026, bool includePast = false)
    {
        if (eventId == 0 || string.IsNullOrEmpty(sector)) return RedirectToAction("Index", "Dashboard");

        ViewBag.EventId = eventId;
        ViewBag.Sector = sector;

        DateTime targetMinDate;
        DateTime targetMaxDate;

        // SMART OVERRIDE: If the user clicked a specific date dot on the chart, lock the DB query to exactly that day!
        if (!string.IsNullOrEmpty(travelDate) && DateTime.TryParse(travelDate, out DateTime specificDate))
        {
            targetMinDate = specificDate.Date;
            targetMaxDate = specificDate.Date;
        }
        else
        {
            // Original Logic: Find the range of the whole sector event
            var yearStart = new DateTime(year, 1, 1).AddDays(-15);
            var yearEnd = new DateTime(year, 12, 31).AddDays(15);
            DateTime sectorMinDate = DateTime.MaxValue;
            DateTime sectorMaxDate = DateTime.MinValue;

            if (_cache.TryGetValue("RawEventListData", out List<EventListData> cachedEvents) && cachedEvents != null)
            {
                var apiEvent = cachedEvents.FirstOrDefault(e => e.EventId == eventId);
                if (apiEvent != null && apiEvent.Details != null)
                {
                    var sectorData = apiEvent.Details.FirstOrDefault(s => $"{s.FromCode}-{s.ToCode}" == sector);
                    if (sectorData != null)
                    {
                        var cleanDates = sectorData.DateRange?.Where(dt => dt >= yearStart && dt <= yearEnd).ToList() ?? new List<DateTime>();
                        if (!cleanDates.Any()) cleanDates.Add(sectorData.Date);

                        sectorMinDate = cleanDates.Min();
                        sectorMaxDate = cleanDates.Max();
                    }
                }
            }

            targetMinDate = sectorMinDate == DateTime.MaxValue ? yearStart : sectorMinDate;
            targetMaxDate = sectorMaxDate == DateTime.MinValue ? yearEnd : sectorMaxDate;
        }

        // Passes the dates (whether it's an exact day or a range) to your existing DB helper!
        var attemptRecords = await _dbHelper.GetAttemptDetailsAsync(sector, targetMinDate.Date, targetMaxDate.Date);

        return View(attemptRecords);
    }

    [HttpGet]
    public async Task<IActionResult> ExportAttemptsToExcel(int eventId, string sector, int year = 2026, bool includePast = false)
    {
        if (eventId == 0 || string.IsNullOrEmpty(sector)) return BadRequest("Invalid parameters");

        var yearStart = new DateTime(year, 1, 1).AddDays(-15);
        var yearEnd = new DateTime(year, 12, 31).AddDays(15);
        DateTime sectorMinDate = DateTime.MaxValue;
        DateTime sectorMaxDate = DateTime.MinValue;

        if (_cache.TryGetValue("RawEventListData", out List<EventListData> cachedEvents) && cachedEvents != null)
        {
            var apiEvent = cachedEvents.FirstOrDefault(e => e.EventId == eventId);
            if (apiEvent != null && apiEvent.Details != null)
            {
                var sectorData = apiEvent.Details.FirstOrDefault(s => $"{s.FromCode}-{s.ToCode}" == sector);
                if (sectorData != null)
                {
                    var cleanDates = sectorData.DateRange?.Where(dt => dt >= yearStart && dt <= yearEnd).ToList() ?? new List<DateTime>();
                    if (!cleanDates.Any()) cleanDates.Add(sectorData.Date);

                    sectorMinDate = cleanDates.Min();
                    sectorMaxDate = cleanDates.Max();
                }
            }
        }

        if (sectorMinDate == DateTime.MaxValue) sectorMinDate = yearStart;
        if (sectorMaxDate == DateTime.MinValue) sectorMaxDate = yearEnd;

        var attemptRecords = await _dbHelper.GetAttemptDetailsAsync(sector, sectorMinDate.Date, sectorMaxDate.Date);

        using (var workbook = new XLWorkbook())
        {
            var worksheet = workbook.Worksheets.Add("Attempt Data");

            // Write Headers
            string[] headers = { "AQ ID", "Entry Date", "Request Id", "Org", "Des", "Travel Date", "Airline Code", "Flight Number",
                                 "No of Pax", "Auto Quote", "Status", "Negotiated Fare", "Confirmation Status",
                                 "Remarks", "PNR", "Open Remarks", "Lead" };

            for (int i = 0; i < headers.Length; i++)
            {
                var cell = worksheet.Cell(1, i + 1);
                cell.Value = headers[i];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = XLColor.LightGray;
            }

            // Write Data
            // Write Data (ExportAttemptsToExcel)
            int row = 2;
            foreach (var rec in attemptRecords)
            {
                worksheet.Cell(row, 1).Value = rec.AqId ?? "";
                worksheet.Cell(row, 2).Value = rec.RawEntryDate.ToString("yyyy-MM-dd");
                worksheet.Cell(row, 3).Value = rec.ReqId ?? ""; // Fixed column alignment!
                worksheet.Cell(row, 4).Value = rec.Origin ?? "";
                worksheet.Cell(row, 5).Value = rec.Destination ?? "";
                worksheet.Cell(row, 6).Value = rec.RawTravelDate.ToString("yyyy-MM-dd");
                worksheet.Cell(row, 7).Value = rec.AirlineCode ?? "";

                // Safely handles missing flight codes
                worksheet.Cell(row, 8).Value = rec.FlightCode?.Replace(rec.AirlineCode ?? "", "").Trim() ?? "";

                worksheet.Cell(row, 9).Value = rec.Pax;
                worksheet.Cell(row, 10).Value = rec.AutoQuote?.Replace("₹", "").Replace(",", "") ?? "";

                // Safely handles the Pending check
                worksheet.Cell(row, 11).Value = (rec.Status == "Pending") ? "" : (rec.Status ?? "");

                worksheet.Cell(row, 12).Value = rec.NegFare?.Replace("₹", "").Replace(",", "") ?? "";
                worksheet.Cell(row, 13).Value = rec.ConfStatus ?? "";
                worksheet.Cell(row, 14).Value = rec.Remarks ?? "";
                worksheet.Cell(row, 15).Value = rec.PNR ?? "";
                worksheet.Cell(row, 16).Value = rec.OpenRemarks ?? "";
                worksheet.Cell(row, 17).Value = rec.Lead ?? "";
                row++;
            }

            // =========================================================================
            // CRITICAL FIX: THE HIDDEN SHEET METHOD FOR CORRUPTION-FREE DATA VALIDATION
            // =========================================================================
            var hiddenSheet = workbook.Worksheets.Add("ValidationData");
            hiddenSheet.Hide();

            // Populate Status List
            hiddenSheet.Cell("A1").Value = "NEGOTIATION SENT";
            hiddenSheet.Cell("A2").Value = "REQUEST REJECTED";
            hiddenSheet.Cell("A3").Value = "SELF REJECTED";
            hiddenSheet.Cell("A4").Value = "PNR CREATED";

            // Populate Conf Status List
            hiddenSheet.Cell("B1").Value = "YES";
            hiddenSheet.Cell("B2").Value = "NO";

            // Populate Remarks List
            hiddenSheet.Cell("C1").Value = "PNR CREATED";
            hiddenSheet.Cell("C2").Value = "NEGOTIATION REJECTED";
            hiddenSheet.Cell("C3").Value = "REQUEST REJECTED";
            hiddenSheet.Cell("C4").Value = "SELF REJECTED";
            hiddenSheet.Cell("C5").Value = "NEGOTIATION SATISFIED";
            hiddenSheet.Cell("C6").Value = "NEGOTIATION NOT SATISFIED";
            hiddenSheet.Cell("C7").Value = "NEGOTIATION PENDING";

            // Apply to Column 10 (Status)
            var statusRange = worksheet.Range(2, 10, 10000, 10);
            statusRange.SetDataValidation().List("=ValidationData!$A$1:$A$4", true);
            statusRange.SetDataValidation().IgnoreBlanks = true;

            // Apply to Column 12 (Confirmation Status)
            var confStatusRange = worksheet.Range(2, 12, 10000, 12);
            confStatusRange.SetDataValidation().List("=ValidationData!$B$1:$B$2", true);
            confStatusRange.SetDataValidation().IgnoreBlanks = true;

            // Apply to Column 13 (Remarks)
            var remarksRange = worksheet.Range(2, 13, 10000, 13);
            remarksRange.SetDataValidation().List("=ValidationData!$C$1:$C$7", true);
            remarksRange.SetDataValidation().IgnoreBlanks = true;

            worksheet.Columns().AdjustToContents();

            using (var stream = new MemoryStream())
            {
                workbook.SaveAs(stream);
                var content = stream.ToArray();
                return File(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"AirIQ_Attempts_{sector}.xlsx");
            }
        }
    }

    // --- CRITICAL FIX: SMART EXCEL HEADER PARSER ---
    private string GetExcelColumnValue(DataRow row, params string[] possibleNames)
    {
        // This strips hidden Byte Order Marks and spaces so headers map perfectly every time
        foreach (DataColumn col in row.Table.Columns)
        {
            string cleanColName = col.ColumnName.Replace("\uFEFF", "").Trim().ToUpper();
            foreach (var name in possibleNames)
            {
                if (cleanColName == name.ToUpper())
                {
                    if (row[col] != DBNull.Value) return row[col].ToString()?.Trim() ?? "";
                }
            }
        }
        return "";
    }

    [HttpPost]
    public async Task<IActionResult> UploadAttempts(IFormFile file)
    {
        if (file == null || file.Length == 0)
            return Json(new { success = false, message = "No file selected or file is empty." });

        var ext = Path.GetExtension(file.FileName).ToLower();
        if (ext != ".csv" && ext != ".xls" && ext != ".xlsx")
            return Json(new { success = false, message = "Please upload a valid .csv, .xls, or .xlsx file." });

        try
        {
            var records = new List<UploadAttemptCsvRecord>();
            var dbInsertList = new List<GroupRaisedEntryModel>();

            if (ext == ".csv")
            {
                using (var reader = new StreamReader(file.OpenReadStream()))
                using (var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture) { HasHeaderRecord = true, MissingFieldFound = null }))
                {
                    csv.Context.RegisterClassMap<UploadAttemptCsvRecordMap>();
                    records = csv.GetRecords<UploadAttemptCsvRecord>().ToList();
                }
            }
            else
            {
                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

                using (var stream = file.OpenReadStream())
                using (var reader = ExcelReaderFactory.CreateReader(stream))
                {
                    var result = reader.AsDataSet(new ExcelDataSetConfiguration()
                    {
                        ConfigureDataTable = (_) => new ExcelDataTableConfiguration() { UseHeaderRow = true }
                    });

                    var dt = result.Tables[0];

                    foreach (DataRow row in dt.Rows)
                    {
                        records.Add(new UploadAttemptCsvRecord
                        {
                            AqId = GetExcelColumnValue(row, "AQ ID", "AqId"),
                            GroupRaisedRefNo = GetExcelColumnValue(row, "Request Id", "Group Raised Ref Number", "GroupRaisedRefNo"),
                            Origin = GetExcelColumnValue(row, "Org", "Origin"),
                            Destination = GetExcelColumnValue(row, "Des", "Destination"),
                            TravelDate = GetExcelColumnValue(row, "Travel Date", "TravelDate"),
                            AirlineCode = GetExcelColumnValue(row, "Airline Code", "AirlineCode"),
                            FlightNumber = GetExcelColumnValue(row, "Flight Number", "FlightNo"),
                            Pax = GetExcelColumnValue(row, "No of Pax", "Pax", "RaisedPax"),
                            AutoQuote = GetExcelColumnValue(row, "Auto Quote", "AutoQuote"),
                            Status = GetExcelColumnValue(row, "Status"),
                            NegotiatedFare = GetExcelColumnValue(row, "Negotiated Fare", "NegotiatedFare"),
                            ConfirmedStatus = GetExcelColumnValue(row, "Confirmation Status", "Confirmed Status"),
                            Remarks = GetExcelColumnValue(row, "Remarks"),
                            PNR = GetExcelColumnValue(row, "PNR"),
                            OpenRemarks = GetExcelColumnValue(row, "Open Remarks", "OpenRemarks"),
                            Lead = GetExcelColumnValue(row, "Lead")
                        });
                    }
                }
            }

            _cache.TryGetValue("FDestList", out List<FDestData> cachedFDest);
            cachedFDest ??= new List<FDestData>();

            var today = DateTime.Now;

            foreach (var rec in records)
            {
                if (string.IsNullOrEmpty(rec.Origin) && string.IsNullOrEmpty(rec.Destination)) continue;

                // =========================================================================
                // CRITICAL FIX: AGGRESSIVE DIGIT EXTRACTOR FOR UPSERTS
                // =========================================================================
                // This rips out "AQ", spaces, or any other junk so int.TryParse successfully grabs the raw ID number
                string cleanAqId = new String(rec.AqId?.Where(Char.IsDigit).ToArray());
                int.TryParse(cleanAqId, out int parsedAqId);

                // CRITICAL FIX: Ensure the date is valid. If Excel is blank/corrupt, fallback to Today.
                if (!DateTime.TryParse(rec.TravelDate, out DateTime parsedTravelDate) || parsedTravelDate.Year < 2000)
                {
                    parsedTravelDate = DateTime.Today;
                }
                string calculatedSector = $"{rec.Origin?.Trim()}-{rec.Destination?.Trim()}";

                int feedId = await _dbHelper.GetOrInsertBlackoutFareFeedIdAsync(
                                calculatedSector,
                                parsedTravelDate,
                                rec.AirlineCode?.Trim(),
                                rec.FlightNumber?.Trim()
                               );

                var destMatch = cachedFDest.FirstOrDefault(d =>
                    string.Equals(d?.FromCode, rec.Origin?.Trim(), StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(d?.ToCode, rec.Destination?.Trim(), StringComparison.OrdinalIgnoreCase));

                int.TryParse(rec.Pax?.Replace(",", ""), out int paxCount);
                decimal.TryParse(rec.AutoQuote?.Replace("₹", "").Replace(",", ""), out decimal autoQuoteFare);
                decimal.TryParse(rec.NegotiatedFare?.Replace("₹", "").Replace(",", ""), out decimal negFare);

                string mappedStatus = "";
                string rawStatus = rec.Status?.ToUpper() ?? "";
                if (rawStatus.Contains("PNR CREATED")) mappedStatus ="PNR CREATED";
                else if (rawStatus.Contains("REJECTED")) mappedStatus = "REJECTED";
                else if (rawStatus.Contains("NEGOTIATION SENT")) mappedStatus = "NEGOTIATION SENT";

                dbInsertList.Add(new GroupRaisedEntryModel
                {
                    GroupRaisedEntryID = parsedAqId > 0 ? parsedAqId : (int?)null, // Passes the ID to SQL!
                    BlackoutFareFeedID = feedId > 0 ? feedId : null,
                    GroupRaisedRefNo = rec.GroupRaisedRefNo,
                    PNR = rec.PNR,
                    RaisedPax = paxCount,
                    GroupFare = autoQuoteFare,
                    UserName = destMatch?.InternalOwner ?? rec.Lead,
                    Status = mappedStatus!,
                    FDestID = destMatch?.FDestID,
                    RaisedDate = today,
                    EntryDate = today,
                    Remarks = rec.Remarks,
                    OpenRemarks = rec.OpenRemarks,
                    ConfirmationStatus = rec.ConfirmedStatus,
                    NegotiatedFare = negFare,
                    ModifiedDate = today
                });
            }

            bool isSuccess = await _dbHelper.BulkInsertGroupRaisedEntriesAsync(dbInsertList);

            if (isSuccess)
                return Json(new { success = true, message = $"Successfully imported {dbInsertList.Count} records." });
            else
                return Json(new { success = false, message = "Database insertion failed. Check server logs." });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = "Error processing file: " + ex.Message });
        }
    }

    [HttpGet]
    public async Task<IActionResult> AllAttempts(string fromDate, string toDate, string travelDate)
    {
        DateTime? entryStart = null;
        DateTime? entryEnd = null;
        DateTime? specificTravel = null;

        if (!string.IsNullOrEmpty(travelDate) && DateTime.TryParse(travelDate, out DateTime pTravel))
        {
            specificTravel = pTravel;
            ViewBag.TravelDate = specificTravel.Value.ToString("yyyy-MM-dd");
            ViewBag.FromDate = "";
            ViewBag.ToDate = "";
        }
        else if (!string.IsNullOrEmpty(fromDate) && !string.IsNullOrEmpty(toDate))
        {
            if (DateTime.TryParse(fromDate, out DateTime pFrom)) entryStart = pFrom;
            if (DateTime.TryParse(toDate, out DateTime pTo)) entryEnd = pTo;
            ViewBag.FromDate = entryStart?.ToString("yyyy-MM-dd");
            ViewBag.ToDate = entryEnd?.ToString("yyyy-MM-dd");
            ViewBag.TravelDate = "";
        }
        else
        {
            // Default load fetches today's entries only
            entryStart = DateTime.Today;
            entryEnd = DateTime.Today;
            ViewBag.FromDate = entryStart?.ToString("yyyy-MM-dd");
            ViewBag.ToDate = entryEnd?.ToString("yyyy-MM-dd");
            ViewBag.TravelDate = "";
        }

        var allRecords = await _dbHelper.GetAllAttemptDetailsAsync(entryStart, entryEnd, specificTravel);

        // --- NEW: Map internal owner to Lead ---
        _cache.TryGetValue("FDestList", out List<FDestData> cachedFDest);
        cachedFDest ??= new List<FDestData>();

        foreach (var rec in allRecords)
        {
            var destMatch = cachedFDest.FirstOrDefault(f =>
                string.Equals(f.FromCode, rec.Origin?.Trim(), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(f.ToCode, rec.Destination?.Trim(), StringComparison.OrdinalIgnoreCase));

            if (destMatch != null && !string.IsNullOrEmpty(destMatch.InternalOwner))
            {
                rec.Lead = destMatch.InternalOwner;
            }
            else if (string.IsNullOrEmpty(rec.Lead))
            {
                rec.Lead = "Unassigned";
            }
        }

        return View(allRecords);
    }


    //[HttpGet]
    //public async Task<IActionResult> ExportAllAttemptsToExcel(string fromDate, string toDate, string travelDate)
    //{
    //    DateTime? entryStart = null;
    //    DateTime? entryEnd = null;
    //    DateTime? specificTravel = null;

    //    if (!string.IsNullOrEmpty(travelDate) && DateTime.TryParse(travelDate, out DateTime pTravel))
    //    {
    //        specificTravel = pTravel;
    //    }
    //    else if (!string.IsNullOrEmpty(fromDate) && !string.IsNullOrEmpty(toDate))
    //    {
    //        if (DateTime.TryParse(fromDate, out DateTime pFrom)) entryStart = pFrom;
    //        if (DateTime.TryParse(toDate, out DateTime pTo)) entryEnd = pTo;
    //    }
    //    else
    //    {
    //        entryStart = DateTime.Today;
    //        entryEnd = DateTime.Today;
    //    }
    //    var attemptRecords = await _dbHelper.GetAllAttemptDetailsAsync(entryStart, entryEnd, specificTravel);

    //    using (var workbook = new XLWorkbook())
    //    {
    //        var worksheet = workbook.Worksheets.Add("All Attempts");

    //        string[] headers = { "AQ ID", "Request Id", "Org", "Des", "Travel Date", "Airline Code", "Flight Number",
    //                         "No of Pax", "Auto Quote", "Status", "Negotiated Fare", "Confirmation Status",
    //                         "Remarks", "PNR", "Open Remarks", "Lead" };

    //        for (int i = 0; i < headers.Length; i++)
    //        {
    //            var cell = worksheet.Cell(1, i + 1);
    //            cell.Value = headers[i];
    //            cell.Style.Font.Bold = true;
    //            cell.Style.Fill.BackgroundColor = XLColor.LightGray;
    //        }

    //        // Write Data (ExportAllAttemptsToExcel)
    //        int row = 2;
    //        foreach (var rec in attemptRecords)
    //        {
    //            worksheet.Cell(row, 1).Value = rec.AqId ?? "";
    //            worksheet.Cell(row, 2).Value = rec.ReqId ?? "";
    //            worksheet.Cell(row, 3).Value = rec.Origin ?? "";
    //            worksheet.Cell(row, 4).Value = rec.Destination ?? "";
    //            worksheet.Cell(row, 5).Value = rec.RawTravelDate.ToString("yyyy-MM-dd");
    //            worksheet.Cell(row, 6).Value = rec.AirlineCode ?? "";

    //            // Safely handles missing flight codes
    //            worksheet.Cell(row, 7).Value = rec.FlightCode?.Replace(rec.AirlineCode ?? "", "").Trim() ?? "";

    //            worksheet.Cell(row, 8).Value = rec.Pax;
    //            worksheet.Cell(row, 9).Value = rec.AutoQuote?.Replace("₹", "").Replace(",", "") ?? "";

    //            // Safely handles the Pending check
    //            worksheet.Cell(row, 10).Value = (rec.Status == "Pending") ? "" : (rec.Status ?? "");

    //            worksheet.Cell(row, 11).Value = rec.NegFare?.Replace("₹", "").Replace(",", "") ?? "";
    //            worksheet.Cell(row, 12).Value = rec.ConfStatus ?? "";
    //            worksheet.Cell(row, 13).Value = rec.Remarks ?? "";
    //            worksheet.Cell(row, 14).Value = rec.PNR ?? "";
    //            worksheet.Cell(row, 15).Value = rec.OpenRemarks ?? "";
    //            worksheet.Cell(row, 16).Value = rec.Lead ?? "";
    //            row++;
    //        }

    //        var hiddenSheet = workbook.Worksheets.Add("ValidationData");
    //        hiddenSheet.Hide();

    //        // Populate Status List
    //        hiddenSheet.Cell("A1").Value = "NEGOTIATION SENT";
    //        hiddenSheet.Cell("A2").Value = "REQUEST REJECTED";
    //        hiddenSheet.Cell("A3").Value = "SELF REJECTED";
    //        hiddenSheet.Cell("A4").Value = "PNR CREATED";

    //        // Populate Conf Status List
    //        hiddenSheet.Cell("B1").Value = "YES";
    //        hiddenSheet.Cell("B2").Value = "NO";

    //        // Populate Remarks List
    //        hiddenSheet.Cell("C1").Value = "PNR CREATED";
    //        hiddenSheet.Cell("C2").Value = "NEGOTIATION REJECTED";
    //        hiddenSheet.Cell("C3").Value = "REQUEST REJECTED";
    //        hiddenSheet.Cell("C4").Value = "SELF REJECTED";
    //        hiddenSheet.Cell("C5").Value = "NEGOTIATION SATISFIED";
    //        hiddenSheet.Cell("C6").Value = "NEGOTIATION NOT SATISFIED";
    //        hiddenSheet.Cell("C7").Value = "NEGOTIATION PENDING";

    //        // Apply to Column 10 (Status)
    //        var statusRange = worksheet.Range(2, 10, 10000, 10);
    //        statusRange.SetDataValidation().List("=ValidationData!$A$1:$A$4", true);
    //        statusRange.SetDataValidation().IgnoreBlanks = true;

    //        // Apply to Column 12 (Confirmation Status)
    //        var confStatusRange = worksheet.Range(2, 12, 10000, 12);
    //        confStatusRange.SetDataValidation().List("=ValidationData!$B$1:$B$2", true);
    //        confStatusRange.SetDataValidation().IgnoreBlanks = true;

    //        // Apply to Column 13 (Remarks)
    //        var remarksRange = worksheet.Range(2, 13, 10000, 13);
    //        remarksRange.SetDataValidation().List("=ValidationData!$C$1:$C$7", true);
    //        remarksRange.SetDataValidation().IgnoreBlanks = true;

    //        worksheet.Columns().AdjustToContents();

    //        using (var stream = new MemoryStream())
    //        {
    //            workbook.SaveAs(stream);
    //            var content = stream.ToArray();
    //            return File(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"AirIQ_Master_All_Attempts.xlsx");
    //        }
    //    }
    //}

    [HttpGet]
    public async Task<IActionResult> ExportAllAttemptsToExcel(
        string fromDate, string toDate, string travelDate,
        string fSector, string fStation, string fAirline,
        string fTravelDate, string fStatus, string fConfStatus,
        string fLead, string fFlightNo, string fMonth)
    {
        DateTime? entryStart = null;
        DateTime? entryEnd = null;
        DateTime? specificTravel = null;

        if (!string.IsNullOrEmpty(travelDate) && DateTime.TryParse(travelDate, out DateTime pTravel))
        {
            specificTravel = pTravel;
        }
        else if (!string.IsNullOrEmpty(fromDate) && !string.IsNullOrEmpty(toDate))
        {
            if (DateTime.TryParse(fromDate, out DateTime pFrom)) entryStart = pFrom;
            if (DateTime.TryParse(toDate, out DateTime pTo)) entryEnd = pTo;
        }
        else
        {
            entryStart = DateTime.Today;
            entryEnd = DateTime.Today;
        }

        // Fetch Base Records
        var attemptRecords = await _dbHelper.GetAllAttemptDetailsAsync(entryStart, entryEnd, specificTravel);

        // --- MAP LEADS (Required for the Lead filter to work!) ---
        _cache.TryGetValue("FDestList", out List<FDestData> cachedFDest);
        cachedFDest ??= new List<FDestData>();

        foreach (var rec in attemptRecords)
        {
            var destMatch = cachedFDest.FirstOrDefault(f =>
                string.Equals(f.FromCode, rec.Origin?.Trim(), StringComparison.OrdinalIgnoreCase) &&
                string.Equals(f.ToCode, rec.Destination?.Trim(), StringComparison.OrdinalIgnoreCase));

            if (destMatch != null && !string.IsNullOrEmpty(destMatch.InternalOwner))
            {
                rec.Lead = destMatch.InternalOwner;
            }
            else if (string.IsNullOrEmpty(rec.Lead))
            {
                rec.Lead = "Unassigned";
            }
        }

        // ==========================================================
        // APPLY DYNAMIC UI FILTERS TO C# DATA
        // ==========================================================
        if (!string.IsNullOrEmpty(fSector))
            attemptRecords = attemptRecords.Where(r => $"{r.Origin?.Trim()}-{r.Destination?.Trim()}".Equals(fSector, StringComparison.OrdinalIgnoreCase)).ToList();

        if (!string.IsNullOrEmpty(fStation))
            attemptRecords = attemptRecords.Where(r =>
                string.Equals(r.Origin?.Trim(), fStation, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(r.Destination?.Trim(), fStation, StringComparison.OrdinalIgnoreCase)).ToList();

        if (!string.IsNullOrEmpty(fAirline))
            attemptRecords = attemptRecords.Where(r => string.Equals(r.AirlineCode?.Trim(), fAirline, StringComparison.OrdinalIgnoreCase)).ToList();

        if (!string.IsNullOrEmpty(fTravelDate))
            attemptRecords = attemptRecords.Where(r => r.TravelDate == fTravelDate).ToList();

        if (!string.IsNullOrEmpty(fStatus))
        {
            attemptRecords = attemptRecords.Where(r => {
                string recStatus = r.Status?.ToUpper() ?? "PENDING";
                if (recStatus != "PNR CREATED" && recStatus != "REJECTED" && recStatus != "NEGOTIATION SENT")
                    recStatus = "PENDING";
                return string.Equals(recStatus, fStatus, StringComparison.OrdinalIgnoreCase);
            }).ToList();
        }

        if (!string.IsNullOrEmpty(fConfStatus))
        {
            attemptRecords = attemptRecords.Where(r => {
                string recConf = r.ConfStatus?.ToUpper() ?? "";
                if (recConf != "YES" && recConf != "NO") recConf = "";
                return string.Equals(recConf, fConfStatus, StringComparison.OrdinalIgnoreCase);
            }).ToList();
        }

        if (!string.IsNullOrEmpty(fLead))
            attemptRecords = attemptRecords.Where(r => string.Equals(r.Lead, fLead, StringComparison.OrdinalIgnoreCase)).ToList();

        if (!string.IsNullOrEmpty(fFlightNo))
            attemptRecords = attemptRecords.Where(r => r.FlightCode != null && r.FlightCode.Contains(fFlightNo, StringComparison.OrdinalIgnoreCase)).ToList();

        if (!string.IsNullOrEmpty(fMonth))
        {
            attemptRecords = attemptRecords.Where(r => {
                if (DateTime.TryParse(r.TravelDate, out DateTime dt))
                    return string.Equals(dt.ToString("MMMM"), fMonth, StringComparison.OrdinalIgnoreCase);
                return false;
            }).ToList();
        }


        // ==========================================================
        // EXCEL GENERATION (Unchanged)
        // ==========================================================
        using (var workbook = new XLWorkbook())
        {
            var worksheet = workbook.Worksheets.Add("All Attempts");

            string[] headers = { "AQ ID", "Request Id", "Org", "Des", "Travel Date", "Airline Code", "Flight Number",
                             "No of Pax", "Auto Quote", "Status", "Negotiated Fare", "Confirmation Status",
                             "Remarks", "PNR", "Open Remarks", "Lead" };

            for (int i = 0; i < headers.Length; i++)
            {
                var cell = worksheet.Cell(1, i + 1);
                cell.Value = headers[i];
                cell.Style.Font.Bold = true;
                cell.Style.Fill.BackgroundColor = XLColor.LightGray;
            }

            int row = 2;
            foreach (var rec in attemptRecords)
            {
                worksheet.Cell(row, 1).Value = rec.AqId ?? "";
                worksheet.Cell(row, 2).Value = rec.ReqId ?? "";
                worksheet.Cell(row, 3).Value = rec.Origin ?? "";
                worksheet.Cell(row, 4).Value = rec.Destination ?? "";
                worksheet.Cell(row, 5).Value = rec.RawTravelDate.ToString("yyyy-MM-dd");
                worksheet.Cell(row, 6).Value = rec.AirlineCode ?? "";
                worksheet.Cell(row, 7).Value = rec.FlightCode?.Replace(rec.AirlineCode ?? "", "").Trim() ?? "";
                worksheet.Cell(row, 8).Value = rec.Pax;
                worksheet.Cell(row, 9).Value = rec.AutoQuote?.Replace("₹", "").Replace(",", "") ?? "";
                worksheet.Cell(row, 10).Value = (rec.Status == "Pending") ? "" : (rec.Status ?? "");
                worksheet.Cell(row, 11).Value = rec.NegFare?.Replace("₹", "").Replace(",", "") ?? "";
                worksheet.Cell(row, 12).Value = rec.ConfStatus ?? "";
                worksheet.Cell(row, 13).Value = rec.Remarks ?? "";
                worksheet.Cell(row, 14).Value = rec.PNR ?? "";
                worksheet.Cell(row, 15).Value = rec.OpenRemarks ?? "";
                worksheet.Cell(row, 16).Value = rec.Lead ?? "";
                row++;
            }

            var hiddenSheet = workbook.Worksheets.Add("ValidationData");
            hiddenSheet.Hide();

            hiddenSheet.Cell("A1").Value = "NEGOTIATION SENT";
            hiddenSheet.Cell("A2").Value = "REQUEST REJECTED";
            hiddenSheet.Cell("A3").Value = "SELF REJECTED";
            hiddenSheet.Cell("A4").Value = "PNR CREATED";

            hiddenSheet.Cell("B1").Value = "YES";
            hiddenSheet.Cell("B2").Value = "NO";

            hiddenSheet.Cell("C1").Value = "PNR CREATED";
            hiddenSheet.Cell("C2").Value = "NEGOTIATION REJECTED";
            hiddenSheet.Cell("C3").Value = "REQUEST REJECTED";
            hiddenSheet.Cell("C4").Value = "SELF REJECTED";
            hiddenSheet.Cell("C5").Value = "NEGOTIATION SATISFIED";
            hiddenSheet.Cell("C6").Value = "NEGOTIATION NOT SATISFIED";
            hiddenSheet.Cell("C7").Value = "NEGOTIATION PENDING";

            var statusRange = worksheet.Range(2, 10, 10000, 10);
            statusRange.SetDataValidation().List("=ValidationData!$A$1:$A$4", true);
            statusRange.SetDataValidation().IgnoreBlanks = true;

            var confStatusRange = worksheet.Range(2, 12, 10000, 12);
            confStatusRange.SetDataValidation().List("=ValidationData!$B$1:$B$2", true);
            confStatusRange.SetDataValidation().IgnoreBlanks = true;

            var remarksRange = worksheet.Range(2, 13, 10000, 13);
            remarksRange.SetDataValidation().List("=ValidationData!$C$1:$C$7", true);
            remarksRange.SetDataValidation().IgnoreBlanks = true;

            worksheet.Columns().AdjustToContents();

            using (var stream = new MemoryStream())
            {
                workbook.SaveAs(stream);
                var content = stream.ToArray();
                return File(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"AirIQ_Master_All_Attempts.xlsx");
            }
        }
    }
}