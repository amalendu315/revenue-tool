namespace inventory_analytics_tool.Models
{
    public class AttemptRecord
    {
        public int Id { get; set; }
        public int SectorId { get; set; }
        public string AqId { get; set; }
        public string ReqId { get; set; }
        public string Origin { get; set; }
        public string Destination { get; set; }
        public string TravelDate { get; set; }
        public DateTime RawTravelDate { get; set; } // Used internally to map API fares
        public string AirlineCode { get; set; }
        public string FlightCode { get; set; }
        public int Pax { get; set; }
        public string OnlineFare { get; set; }
        public string AutoQuote { get; set; }
        public string Status { get; set; }
        public string NegFare { get; set; }
        public string ConfStatus { get; set; }
        public string Remarks { get; set; }
        public string PNR { get; set; }
        public string OpenRemarks { get; set; }
        public string Lead { get; set; }
        public string EntryDate { get; set; }
        public DateTime RawEntryDate { get; set; }
    }
}
