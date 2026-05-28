namespace inventory_analytics_tool.Models
{
    public class Login
    {
        // --- 1. LOGIN API MODELS ---
        public class LoginApiResponse
        {
            public string token { get; set; }
            public string token_type { get; set; }
            public LoginUser user { get; set; }
            public int expiration { get; set; }
        }

        public class LoginUser
        {
            public int manager_id { get; set; }
            public string name { get; set; }
            public string username { get; set; }
            public string email { get; set; }
            public string mobile { get; set; }
            public string airport { get; set; }
            public string type { get; set; }
        }
    }
}