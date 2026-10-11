using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using VpnBackend.Models;

namespace VpnBackend.Services
{
    public class GeoIPService
    {
        private readonly HttpClient _http;
        private readonly Dictionary<string, (string country, string code)> _cache = new();

        private static readonly Dictionary<string, string> CountryFlags = new()
        {
            { "US", "🇺🇸" }, { "GB", "🇬🇧" }, { "CA", "🇨🇦" }, { "AU", "🇦🇺" },
            { "DE", "🇩🇪" }, { "FR", "🇫🇷" }, { "NL", "🇳🇱" }, { "SE", "🇸🇪" },
            { "JP", "🇯🇵" }, { "SG", "🇸🇬" }, { "HK", "🇭🇰" }, { "IN", "🇮🇳" },
            { "BR", "🇧🇷" }, { "MX", "🇲🇽" }, { "RU", "🇷🇺" }, { "KR", "🇰🇷" },
            { "CH", "🇨🇭" }, { "NZ", "🇳🇿" }, { "TW", "🇹🇼" }, { "IR", "🇮🇷" },
            { "AE", "🇦🇪" }, { "TR", "🇹🇷" }, { "MY", "🇲🇾" }, { "TH", "🇹🇭" },
            { "VN", "🇻🇳" }, { "PH", "🇵🇭" }, { "ID", "🇮🇩" }, { "PK", "🇵🇰" },
        };

        public GeoIPService()
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        }

        public async Task EnrichAsync(VpnNode node)
        {
            Console.WriteLine($"[GeoIP Debug] شروع enrich برای {node.Address}:{node.Port}");

            if (_cache.TryGetValue(node.Address, out var cached))
            {
                node.Country = cached.country;
                node.CountryCode = cached.code;
                node.CountryFlag = GetFlag(cached.code);
                Console.WriteLine($"[GeoIP Debug] نتیجه از cache: {cached.country} ({cached.code})");
                return;
            }

            try
            {
                var url = $"http://ip-api.com/json/{node.Address}";
                Console.WriteLine($"[GeoIP Debug] درخواست به {url}");
                var response = await _http.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                {
                    Console.WriteLine($"[GeoIP Warning] پاسخ ناموفق از GeoIP: {(int)response.StatusCode} {response.StatusCode}");
                    return;
                }

                var json = await response.Content.ReadAsStringAsync();
                var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (root.GetProperty("status").GetString() != "success")
                {
                    Console.WriteLine($"[GeoIP Warning] GeoIP status success نبود برای {node.Address}: {json}");
                    return;
                }

                var country = root.GetProperty("country").GetString() ?? "Unknown";
                var code = root.GetProperty("countryCode").GetString() ?? "";

                node.Country = country;
                node.CountryCode = code;
                node.CountryFlag = GetFlag(code);

                _cache[node.Address] = (country, code);
                Console.WriteLine($"[GeoIP Debug] ذخیره شد: {node.Address} -> {country} ({code})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[GeoIP Error] EnrichAsync برای {node.Address} شکست خورد: {ex.Message}");
            }
        }

        private static string GetFlag(string countryCode) =>
            CountryFlags.TryGetValue(countryCode?.ToUpperInvariant() ?? "", out var flag) ? flag : "🌍";
    }
}