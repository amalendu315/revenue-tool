using inventory_analytics_tool.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace inventory_analytics_tool.InventoryProviders
{
    public class OfflineInventoryProvider : ISupplierInventoryProvider
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly ILogger<OfflineInventoryProvider> _logger;
        private readonly string _baseUrl;
        private readonly string _username;
        private readonly string _password;

        public string ProviderName => "Offline";

        public OfflineInventoryProvider(IHttpClientFactory httpClientFactory, IConfiguration config, ILogger<OfflineInventoryProvider> logger)
        {
            _httpClientFactory = httpClientFactory;
            _logger = logger;
            _baseUrl = config["SupplierApi:BaseUrl"];
            _username = config["SupplierApi:Username"];
            _password = config["SupplierApi:Password"];
        }

        public async Task<List<StandardInventoryRecord>> FetchInventoryAsync(CancellationToken cancellationToken = default)
        {
            var results = new List<StandardInventoryRecord>();
            try
            {
                var client = _httpClientFactory.CreateClient();

                // Configure Basic Authentication
                var authBytes = Encoding.ASCII.GetBytes($"{_username}:{_password}");
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));

                var response = await client.GetAsync($"{_baseUrl}OfflineInventory", cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogError($"Offline API failed with status code: {response.StatusCode}");
                    return results;
                }

                var jsonResponse = await response.Content.ReadAsStringAsync(cancellationToken);
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
                var apiResult = JsonSerializer.Deserialize<OfflineInventoryResponse>(jsonResponse, options);

                if (apiResult?.Status == true && apiResult.Data != null)
                {
                    results.AddRange(apiResult.Data);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"Offline Inventory API Error: {ex.Message}");
            }

            return results;
        }

        // Internal class to handle the specific JSON wrapper from the API
        private class OfflineInventoryResponse
        {
            public bool Status { get; set; }
            public string Message { get; set; }
            public int Count { get; set; }
            public List<StandardInventoryRecord> Data { get; set; }
        }
    }
}