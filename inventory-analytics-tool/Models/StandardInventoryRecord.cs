namespace inventory_analytics_tool.Models
{
    public class StandardInventoryRecord
    {
        public string SupplierName { get; set; } = string.Empty;
        public string SupplierType { get; set; } = "API"; // "API" or "Offline"
        public string Origin { get; set; } = string.Empty;
        public string Destination { get; set; } = string.Empty;
        public List<DateTime> AvailableDates { get; set; } = new List<DateTime>();
    }
}
