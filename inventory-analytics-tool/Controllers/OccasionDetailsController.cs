//using ClosedXML.Excel;
//using CsvHelper;
//using CsvHelper.Configuration;
//using DocumentFormat.OpenXml.Bibliography;
//using ExcelDataReader;
//using inventory_analytics_tool.Helpers;
//using inventory_analytics_tool.InventoryProviders;
//using inventory_analytics_tool.Models;
//using Microsoft.AspNetCore.Authorization;
//using Microsoft.AspNetCore.Mvc;
//using Microsoft.Extensions.Caching.Memory;
//using System.Data;
//using System.Diagnostics;
//using System.Globalization;
//using System.IO;
//using System.Linq; 
//using System.Text.Json;

//namespace purchase_analytics.Controllers;

//// --- DYNAMIC CSV HEADER MAPPER ---
//public sealed class UploadAttemptCsvRecordMap : ClassMap<UploadAttemptCsvRecord>
//{
//    public UploadAttemptCsvRecordMap()
//    {
//        Map(m => m.AqId).Name("AQ ID", "AqId").Index(0).Optional();
//        Map(m => m.GroupRaisedRefNo).Name("Request Id", "Group Raised Ref Number", "GroupRaisedRefNo").Optional();
//        Map(m => m.Origin).Name("Org", "Origin").Optional();
//        Map(m => m.Destination).Name("Des", "Destination").Optional();
//        Map(m => m.TravelDate).Name("Travel Date", "TravelDate").Optional();
//        Map(m => m.AirlineCode).Name("Airline Code", "AirlineCode").Optional();
//        Map(m => m.FlightNumber).Name("Flight Number", "FlightNo").Optional();
//        Map(m => m.Pax).Name("No of Pax", "Pax", "RaisedPax").Optional();
//        Map(m => m.AutoQuote).Name("Auto Quote", "AutoQuote").Optional();
//        Map(m => m.Status).Name("Status").Optional();
//        Map(m => m.NegotiatedFare).Name("Negotiated Fare", "NegotiatedFare").Optional();
//        Map(m => m.ConfirmedStatus).Name("Confirmation Status", "Confirmed Status", "ConfirmedStatus").Optional();
//        Map(m => m.Remarks).Name("Remarks").Optional();
//        Map(m => m.PNR).Name("PNR").Optional();
//        Map(m => m.OpenRemarks).Name("Open Remarks", "OpenRemarks").Optional();
//        Map(m => m.Lead).Name("Lead").Optional();
//    }
//}

//[Authorize(Roles = "Admin, Revenue")]
//public class OccasionDetailsController : Controller
//{
//    private readonly ILogger<OccasionDetailsController> _logger;
//    private readonly IDatabaseHelper _dbHelper;
//    private readonly IMemoryCache _cache;

//    public OccasionDetailsController(ILogger<OccasionDetailsController> logger, IDatabaseHelper dbHelper, IMemoryCache cache)
//    {
//        _logger = logger;
//        _dbHelper = dbHelper;
//        _cache = cache;
//    }

//    public IActionResult Index(int id, string sectorType = "All", string lead = "All", int year = 2026, string priority = "All")
//    {
//        if (id == 0) return RedirectToAction("Index", "Dashboard");

//        ViewBag.EventId = id;
//        ViewBag.SectorType = sectorType;
//        ViewBag.Lead = lead;
//        ViewBag.Year = year;
//        ViewBag.Priority = priority;
//        ViewBag.EventName = "Details";

//        string targetDashKey = (year == 2026) ? "DashboardMasterData" : $"HistoricalDashboardMasterData_{year}";

//        if (_cache.TryGetValue(targetDashKey, out List<DashboardUiEvent> dashEvents) && dashEvents != null)
//        {
//            var ev = dashEvents.FirstOrDefault(e => e.Id == id);
//            if (ev != null)
//            {
//                ViewBag.EventName = ev.Name;

//                if (DateTime.TryParse(ev.EventDate, out DateTime parsedDate))
//                {
//                    ViewBag.EventDate = parsedDate.ToString("MMM dd, yyyy");
//                    ViewBag.EventDay = parsedDate.ToString("dddd");
//                }
//                else
//                {
//                    ViewBag.EventDate = ev.EventDate;
//                    ViewBag.EventDay = string.Empty;
//                }
//            }
//        }

//        return View();
//    }




//    [HttpGet]
//    public async Task<IActionResult> GetSectorData(int id, string sectorType = "All", int year = 2026, bool includePast = false, string lead = "All", string priority = "All")
//    {
//        try
//        {
//            bool usePrimaryCache = (year == 2026 && !includePast);
//            string targetRawKey = usePrimaryCache ? "RawEventListData" : $"HistoricalRawEventListData_{year}";
//            var allCachedEvents = new List<EventListData>();
//            if (!_cache.TryGetValue(targetRawKey, out List<EventListData> cachedEvents) || cachedEvents == null)
//                return Json(new { success = true, data = new List<object>() });

//            var apiEvent = cachedEvents.FirstOrDefault(e => e.EventId == id);
//            if (apiEvent == null || apiEvent.Details == null) return Json(new { success = true, data = new List<object>() });

//            _cache.TryGetValue("FDestList", out List<FDestData> cachedFDest);
//            cachedFDest ??= new List<FDestData>();

//            var intlSectors = cachedFDest.Where(d => d.IsInternationalNew)
//                                         .Select(d => $"{d.FromCode}-{d.ToCode}")
//                                         .ToHashSet(StringComparer.OrdinalIgnoreCase);

//            var today = DateTime.Today;
//            var yearStart = new DateTime(year, 1, 1).AddDays(-15);
//            var yearEnd = new DateTime(year, 12, 31).AddDays(15);

//            // BULK FETCH: Get all attempts for all sectors simultaneously
//            var allSectorStrings = apiEvent.Details.Select(d => $"{d.FromCode}-{d.ToCode}").Distinct().ToList();
//            var bulkDetailedCounts = await _dbHelper.GetBulkAttemptCountsDetailedAsync(allSectorStrings, yearStart, yearEnd);

//            var sectorList = new List<object>();

//            foreach (var d in apiEvent.Details)
//            {
//                string sectorString = $"{d.FromCode}-{d.ToCode}";
//                bool isIntl = intlSectors.Contains(sectorString);

//                if (string.Equals(sectorType, "Domestic", StringComparison.OrdinalIgnoreCase) && isIntl) continue;
//                if (string.Equals(sectorType, "International", StringComparison.OrdinalIgnoreCase) && !isIntl) continue;

//                if (!string.Equals(priority, "All", StringComparison.OrdinalIgnoreCase) &&
//                    !string.Equals(priority, "Weekend", StringComparison.OrdinalIgnoreCase))
//                {
//                    bool hasPriorityMatch = false;
//                    if (d.Details != null && d.Details.Any())
//                    {
//                        foreach (var detail in d.Details)
//                        {
//                            string p = !string.IsNullOrEmpty(detail.BlackoutPriority) ? detail.BlackoutPriority : "Normal";
//                            if (string.Equals(p, priority, StringComparison.OrdinalIgnoreCase))
//                            {
//                                hasPriorityMatch = true;
//                                break;
//                            }
//                        }
//                    }
//                    else if (string.Equals(priority, "Normal", StringComparison.OrdinalIgnoreCase))
//                    {
//                        hasPriorityMatch = true;
//                    }
//                    if (!hasPriorityMatch) continue;
//                }

//                var destMatch = cachedFDest.FirstOrDefault(f =>
//                string.Equals(f.FromCode, d.FromCode, StringComparison.OrdinalIgnoreCase) &&
//                string.Equals(f.ToCode, d.ToCode, StringComparison.OrdinalIgnoreCase));

//                string stationName = !string.IsNullOrEmpty(destMatch?.StationName) ? destMatch.StationName : (destMatch?.FDestName ?? "Unassigned");
//                string leadName = !string.IsNullOrEmpty(destMatch?.InternalOwner) ? destMatch.InternalOwner.Trim() : "Unassigned";

//                if (!string.Equals(lead, "All", StringComparison.OrdinalIgnoreCase) &&
//                    !string.Equals(lead, leadName, StringComparison.OrdinalIgnoreCase))
//                {
//                    continue;
//                }

//                var actualTravelDates = d.Details?.Select(x => x.TravelDate.Date).Distinct().ToList() ?? new List<DateTime>();
//                var cleanDates = actualTravelDates.Where(dt => dt >= yearStart && dt <= yearEnd).OrderBy(dt => dt).ToList();

//                if (!cleanDates.Any()) cleanDates.Add(d.Date);

//                bool isFutureOrPresent = cleanDates.Max().Date >= today;
//                if (!includePast && !isFutureOrPresent) continue;

//                var chunks = new List<(DateTime start, DateTime end)>();
//                DateTime chunkStart = cleanDates.First();
//                DateTime chunkEnd = chunkStart;

//                for (int i = 1; i < cleanDates.Count; i++)
//                {
//                    if ((cleanDates[i] - chunkEnd).TotalDays <= 8.0)
//                    {
//                        chunkEnd = cleanDates[i];
//                    }
//                    else
//                    {
//                        chunks.Add((chunkStart, chunkEnd));
//                        chunkStart = cleanDates[i];
//                        chunkEnd = chunkStart;
//                    }
//                }
//                chunks.Add((chunkStart, chunkEnd));

//                var chunkStrings = chunks.Select(c =>
//                {
//                    if (c.start == c.end) return $"{c.start:d MMM yyyy}";
//                    if (c.start.Year == c.end.Year) return $"{c.start:d MMM} – {c.end:d MMM yyyy}";
//                    return $"{c.start:d MMM yyyy} – {c.end:d MMM yyyy}";
//                });

//                string dRange = string.Join(", ", chunkStrings);

//                int dynamicAchieved = 0;
//                int dynamicTarget = 0;
//                int dynamicFlightCount = 0;
//                var dynamicAirlineAchieved = new Dictionary<string, int>();
//                var flightCountsByAirline = new Dictionary<string, int>();

//                if (d.Details != null && d.Details.Any())
//                {
//                    var validDailyDetails = d.Details.Where(x => cleanDates.Contains(x.TravelDate.Date)).ToList();

//                    dynamicAchieved = validDailyDetails.Sum(x => x.Achieved);
//                    dynamicTarget = validDailyDetails.Sum(x => x.TargetSeat);
//                    dynamicFlightCount = validDailyDetails.Sum(x => x.FlightCount);

//                    foreach (var daily in validDailyDetails)
//                    {
//                        if (daily.Airlines != null)
//                        {
//                            foreach (var a in daily.Airlines)
//                            {
//                                string code = !string.IsNullOrEmpty(a.AirlineCode) ? a.AirlineCode : "OAL";
//                                dynamicAirlineAchieved[code] = dynamicAirlineAchieved.GetValueOrDefault(code, 0) + a.Achieved;

//                                int flightCount = daily.Flights?.Count(f => string.Equals(f.AirlineCode, a.AirlineCode, StringComparison.OrdinalIgnoreCase)) ?? 0;
//                                flightCountsByAirline[code] = flightCountsByAirline.GetValueOrDefault(code, 0) + flightCount;
//                            }
//                        }
//                    }
//                }
//                else
//                {
//                    dynamicAchieved = d.Achieved;
//                    dynamicTarget = d.TargetSeat;
//                    dynamicFlightCount = d.FlightCount;
//                    if (d.Airlines != null)
//                    {
//                        foreach (var a in d.Airlines)
//                        {
//                            string code = !string.IsNullOrEmpty(a.AirlineCode) ? a.AirlineCode : "OAL";
//                            dynamicAirlineAchieved[code] = dynamicAirlineAchieved.GetValueOrDefault(code, 0) + a.Achieved;
//                        }
//                    }
//                }

//                var airlines = flightCountsByAirline.Select(kvp => $"{kvp.Key} ({kvp.Value})").ToList();
//                if (!airlines.Any())
//                {
//                    airlines = d.Airlines?.Select(a => $"{a.AirlineName} ({d.Flights?.Count(f => f.AirlineCode == a.AirlineCode) ?? 0})").ToList() ?? new List<string>();
//                }

//                var absoluteMin = cleanDates.First();
//                var absoluteMax = cleanDates.Last();
//                int liveAttemptCount = bulkDetailedCounts
//                        .Where(x => string.Equals(x.Sector, sectorString, StringComparison.OrdinalIgnoreCase)
//                                 && x.TravelDate >= absoluteMin
//                                 && x.TravelDate <= absoluteMax)
//                        .Sum(x => x.Count);

//                sectorList.Add(new
//                {
//                    festivalId = id,
//                    sector = sectorString,
//                    dateRange = dRange,
//                    travelDate = d.Date,
//                    airlinesCount = dynamicAirlineAchieved.Count > 0 ? dynamicAirlineAchieved.Count : (d.Airlines?.Count ?? 0),
//                    flightCount = dynamicFlightCount,
//                    totalStrength = d.TotalStrength,
//                    target = dynamicTarget,
//                    achieved = dynamicAchieved,
//                    attemptCount = liveAttemptCount,
//                    airlinesBreakdown = airlines,
//                    airlineAchieved = dynamicAirlineAchieved,
//                    station = stationName,
//                    lead = leadName
//                });
//            }

//            return Json(new { success = true, data = sectorList });
//        }
//        catch (Exception ex)
//        {
//            _logger.LogError($"Sector Load Error: {ex.Message}");
//            return Json(new { success = false, message = ex.Message });
//        }
//    }


//    [HttpGet]
//    public async Task<IActionResult> GetFareData(int eventId, string sector, int year = 2026, bool includePast = false, string priority = "All")
//    {
//        try
//        {
//            bool usePrimaryCache = (year == 2026 && !includePast);
//            string targetRawKey = usePrimaryCache ? "RawEventListData" : $"HistoricalRawEventListData_{year}";
//            string targetDashKey = usePrimaryCache ? "DashboardMasterData" : $"HistoricalDashboardMasterData_{year}";
//            var allCachedEvents = new List<EventListData>();
//            if (_cache.TryGetValue("RawEventListData", out List<EventListData> primaryRaw)) allCachedEvents.AddRange(primaryRaw);
//            if (_cache.TryGetValue("HistoricalRawEventListData", out List<EventListData> histRaw)) allCachedEvents.AddRange(histRaw);
//            if (!_cache.TryGetValue(targetRawKey, out List<EventListData> cachedEvents) || cachedEvents == null)
//                return Json(new { success = true, data = new List<object>() });

//            var apiEvent = cachedEvents.FirstOrDefault(e => e.EventId == eventId);
//            if (apiEvent == null || apiEvent.Details == null) return Json(new { success = true, data = new List<object>() });

//            var sectorData = apiEvent.Details.FirstOrDefault(s =>
//                string.Equals($"{s.FromCode}-{s.ToCode}", sector, StringComparison.OrdinalIgnoreCase));

//            if (sectorData == null)
//                return Json(new { success = true, data = new List<object>() });

//            var today = DateTime.Today;
//            var yearStart = new DateTime(year - 1, 12, 15);
//            var yearEnd = new DateTime(year + 1, 1, 15);

//            var allDashEvents = new List<DashboardUiEvent>();
//            if (_cache.TryGetValue("DashboardMasterData", out List<DashboardUiEvent> primaryDash)) allDashEvents.AddRange(primaryDash);
//            if (_cache.TryGetValue("HistoricalDashboardMasterData", out List<DashboardUiEvent> histDash)) allDashEvents.AddRange(histDash);

//            var masterDates = new List<DateTime>();
//            var dashEvent = allDashEvents.FirstOrDefault(e => e.Id == eventId);
//            if (dashEvent != null && dashEvent.DailyStats != null)
//            {
//                masterDates = dashEvent.DailyStats.Select(ds => DateTime.Parse(ds.Date)).Where(dt => dt >= yearStart && dt <= yearEnd).OrderBy(d => d).ToList();
//            }

//            var blackoutDates = sectorData.DateRange?.Select(d => d.Date).ToList() ?? new List<DateTime>();
//            var detailDates = sectorData.Details?.Select(d => d.TravelDate.Date).ToList() ?? new List<DateTime>();

//            var allDates = blackoutDates
//                .Concat(detailDates)
//                .Where(d => d >= yearStart.Date && d <= yearEnd.Date)
//                .Distinct()
//                .OrderBy(d => d)
//                .ToList();

//            if (!allDates.Any() && sectorData.Date != default)
//            {
//                allDates.Add(sectorData.Date.Date);
//            }

//            // BULK FETCH: Preload ALL database requirements outside the loop
//            var overallMinDate = allDates.DefaultIfEmpty(yearStart).Min();
//            var overallMaxDate = allDates.DefaultIfEmpty(yearEnd).Max();

//            var bulkAttemptCounts = await _dbHelper.GetBulkAttemptCountsByDateAsync(sector, overallMinDate, overallMaxDate);
//            var bulkFares = await _dbHelper.GetBulkFlightLatestFaresAsync(sector, overallMinDate, overallMaxDate);

//            var fareList = new List<object>();

//            foreach (var date in allDates)
//            {
//                if (!includePast && date.Date < today)
//                    continue;

//                var tdd = sectorData.Details?.FirstOrDefault(d => d.TravelDate.Date == date.Date);

//                if (!string.Equals(priority, "All", StringComparison.OrdinalIgnoreCase) &&
//                    !string.Equals(priority, "Weekend", StringComparison.OrdinalIgnoreCase))
//                {
//                    string p = !string.IsNullOrEmpty(tdd?.BlackoutPriority) ? tdd.BlackoutPriority : "Normal";
//                    if (!string.Equals(p, priority, StringComparison.OrdinalIgnoreCase))
//                    {
//                        continue; 
//                    }
//                }

//                double finalOnlineFare = 0;
//                string finalOnlineFareEntryDate = "-";
//                int activeFlightCount = 0;
//                int activeAirlinesCount = 0;
//                int activeTotalStrength = 0;
//                var airlineAchieved = new Dictionary<string, int>();

//                int liveAttemptCount = bulkAttemptCounts.GetValueOrDefault(date.Date, 0);

//                if (tdd != null)
//                {
//                    var thresholdDate = DateTime.Today.AddDays(-3);
//                    var validDbFares = bulkFares
//                        .Where(f => f.TravelDate.Date == date.Date && f.EntryDate >= thresholdDate)
//                        .ToList();

//                    if (tdd.Flights != null && tdd.Flights.Any())
//                    {
//                        var validFlights = tdd.Flights
//                            .Where(f => validDbFares.Any(df =>
//                                df.AirlineCode == f.AirlineCode &&
//                                df.FlightNo == f.FlightNo))
//                            .GroupBy(f => new { f.AirlineCode, f.FlightNo })
//                            .Select(g => g.First())
//                            .ToList();

//                        activeFlightCount = validFlights.Count;
//                        activeAirlinesCount = validFlights.Select(f => f.AirlineCode).Distinct().Count();
//                        activeTotalStrength = activeFlightCount * 150;

//                        var matchingDbRecords = validDbFares
//                            .Where(df => validFlights.Any(f =>
//                                f.AirlineCode == df.AirlineCode &&
//                                f.FlightNo == df.FlightNo))
//                            .Where(df => df.LatestFare > 0)
//                            .ToList();

//                        if (matchingDbRecords.Any())
//                        {
//                            var lowestRecord = matchingDbRecords
//                                .OrderBy(df => df.LatestFare)
//                                .ThenByDescending(df => df.EntryDate)
//                                .First();

//                            finalOnlineFare = lowestRecord.LatestFare;
//                            finalOnlineFareEntryDate = lowestRecord.EntryDate.ToString("dd MMM yyyy HH:mm tt");
//                        }
//                    }

//                    if (tdd.Airlines != null)
//                    {
//                        foreach (var a in tdd.Airlines)
//                        {
//                            string code = !string.IsNullOrEmpty(a.AirlineCode) ? a.AirlineCode : "OAL";
//                            airlineAchieved[code] = airlineAchieved.GetValueOrDefault(code, 0) + a.Achieved;
//                        }
//                    }
//                }

//                fareList.Add(new
//                {
//                    sector = sector,
//                    doj = date.ToString("dd MMM yyyy"),
//                    travelDate = date,
//                    priority = !string.IsNullOrEmpty(tdd?.BlackoutPriority) ? tdd.BlackoutPriority : "Normal",
//                    airlinesCount = tdd != null ? activeAirlinesCount : 0,
//                    flightCount = tdd != null ? activeFlightCount : 0,
//                    totalStrength = tdd != null ? activeTotalStrength : 0,
//                    target = tdd?.TargetSeat ?? 0,
//                    achieved = tdd?.Achieved ?? 0,
//                    attemptCount = liveAttemptCount,
//                    targetFare = tdd != null ? (int)Math.Round(tdd.TargetFare, MidpointRounding.AwayFromZero) : 0,
//                    avgPurFare = tdd != null ? (int)Math.Round(tdd.AvgPurchaseFare, MidpointRounding.AwayFromZero) : 0,
//                    lastOnlineFare = (int)Math.Round(finalOnlineFare, MidpointRounding.AwayFromZero),
//                    lastOnlineFareEntryDate = finalOnlineFareEntryDate,
//                    hasFlightData = tdd != null,
//                    flights = tdd?.Flights ?? new List<EventFlight>(),
//                    airlineAchieved = airlineAchieved
//                });
//            }

//            return Json(new { success = true, data = fareList });
//        }
//        catch (Exception ex)
//        {
//            return Json(new { success = false, message = ex.Message });
//        }
//    }

//    [HttpGet]
//    public async Task<IActionResult> GetFareTrendChartData(string sector, string date, string airline, string flightNo)
//    {
//        if (string.IsNullOrEmpty(sector) || string.IsNullOrEmpty(date) || string.IsNullOrEmpty(airline) || string.IsNullOrEmpty(flightNo))
//            return Json(new { success = false, message = "Missing specific flight parameters." });

//        if (!DateTime.TryParse(date, out DateTime parsedDate))
//            return Json(new { success = false, message = "Invalid travel date format." });

//        try
//        {
//            var trendData = await _dbHelper.GetFareTrendAsync(sector, parsedDate, airline, flightNo);
//            return Json(new { success = true, data = trendData });
//        }
//        catch (Exception ex)
//        {
//            return Json(new { success = false, message = ex.Message });
//        }
//    }

//    [HttpGet]
//    public async Task<IActionResult> GetFlightLevelFares(int eventId, string sector, string date, int year = 2026, bool includePast = false)
//    {
//        if (!DateTime.TryParse(date, out DateTime parsedDate)) return Json(new { success = false });

//        bool usePrimaryCache = (year == 2026 && !includePast);
//        string targetRawKey = usePrimaryCache ? "RawEventListData" : $"HistoricalRawEventListData_{year}";

//        double targetFare = 0;
//        var apiFlights = new List<EventFlight>();

//        if (_cache.TryGetValue(targetRawKey, out List<EventListData> cachedEvents) && cachedEvents != null)
//        {
//            var apiEvent = cachedEvents.FirstOrDefault(e => e.EventId == eventId);
//            if (apiEvent != null && apiEvent.Details != null)
//            {
//                var sectorData = apiEvent.Details.FirstOrDefault(s => $"{s.FromCode}-{s.ToCode}" == sector);
//                if (sectorData != null && sectorData.Details != null)
//                {
//                    var dayData = sectorData.Details.FirstOrDefault(d => d.TravelDate.Date == parsedDate.Date);
//                    if (dayData != null)
//                    {
//                        targetFare = dayData.TargetFare;
//                        if (dayData.Flights != null) apiFlights = dayData.Flights;
//                    }
//                }
//            }
//        }

//        var dbFares = await _dbHelper.GetFlightLatestFaresAsync(sector, parsedDate);

//        var thresholdDate = DateTime.Today.AddDays(-3);
//        var validDbFares = dbFares.Where(df => df.EntryDate >= thresholdDate).ToList();

//        var results = new List<FlightFareTrendRow>();

//        var uniqueApiFlights = apiFlights
//            .GroupBy(f => new { f.AirlineCode, f.FlightNo })
//            .Select(g => g.First())
//            .ToList();

//        foreach (var f in uniqueApiFlights)
//        {
//            var dbMatch = validDbFares.FirstOrDefault(df => df.AirlineCode == f.AirlineCode && df.FlightNo == f.FlightNo);

//            if (dbMatch == null) continue;

//            results.Add(new FlightFareTrendRow
//            {
//                Sector = sector,
//                DOJ = parsedDate.ToString("dd MMM yyyy"),
//                AirlineCode = f.AirlineCode,
//                FlightNo = f.FlightNo,
//                TargetFare = Math.Round(targetFare, MidpointRounding.AwayFromZero),
//                LatestFare = Math.Round(dbMatch.LatestFare, MidpointRounding.AwayFromZero),
//                EntryDate = dbMatch.EntryDate.ToString("dd MMM yyyy hh:mm tt")
//            });
//        }

//        return Json(new { success = true, data = results });
//    }

//    public IActionResult FareDetails(int eventId, string sector, string sectorType = "All", string lead = "All", int year=2026, string priority = "All")
//    {
//        if (eventId == 0 || string.IsNullOrEmpty(sector)) return RedirectToAction("Index", "Dashboard");

//        ViewBag.EventId = eventId;
//        ViewBag.Sector = sector;
//        ViewBag.SectorType = sectorType;
//        ViewBag.Lead = lead;
//        ViewBag.Year = year;
//        ViewBag.Priority = priority;
//        ViewBag.EventName = "Details"; 

//        if (_cache.TryGetValue("DashboardMasterData", out List<DashboardUiEvent> dashEvents) && dashEvents != null)
//        {
//            var ev = dashEvents.FirstOrDefault(e => e.Id == eventId);
//            if (ev != null) ViewBag.EventName = ev.Name;
//        }
//        return View();
//    }

//    public IActionResult FareTrend(int eventId, string sector, string date, string sectorType = "All", string lead = "All", int year = 2026, string priority = "All")
//    {
//        if (eventId == 0 || string.IsNullOrEmpty(sector) || string.IsNullOrEmpty(date))
//            return RedirectToAction("Index", "Dashboard");

//        ViewBag.EventId = eventId;
//        ViewBag.Sector = sector;
//        ViewBag.TravelDate = date;
//        ViewBag.SectorType = sectorType;
//        ViewBag.Priority = priority;
//        ViewBag.Lead = lead; 
//        ViewBag.Year = year;

//        return View();
//    }

//    [HttpGet]
//    public async Task<IActionResult> AttemptDetails(int eventId, string sector, string travelDate = null, int year = 2026, bool includePast = false)
//    {
//        if (eventId == 0 || string.IsNullOrEmpty(sector)) return RedirectToAction("Index", "Dashboard");

//        ViewBag.EventId = eventId;
//        ViewBag.Sector = sector;
//        ViewBag.Year = year;

//        DateTime targetMinDate;
//        DateTime targetMaxDate;

//        if (!string.IsNullOrEmpty(travelDate) && DateTime.TryParse(travelDate, out DateTime specificDate))
//        {
//            targetMinDate = specificDate.Date;
//            targetMaxDate = specificDate.Date;
//        }
//        else
//        {
//            var yearStart = new DateTime(year, 1, 1).AddDays(-15);
//            var yearEnd = new DateTime(year, 12, 31).AddDays(15);
//            DateTime sectorMinDate = DateTime.MaxValue;
//            DateTime sectorMaxDate = DateTime.MinValue;

//            string targetRawKey = (year == 2026) ? "RawEventListData" : $"HistoricalRawEventListData_{year}";

//            if (_cache.TryGetValue(targetRawKey, out List<EventListData> cachedEvents) && cachedEvents != null)
//            {
//                var apiEvent = cachedEvents.FirstOrDefault(e => e.EventId == eventId);
//                if (apiEvent != null && apiEvent.Details != null)
//                {
//                    var sectorData = apiEvent.Details.FirstOrDefault(s => $"{s.FromCode}-{s.ToCode}" == sector);
//                    if (sectorData != null)
//                    {
//                        var cleanDates = sectorData.DateRange?.Where(dt => dt >= yearStart && dt <= yearEnd).ToList() ?? new List<DateTime>();
//                        if (!cleanDates.Any()) cleanDates.Add(sectorData.Date);

//                        sectorMinDate = cleanDates.Min();
//                        sectorMaxDate = cleanDates.Max();
//                    }
//                }
//            }

//            targetMinDate = sectorMinDate == DateTime.MaxValue ? yearStart : sectorMinDate;
//            targetMaxDate = sectorMaxDate == DateTime.MinValue ? yearEnd : sectorMaxDate;
//        }

//        var attemptRecords = await _dbHelper.GetAttemptDetailsAsync(sector, targetMinDate.Date, targetMaxDate.Date);

//        return View(attemptRecords);
//    }

//    [HttpGet]
//    public async Task<IActionResult> ExportAttemptsToExcel(int eventId, string sector, int year = 2026, bool includePast = false)
//    {
//        if (eventId == 0 || string.IsNullOrEmpty(sector)) return BadRequest("Invalid parameters");

//        var yearStart = new DateTime(year, 1, 1).AddDays(-15);
//        var yearEnd = new DateTime(year, 12, 31).AddDays(15);
//        DateTime sectorMinDate = DateTime.MaxValue;
//        DateTime sectorMaxDate = DateTime.MinValue;

//        if (_cache.TryGetValue("RawEventListData", out List<EventListData> cachedEvents) && cachedEvents != null)
//        {
//            var apiEvent = cachedEvents.FirstOrDefault(e => e.EventId == eventId);
//            if (apiEvent != null && apiEvent.Details != null)
//            {
//                var sectorData = apiEvent.Details.FirstOrDefault(s => $"{s.FromCode}-{s.ToCode}" == sector);
//                if (sectorData != null)
//                {
//                    var cleanDates = sectorData.DateRange?.Where(dt => dt >= yearStart && dt <= yearEnd).ToList() ?? new List<DateTime>();
//                    if (!cleanDates.Any()) cleanDates.Add(sectorData.Date);

//                    sectorMinDate = cleanDates.Min();
//                    sectorMaxDate = cleanDates.Max();
//                }
//            }
//        }

//        if (sectorMinDate == DateTime.MaxValue) sectorMinDate = yearStart;
//        if (sectorMaxDate == DateTime.MinValue) sectorMaxDate = yearEnd;

//        var attemptRecords = await _dbHelper.GetAttemptDetailsAsync(sector, sectorMinDate.Date, sectorMaxDate.Date);

//        using (var workbook = new XLWorkbook())
//        {
//            var worksheet = workbook.Worksheets.Add("Attempt Data");

//            string[] headers = { "AQ ID", "Entry Date", "Request Id", "Org", "Des", "Travel Date", "Airline Code", "Flight Number",
//                                 "No of Pax", "Auto Quote", "Status", "Negotiated Fare", "Confirmation Status",
//                                 "Remarks", "PNR", "Open Remarks", "Lead" };

//            for (int i = 0; i < headers.Length; i++)
//            {
//                var cell = worksheet.Cell(1, i + 1);
//                cell.Value = headers[i];
//                cell.Style.Font.Bold = true;
//                cell.Style.Fill.BackgroundColor = XLColor.LightGray;
//            }

//            int row = 2;

//                foreach (var rec in attemptRecords)
//                {
//                    worksheet.Cell(row, 1).Value = rec.AqId ?? "";
//                worksheet.Cell(row, 2).Value = rec.RawEntryDate.ToString("yyyy-MM-dd") ?? "";
//                    worksheet.Cell(row, 3).Value = rec.ReqId ?? "";
//                    worksheet.Cell(row, 4).Value = rec.Origin ?? "";
//                    worksheet.Cell(row, 5).Value = rec.Destination ?? "";
//                    worksheet.Cell(row, 6).Value = rec.RawTravelDate.ToString("yyyy-MM-dd");
//                    worksheet.Cell(row, 7).Value = rec.AirlineCode ?? "";

//                    string fCode = rec.FlightCode ?? "";
//                    string aCode = rec.AirlineCode ?? "";
//                    if (!string.IsNullOrEmpty(aCode)) { fCode = fCode.Replace(aCode, "").Trim(); }
//                    worksheet.Cell(row, 8).Value = fCode;

//                    worksheet.Cell(row, 9).Value = rec.Pax;

//                    string autoQuote = rec.AutoQuote ?? "";
//                    worksheet.Cell(row, 10).Value = autoQuote.Replace("₹", "").Replace(",", "");

//                    worksheet.Cell(row, 11).Value = rec.Status ?? "";

//                    string negFare = rec.NegFare ?? "";
//                    worksheet.Cell(row, 12).Value = negFare.Replace("₹", "").Replace(",", "");

//                    worksheet.Cell(row, 13).Value = rec.ConfStatus ?? "";
//                    worksheet.Cell(row, 14).Value = rec.Remarks ?? "";
//                    worksheet.Cell(row, 15).Value = rec.PNR ?? "";
//                    worksheet.Cell(row, 16).Value = rec.OpenRemarks ?? "";
//                    worksheet.Cell(row, 17).Value = rec.Lead ?? "";
//                    row++;
//                }

//            var hiddenSheet = workbook.Worksheets.Add("ValidationData");
//            hiddenSheet.Hide();

//            hiddenSheet.Cell("A1").Value = "NEGOTIATION SENT";
//            hiddenSheet.Cell("A2").Value = "QUOTATION PENDING";
//            hiddenSheet.Cell("A3").Value = "REQUEST REJECTED";
//            hiddenSheet.Cell("A4").Value = "SELF REJECTED";
//            hiddenSheet.Cell("A5").Value = "PNR CREATED";

//            hiddenSheet.Cell("B1").Value = "YES";
//            hiddenSheet.Cell("B2").Value = "NO";

//            hiddenSheet.Cell("C1").Value = "PNR CREATED";
//            hiddenSheet.Cell("C2").Value = "NEGOTIATION REJECTED";
//            hiddenSheet.Cell("C3").Value = "REQUEST REJECTED";
//            hiddenSheet.Cell("C4").Value = "SELF REJECTED";
//            hiddenSheet.Cell("C5").Value = "NEGOTIATION SATISFIED";
//            hiddenSheet.Cell("C6").Value = "NEGOTIATION NOT SATISFIED";
//            hiddenSheet.Cell("C7").Value = "NEGOTIATION PENDING";

//            var statusRange = worksheet.Range(2, 10, 10000, 10);
//            statusRange.SetDataValidation().List("=ValidationData!$A$1:$A$5", true);
//            statusRange.SetDataValidation().IgnoreBlanks = true;

//            var confStatusRange = worksheet.Range(2, 12, 10000, 12);
//            confStatusRange.SetDataValidation().List("=ValidationData!$B$1:$B$2", true);
//            confStatusRange.SetDataValidation().IgnoreBlanks = true;

//            var remarksRange = worksheet.Range(2, 13, 10000, 13);
//            remarksRange.SetDataValidation().List("=ValidationData!$C$1:$C$7", true);
//            remarksRange.SetDataValidation().IgnoreBlanks = true;

//            worksheet.Columns().AdjustToContents();

//            using (var stream = new MemoryStream())
//            {
//                workbook.SaveAs(stream);
//                var content = stream.ToArray();
//                return File(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"AirIQ_Attempts_{sector}.xlsx");
//            }
//        }
//    }

//    private string GetExcelColumnValue(DataRow row, params string[] possibleNames)
//    {
//        // This strips hidden Byte Order Marks and spaces so headers map perfectly every time
//        foreach (DataColumn col in row.Table.Columns)
//        {
//            string cleanColName = col.ColumnName.Replace("\uFEFF", "").Trim().ToUpper();
//            foreach (var name in possibleNames)
//            {
//                if (cleanColName == name.ToUpper())
//                {
//                    if (row[col] != DBNull.Value) return row[col].ToString()?.Trim() ?? "";
//                }
//            }
//        }
//        return "";
//    }

//    //[HttpPost]
//    //public async Task<IActionResult> UploadAttempts(IFormFile file)
//    //{
//    //    if (file == null || file.Length == 0)
//    //        return Json(new { success = false, message = "No file selected or file is empty." });

//    //    var ext = Path.GetExtension(file.FileName).ToLower();
//    //    if (ext != ".csv" && ext != ".xls" && ext != ".xlsx")
//    //        return Json(new { success = false, message = "Please upload a valid .csv, .xls, or .xlsx file." });

//    //    try
//    //    {
//    //        var records = new List<UploadAttemptCsvRecord>();
//    //        var dbInsertList = new List<GroupRaisedEntryModel>();

//    //        if (ext == ".csv")
//    //        {
//    //            using (var reader = new StreamReader(file.OpenReadStream()))
//    //            using (var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture) { HasHeaderRecord = true, MissingFieldFound = null }))
//    //            {
//    //                csv.Context.RegisterClassMap<UploadAttemptCsvRecordMap>();
//    //                records = csv.GetRecords<UploadAttemptCsvRecord>().ToList();
//    //            }
//    //        }
//    //        else
//    //        {
//    //            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

//    //            using (var stream = file.OpenReadStream())
//    //            using (var reader = ExcelReaderFactory.CreateReader(stream))
//    //            {
//    //                var result = reader.AsDataSet(new ExcelDataSetConfiguration()
//    //                {
//    //                    ConfigureDataTable = (_) => new ExcelDataTableConfiguration() { UseHeaderRow = true }
//    //                });

//    //                var dt = result.Tables[0];

//    //                foreach (DataRow row in dt.Rows)
//    //                {
//    //                    records.Add(new UploadAttemptCsvRecord
//    //                    {
//    //                        AqId = GetExcelColumnValue(row, "AQ ID", "AqId"),
//    //                        GroupRaisedRefNo = GetExcelColumnValue(row, "Request Id", "Group Raised Ref Number", "GroupRaisedRefNo"),
//    //                        Origin = GetExcelColumnValue(row, "Org", "Origin"),
//    //                        Destination = GetExcelColumnValue(row, "Des", "Destination"),
//    //                        TravelDate = GetExcelColumnValue(row, "Travel Date", "TravelDate"),
//    //                        AirlineCode = GetExcelColumnValue(row, "Airline Code", "AirlineCode"),
//    //                        FlightNumber = GetExcelColumnValue(row, "Flight Number", "FlightNo"),
//    //                        Pax = GetExcelColumnValue(row, "No of Pax", "Pax", "RaisedPax"),
//    //                        AutoQuote = GetExcelColumnValue(row, "Auto Quote", "AutoQuote"),
//    //                        Status = GetExcelColumnValue(row, "Status"),
//    //                        NegotiatedFare = GetExcelColumnValue(row, "Negotiated Fare", "NegotiatedFare"),
//    //                        ConfirmedStatus = GetExcelColumnValue(row, "Confirmation Status", "Confirmed Status"),
//    //                        Remarks = GetExcelColumnValue(row, "Remarks"),
//    //                        PNR = GetExcelColumnValue(row, "PNR"),
//    //                        OpenRemarks = GetExcelColumnValue(row, "Open Remarks", "OpenRemarks"),
//    //                        Lead = GetExcelColumnValue(row, "Lead")
//    //                    });
//    //                }
//    //            }
//    //        }

//    //        _cache.TryGetValue("FDestList", out List<FDestData> cachedFDest);
//    //        cachedFDest ??= new List<FDestData>();
//    //        var today = DateTime.Now;

//    //        var uniqueApiRequests = records
//    //            .Where(r => !string.IsNullOrEmpty(r.Origin) && !string.IsNullOrEmpty(r.Destination))
//    //            .Select(r => new
//    //            {
//    //                Sector = $"{r.Origin?.Trim()}-{r.Destination?.Trim()}",
//    //                TravelDate = DateTime.TryParse(r.TravelDate, out DateTime d) && d.Year >= 2000 ? d.Date : DateTime.Today
//    //            })
//    //            .Distinct()
//    //            .ToList();

//    //        var apiCache = new System.Collections.Concurrent.ConcurrentDictionary<string, JsonElement>();

//    //        using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
//    //        var semaphore = new SemaphoreSlim(10);

//    //        var fetchTasks = uniqueApiRequests.Select(async req =>
//    //        {
//    //            string cacheKey = $"{req.Sector}_{req.TravelDate:MM/dd/yyyy}";
//    //            await semaphore.WaitAsync();
//    //            try
//    //            {
//    //                var requestBody = new { Sectors = req.Sector, FlightDate = req.TravelDate.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture) };
//    //                var content = new StringContent(JsonSerializer.Serialize(requestBody), System.Text.Encoding.UTF8, "application/json");

//    //                var response = await httpClient.PostAsync("http://13.203.38.118:8080/ccloop", content);
//    //                if (response.IsSuccessStatusCode)
//    //                {
//    //                    var jsonResp = await response.Content.ReadAsStringAsync();
//    //                    using var doc = JsonDocument.Parse(jsonResp);
//    //                    if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
//    //                    {
//    //                        var firstObj = doc.RootElement[0];
//    //                        if (firstObj.TryGetProperty("flights", out JsonElement flights) && flights.ValueKind == JsonValueKind.Array)
//    //                        {
//    //                            apiCache[cacheKey] = flights.Clone();
//    //                        }
//    //                    }
//    //                }
//    //            }
//    //            catch (Exception ex) { Console.WriteLine($"API Fetch Error for {cacheKey}: {ex.Message}"); }
//    //            finally { semaphore.Release(); }
//    //        });

//    //        await Task.WhenAll(fetchTasks);

//    //        var dbFeedCache = new Dictionary<string, int>();

//    //        foreach (var rec in records)
//    //        {
//    //            if (string.IsNullOrEmpty(rec.Origin) && string.IsNullOrEmpty(rec.Destination)) continue;

//    //            string cleanAqId = new String(rec.AqId?.Where(Char.IsDigit).ToArray());
//    //            int.TryParse(cleanAqId, out int parsedAqId);

//    //            if (!DateTime.TryParse(rec.TravelDate, out DateTime parsedTravelDate) || parsedTravelDate.Year < 2000)
//    //            {
//    //                parsedTravelDate = DateTime.Today;
//    //            }
//    //            string calculatedSector = $"{rec.Origin?.Trim()}-{rec.Destination?.Trim()}";

//    //            decimal liveAmount = 0;
//    //            string depTime = "00:00:00";
//    //            string arrTime = "00:00:00";

//    //            string cacheKey = $"{calculatedSector}_{parsedTravelDate:MM/dd/yyyy}";

//    //            if (apiCache.TryGetValue(cacheKey, out JsonElement flightArray))
//    //            {
//    //                string targetAirline = rec.AirlineCode?.Trim() ?? "";
//    //                string targetFlightNum = new String(rec.FlightNumber?.Where(Char.IsDigit).ToArray()) ?? "";

//    //                foreach (var f in flightArray.EnumerateArray())
//    //                {
//    //                    string apiAirline = f.TryGetProperty("AirlineCode", out var airProp) ? airProp.ToString().Trim() : "";
//    //                    string rawApiFlightNum = f.TryGetProperty("FlightNumber", out var fnProp) ? fnProp.ToString() : "";
//    //                    string apiFlightNum = new String(rawApiFlightNum.Where(Char.IsDigit).ToArray());

//    //                    if (string.Equals(apiAirline, targetAirline, StringComparison.OrdinalIgnoreCase) && apiFlightNum == targetFlightNum)
//    //                    {
//    //                        double baseAmt = 0;
//    //                        if (f.TryGetProperty("BaseAmount", out var baseEl) && baseEl.ValueKind == JsonValueKind.Number) baseAmt = baseEl.GetDouble();

//    //                        double taxAmt = 0;
//    //                        if (f.TryGetProperty("TotalTaxAmount", out var taxEl) && taxEl.ValueKind == JsonValueKind.Number) taxAmt = taxEl.GetDouble();

//    //                        liveAmount = (decimal)(baseAmt + taxAmt);

//    //                        if (f.TryGetProperty("DepartureDateTime", out var depEl) && DateTime.TryParse(depEl.GetString(), out DateTime dDate)) depTime = dDate.ToString("HH:mm:ss");
//    //                        if (f.TryGetProperty("ArrivalDateTime", out var arrEl) && DateTime.TryParse(arrEl.GetString(), out DateTime aDate)) arrTime = aDate.ToString("HH:mm:ss");

//    //                        break;
//    //                    }
//    //                }
//    //            }

//    //            string feedCacheKey = $"{calculatedSector}_{parsedTravelDate:MM/dd/yyyy}_{rec.AirlineCode?.Trim()}_{rec.FlightNumber?.Trim()}";
//    //            int feedId = 0;

//    //            if (dbFeedCache.ContainsKey(feedCacheKey))
//    //            {
//    //                feedId = dbFeedCache[feedCacheKey];
//    //            }
//    //            else
//    //            {
//    //                feedId = await _dbHelper.GetOrInsertBlackoutFareFeedIdAsync(calculatedSector, parsedTravelDate, rec.AirlineCode?.Trim(), rec.FlightNumber?.Trim(), liveAmount, arrTime, depTime);
//    //                dbFeedCache[feedCacheKey] = feedId;
//    //            }

//    //            var destMatch = cachedFDest.FirstOrDefault(d =>
//    //                string.Equals(d?.FromCode, rec.Origin?.Trim(), StringComparison.OrdinalIgnoreCase) &&
//    //                string.Equals(d?.ToCode, rec.Destination?.Trim(), StringComparison.OrdinalIgnoreCase));

//    //            int.TryParse(rec.Pax?.Replace(",", ""), out int paxCount);
//    //            decimal.TryParse(rec.AutoQuote?.Replace("₹", "").Replace(",", ""), out decimal autoQuoteFare);
//    //            decimal.TryParse(rec.NegotiatedFare?.Replace("₹", "").Replace(",", ""), out decimal negFare);

//    //            string mappedStatus = "";
//    //            string rawStatus = rec.Status?.ToUpper() ?? "";
//    //            if (rawStatus.Contains("PNR CREATED")) mappedStatus = "PNR CREATED";
//    //            else if (rawStatus.Contains("REJECTED")) mappedStatus = "REJECTED";
//    //            else if (rawStatus.Contains("NEGOTIATION SENT")) mappedStatus = "NEGOTIATION SENT";

//    //            dbInsertList.Add(new GroupRaisedEntryModel
//    //            {
//    //                GroupRaisedEntryID = parsedAqId > 0 ? parsedAqId : (int?)null,
//    //                BlackoutFareFeedID = feedId > 0 ? feedId : null,
//    //                GroupRaisedRefNo = rec.GroupRaisedRefNo,
//    //                PNR = rec.PNR,
//    //                RaisedPax = paxCount,
//    //                GroupFare = autoQuoteFare,
//    //                UserName = destMatch?.InternalOwner ?? rec.Lead,
//    //                Status = mappedStatus!,
//    //                FDestID = destMatch?.FDestID,
//    //                RaisedDate = today,
//    //                EntryDate = today,
//    //                Remarks = rec.Remarks,
//    //                OpenRemarks = rec.OpenRemarks,
//    //                ConfirmationStatus = rec.ConfirmedStatus,
//    //                NegotiatedFare = negFare,
//    //                ModifiedDate = today
//    //            });
//    //        }

//    //        bool isSuccess = await _dbHelper.BulkInsertGroupRaisedEntriesAsync(dbInsertList);

//    //        if (isSuccess)
//    //            return Json(new { success = true, message = $"Successfully imported {dbInsertList.Count} records." });
//    //        else
//    //            return Json(new { success = false, message = "Database insertion failed. Check server logs." });
//    //    }
//    //    catch (Exception ex)
//    //    {
//    //        return Json(new { success = false, message = "Error processing file: " + ex.Message });
//    //    }
//    //}

//    [HttpPost]
//    public async Task<IActionResult> UploadAttempts(IFormFile file)
//    {
//        if (file == null || file.Length == 0)
//            return Json(new { success = false, message = "No file selected or file is empty." });

//        var ext = Path.GetExtension(file.FileName).ToLower();
//        if (ext != ".csv" && ext != ".xls" && ext != ".xlsx")
//            return Json(new { success = false, message = "Please upload a valid .csv, .xls, or .xlsx file." });

//        try
//        {
//            var records = new List<UploadAttemptCsvRecord>();
//            var dbInsertList = new List<GroupRaisedEntryModel>();

//            // --- 1. FILE PARSING ---
//            if (ext == ".csv")
//            {
//                using (var reader = new StreamReader(file.OpenReadStream()))
//                using (var csv = new CsvReader(reader, new CsvConfiguration(CultureInfo.InvariantCulture) { HasHeaderRecord = true, MissingFieldFound = null }))
//                {
//                    csv.Context.RegisterClassMap<UploadAttemptCsvRecordMap>();
//                    records = csv.GetRecords<UploadAttemptCsvRecord>().ToList();
//                }
//            }
//            else
//            {
//                System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);

//                using (var stream = file.OpenReadStream())
//                using (var reader = ExcelReaderFactory.CreateReader(stream))
//                {
//                    var result = reader.AsDataSet(new ExcelDataSetConfiguration()
//                    {
//                        ConfigureDataTable = (_) => new ExcelDataTableConfiguration() { UseHeaderRow = true }
//                    });

//                    var dt = result.Tables[0];

//                    foreach (DataRow row in dt.Rows)
//                    {
//                        records.Add(new UploadAttemptCsvRecord
//                        {
//                            AqId = GetExcelColumnValue(row, "AQ ID", "AqId"),
//                            GroupRaisedRefNo = GetExcelColumnValue(row, "Request Id", "Group Raised Ref Number", "GroupRaisedRefNo"),
//                            Origin = GetExcelColumnValue(row, "Org", "Origin"),
//                            Destination = GetExcelColumnValue(row, "Des", "Destination"),
//                            TravelDate = GetExcelColumnValue(row, "Travel Date", "TravelDate"),
//                            AirlineCode = GetExcelColumnValue(row, "Airline Code", "AirlineCode"),
//                            FlightNumber = GetExcelColumnValue(row, "Flight Number", "FlightNo"),
//                            Pax = GetExcelColumnValue(row, "No of Pax", "Pax", "RaisedPax"),
//                            AutoQuote = GetExcelColumnValue(row, "Auto Quote", "AutoQuote"),
//                            Status = GetExcelColumnValue(row, "Status"),
//                            NegotiatedFare = GetExcelColumnValue(row, "Negotiated Fare", "NegotiatedFare"),
//                            ConfirmedStatus = GetExcelColumnValue(row, "Confirmation Status", "Confirmed Status"),
//                            Remarks = GetExcelColumnValue(row, "Remarks"),
//                            PNR = GetExcelColumnValue(row, "PNR"),
//                            OpenRemarks = GetExcelColumnValue(row, "Open Remarks", "OpenRemarks"),
//                            Lead = GetExcelColumnValue(row, "Lead")
//                        });
//                    }
//                }
//            }

//            _cache.TryGetValue("FDestList", out List<FDestData> cachedFDest);
//            cachedFDest ??= new List<FDestData>();
//            var today = DateTime.Now;

//            // --- 2. EXTRACT UNIQUE SECTORS & DATES ---
//            var uniqueApiRequests = records
//                .Where(r => !string.IsNullOrEmpty(r.Origin) && !string.IsNullOrEmpty(r.Destination))
//                .Select(r => new
//                {
//                    Sector = $"{r.Origin?.Trim()}-{r.Destination?.Trim()}",
//                    TravelDate = DateTime.TryParse(r.TravelDate, out DateTime d) && d.Year >= 2000 ? d.Date : DateTime.Today
//                })
//                .Distinct()
//                .ToList();

//            // --- 3. BATCH SECTORS FOR API CALL ---
//            var apiBatches = new List<(string Sectors, DateTime TravelDate)>();
//            var groupedByDate = uniqueApiRequests.GroupBy(r => r.TravelDate);

//            foreach (var dateGroup in groupedByDate)
//            {
//                var delSectors = dateGroup.Where(r => r.Sector.Contains("DEL", StringComparison.OrdinalIgnoreCase)).Select(r => r.Sector).ToList();
//                var otherSectors = dateGroup.Where(r => !r.Sector.Contains("DEL", StringComparison.OrdinalIgnoreCase)).Select(r => r.Sector).ToList();

//                // Process DEL sectors one by one
//                foreach (var sec in delSectors)
//                {
//                    apiBatches.Add((sec, dateGroup.Key));
//                }

//                // Process all other sectors in batches of 10
//                for (int i = 0; i < otherSectors.Count; i += 10)
//                {
//                    apiBatches.Add((string.Join(",", otherSectors.Skip(i).Take(10)), dateGroup.Key));
//                }
//            }

//            // Using ConcurrentBag to safely store flights from batched responses
//            var flightCache = new System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentBag<JsonElement>>();

//            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
//            var semaphore = new SemaphoreSlim(10);

//            // --- 4. EXECUTE BATCHED API REQUESTS ---
//            var fetchTasks = apiBatches.Select(async batch =>
//            {
//                await semaphore.WaitAsync();
//                try
//                {
//                    var requestBody = new { Sectors = batch.Sectors, FlightDate = batch.TravelDate.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture) };
//                    var content = new StringContent(JsonSerializer.Serialize(requestBody), System.Text.Encoding.UTF8, "application/json");

//                    var response = await httpClient.PostAsync("http://13.203.38.118:8080/ccloop", content);
//                    if (response.IsSuccessStatusCode)
//                    {
//                        var jsonResp = await response.Content.ReadAsStringAsync();
//                        using var doc = JsonDocument.Parse(jsonResp);

//                        if (doc.RootElement.ValueKind == JsonValueKind.Array)
//                        {
//                            foreach (var rootObj in doc.RootElement.EnumerateArray())
//                            {
//                                if (rootObj.TryGetProperty("flights", out JsonElement flights) && flights.ValueKind == JsonValueKind.Array)
//                                {
//                                    // Map the batched flights back to their specific origin/destination keys
//                                    foreach (var f in flights.EnumerateArray())
//                                    {
//                                        string org = f.TryGetProperty("Origin", out var o) ? o.GetString() : "";
//                                        string des = f.TryGetProperty("Destination", out var d) ? d.GetString() : "";

//                                        if (!string.IsNullOrEmpty(org) && !string.IsNullOrEmpty(des))
//                                        {
//                                            string flightSector = $"{org}-{des}";
//                                            string cacheKey = $"{flightSector}_{batch.TravelDate:MM/dd/yyyy}";

//                                            var bag = flightCache.GetOrAdd(cacheKey, _ => new System.Collections.Concurrent.ConcurrentBag<JsonElement>());
//                                            bag.Add(f.Clone());
//                                        }
//                                    }
//                                }
//                            }
//                        }
//                    }
//                }
//                catch (Exception ex) { Console.WriteLine($"API Fetch Error for {batch.Sectors}: {ex.Message}"); }
//                finally { semaphore.Release(); }
//            });

//            await Task.WhenAll(fetchTasks);

//            var dbFeedCache = new Dictionary<string, int>();

//            // --- 5. COMPILE DATABASE ENTRIES ---
//            foreach (var rec in records)
//            {
//                if (string.IsNullOrEmpty(rec.Origin) && string.IsNullOrEmpty(rec.Destination)) continue;

//                string cleanAqId = new String(rec.AqId?.Where(Char.IsDigit).ToArray());
//                int.TryParse(cleanAqId, out int parsedAqId);

//                if (!DateTime.TryParse(rec.TravelDate, out DateTime parsedTravelDate) || parsedTravelDate.Year < 2000)
//                {
//                    parsedTravelDate = DateTime.Today;
//                }
//                string calculatedSector = $"{rec.Origin?.Trim()}-{rec.Destination?.Trim()}";

//                decimal liveAmount = 0;
//                string depTime = "00:00:00";
//                string arrTime = "00:00:00";

//                string cacheKey = $"{calculatedSector}_{parsedTravelDate:MM/dd/yyyy}";

//                // Extract properties safely from our un-batched dictionary
//                if (flightCache.TryGetValue(cacheKey, out var flightArray))
//                {
//                    string targetAirline = rec.AirlineCode?.Trim() ?? "";
//                    string targetFlightNum = new String(rec.FlightNumber?.Where(Char.IsDigit).ToArray()) ?? "";

//                    foreach (var f in flightArray)
//                    {
//                        string apiAirline = f.TryGetProperty("AirlineCode", out var airProp) ? airProp.ToString().Trim() : "";
//                        string rawApiFlightNum = f.TryGetProperty("FlightNumber", out var fnProp) ? fnProp.ToString() : "";
//                        string apiFlightNum = new String(rawApiFlightNum.Where(Char.IsDigit).ToArray());

//                        if (string.Equals(apiAirline, targetAirline, StringComparison.OrdinalIgnoreCase) && apiFlightNum == targetFlightNum)
//                        {
//                            double baseAmt = 0;
//                            if (f.TryGetProperty("BaseAmount", out var baseEl) && baseEl.ValueKind == JsonValueKind.Number) baseAmt = baseEl.GetDouble();

//                            double taxAmt = 0;
//                            if (f.TryGetProperty("TotalTaxAmount", out var taxEl) && taxEl.ValueKind == JsonValueKind.Number) taxAmt = taxEl.GetDouble();

//                            liveAmount = (decimal)(baseAmt + taxAmt);

//                            // Format strict to 24-hour numerical strings mapped with AM/PM
//                            if (f.TryGetProperty("DepartureDateTime", out var depEl) && DateTime.TryParse(depEl.GetString(), out DateTime dDate))
//                                depTime = dDate.ToString("HH:mm tt");

//                            if (f.TryGetProperty("ArrivalDateTime", out var arrEl) && DateTime.TryParse(arrEl.GetString(), out DateTime aDate))
//                                arrTime = aDate.ToString("HH:mm tt");

//                            break;
//                        }
//                    }
//                }

//                string feedCacheKey = $"{calculatedSector}_{parsedTravelDate:MM/dd/yyyy}_{rec.AirlineCode?.Trim()}_{rec.FlightNumber?.Trim()}";
//                int feedId = 0;

//                if (dbFeedCache.ContainsKey(feedCacheKey))
//                {
//                    feedId = dbFeedCache[feedCacheKey];
//                }
//                else
//                {
//                    feedId = await _dbHelper.GetOrInsertBlackoutFareFeedIdAsync(calculatedSector, parsedTravelDate, rec.AirlineCode?.Trim(), rec.FlightNumber?.Trim(), liveAmount, arrTime, depTime);
//                    dbFeedCache[feedCacheKey] = feedId;
//                }

//                var destMatch = cachedFDest.FirstOrDefault(d =>
//                    string.Equals(d?.FromCode, rec.Origin?.Trim(), StringComparison.OrdinalIgnoreCase) &&
//                    string.Equals(d?.ToCode, rec.Destination?.Trim(), StringComparison.OrdinalIgnoreCase));

//                int.TryParse(rec.Pax?.Replace(",", ""), out int paxCount);
//                decimal.TryParse(rec.AutoQuote?.Replace("₹", "").Replace(",", ""), out decimal autoQuoteFare);
//                decimal.TryParse(rec.NegotiatedFare?.Replace("₹", "").Replace(",", ""), out decimal negFare);

//                string mappedStatus = "";
//                string rawStatus = rec.Status?.ToUpper() ?? "";
//                if (rawStatus.Contains("PNR CREATED")) mappedStatus = "PNR CREATED";
//                else if (rawStatus.Contains("REJECTED")) mappedStatus = "REJECTED";
//                else if (rawStatus.Contains("NEGOTIATION SENT")) mappedStatus = "NEGOTIATION SENT";

//                dbInsertList.Add(new GroupRaisedEntryModel
//                {
//                    GroupRaisedEntryID = parsedAqId > 0 ? parsedAqId : (int?)null,
//                    BlackoutFareFeedID = feedId > 0 ? feedId : null,
//                    GroupRaisedRefNo = rec.GroupRaisedRefNo,
//                    PNR = rec.PNR,
//                    RaisedPax = paxCount,
//                    GroupFare = autoQuoteFare,
//                    UserName = destMatch?.InternalOwner ?? rec.Lead,
//                    Status = mappedStatus!,
//                    FDestID = destMatch?.FDestID,
//                    RaisedDate = today,
//                    EntryDate = today,
//                    Remarks = rec.Remarks,
//                    OpenRemarks = rec.OpenRemarks,
//                    ConfirmationStatus = rec.ConfirmedStatus,
//                    NegotiatedFare = negFare,
//                    ModifiedDate = today
//                });
//            }

//            // --- 6. DATABASE COMMIT ---
//            bool isSuccess = await _dbHelper.BulkInsertGroupRaisedEntriesAsync(dbInsertList);

//            // --> ADD THIS NEW LOGGING BLOCK HERE <--
//            var sessionIdClaim = User.Claims.FirstOrDefault(c => c.Type == "SessionId")?.Value;
//            Guid? sessionId = Guid.TryParse(sessionIdClaim, out Guid sid) ? sid : null;

//            var uploadLog = new UserUploadLog
//            {
//                SessionId = sessionId,
//                Username = User.Identity?.Name ?? "Unknown",
//                FileName = file.FileName,
//                FileExtension = ext,
//                FileSizeBytes = file.Length,
//                RecordsParsed = dbInsertList.Count,
//                IsSuccessful = isSuccess,
//                ErrorMessage = isSuccess ? null : "Database insertion failed during bulk operation.",
//                Timestamp = DateTime.UtcNow
//            };
//            await _dbHelper.InsertUserUploadLogAsync(uploadLog);

//            if (isSuccess)
//                return Json(new { success = true, message = $"Successfully imported {dbInsertList.Count} records." });
//            else
//                return Json(new { success = false, message = "Database insertion failed. Check server logs." });
//        }
//        catch (Exception ex)
//        {
//            var sessionIdClaim = User.Claims.FirstOrDefault(c => c.Type == "SessionId")?.Value;
//            Guid? sessionId = Guid.TryParse(sessionIdClaim, out Guid sid) ? sid : null;

//            var uploadLog = new UserUploadLog
//            {
//                SessionId = sessionId,
//                Username = User.Identity?.Name ?? "Unknown",
//                FileName = file.FileName,
//                FileExtension = ext,
//                FileSizeBytes = file.Length,
//                RecordsParsed = 0,
//                IsSuccessful = false,
//                ErrorMessage = ex.Message,
//                Timestamp = DateTime.UtcNow
//            };
//            await _dbHelper.InsertUserUploadLogAsync(uploadLog);
//            return Json(new { success = false, message = "Error processing file: " + ex.Message });
//        }
//    }

//    private Dictionary<int, string> BuildOccasionMap(List<AttemptRecord> records)
//    {
//        if (!_cache.TryGetValue("MasterOccasionMap", out Dictionary<string, string> sectorDateToEvent))
//        {
//            sectorDateToEvent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
//            var allRawEvents = new List<EventListData>();
//            var allDashEvents = new List<DashboardUiEvent>();

//            foreach (var yr in new[] { 2024, 2025, 2026, 2027, 2028 })
//            {
//                string key = yr == 2026 ? "RawEventListData" : $"HistoricalRawEventListData_{yr}";
//                if (_cache.TryGetValue(key, out List<EventListData> rawList) && rawList != null)
//                    allRawEvents.AddRange(rawList);

//                string dashKey = yr == 2026 ? "DashboardMasterData" : $"HistoricalDashboardMasterData_{yr}";
//                if (_cache.TryGetValue(dashKey, out List<DashboardUiEvent> dashList) && dashList != null)
//                    allDashEvents.AddRange(dashList);
//            }

//            foreach (var rawEv in allRawEvents)
//            {
//                var dashEv = allDashEvents.FirstOrDefault(d => d.Id == rawEv.EventId);
//                string evName = dashEv?.Name ?? rawEv.Event ?? "Unknown Occasion";

//                if (rawEv.Details != null)
//                {
//                    foreach (var sec in rawEv.Details)
//                    {
//                        string sector = $"{sec.FromCode}-{sec.ToCode}";
//                        var dates = new HashSet<DateTime>();
//                        if (sec.DateRange != null) foreach (var d in sec.DateRange) dates.Add(d.Date);
//                        if (sec.Details != null) foreach (var d in sec.Details) dates.Add(d.TravelDate.Date);

//                        foreach (var d in dates)
//                        {
//                            string k = $"{sector}|{d:yyyy-MM-dd}";
//                            if (!sectorDateToEvent.ContainsKey(k))
//                                sectorDateToEvent[k] = evName;
//                            else if (!sectorDateToEvent[k].Contains(evName))
//                                sectorDateToEvent[k] += " / " + evName;
//                        }
//                    }
//                }
//            }

//            _cache.Set("MasterOccasionMap", sectorDateToEvent, TimeSpan.FromMinutes(30));
//        }

//        var map = new Dictionary<int, string>();
//        foreach (var rec in records)
//        {
//            string sector = $"{rec.Origin?.Trim()}-{rec.Destination?.Trim()}";
//            string key = $"{sector}|{rec.RawTravelDate:yyyy-MM-dd}";
//            map[rec.Id] = sectorDateToEvent!.TryGetValue(key, out string ev) ? ev : "-";
//        }

//        return map;
//    }

//    [HttpGet]
//    public async Task<IActionResult> AllAttempts(string fromDate, string toDate, string travelDate)
//    {
//        DateTime? entryStart = null;
//        DateTime? entryEnd = null;
//        DateTime? specificTravel = null;

//        if (!string.IsNullOrEmpty(travelDate) && DateTime.TryParse(travelDate, out DateTime pTravel))
//        {
//            specificTravel = pTravel;
//            ViewBag.TravelDate = specificTravel.Value.ToString("yyyy-MM-dd");
//            ViewBag.FromDate = "";
//            ViewBag.ToDate = "";
//        }
//        else if (!string.IsNullOrEmpty(fromDate) && !string.IsNullOrEmpty(toDate))
//        {
//            if (DateTime.TryParse(fromDate, out DateTime pFrom)) entryStart = pFrom;
//            if (DateTime.TryParse(toDate, out DateTime pTo)) entryEnd = pTo;
//            ViewBag.FromDate = entryStart?.ToString("yyyy-MM-dd");
//            ViewBag.ToDate = entryEnd?.ToString("yyyy-MM-dd");
//            ViewBag.TravelDate = "";
//        }
//        else
//        {
//            entryStart = DateTime.Today;
//            entryEnd = DateTime.Today;
//            ViewBag.FromDate = entryStart?.ToString("yyyy-MM-dd");
//            ViewBag.ToDate = entryEnd?.ToString("yyyy-MM-dd");
//            ViewBag.TravelDate = "";
//        }

//        var allRecords = await _dbHelper.GetAllAttemptDetailsAsync(entryStart, entryEnd, specificTravel);

//        _cache.TryGetValue("FDestList", out List<FDestData> cachedFDest);
//        cachedFDest ??= new List<FDestData>();

//        var destDict = cachedFDest
//            .Where(f => !string.IsNullOrEmpty(f.FromCode) && !string.IsNullOrEmpty(f.ToCode))
//            .GroupBy(f => $"{f.FromCode.Trim()}-{f.ToCode.Trim()}".ToUpper())
//            .ToDictionary(g => g.Key, g => g.First().InternalOwner);

//        foreach (var rec in allRecords)
//        {
//            string lookupKey = $"{rec.Origin?.Trim()}-{rec.Destination?.Trim()}".ToUpper();

//            if (destDict.TryGetValue(lookupKey, out string internalOwner) && !string.IsNullOrEmpty(internalOwner))
//            {
//                rec.Lead = internalOwner;
//            }
//            else if (string.IsNullOrEmpty(rec.Lead))
//            {
//                rec.Lead = "Unassigned";
//            }
//        }

//        ViewBag.OccasionMap = BuildOccasionMap(allRecords);

//        return View(allRecords);
//    }

//    [HttpGet]
//    public async Task<IActionResult> ExportAllAttemptsToExcel(
// string fromDate, string toDate, string travelDate,
// string fSector, string fStation, string fAirline,
// string fTravelDate, string fStatus, string fConfStatus,
// string fLead, string fFlightNo, string fMonth)
//    {
//        DateTime? entryStart = null;
//        DateTime? entryEnd = null;
//        DateTime? specificTravel = null;

//        if (!string.IsNullOrEmpty(travelDate) && DateTime.TryParse(travelDate, out DateTime pTravel))
//        {
//            specificTravel = pTravel;
//        }
//        else if (!string.IsNullOrEmpty(fromDate) && !string.IsNullOrEmpty(toDate))
//        {
//            if (DateTime.TryParse(fromDate, out DateTime pFrom)) entryStart = pFrom;
//            if (DateTime.TryParse(toDate, out DateTime pTo)) entryEnd = pTo;
//        }
//        else
//        {
//            entryStart = DateTime.Today;
//            entryEnd = DateTime.Today;
//        }

//        var attemptRecords = await _dbHelper.GetAllAttemptDetailsAsync(entryStart, entryEnd, specificTravel);

//        _cache.TryGetValue("FDestList", out List<FDestData> cachedFDest);
//        cachedFDest ??= new List<FDestData>();

//        var destDict = cachedFDest
//            .Where(f => !string.IsNullOrEmpty(f.FromCode) && !string.IsNullOrEmpty(f.ToCode))
//            .GroupBy(f => $"{f.FromCode.Trim()}-{f.ToCode.Trim()}".ToUpper())
//            .ToDictionary(g => g.Key, g => g.First().InternalOwner);

//        foreach (var rec in attemptRecords)
//        {
//            string lookupKey = $"{rec.Origin?.Trim()}-{rec.Destination?.Trim()}".ToUpper();

//            if (destDict.TryGetValue(lookupKey, out string internalOwner) && !string.IsNullOrEmpty(internalOwner))
//            {
//                rec.Lead = internalOwner;
//            }
//            else if (string.IsNullOrEmpty(rec.Lead))
//            {
//                rec.Lead = "Unassigned";
//            }
//        }

//        // Filter Logic
//        if (!string.IsNullOrEmpty(fSector)) attemptRecords = attemptRecords.Where(r => $"{r.Origin?.Trim()}-{r.Destination?.Trim()}".Equals(fSector, StringComparison.OrdinalIgnoreCase)).ToList();
//        if (!string.IsNullOrEmpty(fStation)) attemptRecords = attemptRecords.Where(r => string.Equals(r.Origin?.Trim(), fStation, StringComparison.OrdinalIgnoreCase) || string.Equals(r.Destination?.Trim(), fStation, StringComparison.OrdinalIgnoreCase)).ToList();
//        if (!string.IsNullOrEmpty(fAirline)) attemptRecords = attemptRecords.Where(r => string.Equals(r.AirlineCode?.Trim(), fAirline, StringComparison.OrdinalIgnoreCase)).ToList();
//        if (!string.IsNullOrEmpty(fTravelDate)) attemptRecords = attemptRecords.Where(r => r.TravelDate == fTravelDate).ToList();
//        if (!string.IsNullOrEmpty(fStatus)) attemptRecords = attemptRecords.Where(r => { string recStatus = r.Status?.ToUpper() ?? "PENDING"; if (recStatus != "PNR CREATED" && recStatus != "REJECTED" && recStatus != "NEGOTIATION SENT") recStatus = "PENDING"; return string.Equals(recStatus, fStatus, StringComparison.OrdinalIgnoreCase); }).ToList();
//        if (!string.IsNullOrEmpty(fConfStatus)) attemptRecords = attemptRecords.Where(r => { string recConf = r.ConfStatus?.ToUpper() ?? ""; if (recConf != "YES" && recConf != "NO") recConf = ""; return string.Equals(recConf, fConfStatus, StringComparison.OrdinalIgnoreCase); }).ToList();
//        if (!string.IsNullOrEmpty(fLead)) attemptRecords = attemptRecords.Where(r => string.Equals(r.Lead, fLead, StringComparison.OrdinalIgnoreCase)).ToList();
//        if (!string.IsNullOrEmpty(fFlightNo)) attemptRecords = attemptRecords.Where(r => r.FlightCode != null && r.FlightCode.Contains(fFlightNo, StringComparison.OrdinalIgnoreCase)).ToList();
//        if (!string.IsNullOrEmpty(fMonth)) attemptRecords = attemptRecords.Where(r => { if (DateTime.TryParse(r.TravelDate, out DateTime dt)) return string.Equals(dt.ToString("MMMM"), fMonth, StringComparison.OrdinalIgnoreCase); return false; }).ToList();

//        var occasionMap = BuildOccasionMap(attemptRecords);

//        using (var workbook = new XLWorkbook())
//        {
//            var worksheet = workbook.Worksheets.Add("All Attempts");

//            string[] headers = { "AQ ID", "Occasion Name", "Entry Date", "Request Id", "Org", "Des", "Travel Date", "Airline Code", "Flight Number",
//                       "No of Pax", "Auto Quote", "Status", "Negotiated Fare", "Confirmation Status",
//                       "Remarks", "PNR", "Open Remarks", "Lead" };

//            for (int i = 0; i < headers.Length; i++)
//            {
//                var cell = worksheet.Cell(1, i + 1);
//                cell.Value = headers[i];
//                cell.Style.Font.Bold = true;
//                cell.Style.Fill.BackgroundColor = XLColor.LightGray;
//            }

//            int row = 2;
//            foreach (var rec in attemptRecords)
//            {
//                worksheet.Cell(row, 1).Value = rec.AqId ?? "";
//                worksheet.Cell(row, 2).Value = occasionMap.ContainsKey(rec.Id) ? occasionMap[rec.Id] : "-";
//                worksheet.Cell(row, 3).Value = rec.RawEntryDate.ToString("dd MMM yyyy");
//                worksheet.Cell(row, 4).Value = rec.ReqId ?? "";
//                worksheet.Cell(row, 5).Value = rec.Origin ?? "";
//                worksheet.Cell(row, 6).Value = rec.Destination ?? "";
//                worksheet.Cell(row, 7).Value = rec.RawTravelDate.ToString("dd MMM yyyy");
//                worksheet.Cell(row, 8).Value = rec.AirlineCode ?? "";

//                string fCode = rec.FlightCode ?? "";
//                string aCode = rec.AirlineCode ?? "";
//                if (!string.IsNullOrEmpty(aCode)) { fCode = fCode.Replace(aCode, "").Trim(); }

//                worksheet.Cell(row, 9).Value = fCode;
//                worksheet.Cell(row, 10).Value = rec.Pax;
//                worksheet.Cell(row, 11).Value = rec.AutoQuote?.Replace("₹", "").Replace(",", "") ?? "";
//                worksheet.Cell(row, 12).Value = rec.Status ?? "";
//                worksheet.Cell(row, 13).Value = rec.NegFare?.Replace("₹", "").Replace(",", "") ?? "";
//                worksheet.Cell(row, 14).Value = rec.ConfStatus ?? "";
//                worksheet.Cell(row, 15).Value = rec.Remarks ?? "";
//                worksheet.Cell(row, 16).Value = rec.PNR ?? "";
//                worksheet.Cell(row, 17).Value = rec.OpenRemarks ?? "";
//                worksheet.Cell(row, 18).Value = rec.Lead ?? "";
//                row++;
//            }

//            var hiddenSheet = workbook.Worksheets.Add("ValidationData");
//            hiddenSheet.Hide();

//            hiddenSheet.Cell("A1").Value = "NEGOTIATION SENT";
//            hiddenSheet.Cell("A2").Value = "QUOTATION PENDING";
//            hiddenSheet.Cell("A3").Value = "REQUEST REJECTED";
//            hiddenSheet.Cell("A4").Value = "SELF REJECTED";
//            hiddenSheet.Cell("A5").Value = "PNR CREATED";
//            hiddenSheet.Cell("B1").Value = "YES";
//            hiddenSheet.Cell("B2").Value = "NO";
//            hiddenSheet.Cell("C1").Value = "PNR CREATED";
//            hiddenSheet.Cell("C2").Value = "NEGOTIATION REJECTED";
//            hiddenSheet.Cell("C3").Value = "REQUEST REJECTED";
//            hiddenSheet.Cell("C4").Value = "SELF REJECTED";
//            hiddenSheet.Cell("C5").Value = "NEGOTIATION SATISFIED";
//            hiddenSheet.Cell("C6").Value = "NEGOTIATION NOT SATISFIED";
//            hiddenSheet.Cell("C7").Value = "NEGOTIATION PENDING";

//            var statusRange = worksheet.Range(2, 12, 10000, 12);
//            statusRange.SetDataValidation().List("=ValidationData!$A$1:$A$5", true);
//            statusRange.SetDataValidation().IgnoreBlanks = true;

//            var confStatusRange = worksheet.Range(2, 14, 10000, 14);
//            confStatusRange.SetDataValidation().List("=ValidationData!$B$1:$B$2", true);
//            confStatusRange.SetDataValidation().IgnoreBlanks = true;

//            var remarksRange = worksheet.Range(2, 15, 10000, 15);
//            remarksRange.SetDataValidation().List("=ValidationData!$C$1:$C$7", true);
//            remarksRange.SetDataValidation().IgnoreBlanks = true;

//            worksheet.Columns().AdjustToContents();

//            using (var stream = new MemoryStream())
//            {
//                workbook.SaveAs(stream);
//                var content = stream.ToArray();
//                return File(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"AirIQ_Master_All_Attempts.xlsx");
//            }
//        }
//    }


//    [HttpGet]
//    [Route("OccasionDetails/TestTripforoAPI")]
//    [AllowAnonymous] 
//    public async Task<IActionResult> TestTripforoAPI([FromServices] IEnumerable<ISupplierInventoryProvider> inventoryProviders)
//    {
//        var tripforoProvider = inventoryProviders.FirstOrDefault(p => p.ProviderName == "Tripforo");

//        if (tripforoProvider == null)
//        {
//            return BadRequest("Tripforo provider not found or not registered properly.");
//        }

//        try
//        {
//            var inventoryList = await tripforoProvider.FetchInventoryAsync();

//            return Json(new
//            {
//                success = true,
//                totalSectorsFound = inventoryList.Count,
//                data = inventoryList
//            });
//        }
//        catch (Exception ex)
//        {
//            return Json(new { success = false, message = ex.Message });
//        }
//    }
//}
using ClosedXML.Excel;
using CsvHelper;
using CsvHelper.Configuration;
using ExcelDataReader;
using inventory_analytics_tool.Helpers;
using inventory_analytics_tool.InventoryProviders;
using inventory_analytics_tool.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using System.Data;
using System.Globalization;
using System.Text.Json;

namespace purchase_analytics.Controllers;

// --- DYNAMIC CSV HEADER MAPPER ---
public sealed class UploadAttemptCsvRecordMap : ClassMap<UploadAttemptCsvRecord>
{
    public UploadAttemptCsvRecordMap()
    {
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

[Authorize(Roles = "Admin, Revenue")]
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

    // --- CACHE RESOLUTION HELPERS (Past + Present + Future Safe) ---
    private DashboardUiEvent? GetDashboardEvent(int eventId, int year)
    {
        // 1. Check Year-Specific Historical Cache (Contains all past and future events for that year)
        if (_cache.TryGetValue($"HistoricalDashboardMasterData_{year}", out List<DashboardUiEvent>? histEvents) && histEvents != null)
        {
            var ev = histEvents.FirstOrDefault(e => e.Id == eventId);
            if (ev != null) return ev;
        }

        // 2. Fallback to Primary Cache (Phase 1 essential data)
        if (_cache.TryGetValue("DashboardMasterData", out List<DashboardUiEvent>? primEvents) && primEvents != null)
        {
            var ev = primEvents.FirstOrDefault(e => e.Id == eventId);
            if (ev != null) return ev;
        }

        // 3. Fallback across all cached years
        foreach (var yr in new[] { 2026, 2025, 2024, 2027, 2028 })
        {
            if (yr == year) continue;
            if (_cache.TryGetValue($"HistoricalDashboardMasterData_{yr}", out List<DashboardUiEvent>? otherList) && otherList != null)
            {
                var ev = otherList.FirstOrDefault(e => e.Id == eventId);
                if (ev != null) return ev;
            }
        }

        return null;
    }

    private EventListData? GetEventData(int eventId, int year)
    {
        // 1. Check Year-Specific Historical Cache (Contains all past and future records for that year)
        if (_cache.TryGetValue($"HistoricalRawEventListData_{year}", out List<EventListData>? histEvents) && histEvents != null)
        {
            var ev = histEvents.FirstOrDefault(e => e.EventId == eventId);
            if (ev != null) return ev;
        }

        // 2. Fallback to Primary Cache
        if (_cache.TryGetValue("RawEventListData", out List<EventListData>? primEvents) && primEvents != null)
        {
            var ev = primEvents.FirstOrDefault(e => e.EventId == eventId);
            if (ev != null) return ev;
        }

        // 3. Fallback across all cached years
        foreach (var yr in new[] { 2026, 2025, 2024, 2027, 2028 })
        {
            if (yr == year) continue;
            if (_cache.TryGetValue($"HistoricalRawEventListData_{yr}", out List<EventListData>? otherList) && otherList != null)
            {
                var ev = otherList.FirstOrDefault(e => e.EventId == eventId);
                if (ev != null) return ev;
            }
        }

        return null;
    }

    public IActionResult Index(int id, string sectorType = "All", string lead = "All", int year = 2026, string priority = "All")
    {
        if (id == 0) return RedirectToAction("Index", "Dashboard");

        ViewBag.EventId = id;
        ViewBag.SectorType = sectorType;
        ViewBag.Lead = lead;
        ViewBag.Year = year;
        ViewBag.Priority = priority;
        ViewBag.EventName = "Details";

        var ev = GetDashboardEvent(id, year);
        if (ev != null)
        {
            ViewBag.EventName = ev.Name;

            if (DateTime.TryParse(ev.EventDate, out DateTime parsedDate))
            {
                ViewBag.EventDate = parsedDate.ToString("MMM dd, yyyy");
                ViewBag.EventDay = parsedDate.ToString("dddd");
            }
            else
            {
                ViewBag.EventDate = ev.EventDate;
                ViewBag.EventDay = string.Empty;
            }
        }

        return View();
    }

    [HttpGet]
    public async Task<IActionResult> GetSectorData(int id, string sectorType = "All", int year = 2026, bool includePast = true, string lead = "All", string priority = "All")
    {
        try
        {
            var apiEvent = GetEventData(id, year);
            if (apiEvent == null || apiEvent.Details == null)
                return Json(new { success = true, data = new List<object>() });

            _cache.TryGetValue("FDestList", out List<FDestData>? cachedFDest);
            cachedFDest ??= new List<FDestData>();

            var intlSectors = cachedFDest.Where(d => d.IsInternationalNew)
                                         .Select(d => $"{d.FromCode}-{d.ToCode}")
                                         .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var today = DateTime.Today;
            var yearStart = new DateTime(year, 1, 1).AddDays(-15);
            var yearEnd = new DateTime(year, 12, 31).AddDays(15);

            var allSectorStrings = apiEvent.Details.Select(d => $"{d.FromCode}-{d.ToCode}").Distinct().ToList();
            var bulkDetailedCounts = await _dbHelper.GetBulkAttemptCountsDetailedAsync(allSectorStrings, yearStart, yearEnd);

            var sectorList = new List<object>();

            foreach (var d in apiEvent.Details)
            {
                string sectorString = $"{d.FromCode}-{d.ToCode}";
                bool isIntl = intlSectors.Contains(sectorString);

                if (string.Equals(sectorType, "Domestic", StringComparison.OrdinalIgnoreCase) && isIntl) continue;
                if (string.Equals(sectorType, "International", StringComparison.OrdinalIgnoreCase) && !isIntl) continue;

                if (!string.Equals(priority, "All", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(priority, "Weekend", StringComparison.OrdinalIgnoreCase))
                {
                    bool hasPriorityMatch = false;
                    if (d.Details != null && d.Details.Any())
                    {
                        foreach (var detail in d.Details)
                        {
                            string p = !string.IsNullOrEmpty(detail.BlackoutPriority) ? detail.BlackoutPriority : "Normal";
                            if (string.Equals(p, priority, StringComparison.OrdinalIgnoreCase))
                            {
                                hasPriorityMatch = true;
                                break;
                            }
                        }
                    }
                    else if (string.Equals(priority, "Normal", StringComparison.OrdinalIgnoreCase))
                    {
                        hasPriorityMatch = true;
                    }
                    if (!hasPriorityMatch) continue;
                }

                var destMatch = cachedFDest.FirstOrDefault(f =>
                    string.Equals(f.FromCode, d.FromCode, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(f.ToCode, d.ToCode, StringComparison.OrdinalIgnoreCase));

                string stationName = !string.IsNullOrEmpty(destMatch?.StationName) ? destMatch.StationName : (destMatch?.FDestName ?? "Unassigned");
                string leadName = !string.IsNullOrEmpty(destMatch?.InternalOwner) ? destMatch.InternalOwner.Trim() : "Unassigned";

                if (!string.Equals(lead, "All", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(lead, leadName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var actualTravelDates = d.Details?.Select(x => x.TravelDate.Date).Distinct().ToList() ?? new List<DateTime>();
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

                int dynamicAchieved = 0;
                int dynamicTarget = 0;
                int dynamicFlightCount = 0;
                var dynamicAirlineAchieved = new Dictionary<string, int>();
                var flightCountsByAirline = new Dictionary<string, int>();

                if (d.Details != null && d.Details.Any())
                {
                    var validDailyDetails = d.Details.Where(x => cleanDates.Contains(x.TravelDate.Date)).ToList();

                    dynamicAchieved = validDailyDetails.Sum(x => x.Achieved);
                    dynamicTarget = validDailyDetails.Sum(x => x.TargetSeat);
                    dynamicFlightCount = validDailyDetails.Sum(x => x.FlightCount);

                    foreach (var daily in validDailyDetails)
                    {
                        if (daily.Airlines != null)
                        {
                            foreach (var a in daily.Airlines)
                            {
                                string code = !string.IsNullOrEmpty(a.AirlineCode) ? a.AirlineCode : "OAL";
                                dynamicAirlineAchieved[code] = dynamicAirlineAchieved.GetValueOrDefault(code, 0) + a.Achieved;

                                int flightCount = daily.Flights?.Count(f => string.Equals(f.AirlineCode, a.AirlineCode, StringComparison.OrdinalIgnoreCase)) ?? 0;
                                flightCountsByAirline[code] = flightCountsByAirline.GetValueOrDefault(code, 0) + flightCount;
                            }
                        }
                    }
                }
                else
                {
                    dynamicAchieved = d.Achieved;
                    dynamicTarget = d.TargetSeat;
                    dynamicFlightCount = d.FlightCount;
                    if (d.Airlines != null)
                    {
                        foreach (var a in d.Airlines)
                        {
                            string code = !string.IsNullOrEmpty(a.AirlineCode) ? a.AirlineCode : "OAL";
                            dynamicAirlineAchieved[code] = dynamicAirlineAchieved.GetValueOrDefault(code, 0) + a.Achieved;
                        }
                    }
                }

                var airlines = flightCountsByAirline.Select(kvp => $"{kvp.Key} ({kvp.Value})").ToList();
                if (!airlines.Any())
                {
                    airlines = d.Airlines?.Select(a => $"{a.AirlineName} ({d.Flights?.Count(f => f.AirlineCode == a.AirlineCode) ?? 0})").ToList() ?? new List<string>();
                }

                var absoluteMin = cleanDates.First();
                var absoluteMax = cleanDates.Last();
                int liveAttemptCount = bulkDetailedCounts
                        .Where(x => string.Equals(x.Sector, sectorString, StringComparison.OrdinalIgnoreCase)
                                 && x.TravelDate >= absoluteMin
                                 && x.TravelDate <= absoluteMax)
                        .Sum(x => x.Count);

                sectorList.Add(new
                {
                    festivalId = id,
                    sector = sectorString,
                    dateRange = dRange,
                    travelDate = d.Date,
                    airlinesCount = dynamicAirlineAchieved.Count > 0 ? dynamicAirlineAchieved.Count : (d.Airlines?.Count ?? 0),
                    flightCount = dynamicFlightCount,
                    totalStrength = d.TotalStrength,
                    target = dynamicTarget,
                    achieved = dynamicAchieved,
                    attemptCount = liveAttemptCount,
                    airlinesBreakdown = airlines,
                    airlineAchieved = dynamicAirlineAchieved,
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

    [HttpGet]
    public async Task<IActionResult> GetFareData(int eventId, string sector, int year = 2026, bool includePast = true, string priority = "All")
    {
        try
        {
            var apiEvent = GetEventData(eventId, year);
            if (apiEvent == null || apiEvent.Details == null)
                return Json(new { success = true, data = new List<object>() });

            var sectorData = apiEvent.Details.FirstOrDefault(s =>
                string.Equals($"{s.FromCode}-{s.ToCode}", sector, StringComparison.OrdinalIgnoreCase));

            if (sectorData == null)
                return Json(new { success = true, data = new List<object>() });

            var today = DateTime.Today;
            var yearStart = new DateTime(year - 1, 12, 15);
            var yearEnd = new DateTime(year + 1, 1, 15);

            var blackoutDates = sectorData.DateRange?.Select(d => d.Date).ToList() ?? new List<DateTime>();
            var detailDates = sectorData.Details?.Select(d => d.TravelDate.Date).ToList() ?? new List<DateTime>();

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

            var overallMinDate = allDates.DefaultIfEmpty(yearStart).Min();
            var overallMaxDate = allDates.DefaultIfEmpty(yearEnd).Max();

            var bulkAttemptCounts = await _dbHelper.GetBulkAttemptCountsByDateAsync(sector, overallMinDate, overallMaxDate);
            var bulkFares = await _dbHelper.GetBulkFlightLatestFaresAsync(sector, overallMinDate, overallMaxDate);

            var fareList = new List<object>();

            foreach (var date in allDates)
            {
                if (!includePast && date.Date < today)
                    continue;

                var tdd = sectorData.Details?.FirstOrDefault(d => d.TravelDate.Date == date.Date);

                if (!string.Equals(priority, "All", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(priority, "Weekend", StringComparison.OrdinalIgnoreCase))
                {
                    string p = !string.IsNullOrEmpty(tdd?.BlackoutPriority) ? tdd.BlackoutPriority : "Normal";
                    if (!string.Equals(p, priority, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }
                }

                double finalOnlineFare = 0;
                string finalOnlineFareEntryDate = "-";
                int activeFlightCount = 0;
                int activeAirlinesCount = 0;
                int activeTotalStrength = 0;
                var airlineAchieved = new Dictionary<string, int>();

                int liveAttemptCount = bulkAttemptCounts.GetValueOrDefault(date.Date, 0);

                if (tdd != null)
                {
                    var thresholdDate = DateTime.Today.AddDays(-3);
                    var validDbFares = bulkFares
                        .Where(f => f.TravelDate.Date == date.Date && f.EntryDate >= thresholdDate)
                        .ToList();

                    if (tdd.Flights != null && tdd.Flights.Any())
                    {
                        var matchingFlights = tdd.Flights
                            .Where(f => validDbFares.Any(df =>
                                df.AirlineCode == f.AirlineCode &&
                                df.FlightNo == f.FlightNo))
                            .GroupBy(f => new { f.AirlineCode, f.FlightNo })
                            .Select(g => g.First())
                            .ToList();

                        // Fallback to all scheduled flights if no recent 3-day feed exists (past events)
                        var displayFlights = matchingFlights.Any()
                            ? matchingFlights
                            : tdd.Flights.GroupBy(f => new { f.AirlineCode, f.FlightNo }).Select(g => g.First()).ToList();

                        activeFlightCount = displayFlights.Count;
                        activeAirlinesCount = displayFlights.Select(f => f.AirlineCode).Distinct().Count();
                        activeTotalStrength = activeFlightCount * 150;

                        var matchingDbRecords = validDbFares
                            .Where(df => displayFlights.Any(f =>
                                f.AirlineCode == df.AirlineCode &&
                                f.FlightNo == df.FlightNo))
                            .Where(df => df.LatestFare > 0)
                            .ToList();

                        if (matchingDbRecords.Any())
                        {
                            var lowestRecord = matchingDbRecords
                                .OrderBy(df => df.LatestFare)
                                .ThenByDescending(df => df.EntryDate)
                                .First();

                            finalOnlineFare = lowestRecord.LatestFare;
                            finalOnlineFareEntryDate = lowestRecord.EntryDate.ToString("dd MMM yyyy HH:mm tt");
                        }
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
                    priority = !string.IsNullOrEmpty(tdd?.BlackoutPriority) ? tdd.BlackoutPriority : "Normal",
                    airlinesCount = tdd != null ? activeAirlinesCount : 0,
                    flightCount = tdd != null ? activeFlightCount : 0,
                    totalStrength = tdd != null ? activeTotalStrength : 0,
                    target = tdd?.TargetSeat ?? 0,
                    achieved = tdd?.Achieved ?? 0,
                    attemptCount = liveAttemptCount,
                    targetFare = tdd != null ? (int)Math.Round(tdd.TargetFare, MidpointRounding.AwayFromZero) : 0,
                    avgPurFare = tdd != null ? (int)Math.Round(tdd.AvgPurchaseFare, MidpointRounding.AwayFromZero) : 0,
                    lastOnlineFare = (int)Math.Round(finalOnlineFare, MidpointRounding.AwayFromZero),
                    lastOnlineFareEntryDate = finalOnlineFareEntryDate,
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
    public async Task<IActionResult> GetFlightLevelFares(int eventId, string sector, string date, int year = 2026, bool includePast = true)
    {
        if (!DateTime.TryParse(date, out DateTime parsedDate)) return Json(new { success = false });

        double targetFare = 0;
        var apiFlights = new List<EventFlight>();

        var apiEvent = GetEventData(eventId, year);
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

            results.Add(new FlightFareTrendRow
            {
                Sector = sector,
                DOJ = parsedDate.ToString("dd MMM yyyy"),
                AirlineCode = f.AirlineCode,
                FlightNo = f.FlightNo,
                TargetFare = Math.Round(targetFare, MidpointRounding.AwayFromZero),
                LatestFare = dbMatch != null ? Math.Round(dbMatch.LatestFare, MidpointRounding.AwayFromZero) : 0,
                EntryDate = dbMatch != null ? dbMatch.EntryDate.ToString("dd MMM yyyy hh:mm tt") : "-"
            });
        }

        return Json(new { success = true, data = results });
    }

    public IActionResult FareDetails(int eventId, string sector, string sectorType = "All", string lead = "All", int year = 2026, string priority = "All")
    {
        if (eventId == 0 || string.IsNullOrEmpty(sector)) return RedirectToAction("Index", "Dashboard");

        ViewBag.EventId = eventId;
        ViewBag.Sector = sector;
        ViewBag.SectorType = sectorType;
        ViewBag.Lead = lead;
        ViewBag.Year = year;
        ViewBag.Priority = priority;
        ViewBag.EventName = "Details";

        var ev = GetDashboardEvent(eventId, year);
        if (ev != null)
        {
            ViewBag.EventName = ev.Name;
        }

        return View();
    }

    public IActionResult FareTrend(int eventId, string sector, string date, string sectorType = "All", string lead = "All", int year = 2026, string priority = "All")
    {
        if (eventId == 0 || string.IsNullOrEmpty(sector) || string.IsNullOrEmpty(date))
            return RedirectToAction("Index", "Dashboard");

        ViewBag.EventId = eventId;
        ViewBag.Sector = sector;
        ViewBag.TravelDate = date;
        ViewBag.SectorType = sectorType;
        ViewBag.Priority = priority;
        ViewBag.Lead = lead;
        ViewBag.Year = year;

        var ev = GetDashboardEvent(eventId, year);
        if (ev != null)
        {
            ViewBag.EventName = ev.Name;
        }

        return View();
    }

    [HttpGet]
    public async Task<IActionResult> AttemptDetails(int eventId, string sector, string travelDate = null, int year = 2026, bool includePast = true)
    {
        if (eventId == 0 || string.IsNullOrEmpty(sector)) return RedirectToAction("Index", "Dashboard");

        ViewBag.EventId = eventId;
        ViewBag.Sector = sector;
        ViewBag.Year = year;

        DateTime targetMinDate;
        DateTime targetMaxDate;

        if (!string.IsNullOrEmpty(travelDate) && DateTime.TryParse(travelDate, out DateTime specificDate))
        {
            targetMinDate = specificDate.Date;
            targetMaxDate = specificDate.Date;
        }
        else
        {
            var yearStart = new DateTime(year, 1, 1).AddDays(-15);
            var yearEnd = new DateTime(year, 12, 31).AddDays(15);
            DateTime sectorMinDate = DateTime.MaxValue;
            DateTime sectorMaxDate = DateTime.MinValue;

            var apiEvent = GetEventData(eventId, year);
            if (apiEvent != null && apiEvent.Details != null)
            {
                var sectorData = apiEvent.Details.FirstOrDefault(s => $"{s.FromCode}-{s.ToCode}" == sector);
                if (sectorData != null)
                {
                    var cleanDates = sectorData.DateRange?.Where(dt => dt >= yearStart && dt <= yearEnd).ToList() ?? new List<DateTime>();
                    if (!cleanDates.Any() && sectorData.Details != null)
                    {
                        cleanDates = sectorData.Details.Select(x => x.TravelDate.Date).Where(dt => dt >= yearStart && dt <= yearEnd).Distinct().ToList();
                    }
                    if (!cleanDates.Any()) cleanDates.Add(sectorData.Date);

                    sectorMinDate = cleanDates.Min();
                    sectorMaxDate = cleanDates.Max();
                }
            }

            targetMinDate = sectorMinDate == DateTime.MaxValue ? yearStart : sectorMinDate;
            targetMaxDate = sectorMaxDate == DateTime.MinValue ? yearEnd : sectorMaxDate;
        }

        var attemptRecords = await _dbHelper.GetAttemptDetailsAsync(sector, targetMinDate.Date, targetMaxDate.Date);

        return View(attemptRecords);
    }

    [HttpGet]
    public async Task<IActionResult> ExportAttemptsToExcel(int eventId, string sector, int year = 2026, bool includePast = true)
    {
        if (eventId == 0 || string.IsNullOrEmpty(sector)) return BadRequest("Invalid parameters");

        var yearStart = new DateTime(year, 1, 1).AddDays(-15);
        var yearEnd = new DateTime(year, 12, 31).AddDays(15);
        DateTime sectorMinDate = DateTime.MaxValue;
        DateTime sectorMaxDate = DateTime.MinValue;

        var apiEvent = GetEventData(eventId, year);
        if (apiEvent != null && apiEvent.Details != null)
        {
            var sectorData = apiEvent.Details.FirstOrDefault(s => $"{s.FromCode}-{s.ToCode}" == sector);
            if (sectorData != null)
            {
                var cleanDates = sectorData.DateRange?.Where(dt => dt >= yearStart && dt <= yearEnd).ToList() ?? new List<DateTime>();
                if (!cleanDates.Any() && sectorData.Details != null)
                {
                    cleanDates = sectorData.Details.Select(x => x.TravelDate.Date).Where(dt => dt >= yearStart && dt <= yearEnd).Distinct().ToList();
                }
                if (!cleanDates.Any()) cleanDates.Add(sectorData.Date);

                sectorMinDate = cleanDates.Min();
                sectorMaxDate = cleanDates.Max();
            }
        }

        if (sectorMinDate == DateTime.MaxValue) sectorMinDate = yearStart;
        if (sectorMaxDate == DateTime.MinValue) sectorMaxDate = yearEnd;

        var attemptRecords = await _dbHelper.GetAttemptDetailsAsync(sector, sectorMinDate.Date, sectorMaxDate.Date);

        using (var workbook = new XLWorkbook())
        {
            var worksheet = workbook.Worksheets.Add("Attempt Data");

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

            int row = 2;
            foreach (var rec in attemptRecords)
            {
                worksheet.Cell(row, 1).Value = rec.AqId ?? "";
                worksheet.Cell(row, 2).Value = rec.RawEntryDate.ToString("yyyy-MM-dd") ?? "";
                worksheet.Cell(row, 3).Value = rec.ReqId ?? "";
                worksheet.Cell(row, 4).Value = rec.Origin ?? "";
                worksheet.Cell(row, 5).Value = rec.Destination ?? "";
                worksheet.Cell(row, 6).Value = rec.RawTravelDate.ToString("yyyy-MM-dd");
                worksheet.Cell(row, 7).Value = rec.AirlineCode ?? "";

                string fCode = rec.FlightCode ?? "";
                string aCode = rec.AirlineCode ?? "";
                if (!string.IsNullOrEmpty(aCode)) { fCode = fCode.Replace(aCode, "").Trim(); }
                worksheet.Cell(row, 8).Value = fCode;

                worksheet.Cell(row, 9).Value = rec.Pax;

                string autoQuote = rec.AutoQuote ?? "";
                worksheet.Cell(row, 10).Value = autoQuote.Replace("₹", "").Replace(",", "");

                worksheet.Cell(row, 11).Value = rec.Status ?? "";

                string negFare = rec.NegFare ?? "";
                worksheet.Cell(row, 12).Value = negFare.Replace("₹", "").Replace(",", "");

                worksheet.Cell(row, 13).Value = rec.ConfStatus ?? "";
                worksheet.Cell(row, 14).Value = rec.Remarks ?? "";
                worksheet.Cell(row, 15).Value = rec.PNR ?? "";
                worksheet.Cell(row, 16).Value = rec.OpenRemarks ?? "";
                worksheet.Cell(row, 17).Value = rec.Lead ?? "";
                row++;
            }

            var hiddenSheet = workbook.Worksheets.Add("ValidationData");
            hiddenSheet.Hide();

            hiddenSheet.Cell("A1").Value = "NEGOTIATION SENT";
            hiddenSheet.Cell("A2").Value = "QUOTATION PENDING";
            hiddenSheet.Cell("A3").Value = "REQUEST REJECTED";
            hiddenSheet.Cell("A4").Value = "SELF REJECTED";
            hiddenSheet.Cell("A5").Value = "PNR CREATED";

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
            statusRange.SetDataValidation().List("=ValidationData!$A$1:$A$5", true);
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
                return File(content, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"AirIQ_Attempts_{sector}.xlsx");
            }
        }
    }

    private string GetExcelColumnValue(DataRow row, params string[] possibleNames)
    {
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

            _cache.TryGetValue("FDestList", out List<FDestData>? cachedFDest);
            cachedFDest ??= new List<FDestData>();
            var today = DateTime.Now;

            var uniqueApiRequests = records
                .Where(r => !string.IsNullOrEmpty(r.Origin) && !string.IsNullOrEmpty(r.Destination))
                .Select(r => new
                {
                    Sector = $"{r.Origin?.Trim()}-{r.Destination?.Trim()}",
                    TravelDate = DateTime.TryParse(r.TravelDate, out DateTime d) && d.Year >= 2000 ? d.Date : DateTime.Today
                })
                .Distinct()
                .ToList();

            var apiBatches = new List<(string Sectors, DateTime TravelDate)>();
            var groupedByDate = uniqueApiRequests.GroupBy(r => r.TravelDate);

            foreach (var dateGroup in groupedByDate)
            {
                var delSectors = dateGroup.Where(r => r.Sector.Contains("DEL", StringComparison.OrdinalIgnoreCase)).Select(r => r.Sector).ToList();
                var otherSectors = dateGroup.Where(r => !r.Sector.Contains("DEL", StringComparison.OrdinalIgnoreCase)).Select(r => r.Sector).ToList();

                foreach (var sec in delSectors)
                {
                    apiBatches.Add((sec, dateGroup.Key));
                }

                for (int i = 0; i < otherSectors.Count; i += 10)
                {
                    apiBatches.Add((string.Join(",", otherSectors.Skip(i).Take(10)), dateGroup.Key));
                }
            }

            var flightCache = new System.Collections.Concurrent.ConcurrentDictionary<string, System.Collections.Concurrent.ConcurrentBag<JsonElement>>();
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var semaphore = new SemaphoreSlim(10);

            var fetchTasks = apiBatches.Select(async batch =>
            {
                await semaphore.WaitAsync();
                try
                {
                    var requestBody = new { Sectors = batch.Sectors, FlightDate = batch.TravelDate.ToString("MM/dd/yyyy", CultureInfo.InvariantCulture) };
                    var content = new StringContent(JsonSerializer.Serialize(requestBody), System.Text.Encoding.UTF8, "application/json");

                    var response = await httpClient.PostAsync("http://13.203.38.118:8080/ccloop", content);
                    if (response.IsSuccessStatusCode)
                    {
                        var jsonResp = await response.Content.ReadAsStringAsync();
                        using var doc = JsonDocument.Parse(jsonResp);

                        if (doc.RootElement.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var rootObj in doc.RootElement.EnumerateArray())
                            {
                                if (rootObj.TryGetProperty("flights", out JsonElement flights) && flights.ValueKind == JsonValueKind.Array)
                                {
                                    foreach (var f in flights.EnumerateArray())
                                    {
                                        string org = f.TryGetProperty("Origin", out var o) ? o.GetString() : "";
                                        string des = f.TryGetProperty("Destination", out var d) ? d.GetString() : "";

                                        if (!string.IsNullOrEmpty(org) && !string.IsNullOrEmpty(des))
                                        {
                                            string flightSector = $"{org}-{des}";
                                            string cacheKey = $"{flightSector}_{batch.TravelDate:MM/dd/yyyy}";

                                            var bag = flightCache.GetOrAdd(cacheKey, _ => new System.Collections.Concurrent.ConcurrentBag<JsonElement>());
                                            bag.Add(f.Clone());
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
                catch (Exception ex) { Console.WriteLine($"API Fetch Error for {batch.Sectors}: {ex.Message}"); }
                finally { semaphore.Release(); }
            });

            await Task.WhenAll(fetchTasks);

            var dbFeedCache = new Dictionary<string, int>();

            foreach (var rec in records)
            {
                if (string.IsNullOrEmpty(rec.Origin) && string.IsNullOrEmpty(rec.Destination)) continue;

                string cleanAqId = new string(rec.AqId?.Where(char.IsDigit).ToArray() ?? Array.Empty<char>());
                int.TryParse(cleanAqId, out int parsedAqId);

                if (!DateTime.TryParse(rec.TravelDate, out DateTime parsedTravelDate) || parsedTravelDate.Year < 2000)
                {
                    parsedTravelDate = DateTime.Today;
                }
                string calculatedSector = $"{rec.Origin?.Trim()}-{rec.Destination?.Trim()}";

                decimal liveAmount = 0;
                string depTime = "00:00:00";
                string arrTime = "00:00:00";

                string cacheKey = $"{calculatedSector}_{parsedTravelDate:MM/dd/yyyy}";

                if (flightCache.TryGetValue(cacheKey, out var flightArray))
                {
                    string targetAirline = rec.AirlineCode?.Trim() ?? "";
                    string targetFlightNum = new string(rec.FlightNumber?.Where(char.IsDigit).ToArray() ?? Array.Empty<char>());

                    foreach (var f in flightArray)
                    {
                        string apiAirline = f.TryGetProperty("AirlineCode", out var airProp) ? airProp.ToString().Trim() : "";
                        string rawApiFlightNum = f.TryGetProperty("FlightNumber", out var fnProp) ? fnProp.ToString() : "";
                        string apiFlightNum = new string(rawApiFlightNum.Where(char.IsDigit).ToArray());

                        if (string.Equals(apiAirline, targetAirline, StringComparison.OrdinalIgnoreCase) && apiFlightNum == targetFlightNum)
                        {
                            double baseAmt = 0;
                            if (f.TryGetProperty("BaseAmount", out var baseEl) && baseEl.ValueKind == JsonValueKind.Number) baseAmt = baseEl.GetDouble();

                            double taxAmt = 0;
                            if (f.TryGetProperty("TotalTaxAmount", out var taxEl) && taxEl.ValueKind == JsonValueKind.Number) taxAmt = taxEl.GetDouble();

                            liveAmount = (decimal)(baseAmt + taxAmt);

                            if (f.TryGetProperty("DepartureDateTime", out var depEl) && DateTime.TryParse(depEl.GetString(), out DateTime dDate))
                                depTime = dDate.ToString("HH:mm tt");

                            if (f.TryGetProperty("ArrivalDateTime", out var arrEl) && DateTime.TryParse(arrEl.GetString(), out DateTime aDate))
                                arrTime = aDate.ToString("HH:mm tt");

                            break;
                        }
                    }
                }

                string feedCacheKey = $"{calculatedSector}_{parsedTravelDate:MM/dd/yyyy}_{rec.AirlineCode?.Trim()}_{rec.FlightNumber?.Trim()}";
                int feedId = 0;

                if (dbFeedCache.ContainsKey(feedCacheKey))
                {
                    feedId = dbFeedCache[feedCacheKey];
                }
                else
                {
                    feedId = await _dbHelper.GetOrInsertBlackoutFareFeedIdAsync(calculatedSector, parsedTravelDate, rec.AirlineCode?.Trim(), rec.FlightNumber?.Trim(), liveAmount, arrTime, depTime);
                    dbFeedCache[feedCacheKey] = feedId;
                }

                var destMatch = cachedFDest.FirstOrDefault(d =>
                    string.Equals(d?.FromCode, rec.Origin?.Trim(), StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(d?.ToCode, rec.Destination?.Trim(), StringComparison.OrdinalIgnoreCase));

                int.TryParse(rec.Pax?.Replace(",", ""), out int paxCount);
                decimal.TryParse(rec.AutoQuote?.Replace("₹", "").Replace(",", ""), out decimal autoQuoteFare);
                decimal.TryParse(rec.NegotiatedFare?.Replace("₹", "").Replace(",", ""), out decimal negFare);

                string mappedStatus = "";
                string rawStatus = rec.Status?.ToUpper() ?? "";
                if (rawStatus.Contains("PNR CREATED")) mappedStatus = "PNR CREATED";
                else if (rawStatus.Contains("REJECTED")) mappedStatus = "REJECTED";
                else if (rawStatus.Contains("NEGOTIATION SENT")) mappedStatus = "NEGOTIATION SENT";

                dbInsertList.Add(new GroupRaisedEntryModel
                {
                    GroupRaisedEntryID = parsedAqId > 0 ? parsedAqId : (int?)null,
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

            var sessionIdClaim = User.Claims.FirstOrDefault(c => c.Type == "SessionId")?.Value;
            Guid? sessionId = Guid.TryParse(sessionIdClaim, out Guid sid) ? sid : null;

            var uploadLog = new UserUploadLog
            {
                SessionId = sessionId,
                Username = User.Identity?.Name ?? "Unknown",
                FileName = file.FileName,
                FileExtension = ext,
                FileSizeBytes = file.Length,
                RecordsParsed = dbInsertList.Count,
                IsSuccessful = isSuccess,
                ErrorMessage = isSuccess ? null : "Database insertion failed during bulk operation.",
                Timestamp = DateTime.UtcNow
            };
            await _dbHelper.InsertUserUploadLogAsync(uploadLog);

            if (isSuccess)
                return Json(new { success = true, message = $"Successfully imported {dbInsertList.Count} records." });
            else
                return Json(new { success = false, message = "Database insertion failed. Check server logs." });
        }
        catch (Exception ex)
        {
            var sessionIdClaim = User.Claims.FirstOrDefault(c => c.Type == "SessionId")?.Value;
            Guid? sessionId = Guid.TryParse(sessionIdClaim, out Guid sid) ? sid : null;

            var uploadLog = new UserUploadLog
            {
                SessionId = sessionId,
                Username = User.Identity?.Name ?? "Unknown",
                FileName = file.FileName,
                FileExtension = ext,
                FileSizeBytes = file.Length,
                RecordsParsed = 0,
                IsSuccessful = false,
                ErrorMessage = ex.Message,
                Timestamp = DateTime.UtcNow
            };
            await _dbHelper.InsertUserUploadLogAsync(uploadLog);
            return Json(new { success = false, message = "Error processing file: " + ex.Message });
        }
    }

    private Dictionary<int, string> BuildOccasionMap(List<AttemptRecord> records)
    {
        if (!_cache.TryGetValue("MasterOccasionMap", out Dictionary<string, string>? sectorDateToEvent) || sectorDateToEvent == null)
        {
            sectorDateToEvent = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var allRawEvents = new List<EventListData>();
            var allDashEvents = new List<DashboardUiEvent>();

            foreach (var yr in new[] { 2024, 2025, 2026, 2027, 2028 })
            {
                if (_cache.TryGetValue($"HistoricalRawEventListData_{yr}", out List<EventListData>? histRaw) && histRaw != null)
                    allRawEvents.AddRange(histRaw);
                else if (yr == 2026 && _cache.TryGetValue("RawEventListData", out List<EventListData>? rawList) && rawList != null)
                    allRawEvents.AddRange(rawList);

                if (_cache.TryGetValue($"HistoricalDashboardMasterData_{yr}", out List<DashboardUiEvent>? histDash) && histDash != null)
                    allDashEvents.AddRange(histDash);
                else if (yr == 2026 && _cache.TryGetValue("DashboardMasterData", out List<DashboardUiEvent>? dashList) && dashList != null)
                    allDashEvents.AddRange(dashList);
            }

            foreach (var rawEv in allRawEvents)
            {
                var dashEv = allDashEvents.FirstOrDefault(d => d.Id == rawEv.EventId);
                string evName = dashEv?.Name ?? rawEv.Event ?? "Unknown Occasion";

                if (rawEv.Details != null)
                {
                    foreach (var sec in rawEv.Details)
                    {
                        string sector = $"{sec.FromCode}-{sec.ToCode}";
                        var dates = new HashSet<DateTime>();
                        if (sec.DateRange != null) foreach (var d in sec.DateRange) dates.Add(d.Date);
                        if (sec.Details != null) foreach (var d in sec.Details) dates.Add(d.TravelDate.Date);

                        foreach (var d in dates)
                        {
                            string k = $"{sector}|{d:yyyy-MM-dd}";
                            if (!sectorDateToEvent.ContainsKey(k))
                                sectorDateToEvent[k] = evName;
                            else if (!sectorDateToEvent[k].Contains(evName))
                                sectorDateToEvent[k] += " / " + evName;
                        }
                    }
                }
            }

            _cache.Set("MasterOccasionMap", sectorDateToEvent, TimeSpan.FromMinutes(30));
        }

        var map = new Dictionary<int, string>();
        foreach (var rec in records)
        {
            string sector = $"{rec.Origin?.Trim()}-{rec.Destination?.Trim()}";
            string key = $"{sector}|{rec.RawTravelDate:yyyy-MM-dd}";
            map[rec.Id] = sectorDateToEvent!.TryGetValue(key, out string? ev) ? ev : "-";
        }

        return map;
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
            entryStart = DateTime.Today;
            entryEnd = DateTime.Today;
            ViewBag.FromDate = entryStart?.ToString("yyyy-MM-dd");
            ViewBag.ToDate = entryEnd?.ToString("yyyy-MM-dd");
            ViewBag.TravelDate = "";
        }

        var allRecords = await _dbHelper.GetAllAttemptDetailsAsync(entryStart, entryEnd, specificTravel);

        _cache.TryGetValue("FDestList", out List<FDestData>? cachedFDest);
        cachedFDest ??= new List<FDestData>();

        var destDict = cachedFDest
            .Where(f => !string.IsNullOrEmpty(f.FromCode) && !string.IsNullOrEmpty(f.ToCode))
            .GroupBy(f => $"{f.FromCode.Trim()}-{f.ToCode.Trim()}".ToUpper())
            .ToDictionary(g => g.Key, g => g.First().InternalOwner);

        foreach (var rec in allRecords)
        {
            string lookupKey = $"{rec.Origin?.Trim()}-{rec.Destination?.Trim()}".ToUpper();

            if (destDict.TryGetValue(lookupKey, out string? internalOwner) && !string.IsNullOrEmpty(internalOwner))
            {
                rec.Lead = internalOwner;
            }
            else if (string.IsNullOrEmpty(rec.Lead))
            {
                rec.Lead = "Unassigned";
            }
        }

        ViewBag.OccasionMap = BuildOccasionMap(allRecords);

        return View(allRecords);
    }

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

        var attemptRecords = await _dbHelper.GetAllAttemptDetailsAsync(entryStart, entryEnd, specificTravel);

        _cache.TryGetValue("FDestList", out List<FDestData>? cachedFDest);
        cachedFDest ??= new List<FDestData>();

        var destDict = cachedFDest
            .Where(f => !string.IsNullOrEmpty(f.FromCode) && !string.IsNullOrEmpty(f.ToCode))
            .GroupBy(f => $"{f.FromCode.Trim()}-{f.ToCode.Trim()}".ToUpper())
            .ToDictionary(g => g.Key, g => g.First().InternalOwner);

        foreach (var rec in attemptRecords)
        {
            string lookupKey = $"{rec.Origin?.Trim()}-{rec.Destination?.Trim()}".ToUpper();

            if (destDict.TryGetValue(lookupKey, out string? internalOwner) && !string.IsNullOrEmpty(internalOwner))
            {
                rec.Lead = internalOwner;
            }
            else if (string.IsNullOrEmpty(rec.Lead))
            {
                rec.Lead = "Unassigned";
            }
        }

        if (!string.IsNullOrEmpty(fSector)) attemptRecords = attemptRecords.Where(r => $"{r.Origin?.Trim()}-{r.Destination?.Trim()}".Equals(fSector, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!string.IsNullOrEmpty(fStation)) attemptRecords = attemptRecords.Where(r => string.Equals(r.Origin?.Trim(), fStation, StringComparison.OrdinalIgnoreCase) || string.Equals(r.Destination?.Trim(), fStation, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!string.IsNullOrEmpty(fAirline)) attemptRecords = attemptRecords.Where(r => string.Equals(r.AirlineCode?.Trim(), fAirline, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!string.IsNullOrEmpty(fTravelDate)) attemptRecords = attemptRecords.Where(r => r.TravelDate == fTravelDate).ToList();
        if (!string.IsNullOrEmpty(fStatus)) attemptRecords = attemptRecords.Where(r => { string recStatus = r.Status?.ToUpper() ?? "PENDING"; if (recStatus != "PNR CREATED" && recStatus != "REJECTED" && recStatus != "NEGOTIATION SENT") recStatus = "PENDING"; return string.Equals(recStatus, fStatus, StringComparison.OrdinalIgnoreCase); }).ToList();
        if (!string.IsNullOrEmpty(fConfStatus)) attemptRecords = attemptRecords.Where(r => { string recConf = r.ConfStatus?.ToUpper() ?? ""; if (recConf != "YES" && recConf != "NO") recConf = ""; return string.Equals(recConf, fConfStatus, StringComparison.OrdinalIgnoreCase); }).ToList();
        if (!string.IsNullOrEmpty(fLead)) attemptRecords = attemptRecords.Where(r => string.Equals(r.Lead, fLead, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!string.IsNullOrEmpty(fFlightNo)) attemptRecords = attemptRecords.Where(r => r.FlightCode != null && r.FlightCode.Contains(fFlightNo, StringComparison.OrdinalIgnoreCase)).ToList();
        if (!string.IsNullOrEmpty(fMonth)) attemptRecords = attemptRecords.Where(r => { if (DateTime.TryParse(r.TravelDate, out DateTime dt)) return string.Equals(dt.ToString("MMMM"), fMonth, StringComparison.OrdinalIgnoreCase); return false; }).ToList();

        var occasionMap = BuildOccasionMap(attemptRecords);

        using (var workbook = new XLWorkbook())
        {
            var worksheet = workbook.Worksheets.Add("All Attempts");

            string[] headers = { "AQ ID", "Occasion Name", "Entry Date", "Request Id", "Org", "Des", "Travel Date", "Airline Code", "Flight Number",
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
                worksheet.Cell(row, 2).Value = occasionMap.ContainsKey(rec.Id) ? occasionMap[rec.Id] : "-";
                worksheet.Cell(row, 3).Value = rec.RawEntryDate.ToString("dd MMM yyyy");
                worksheet.Cell(row, 4).Value = rec.ReqId ?? "";
                worksheet.Cell(row, 5).Value = rec.Origin ?? "";
                worksheet.Cell(row, 6).Value = rec.Destination ?? "";
                worksheet.Cell(row, 7).Value = rec.RawTravelDate.ToString("dd MMM yyyy");
                worksheet.Cell(row, 8).Value = rec.AirlineCode ?? "";

                string fCode = rec.FlightCode ?? "";
                string aCode = rec.AirlineCode ?? "";
                if (!string.IsNullOrEmpty(aCode)) { fCode = fCode.Replace(aCode, "").Trim(); }

                worksheet.Cell(row, 9).Value = fCode;
                worksheet.Cell(row, 10).Value = rec.Pax;
                worksheet.Cell(row, 11).Value = rec.AutoQuote?.Replace("₹", "").Replace(",", "") ?? "";
                worksheet.Cell(row, 12).Value = rec.Status ?? "";
                worksheet.Cell(row, 13).Value = rec.NegFare?.Replace("₹", "").Replace(",", "") ?? "";
                worksheet.Cell(row, 14).Value = rec.ConfStatus ?? "";
                worksheet.Cell(row, 15).Value = rec.Remarks ?? "";
                worksheet.Cell(row, 16).Value = rec.PNR ?? "";
                worksheet.Cell(row, 17).Value = rec.OpenRemarks ?? "";
                worksheet.Cell(row, 18).Value = rec.Lead ?? "";
                row++;
            }

            var hiddenSheet = workbook.Worksheets.Add("ValidationData");
            hiddenSheet.Hide();

            hiddenSheet.Cell("A1").Value = "NEGOTIATION SENT";
            hiddenSheet.Cell("A2").Value = "QUOTATION PENDING";
            hiddenSheet.Cell("A3").Value = "REQUEST REJECTED";
            hiddenSheet.Cell("A4").Value = "SELF REJECTED";
            hiddenSheet.Cell("A5").Value = "PNR CREATED";
            hiddenSheet.Cell("B1").Value = "YES";
            hiddenSheet.Cell("B2").Value = "NO";
            hiddenSheet.Cell("C1").Value = "PNR CREATED";
            hiddenSheet.Cell("C2").Value = "NEGOTIATION REJECTED";
            hiddenSheet.Cell("C3").Value = "REQUEST REJECTED";
            hiddenSheet.Cell("C4").Value = "SELF REJECTED";
            hiddenSheet.Cell("C5").Value = "NEGOTIATION SATISFIED";
            hiddenSheet.Cell("C6").Value = "NEGOTIATION NOT SATISFIED";
            hiddenSheet.Cell("C7").Value = "NEGOTIATION PENDING";

            var statusRange = worksheet.Range(2, 12, 10000, 12);
            statusRange.SetDataValidation().List("=ValidationData!$A$1:$A$5", true);
            statusRange.SetDataValidation().IgnoreBlanks = true;

            var confStatusRange = worksheet.Range(2, 14, 10000, 14);
            confStatusRange.SetDataValidation().List("=ValidationData!$B$1:$B$2", true);
            confStatusRange.SetDataValidation().IgnoreBlanks = true;

            var remarksRange = worksheet.Range(2, 15, 10000, 15);
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

    [HttpGet]
    [Route("OccasionDetails/TestTripforoAPI")]
    [AllowAnonymous]
    public async Task<IActionResult> TestTripforoAPI([FromServices] IEnumerable<ISupplierInventoryProvider> inventoryProviders)
    {
        var tripforoProvider = inventoryProviders.FirstOrDefault(p => p.ProviderName == "Tripforo");

        if (tripforoProvider == null)
        {
            return BadRequest("Tripforo provider not found or not registered properly.");
        }

        try
        {
            var inventoryList = await tripforoProvider.FetchInventoryAsync();

            return Json(new
            {
                success = true,
                totalSectorsFound = inventoryList.Count,
                data = inventoryList
            });
        }
        catch (Exception ex)
        {
            return Json(new { success = false, message = ex.Message });
        }
    }
}