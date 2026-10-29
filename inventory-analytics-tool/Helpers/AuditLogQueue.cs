using inventory_analytics_tool.Models;
using System.Threading.Channels;

namespace inventory_analytics_tool.Helpers
{
    public class AuditLogQueue
    {
        private readonly Channel<UserActivityLog> _queue;

        public AuditLogQueue()
        {
            // Holds up to 10,000 logs in RAM. If a massive traffic spike hits, it drops the oldest to protect server memory.
            var options = new BoundedChannelOptions(10000)
            {
                FullMode = BoundedChannelFullMode.DropOldest
            };
            _queue = Channel.CreateBounded<UserActivityLog>(options);
        }

        public async ValueTask QueueLogAsync(UserActivityLog log)
        {
            await _queue.Writer.WriteAsync(log);
        }

        public IAsyncEnumerable<UserActivityLog> DequeueAsync(CancellationToken ct)
        {
            return _queue.Reader.ReadAllAsync(ct);
        }
    }
}