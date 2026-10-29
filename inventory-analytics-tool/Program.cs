using inventory_analytics_tool.Filters;
using inventory_analytics_tool.Helpers;
using inventory_analytics_tool.InventoryProviders;
using inventory_analytics_tool.Repositories;
using inventory_analytics_tool.Services;
using inventory_analytics_tool.Workers;
using Microsoft.AspNetCore.Authentication.Cookies;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddScoped<IDatabaseHelper, DatabaseHelper>();
builder.Services.AddScoped<IClickHouseHelper, ClickHouseHelper>();
builder.Services.AddScoped<ISearchPulseService, SearchPulseService>();

builder.Services.AddSingleton<AuditLogQueue>();
builder.Services.AddHostedService<AuditLogWorker>();

// Add services to the container.
builder.Services.AddControllersWithViews(options =>
{
    options.Filters.Add<AuditLogActionFilter>();
});

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/"; // Redirect here if unauthorized
        options.AccessDeniedPath = "/Login/AccessDenied";
        options.ExpireTimeSpan = TimeSpan.FromHours(8); // Mock session length
    });

// 1. Enable Server RAM Caching
builder.Services.AddMemoryCache();

// 2. Enable HTTP Client Factory (used by the worker)
builder.Services.AddHttpClient();

builder.Services.AddScoped<ISupplierInventoryProvider, AarBeeInventoryProvider>();
builder.Services.AddScoped<ISupplierInventoryProvider, OfflineInventoryProvider>();
builder.Services.AddScoped<ISalesAnalyticsRepository, SalesAnalyticsRepository>();
builder.Services.AddScoped<IVendorPurchaseRepository, VendorPurchaseRepository>();
builder.Services.AddScoped<ISupplierInventoryProvider, FareBoutiqueInventoryProvider>();
builder.Services.AddScoped<ISupplierInventoryProvider, TripforoInventoryProvider>();
builder.Services.AddScoped<ISupplierInventoryProvider, SafiyaInventoryProvider>();
builder.Services.AddScoped<ISupplierInventoryProvider, TripWorkInventoryProvider>();
builder.Services.AddScoped<ISupplierInventoryProvider, BooknTravelInventoryProvider>();
builder.Services.AddScoped<ISupplierInventoryProvider, JustClicknPayInventoryProvider>();
builder.Services.AddScoped<ISupplierInventoryProvider, TripsastaInventoryProvider>();
builder.Services.AddScoped<ISupplierInventoryProvider, TravelogyInventoryProvider>();
builder.Services.AddScoped<ISupplierInventoryProvider, TravelopediaInventoryProvider>();
builder.Services.AddHostedService<SupplierInventoryWorker>();
builder.Services.AddHostedService<FestivalDataWorker>();
builder.Services.AddHostedService<FareAuditWorker>();
builder.Services.AddHostedService<DemandAnalyticsWorker>();

builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));


var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapReverseProxy().RequireAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Login}/{action=Index}/{id?}");

app.Run();

