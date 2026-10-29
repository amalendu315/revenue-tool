using inventory_analytics_tool.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace inventory_analytics_tool.InventoryProviders
{
    public class FareBoutiqueInventoryProvider : ISupplierInventoryProvider
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _config;
        private readonly ILogger<FareBoutiqueInventoryProvider> _logger;

        // Ensure this exactly matches the name in your Master Supplier Database
        public string ProviderName => "Fare Boutique";

        public FareBoutiqueInventoryProvider(IHttpClientFactory httpClientFactory, IConfiguration config, ILogger<FareBoutiqueInventoryProvider> logger)
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
                client.Timeout = TimeSpan.FromMinutes(3); // Generous timeout for multiple calls

                string ipAddress = _config["FareBoutique:IpAddress"] ?? "127.0.0.1";
                string token = _config["FareBoutique:Token"] ?? "";

                if (string.IsNullOrEmpty(token))
                {
                    _logger.LogWarning("Fare Boutique Token is missing from configuration.");
                    return results;
                }

                var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

                // ==========================================
                // STEP 1: FETCH ALL SECTORS
                // ==========================================
                var sectorPayload = new
                {
                    trip_type = "oneway",
                    end_user_ip = ipAddress,
                    token = token
                };

                var sectorContent = new StringContent(JsonSerializer.Serialize(sectorPayload), Encoding.UTF8, "application/json");
                var sectorResponse = await client.PostAsync("https://api.fareboutique.in/v1/api/sector", sectorContent, cancellationToken);

                if (!sectorResponse.IsSuccessStatusCode)
                {
                    _logger.LogError($"Fare Boutique Step 1 (Sectors) failed with status: {sectorResponse.StatusCode}");
                    return results;
                }

                var sectorJson = await sectorResponse.Content.ReadAsStringAsync(cancellationToken);
                var sectorData = JsonSerializer.Deserialize<FbSectorResponseRoot>(sectorJson, jsonOptions);

                if (sectorData?.replyCode != "success" || sectorData.data == null || !sectorData.data.Any())
                {
                    _logger.LogWarning("Fare Boutique returned no sectors or an error.");
                    return results;
                }

                _logger.LogInformation($"Fare Boutique: Found {sectorData.data.Count} sectors. Moving to Step 2 (Fetching Dates).");

                // ==========================================
                // STEP 2: FETCH DATES FOR EACH SECTOR (Concurrently)
                // ==========================================
                // We use a Semaphore to process 5 sectors at a time. 
                // This prevents us from hammering their server and getting blocked.
                using var semaphore = new SemaphoreSlim(5);
                var fetchTasks = new List<Task<StandardInventoryRecord>>();

                foreach (var sector in sectorData.data)
                {
                    if (string.IsNullOrWhiteSpace(sector.departure_city_code) || string.IsNullOrWhiteSpace(sector.arrival_city_code))
                        continue;

                    fetchTasks.Add(FetchDatesForSectorAsync(
                        client,
                        sector.departure_city_code.Trim().ToUpper(),
                        sector.arrival_city_code.Trim().ToUpper(),
                        ipAddress,
                        token,
                        semaphore,
                        jsonOptions,
                        cancellationToken));
                }

                // Wait for all the individual sector requests to finish
                var processedRecords = await Task.WhenAll(fetchTasks);

                // Only add records that actually returned available dates
                results.AddRange(processedRecords.Where(r => r != null && r.AvailableDates.Any()));
            }
            catch (Exception ex)
            {
                _logger.LogError($"Exception in Fare Boutique provider: {ex.Message}");
            }

            return results;
        }

        private async Task<StandardInventoryRecord> FetchDatesForSectorAsync(
            HttpClient client, string origin, string dest, string ipAddress, string token,
            SemaphoreSlim semaphore, JsonSerializerOptions jsonOptions, CancellationToken cancellationToken)
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var datePayload = new
                {
                    trip_type = "oneway",
                    departure_city_code = origin,
                    arrival_city_code = dest,
                    end_user_ip = ipAddress,
                    token = token
                };

                var dateContent = new StringContent(JsonSerializer.Serialize(datePayload), Encoding.UTF8, "application/json");
                var response = await client.PostAsync("https://api.fareboutique.in/v1/api/onward_dates", dateContent, cancellationToken);

                if (response.IsSuccessStatusCode)
                {
                    var dateJson = await response.Content.ReadAsStringAsync(cancellationToken);
                    var dateData = JsonSerializer.Deserialize<FbDateResponseRoot>(dateJson, jsonOptions);

                    if (dateData?.replyCode == "success" && dateData.data != null)
                    {
                        var parsedDates = new List<DateTime>();
                        foreach (var d in dateData.data)
                        {
                            if (DateTime.TryParse(d.flight_date, out DateTime validDate))
                            {
                                parsedDates.Add(validDate.Date);
                            }
                        }

                        if (parsedDates.Any())
                        {
                            return new StandardInventoryRecord
                            {
                                SupplierName = ProviderName,
                                SupplierType = "API",
                                Origin = origin,
                                Destination = dest,
                                AvailableDates = parsedDates.Distinct().OrderBy(d => d).ToList()
                            };
                        }
                    }
                }

                // Small delay to be polite to their API
                await Task.Delay(200, cancellationToken);
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Failed to fetch dates for {origin}-{dest} from Fare Boutique: {ex.Message}");
                return null;
            }
            finally
            {
                semaphore.Release();
            }
        }

        // --- INTERNAL CLASSES FOR DESERIALIZATION ---
        private class FbSectorResponseRoot
        {
            public string replyCode { get; set; }
            public string replyMsg { get; set; }
            public List<FbSectorData> data { get; set; }
        }

        private class FbSectorData
        {
            public string departure_city_code { get; set; }
            public string arrival_city_code { get; set; }
        }

        private class FbDateResponseRoot
        {
            public string replyCode { get; set; }
            public string replyMsg { get; set; }
            public List<FbDateData> data { get; set; }
        }

        private class FbDateData
        {
            public string flight_date { get; set; }
        }
    }
}