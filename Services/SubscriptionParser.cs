using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using VpnBackend.Models;

namespace VpnBackend.Services
{
    /// <summary>
    /// این سرویس وظیفه دانلود لینک سابسکریپشن و استخراج تک‌تک کانفیگ‌ها را دارد
    /// </summary>
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
            try
            {
                // ۱. دانلود محتوای لینک
                var base64Content = await _httpClient.GetStringAsync(subUrl);
                
                // ۲. رمزگشایی لیست (اگر Base64 باشد)
                string decodedContent = base64Content;
                if (!base64Content.Contains("://")) 
                {
                    decodedContent = DecodeSafeBase64(base64Content);
                }

                // ۳. تفکیک خط به خط
                var lines = decodedContent.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);

                foreach (var line in lines)
                {
                    var node = ParseNode(line.Trim());
                    if (node != null)
                    {
                        nodes.Add(node);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Parser Error] Failed to fetch sub: {ex.Message}");
            }
            return nodes;
        }

        private VpnNode? ParseNode(string link)
        {
            try
            {
                if (link.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase))
                {
                    return ParseVmess(link);
                }
                else if (link.StartsWith("vless://", StringComparison.OrdinalIgnoreCase) || 
                         link.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase))
                {
                    return ParseUrlFormat(link); 
                }
            }
            catch (Exception)
            {
                // از کانفیگ‌های خراب چشم‌پوشی می‌کنیم
            }
            return null;
        }

        private VpnNode ParseVmess(string link)
        {
            string base64Json = link.Substring(8);
            string jsonString = DecodeSafeBase64(base64Json);
            
            using JsonDocument doc = JsonDocument.Parse(jsonString);
            var root = doc.RootElement;

            return new VpnNode
            {
                Protocol = "vmess",
                OriginalLink = link,
                Address = root.GetProperty("add").GetString() ?? "",
                Port = root.GetProperty("port").ValueKind == JsonValueKind.Number 
                       ? root.GetProperty("port").GetInt32() 
                       : int.Parse(root.GetProperty("port").GetString() ?? "0"),
                Name = root.TryGetProperty("ps", out var psProp) ? psProp.GetString() ?? "Unknown VMess" : "Unknown VMess"
            };
        }

        private VpnNode ParseUrlFormat(string link)
        {
            var uri = new Uri(link);
            
            return new VpnNode
            {
                Protocol = uri.Scheme, 
                OriginalLink = link,
                Address = uri.Host,
                Port = uri.Port,
                Name = !string.IsNullOrEmpty(uri.Fragment) 
                       ? Uri.UnescapeDataString(uri.Fragment.Substring(1)) 
                       : $"Unknown {uri.Scheme}"
            };
        }

        private string DecodeSafeBase64(string base64)
        {
            string b64 = base64.Trim().Replace('-', '+').Replace('_', '/');
            int mod4 = b64.Length % 4;
            if (mod4 > 0)
            {
                b64 += new string('=', 4 - mod4);
            }
            byte[] data = Convert.FromBase64String(b64);
            return Encoding.UTF8.GetString(data);
        }
    }
}