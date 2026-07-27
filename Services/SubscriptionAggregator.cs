using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using VpnBackend.Models;

namespace VpnBackend.Services
{
    /// <summary>
    /// این کلاس وظیفه مدیریت چندین لینک سابسکریپشن و ادغام خروجی آن‌ها را دارد
    /// </summary>
    public class SubscriptionAggregator
    {
        private readonly SubscriptionParser _parser;

        // ما همان پارسری که قبلاً ساختید را به این کلاس می‌دهیم تا از آن استفاده کند
        public SubscriptionAggregator(SubscriptionParser parser)
        {
            _parser = parser;
        }

        /// <summary>
        /// این متد یک لیست از لینک‌ها را می‌گیرد و تمام کانفیگ‌های سالم آن‌ها را در یک لیست واحد برمی‌گرداند
        /// </summary>
        public async Task<List<VpnNode>> FetchAllSubscriptionsAsync(List<string> subUrls)
        {
            var allNodes = new List<VpnNode>();

            foreach (var url in subUrls)
            {
                Console.WriteLine($"[Info] Fetching nodes from: {url}");
                
                // استفاده از متد کلاس قبلی برای خواندن یک لینک
                var nodesFromUrl = await _parser.ParseSubscriptionAsync(url);
                
                if (nodesFromUrl != null && nodesFromUrl.Count > 0)
                {
                    allNodes.AddRange(nodesFromUrl);
                    Console.WriteLine($"[Success] Added {nodesFromUrl.Count} nodes from this link.");
                }
            }

            Console.WriteLine($"[Finished] Total nodes collected: {allNodes.Count}");
            return allNodes;
        }
    }
}