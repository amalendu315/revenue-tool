namespace inventory_analytics_tool.Models
{
    public class FareAuditDbRecord
    {
        public int id { get; set; }
        public string bookingId { get; set; } = string.Empty;
        public string ticketId { get; set; } = string.Empty;
        public string fareLogId { get; set; } = string.Empty;
        public string logType { get; set; } = string.Empty;
        public string source { get; set; } = string.Empty;
        public DateTime? requestDateTime { get; set; }
        public string pnr { get; set; } = string.Empty;
        public string flightSector { get; set; } = string.Empty;
        public string flightNumber { get; set; } = string.Empty;
        public DateTime? travelDateTime { get; set; }
        public decimal? fsSameFlightFare { get; set; }
        public decimal? fsLowestFlightFare { get; set; }
        public int? fsAvailableStock { get; set; }
        public string fsErrorMessage { get; set; } = string.Empty;
        public decimal? fsAverageFare { get; set; }
        public decimal? aoSameFlightFare { get; set; }
        public decimal? aoLowestFare { get; set; }
        public decimal? aoAverageFare { get; set; }
        public string aoErrorMessage { get; set; } = string.Empty;
        public int? flightWiseTotalSeatsPurchased { get; set; }
        public int? flightWiseTotalSeatsSold { get; set; }
        public int? flightWiseTotalSeatsLeft { get; set; }
        public decimal? flightWiseAverageCost { get; set; }
        public int? routeWiseSeatsPurchased { get; set; }
        public int? routeWiseSeatsSold { get; set; }
        public int? routeWiseSeatsLeft { get; set; }
        public decimal? routeWiseAverageCost { get; set; }
        public decimal? averageSellFare { get; set; }
        public string status { get; set; } = string.Empty;
        public string remark { get; set; } = string.Empty;
        public DateTime? taskCompletedDateTime { get; set; }
        public int? fsSameFlightStock { get; set; }
        public string fsLowestFareFlightNumber { get; set; } = string.Empty;
        public string fsLowestFareFlightDepartureTime { get; set; } = string.Empty;
        public string aoLowestFareFlightNumber { get; set; } = string.Empty;
        public int? aoSameFlightStock { get; set; }
        public string aoLowestFareFlightDepartureTime { get; set; } = string.Empty;
        public string fareAuditRemarks { get; set; } = string.Empty;
        public int? aoAvailableStock { get; set; }
        public decimal? actualManagedFare { get; set; }
        public int? bookedPaxQty { get; set; }
        public decimal? fsWindowedLowestFare { get; set; }
        public string fsWindowedLowestFareFlightNumber { get; set; } = string.Empty;
        public string fsWindowedLowestFareDepartureTime { get; set; } = string.Empty;
        public decimal? aoWindowedLowestFare { get; set; }
        public string aoWindowedLowestFareFlightNumber { get; set; } = string.Empty;
        public string aoWindowedLowestFareDepartureTime { get; set; } = string.Empty;
    }
}
