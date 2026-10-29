using System;

namespace inventory_analytics_tool.Models
{
    public class UserSession
    {
        public Guid SessionId { get; set; }
        public string Username { get; set; } = string.Empty;
        public string UserRole { get; set; } = string.Empty;
        public string? IpAddress { get; set; }
        public string? UserAgent { get; set; }
        public string? BrowserName { get; set; }
        public string? OperatingSystem { get; set; }
        public DateTime LoginTime { get; set; }
        public DateTime? LogoutTime { get; set; }
        public int? DurationSeconds { get; set; }
        public bool IsActive { get; set; }

        // Formats time exactly to 24-hour numerical string with AM/PM tag
        public string DisplayLoginTime => LoginTime.ToString("yyyy-MM-dd HH:mm tt");
        public string DisplayLogoutTime => LogoutTime.HasValue ? LogoutTime.Value.ToString("yyyy-MM-dd HH:mm tt") : "Active";
    }

    public class UserActivityLog
    {
        public long ActivityId { get; set; }
        public Guid? SessionId { get; set; }
        public string Username { get; set; } = string.Empty;
        public string UserRole { get; set; } = string.Empty;
        public string HttpMethod { get; set; } = string.Empty;
        public string ActivityType { get; set; } = string.Empty;
        public string? Area { get; set; }
        public string ControllerName { get; set; } = string.Empty;
        public string ActionName { get; set; } = string.Empty;
        public string RequestPath { get; set; } = string.Empty;
        public string? QueryString { get; set; }
        public int StatusCode { get; set; }
        public int ExecutionDurationMs { get; set; }
        public DateTime Timestamp { get; set; }

        public string DisplayTimestamp => Timestamp.ToString("yyyy-MM-dd HH:mm tt");
    }

    public class UserUploadLog
    {
        public long UploadId { get; set; }
        public long? ActivityId { get; set; }
        public Guid? SessionId { get; set; }
        public string Username { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string FileExtension { get; set; } = string.Empty;
        public long FileSizeBytes { get; set; }
        public int RecordsParsed { get; set; }
        public bool IsSuccessful { get; set; }
        public string? ErrorMessage { get; set; }
        public DateTime Timestamp { get; set; }

        public string DisplayTimestamp => Timestamp.ToString("yyyy-MM-dd HH:mm tt");
    }
}