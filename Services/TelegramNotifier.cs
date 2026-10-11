using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Telegram.Bot;
using Telegram.Bot.Exceptions;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using VpnBackend.Models;

namespace VpnBackend.Services
{
    public class TelegramNotifier
    {
        private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(60) };

        private readonly ITelegramBotClient? _bot;
        private readonly string _chatId;
        private readonly string _channel;
        private readonly string _subUrl;

        private const int MaxMessageLength = 3800;
        private const int ShowcaseCount    = 25;
        private const int MaxRetryAttempts = 3;
        private const string MessageStateFile = ".telegram-message-ids.json";

        private sealed class TelegramMessageState
        {
            public string ChatId { get; set; } = "";
            public List<int> MessageIds { get; set; } = new();
        }

        public TelegramNotifier()
            : this(TelegramConfig.Token, TelegramConfig.ChatId,
                   TelegramConfig.ChannelHandle, TelegramConfig.SubscriptionUrl) { }

        public TelegramNotifier(string? botToken, string? chatId, string channel, string subUrl)
        {
            _chatId  = TelegramConfig.NormalizeChatTarget(chatId)?.Trim() ?? "";
            _channel = channel?.Trim() ?? "";
            _subUrl  = subUrl?.Trim()  ?? "";

            if (!string.IsNullOrWhiteSpace(botToken) && !string.IsNullOrWhiteSpace(_chatId))
                _bot = new TelegramBotClient(botToken.Trim(), SharedHttp);
        }

        public bool IsConfigured => _bot is not null && !string.IsNullOrWhiteSpace(_chatId);

        public async Task<bool> VerifyAsync(CancellationToken ct = default)
        {
            if (!IsConfigured)
            {
                Console.WriteLine("[Telegram] تنظیمات TELEGRAM_BOT_TOKEN یا TELEGRAM_CHAT_ID وجود ندارد.");
                return false;
            }

            try
            {
                var me = await _bot!.GetMe(ct);
                var resolvedChatId = await ResolveChatTargetAsync(_chatId, ct);
                if (string.IsNullOrWhiteSpace(resolvedChatId))
                {
                    Console.WriteLine("[Telegram] ✗ نام کانال/URL معتبر نیست و نمی‌توان آن را به chat ID تبدیل کرد.");
                    return false;
                }

                var chat = await _bot!.GetChat(resolvedChatId, ct);
                Console.WriteLine($"[Telegram] ✓ @{me.Username} متصل شد → {chat.Title ?? resolvedChatId}");
                return true;
            }
            catch (ApiRequestException ex) when (ex.ErrorCode is 401)
            {
                Console.WriteLine("[Telegram] ✗ توکن نامعتبر (401). یک توکن جدید بگیر.");
                return false;
            }
            catch (ApiRequestException ex) when (ex.ErrorCode is 400 or 403)
            {
                Console.WriteLine($"[Telegram] ✗ دسترسی به کانال تنظیم‌شده نیست ({ex.ErrorCode}). " +
                                  "ربات را ادمین کانال کن.");
                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Telegram] ✗ خطا: {ex.Message}");
                return false;
            }
        }

        public async Task<bool> PublishAsync(
            List<VpnNode> healthyNodes, int totalSources, int totalNodes, CancellationToken ct = default)
        {
            if (!IsConfigured || healthyNodes is null)
            {
                Console.WriteLine("[Telegram] تنظیمات انتشار معتبر نیست.");
                return false;
            }

            var resolvedChatId = await ResolveChatTargetAsync(_chatId, ct);
            if (string.IsNullOrWhiteSpace(resolvedChatId))
            {
                Console.WriteLine("[Telegram] ✗ کانال هدف قابل‌حل نیست؛ ارسال قطع شد.");
                return false;
            }

            if (!await DeletePreviouslyPublishedAsync(resolvedChatId, ct))
                return false;

            if (healthyNodes.Count == 0)
            {
                Console.WriteLine("[Telegram] هیچ سرور سالمی نیست؛ پیام‌های قبلی حذف شدند.");
                return true;
            }

            var ordered = healthyNodes
                .GroupBy(node => node.GetDeduplicationKey(), StringComparer.Ordinal)
                .Select(group => group.First())
                .OrderBy(node => node.PingMs)
                .ToList();
            var messageState = new TelegramMessageState { ChatId = resolvedChatId };
            if (!await SendTrackedAsync(() => _bot!.SendMessage(
                    chatId: resolvedChatId,
                    text: BuildPost(ordered, totalSources, totalNodes),
                    parseMode: ParseMode.Html,
                    linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
                    replyMarkup: BuildButtons(),
                    cancellationToken: ct), "پست اصلی", resolvedChatId, messageState, ct))
                return false;

            await Task.Delay(2000, ct);

            // ارسال کامل همه سرورهای سالم به‌صورت لیست نام + لوکیشن + پینگ
            var fullListChunks = BuildServerListChunks(ordered);
            for (var i = 0; i < fullListChunks.Count; i++)
            {
                var chunk = fullListChunks[i];
                if (!await SendTrackedAsync(() => _bot!.SendMessage(
                    chatId: resolvedChatId,
                    text: $"<b>📍 سرورهای سالم ({i + 1}/{fullListChunks.Count})</b>\n\n{chunk}",
                    parseMode: ParseMode.Html,
                    linkPreviewOptions: new LinkPreviewOptions { IsDisabled = true },
                    cancellationToken: ct),
                    $"لیست سرورها {i + 1}/{fullListChunks.Count}",
                    resolvedChatId, messageState, ct))
                    return false;

                await Task.Delay(1500, ct);
            }

            if (!await SendFileAsync("my-sub.txt",
                    $"📥 فایل سابسکریپشن ({ordered.Count} سرور)\n{EscapeHtml(_channel)}", resolvedChatId, messageState, ct))
                return false;

            await Task.Delay(2000, ct);

            return await SendFileAsync("valid-servers.txt",
                $"📄 لیست کامل کانفیگ‌های سالم\n{EscapeHtml(_channel)}", resolvedChatId, messageState, ct);
        }

        private string BuildPost(List<VpnNode> nodes, int totalSources, int totalNodes)
        {
            var tehran = DateTime.UtcNow.AddMinutes(210);
            var sb = new StringBuilder();

            sb.AppendLine("⚡️ <b>کانفیگ‌های تست‌شده و سالم</b>");
            sb.AppendLine($"🕒 {tehran:yyyy/MM/dd HH:mm} (تهران)");
            sb.AppendLine();
            sb.AppendLine($"✅ سالم: <b>{nodes.Count}</b> / {totalNodes} کانفیگ ({totalSources} منبع)");
            sb.AppendLine($"🚀 بهترین پینگ: <b>{nodes[0].PingMs}ms</b>");

            var protocols = nodes.GroupBy(n => n.Protocol)
                                 .OrderByDescending(g => g.Count())
                                 .Select(g => $"{g.Key} ({g.Count()})");
            sb.AppendLine($"🔐 {EscapeHtml(string.Join(" · ", protocols))}");

            sb.AppendLine();
            sb.AppendLine("🔗 <b>سابسکریپشن (خودکار آپدیت):</b>");
            sb.AppendLine($"<pre>{EscapeHtml(_subUrl)}</pre>");

            sb.AppendLine($"👇 <b>{Math.Min(ShowcaseCount, nodes.Count)} نمونه (برای کپی ضربه بزنید):</b>");
            sb.Append("<blockquote expandable>");

            int budget = MaxMessageLength - sb.Length - 300;
            int count = 0;

            foreach (var node in nodes.Take(ShowcaseCount))
            {
                var displayName = node.BuildTelegramLabel(_channel);
                var line = $"<b>{EscapeHtml(displayName)}</b>\n<code>{EscapeHtml(Truncate(node.OriginalLink, 1200))}</code>\n\n";
                if (line.Length > budget) break;

                sb.Append(line);
                budget -= line.Length;
                count++;
            }

            sb.AppendLine("</blockquote>");
            sb.AppendLine();
            sb.AppendLine("💡 روی کانفیگ‌ها بزنید تا کپی شود، سپس توی V2rayNG یا Streisand وارد کنید.");
            sb.Append($"\n📢 {EscapeHtml(_channel)}");

            Console.WriteLine($"[Telegram] پست آماده: {count} نمونه");
            return sb.ToString();
        }

        private List<string> BuildServerListChunks(List<VpnNode> nodes)
        {
            var lines = new List<string>();
            for (var i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                var label = node.BuildTelegramLabel(_channel);
                lines.Add($"<b>{i + 1}. {EscapeHtml(label)}</b> · {EscapeHtml(node.Protocol)}");
            }

            var chunks = new List<string>();
            var current = new StringBuilder();

            foreach (var line in lines)
            {
                if (current.Length > 0 && current.Length + line.Length > MaxMessageLength - 120)
                {
                    chunks.Add(current.ToString());
                    current.Clear();
                }

                current.AppendLine(line);
            }

            if (current.Length > 0)
                chunks.Add(current.ToString());

            return chunks;
        }

        private InlineKeyboardMarkup BuildButtons()
        {
            var handle = _channel.TrimStart('@');
            var text = Uri.EscapeDataString("کانفیگ‌های VPN سالم ۲۴/۷ 👇");

            return new InlineKeyboardMarkup(new[]
            {
                new[] { InlineKeyboardButton.WithUrl("📢 عضویت", $"https://t.me/{handle}") },
                new[] { InlineKeyboardButton.WithUrl("🤖 ربات", TelegramConfig.BotLink),
                        InlineKeyboardButton.WithUrl("↗️ اشتراک", $"https://t.me/share/url?url=https://t.me/{handle}&text={text}") }
            });
        }

        private async Task<bool> SendFileAsync(
            string path, string caption, string resolvedChatId, TelegramMessageState messageState, CancellationToken ct)
        {
            if (!File.Exists(path))
            {
                Console.WriteLine($"[Telegram] ✗ فایل موردنیاز پیدا نشد: {path}");
                return false;
            }

            return await SendTrackedAsync(async () =>
            {
                await using var stream = File.OpenRead(path);
                return await _bot!.SendDocument(
                    chatId: resolvedChatId,
                    document: InputFile.FromStream(stream, Path.GetFileName(path)),
                    caption: caption,
                    parseMode: ParseMode.Html,
                    cancellationToken: ct);
            }, $"فایل {Path.GetFileName(path)}", resolvedChatId, messageState, ct);
        }

        private async Task<bool> DeletePreviouslyPublishedAsync(string resolvedChatId, CancellationToken ct)
        {
            var messageState = await LoadMessageStateAsync(resolvedChatId);
            if (messageState is null)
            {
                Console.WriteLine("[Telegram] ✗ فایل شناسهٔ پیام‌ها خراب یا ناخواناست؛ برای جلوگیری از تکرار انتشار متوقف شد.");
                return false;
            }

            if (!string.Equals(messageState.ChatId, resolvedChatId, StringComparison.Ordinal))
            {
                Console.WriteLine("[Telegram] ✗ شناسهٔ کانال با وضعیت پیام‌های ذخیره‌شده فرق دارد؛ ابتدا وضعیت کانال را جداگانه تنظیم کنید.");
                return false;
            }

            foreach (var messageId in messageState.MessageIds.ToList())
            {
                try
                {
                    await _bot!.DeleteMessage(resolvedChatId, messageId, ct);
                }
                catch (ApiRequestException ex) when (
                    ex.ErrorCode == 400 && ex.Message.Contains("message to delete not found", StringComparison.OrdinalIgnoreCase))
                {
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Telegram] ✗ حذف پیام {messageId} ناموفق بود ({ex.Message})؛ انتشار جدید متوقف شد.");
                    return false;
                }

                messageState.MessageIds.Remove(messageId);
                if (!await SaveMessageStateAsync(messageState))
                    return false;
            }

            return true;
        }

        private async Task<bool> SendTrackedAsync(
            Func<Task<Message>> action,
            string label,
            string resolvedChatId,
            TelegramMessageState messageState,
            CancellationToken ct)
        {
            var attempt = 0;
            while (attempt < MaxRetryAttempts)
            {
                try
                {
                    var message = await action();
                    messageState.MessageIds.Add(message.MessageId);
                    if (!await SaveMessageStateAsync(messageState))
                    {
                        try
                        {
                            await _bot!.DeleteMessage(resolvedChatId, message.MessageId, ct);
                        }
                        catch
                        {
                        }

                        Console.WriteLine("[Telegram] ✗ ذخیرهٔ شناسهٔ پیام ناموفق بود؛ انتشار بعدی متوقف شد.");
                        return false;
                    }

                    Console.WriteLine($"[Telegram] ✓ {label}");
                    return true;
                }
                catch (ApiRequestException ex) when (ex.ErrorCode == 429)
                {
                    var wait = ex.Parameters?.RetryAfter ?? 5;
                    Console.WriteLine($"[Telegram] محدودیت نرخ؛ {wait}s صبر...");
                    await Task.Delay(TimeSpan.FromSeconds(wait + 1), ct);
                    attempt++;
                }
                catch (ApiRequestException ex)
                {
                    Console.WriteLine($"[Telegram] ✗ API ({label}): {ex.ErrorCode}");
                    return false;
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Telegram] ✗ {label} ناموفق/نامشخص بود ({ex.Message})؛ برای جلوگیری از ارسال تکراری دوباره تلاش نمی‌شود.");
                    return false;
                }
            }

            return false;
        }

        private static async Task<TelegramMessageState?> LoadMessageStateAsync(string resolvedChatId)
        {
            if (!File.Exists(MessageStateFile))
                return new TelegramMessageState { ChatId = resolvedChatId };

            try
            {
                var json = await File.ReadAllTextAsync(MessageStateFile);
                return JsonSerializer.Deserialize<TelegramMessageState>(json);
            }
            catch
            {
                return null;
            }
        }

        private static async Task<bool> SaveMessageStateAsync(TelegramMessageState messageState)
        {
            var temporaryPath = MessageStateFile + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(messageState));
                File.Move(temporaryPath, MessageStateFile, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Telegram] ✗ ذخیرهٔ وضعیت پیام‌ها ناموفق بود: {ex.Message}");
                try { File.Delete(temporaryPath); }
                catch { }
                return false;
            }
        }

        private async Task<string?> ResolveChatTargetAsync(string? rawChatId, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(rawChatId))
                return null;

            var candidate = rawChatId.Trim();
            if (long.TryParse(candidate, out _))
                return candidate;

            if (candidate.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var uri = new Uri(candidate);
                    var username = uri.AbsolutePath.Trim('/');
                    if (!string.IsNullOrWhiteSpace(username))
                    {
                        Console.WriteLine($"[Telegram] resolving channel URL -> @{username}");
                        var chat = await _bot!.GetChat($"@{username}", ct);
                        return chat.Id.ToString();
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Telegram] failed to resolve URL chat target: {ex.Message}");
                }
            }

            if (candidate.StartsWith("@", StringComparison.Ordinal))
            {
                try
                {
                    Console.WriteLine($"[Telegram] resolving username -> {candidate}");
                    var chat = await _bot!.GetChat(candidate, ct);
                    return chat.Id.ToString();
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[Telegram] failed to resolve username target: {ex.Message}");
                }
            }

            return candidate;
        }

        private static string Truncate(string? s, int max) =>
            string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s[..max] + "...");

        private static string EscapeHtml(string? input) =>
            string.IsNullOrEmpty(input) ? "" :
            input
                .Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;");
    }
}