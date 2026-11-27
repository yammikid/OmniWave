using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;

namespace DynamicIsland.Services;

/// <summary>
/// Локальный HTTP-ретранслятор заголовков потокового аудио (User-Agent / Referer) для LibVLC и DownloadService.
/// </summary>
public static class MediaStreamRelayService
{
    private static readonly ConcurrentDictionary<string, StreamHeaderEntry> StreamHeaders = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, RelayTarget> RelayEntries = new(StringComparer.Ordinal);
    private static readonly object RelayInitLock = new();
    private static readonly HttpClient RelayHttpClient;

    private static LocalRelayServer? _server;

    static MediaStreamRelayService()
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = true,
            AutomaticDecompression = DecompressionMethods.None,
            AllowAutoRedirect = true,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        };
        RelayHttpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(20)
        };
    }

    public static void RegisterStreamHeaders(string? url, string? userAgent, string? referer = null)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        if (string.IsNullOrWhiteSpace(userAgent) && string.IsNullOrWhiteSpace(referer)) return;

        if (StreamHeaders.Count > 200)
        {
            var now = DateTime.UtcNow;
            foreach (var kv in StreamHeaders)
            {
                if (kv.Value.ExpiresUtc <= now || StreamHeaders.Count > 120)
                    StreamHeaders.TryRemove(kv.Key, out _);
            }
        }

        StreamHeaders[url] = new StreamHeaderEntry(
            UserAgent: string.IsNullOrWhiteSpace(userAgent) ? null : userAgent,
            Referer: string.IsNullOrWhiteSpace(referer) ? null : referer,
            ExpiresUtc: DateTime.UtcNow.AddHours(6));
    }

    public static bool HasRegisteredStreamHeaders(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (StreamHeaders.TryGetValue(url, out var entry))
        {
            if (entry.ExpiresUtc > DateTime.UtcNow)
                return true;
            StreamHeaders.TryRemove(url, out _);
        }
        return false;
    }

    public static bool TryGetStreamHeaders(string? url, out string? userAgent, out string? referer)
    {
        userAgent = null;
        referer = null;
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (StreamHeaders.TryGetValue(url, out var entry))
        {
            if (entry.ExpiresUtc > DateTime.UtcNow)
            {
                userAgent = entry.UserAgent;
                referer = entry.Referer;
                return true;
            }
            StreamHeaders.TryRemove(url, out _);
        }
        return false;
    }

    public static string CreateLocalMediaRelayUrl(string targetUrl, string? userAgent = null, string? referer = null)
    {
        int port = EnsureServerPort();
        var now = DateTime.UtcNow;
        if (RelayEntries.Count > 120)
        {
            foreach (var kv in RelayEntries)
            {
                if (kv.Value.ExpiresUtc <= now || RelayEntries.Count > 80)
                    RelayEntries.TryRemove(kv.Key, out _);
            }
        }

        string id = Guid.NewGuid().ToString("N")[..16];
        RelayEntries[id] = new RelayTarget(
            TargetUrl: targetUrl,
            UserAgent: string.IsNullOrWhiteSpace(userAgent) ? null : userAgent,
            Referer: string.IsNullOrWhiteSpace(referer) ? null : referer,
            ExpiresUtc: now.AddHours(6));
        return $"http://127.0.0.1:{port}/__omniwave_relay/{id}";
    }

    public static void Shutdown()
    {
        LocalRelayServer? server;
        lock (RelayInitLock)
        {
            server = _server;
            _server = null;
        }
        server?.Dispose();
    }

    private static int EnsureServerPort()
    {
        lock (RelayInitLock)
        {
            if (_server != null && _server.IsListening)
                return _server.Port;

            _server?.Dispose();
            _server = new LocalRelayServer();
            return _server.Port;
        }
    }

    private sealed record StreamHeaderEntry(string? UserAgent, string? Referer, DateTime ExpiresUtc);

    private sealed record RelayTarget(string TargetUrl, string? UserAgent, string? Referer, DateTime ExpiresUtc);

    private sealed class LocalRelayServer : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();

        public int Port { get; }
        public bool IsListening { get; private set; }

        public LocalRelayServer()
        {
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
            Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            IsListening = true;
            _ = Task.Run(AcceptLoopAsync);
        }

        private async Task AcceptLoopAsync()
        {
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    var client = await _listener.AcceptTcpClientAsync(_cts.Token);
                    _ = Task.Run(() => HandleClientAsync(client, _cts.Token));
                }
            }
            catch
            {
                IsListening = false;
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
        {
            using (client)
            {
                client.NoDelay = true;
                try
                {
                    await using var clientStream = client.GetStream();
                    var headerBytes = await ReadHttpHeaderBlockAsync(clientStream, ct);
                    if (headerBytes.Length == 0)
                        return;

                    string headerText = Encoding.Latin1.GetString(headerBytes);
                    var lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
                    if (lines.Length == 0)
                        return;

                    var reqParts = lines[0].Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                    if (reqParts.Length < 3)
                        return;

                    string method = reqParts[0].ToUpperInvariant();
                    string target = reqParts[1];

                    const string relayPrefix = "/__omniwave_relay/";
                    string? relayId = null;
                    if (target.StartsWith(relayPrefix, StringComparison.Ordinal))
                        relayId = target[relayPrefix.Length..];
                    else if (Uri.TryCreate(target, UriKind.Absolute, out var absUri) &&
                             absUri.AbsolutePath.StartsWith(relayPrefix, StringComparison.Ordinal))
                        relayId = absUri.AbsolutePath[relayPrefix.Length..];

                    if (string.IsNullOrWhiteSpace(relayId) || (method != "GET" && method != "HEAD"))
                    {
                        await WriteSimpleErrorAsync(clientStream, "400 Bad Request", ct);
                        return;
                    }

                    await HandleMediaRelayRequestAsync(clientStream, method, relayId, lines, ct);
                }
                catch { }
            }
        }

        private static async Task HandleMediaRelayRequestAsync(
            Stream clientStream,
            string method,
            string relayId,
            string[] requestLines,
            CancellationToken ct)
        {
            int qIdx = relayId.IndexOfAny(new[] { '?', '#' });
            if (qIdx >= 0)
                relayId = relayId[..qIdx];

            if (!RelayEntries.TryGetValue(relayId, out var entry) || entry.ExpiresUtc <= DateTime.UtcNow)
            {
                await WriteSimpleErrorAsync(clientStream, "404 Not Found", ct);
                return;
            }

            try
            {
                using var req = new HttpRequestMessage(method == "HEAD" ? HttpMethod.Head : HttpMethod.Get, entry.TargetUrl);

                foreach (var line in requestLines.Skip(1))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    int colon = line.IndexOf(':');
                    if (colon <= 0) continue;
                    string hName = line[..colon].Trim();
                    string hVal = line[(colon + 1)..].Trim();

                    if (hName.Equals("Range", StringComparison.OrdinalIgnoreCase) ||
                        hName.Equals("If-Range", StringComparison.OrdinalIgnoreCase))
                    {
                        req.Headers.TryAddWithoutValidation(hName, hVal);
                    }
                }

                string ua = !string.IsNullOrWhiteSpace(entry.UserAgent)
                    ? entry.UserAgent!
                    : "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
                req.Headers.TryAddWithoutValidation("User-Agent", ua);
                if (!string.IsNullOrWhiteSpace(entry.Referer))
                    req.Headers.TryAddWithoutValidation("Referer", entry.Referer);

                using var resp = await RelayHttpClient.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

                var sb = new StringBuilder();
                int code = (int)resp.StatusCode;
                string reason = string.IsNullOrWhiteSpace(resp.ReasonPhrase) ? "OK" : resp.ReasonPhrase;
                sb.Append($"HTTP/1.1 {code} {reason}\r\n");

                if (resp.Content.Headers.ContentType != null)
                    sb.Append($"Content-Type: {resp.Content.Headers.ContentType}\r\n");
                if (resp.Content.Headers.ContentLength.HasValue)
                    sb.Append($"Content-Length: {resp.Content.Headers.ContentLength.Value}\r\n");
                if (resp.Content.Headers.ContentRange != null)
                    sb.Append($"Content-Range: {resp.Content.Headers.ContentRange}\r\n");
                sb.Append("Accept-Ranges: bytes\r\n");
                sb.Append("Connection: close\r\n\r\n");

                await clientStream.WriteAsync(Encoding.ASCII.GetBytes(sb.ToString()), ct);
                await clientStream.FlushAsync(ct);

                if (method != "HEAD")
                {
                    await using var bodyStream = await resp.Content.ReadAsStreamAsync(ct);
                    await bodyStream.CopyToAsync(clientStream, 32768, ct);
                    await clientStream.FlushAsync(ct);
                }
            }
            catch
            {
                await WriteSimpleErrorAsync(clientStream, "502 Bad Gateway", ct);
            }
        }

        private static async Task<byte[]> ReadHttpHeaderBlockAsync(Stream stream, CancellationToken ct)
        {
            var buffer = new byte[8192];
            using var ms = new MemoryStream();
            while (ms.Length < 65536)
            {
                int read = await stream.ReadAsync(buffer, 0, buffer.Length, ct);
                if (read <= 0) break;
                ms.Write(buffer, 0, read);

                if (TryExtractHeaderBytes(ms, out var header))
                    return header;
            }
            return Array.Empty<byte>();
        }

        private static bool TryExtractHeaderBytes(MemoryStream ms, out byte[] header)
        {
            var span = ms.GetBuffer().AsSpan(0, (int)ms.Length);
            int idx = span.IndexOf("\r\n\r\n"u8);
            if (idx >= 0)
            {
                header = span[..(idx + 4)].ToArray();
                return true;
            }
            header = Array.Empty<byte>();
            return false;
        }

        private static async Task WriteSimpleErrorAsync(Stream stream, string status, CancellationToken ct)
        {
            try
            {
                var bytes = Encoding.ASCII.GetBytes($"HTTP/1.1 {status}\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(bytes, ct);
                await stream.FlushAsync(ct);
            }
            catch { }
        }

        public void Dispose()
        {
            IsListening = false;
            try { _cts.Cancel(); } catch { }
            try { _listener.Stop(); } catch { }
            _cts.Dispose();
        }
    }
}
