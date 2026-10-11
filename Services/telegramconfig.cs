using Microsoft.Extensions.Configuration;

namespace VpnBackend.Services
{
    public static class TelegramConfig
    {
        private static readonly IConfiguration Config;

        static TelegramConfig()
        {
            Config = new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: true)
                .AddEnvironmentVariables()
                .Build();
        }

        public const string ChannelHandle = "@vpn_free_iranin";
        public const string BotLink = "https://t.me/VpnFreeIranin_Bot";
        public const string SubscriptionUrl = "https://raw.githubusercontent.com/matin-poyani/VPNBACKEND/main/my-sub.txt";

        public static string? Token =>
            Environment.GetEnvironmentVariable("TELEGRAM_BOT_TOKEN") ??
            Environment.GetEnvironmentVariable("TELEGRAM_TOKEN") ??
            FirstConfigured(Config["Telegram:Token"]);

        public static string? ChatId =>
            Environment.GetEnvironmentVariable("TELEGRAM_CHAT_ID") ??
            Environment.GetEnvironmentVariable("TELEGRAM_CHANNEL_ID") ??
            FirstConfigured(Config["Telegram:ChatId"], Config["Telegram:ChannelId"]);

        public static bool IsConfigured =>
            !string.IsNullOrWhiteSpace(Token) && !string.IsNullOrWhiteSpace(ChatId);

        public static bool UsesNumericChatId =>
            long.TryParse(NormalizeChatTarget(ChatId), out _);

        public static bool UsesUsernameChatId =>
            !string.IsNullOrWhiteSpace(ChatId) &&
            !UsesNumericChatId &&
            NormalizeChatTarget(ChatId).StartsWith("@", StringComparison.Ordinal);

        public static string ExplainChatIdRequirement() =>
            "برای کانال عمومی، @username قابل استفاده است؛ برای کانال خصوصی، شناسه عددی chat را در Telegram:ChatId یا TELEGRAM_CHAT_ID تنظیم کنید.";

        private static string? FirstConfigured(params string?[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }

            return null;
        }

        public static string NormalizeChatTarget(string? target)
        {
            if (string.IsNullOrWhiteSpace(target))
                return string.Empty;

            var value = target.Trim();
            if (value.StartsWith("https://t.me/", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("http://t.me/", StringComparison.OrdinalIgnoreCase))
            {
                var uri = new Uri(value);
                var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

                if (segments.Length >= 2 &&
                    segments[0].Equals("c", StringComparison.OrdinalIgnoreCase) &&
                    long.TryParse(segments[1], out _))
                {
                    return segments[1];
                }

                return segments.Length > 0 ? (segments[0].StartsWith("@") ? segments[0] : $"@{segments[0]}") : string.Empty;
            }

            if (value.StartsWith("t.me/", StringComparison.OrdinalIgnoreCase) ||
                value.StartsWith("telegram.me/", StringComparison.OrdinalIgnoreCase))
            {
                var segments = value.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length >= 2 &&
                    segments[0].Equals("c", StringComparison.OrdinalIgnoreCase) &&
                    long.TryParse(segments[1], out _))
                {
                    return segments[1];
                }

                var path = segments.LastOrDefault();
                return string.IsNullOrWhiteSpace(path) ? string.Empty : (path.StartsWith("@") ? path : $"@{path}");
            }

            if (value.StartsWith("@", StringComparison.Ordinal))
                return value;

            if (long.TryParse(value, out _))
                return value;

            return value;
        }
    }
}