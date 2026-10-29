using inventory_analytics_tool.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace inventory_analytics_tool.InventoryProviders
{
    public class TripforoInventoryProvider : ISupplierInventoryProvider
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _config;
        private readonly ILogger<TripforoInventoryProvider> _logger;

        public string ProviderName => "Tripforo";

        public TripforoInventoryProvider(IHttpClientFactory httpClientFactory, IConfiguration config, ILogger<TripforoInventoryProvider> logger)
        {
            _httpClientFactory = httpClientFactory;
            _config = config;
            _logger = logger;
        }

        public async Task<List<StandardInventoryRecord>> FetchInventoryAsync(CancellationToken cancellationToken = default)
        {
            var results = new List<StandardInventoryRecord>();

            try
            {
                var client = _httpClientFactory.CreateClient();

                // 1. Build the specific Tripforo Request Body
                // Note: Move these credentials to your appsettings.json later!
                var requestBody = new
                {
                    Auth_Header = new
                    {
                        UserId = _config["Tripforo:UserId"] ?? "airiq",
                        Password = _config["Tripforo:Password"] ?? "3BB0C8CF63B4FCA8348E505AE8EA4AEFB9D45FA7",
                        IP_Address = "XXXXXXXXX", // Replace with your actual server IP if required
                        Request_Id = "Biplab",
                        IMEI_Number = "2232323232326"
                    }
                };

                var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

                // 2. Execute the Call
                var response = await client.PostAsync("http://api.tripforo.com/flight/AirAPIService.svc/JSONService/Air_SectorAvailabilityPI", content, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError($"Tripforo API failed with status code: {response.StatusCode}");
                    return results;
                }

                var jsonResponse = await response.Content.ReadAsStringAsync(cancellationToken);

                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var tripforoData = JsonSerializer.Deserialize<TripforoResponseRoot>(jsonResponse, options);

                if (tripforoData?.Response_Header?.Error_Code != "0000" || tripforoData.SectorsPIs == null)
                {
                    _logger.LogWarning($"Tripforo returned an error or empty data: {tripforoData?.Response_Header?.Error_Desc}");
                    return results;
                }

                // 3. Map the messy Tripforo data to our clean Standard Model
                foreach (var sector in tripforoData.SectorsPIs)
                {
                    if (string.IsNullOrEmpty(sector.Origin) || string.IsNullOrEmpty(sector.Destination)) continue;

                    var standardRecord = new StandardInventoryRecord
                    {
                        SupplierName = ProviderName,
                        SupplierType = "API",
                        Origin = sector.Origin.Trim().ToUpper(),
                        Destination = sector.Destination.Trim().ToUpper(),
                        AvailableDates = ParseTripforoDates(sector.AvailableDates)
                    };

                    // Only add if it actually has valid dates
                    if (standardRecord.AvailableDates.Any())
                    {
                        results.Add(standardRecord);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Exception in Tripforo provider: {ex.Message}");
            }

            return results;
        }

        // Helper to safely parse dates (assuming they come in as "MM/dd/yyyy" or comma-separated)
        private List<DateTime> ParseTripforoDates(string dateString)
        {
            var parsedDates = new List<DateTime>();
            if (string.IsNullOrWhiteSpace(dateString)) return parsedDates;

            // FIX 1: Split by the pipe character '|'
            var dateArray = dateString.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var d in dateArray)
            {
                // FIX 2: Force the parser to understand US format (MM/dd/yyyy)
                if (DateTime.TryParse(d.Trim(), CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.None, out DateTime validDate))
                {
                    parsedDates.Add(validDate.Date);
                }
            }

            return parsedDates.Distinct().OrderBy(d => d).ToList();
        }

        // --- INTERNAL CLASSES TO DESERIALIZE TRIPFORO JSON ---
        private class TripforoResponseRoot
        {
            public TripforoResponseHeader Response_Header { get; set; }
            public List<TripforoSector> SectorsPIs { get; set; }
        }

        private class TripforoResponseHeader
        {
            public string Error_Code { get; set; }
            public string Error_Desc { get; set; }
        }

        private class TripforoSector
        {
            public string AvailableDates { get; set; }
            public string Destination { get; set; }
            public string Origin { get; set; }
            public string MaxTravelDate { get; set; }
        }
    }
}