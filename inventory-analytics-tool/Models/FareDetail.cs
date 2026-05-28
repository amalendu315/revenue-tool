namespace inventory_analytics_tool.Models
{
    public class FareDetail
    {
        public int Id { get; set; }
        public int SectorId { get; set; } // Foreign key linking back to SectorDetail
        public string Sector { get; set; }
        public string DOJ { get; set; } // Date of Journey
        public int AirlinesCount { get; set; }
        public int FlightCount { get; set; }
        public int TotalStrength { get; set; }
        public int Target { get; set; }
        public int Achieved { get; set; }
        public int AttemptCount { get; set; }
        public decimal TargetFare { get; set; }
        public decimal AvgPurFare { get; set; }
        public decimal LastOnlineFare { get; set; }
    }
}