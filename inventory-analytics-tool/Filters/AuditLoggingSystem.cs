using inventory_analytics_tool.Helpers;
using inventory_analytics_tool.Models;
using Microsoft.AspNetCore.Mvc.Filters;
using System.Diagnostics;
using System.Security.Claims;

namespace inventory_analytics_tool.Filters
{
    // 1. THE FILTER (Intercepts traffic)
    public class AuditLogActionFilter : IAsyncActionFilter
    {
        private readonly AuditLogQueue _queue;

        public AuditLogActionFilter(AuditLogQueue queue)
        {
            _queue = queue;
        }

        public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
        {
            var sw = Stopwatch.StartNew();

            // Let the controller do its normal work
            var executedContext = await next();

            sw.Stop();

            var user = context.HttpContext.User;
            if (user.Identity == null || !user.Identity.IsAuthenticated) return;

            string username = user.Identity.Name ?? "Unknown";
            string role = user.Claims.FirstOrDefault(c => c.Type == ClaimTypes.Role)?.Value ?? "Unknown";

            var sessionIdClaim = user.Claims.FirstOrDefault(c => c.Type == "SessionId")?.Value;
            Guid? sessionId = Guid.TryParse(sessionIdClaim, out Guid sid) ? sid : null;

            var request = context.HttpContext.Request;
            string method = request.Method.ToUpper();

            // Exclude overly noisy endpoints if necessary (e.g., rapid AJAX polling)
            string action = context.RouteData.Values["action"]?.ToString() ?? "";
            if (action == "GetActiveAlerts") return; // Example of ignoring a noisy polling endpoint

            var log = new UserActivityLog
            {
                SessionId = sessionId,
                Username = username,
                UserRole = role,
                HttpMethod = method,
                ActivityType = (method == "GET") ? "READ" : "WRITE",
                ControllerName = context.RouteData.Values["controller"]?.ToString() ?? "",
                ActionName = action,
                RequestPath = request.Path,
                QueryString = request.QueryString.Value,
                StatusCode = executedContext.HttpContext.Response?.StatusCode ?? 200,
                ExecutionDurationMs = (int)sw.ElapsedMilliseconds,
                Timestamp = DateTime.UtcNow
            };

            // Push to the in-memory queue instantly (does not block the UI)
            await _queue.QueueLogAsync(log);
        }
    }

    // 2. THE BACKGROUND WORKER (Saves to DB silently)
    public class AuditLogWorker : BackgroundService
    {
        private readonly AuditLogQueue _queue;
        private readonly IServiceScopeFactory _scopeFactory;
        private readonly ILogger<AuditLogWorker> _logger;

        public AuditLogWorker(AuditLogQueue queue, IServiceScopeFactory scopeFactory, ILogger<AuditLogWorker> logger)
        {
            _queue = queue;
            _scopeFactory = scopeFactory;
            _logger = logger;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            // Loops continuously, waiting for items to appear in the queue
            await foreach (var log in _queue.DequeueAsync(stoppingToken))
            {
                try
                {
                    using (var scope = _scopeFactory.CreateScope())
                    {
                        var dbHelper = scope.ServiceProvider.GetRequiredService<IDatabaseHelper>();
                        await dbHelper.InsertUserActivityLogAsync(log);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Failed to save User Activity Log: {ex.Message}");
                }
            }
        }
    }
}