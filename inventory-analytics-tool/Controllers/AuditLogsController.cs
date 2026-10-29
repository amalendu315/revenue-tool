using inventory_analytics_tool.Helpers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace inventory_analytics_tool.Controllers
{
    [Authorize(Roles = "Admin")]
    public class AuditLogsController : Controller
    {
        private readonly IDatabaseHelper _dbHelper;
        private readonly ILogger<AuditLogsController> _logger;

        public AuditLogsController(IDatabaseHelper dbHelper, ILogger<AuditLogsController> logger)
        {
            _dbHelper = dbHelper;
            _logger = logger;
        }

        public IActionResult Index()
        {
            return View();
        }

        [HttpGet]
        public async Task<IActionResult> GetSessions()
        {
            try
            {
                var sessions = await _dbHelper.GetUserSessionsAsync();

                // Transform into flat objects utilizing your specific 24h AM/PM format rules mapped in the model
                var formattedData = sessions.Select(s => new {
                    s.SessionId,
                    s.Username,
                    s.UserRole,
                    s.IpAddress,
                    s.BrowserName,
                    s.OperatingSystem,
                    LoginTime = s.DisplayLoginTime,
                    LogoutTime = s.DisplayLogoutTime,
                    s.DurationSeconds,
                    s.IsActive
                });

                return Json(new { success = true, data = formattedData });
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error loading sessions: {ex.Message}");
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetActivities()
        {
            try
            {
                var activities = await _dbHelper.GetUserActivityLogsAsync();
                var formattedData = activities.Select(a => new {
                    a.ActivityId,
                    a.Username,
                    a.UserRole,
                    a.HttpMethod,
                    a.ActivityType,
                    a.ControllerName,
                    a.ActionName,
                    a.StatusCode,
                    a.ExecutionDurationMs,
                    Timestamp = a.DisplayTimestamp
                });

                return Json(new { success = true, data = formattedData });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetUploads()
        {
            try
            {
                var uploads = await _dbHelper.GetUserUploadLogsAsync();
                var formattedData = uploads.Select(u => new {
                    u.UploadId,
                    u.Username,
                    u.FileName,
                    u.FileExtension,
                    u.FileSizeBytes,
                    u.RecordsParsed,
                    u.IsSuccessful,
                    u.ErrorMessage,
                    Timestamp = u.DisplayTimestamp
                });

                return Json(new { success = true, data = formattedData });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }
    }
}