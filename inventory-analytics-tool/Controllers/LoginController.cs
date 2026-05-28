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

    public LoginController(ILogger<LoginController> logger)
    {
        _logger = logger;
    }

    [HttpGet]
    public IActionResult Index()
    {
        if (User.Identity != null && User.Identity.IsAuthenticated)
        {
            return RedirectToAction("Index", "Dashboard");
        }
        return View();
    }

    [HttpPost]
    public async Task<IActionResult> Index(string username, string password)
    {
        using var httpClient = new HttpClient();

        // 1. Prepare payload for the API
        var payload = new { Username = username, Password = password };
        var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        try
        {
            // 2. Call Azure Login API
            var response = await httpClient.PostAsync("https://shivent.azurewebsites.net/managerlogin", content);

            if (response.IsSuccessStatusCode)
            {
                var responseString = await response.Content.ReadAsStringAsync();
                var loginResult = JsonSerializer.Deserialize<LoginApiResponse>(responseString, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (loginResult != null && !string.IsNullOrEmpty(loginResult.token))
                {
                    // 3. Save the token and username securely in the cookie session
                    var claims = new List<Claim>
                    {
                        new Claim(ClaimTypes.Name, loginResult.user?.name ?? username),
                        new Claim("jwt_token", loginResult.token)
                    };

                    var claimsIdentity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
                    var authProperties = new AuthenticationProperties { IsPersistent = true };

                    await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(claimsIdentity), authProperties);

                    return RedirectToAction("Index", "Dashboard");
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Login API Error: {ex.Message}");
        }

        // If login fails
        ViewBag.ErrorMessage = "Invalid username or password. Please try again.";
        return View();
    }

    public async Task<IActionResult> Logout()
    {
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return RedirectToAction("Index", "Login");
    }
}