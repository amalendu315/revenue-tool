using System.Collections.Generic;

namespace inventory_analytics_tool.Models.ViewModels
{
    public class SectorInventoryRow
    {
        public string Sector { get; set; } = string.Empty;
        public string SupplierName { get; set; } = string.Empty;
        public int AvailableDays { get; set; }
        public string AvailableDates { get; set; } = string.Empty;
    }

    public class SectorInventoryViewModel
    {
        public string DateRangeLabel { get; set; } = string.Empty;
        public List<SectorInventoryRow> InventoryRows { get; set; } = new List<SectorInventoryRow>();
    }
}