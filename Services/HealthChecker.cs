using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using System.Text;
using System.Text.Json;
using System.Threading;
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
        private const string ProbeUrl = "https://www.gstatic.com/generate_204";
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
            Console.WriteLine($"[Health Debug] شروع بررسی سرور: {address}:{port} | protocol={protocol ?? "unknown"}");

            if (string.IsNullOrEmpty(address) || port <= 0 || port > 65535)
            {
                Console.WriteLine($"[Health Debug] ورودی نامعتبر: address='{address}', port={port}");
                return (false, -1);
            }

            var tcpResult = await CheckTcpAsync(address, port);
            if (!tcpResult.IsAlive)
            {
                Console.WriteLine($"[Health Debug] TCP شکست خورد: {address}:{port}");
                return (false, -1);
            }

            Console.WriteLine($"[Health Debug] TCP OK: {address}:{port} | Ping={tcpResult.Ping}ms");

            if (string.IsNullOrWhiteSpace(_xrayBinaryPath))
            {
                Console.WriteLine("[Health Debug] Xray پیدا نشد؛ پینگ Xray قابل‌اندازه‌گیری نیست.");
                return (false, -1);
            }

            var xrayResult = await CheckNodeWithXrayAsync(address, port, link, protocol, _xrayBinaryPath);
            Console.WriteLine($"[Health Debug] نتیجه Xray برای {address}:{port}: {(xrayResult.IsAlive ? $"OK ({xrayResult.Ping}ms)" : "FAIL")}");
            return xrayResult;
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
            catch (Exception ex)
            {
                Console.WriteLine($"[Health Error] CheckTcpAsync شکست خورد برای {address}:{port}. {ex.Message}");
                return (false, -1);
            }
        }

        private async Task<(bool IsAlive, long Ping)> CheckNodeWithXrayAsync(
            string address,
            int port,
            string? link,
            string? protocol,
            string xrayBinaryPath)
        {
            if (string.IsNullOrWhiteSpace(link))
                return (false, -1);

            var socksPort = GetAvailablePort();
            string? xrayConfig = BuildXrayConfig(link, address, port, protocol, socksPort);
            if (string.IsNullOrWhiteSpace(xrayConfig))
                return (false, -1);

            string tempFile = Path.Combine(Path.GetTempPath(), $"xray-check-{Guid.NewGuid():N}.json");
            Process? process = null;
            Task<string>? outputTask = null;
            Task<string>? errorTask = null;

            try
            {
                await File.WriteAllTextAsync(tempFile, xrayConfig);

                var psi = new ProcessStartInfo
                {
                    FileName = xrayBinaryPath,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("run");
                psi.ArgumentList.Add("-config");
                psi.ArgumentList.Add(tempFile);

                process = Process.Start(psi);
                if (process == null)
                    return (false, -1);

                outputTask = process.StandardOutput.ReadToEndAsync();
                errorTask = process.StandardError.ReadToEndAsync();
                using var cancellation = new CancellationTokenSource(XrayTimeoutMs);

                if (!await WaitForProxyAsync(process, socksPort, cancellation.Token))
                    return (false, -1);

                var stopwatch = Stopwatch.StartNew();
                await ProbeThroughXrayAsync(socksPort, cancellation.Token);
                stopwatch.Stop();
                return (true, stopwatch.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Health Error] Xray Validate شکست خورد برای {address}:{port}. {ex.Message}");
                return (false, -1);
            }
            finally
            {
                if (process is not null)
                {
                    try
                    {
                        if (!process.HasExited)
                            process.Kill(entireProcessTree: true);
                    }
                    catch { }

                    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); }
                    catch { }

                    try { await outputTask!.WaitAsync(TimeSpan.FromSeconds(2)); }
                    catch { }

                    try { await errorTask!.WaitAsync(TimeSpan.FromSeconds(2)); }
                    catch { }

                    process.Dispose();
                }

                try { File.Delete(tempFile); }
                catch (Exception deleteEx)
                {
                    Console.WriteLine($"[Health Warning] حذف فایل موقت Xray ناموفق بود: {deleteEx.Message}");
                }
            }
        }

        private static int GetAvailablePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }

        private static async Task<bool> WaitForProxyAsync(Process process, int socksPort, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested && !process.HasExited)
            {
                using var client = new TcpClient();
                try
                {
                    await client.ConnectAsync(IPAddress.Loopback, socksPort, ct);
                    return true;
                }
                catch (SocketException)
                {
                    await Task.Delay(100, ct);
                }
            }

            return false;
        }

        private static async Task ProbeThroughXrayAsync(int socksPort, CancellationToken ct)
        {
            using var handler = new SocketsHttpHandler
            {
                ConnectCallback = (context, token) => ConnectThroughSocksAsync(socksPort, context.DnsEndPoint, token)
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromMilliseconds(XrayTimeoutMs) };
            using var response = await client.GetAsync(ProbeUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            response.EnsureSuccessStatusCode();
        }

        private static async ValueTask<Stream> ConnectThroughSocksAsync(
            int socksPort, DnsEndPoint destination, CancellationToken ct)
        {
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(IPAddress.Loopback, socksPort, ct);
                var stream = new NetworkStream(socket, ownsSocket: true);
                await stream.WriteAsync(new byte[] { 5, 1, 0 }, ct);

                var greeting = new byte[2];
                await stream.ReadExactlyAsync(greeting, ct);
                if (greeting[0] != 5 || greeting[1] != 0)
                    throw new IOException("Xray SOCKS5 proxy rejected no-auth negotiation.");

                var hostname = Encoding.ASCII.GetBytes(new System.Globalization.IdnMapping().GetAscii(destination.Host));
                if (hostname.Length is 0 or > 255)
                    throw new IOException("Probe host is not valid for SOCKS5.");

                var request = new byte[7 + hostname.Length];
                request[0] = 5;
                request[1] = 1;
                request[2] = 0;
                request[3] = 3;
                request[4] = (byte)hostname.Length;
                hostname.CopyTo(request, 5);
                request[^2] = (byte)(destination.Port >> 8);
                request[^1] = (byte)destination.Port;
                await stream.WriteAsync(request, ct);

                var reply = new byte[4];
                await stream.ReadExactlyAsync(reply, ct);
                if (reply[0] != 5 || reply[1] != 0)
                    throw new IOException($"Xray SOCKS5 connect failed with code {reply[1]}.");

                var addressLength = reply[3] switch
                {
                    1 => 4,
                    4 => 16,
                    3 => await ReadSocksDomainLengthAsync(stream, ct),
                    _ => throw new IOException("Xray SOCKS5 returned an invalid address type.")
                };
                var boundAddress = new byte[addressLength + 2];
                await stream.ReadExactlyAsync(boundAddress, ct);
                return stream;
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }

        private static async Task<int> ReadSocksDomainLengthAsync(Stream stream, CancellationToken ct)
        {
            var length = new byte[1];
            await stream.ReadExactlyAsync(length, ct);
            return length[0];
        }

        private static string? BuildXrayConfig(string link, string address, int port, string? protocol, int socksPort)
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
                    "vmess" => BuildVmessXrayConfig(link, address, port, socksPort),
                    "vless" => BuildVlessXrayConfig(link, address, port, socksPort),
                    "trojan" => BuildTrojanXrayConfig(link, address, port, socksPort),
                    _ => null
                };
            }
            catch
            {
                return null;
            }
        }

        private static string BuildVmessXrayConfig(string link, string address, int port, int socksPort)
        {
            var base64Json = link.Substring("vmess://".Length);
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(NormalizeBase64(base64Json)));
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var id = root.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? "" : "";
            var tls = root.TryGetProperty("tls", out var tlsProp) && tlsProp.ValueKind == JsonValueKind.String ? tlsProp.GetString() ?? "none" : "none";
            var net = root.TryGetProperty("net", out var netProp) && netProp.ValueKind == JsonValueKind.String ? netProp.GetString() ?? "tcp" : "tcp";
            var alterId = root.TryGetProperty("aid", out var aidProp) && int.TryParse(aidProp.ToString(), out var parsedAlterId) ? parsedAlterId : 0;
            var cipher = root.TryGetProperty("scy", out var cipherProp) ? cipherProp.GetString() ?? "auto" : "auto";
            var serverName = GetJsonString(root, "sni") ?? GetJsonString(root, "host") ?? address;
            var networkType = GetJsonString(root, "type") ?? "none";

            var config = new
            {
                log = new { loglevel = "warning" },
                inbounds = new[]
                {
                    new
                    {
                        port = socksPort,
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
                                        new { id = id, alterId, security = cipher }
                                    }
                                }
                            }
                        },
                        streamSettings = BuildStreamSettings(
                            net,
                            string.Equals(tls, "tls", StringComparison.OrdinalIgnoreCase) || tls == "1" ? "tls" : "none",
                            serverName,
                            GetJsonString(root, "fp"),
                            GetJsonString(root, "host"),
                            GetJsonString(root, "path"),
                            networkType,
                            null,
                            null,
                            null,
                            false)
                    }
                }
            };

            return JsonSerializer.Serialize(config, JsonOptions);
        }

        private static string BuildVlessXrayConfig(string link, string address, int port, int socksPort)
        {
            var uri = new Uri(link);
            var query = ParseQuery(uri.Query);
            var serverName = GetQueryValue(query, "sni") ?? GetQueryValue(query, "serverName") ?? address;
            var config = new
            {
                log = new { loglevel = "warning" },
                inbounds = new[] { new { port = socksPort, protocol = "socks", settings = new { auth = "noauth", udp = true } } },
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
                                    address,
                                    port,
                                    users = new[] { new { id = Uri.UnescapeDataString(uri.UserInfo), encryption = GetQueryValue(query, "encryption") ?? "none", flow = GetQueryValue(query, "flow") ?? "", level = 0 } }
                                }
                            }
                        },
                        streamSettings = BuildStreamSettings(
                            GetQueryValue(query, "type") ?? "tcp",
                            GetQueryValue(query, "security") ?? "none",
                            serverName,
                            GetQueryValue(query, "fp"),
                            GetQueryValue(query, "host"),
                            GetQueryValue(query, "path") ?? GetQueryValue(query, "serviceName"),
                            GetQueryValue(query, "headerType"),
                            GetQueryValue(query, "pbk"),
                            GetQueryValue(query, "sid"),
                            GetQueryValue(query, "spx"),
                            IsTrue(GetQueryValue(query, "allowInsecure")))
                    }
                }
            };
            return JsonSerializer.Serialize(config, JsonOptions);
        }

        private static string BuildTrojanXrayConfig(string link, string address, int port, int socksPort)
        {
            var uri = new Uri(link);
            var query = ParseQuery(uri.Query);
            var serverName = GetQueryValue(query, "sni") ?? GetQueryValue(query, "serverName") ?? address;
            var config = new
            {
                log = new { loglevel = "warning" },
                inbounds = new[] { new { port = socksPort, protocol = "socks", settings = new { auth = "noauth", udp = true } } },
                outbounds = new[]
                {
                    new
                    {
                        protocol = "trojan",
                        settings = new
                        {
                            servers = new[] { new { address, port, password = Uri.UnescapeDataString(uri.UserInfo), email = "xray-test@example.com" } }
                        },
                        streamSettings = BuildStreamSettings(
                            GetQueryValue(query, "type") ?? "tcp",
                            GetQueryValue(query, "security") ?? "tls",
                            serverName,
                            GetQueryValue(query, "fp"),
                            GetQueryValue(query, "host"),
                            GetQueryValue(query, "path") ?? GetQueryValue(query, "serviceName"),
                            GetQueryValue(query, "headerType"),
                            null,
                            null,
                            null,
                            IsTrue(GetQueryValue(query, "allowInsecure")))
                    }
                }
            };
            return JsonSerializer.Serialize(config, JsonOptions);
        }

        private static JsonObject BuildStreamSettings(
            string network, string security, string? serverName, string? fingerprint,
            string? host, string? path, string? headerType, string? publicKey,
            string? shortId, string? spiderX, bool allowInsecure)
        {
            var normalizedNetwork = string.IsNullOrWhiteSpace(network) ? "tcp" : network.ToLowerInvariant();
            var normalizedSecurity = string.IsNullOrWhiteSpace(security) ? "none" : security.ToLowerInvariant();
            var stream = new JsonObject { ["network"] = normalizedNetwork, ["security"] = normalizedSecurity };
            if (normalizedSecurity == "tls")
                stream["tlsSettings"] = new JsonObject { ["serverName"] = serverName, ["allowInsecure"] = allowInsecure, ["fingerprint"] = fingerprint };
            else if (normalizedSecurity == "reality")
                stream["realitySettings"] = new JsonObject { ["serverName"] = serverName, ["fingerprint"] = fingerprint ?? "chrome", ["publicKey"] = publicKey, ["shortId"] = shortId, ["spiderX"] = spiderX ?? "" };

            if (normalizedNetwork == "ws")
                stream["wsSettings"] = new JsonObject { ["path"] = path ?? "/", ["headers"] = new JsonObject { ["Host"] = host ?? "" } };
            else if (normalizedNetwork == "grpc")
                stream["grpcSettings"] = new JsonObject { ["serviceName"] = path ?? "", ["authority"] = host ?? "", ["multiMode"] = string.Equals(headerType, "multi", StringComparison.OrdinalIgnoreCase) };
            else if (normalizedNetwork is "http" or "h2")
            {
                stream["network"] = "http";
                stream["httpSettings"] = new JsonObject
                {
                    ["path"] = path ?? "/",
                    ["host"] = new JsonArray((host ?? serverName ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(value => (JsonNode?)JsonValue.Create(value.Trim())).ToArray())
                };
            }
            else if (normalizedNetwork == "tcp" && string.Equals(headerType, "http", StringComparison.OrdinalIgnoreCase))
                stream["tcpSettings"] = new JsonObject { ["header"] = new JsonObject { ["type"] = "http" } };

            return stream;
        }

        private static Dictionary<string, string> ParseQuery(string query)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var separator = part.IndexOf('=');
                var key = separator < 0 ? part : part[..separator];
                var value = separator < 0 ? "" : part[(separator + 1)..];
                values[Uri.UnescapeDataString(key.Replace('+', ' '))] = Uri.UnescapeDataString(value.Replace('+', ' '));
            }
            return values;
        }

        private static string? GetQueryValue(Dictionary<string, string> query, string key) => query.TryGetValue(key, out var value) ? value : null;

        private static bool IsTrue(string? value) => value is "1" or "true";

        private static string? GetJsonString(JsonElement element, string propertyName) =>
            element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

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