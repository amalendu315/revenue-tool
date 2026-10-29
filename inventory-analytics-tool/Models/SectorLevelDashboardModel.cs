using System;
using System.Text.Json.Serialization;

namespace inventory_analytics_tool.Models
{
    public class SectorLevelDashboardModel
    {
        public string SectorCode { get; set; } = string.Empty;
        public string LeadName { get; set; } = "Unassigned";
        public int AssignedSectors { get; set; } = 0;
        public int PurchasedSectors { get; set; } = 0;
        public int LiveSectors { get; set; } = 0;
        public int FareSupportiveCount { get; set; } = 0;
        public int D7NotLiveCount { get; set; } = 0;
        public string OpenTillDate { get; set; } = "N/A";
        public int DaysOpen { get; set; } = 0;
        public double AvgPnrPerDay { get; set; } = 0;
        public double AvgPnrOpenPerDay { get; set; } = 0;
        public int TotalPnrs { get; set; } = 0;
        public int LivePnrCount { get; set; } = 0;
        public int HiddenPnrCount { get; set; } = 0;
        public double HiddenPercentage { get; set; } = 0;
        public int FareErrorPnrs { get; set; } = 0;
        public int ManagedFares { get; set; } = 0;

        // Used internally for date math, won't be sent to the browser
        [JsonIgnore]
        public DateTime? MaxTravelDate { get; set; }
    }
}