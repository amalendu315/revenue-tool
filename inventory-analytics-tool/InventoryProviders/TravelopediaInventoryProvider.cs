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
    public class TravelopediaInventoryProvider : ISupplierInventoryProvider
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _config;
        private readonly ILogger<TravelopediaInventoryProvider> _logger;

        // Ensure this exactly matches the name in your Master Supplier Database
        public string ProviderName => "TRAVELOPEDIA INTERNATIONAL PRIVATE LIMITED";

        public TravelopediaInventoryProvider(IHttpClientFactory httpClientFactory, IConfiguration config, ILogger<TravelopediaInventoryProvider> logger)
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

                // 1. Build the Auth Payload from configuration
                var requestBody = new
                {
                    Auth_Header = new
                    {
                        UserId = _config["Travelopedia:UserId"] ?? "tpairiq",
                        Password = _config["Travelopedia:Password"] ?? "",
                        Request_Id = _config["Travelopedia:Request_Id"] ?? "",
                        IP_Address = _config["Travelopedia:IP_Address"] ?? "",
                        IMEI_Number = _config["Travelopedia:IMEI_Number"] ?? ""
                    }
                };

                var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

                // 2. Execute the POST Call
                var response = await client.PostAsync("https://api.travelopediaonline.com/Flight/AirAPIService.svc/JSONService/Air_SectorAvailabilityPI", content, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError($"Travelopedia API failed with status code: {response.StatusCode}");
                    return results;
                }

                var jsonResponse = await response.Content.ReadAsStringAsync(cancellationToken);
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

                var data = JsonSerializer.Deserialize<TravelopediaResponseRoot>(jsonResponse, options);

                // Check for successful status (0000) and ensure payload exists
                if (data?.Response_Header?.Error_Code != "0000" || data.SectorsPIs == null)
                {
                    _logger.LogWarning($"Travelopedia returned an error or empty data: {data?.Response_Header?.Error_Desc}");
                    return results;
                }

                // 3. Map to Standard Inventory Record
                foreach (var sector in data.SectorsPIs)
                {
                    if (string.IsNullOrEmpty(sector.Origin) || string.IsNullOrEmpty(sector.Destination)) continue;

                    var standardRecord = new StandardInventoryRecord
                    {
                        SupplierName = ProviderName,
                        SupplierType = "API",
                        Origin = sector.Origin.Trim().ToUpper(),
                        Destination = sector.Destination.Trim().ToUpper(),
                        AvailableDates = ParseDates(sector.AvailableDates)
                    };

                    // Only add if there are valid parsed dates
                    if (standardRecord.AvailableDates.Any())
                    {
                        results.Add(standardRecord);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Exception in Travelopedia provider: {ex.Message}");
            }

            return results;
        }

        // Helper to parse pipe-delimited US formatted dates (MM/dd/yyyy)
        private List<DateTime> ParseDates(string dateString)
        {
            var parsedDates = new List<DateTime>();
            if (string.IsNullOrWhiteSpace(dateString)) return parsedDates;

            var dateArray = dateString.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var d in dateArray)
            {
                if (DateTime.TryParse(d.Trim(), CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.None, out DateTime validDate))
                {
                    parsedDates.Add(validDate.Date);
                }
            }

            return parsedDates.Distinct().OrderBy(d => d).ToList();
        }

        // --- INTERNAL CLASSES TO DESERIALIZE TRAVELOPEDIA JSON ---
        private class TravelopediaResponseRoot
        {
            public TravelopediaResponseHeader Response_Header { get; set; }
            public List<TravelopediaSector> SectorsPIs { get; set; }
        }

        private class TravelopediaResponseHeader
        {
            public string Error_Code { get; set; }
            public string Error_Desc { get; set; }
        }

        private class TravelopediaSector
        {
            public string AvailableDates { get; set; }
            public string Destination { get; set; }
            public string Origin { get; set; }
        }
    }
}