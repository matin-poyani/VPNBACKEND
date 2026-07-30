using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using VpnBackend.Models;
using VpnBackend.Services;

namespace VpnBackend
{
    class Program
    {
        static async Task Main(string[] args)
        {
            string inputFilePath = "urls.txt";
            string outputFilePath = "my-sub.txt";

            if (!File.Exists(inputFilePath))
            {
                Console.WriteLine("[Error] فایل urls.txt پیدا نشد! لطفاً فایل را بسازید.");
                return;
            }

            string[] urls = await File.ReadAllLinesAsync(inputFilePath);
            Console.WriteLine($"[Info] تعداد {urls.Length} خط از فایل خوانده شد.");

            using var httpClient = new HttpClient();
            var parser = new SubscriptionParser(httpClient);
            var allNodes = new List<VpnNode>();

            foreach (var url in urls)
            {
                string cleanUrl = url.Trim();
                
                if (string.IsNullOrWhiteSpace(cleanUrl) || cleanUrl.StartsWith("#"))
                {
                    continue;
                }

                Console.WriteLine($"[Fetching] در حال دانلود: {cleanUrl}");
                var nodes = await parser.ParseSubscriptionAsync(cleanUrl);
                allNodes.AddRange(nodes);
            }

            Console.WriteLine($"[Success] مجموعاً {allNodes.Count} کانفیگ سالم استخراج شد.");

            var linksBuilder = new StringBuilder();
            foreach (var node in allNodes)
            {
                linksBuilder.AppendLine(node.OriginalLink);
            }

            byte[] bytes = Encoding.UTF8.GetBytes(linksBuilder.ToString());
            string base64Subscription = Convert.ToBase64String(bytes);

            await File.WriteAllTextAsync(outputFilePath, base64Subscription);
            Console.WriteLine($"[Saved] فایل نهایی با موفقیت در {outputFilePath} ذخیره شد!");
        }
    }
}