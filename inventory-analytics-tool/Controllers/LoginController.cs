//using inventory_analytics_tool.Models;
//using Microsoft.AspNetCore.Authentication;
//using Microsoft.AspNetCore.Authentication.Cookies;
//using Microsoft.AspNetCore.Mvc;
//using System.Diagnostics;
//using System.Security.Claims;
//using System.Text;
//using System.Text.Json;
//using static inventory_analytics_tool.Models.Login;

//namespace purchase_analytics.Controllers;

//public class LoginController : Controller
//{
//    private readonly ILogger<LoginController> _logger;
//    private readonly IHttpClientFactory _httpClientFactory;

//    public LoginController(ILogger<LoginController> logger, IHttpClientFactory httpClientFactory)
//    {
//        _logger = logger;
//        _httpClientFactory = httpClientFactory;
//    }

//    [HttpGet]
//    public IActionResult Index()
//    {
//        if (User.Identity != null && User.Identity.IsAuthenticated)
//        {
//            if (User.IsInRole("Admin") || User.IsInRole("Revenue"))
//            {
//                return RedirectToAction("Index", "Dashboard");
//            }
//            else if (User.IsInRole("Supplier"))
//            {
//                return RedirectToAction("Index", "SupplierAnalytics");
//            }
//        }
//        return View();
//    }

//    [HttpPost]
//    public async Task<IActionResult> Index(string username, string password)
//    {
//        var httpClient = _httpClientFactory.CreateClient();

//        // 1. Prepare payload for the API
//        var payload = new { Username = username, Password = password };
//        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

//        try
//        {
//            // 2. Call Azure Login API
//            var response = await httpClient.PostAsync("https://shivent.azurewebsites.net/managerlogin", content);

//            if (response.IsSuccessStatusCode)
//            {
//                var responseString = await response.Content.ReadAsStringAsync();
//                var loginResult = JsonSerializer.Deserialize<LoginApiResponse>(responseString, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

//                if (loginResult != null && !string.IsNullOrEmpty(loginResult.token))
//                {
//                    // 3. Save the token and username securely in the cookie session
//                    var claims = new List<Claim>
//                    {
//                        new Claim(ClaimTypes.Name, loginResult.user?.name ?? username),
//                        new Claim("jwt_token", loginResult.token),
//                        new Claim(ClaimTypes.Role, loginResult.user?.type ?? "NoRole")
//                    };

//                    var claimsIdentity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
//                    var authProperties = new AuthenticationProperties { IsPersistent = true };

//                    await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(claimsIdentity), authProperties);
//                    string userRole = loginResult.user?.type ?? "NoRole";
//                    if (userRole.Equals("Admin", StringComparison.OrdinalIgnoreCase) ||
//    userRole.Equals("Revenue", StringComparison.OrdinalIgnoreCase))
//                    {
//                        // Route for Admin & Revenue
//                        return RedirectToAction("Index", "Dashboard");
//                    }
//                    else if (userRole.Equals("Supplier", StringComparison.OrdinalIgnoreCase))
//                    {
//                        // Route for Supplier
//                        return RedirectToAction("Index", "SupplierAnalytics");
//                    }
//                    else
//                    {
//                        return RedirectToAction("Unassigned", "Login");
//                    }
//                }
//            }
//        }
//        catch (Exception ex)
//        {
//            _logger.LogError($"Login API Error: {ex.Message}");
//        }

//        // If login fails
//        ViewBag.ErrorMessage = "Invalid username or password. Please try again.";
//        return View();
//    }

//    [HttpGet]
//    public IActionResult AccessDenied()
//    {
//        return View();
//    }

//    [HttpGet]
//    public IActionResult Unassigned()
//    {
//        return View();
//    }

//    public async Task<IActionResult> Logout()
//    {
//        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
//        return RedirectToAction("Index", "Login");
//    }
//}
using inventory_analytics_tool.Helpers;
using inventory_analytics_tool.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using System.Diagnostics;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using static inventory_analytics_tool.Models.Login;

namespace purchase_analytics.Controllers;

public class LoginController : Controller
{
    private readonly ILogger<LoginController> _logger;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IDatabaseHelper _dbHelper; // NEW: Added IDatabaseHelper

    public LoginController(ILogger<LoginController> logger, IHttpClientFactory httpClientFactory, IDatabaseHelper dbHelper)
    {
        _logger = logger;
        _httpClientFactory = httpClientFactory;
        _dbHelper = dbHelper;
    }

    [HttpGet]
    public IActionResult Index()
    {
        if (User.Identity != null && User.Identity.IsAuthenticated)
        {
            if (User.IsInRole("Admin") || User.IsInRole("Revenue"))
            {
                return RedirectToAction("Index", "Dashboard");
            }
            else if (User.IsInRole("Supplier"))
            {
                return RedirectToAction("Index", "SupplierAnalytics");
            }
        }
        return View();
    }

    [HttpPost]
    public async Task<IActionResult> Index(string username, string password)
    {
        var httpClient = _httpClientFactory.CreateClient();

        var payload = new { Username = username, Password = password };
        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        try
        {
            var response = await httpClient.PostAsync("https://shivent.azurewebsites.net/managerlogin", content);

            if (response.IsSuccessStatusCode)
            {
                var responseString = await response.Content.ReadAsStringAsync();
                var loginResult = JsonSerializer.Deserialize<LoginApiResponse>(responseString, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (loginResult != null && !string.IsNullOrEmpty(loginResult.token))
                {
                    // NEW: Generate a unique ID for this specific login session
                    var sessionId = Guid.NewGuid();
                    string actualUsername = loginResult.user?.name ?? username;
                    string userRole = loginResult.user?.type ?? "NoRole";

                    var claims = new List<Claim>
                    {
                        new Claim(ClaimTypes.Name, actualUsername),
                        new Claim("jwt_token", loginResult.token),
                        new Claim(ClaimTypes.Role, userRole),
                        new Claim("SessionId", sessionId.ToString()) // NEW: Attach Session ID to cookie
                    };

                    var claimsIdentity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
                    var authProperties = new AuthenticationProperties { IsPersistent = true };

                    await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(claimsIdentity), authProperties);

                    // NEW: Fire-and-forget logging to avoid slowing down login time
                    _ = LogSessionToDatabaseAsync(sessionId, actualUsername, userRole);

                    if (userRole.Equals("Admin", StringComparison.OrdinalIgnoreCase) || userRole.Equals("Revenue", StringComparison.OrdinalIgnoreCase))
                    {
                        return RedirectToAction("Index", "Dashboard");
                    }
                    else if (userRole.Equals("Supplier", StringComparison.OrdinalIgnoreCase))
                    {
                        return RedirectToAction("Index", "SupplierAnalytics");
                    }
                    else
                    {
                        return RedirectToAction("Unassigned", "Login");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Login API Error: {ex.Message}");
        }

        ViewBag.ErrorMessage = "Invalid username or password. Please try again.";
        return View();
    }

    [HttpGet]
    public IActionResult AccessDenied()
    {
        return View();
    }

    [HttpGet]
    public IActionResult Unassigned()
    {
        return View();
    }

    public async Task<IActionResult> Logout()
    {
        // NEW: Intercept the Session ID to close it out in the database before signing out
        var sessionIdClaim = User.Claims.FirstOrDefault(c => c.Type == "SessionId")?.Value;

        if (!string.IsNullOrEmpty(sessionIdClaim) && Guid.TryParse(sessionIdClaim, out Guid sessionId))
        {
            await _dbHelper.CloseUserSessionAsync(sessionId, DateTime.UtcNow);
        }

        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction("Index", "Login");
    }

    // --- NEW HELPER METHODS FOR SESSION LOGGING ---

    private async Task LogSessionToDatabaseAsync(Guid sessionId, string username, string role)
    {
        try
        {
            string userAgent = Request.Headers["User-Agent"].ToString();
            string ipAddress = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "Unknown IP";
            var (browser, os) = ParseUserAgent(userAgent);

            var sessionData = new UserSession
            {
                SessionId = sessionId,
                Username = username,
                UserRole = role,
                IpAddress = ipAddress,
                UserAgent = userAgent,
                BrowserName = browser,
                OperatingSystem = os,
                LoginTime = DateTime.UtcNow,
                IsActive = true
            };

            await _dbHelper.CreateUserSessionAsync(sessionData);
        }
        catch (Exception ex)
        {
            _logger.LogError($"Failed to log user session: {ex.Message}");
        }
    }

    private (string Browser, string OS) ParseUserAgent(string userAgent)
    {
        if (string.IsNullOrWhiteSpace(userAgent)) return ("Unknown", "Unknown");

        string os = "Unknown OS";
        if (userAgent.Contains("Windows NT 10.0")) os = "Windows 10/11";
        else if (userAgent.Contains("Windows NT 6.3")) os = "Windows 8.1";
        else if (userAgent.Contains("Windows NT 6.2")) os = "Windows 8";
        else if (userAgent.Contains("Mac OS X")) os = "Mac OS";
        else if (userAgent.Contains("Linux")) os = "Linux";
        else if (userAgent.Contains("Android")) os = "Android";
        else if (userAgent.Contains("iPhone") || userAgent.Contains("iPad")) os = "iOS";

        string browser = "Unknown Browser";
        if (userAgent.Contains("Edg/")) browser = "Microsoft Edge";
        else if (userAgent.Contains("Chrome/") && !userAgent.Contains("Edg/")) browser = "Google Chrome";
        else if (userAgent.Contains("Firefox/")) browser = "Mozilla Firefox";
        else if (userAgent.Contains("Safari/") && !userAgent.Contains("Chrome/")) browser = "Safari";

        return (browser, os);
    }
}