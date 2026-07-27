using Microsoft.AspNetCore.Mvc;
using System.Collections.Generic;
using System.Threading.Tasks;
using VpnBackend.Models;
using VpnBackend.Services;

namespace VpnBackend.Controllers
{
    /// <summary>
    /// این کنترلر درخواست‌های اپلیکیشن اندروید شما را پاسخ می‌دهد
    /// </summary>
    [ApiController]
    [Route("api/[controller]")]
    public class VpnController : ControllerBase
    {
        private readonly SubscriptionParser _parser;
        private readonly HealthChecker _checker;

        public VpnController(SubscriptionParser parser, HealthChecker checker)
        {
            _parser = parser;
            _checker = checker;
        }

        // اپلیکیشن اندروید شما متد زیر را صدا می‌زند: GET /api/vpn/servers
        [HttpGet("servers")]
        public async Task<ActionResult<List<VpnNode>>> GetHealthyServers()
        {
            // لینک سابسکریپشن خود را اینجا قرار دهید
            string subUrl = "https://example.com/your-sub-link"; 
            
            // ۱. دریافت تمام کانفیگ‌ها
            var allNodes = await _parser.ParseSubscriptionAsync(subUrl);
            var healthyNodes = new List<VpnNode>();

            // ۲. فیلتر کردن سرورهای سالم
            // نکته: در پروژه نهایی بهتر است این پردازش در بک‌گراند انجام شود تا کاربر معطل نشود
            foreach (var node in allNodes)
            {
                var (isAlive, ping) = await _checker.CheckNodeAsync(node.Address, node.Port);
                if (isAlive)
                {
                    node.IsAlive = true;
                    node.PingMs = ping;
                    healthyNodes.Add(node);
                }
            }

            // ۳. ارسال خروجی JSON به اپلیکیشن اندروید
            return Ok(healthyNodes);
        }
    }
}