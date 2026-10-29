using inventory_analytics_tool.Models;
using Microsoft.Extensions.Configuration;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace inventory_analytics_tool.Repositories
{
    public interface ISalesAnalyticsRepository
    {
        Task<List<SalesSummaryRecord>> GetSalesSummaryAsync(DateTime fromDate, DateTime toDate);
        Task<List<SupplierMasterRecord>> GetAllSuppliersAsync();
    }

    public class SupplierMasterRecord
    {
        public int Id { get; set; }
        public string SupplierName { get; set; } = string.Empty;
        public string SupplierType { get; set; } = string.Empty;
        public string SupplierDescription { get; set; } = string.Empty;
        public bool Status { get; set; }
    }

    public class SalesAnalyticsRepository : ISalesAnalyticsRepository
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly string _baseUrl;
        private readonly string _username;
        private readonly string _password;

        public SalesAnalyticsRepository(IHttpClientFactory httpClientFactory, IConfiguration config)
        {
            _httpClientFactory = httpClientFactory;
            _baseUrl = config["SupplierApi:BaseUrl"];
            _username = config["SupplierApi:Username"];
            _password = config["SupplierApi:Password"];
        }

        private HttpClient CreateAuthenticatedClient()
        {
            var client = _httpClientFactory.CreateClient();
            var authBytes = Encoding.ASCII.GetBytes($"{_username}:{_password}");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));
            return client;
        }

        public async Task<List<SalesSummaryRecord>> GetSalesSummaryAsync(DateTime fromDate, DateTime toDate)
        {
            try
            {
                var client = CreateAuthenticatedClient();

                // Format dates to YYYY-MM-DD
                string url = $"{_baseUrl}SalesSummary?fromdate={fromDate:yyyy-MM-dd}&todate={toDate:yyyy-MM-dd}";

                var response = await client.GetAsync(url);
                if (!response.IsSuccessStatusCode) return new List<SalesSummaryRecord>();

                var jsonResponse = await response.Content.ReadAsStringAsync();
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

                return JsonSerializer.Deserialize<List<SalesSummaryRecord>>(jsonResponse, options) ?? new List<SalesSummaryRecord>();
            }
            catch
            {
                // In a production app, you might want to inject ILogger here as well to log the exception
                return new List<SalesSummaryRecord>();
            }
        }

        public async Task<List<SupplierMasterRecord>> GetAllSuppliersAsync()
        {
            try
            {
                var client = CreateAuthenticatedClient();

                var response = await client.GetAsync($"{_baseUrl}SupplierMaster");
                if (!response.IsSuccessStatusCode) return new List<SupplierMasterRecord>();

                var jsonResponse = await response.Content.ReadAsStringAsync();
                var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };

                return JsonSerializer.Deserialize<List<SupplierMasterRecord>>(jsonResponse, options) ?? new List<SupplierMasterRecord>();
            }
            catch
            {
                return new List<SupplierMasterRecord>();
            }
        }
    }
}