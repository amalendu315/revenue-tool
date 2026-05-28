namespace inventory_analytics_tool.Models
{
    // This represents the EXACT data the Dashboard UI needs.
    public class DashboardUiEvent
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string EventDate { get; set; }
        public int SectorsCovered { get; set; }
        public bool IsImportant { get; set; }
        public List<string> AirlinesBreakdown { get; set; }
        public List<DailyStat> DailyStats { get; set; }
        public Dictionary<string, int> AirlineAchieved { get; set; }
    }

    public class DailyStat
    {
        public string Date { get; set; }
        public int TargetSeat { get; set; }
        public int Achieved { get; set; }
        public int FlightCount { get; set; }
        public string Priority { get; set; }
    }
}