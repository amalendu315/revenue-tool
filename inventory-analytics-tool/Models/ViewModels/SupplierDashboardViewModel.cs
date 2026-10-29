namespace inventory_analytics_tool.Models.ViewModels
{
    public class SupplierDashboardViewModel
    {
        public string DateRange1Label { get; set; } = string.Empty;
        public string DateRange2Label { get; set; } = string.Empty;

        // TOP LEVEL SUMMARY: RANGE 1
        public int R1ApiPax { get; set; }
        public decimal R1ApiAmount { get; set; }
        public int R1ApiActiveCount { get; set; }

        public int R1OfflinePax { get; set; }
        public decimal R1OfflineAmount { get; set; }
        public int R1OfflineActiveCount { get; set; }

        // TOP LEVEL SUMMARY: RANGE 2
        public int R2ApiPax { get; set; }
        public decimal R2ApiAmount { get; set; }
        public int R2ApiActiveCount { get; set; }

        public int R2OfflinePax { get; set; }
        public decimal R2OfflineAmount { get; set; }
        public int R2OfflineActiveCount { get; set; }

        public List<SupplierSummaryRow> SummaryRows { get; set; } = new List<SupplierSummaryRow>();
    }

    public class SupplierSummaryRow
    {
        public string SupplierName { get; set; } = string.Empty;
        public string SupplierType { get; set; } = string.Empty; // "API" or "Offline"
        public bool IsActive { get; set; }

        public int TotalAvailableSectors { get; set; }
        public string AvailableDateRange { get; set; } = "-";

        // Range 1 (e.g., Last Month)
        public int Range1Pax { get; set; }
        public decimal Range1SaleAmount { get; set; }

        // Range 2 (e.g., Current Month)
        public int Range2Pax { get; set; }
        public decimal Range2SaleAmount { get; set; }
    }
}
