using inventory_analytics_tool.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace inventory_analytics_tool.Controllers
{
    [Authorize(Roles = "Admin")]
    public class FareAuditController : Controller
    {
        private readonly IMemoryCache _cache;

        public FareAuditController(IMemoryCache cache)
        {
            _cache = cache;
        }

        [HttpGet]
        public IActionResult Index()
        {
            // This automatically looks for Views/FareAudit/Index.cshtml
            return View();
        }

        [HttpGet]
        public IActionResult GetLeadLevelDashboardData(string sectorType = "All", string lead = "All")
        {
            try
            {
                if (_cache.TryGetValue("LeadTrackersCache", out Dictionary<string, inventory_analytics_tool.Workers.FareAuditWorker.LeadDataTracker> leadTrackers))
                {
                    _cache.TryGetValue("FDestList", out List<FDestData> cachedFDest);
                    cachedFDest ??= new List<FDestData>();

                    var finalData = new List<LeadLevelDashboardModel>();

                    foreach (var tracker in leadTrackers.Values)
                    {
                        // 1. Filter by Lead
                        if (lead != "All" && lead != "All Leads" && !string.IsNullOrEmpty(lead) && !string.Equals(tracker.LeadName, lead, StringComparison.OrdinalIgnoreCase))
                            continue;

                        // 2. Helper to accurately count HashSets based on Domestic/Intl
                        int CountFiltered(HashSet<string> sectors)
                        {
                            // CRITICAL FIX: If "All" is selected, just return the raw count immediately.
                            if (string.IsNullOrEmpty(sectorType) || sectorType == "All" || sectorType == "Select")
                                return sectors.Count;

                            return sectors.Count(s =>
                            {
                                var dest = cachedFDest.FirstOrDefault(f => $"{f.FromCode?.Trim()}-{f.ToCode?.Trim()}" == s);

                                // Default to Domestic (false) if we can't find it in the FDest list
                                bool isIntl = dest != null && dest.IsInternationalNew;

                                return string.Equals(sectorType, "International", StringComparison.OrdinalIgnoreCase) ? isIntl : !isIntl;
                            });
                        }

                        // CRITICAL FIX 2: Only add leads to the dashboard if they actually have some data
                        int assignedCount = CountFiltered(tracker.AssignedSectorsSet);
                        int purchasedCount = CountFiltered(tracker.PurchasedSectorsSet);
                        int liveCount = CountFiltered(tracker.LiveSectorsSet);

                        if (assignedCount > 0 || purchasedCount > 0 || liveCount > 0)
                        {
                            finalData.Add(new LeadLevelDashboardModel
                            {
                                LeadName = string.IsNullOrEmpty(tracker.LeadName) ? "Unassigned" : tracker.LeadName,
                                AssignedSectors = assignedCount,
                                PurchasedSectors = purchasedCount,
                                LiveSectors = liveCount,
                                FareSupportiveCount = CountFiltered(tracker.FareSupportiveSectorsSet),
                                SellingAtLossCount = CountFiltered(tracker.SellingAtLossSectorsSet),
                                SellingAtProfitCount = CountFiltered(tracker.SellingAtProfitSectorsSet),
                                D7NotLiveCount = CountFiltered(tracker.D7NotLiveSectorsSet),
                                BlockSectorCount = CountFiltered(tracker.BlockSectorsSet),
                                NewlyAddedCount = CountFiltered(tracker.NewlyAddedSectorsSet)
                            });
                        }
                    }
                    return Json(new { success = true, data = finalData });
                }

                return Json(new { success = true, data = new List<LeadLevelDashboardModel>() });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpGet]
        public IActionResult LeadCategoryDetails()
        {
            return View();
        }

        [HttpGet]
        public IActionResult GetLeadCategoryDetailsData(string lead, string category, string sectorType = "All")
        {
            try
            {
                if (string.IsNullOrWhiteSpace(lead) || string.IsNullOrWhiteSpace(category))
                    return Json(new { success = false, message = "Lead and category are required." });

                if (_cache.TryGetValue("LeadCategoryDetailsCache", out Dictionary<string, Dictionary<string, List<SectorCategoryDetailModel>>> allDetailedData))
                {
                    var leadKey = allDetailedData.Keys.FirstOrDefault(k => string.Equals(k, lead, StringComparison.OrdinalIgnoreCase));
                    if (leadKey != null)
                    {
                        var categoryKey = allDetailedData[leadKey].Keys.FirstOrDefault(k => string.Equals(k, category, StringComparison.OrdinalIgnoreCase));
                        if (categoryKey != null)
                        {
                            var categoryData = allDetailedData[leadKey][categoryKey];

                            // Filter Drill-down page by Sector Type
                            if (sectorType != "All" && sectorType != "Select" && !string.IsNullOrEmpty(sectorType))
                            {
                                _cache.TryGetValue("FDestList", out List<FDestData> fDestList);
                                fDestList ??= new List<FDestData>();

                                categoryData = categoryData.Where(x => {
                                    var dest = fDestList.FirstOrDefault(f => $"{f.FromCode?.Trim()}-{f.ToCode?.Trim()}" == x.Sector);
                                    bool isIntl = dest != null && dest.IsInternationalNew;

                                    return sectorType.Equals("International", StringComparison.OrdinalIgnoreCase) ? isIntl : !isIntl;
                                }).ToList();
                            }
                            return Json(new { success = true, data = categoryData });
                        }
                    }
                }
                return Json(new { success = true, data = new List<SectorCategoryDetailModel>() });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpGet]
        public IActionResult SectorAudit()
        {
            return View();
        }

        [HttpGet]
        public IActionResult GetSectorLevelDashboardData()
        {
            try
            {
                if (_cache.TryGetValue("SectorLevelAuditData", out List<SectorLevelDashboardModel> data) && data != null)
                {
                    return Json(new { success = true, data = data });
                }

                return Json(new { success = true, data = new List<SectorLevelDashboardModel>() });
            }
            catch (System.Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpGet]
        public IActionResult SectorDetails()
        {
            return View();
        }

       
        [HttpGet]
        public IActionResult GetSectorDetailsData(string sector)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(sector))
                {
                    return Json(new { success = false, message = "Sector is required." });
                }

                if (_cache.TryGetValue("DetailedPnrAuditData", out Dictionary<string, List<FareAuditDbRecord>> allDetailedData))
                {
                    // Normalize the incoming UI sector exactly the same way the worker does
                    string cleanSector = sector.Replace(" ", "").Replace("-", "").Replace("/", "").Trim().ToUpper();

                    if (allDetailedData.TryGetValue(cleanSector, out var sectorSpecificData))
                    {
                        return Json(new { success = true, data = sectorSpecificData });
                    }
                }

                // If no data is found for that sector, return an empty array safely
                return Json(new { success = true, data = new List<FareAuditDbRecord>() });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        // Add this inside FareAuditController.cs

        [HttpGet]
        public IActionResult IndexV2()
        {
            return View();
        }

        // Add this inside FareAuditController.cs

        [HttpGet]
        public IActionResult LeadCategoryDetailsV3()
        {
            return View();
        }
    }
}