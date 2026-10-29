using inventory_analytics_tool.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace inventory_analytics_tool.InventoryProviders
{
    public class TripWorkInventoryProvider : ISupplierInventoryProvider
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _config;
        private readonly ILogger<TripWorkInventoryProvider> _logger;

        // Ensure this matches the exact name used in your Master Supplier Database!
        public string ProviderName => "TRAVEL PICK.IO";

        public TripWorkInventoryProvider(IHttpClientFactory httpClientFactory, IConfiguration config, ILogger<TripWorkInventoryProvider> logger)
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

                // 1. Fetch API Key from appsettings.json
                var apiKey = _config["TripWorks:ApiKey"];
                if (string.IsNullOrEmpty(apiKey))
                {
                    _logger.LogWarning("TripWorks API Key is missing from configuration.");
                    return results;
                }

                // 2. Attach the Authorization Header
                client.DefaultRequestHeaders.Add("x-api-key", apiKey);

                // 3. Execute the GET Call
                var response = await client.GetAsync("https://api.tripworks.in/get-all-available-routes", cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError($"TripWorks API failed with status code: {response.StatusCode}");
                    return results;
                }

                var jsonResponse = await response.Content.ReadAsStringAsync(cancellationToken);
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

                var data = JsonSerializer.Deserialize<TripWorkResponseRoot>(jsonResponse, options);

                if (data?.Routes == null) return results;

                // 4. Map to Standard Inventory Record
                foreach (var route in data.Routes)
                {
                    if (string.IsNullOrEmpty(route.From) || string.IsNullOrEmpty(route.To)) continue;

                    var standardRecord = new StandardInventoryRecord
                    {
                        SupplierName = ProviderName,
                        SupplierType = "API",
                        Origin = route.From.Trim().ToUpper(),
                        Destination = route.To.Trim().ToUpper(),
                        AvailableDates = ParseDates(route.Available_Dates)
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
                _logger.LogError($"Exception in TripWorks provider: {ex.Message}");
            }

            return results;
        }

        // Helper to safely parse dates directly from the JSON array
        private List<DateTime> ParseDates(List<string> dateStrings)
        {
            var parsedDates = new List<DateTime>();
            if (dateStrings == null || !dateStrings.Any()) return parsedDates;

            // Define expected formats (Handling both Indian dd-MM-yyyy and US MM-dd-yyyy just in case)
            string[] formats = { "dd-MM-yyyy", "MM-dd-yyyy", "d-M-yyyy", "M-d-yyyy" };

            foreach (var d in dateStrings)
            {
                if (DateTime.TryParseExact(d.Trim(), formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime validDate))
                {
                    parsedDates.Add(validDate.Date);
                }
            }

            return parsedDates.Distinct().OrderBy(d => d).ToList();
        }

        // --- INTERNAL CLASSES TO DESERIALIZE TRIPWORKS JSON ---
        private class TripWorkResponseRoot
        {
            public List<TripWorkRoute> Routes { get; set; }
        }

        private class TripWorkRoute
        {
            public string Route { get; set; }
            public string From { get; set; }
            public string To { get; set; }
            public List<string> Available_Dates { get; set; }
            public int Total_Available_Dates { get; set; }
        }
    }
}