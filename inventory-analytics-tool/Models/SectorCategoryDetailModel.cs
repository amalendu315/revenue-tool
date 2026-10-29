namespace inventory_analytics_tool.Models
{
    public class SectorCategoryDetailModel
    {
        public string Station { get; set; }
        public string Sector { get; set; }

        // Sector Aggregate Dates (Populated for ALL categories)
        public string AvailableFrom { get; set; }
        public string OpenTill { get; set; }
        public string InventoryTill { get; set; }
        public string LastUpdate { get; set; }

        // Flight-Specific Details (ONLY populated for Live & Fare Supportive)
        public string TravelDate { get; set; }
        public string FlightNo { get; set; }
        public double? OurPrice { get; set; }
        public double? OnlinePrice { get; set; }
    }
}