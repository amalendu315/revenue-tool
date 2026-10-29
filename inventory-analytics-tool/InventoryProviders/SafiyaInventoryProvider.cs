using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using inventory_analytics_tool.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Globalization;

namespace inventory_analytics_tool.InventoryProviders
{
    public class SafiyaInventoryProvider : ISupplierInventoryProvider
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _config;
        private readonly ILogger<SafiyaInventoryProvider> _logger;

        // This name must match the 'Name' column in your [Marketplace].[APIVendor] table!
        public string ProviderName => "SafiyaTravels";

        public SafiyaInventoryProvider(IHttpClientFactory httpClientFactory, IConfiguration config, ILogger<SafiyaInventoryProvider> logger)
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

                var requestBody = new
                {
                    Auth_Header = new
                    {
                        UserId = "sgairiq",
                        Password = "4137919998C30CF6BDE0F5FEE04B545CF9C4EECF",
                        Request_Id = "5500887959054",
                        IP_Address = "136.232.82.174",
                        IMEI_Number = "2232323232321"
                    }
                };

                var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");

                var response = await client.PostAsync("https://api.safiyago.com/Flight/AirAPIService.svc/JSONService/Air_SectorAvailabilityPI", content, cancellationToken);

                if (!response.IsSuccessStatusCode) return results;

                var jsonResponse = await response.Content.ReadAsStringAsync(cancellationToken);
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

                // We use the same structure as Tripforo based on the response you provided
                var data = JsonSerializer.Deserialize<TripforoResponseRoot>(jsonResponse, options);

                if (data?.SectorsPIs == null) return results;

                foreach (var sector in data.SectorsPIs)
                {
                    results.Add(new StandardInventoryRecord
                    {
                        SupplierName = ProviderName,
                        SupplierType = "API",
                        Origin = sector.Origin.Trim().ToUpper(),
                        Destination = sector.Destination.Trim().ToUpper(),
                        AvailableDates = ParseDates(sector.AvailableDates)
                    });
                }
            }
            catch (Exception ex) { _logger.LogError($"Safiya Error: {ex.Message}"); }
            return results;
        }

        private List<DateTime> ParseDates(string dateString)
        {
            var parsedDates = new List<DateTime>();
            if (string.IsNullOrWhiteSpace(dateString)) return parsedDates;

            // Handle the pipe '|' separator
            var dateArray = dateString.Split(new[] { '|' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var d in dateArray)
            {
                if (DateTime.TryParse(d.Trim(), CultureInfo.GetCultureInfo("en-US"), DateTimeStyles.None, out DateTime validDate))
                    parsedDates.Add(validDate.Date);
            }
            return parsedDates.Distinct().OrderBy(d => d).ToList();
        }

        // Reuse the same internal class structure as Tripforo
        private class TripforoResponseRoot { public List<TripforoSector> SectorsPIs { get; set; } }
        private class TripforoSector { public string AvailableDates { get; set; } public string Destination { get; set; } public string Origin { get; set; } }
    }
}