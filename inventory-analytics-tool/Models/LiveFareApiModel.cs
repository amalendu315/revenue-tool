namespace inventory_analytics_tool.Models
{
    public class CcLoopRequest
    {
        public string Sectors { get; set; }
        public string FlightDate { get; set; }
    }

    public class CcLoopResponseObj
    {
        public List<CcLoopFlight> flights { get; set; }
    }

    public class CcLoopFlight
    {
        public string airline { get; set; }
        public string AirlineCode { get; set; }
        public string FlightNumber { get; set; }
        public string Origin { get; set; }
        public string Destination { get; set; }
        public string DepartureDateTime { get; set; }
        public string ArrivalDateTime { get; set; }
        public double BaseAmount { get; set; }
        public double TotalTaxAmount { get; set; }
        public string Source { get; set; }
    }
}