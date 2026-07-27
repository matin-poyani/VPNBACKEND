using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text;
using System.Linq;
using VpnBackend.Services;
using VpnBackend.Models;

namespace VpnBackend
{
    class Program
    {
        static async Task Main(string[] args)
        {
            Console.WriteLine("=== شروع فرآیند تجمیع کانفیگ‌ها ===");

            // مسیر فایل‌هایی که می‌خوانیم و می‌نویسیم
            string urlsFilePath = "urls.txt";
            string outputFilePath = "sub.txt";

            // بررسی می‌کنیم که آیا فایل urls.txt وجود دارد یا نه
            if (!File.Exists(urlsFilePath))
            {
                Console.WriteLine($"[Error] فایل {urlsFilePath} پیدا نشد!");
                Console.WriteLine("لطفاً یک فایل به نام urls.txt بسازید و لینک‌های سابسکریپشن خود را در آن قرار دهید.");
                return;
            }

            // خواندن تمام خطوط فایل (هر خط یک لینک محسوب می‌شود)
            var urls = File.ReadAllLines(urlsFilePath)
                           .Where(u => !string.IsNullOrWhiteSpace(u)) // خطوط خالی را حذف می‌کنیم
                           .Select(u => u.Trim()) // فاصله‌های اضافی را پاک می‌کنیم
                           .ToList();

            if (urls.Count == 0)
            {
                Console.WriteLine("[Warning] فایل urls.txt خالی است. هیچ لینکی برای بررسی وجود ندارد.");
                return;
            }

            // آماده‌سازی ابزارهای دانلود و تجمیع
            using var httpClient = new HttpClient();
            var parser = new SubscriptionParser(httpClient);
            var aggregator = new SubscriptionAggregator(parser);

            Console.WriteLine($"[Info] در حال پردازش {urls.Count} لینک...");
            // ارسال لیست لینک‌ها به کلاس Aggregator که در مرحله قبل ساختیم
            List<VpnNode> allNodes = await aggregator.FetchAllSubscriptionsAsync(urls);

            if (allNodes.Count == 0)
            {
                Console.WriteLine("[Warning] هیچ کانفیگ سالمی از لینک‌ها استخراج نشد.");
                return;
            }

            // فقط لینک‌های اصلی کانفیگ‌ها را جدا می‌کنیم و با 'Enter' به هم می‌چسبانیم
            var rawLinks = allNodes.Select(n => n.OriginalLink);
            string combinedText = string.Join("\n", rawLinks);

            // طبق استاندارد V2ray، کل متن باید به Base64 تبدیل شود
            byte[] textBytes = Encoding.UTF8.GetBytes(combinedText);
            string base64Output = Convert.ToBase64String(textBytes);

            // ذخیره محتوای Base64 در فایل نهایی
            File.WriteAllText(outputFilePath, base64Output);

            Console.WriteLine($"[Success] تعداد {allNodes.Count} کانفیگ با موفقیت در فایل {outputFilePath} ذخیره شد.");
            Console.WriteLine("=== پایان فرآیند ===");
        }
    }
}