namespace inventory_analytics_tool.Models
{
    public class LeadLevelDashboardModel
    {
        public string LeadName { get; set; } = string.Empty;
        public int AssignedSectors { get; set; }
        public int PurchasedSectors { get; set; }
        public int LiveSectors { get; set; }

        // These will be "N/A" or 0 for now until we integrate the scrape data
        public int FareSupportiveCount { get; set; } = 0;
        public int D7NotLiveCount { get; set; } = 0;
        public int BlockSectorCount { get; set; } = 0;
        public int NewlyAddedCount { get; set; } = 0;

        public int SellingAtLossCount { get; set; }
        public int SellingAtProfitCount { get; set; }
        public int NewlyAddedSectors { get; set; }

        public string LastUpdatedTime { get; set; } = string.Empty;
    }
}