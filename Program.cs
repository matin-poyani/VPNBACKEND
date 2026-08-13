using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using VpnBackend.Models;
using VpnBackend.Services;

namespace VpnBackend
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;

            string inputFilePath = "urls.txt";
            string outputFilePath = "my-sub.txt";
            string readableOutputFilePath = "valid-servers.txt";

            Console.WriteLine("========================================");
            Console.WriteLine("VPN Subscription Updater");
            Console.WriteLine("========================================");

            if (!File.Exists(inputFilePath))
            {
                Console.WriteLine("[Error] فایل urls.txt پیدا نشد! لطفاً فایل را بسازید.");
                return;
            }

            string[] urls = await File.ReadAllLinesAsync(inputFilePath);
            Console.WriteLine($"[Info] تعداد {urls.Length} خط از فایل ورودی خوانده شد.");

            var cleanUrls = urls
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x) && !x.StartsWith("#"))
                .Distinct()
                .ToList();

            Console.WriteLine($"[Info] تعداد {cleanUrls.Count} لینک معتبر بعد از فیلتر شدن.");

            using var httpClient = new HttpClient
            {
                Timeout = TimeSpan.FromSeconds(20)
            };

            var parser = new SubscriptionParser(httpClient);
            var healthChecker = new HealthChecker();
            var allNodes = new List<VpnNode>();

            foreach (var url in cleanUrls)
            {
                Console.WriteLine($"[Fetching] در حال دانلود: {url}");
                var nodes = await parser.ParseSubscriptionAsync(url);
                allNodes.AddRange(nodes);
            }

            Console.WriteLine($"[Info] مجموع کانفیگ‌های استخراج‌شده: {allNodes.Count}");

            if (allNodes.Count == 0)
            {
                await File.WriteAllTextAsync(outputFilePath, string.Empty);
                await File.WriteAllTextAsync(readableOutputFilePath, string.Empty);
                Console.WriteLine("[Info] هیچ کانفیگی پیدا نشد؛ فایل خروجی خالی شد.");
                return;
            }

            var healthyNodes = new List<VpnNode>();
            var semaphore = new SemaphoreSlim(10);

            var tasks = allNodes
                .DistinctBy(node => node.OriginalLink)
                .Select(async node =>
                {
                    await semaphore.WaitAsync();
                    try
                    {
                        Console.WriteLine($"[Checking] بررسی سرور: {node.Address}:{node.Port} ({node.Protocol})");

                        var (isAlive, ping) = await healthChecker.CheckNodeAsync(
                            node.Address,
                            node.Port,
                            node.OriginalLink,
                            node.Protocol);

                        if (isAlive)
                        {
                            node.IsAlive = true;
                            node.PingMs = ping;
                            lock (healthyNodes)
                            {
                                healthyNodes.Add(node);
                            }

                            Console.WriteLine($"[OK] سرور سالم است | ping: {ping}ms");
                        }
                        else
                        {
                            Console.WriteLine($"[FAIL] سرور غیرفعال یا نامعتبر است.");
                        }
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                });

            await Task.WhenAll(tasks);

            var finalNodes = healthyNodes
                .Where(x => !string.IsNullOrWhiteSpace(x.OriginalLink))
                .GroupBy(x => x.OriginalLink)
                .Select(g => g.First())
                .OrderBy(x => x.PingMs == -1 ? int.MaxValue : x.PingMs)
                .ToList();

            Console.WriteLine($"[Success] مجموعاً {finalNodes.Count} کانفیگ سالم و بدون تکرار باقی ماند.");

            var readableLinks = finalNodes
                .Select(x => x.OriginalLink)
                .Distinct()
                .ToList();

            var readableText = string.Join(Environment.NewLine, readableLinks);
            var base64Subscription = Convert.ToBase64String(Encoding.UTF8.GetBytes(readableText));

            var existingContent = File.Exists(outputFilePath)
                ? await File.ReadAllTextAsync(outputFilePath)
                : string.Empty;

            if (!string.Equals(existingContent, base64Subscription, StringComparison.Ordinal))
            {
                await File.WriteAllTextAsync(outputFilePath, base64Subscription);
                Console.WriteLine($"[Saved] فایل Base64 در {outputFilePath} ذخیره شد.");
            }
            else
            {
                Console.WriteLine($"[Info] محتوای {outputFilePath} تغییری نکرده است؛ فایل دوباره نوشته نشد.");
            }

            await File.WriteAllTextAsync(readableOutputFilePath, readableText);
            Console.WriteLine($"[Saved] خروجی خوانا در {readableOutputFilePath} ذخیره شد.");
            Console.WriteLine("========================================");
            Console.WriteLine($"Summary: {cleanUrls.Count} source links -> {allNodes.Count} nodes -> {finalNodes.Count} healthy links");
            Console.WriteLine("========================================");
        }
    }
}