using inventory_analytics_tool.Models;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace inventory_analytics_tool.InventoryProviders
{
    public interface ISupplierInventoryProvider
    {
        string ProviderName { get; }
        Task<List<StandardInventoryRecord>> FetchInventoryAsync(CancellationToken cancellationToken = default);
    }
}
