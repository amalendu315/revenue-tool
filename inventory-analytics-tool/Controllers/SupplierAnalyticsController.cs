using inventory_analytics_tool.Models;
using inventory_analytics_tool.Models.ViewModels;
using inventory_analytics_tool.Repositories;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace inventory_analytics_tool.Controllers
{
    [Authorize(Roles = "Admin, Supplier")] // Secure this feature
    public class SupplierAnalyticsController : Controller
    {
        private readonly ISalesAnalyticsRepository _salesRepo;
        private readonly IMemoryCache _cache;

        public SupplierAnalyticsController(ISalesAnalyticsRepository salesRepo, IMemoryCache cache)
        {
            _salesRepo = salesRepo;
            _cache = cache;
        }

        private string Normalize(string name) => name?.Trim().ToLower() ?? "unknown";

        private string GenerateSmartDateLabel(DateTime start, DateTime end)
        {
            bool isLastDayOfMonth = end.Date == new DateTime(start.Year, start.Month, DateTime.DaysInMonth(start.Year, start.Month));

            bool isMonthToDate = (start.Month == DateTime.Today.Month &&
                                  start.Year == DateTime.Today.Year &&
                                  end.Date == DateTime.Today);

            if (start.Day == 1 && (isLastDayOfMonth || isMonthToDate))
            {
                return start.ToString("MMMM yyyy"); 
            }

            if (start.Date == end.Date)
            {
                return start.ToString("dd MMMM yyyy"); 
            }

            return $"{start:dd MMM yyyy} - {end:dd MMM yyyy}"; 
        }

        [HttpGet]
        public async Task<IActionResult> Index(DateTime? r1Start, DateTime? r1End, DateTime? r2Start, DateTime? r2End)
        {
            var today = DateTime.Today;
            var thisMonthStart = new DateTime(today.Year, today.Month, 1);

            DateTime range1Start = r1Start ?? thisMonthStart.AddMonths(-1);
            DateTime range1End = r1End ?? thisMonthStart.AddDays(-1);
            DateTime range2Start = r2Start ?? thisMonthStart;
            DateTime range2End = r2End ?? today;

            // 1. Fetch EVERYTHING
            var rawSuppliers = await _salesRepo.GetAllSuppliersAsync();
            var allSuppliers = rawSuppliers
                                    .Where(s => !string.IsNullOrWhiteSpace(s.SupplierName) &&
                                                !s.SupplierName.Contains("test", StringComparison.OrdinalIgnoreCase))
                                    .ToList();
            var range1Sales = await _salesRepo.GetSalesSummaryAsync(range1Start, range1End);
            var range2Sales = await _salesRepo.GetSalesSummaryAsync(range2Start, range2End);
            var apiInventory = _cache.Get<List<StandardInventoryRecord>>("SupplierApiInventoryCache") ?? new List<StandardInventoryRecord>();

            var inventorySummary = apiInventory
    .GroupBy(i => $"{Normalize(i.SupplierName)}|{Normalize(i.SupplierType)}")
    .ToDictionary(g => g.Key, g => new
    {
        UniqueSectorsCount = g.Select(x => $"{x.Origin}-{x.Destination}").Distinct().Count(),
        MinDate = g.SelectMany(x => x.AvailableDates).DefaultIfEmpty(DateTime.MinValue).Min(),
        MaxDate = g.SelectMany(x => x.AvailableDates).DefaultIfEmpty(DateTime.MinValue).Max()
    });

            var viewModel = new SupplierDashboardViewModel
            {
                DateRange1Label = GenerateSmartDateLabel(range1Start, range1End),
                DateRange2Label = GenerateSmartDateLabel(range2Start, range2End)
            };

            var supplierMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "Trip Foro", "tripforo" },
                { "BOOKNTRAVEL INDIA PVT LTD", "bookntravel" },
                { "SafiyaTravels", "safiyatravels" },
                {"TRAVEL PICK.IO", "TRAVEL PICK.IO" }
            };

            string GetMappedName(string rawName)
            {
                if (string.IsNullOrWhiteSpace(rawName)) return "unknown";
                var normalized = rawName.Trim();
                if (normalized.Equals("Trip Foro", StringComparison.OrdinalIgnoreCase)) return "tripforo";
                if (normalized.Equals("Safiya Travels", StringComparison.OrdinalIgnoreCase)) return "safiyatravels";
                return normalized.ToLower();
            }

            foreach (var masterVendor in allSuppliers.GroupBy(s => new { s.SupplierName, s.SupplierType }).Select(g => g.First()))
            {
                var mappedMasterName = GetMappedName(masterVendor.SupplierName);

                var r1DataMatches = range1Sales.Where(x =>
     GetMappedName(x.SupplierName) == mappedMasterName &&
     Normalize(x.SupplierType) == Normalize(masterVendor.SupplierType));

                var r2DataMatches = range2Sales.Where(x =>
                    GetMappedName(x.SupplierName) == mappedMasterName &&
                    Normalize(x.SupplierType) == Normalize(masterVendor.SupplierType));

                var row = new SupplierSummaryRow
                {
                    SupplierName = masterVendor.SupplierName,
                    SupplierType = masterVendor.SupplierType,
                    IsActive = masterVendor.Status,
                    Range1Pax = r1DataMatches.Sum(x => x.TotalPax),
                    Range1SaleAmount = r1DataMatches.Sum(x => x.TotalSaleAmount),
                    Range2Pax = r2DataMatches.Sum(x => x.TotalPax),
                    Range2SaleAmount = r2DataMatches.Sum(x => x.TotalSaleAmount),
                    TotalAvailableSectors = 0,
                    AvailableDateRange = "-"
                };

                string invKey = $"{mappedMasterName}|{Normalize(masterVendor.SupplierType)}";
                if (inventorySummary.TryGetValue(invKey, out var invStats))
                {
                    row.TotalAvailableSectors = invStats.UniqueSectorsCount;

                   
                    if (invStats.MinDate > DateTime.MinValue)
                    {
                        row.AvailableDateRange = $"{invStats.MinDate:dd MMM yyyy} - {invStats.MaxDate:dd MMM yyyy}";
                    }
                   
                }
                if (row.SupplierType.Equals("Offline", StringComparison.OrdinalIgnoreCase))
                {
                    // Check if they have ANY sales in either selected range
                    bool hasSales = row.Range1SaleAmount > 0 || row.Range2SaleAmount > 0;

                    // Check if they have valid inventory dates (not just empty sectors)
                    bool hasValidDates = row.AvailableDateRange != "-";

                    // Override the DB status: They are only Active if one of these is true
                    row.IsActive = hasSales || hasValidDates;
                }

                viewModel.SummaryRows.Add(row);
            }

            viewModel.R1ApiPax = viewModel.SummaryRows.Where(r => r.SupplierType == "API").Sum(r => r.Range1Pax);
            viewModel.R1ApiAmount = viewModel.SummaryRows.Where(r => r.SupplierType == "API").Sum(r => r.Range1SaleAmount);
            viewModel.R1ApiActiveCount = viewModel.SummaryRows.Count(r => r.SupplierType == "API" && r.IsActive && r.Range1SaleAmount > 0);

            viewModel.R1OfflinePax = viewModel.SummaryRows.Where(r => r.SupplierType == "Offline").Sum(r => r.Range1Pax);
            viewModel.R1OfflineAmount = viewModel.SummaryRows.Where(r => r.SupplierType == "Offline").Sum(r => r.Range1SaleAmount);
            viewModel.R1OfflineActiveCount = viewModel.SummaryRows.Count(r => r.SupplierType == "Offline" && r.IsActive && r.Range1SaleAmount > 0);

            viewModel.R2ApiPax = viewModel.SummaryRows.Where(r => r.SupplierType == "API").Sum(r => r.Range2Pax);
            viewModel.R2ApiAmount = viewModel.SummaryRows.Where(r => r.SupplierType == "API").Sum(r => r.Range2SaleAmount);
            viewModel.R2ApiActiveCount = viewModel.SummaryRows.Count(r => r.SupplierType == "API" && r.IsActive && r.Range2SaleAmount > 0);

            viewModel.R2OfflinePax = viewModel.SummaryRows.Where(r => r.SupplierType == "Offline").Sum(r => r.Range2Pax);
            viewModel.R2OfflineAmount = viewModel.SummaryRows.Where(r => r.SupplierType == "Offline").Sum(r => r.Range2SaleAmount);
            viewModel.R2OfflineActiveCount = viewModel.SummaryRows.Count(r => r.SupplierType == "Offline" && r.IsActive && r.Range2SaleAmount > 0);

            viewModel.SummaryRows = viewModel.SummaryRows.OrderBy(r => r.SupplierType).ThenByDescending(r => r.Range2SaleAmount).ToList();

            return View(viewModel);
        }

        [HttpGet]
        public async Task<IActionResult> SupplierList(string type, DateTime? r1Start, DateTime? r1End, DateTime? r2Start, DateTime? r2End)
        {
            var today = DateTime.Today;
            var thisMonthStart = new DateTime(today.Year, today.Month, 1);

            DateTime range1Start = r1Start ?? thisMonthStart.AddMonths(-1);
            DateTime range1End = r1End ?? thisMonthStart.AddDays(-1);
            DateTime range2Start = r2Start ?? thisMonthStart;
            DateTime range2End = r2End ?? today;

            var rawSuppliers = await _salesRepo.GetAllSuppliersAsync();
            var allSuppliers = rawSuppliers
                .Where(s => !string.IsNullOrWhiteSpace(s.SupplierName) &&
                            !s.SupplierName.Contains("test", StringComparison.OrdinalIgnoreCase) &&
                            s.SupplierType.Equals(type, StringComparison.OrdinalIgnoreCase)) 
                .ToList();

            var range1Sales = await _salesRepo.GetSalesSummaryAsync(range1Start, range1End);
            var range2Sales = await _salesRepo.GetSalesSummaryAsync(range2Start, range2End);
            var apiInventory = _cache.Get<List<StandardInventoryRecord>>("SupplierApiInventoryCache") ?? new List<StandardInventoryRecord>();

            var inventorySummary = apiInventory
    .GroupBy(i => $"{Normalize(i.SupplierName)}|{Normalize(i.SupplierType)}")
    .ToDictionary(g => g.Key, g => new
    {
        UniqueSectorsCount = g.Select(x => $"{x.Origin}-{x.Destination}").Distinct().Count(),
        MinDate = g.SelectMany(x => x.AvailableDates).DefaultIfEmpty(DateTime.MinValue).Min(),
        MaxDate = g.SelectMany(x => x.AvailableDates).DefaultIfEmpty(DateTime.MinValue).Max()
    });

            var viewModel = new SupplierDashboardViewModel
            {
                DateRange1Label = GenerateSmartDateLabel(range1Start, range1End),
                DateRange2Label = GenerateSmartDateLabel(range2Start, range2End)
            };

            string GetMappedName(string rawName)
            {
                if (string.IsNullOrWhiteSpace(rawName)) return "unknown";
                var normalized = rawName.Trim();
                if (normalized.Equals("Trip Foro", StringComparison.OrdinalIgnoreCase)) return "tripforo";
                if (normalized.Equals("Safiya Travels", StringComparison.OrdinalIgnoreCase)) return "safiyatravels";
                return normalized.ToLower();
            }

            foreach (var masterVendor in allSuppliers.GroupBy(s => new { s.SupplierName, s.SupplierType }).Select(g => g.First()))
            {
                var mappedMasterName = GetMappedName(masterVendor.SupplierName);

                var r1DataMatches = range1Sales.Where(x =>
    GetMappedName(x.SupplierName) == mappedMasterName &&
    Normalize(x.SupplierType) == Normalize(masterVendor.SupplierType));

                var r2DataMatches = range2Sales.Where(x =>
                    GetMappedName(x.SupplierName) == mappedMasterName &&
                    Normalize(x.SupplierType) == Normalize(masterVendor.SupplierType));

                var row = new SupplierSummaryRow
                {
                    SupplierName = masterVendor.SupplierName,
                    SupplierType = masterVendor.SupplierType,
                    IsActive = masterVendor.Status,
                    // 👇 Sum the data here!
                    Range1Pax = r1DataMatches.Sum(x => x.TotalPax),
                    Range1SaleAmount = r1DataMatches.Sum(x => x.TotalSaleAmount),
                    Range2Pax = r2DataMatches.Sum(x => x.TotalPax),
                    Range2SaleAmount = r2DataMatches.Sum(x => x.TotalSaleAmount),
                    TotalAvailableSectors = 0,
                    AvailableDateRange = "-"
                };

                string invKey = $"{mappedMasterName}|{Normalize(masterVendor.SupplierType)}";
                if (inventorySummary.TryGetValue(invKey, out var invStats))
                {
                    row.TotalAvailableSectors = invStats.UniqueSectorsCount;

                    // FIX: Only apply the date format if it is a valid calendar date
                    if (invStats.MinDate > DateTime.MinValue)
                    {
                        row.AvailableDateRange = $"{invStats.MinDate:dd MMM yyyy} - {invStats.MaxDate:dd MMM yyyy}";
                    }
                    // (If it is MinValue, it gracefully ignores this and keeps the default "-" we set earlier)
                }

                if (row.SupplierType.Equals("Offline", StringComparison.OrdinalIgnoreCase))
                {
                    // Check if they have ANY sales in either selected range
                    bool hasSales = row.Range1SaleAmount > 0 || row.Range2SaleAmount > 0;

                    // Check if they have valid inventory dates (not just empty sectors)
                    bool hasValidDates = row.AvailableDateRange != "-";

                    // Override the DB status: They are only Active if one of these is true
                    row.IsActive = hasSales || hasValidDates;
                }

                viewModel.SummaryRows.Add(row);
            }

            viewModel.SummaryRows = viewModel.SummaryRows.OrderByDescending(r => r.Range2SaleAmount).ToList();

            ViewData["ListType"] = type.ToUpper();
            return View(viewModel);
        }

        [HttpGet]
        public async Task<IActionResult> SupplierDetails(string supplierName, string supplierType, DateTime? r1Start, DateTime? r1End, DateTime? r2Start, DateTime? r2End)
        {
            var today = DateTime.Today;
            var thisMonthStart = new DateTime(today.Year, today.Month, 1);

            DateTime range1Start = r1Start ?? thisMonthStart.AddMonths(-1);
            DateTime range1End = r1End ?? thisMonthStart.AddDays(-1);
            DateTime range2Start = r2Start ?? thisMonthStart;
            DateTime range2End = r2End ?? today;

            string GetMappedName(string rawName)
            {
                if (string.IsNullOrWhiteSpace(rawName)) return "unknown";
                var normalized = rawName.Trim();
                if (normalized.Equals("Trip Foro", StringComparison.OrdinalIgnoreCase)) return "tripforo";
                if (normalized.Equals("Safiya Travels", StringComparison.OrdinalIgnoreCase)) return "safiyatravels";
                return normalized.ToLower();
            }

            string mappedSupplierName = GetMappedName(supplierName);
            string normalizedSupplierName = Normalize(supplierName);

            var range1Sales = await _salesRepo.GetSalesSummaryAsync(range1Start, range1End);
            var range2Sales = await _salesRepo.GetSalesSummaryAsync(range2Start, range2End);

            var r1SupplierSales = range1Sales.Where(x =>
                Normalize(x.SupplierName) == normalizedSupplierName &&
                Normalize(x.SupplierType) == Normalize(supplierType)).ToList();

            var r2SupplierSales = range2Sales.Where(x =>
                Normalize(x.SupplierName) == normalizedSupplierName &&
                Normalize(x.SupplierType) == Normalize(supplierType)).ToList();

            var apiInventory = _cache.Get<List<StandardInventoryRecord>>("SupplierApiInventoryCache") ?? new List<StandardInventoryRecord>();

            var supplierInventory = apiInventory.Where(x =>
                Normalize(x.SupplierName) == mappedSupplierName &&
                Normalize(x.SupplierType) == Normalize(supplierType)).ToList();

            var inventorySummary = supplierInventory
                .GroupBy(i => $"{i.Origin}-{i.Destination}")
                .ToDictionary(g => g.Key, g => new
                {
                    MinDate = g.SelectMany(x => x.AvailableDates).DefaultIfEmpty(DateTime.MinValue).Min(),
                    MaxDate = g.SelectMany(x => x.AvailableDates).DefaultIfEmpty(DateTime.MinValue).Max(),
                    DateCount = g.SelectMany(x => x.AvailableDates).Distinct().Count()
                });

            var allSectors = inventorySummary.Keys
                .Union(r1SupplierSales.Select(x => $"{x.Origin}-{x.Destination}"))
                .Union(r2SupplierSales.Select(x => $"{x.Origin}-{x.Destination}"))
                .Distinct();

            var viewModel = new SupplierSectorDetailsViewModel
            {
                SupplierName = supplierName,
                SupplierType = supplierType,
                DateRange1Label = GenerateSmartDateLabel(range1Start, range1End),
                DateRange2Label = GenerateSmartDateLabel(range2Start, range2End)
            };

            foreach (var sector in allSectors)
            {
                var r1Data = r1SupplierSales.FirstOrDefault(x => $"{x.Origin}-{x.Destination}" == sector);
                var r2Data = r2SupplierSales.FirstOrDefault(x => $"{x.Origin}-{x.Destination}" == sector);

                var row = new SectorDetailRow
                {
                    Sector = sector,
                    Range1Pax = r1Data?.TotalPax ?? 0,
                    Range1SaleAmount = r1Data?.TotalSaleAmount ?? 0,
                    Range2Pax = r2Data?.TotalPax ?? 0,
                    Range2SaleAmount = r2Data?.TotalSaleAmount ?? 0,
                    StartDate = "-",
                    EndDate = "-"
                };

                if (inventorySummary.TryGetValue(sector, out var invStats))
                {
                    row.DateCount = invStats.DateCount;
                    row.EstimatedPax = invStats.DateCount * 150;

                    if (invStats.MinDate > DateTime.MinValue)
                    {
                        row.StartDate = invStats.MinDate.ToString("dd MMM yyyy");
                        row.EndDate = invStats.MaxDate.ToString("dd MMM yyyy");
                    }
                }

                if (row.Range1SaleAmount > 0)
                {
                    row.PercentageChange = (double)((row.Range2SaleAmount - row.Range1SaleAmount) / row.Range1SaleAmount) * 100;
                }
                else if (row.Range2SaleAmount > 0)
                {
                    row.PercentageChange = 100; 
                }

                viewModel.SectorRows.Add(row);
            }

            viewModel.SectorRows = viewModel.SectorRows.OrderByDescending(r => r.Range2SaleAmount).ToList();

            return View(viewModel);
        }

        [HttpGet]
        public IActionResult SectorInventoryReport(DateTime? start, DateTime? end)
        {
            var today = DateTime.Today;
            // Default to current month if no dates are passed
            DateTime startDate = start ?? new DateTime(today.Year, today.Month, 1);
            DateTime endDate = end ?? new DateTime(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month));

            var apiInventory = _cache.Get<List<StandardInventoryRecord>>("SupplierApiInventoryCache") ?? new List<StandardInventoryRecord>();

            var viewModel = new SectorInventoryViewModel
            {
                DateRangeLabel = GenerateSmartDateLabel(startDate, endDate)
            };

            // 1. Filter valid dates first to ensure accuracy based on Travel Date
            var processedInventory = apiInventory.Select(inv => new
            {
                Sector = $"{inv.Origin}-{inv.Destination}",
                Supplier = inv.SupplierName,
                // Only keep Travel Dates that fall within the selected filter window
                ValidDates = inv.AvailableDates?.Where(d => d.Date >= startDate.Date && d.Date <= endDate.Date).ToList() ?? new List<DateTime>()
            }).Where(inv => inv.ValidDates.Any()).ToList();

            // 2. Group by BOTH Sector and Supplier to create distinct rows!
            var grouped = processedInventory.GroupBy(x => new { x.Sector, x.Supplier });

            foreach (var g in grouped)
            {
                var allDates = g.SelectMany(x => x.ValidDates)
                                .Distinct()
                                .OrderBy(d => d)
                                .ToList();

                viewModel.InventoryRows.Add(new SectorInventoryRow
                {
                    Sector = g.Key.Sector,
                    SupplierName = g.Key.Supplier,
                    AvailableDays = allDates.Count,
                    // Format dates as a comma-separated string
                    AvailableDates = string.Join(", ", allDates.Select(d => d.ToString("dd MMM yyyy")))
                });
            }

            // Sort alphabetically by Sector, then by Supplier
            viewModel.InventoryRows = viewModel.InventoryRows.OrderBy(r => r.Sector).ThenBy(r => r.SupplierName).ToList();

            return View(viewModel);
        }
    }
}
