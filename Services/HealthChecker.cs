using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace VpnBackend.Services
{
    /// <summary>
    /// این سروه برای تست سلامت سرورهای VPN استفاده می‌شود.
    /// ابتدا TCP را بررسی می‌کند و اگر Xray موجود باشد، یک تست پروتکل-مستقل هم انجام می‌دهد.
    /// </summary>
    public class HealthChecker
    {
        private const int TimeoutMs = 2000;
        private const int XrayTimeoutMs = 15000;
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
        private readonly string? _xrayBinaryPath;

        public HealthChecker(string? xrayBinaryPath = null)
        {
            _xrayBinaryPath = string.IsNullOrWhiteSpace(xrayBinaryPath)
                ? ResolveXrayPath()
                : xrayBinaryPath;
        }

        public async Task<(bool IsAlive, long Ping)> CheckNodeAsync(
            string address,
            int port,
            string? link = null,
            string? protocol = null)
        {
            if (string.IsNullOrEmpty(address) || port <= 0 || port > 65535)
                return (false, -1);

            var tcpResult = await CheckTcpAsync(address, port);
            if (!tcpResult.IsAlive)
                return (false, -1);

            if (string.IsNullOrWhiteSpace(_xrayBinaryPath))
                return tcpResult;

            var xrayResult = await CheckNodeWithXrayAsync(address, port, link, protocol, _xrayBinaryPath);
            return xrayResult ? tcpResult : (false, -1);
        }

        private async Task<(bool IsAlive, long Ping)> CheckTcpAsync(string address, int port)
        {
            using var tcpClient = new TcpClient();
            var stopwatch = new Stopwatch();

            try
            {
                stopwatch.Start();
                var connectTask = tcpClient.ConnectAsync(address, port);
                var timeoutTask = Task.Delay(TimeoutMs);

                if (await Task.WhenAny(connectTask, timeoutTask) == timeoutTask)
                {
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
                return (false, -1);
            }
        }

        private async Task<bool> CheckNodeWithXrayAsync(
            string address,
            int port,
            string? link,
            string? protocol,
            string xrayBinaryPath)
        {
            if (string.IsNullOrWhiteSpace(link))
                return true;

            string? xrayConfig = BuildXrayConfig(link, address, port, protocol);
            if (string.IsNullOrWhiteSpace(xrayConfig))
                return true;

            string tempFile = Path.Combine(Path.GetTempPath(), $"xray-check-{Guid.NewGuid():N}.json");

            try
            {
                await File.WriteAllTextAsync(tempFile, xrayConfig);

                var psi = new ProcessStartInfo
                {
                    FileName = xrayBinaryPath,
                    Arguments = $"-test -config \"{tempFile}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null)
                    return false;

                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();

                var completedTask = process.WaitForExitAsync();
                var timeoutTask = Task.Delay(XrayTimeoutMs);

                var result = await Task.WhenAny(completedTask, timeoutTask);
                if (result != completedTask)
                {
                    try { process.Kill(entireProcessTree: true); }
                    catch { }
                    return false;
                }

                await outputTask;
                await errorTask;

                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
            finally
            {
                try { File.Delete(tempFile); }
                catch { }
            }
        }

        private static string? BuildXrayConfig(string link, string address, int port, string? protocol)
        {
            try
            {
                var normalizedProtocol = (protocol ?? string.Empty).Trim();
                if (string.IsNullOrWhiteSpace(normalizedProtocol))
                {
                    if (link.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase)) normalizedProtocol = "vmess";
                    else if (link.StartsWith("vless://", StringComparison.OrdinalIgnoreCase)) normalizedProtocol = "vless";
                    else if (link.StartsWith("trojan://", StringComparison.OrdinalIgnoreCase)) normalizedProtocol = "trojan";
                }

                normalizedProtocol = normalizedProtocol.ToLowerInvariant();

                return normalizedProtocol switch
                {
                    "vmess" => BuildVmessXrayConfig(link, address, port),
                    "vless" => BuildVlessXrayConfig(link, address, port),
                    "trojan" => BuildTrojanXrayConfig(link, address, port),
                    _ => null
                };
            }
            catch
            {
                return null;
            }
        }

        private static string BuildVmessXrayConfig(string link, string address, int port)
        {
            var base64Json = link.Substring("vmess://".Length);
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(NormalizeBase64(base64Json)));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var id = root.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
            var tls = root.TryGetProperty("tls", out var tlsProp) && tlsProp.ValueKind == JsonValueKind.String ? tlsProp.GetString() ?? "none" : "none";
            var net = root.TryGetProperty("net", out var netProp) && netProp.ValueKind == JsonValueKind.String ? netProp.GetString() ?? "tcp" : "tcp";

            var config = new
            {
                log = new { loglevel = "warning" },
                inbounds = new[]
                {
                    new
                    {
                        port = 10808,
                        protocol = "socks",
                        settings = new { auth = "noauth", udp = true },
                        sniffing = new { enabled = true, destOverride = new[] { "http", "tls" } }
                    }
                },
                outbounds = new[]
                {
                    new
                    {
                        protocol = "vmess",
                        settings = new
                        {
                            vnext = new[]
                            {
                                new
                                {
                                    address = address,
                                    port = port,
                                    users = new[]
                                    {
                                        new { id = id, alterId = 0, security = "auto" }
                                    }
                                }
                            }
                        },
                        streamSettings = new
                        {
                            network = net,
                            security = tls == "tls" ? "tls" : "none"
                        }
                    }
                }
            };

            return JsonSerializer.Serialize(config, JsonOptions);
        }

        private static string BuildVlessXrayConfig(string link, string address, int port)
        {
            var uri = new Uri(link);
            var userId = uri.UserInfo;

            var config = new
            {
                log = new { loglevel = "warning" },
                inbounds = new[]
                {
                    new
                    {
                        port = 10808,
                        protocol = "socks",
                        settings = new { auth = "noauth", udp = true },
                        sniffing = new { enabled = true, destOverride = new[] { "http", "tls" } }
                    }
                },
                outbounds = new[]
                {
                    new
                    {
                        protocol = "vless",
                        settings = new
                        {
                            vnext = new[]
                            {
                                new
                                {
                                    address = address,
                                    port = port,
                                    users = new[]
                                    {
                                        new { id = userId, encryption = "none", level = 0 }
                                    }
                                }
                            }
                        },
                        streamSettings = new
                        {
                            network = "tcp",
                            security = "none"
                        }
                    }
                }
            };

            return JsonSerializer.Serialize(config, JsonOptions);
        }

        private static string BuildTrojanXrayConfig(string link, string address, int port)
        {
            var uri = new Uri(link);
            var password = uri.UserInfo;

            var config = new
            {
                log = new { loglevel = "warning" },
                inbounds = new[]
                {
                    new
                    {
                        port = 10808,
                        protocol = "socks",
                        settings = new { auth = "noauth", udp = true },
                        sniffing = new { enabled = true, destOverride = new[] { "http", "tls" } }
                    }
                },
                outbounds = new[]
                {
                    new
                    {
                        protocol = "trojan",
                        settings = new
                        {
                            servers = new[]
                            {
                                new
                                {
                                    address = address,
                                    port = port,
                                    password = password,
                                    email = "xray-test@example.com"
                                }
                            }
                        },
                        streamSettings = new
                        {
                            network = "tcp",
                            security = "none"
                        }
                    }
                }
            };

            return JsonSerializer.Serialize(config, JsonOptions);
        }

        private static string ResolveXrayPath()
        {
            var candidates = new List<string>();

            var envXrayBinaryPath = Environment.GetEnvironmentVariable("XRAY_BINARY_PATH");
            var envXrayPath = Environment.GetEnvironmentVariable("XRAY_PATH");

            if (!string.IsNullOrWhiteSpace(envXrayBinaryPath)) candidates.Add(envXrayBinaryPath);
            if (!string.IsNullOrWhiteSpace(envXrayPath)) candidates.Add(envXrayPath);
            candidates.Add("/usr/local/bin/xray");
            candidates.Add("/usr/bin/xray");
            candidates.Add("/opt/xray/xray");
            candidates.Add("xray");

            foreach (var candidate in candidates)
            {
                if (string.IsNullOrWhiteSpace(candidate))
                    continue;

                if (candidate.Equals("xray", StringComparison.OrdinalIgnoreCase))
                    return "xray";

                if (File.Exists(candidate))
                    return candidate;
            }

            return string.Empty;
        }

        private static string NormalizeBase64(string base64)
        {
            var normalized = base64.Trim().Replace('-', '+').Replace('_', '/');
            while (normalized.Length % 4 != 0)
                normalized += "=";
            return normalized;
        }
    }
}