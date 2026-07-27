namespace VpnBackend.Models
{
    /// <summary>
    /// این کلاس نمایانگر یک سرور VPN و اطلاعات استخراج شده از لینک آن است
    /// </summary>
    public class VpnNode
    {
        public string Protocol { get; set; } = string.Empty; // vmess, vless, trojan
        public string Address { get; set; } = string.Empty;  // IP یا دامنه سرور
        public int Port { get; set; }
        public string Name { get; set; } = string.Empty;     // اسم کانفیگ
        public string OriginalLink { get; set; } = string.Empty; // لینک کامل و اصلی
        
        // --- متغیرهای مربوط به تست سلامت ---
        public bool IsAlive { get; set; } = false; // آیا سرور در دسترس است؟
        public long PingMs { get; set; } = -1;     // پینگ سرور به میلی‌ثانیه
    }
}