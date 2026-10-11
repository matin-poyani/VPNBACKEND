using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace VpnBackend.Models
{
    public class VpnNode
    {
        public string Protocol { get; set; } = "";
        public string Address { get; set; } = "";
        public int Port { get; set; }
        public string OriginalLink { get; set; } = "";
        public string Name { get; set; } = "";
        public string DisplayName { get; set; } = "";
        public bool IsAlive { get; set; }
        public long PingMs { get; set; }
        public string PingType { get; set; } = "";

        public string Country { get; set; } = "Unknown";
        public string CountryCode { get; set; } = "";
        public string CountryFlag { get; set; } = "🌍";
        public string TelegramChannelHandle { get; set; } = "@vpn_free_iranin";

        public string BuildTelegramLabel(string? channelHandle = null)
        {
            var handle = string.IsNullOrWhiteSpace(channelHandle) ? TelegramChannelHandle : channelHandle.Trim();
            var flag = string.IsNullOrWhiteSpace(CountryFlag) ? "🌍" : CountryFlag;

            var ping = PingMs > 0
                ? $" {PingType} {PingMs}ms".TrimEnd()
                : "";
            return $"{handle} {flag}{ping}".Trim();
        }

        public string GetDeduplicationKey()
        {
            var canonical = OriginalLink;

            try
            {
                if (OriginalLink.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase))
                {
                    var encoded = OriginalLink["vmess://".Length..].Trim().Replace('-', '+').Replace('_', '/');
                    encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
                    using var document = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
                    canonical = "vmess|" + string.Join("&", document.RootElement.EnumerateObject()
                        .Where(property => !property.Name.Equals("ps", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(property => $"{property.Name.ToLowerInvariant()}={property.Value.GetRawText()}"));
                }
                else if (OriginalLink.StartsWith("vless://", StringComparison.OrdinalIgnoreCase) ||
                         OriginalLink.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase))
                {
                    var uri = new Uri(OriginalLink);
                    var query = uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
                        .Select(part =>
                        {
                            var separator = part.IndexOf('=');
                            var key = separator < 0 ? part : part[..separator];
                            var value = separator < 0 ? "" : part[(separator + 1)..];
                            return $"{Uri.UnescapeDataString(key.Replace('+', ' ')).ToLowerInvariant()}={Uri.UnescapeDataString(value.Replace('+', ' '))}";
                        })
                        .OrderBy(part => part, StringComparer.Ordinal);

                    canonical = $"{uri.Scheme.ToLowerInvariant()}|{Uri.UnescapeDataString(uri.UserInfo)}|{uri.Host.ToLowerInvariant()}|{uri.Port}|{string.Join("&", query)}";
                }
            }
            catch
            {
                canonical = OriginalLink;
            }

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
        }
    }
}