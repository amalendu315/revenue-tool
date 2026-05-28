namespace inventory_analytics_tool.Models
{
    public class FestivalEvent
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public DateTime EventDate { get; set; }
        public string DateRange { get; set; }
        public int DaysCount { get; set; }
        public int SectorsCovered { get; set; }
        public int FlightCount { get; set; }
        public int PaxCapacity { get; set; }
        public int TargetSeat { get; set; }
        public int Achieved { get; set; }
        public bool IsImportant { get; set; }
        public string Category { get; set; }
        public string CategoryClass { get; set; }
        public string ThemeColor { get; set; }
        public List<string> AirlinesBreakdown { get; set; }
    }

    // --- FESTIVAL MASTER API MODELS ---
    public class FestivalMasterResponse
    {
        public string Code { get; set; }
        public string Status { get; set; }
        public List<FestivalMasterData> Data { get; set; }
    }

    public class FestivalMasterData
    {
        public int blackoutTypeId { get; set; }
        public string name { get; set; }
        public List<FestivalCalendar> calendars { get; set; }
    }

    public class FestivalCalendar
    {
        public int calendarId { get; set; }
        public DateTime date { get; set; }
        public string description { get; set; }
        public List<DateTime> blackoutDates { get; set; }
    }

    // --- EVENT LISTS API MODELS ---
    public class EventListResponse
    {
        public string Code { get; set; }
        public string Status { get; set; }
        public List<EventListData> Data { get; set; }
    }

    // Level 1: Event Summary
    public class EventListData
    {
        public int EventId { get; set; }
        public string Event { get; set; }
        public DateTime Date { get; set; }
        public string Day { get; set; }
        public List<DateTime> DateRange { get; set; }
        public int TargetSeat { get; set; }
        public int Achieved { get; set; }
        public int FlightCount { get; set; }
        public List<EventFlight> Flights { get; set; }
        public List<EventAirline> Airlines { get; set; }
        public List<EventSectorData> Details { get; set; }
    }

    // Level 2: Sector Breakdowns (e.g. DEL to DXB)
    public class EventSectorData
    {
        public string FromCode { get; set; }
        public string ToCode { get; set; }
        public DateTime Date { get; set; }
        public string Day { get; set; }
        public List<DateTime> DateRange { get; set; }
        public int TotalStrength { get; set; }
        public int TargetSeat { get; set; }
        public int Achieved { get; set; }
        public int FlightCount { get; set; }
        public List<EventFlight> Flights { get; set; }
        public List<EventAirline> Airlines { get; set; }
        public List<EventTravelDateData> Details { get; set; }
    }

    // Level 3: Specific Travel Date Details
    public class EventTravelDateData
    {
        public string FromCode { get; set; }
        public string ToCode { get; set; }
        public DateTime TravelDate { get; set; }
        public int TotalStrength { get; set; }
        public int TargetSeat { get; set; }
        public int Achieved { get; set; }
        public int FlightCount { get; set; }
        public List<EventFlight> Flights { get; set; }
        public List<EventAirline> Airlines { get; set; }
        public double TargetFare { get; set; }
        public double AvgPurchaseFare { get; set; }
        public double LastOnlineFare { get; set; }
        public int AttemptCount { get; set; }
        public string BlackoutPriority { get; set; }
    }

    public class EventFlight
    {
        public string AirlineCode { get; set; }
        public string FlightNo { get; set; }
        public int Achieved { get; set; }
    }

    public class EventAirline
    {
        public string AirlineCode { get; set; }
        public string AirlineName { get; set; }
        public int Achieved { get; set; }
    }

    public class FareTrendRecord
    {
        public double Fare { get; set; }
        public DateTime EntryDate { get; set; }
        public int AttemptCount { get; set; } // Add this line!
    }

    public class FlightFareRecord
    {
        public string Sector { get; set; }
        public DateTime TravelDate { get; set; }
        public string AirlineCode { get; set; }
        public string FlightNo { get; set; }
        public double LatestFare { get; set; }
        public DateTime EntryDate { get; set; }
    }

    public class FlightFareTrendRow
    {
        public string Sector { get; set; }
        public string DOJ { get; set; }
        public string AirlineCode { get; set; }
        public string FlightNo { get; set; }
        public double TargetFare { get; set; }
        public double LatestFare { get; set; }
        public string EntryDate { get; set; }
    }

    public class FDestApiResponse
    {
        public List<FDestData> Data { get; set; }
    }

    public class FDestData
    {
        public int FDestID { get; set; }
        public string FDestName { get; set; }
        public string FromCode { get; set; }
        public string ToCode { get; set; }
        public string InternalOwner { get; set; }
        public string StationName { get; set; }
        public bool IsInternationalNew { get; set; }
    }

    // CSV Reading Model
    public class UploadAttemptCsvRecord
    {
        public string AqId { get; set; } 
        public string GroupRaisedRefNo { get; set; }
        public string Origin { get; set; }
        public string Destination { get; set; }
        public string TravelDate { get; set; }
        public string AirlineCode { get; set; }
        public string FlightNumber { get; set; }
        public string Pax { get; set; }
        public string AutoQuote { get; set; }
        public string Status { get; set; }
        public string NegotiatedFare { get; set; }
        public string ConfirmedStatus { get; set; }
        public string Remarks { get; set; }
        public string PNR { get; set; }
        public string OpenRemarks { get; set; }
        public string Lead { get; set; }
    }

    // Database Insertion Model
    public class GroupRaisedEntryModel
    {
        public int? GroupRaisedEntryID { get; set; }
        public int? BlackoutFareFeedID { get; set; }
        public string GroupRaisedRefNo { get; set; }
        public string PNR { get; set; }
        public int RaisedPax { get; set; }
        public decimal GroupFare { get; set; }
        public string UserName { get; set; }
        public string Status { get; set; }
        public int? FDestID { get; set; }
        public DateTime RaisedDate { get; set; }
        public DateTime EntryDate { get; set; }
        public string Remarks { get; set; }
        public string OpenRemarks { get; set; }
        public string ConfirmationStatus { get; set; }
        public decimal NegotiatedFare { get; set; }
        public DateTime ModifiedDate { get; set; }
    }

}