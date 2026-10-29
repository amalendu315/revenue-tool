namespace inventory_analytics_tool.Models
{
    public class SalesSummaryRecord
    {
        public string SupplierName { get; set; } = string.Empty;
        public string SupplierType { get; set; } = string.Empty; // "API" or "Offline"
        public string Origin { get; set; } = string.Empty;
        public string Destination { get; set; } = string.Empty;
        public int TotalPax { get; set; }
        public decimal TotalSaleAmount { get; set; }
    }
}
