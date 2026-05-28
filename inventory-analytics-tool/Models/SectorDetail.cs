namespace inventory_analytics_tool.Models
{
    public class SectorDetail
    {
        public int Id { get; set; }
        public int FestivalId { get; set; } // Foreign key to link with FestivalEvent
        public string Sector { get; set; }
        public string DateRange { get; set; }
        public int AirlinesCount { get; set; }
        public int FlightCount { get; set; }
        public int TotalStrength { get; set; }
        public int Target { get; set; }
        public int Achieved { get; set; }
        public int AttemptCount { get; set; }
        public List<string> AirlinesBreakdown { get; set; } = new List<string>();
    }
}