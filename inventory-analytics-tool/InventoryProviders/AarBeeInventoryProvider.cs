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
    public class AarBeeInventoryProvider : ISupplierInventoryProvider
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _config;
        private readonly ILogger<AarBeeInventoryProvider> _logger;

        // Ensure this string exactly matches the SupplierName in your Master Supplier Database
        public string ProviderName => "Aar Bee Travels";

        public AarBeeInventoryProvider(IHttpClientFactory httpClientFactory, IConfiguration config, ILogger<AarBeeInventoryProvider> logger)
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

                // 1. Build the Auth Payload from appsettings.json
                var requestBody = new
                {
                    Auth_Header = new
                    {
                        UserId = _config["AarBee:UserId"] ?? "aarbeeairiq",
                        Password = _config["AarBee:Password"] ?? "",
                        Request_Id = _config["AarBee:Request_Id"] ?? "",
                        IP_Address = _config["AarBee:IP_Address"] ?? "",
                        IMEI_Number = _config["AarBee:IMEI_Number"] ?? ""
                    }
                };

                var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

                // 2. Execute the POST Call
                var response = await client.PostAsync("https://api.aarbeeworld.com/Flight/AirAPIService.svc/JSONService/Air_SectorAvailabilityPI", content, cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError($"Aar Bee Travels API failed with status code: {response.StatusCode}");
                    return results;
                }

                var jsonResponse = await response.Content.ReadAsStringAsync(cancellationToken);
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

                var data = JsonSerializer.Deserialize<AarBeeResponseRoot>(jsonResponse, options);

                // Check for successful error code ("0000") and non-null payload
                if (data?.Response_Header?.Error_Code != "0000" || data.SectorsPIs == null)
                {
                    _logger.LogWarning($"Aar Bee Travels returned an error or empty data: {data?.Response_Header?.Error_Desc}");
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

                    if (standardRecord.AvailableDates.Any())
                    {
                        results.Add(standardRecord);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Exception in Aar Bee Travels provider: {ex.Message}");
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

        // --- INTERNAL CLASSES TO DESERIALIZE AAR BEE TRAVELS JSON ---
        private class AarBeeResponseRoot
        {
            public AarBeeResponseHeader Response_Header { get; set; }
            public List<AarBeeSector> SectorsPIs { get; set; }
        }

        private class AarBeeResponseHeader
        {
            public string Error_Code { get; set; }
            public string Error_Desc { get; set; }
        }

        private class AarBeeSector
        {
            public string AvailableDates { get; set; }
            public string Destination { get; set; }
            public string Origin { get; set; }
        }
    }
}