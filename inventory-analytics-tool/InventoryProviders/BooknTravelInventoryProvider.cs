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
    public class BooknTravelInventoryProvider : ISupplierInventoryProvider
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _config;
        private readonly ILogger<BooknTravelInventoryProvider> _logger;

        // Ensure this exactly matches the name in your Master Supplier Database
        // Note: In your earlier map, it showed "BOOKNTRAVEL INDIA PVT LTD"
        public string ProviderName => "BOOKNTRAVEL INDIA PVT LTD";

        public BooknTravelInventoryProvider(IHttpClientFactory httpClientFactory, IConfiguration config, ILogger<BooknTravelInventoryProvider> logger)
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
                var apiKey = _config["BooknTravel:ApiKey"];
                if (string.IsNullOrEmpty(apiKey))
                {
                    _logger.LogWarning("BooknTravel API Key is missing from configuration.");
                    return results;
                }

                // 2. Attach the Authorization Header
                client.DefaultRequestHeaders.Add("Apikey", apiKey);

                // 3. Execute the GET Call
                var response = await client.GetAsync("https://partner.bookntravel.in/fe/api/v1/flight/host/fetch-dest-dates", cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError($"BooknTravel API failed with status code: {response.StatusCode}");
                    return results;
                }

                var jsonResponse = await response.Content.ReadAsStringAsync(cancellationToken);
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

                var data = JsonSerializer.Deserialize<BooknTravelResponseRoot>(jsonResponse, options);

                if (data?.Payload == null) return results;

                // 4. Map to Standard Inventory Record
                foreach (var item in data.Payload)
                {
                    if (string.IsNullOrWhiteSpace(item.Seg) || !item.Seg.Contains("-"))
                        continue;

                    // Split "AMD-DED" into Origin and Destination
                    var segments = item.Seg.Split('-');
                    var origin = segments[0].Trim().ToUpper();
                    var destination = segments[1].Trim().ToUpper();

                    var standardRecord = new StandardInventoryRecord
                    {
                        SupplierName = ProviderName,
                        SupplierType = "API",
                        Origin = origin,
                        Destination = destination,
                        AvailableDates = ParseDates(item.Dates)
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
                _logger.LogError($"Exception in BooknTravel provider: {ex.Message}");
            }

            return results;
        }

        // Helper to parse the standard YYYY-MM-DD dates
        private List<DateTime> ParseDates(List<string> dateStrings)
        {
            var parsedDates = new List<DateTime>();
            if (dateStrings == null || !dateStrings.Any()) return parsedDates;

            foreach (var d in dateStrings)
            {
                // TryParse easily handles ISO 8601 formats like "2026-07-18"
                if (DateTime.TryParse(d.Trim(), out DateTime validDate))
                {
                    parsedDates.Add(validDate.Date);
                }
            }

            return parsedDates.Distinct().OrderBy(d => d).ToList();
        }

        // --- INTERNAL CLASSES TO DESERIALIZE BOOKNTRAVEL JSON ---
        private class BooknTravelResponseRoot
        {
            public List<BooknTravelPayload> Payload { get; set; }
        }

        private class BooknTravelPayload
        {
            public string Seg { get; set; }
            public List<string> Dates { get; set; }
            // We can safely ignore the 'key' array since we don't need it for inventory analytics
        }
    }
}