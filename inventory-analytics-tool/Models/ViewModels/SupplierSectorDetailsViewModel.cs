namespace inventory_analytics_tool.Models.ViewModels
{
    public class SupplierSectorDetailsViewModel
    {
        public string SupplierName { get; set; } = string.Empty;
        public string SupplierType { get; set; } = string.Empty;
        public string DateRange1Label { get; set; } = string.Empty;
        public string DateRange2Label { get; set; } = string.Empty;

        public List<SectorDetailRow> SectorRows { get; set; } = new List<SectorDetailRow>();
    }

    public class SectorDetailRow
    {
        public string Sector { get; set; } = string.Empty;
        public string StartDate { get; set; } = "-";
        public string EndDate { get; set; } = "-";
        public int DateCount { get; set; }
        public int EstimatedPax { get; set; }

        public int Range1Pax { get; set; }
        public decimal Range1SaleAmount { get; set; }

        public int Range2Pax { get; set; }
        public decimal Range2SaleAmount { get; set; }

        public double PercentageChange { get; set; }
    }
}
