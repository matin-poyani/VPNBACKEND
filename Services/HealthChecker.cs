using System;
using System.Diagnostics;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace VpnBackend.Services
{
    /// <summary>
    /// این سرویس وظیفه دارد باز بودن پورت و در دسترس بودن سرور را بررسی کند
    /// </summary>
    public class HealthChecker
    {
        // تنظیم تایم‌اوت روی ۲ ثانیه برای پیدا کردن سرورهای واقعاً سریع
        private const int TimeoutMs = 2000; 

        public async Task<(bool IsAlive, long Ping)> CheckNodeAsync(string address, int port)
        {
            if (string.IsNullOrEmpty(address) || port <= 0 || port > 65535)
                return (false, -1);

            using var tcpClient = new TcpClient();
            var stopwatch = new Stopwatch();

            try
            {
                stopwatch.Start();
                var connectTask = tcpClient.ConnectAsync(address, port);
                var timeoutTask = Task.Delay(TimeoutMs);

                if (await Task.WhenAny(connectTask, timeoutTask) == timeoutTask)
                {
                    // اگر اتصال بیش از ۲ ثانیه طول کشید، سرور را مرده در نظر می‌گیریم
                    stopwatch.Stop();
                    return (false, -1); 
                }

                if (tcpClient.Connected)
                {
                    stopwatch.Stop();
                    return (true, stopwatch.ElapsedMilliseconds);
                }

                return (false, -1);
            }
            catch
            {
                // در صورت مسدود بودن آی‌پی یا خطای شبکه
                return (false, -1);
            }
        }
    }
}