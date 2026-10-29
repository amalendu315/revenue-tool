//using inventory_analytics_tool.Models;
//using System.Net.Http.Headers;
//using System.Text;
//using System.Text.Json;

//namespace inventory_analytics_tool.Repositories
//{
//    public interface IVendorPurchaseRepository
//    {
//        Task<List<PnrData>> GetPnrDetailsAsync(string fromDate, string toDate);
//    }

//    public class VendorPurchaseRepository : IVendorPurchaseRepository
//    {
//        private readonly IHttpClientFactory _httpClientFactory;
//        private readonly IConfiguration _config;
//        private readonly ILogger<VendorPurchaseRepository> _logger;

//        // 1. Inject ILogger here
//        public VendorPurchaseRepository(IHttpClientFactory httpClientFactory, IConfiguration config, ILogger<VendorPurchaseRepository> logger)
//        {
//            _httpClientFactory = httpClientFactory;
//            _config = config;
//            _logger = logger;
//        }

//        public async Task<List<PnrData>> GetPnrDetailsAsync(string fromDate, string toDate)
//        {
//            try
//            {
//                var client = _httpClientFactory.CreateClient();
//                client.Timeout = TimeSpan.FromSeconds(160);

//                // FIX 1: Prevent .NET from sending the hidden header that crashes APIs
//                client.DefaultRequestHeaders.ExpectContinue = false;

//                string authToken = _config["VendorApi:AuthToken"] ?? "";

//                // FIX 2: Prove the token is actually loading at runtime
//                _logger.LogWarning($"[DEBUG AUTH] The auth token loaded from config is {authToken.Length} characters long.");

//                if (authToken.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
//                {
//                    authToken = authToken.Substring("Basic ".Length).Trim();
//                }

//                client.DefaultRequestHeaders.Add("Accept", "application/json");
//                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/115.0.0.0 Safari/537.36");
//                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authToken);

//                var requestBody = new { from_date = fromDate, to_date = toDate };
//                var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
//                content.Headers.ContentType.CharSet = "";

//                _logger.LogInformation($"[API CALL] Requesting {fromDate} to {toDate} from Vendor API...");

//                var response = await client.PostAsync("https://airiqvendorapi.azurewebsites.net/GetPnrDetails", content);

//                if (!response.IsSuccessStatusCode)
//                {
//                    // FIX 3: Read the actual error message the API is screaming back at us!
//                    string errorBody = await response.Content.ReadAsStringAsync();
//                    _logger.LogError($"[VENDOR API REJECTED] Status: {response.StatusCode}. Body: {errorBody}");

//                    return new List<PnrData>();
//                }

//                var jsonResponse = await response.Content.ReadAsStringAsync();

//                try
//                {
//                    var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
//                    var result = JsonSerializer.Deserialize<PnrDetailsResponse>(jsonResponse, options);
//                    return result?.Data ?? new List<PnrData>();
//                }
//                catch (JsonException jsonEx)
//                {
//                    _logger.LogError($"[JSON PARSE ERROR] Failed to parse response: {jsonEx.Message}");
//                    return new List<PnrData>();
//                }
//            }
//            catch (Exception ex)
//            {
//                _logger.LogError($"[API FATAL] {ex.Message}");
//                return new List<PnrData>();
//            }
//        }
//    }
//}

using inventory_analytics_tool.Models;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace inventory_analytics_tool.Repositories
{
    public interface IVendorPurchaseRepository
    {
        // Updated parameter names for clarity
        Task<List<PnrData>> GetPnrDetailsAsync(string travelDate, string travelToDate);
    }

    public class VendorPurchaseRepository : IVendorPurchaseRepository
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConfiguration _config;
        private readonly ILogger<VendorPurchaseRepository> _logger;

        public VendorPurchaseRepository(IHttpClientFactory httpClientFactory, IConfiguration config, ILogger<VendorPurchaseRepository> logger)
        {
            _httpClientFactory = httpClientFactory;
            _config = config;
            _logger = logger;
        }

        public async Task<List<PnrData>> GetPnrDetailsAsync(string travelDate, string travelToDate)
        {
            try
            {
                var client = _httpClientFactory.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(160);

                // Prevent .NET from sending the hidden header that crashes APIs
                client.DefaultRequestHeaders.ExpectContinue = false;

                string authToken = _config["VendorApi:AuthToken"] ?? "";

                _logger.LogWarning($"[DEBUG AUTH] The auth token loaded from config is {authToken.Length} characters long.");

                if (authToken.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
                {
                    authToken = authToken.Substring("Basic ".Length).Trim();
                }

                client.DefaultRequestHeaders.Add("Accept", "application/json");
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/115.0.0.0 Safari/537.36");
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Basic", authToken);

                // CHANGED: Mapped to the new travel_date and travel_to_date keys
                var requestBody = new { travel_date = travelDate, travel_to_date = travelToDate };
                var content = new StringContent(JsonSerializer.Serialize(requestBody), Encoding.UTF8, "application/json");
                content.Headers.ContentType.CharSet = "";

                _logger.LogInformation($"[API CALL] Requesting Travel Dates {travelDate} to {travelToDate} from Vendor API...");

                var response = await client.PostAsync("https://airiqvendorapi.azurewebsites.net/GetPnrDetails", content);

                if (!response.IsSuccessStatusCode)
                {
                    string errorBody = await response.Content.ReadAsStringAsync();
                    _logger.LogError($"[VENDOR API REJECTED] Status: {response.StatusCode}. Body: {errorBody}");

                    return new List<PnrData>();
                }

                var jsonResponse = await response.Content.ReadAsStringAsync();

                try
                {
                    var options = new JsonSerializerOptions 
                    {
                        PropertyNameCaseInsensitive = true,
                        // Add this line to handle stringified numbers like "2250.00" gracefully
                        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString
                    };
                    var result = JsonSerializer.Deserialize<PnrDetailsResponse>(jsonResponse, options);
                    return result?.Data ?? new List<PnrData>();
                }
                catch (JsonException jsonEx)
                {
                    _logger.LogError($"[JSON PARSE ERROR] Failed to parse response: {jsonEx.Message}");
                    return new List<PnrData>();
                }
            }
            catch (Exception ex)
            {
                _logger.LogError($"[API FATAL] {ex.Message}");
                return new List<PnrData>();
            }
        }
    }
}