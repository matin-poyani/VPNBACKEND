using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using VpnBackend.Models;

namespace VpnBackend.Services
{
    public class SubscriptionParser
    {
        private readonly HttpClient _httpClient;

        public SubscriptionParser(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public async Task<List<VpnNode>> ParseSubscriptionAsync(string subUrl)
        {
            var nodes = new List<VpnNode>();

            Console.WriteLine($"[Parser Debug] شروع دانلود/تجزیه لینک: {subUrl}");

            try
            {
                var base64Content = await _httpClient.GetStringAsync(subUrl);
                string decodedContent = base64Content;

                Console.WriteLine($"[Parser Debug] محتوای لینک دریافت شد. طول: {base64Content.Length}");

                if (!base64Content.Contains("://", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine("[Parser Debug] محتوا Base64 تشخیص داده شد؛ در حال decode...");
                    decodedContent = DecodeSafeBase64(base64Content);
                }

                var lines = decodedContent
                    .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                Console.WriteLine($"[Parser Debug] تعداد خط‌ها: {lines.Length}");

                foreach (var line in lines)
                {
                    var trimmed = line.Trim();
                    if (string.IsNullOrWhiteSpace(trimmed))
                        continue;

                    var node = ParseNode(trimmed);
                    if (node is not null)
                    {
                        nodes.Add(node);
                    }
                    else
                    {
                        Console.WriteLine($"[Parser Debug] کانفیگ نامعتبر رد شد: {trimmed[..Math.Min(trimmed.Length, 80)]}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Parser Error] Failed to fetch sub: {subUrl}");
                Console.WriteLine($"[Parser Error] Message: {ex.Message}");
                Console.WriteLine($"[Parser Error] StackTrace: {ex.StackTrace}");
            }

            Console.WriteLine($"[Parser Debug] نتیجه نهایی: {nodes.Count} کانفیگ معتبر از {subUrl}");
            return nodes;
        }

        private static VpnNode? ParseNode(string link)
        {
            try
            {
                if (link.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase))
                    return ParseVmess(link);

                if (link.StartsWith("vless://", StringComparison.OrdinalIgnoreCase) ||
                    link.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase))
                    return ParseUrlFormat(link);
            }
            catch
            {
                // Ignore malformed configs.
            }

            return null;
        }

        private static VpnNode ParseVmess(string link)
        {
            var base64Json = link.Substring("vmess://".Length);
            var jsonString = DecodeSafeBase64(base64Json);

            using var doc = JsonDocument.Parse(jsonString);
            var root = doc.RootElement;

            var name = root.TryGetProperty("ps", out var psProp)
                ? psProp.GetString() ?? "Unknown VMess"
                : "Unknown VMess";

            return new VpnNode
            {
                Protocol = "vmess",
                OriginalLink = link,
                Address = root.TryGetProperty("add", out var add) ? add.GetString() ?? "" : "",
                Port = root.TryGetProperty("port", out var port)
                    ? port.ValueKind == JsonValueKind.Number ? port.GetInt32() : int.Parse(port.GetString() ?? "0")
                    : 0,
                Name = name,
                DisplayName = name,
                IsAlive = false
            };
        }

        private static VpnNode ParseUrlFormat(string link)
        {
            var uri = new Uri(link);
            var name = !string.IsNullOrEmpty(uri.Fragment)
                ? Uri.UnescapeDataString(uri.Fragment.TrimStart('#'))
                : $"{uri.Scheme.ToUpperInvariant()} {uri.Host}";

            return new VpnNode
            {
                Protocol = uri.Scheme,
                OriginalLink = link,
                Address = uri.Host,
                Port = uri.Port,
                Name = name,
                DisplayName = name,
                IsAlive = false
            };
        }

        private static string DecodeSafeBase64(string base64)
        {
            var normalized = base64.Trim().Replace('-', '+').Replace('_', '/');
            var mod4 = normalized.Length % 4;
            if (mod4 > 0)
            {
                normalized += new string('=', 4 - mod4);
            }

            var data = Convert.FromBase64String(normalized);
            return Encoding.UTF8.GetString(data);
        }
    }
}