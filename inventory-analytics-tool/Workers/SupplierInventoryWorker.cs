using inventory_analytics_tool.InventoryProviders;
using inventory_analytics_tool.Models;
using Microsoft.Extensions.Caching.Memory;

namespace inventory_analytics_tool.Workers
{
    public class SupplierInventoryWorker : BackgroundService
    {
        private readonly ILogger<SupplierInventoryWorker> _logger;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly IMemoryCache _cache;

        public SupplierInventoryWorker(
            ILogger<SupplierInventoryWorker> logger,
            IServiceScopeFactory scopeFactory,
            IMemoryCache cache)
        {
            _logger = logger;
            _scopeFactory = scopeFactory;
            _cache = cache;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Supplier Inventory Harvester is starting.");

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    _logger.LogInformation("Fetching daily inventory from all API Suppliers...");

                    // We use scopes because Background Services are Singletons, but our API Clients are Scoped
                    using (var scope = _scopeFactory.CreateScope())
                    {
                        var providers = scope.ServiceProvider.GetRequiredService<IEnumerable<ISupplierInventoryProvider>>();
                        var allInventory = new List<StandardInventoryRecord>();

                        // 1. Fetch from all suppliers (Currently just Tripforo)
                        foreach (var provider in providers)
                        {
                            try
                            {
                                var providerData = await provider.FetchInventoryAsync(stoppingToken);
                                allInventory.AddRange(providerData);
                                _logger.LogInformation($"Fetched {providerData.Count} sectors from {provider.ProviderName}");
                            }
                            catch (Exception ex)
                            {
                                _logger.LogError($"Error fetching from {provider.ProviderName}: {ex.Message}");
                            }
                        }

                        // 2. Save it to the cache permanently (overwrites the old cache from yesterday)
                        if (allInventory.Count > 0)
                        {
                            var cacheOptions = new MemoryCacheEntryOptions()
                                .SetPriority(CacheItemPriority.NeverRemove);

                            _cache.Set("SupplierApiInventoryCache", allInventory, cacheOptions);
                            _logger.LogInformation($"Successfully cached {allInventory.Count} total API inventory records.");
                        }
                    }

                    _logger.LogInformation("Inventory sync complete. Sleeping for 24 hours.");

                    // Sleep for 24 hours (or whatever frequency you prefer)
                    await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Fatal error in SupplierInventoryWorker: {ex.Message}");
                    // Wait 10 minutes before retrying on a hard failure
                    await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
                }
            }
        }
    }
}