using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Telegram.Bot;
using VpnBackend.Models;
using VpnBackend.Services;

var channelHandle = TelegramConfig.ChannelHandle;
var arguments = args ?? Array.Empty<string>();

ConfigureProxyEnvironment();

if (arguments.Contains("--test-telegram", StringComparer.OrdinalIgnoreCase) ||
    arguments.Contains("--telegram-check", StringComparer.OrdinalIgnoreCase))
{
    await RunTelegramSmokeTestAsync();
    return 0;
}

Console.WriteLine("╔════════════════════════════════════════╗");
Console.WriteLine("║   VPN Subscription Updater Started     ║");
Console.WriteLine("╚════════════════════════════════════════╝\n");

await RunCycleAsync(channelHandle);
return 0;

static async Task RunCycleAsync(string channelHandle)
{
    var httpClient = CreateHttpClient(TimeSpan.FromSeconds(30));
    var parser = new SubscriptionParser(httpClient);
    var geoService = new GeoIPService();
    var notifier = new TelegramNotifier();
    var healthChecker = new HealthChecker();
    var allNodes = new List<VpnNode>();
    var healthyNodes = new List<VpnNode>();

    if (!File.Exists("urls.txt"))
    {
        Console.WriteLine("✗ فایل urls.txt پیدا نشد.");
        return;
    }

    var sourceUrls = (await File.ReadAllLinesAsync("urls.txt"))
        .Where(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith("#"))
        .ToList();

    Console.WriteLine($"📡 خواندن {sourceUrls.Count} لینک منبع...\n");

    var sourceCount = 0;
    foreach (var url in sourceUrls)
    {
        try
        {
            Console.Write($"  {url[..Math.Min(50, url.Length)]}... ");
            var urlNodes = await parser.ParseSubscriptionAsync(url);
            if (urlNodes.Count == 0)
            {
                Console.WriteLine("✗");
                continue;
            }

            allNodes.AddRange(urlNodes);
            sourceCount++;
            Console.WriteLine($"✓ ({urlNodes.Count})");
        }
        catch
        {
            Console.WriteLine("✗");
        }

        await Task.Delay(500);
    }

    allNodes = allNodes
        .GroupBy(node => node.GetDeduplicationKey(), StringComparer.Ordinal)
        .Select(group => group.First())
        .ToList();

    Console.WriteLine($"\n✓ کل کانفیگ‌ها: {allNodes.Count}\n");

    if (allNodes.Count == 0)
    {
        Console.WriteLine("✗ کانفیگی استخراج نشد.");
        return;
    }

    Console.WriteLine($"🔍 تست {allNodes.Count} کانفیگ...\n");

    var testTasks = allNodes.Select(async node =>
    {
        var (isAlive, ping) = await healthChecker.CheckNodeAsync(node.Address, node.Port, node.OriginalLink, node.Protocol);
        if (!isAlive)
            return;

        node.IsAlive = true;
        node.PingMs = ping;
        lock (healthyNodes)
        {
            healthyNodes.Add(node);
        }
    }).ToList();

    await Task.WhenAll(testTasks);

    Console.WriteLine($"\n🌍 گرفتن لوکیشن سرورها...\n");

    foreach (var node in healthyNodes)
    {
        await geoService.EnrichAsync(node);
        node.TelegramChannelHandle = channelHandle;
        node.PingType = "Xray RTT";
        node.DisplayName = node.BuildTelegramLabel(channelHandle);
        node.Name = node.DisplayName;
        node.OriginalLink = ApplyConfigDisplayName(node.OriginalLink, node.DisplayName);
        await Task.Delay(100);
    }

    if (healthyNodes.Count == 0)
        Console.WriteLine("✗ هیچ کانفیگی سالم نبود؛ خروجی خالی و حذف پست‌های قبلی اعمال می‌شود.");

    var ordered = healthyNodes
        .OrderBy(n => n.PingMs)
        .ToList();

    var finalSubscription = string.Join("\n", ordered.Select(n => n.OriginalLink));
    await File.WriteAllTextAsync("my-sub.txt", Convert.ToBase64String(Encoding.UTF8.GetBytes(finalSubscription)));

    var text = new StringBuilder();
    text.AppendLine($"تعداد: {ordered.Count}");
    text.AppendLine($"آپدیت: {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");
    text.AppendLine(new string('=', 80));
    text.AppendLine();

    for (var i = 0; i < ordered.Count; i++)
    {
        var node = ordered[i];
        text.AppendLine($"{i + 1}. {node.DisplayName}");
        text.AppendLine($"   {node.OriginalLink}\n");
    }

    await File.WriteAllTextAsync("valid-servers.txt", text.ToString());
    Console.WriteLine("✓ نوشته شد: my-sub.txt, valid-servers.txt\n");

    Console.WriteLine("════════════════════════════════════════");
    Console.WriteLine($"منابع: {sourceCount} | کل: {allNodes.Count} | سالم: {healthyNodes.Count}");
    Console.WriteLine($"[Telegram] وضعیت کانال: {(TelegramConfig.UsesUsernameChatId ? "username" : (TelegramConfig.UsesNumericChatId ? "numeric" : "not-set"))}");
    Console.WriteLine("════════════════════════════════════════\n");

    if (notifier.IsConfigured)
    {
        if (await notifier.VerifyAsync())
        {
            Console.WriteLine("[Telegram] ارسال واقعی به کانال شروع شد...");
            var published = await notifier.PublishAsync(ordered, sourceCount, allNodes.Count);
            Console.WriteLine("\n════════════════════════════════════════");
            Console.WriteLine(published ? "✓ انتشار موفق" : "✗ انتشار کامل نشد");
        }
        else
        {
            Console.WriteLine("\n════════════════════════════════════════");
            Console.WriteLine("✗ انتشار انجام نشد؛ اتصال، توکن، Chat ID و دسترسی ادمین ربات را بررسی کنید.");
        }
    }
    else
    {
        Console.WriteLine("\n════════════════════════════════════════");
        Console.WriteLine("✓ اتمام (بدون ارسال تلگرام — Token/ChatId تنظیم نشده است)");
    }
}

static string ApplyConfigDisplayName(string originalLink, string displayName)
{
    if (string.IsNullOrWhiteSpace(displayName))
        return originalLink;

    if (originalLink.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase))
    {
        try
        {
            var encoded = originalLink["vmess://".Length..];
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(NormalizeBase64(encoded)));
            var node = JsonNode.Parse(decoded) as JsonObject;
            if (node is null)
                return originalLink;

            node["ps"] = displayName;
            var json = node.ToJsonString(new JsonSerializerOptions
            {
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            });

            return "vmess://" + Convert.ToBase64String(Encoding.UTF8.GetBytes(json));
        }
        catch
        {
            return originalLink;
        }
    }

    if (originalLink.StartsWith("vless://", StringComparison.OrdinalIgnoreCase) ||
        originalLink.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase))
    {
        try
        {
            var uri = new Uri(originalLink);
            var basePart = originalLink[..originalLink.IndexOf('#', StringComparison.Ordinal)];
            return $"{basePart}#{Uri.EscapeDataString(displayName)}";
        }
        catch
        {
            return originalLink;
        }
    }

    return originalLink;
}

static string NormalizeBase64(string base64)
{
    var normalized = base64.Trim().Replace('-', '+').Replace('_', '/');
    while (normalized.Length % 4 != 0)
        normalized += "=";

    return normalized;
}

static async Task RunTelegramSmokeTestAsync()
{
    ConfigureProxyEnvironment();
    var token = TelegramConfig.Token;
    var rawChatId = TelegramConfig.ChatId;
    var normalizedChatId = TelegramConfig.NormalizeChatTarget(rawChatId);

    Console.WriteLine("════════════════════════════════════════");
    Console.WriteLine("[Telegram] Smoke Test Started");
    Console.WriteLine($"[Telegram] Token configured: {!string.IsNullOrWhiteSpace(token)}");
    Console.WriteLine($"[Telegram] Chat configured: {!string.IsNullOrWhiteSpace(rawChatId)}");
    Console.WriteLine($"[Telegram] Chat target configured: {!string.IsNullOrWhiteSpace(normalizedChatId)}");
    Console.WriteLine($"[Telegram] Target classification: {(TelegramConfig.UsesNumericChatId ? "numeric" : (TelegramConfig.UsesUsernameChatId ? "username" : "unknown"))}");
    Console.WriteLine("════════════════════════════════════════");

    if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(rawChatId))
    {
        Console.WriteLine("[Telegram] Missing token or chat id. Set them in appsettings.json or environment variables.");
        Environment.ExitCode = 1;
        return;
    }

    try
    {
        var client = new TelegramBotClient(token, CreateHttpClient(TimeSpan.FromSeconds(60)));
        var me = await client.GetMe();
        Console.WriteLine($"[Telegram] Bot verified: @{me.Username}");

        var resolvedChat = await client.GetChat(normalizedChatId);
        Console.WriteLine($"[Telegram] Resolved chat: {resolvedChat.Title ?? resolvedChat.Username ?? "configured target"}");

        var message = await client.SendMessage(
            chatId: resolvedChat.Id.ToString(),
            text: "VPNBACKEND Telegram smoke test ✅"
        );

        Console.WriteLine($"[Telegram] Test message delivered. MessageId={message.MessageId}");
        Console.WriteLine("════════════════════════════════════════");
        Console.WriteLine("[Telegram] Smoke test passed");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[Telegram] Smoke test failed: {ex.Message}");
        Console.WriteLine($"[Telegram] Expected numeric chat ID or a resolvable @username/channel link. {TelegramConfig.ExplainChatIdRequirement()}");
        Environment.ExitCode = 1;
    }
}

static void ConfigureProxyEnvironment()
{
    var proxyUrl = Environment.GetEnvironmentVariable("HTTPS_PROXY")
        ?? Environment.GetEnvironmentVariable("https_proxy")
        ?? Environment.GetEnvironmentVariable("HTTP_PROXY")
        ?? Environment.GetEnvironmentVariable("http_proxy");

    if (!string.IsNullOrWhiteSpace(proxyUrl))
    {
        Console.WriteLine("[Proxy] Proxy environment variable detected.");
    }
    else
    {
        Console.WriteLine("[Proxy] No proxy environment variable detected.");
    }
}

static HttpClient CreateHttpClient(TimeSpan timeout)
{
    var proxyUrl = Environment.GetEnvironmentVariable("HTTPS_PROXY")
        ?? Environment.GetEnvironmentVariable("https_proxy")
        ?? Environment.GetEnvironmentVariable("HTTP_PROXY")
        ?? Environment.GetEnvironmentVariable("http_proxy");

    var handler = new HttpClientHandler();
    if (!string.IsNullOrWhiteSpace(proxyUrl))
    {
        handler.UseProxy = true;
        handler.Proxy = new System.Net.WebProxy(proxyUrl)
        {
            BypassProxyOnLocal = false
        };
    }

    return new HttpClient(handler) { Timeout = timeout };
}

