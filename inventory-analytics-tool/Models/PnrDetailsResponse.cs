using System;
using System.Collections.Generic;

namespace inventory_analytics_tool.Models
{
    public class PnrDetailsResponse
    {
        public string Code { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public List<PnrData> Data { get; set; } = new();
    }

    public class PnrData
    {
        public string PNR { get; set; } = string.Empty;
        public string AirLineName { get; set; } = string.Empty;
        public string DestinationName { get; set; } = string.Empty;
        public DateTime? TravelDateTime { get; set; } // Added ?
        public int Qty { get; set; }
        public int TicketID { get; set; }
        public DateTime? FareLogLastUpdateDate { get; set; } // Added ?
        public double Price { get; set; }
        public string FlightNo { get; set; } = string.Empty;
        public string StationOwner { get; set; } = string.Empty;
        public OnlineSearchData? OnlineSearch { get; set; }
    }

    public class OnlineSearchData
    {
        public int TicketID { get; set; }
        public int FDestID { get; set; }
        public string Destination { get; set; } = string.Empty;
        public int AirlineID { get; set; }
        public string Airline { get; set; } = string.Empty;
        public string PNR { get; set; } = string.Empty;
        public double Price { get; set; }
        public string aCode { get; set; } = string.Empty;
        public string FlightNo { get; set; } = string.Empty;
        public string FlightNo2 { get; set; } = string.Empty;
        public string TravelDateTime { get; set; } = string.Empty;
        public string NameListDate { get; set; } = string.Empty;
        public string ArrivalTime { get; set; } = string.Empty;
        public int TicketOwnerID { get; set; }
        public int Qty { get; set; }
        public int Blocked { get; set; }
        public double Rate { get; set; }
        public bool aStatus { get; set; }
        public string RetFlightNo { get; set; } = string.Empty;
        public string RetTravelDt { get; set; } = string.Empty;
        public string RetDepTime { get; set; } = string.Empty;
        public string RetArivalTime { get; set; } = string.Empty;
        public bool IsInternational { get; set; }
        public string VisibleType { get; set; }
        public int AirportID { get; set; }
        public string SectorFromCode { get; set; } = string.Empty;
        public string SectorToCode { get; set; } = string.Empty;
        public bool? IsNewSector { get; set; }
        public DateTime? EndDateOfNewSector { get; set; }
        public int StationID { get; set; }
        public string CityAirport { get; set; } = string.Empty;
        public string AirportName { get; set; } = string.Empty;
        public string CityAirport2 { get; set; } = string.Empty;
        public string AirportName2 { get; set; } = string.Empty;
        public bool IsInternationalNew { get; set; }
        public double? InfantCharge { get; set; }
        public string CabinLug { get; set; } = string.Empty;
        public string HandLug { get; set; } = string.Empty;
    }
}