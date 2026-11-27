using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace DynamicIsland.Services;

public sealed record BridgeCookieDto(
    string Domain,
    bool HostOnly,
    string Path,
    bool Secure,
    double ExpirationDate,
    string Name,
    string Value);

public sealed record BridgeAuthPayload(
    string Provider,
    string? Raw,
    string? SpDc,
    List<BridgeCookieDto>? Cookies,
    string? CookieHeader = null);

/// <summary>
/// Локальный loopback-мост (127.0.0.1:49215..49219) и автогенератор MV3-расширения OmniWave Bridge:
/// - позволяет расширению в Яндекс Браузере / Chrome / Edge / Brave / Opera автоматически забирать sp_dc Spotify,
///   cookies YouTube и URL-токены Яндекс/ВК без ручного копирования;
/// - определяет установленный браузер по умолчанию для подсказки пользователю.
/// </summary>
public sealed class BrowserBridgeServer : IDisposable
{
    private static readonly int[] CandidatePorts = { 49215, 49216, 49217, 49218, 49219 };
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly SettingsService _settings;
    private readonly Func<BridgeAuthPayload, Task<(bool Ok, string Message)>> _onAuthReceived;
    private readonly string _secret;
    private readonly CancellationTokenSource _cts = new();
    private TcpListener? _listener;
    private int _port;
    private DateTime _lastPingUtc = DateTime.MinValue;
    private bool _disposed;

    public event Action<bool>? StatusChanged;

    public int Port => _port;
    public string ExtensionDirectory { get; }
    public string ManifestPath { get; }
    public string BrowserName { get; }
    public string ExtensionsUrl { get; }

    public bool IsExtensionConnected
        => _port > 0 && _lastPingUtc != DateTime.MinValue && (DateTime.UtcNow - _lastPingUtc).TotalSeconds < 75;

    public BrowserBridgeServer(
        SettingsService settings,
        Func<BridgeAuthPayload, Task<(bool Ok, string Message)>> onAuthReceived)
    {
        _settings = settings;
        _onAuthReceived = onAuthReceived;

        _settings.Update(s => { }, notify: false);
        _secret = _settings.Current.BridgeSecret ?? "";

        (BrowserName, ExtensionsUrl) = DetectDefaultBrowser();

        ExtensionDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OmniWave",
            "BrowserBridge");
        ManifestPath = Path.Combine(ExtensionDirectory, "manifest.json");

        WriteExtensionFiles();
        StartLoopbackServer();
    }

    public string GetConnectUrl(string provider)
    {
        int p = _port > 0 ? _port : CandidatePorts[0];
        return $"http://127.0.0.1:{p}/bridge/connect?provider={Uri.EscapeDataString((provider ?? "").Trim().ToLowerInvariant())}";
    }

    private void StartLoopbackServer()
    {
        foreach (int candidate in CandidatePorts)
        {
            TcpListener? listener = null;
            try
            {
                listener = new TcpListener(IPAddress.Loopback, candidate);
                listener.Start();
                _listener = listener;
                _port = candidate;
                _ = Task.Run(() => AcceptLoopAsync(listener, _cts.Token));
                return;
            }
            catch
            {
                try { listener?.Stop(); } catch { }
            }
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_disposed)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                if (ct.IsCancellationRequested || _disposed) break;
                await Task.Delay(120, ct);
                continue;
            }

            _ = Task.Run(() => HandleClientAsync(client, ct), ct);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 10000;
                client.SendTimeout = 10000;
                using var stream = client.GetStream();

                var req = await ReadHttpRequestAsync(stream, ct);
                if (req == null)
                    return;

                string? origin = req.Headers.TryGetValue("origin", out var origVal) ? origVal : null;
                string allowOrigin = !string.IsNullOrWhiteSpace(origin) ? origin : "*";

                if (string.Equals(req.Method, "OPTIONS", StringComparison.OrdinalIgnoreCase))
                {
                    await SendResponseAsync(stream, "204 No Content", "text/plain; charset=utf-8", "", allowOrigin, ct);
                    return;
                }

                if (string.Equals(req.Method, "GET", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(req.Path, "/bridge/connect", StringComparison.OrdinalIgnoreCase))
                {
                    req.Query.TryGetValue("provider", out var prov);
                    var html = BuildConnectFallbackHtml(prov ?? "");
                    await SendResponseAsync(stream, "200 OK", "text/html; charset=utf-8", html, allowOrigin, ct);
                    return;
                }

                req.Headers.TryGetValue("x-omniwave-token", out var tokenHeader);
                if (string.IsNullOrWhiteSpace(_secret) ||
                    !string.Equals((tokenHeader ?? "").Trim(), _secret, StringComparison.Ordinal))
                {
                    await SendResponseAsync(
                        stream,
                        "403 Forbidden",
                        "application/json; charset=utf-8",
                        "{\"ok\":false,\"message\":\"Forbidden\"}",
                        allowOrigin,
                        ct);
                    return;
                }

                if (string.Equals(req.Method, "GET", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(req.Path, "/bridge/ping", StringComparison.OrdinalIgnoreCase))
                {
                    bool wasConnected = IsExtensionConnected;
                    _lastPingUtc = DateTime.UtcNow;
                    if (!wasConnected)
                        StatusChanged?.Invoke(true);

                    var respJson = JsonSerializer.Serialize(new { ok = true, port = _port, browser = BrowserName });
                    await SendResponseAsync(stream, "200 OK", "application/json; charset=utf-8", respJson, allowOrigin, ct);
                    return;
                }

                if (string.Equals(req.Method, "POST", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(req.Path, "/bridge/auth", StringComparison.OrdinalIgnoreCase))
                {
                    bool wasConnected = IsExtensionConnected;
                    _lastPingUtc = DateTime.UtcNow;
                    if (!wasConnected)
                        StatusChanged?.Invoke(true);

                    BridgeAuthPayload? payload = null;
                    try
                    {
                        if (!string.IsNullOrWhiteSpace(req.Body))
                            payload = JsonSerializer.Deserialize<BridgeAuthPayload>(req.Body, JsonOptions);
                    }
                    catch { }

                    if (payload == null || string.IsNullOrWhiteSpace(payload.Provider))
                    {
                        await SendResponseAsync(
                            stream,
                            "400 Bad Request",
                            "application/json; charset=utf-8",
                            "{\"ok\":false,\"message\":\"Некорректные данные от моста браузера.\"}",
                            allowOrigin,
                            ct);
                        return;
                    }

                    var (ok, message) = await _onAuthReceived(payload);
                    var respJson = JsonSerializer.Serialize(new { ok, message });
                    await SendResponseAsync(stream, "200 OK", "application/json; charset=utf-8", respJson, allowOrigin, ct);
                    return;
                }

                await SendResponseAsync(
                    stream,
                    "404 Not Found",
                    "application/json; charset=utf-8",
                    "{\"ok\":false,\"message\":\"Not Found\"}",
                    allowOrigin,
                    ct);
            }
            catch { }
        }
    }

    private sealed record HttpRequestData(
        string Method,
        string Path,
        Dictionary<string, string> Query,
        Dictionary<string, string> Headers,
        string Body);

    private static async Task<HttpRequestData?> ReadHttpRequestAsync(NetworkStream stream, CancellationToken ct)
    {
        using var ms = new MemoryStream();
        var buf = new byte[8192];
        int headerEnd = -1;

        while (ms.Length < 1024 * 1024)
        {
            int n = await stream.ReadAsync(buf, ct);
            if (n <= 0) break;
            ms.Write(buf, 0, n);

            var arr = ms.ToArray();
            headerEnd = FindHeaderEnd(arr);
            if (headerEnd >= 0)
                break;
        }

        if (headerEnd < 0)
            return null;

        var rawAll = ms.ToArray();
        var headerText = Encoding.ASCII.GetString(rawAll, 0, headerEnd);
        var lines = headerText.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
        if (lines.Length == 0)
            return null;

        var reqLineParts = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (reqLineParts.Length < 2)
            return null;

        string method = reqLineParts[0];
        string rawTarget = reqLineParts[1];
        int qIdx = rawTarget.IndexOf('?');
        string path = qIdx >= 0 ? rawTarget[..qIdx] : rawTarget;
        string queryStr = qIdx >= 0 ? rawTarget[(qIdx + 1)..] : "";

        var query = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrWhiteSpace(queryStr))
        {
            foreach (var pair in queryStr.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                if (eq <= 0) continue;
                var k = Uri.UnescapeDataString(pair[..eq].Replace('+', ' '));
                var v = Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
                query[k] = v;
            }
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            int col = line.IndexOf(':');
            if (col <= 0) continue;
            var name = line[..col].Trim();
            var value = line[(col + 1)..].Trim();
            headers[name] = value;
        }

        int contentLength = 0;
        if (headers.TryGetValue("content-length", out var clStr) && int.TryParse(clStr, out int parsedCl))
            contentLength = Math.Clamp(parsedCl, 0, 4 * 1024 * 1024);

        string body = "";
        if (contentLength > 0)
        {
            int bodyStart = headerEnd + 4;
            int already = Math.Max(0, rawAll.Length - bodyStart);
            var bodyBytes = new byte[contentLength];
            int copied = Math.Min(already, contentLength);
            if (copied > 0)
                Buffer.BlockCopy(rawAll, bodyStart, bodyBytes, 0, copied);

            while (copied < contentLength)
            {
                int n = await stream.ReadAsync(bodyBytes.AsMemory(copied, contentLength - copied), ct);
                if (n <= 0) break;
                copied += n;
            }

            body = Encoding.UTF8.GetString(bodyBytes, 0, copied);
        }

        return new HttpRequestData(method, path, query, headers, body);
    }

    private static int FindHeaderEnd(byte[] bytes)
    {
        for (int i = 0; i <= bytes.Length - 4; i++)
        {
            if (bytes[i] == '\r' && bytes[i + 1] == '\n' && bytes[i + 2] == '\r' && bytes[i + 3] == '\n')
                return i;
        }
        return -1;
    }

    private static async Task SendResponseAsync(
        NetworkStream stream,
        string status,
        string contentType,
        string body,
        string allowOrigin,
        CancellationToken ct)
    {
        var bodyBytes = string.IsNullOrEmpty(body) ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(body);
        var sb = new StringBuilder();
        sb.Append($"HTTP/1.1 {status}\r\n");
        sb.Append($"Content-Type: {contentType}\r\n");
        sb.Append($"Content-Length: {bodyBytes.Length}\r\n");
        sb.Append($"Access-Control-Allow-Origin: {allowOrigin}\r\n");
        sb.Append("Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n");
        sb.Append("Access-Control-Allow-Headers: Content-Type, X-OmniWave-Token\r\n");
        sb.Append("Access-Control-Allow-Private-Network: true\r\n");
        sb.Append("Cache-Control: no-store\r\n");
        sb.Append("Connection: close\r\n\r\n");

        var headerBytes = Encoding.ASCII.GetBytes(sb.ToString());
        await stream.WriteAsync(headerBytes, ct);
        if (bodyBytes.Length > 0)
            await stream.WriteAsync(bodyBytes, ct);
        await stream.FlushAsync(ct);
    }

    private string BuildConnectFallbackHtml(string provider)
    {
        string title = string.Equals(provider, "youtube", StringComparison.OrdinalIgnoreCase)
            ? "YouTube"
            : (string.Equals(provider, "spotify", StringComparison.OrdinalIgnoreCase)
                ? "Spotify"
                : (string.Equals(provider, "vk", StringComparison.OrdinalIgnoreCase) ? "VK Музыка" : "аккаунты"));

        return
            "<!doctype html><html lang=\"ru\"><head><meta charset=\"utf-8\"><title>OmniWave Bridge</title>" +
            "<style>" +
            "body{margin:0;min-height:100vh;display:flex;align-items:center;justify-content:center;background:#0A0B0E;color:#F3F4F6;font-family:'Segoe UI',system-ui,sans-serif;}" +
            ".card{max-width:460px;padding:24px 26px;border-radius:16px;background:#14161D;border:1px solid rgba(255,255,255,0.12);box-shadow:0 18px 46px rgba(0,0,0,0.48);}" +
            "h1{margin:0 0 8px;font-size:18px;color:#FFFFFF;font-weight:600;}" +
            "p{margin:0 0 10px;font-size:13.5px;line-height:1.5;color:#9CA3AF;}" +
            ".hint{display:none;margin-top:14px;padding:12px;border-radius:10px;background:rgba(255,255,255,0.04);border:1px solid rgba(255,255,255,0.08);font-size:12.5px;color:#E5E7EB;}" +
            "code{padding:2px 6px;border-radius:5px;background:rgba(255,255,255,0.08);font-family:Consolas,monospace;font-size:12px;}" +
            "</style></head><body><div class=\"card\">" +
            $"<h1>Синхронизация {WebUtility.HtmlEncode(title)} через OmniWave Bridge...</h1>" +
            "<p id=\"msg\">Если расширение <b>OmniWave Bridge</b> включено, вкладка закроется автоматически через секунду.</p>" +
            "<div id=\"hint\" class=\"hint\">" +
            $"Похоже, расширение ещё не добавлено в <b>{WebUtility.HtmlEncode(BrowserName)}</b>.<br><br>" +
            $"1. Откройте <code>{WebUtility.HtmlEncode(ExtensionsUrl)}</code> и включите <b>Режим разработчика</b>.<br>" +
            $"2. Нажмите «Загрузить распакованное расширение» и выберите папку:<br><code>{WebUtility.HtmlEncode(ExtensionDirectory)}</code>" +
            "</div></div>" +
            "<script>setTimeout(function(){document.getElementById('hint').style.display='block';document.getElementById('msg').textContent='Расширение OmniWave Bridge не откликнулось.';},1600);</script>" +
            "</body></html>";
    }

    private static (string Name, string ExtUrl) DetectDefaultBrowser()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\Shell\Associations\UrlAssociations\https\UserChoice");
            var progId = key?.GetValue("ProgId") as string ?? "";
            if (progId.Contains("Yandex", StringComparison.OrdinalIgnoreCase))
                return ("Яндекс Браузер", "browser://extensions");
            if (progId.Contains("MSEdge", StringComparison.OrdinalIgnoreCase))
                return ("Microsoft Edge", "edge://extensions");
            if (progId.Contains("Brave", StringComparison.OrdinalIgnoreCase))
                return ("Brave", "brave://extensions");
            if (progId.Contains("Opera", StringComparison.OrdinalIgnoreCase))
                return ("Opera", "opera://extensions");
        }
        catch { }

        return ("Chrome / Браузер", "chrome://extensions");
    }

    private void WriteExtensionFiles()
    {
        try
        {
            Directory.CreateDirectory(ExtensionDirectory);

            var manifestJson = """
            {
              "manifest_version": 3,
              "name": "OmniWave Bridge",
              "version": "1.0.0",
              "description": "Автоматическая синхронизация аккаунтов Яндекс, ВК, Spotify и YouTube с плеером OmniWave.",
              "permissions": [
                "cookies",
                "tabs",
                "alarms"
              ],
              "host_permissions": [
                "http://127.0.0.1/*",
                "https://*.spotify.com/*",
                "https://*.youtube.com/*",
                "https://*.google.com/*",
                "https://*.vk.com/*",
                "https://*.vk.ru/*",
                "https://*.yandex.ru/*",
                "https://*.yandex.com/*"
              ],
              "background": {
                "service_worker": "background.js"
              },
              "action": {
                "default_popup": "popup.html",
                "default_title": "OmniWave Bridge"
              }
            }
            """;

            var escapedSecret = JsonSerializer.Serialize(_secret);
            var backgroundJs = $$"""
            const BRIDGE_SECRET = {{escapedSecret}};
            const PORTS = [49215, 49216, 49217, 49218, 49219];
            const SPOTIFY_LOGIN_URL = 'https://accounts.spotify.com/login?continue=https%3A%2F%2Fopen.spotify.com%2F';
            const YOUTUBE_LOGIN_URL = 'https://accounts.google.com/ServiceLogin?service=youtube&passive=true&continue=https%3A%2F%2Fwww.youtube.com%2F';
            const VK_LOGIN_URL = 'https://vk.ru/audios';
            const VK_OAUTH_URL = 'https://oauth.vk.ru/authorize?client_id=2685278&scope=audio,offline&redirect_uri=https://oauth.vk.ru/blank.html&display=mobile&response_type=token&v=5.199';
            const YANDEX_OAUTH_URL = 'https://oauth.yandex.ru/authorize?response_type=token&client_id=23cabbbdc6cd418abb4b39c32c41195d';

            let cachedPort = 49215;
            const pendingTabs = {};
            const recentlySent = {};

            function cleanupPending() {
              const now = Date.now();
              for (const k of Object.keys(pendingTabs)) {
                if (!pendingTabs[k] || pendingTabs[k].expiresAt < now) {
                  delete pendingTabs[k];
                }
              }
              for (const u of Object.keys(recentlySent)) {
                if (recentlySent[u] < now - 15000) {
                  delete recentlySent[u];
                }
              }
            }

            async function pingPort(port) {
              const ctl = new AbortController();
              const timer = setTimeout(() => ctl.abort(), 900);
              try {
                const resp = await fetch('http://127.0.0.1:' + port + '/bridge/ping', {
                  method: 'GET',
                  headers: { 'X-OmniWave-Token': BRIDGE_SECRET },
                  signal: ctl.signal
                });
                if (!resp.ok) return false;
                const data = await resp.json();
                return Boolean(data && data.ok);
              } catch (e) {
                return false;
              } finally {
                clearTimeout(timer);
              }
            }

            async function findActivePort() {
              const ordered = [cachedPort].concat(PORTS.filter(p => p !== cachedPort));
              for (const p of ordered) {
                if (await pingPort(p)) {
                  cachedPort = p;
                  return p;
                }
              }
              return null;
            }

            async function postBridgeAuth(payload) {
              const port = await findActivePort();
              if (!port) return { ok: false, message: 'Плеер OmniWave не запущен.' };
              try {
                const resp = await fetch('http://127.0.0.1:' + port + '/bridge/auth', {
                  method: 'POST',
                  headers: {
                    'Content-Type': 'application/json',
                    'X-OmniWave-Token': BRIDGE_SECRET
                  },
                  body: JSON.stringify(payload)
                });
                if (!resp.ok) return { ok: false, message: 'HTTP ' + resp.status };
                return await resp.json();
              } catch (e) {
                return { ok: false, message: String(e && e.message ? e.message : e) };
              }
            }

            async function readSpotifySpDc() {
              const urls = ['https://open.spotify.com/', 'https://accounts.spotify.com/'];
              for (const url of urls) {
                try {
                  const ck = await chrome.cookies.get({ url, name: 'sp_dc' });
                  if (ck && ck.value) return ck.value;
                } catch (e) {}
              }
              return '';
            }

            async function readVkCookies() {
              const ru = await chrome.cookies.getAll({ domain: 'vk.ru' });
              const com = await chrome.cookies.getAll({ domain: 'vk.com' });
              const byName = {};
              for (const c of com.concat(ru)) {
                if (c && c.name && c.value) {
                  byName[c.name] = c.value;
                }
              }
              if (!byName.remixsid && !byName.remixnsid) return '';
              return Object.keys(byName).map(k => k + '=' + byName[k]).join('; ');
            }

            async function readYouTubeCookies() {
              const yt = await chrome.cookies.getAll({ domain: 'youtube.com' });
              const gg = await chrome.cookies.getAll({ domain: 'google.com' });
              const merged = [];
              const seen = {};
              for (const c of yt.concat(gg)) {
                const key = (c.domain || '') + '|' + (c.path || '/') + '|' + (c.name || '');
                if (!seen[key]) {
                  seen[key] = true;
                  merged.push(c);
                }
              }
              const signedIn = merged.some(c =>
                c.name === 'SAPISID' ||
                c.name === '__Secure-3PAPISID' ||
                c.name === 'SID' ||
                c.name === '__Secure-1PSID'
              );
              if (!signedIn) return null;
              return merged.map(c => ({
                domain: c.domain || '',
                hostOnly: Boolean(c.hostOnly),
                path: c.path || '/',
                secure: Boolean(c.secure),
                expirationDate: Number(c.expirationDate || 0),
                name: c.name || '',
                value: c.value || ''
              }));
            }

            async function handleBridgeConnectTab(tabId, provider) {
              cleanupPending();
              if (provider === 'vk') {
                const cookieHeader = await readVkCookies();
                if (cookieHeader) {
                  const res = await postBridgeAuth({ provider: 'vk', cookieHeader });
                  if (res && res.ok) {
                    try { await chrome.tabs.remove(tabId); } catch (e) {}
                    return res;
                  }
                }
                pendingTabs[tabId] = { provider: 'vk', expiresAt: Date.now() + 300000 };
                try { await chrome.tabs.update(tabId, { url: VK_LOGIN_URL }); } catch (e) {}
                return { ok: false, pending: true };
              }

              if (provider === 'spotify') {
                const spDc = await readSpotifySpDc();
                if (spDc) {
                  const res = await postBridgeAuth({ provider: 'spotify', spDc });
                  if (res && res.ok) {
                    try { await chrome.tabs.remove(tabId); } catch (e) {}
                    return res;
                  }
                }
                pendingTabs[tabId] = { provider: 'spotify', expiresAt: Date.now() + 300000 };
                try { await chrome.tabs.update(tabId, { url: SPOTIFY_LOGIN_URL }); } catch (e) {}
                return { ok: false, pending: true };
              }

              if (provider === 'youtube') {
                const cookies = await readYouTubeCookies();
                if (cookies && cookies.length > 0) {
                  const res = await postBridgeAuth({ provider: 'youtube', cookies });
                  if (res && res.ok) {
                    try { await chrome.tabs.remove(tabId); } catch (e) {}
                    return res;
                  }
                }
                pendingTabs[tabId] = { provider: 'youtube', expiresAt: Date.now() + 300000 };
                try { await chrome.tabs.update(tabId, { url: YOUTUBE_LOGIN_URL }); } catch (e) {}
                return { ok: false, pending: true };
              }

              if (provider === 'all') {
                const vkCk = await readVkCookies();
                if (vkCk) await postBridgeAuth({ provider: 'vk', cookieHeader: vkCk });
                const spDc = await readSpotifySpDc();
                if (spDc) await postBridgeAuth({ provider: 'spotify', spDc });
                const yt = await readYouTubeCookies();
                if (yt && yt.length > 0) await postBridgeAuth({ provider: 'youtube', cookies: yt });
                try { await chrome.tabs.remove(tabId); } catch (e) {}
                return { ok: true };
              }

              return { ok: false };
            }

            async function captureTokenUrl(tabId, url) {
              if (!url || !url.includes('access_token=')) return false;
              const lower = url.toLowerCase();
              let provider = '';
              if (lower.includes('oauth.vk.com/blank.html') || lower.includes('oauth.vk.ru/blank.html')) {
                provider = 'vk';
              } else if (
                lower.includes('music.yandex.ru') ||
                lower.includes('music.yandex.com') ||
                lower.includes('oauth.yandex.ru/verification_code') ||
                lower.includes('oauth.yandex.com/verification_code')
              ) {
                provider = 'yandex';
              }
              if (!provider) return false;

              cleanupPending();
              if (recentlySent[url]) return true;
              recentlySent[url] = Date.now();

              const res = await postBridgeAuth({ provider, raw: url });
              if (res && res.ok) {
                try { await chrome.tabs.remove(tabId); } catch (e) {}
              }
              return true;
            }

            chrome.tabs.onUpdated.addListener(async (tabId, changeInfo, tab) => {
              const url = changeInfo.url || (tab && tab.url) || '';
              if (!url) return;

              if (changeInfo.url) {
                const handled = await captureTokenUrl(tabId, changeInfo.url);
                if (handled) return;
              } else if (changeInfo.status === 'complete' && url.includes('access_token=')) {
                const handled = await captureTokenUrl(tabId, url);
                if (handled) return;
              }

              const mBridge = url.match(/^http:\/\/127\.0\.0\.1:(4921[5-9])\/bridge\/connect\?provider=([a-z]+)/i);
              if (mBridge && (changeInfo.status === 'loading' || changeInfo.url)) {
                if (recentlySent['connect:' + tabId]) return;
                recentlySent['connect:' + tabId] = Date.now();
                await handleBridgeConnectTab(tabId, mBridge[2].toLowerCase());
                return;
              }

              const track = pendingTabs[tabId];
              if (!track) return;

              if (
                track.provider === 'vk' &&
                changeInfo.status === 'complete' &&
                (url.startsWith('https://vk.ru') || url.startsWith('https://vk.com') || url.startsWith('https://m.vk.ru') || url.startsWith('https://m.vk.com'))
              ) {
                const cookieHeader = await readVkCookies();
                if (cookieHeader) {
                  delete pendingTabs[tabId];
                  const res = await postBridgeAuth({ provider: 'vk', cookieHeader });
                  if (res && res.ok) {
                    try { await chrome.tabs.remove(tabId); } catch (e) {}
                  }
                }
              } else if (track.provider === 'spotify' && url.startsWith('https://open.spotify.com')) {
                const spDc = await readSpotifySpDc();
                if (spDc) {
                  delete pendingTabs[tabId];
                  const res = await postBridgeAuth({ provider: 'spotify', spDc });
                  if (res && res.ok) {
                    try { await chrome.tabs.remove(tabId); } catch (e) {}
                  }
                }
              } else if (
                track.provider === 'youtube' &&
                changeInfo.status === 'complete' &&
                url.startsWith('https://www.youtube.com')
              ) {
                const cookies = await readYouTubeCookies();
                if (cookies && cookies.length > 0) {
                  delete pendingTabs[tabId];
                  const res = await postBridgeAuth({ provider: 'youtube', cookies });
                  if (res && res.ok) {
                    try { await chrome.tabs.remove(tabId); } catch (e) {}
                  }
                }
              }
            });

            chrome.cookies.onChanged.addListener(async (changeInfo) => {
              if (changeInfo.removed || !changeInfo.cookie) return;
              const cName = changeInfo.cookie.name;

              if (cName === 'remixsid' || cName === 'remixnsid') {
                const vkTabId = Object.keys(pendingTabs).find(id => pendingTabs[id] && pendingTabs[id].provider === 'vk');
                if (!vkTabId) return;
                const cookieHeader = await readVkCookies();
                if (!cookieHeader) return;
                delete pendingTabs[vkTabId];
                const res = await postBridgeAuth({ provider: 'vk', cookieHeader });
                if (res && res.ok) {
                  try { await chrome.tabs.remove(Number(vkTabId)); } catch (e) {}
                }
                return;
              }

              if (cName !== 'sp_dc') return;
              const spotTabId = Object.keys(pendingTabs).find(id => pendingTabs[id] && pendingTabs[id].provider === 'spotify');
              if (!spotTabId) return;
              const val = changeInfo.cookie.value;
              if (!val) return;
              delete pendingTabs[spotTabId];
              const res = await postBridgeAuth({ provider: 'spotify', spDc: val });
              if (res && res.ok) {
                try { await chrome.tabs.remove(Number(spotTabId)); } catch (e) {}
              }
            });

            chrome.tabs.onRemoved.addListener((tabId) => {
              delete pendingTabs[tabId];
            });

            chrome.runtime.onMessage.addListener((msg, _sender, sendResponse) => {
              (async () => {
                if (!msg || !msg.action) {
                  sendResponse({ ok: false });
                  return;
                }
                if (msg.action === 'getStatus') {
                  const port = await findActivePort();
                  sendResponse({ ok: Boolean(port), port });
                  return;
                }
                if (msg.action === 'syncProvider') {
                  const prov = msg.provider;
                  if (prov === 'spotify') {
                    const spDc = await readSpotifySpDc();
                    if (spDc) {
                      const res = await postBridgeAuth({ provider: 'spotify', spDc });
                      sendResponse(res);
                      return;
                    }
                    const tab = await chrome.tabs.create({ url: SPOTIFY_LOGIN_URL });
                    pendingTabs[tab.id] = { provider: 'spotify', expiresAt: Date.now() + 300000 };
                    sendResponse({ ok: true, message: 'Открыта страница входа Spotify' });
                    return;
                  }
                  if (prov === 'youtube') {
                    const cookies = await readYouTubeCookies();
                    if (cookies && cookies.length > 0) {
                      const res = await postBridgeAuth({ provider: 'youtube', cookies });
                      sendResponse(res);
                      return;
                    }
                    const tab = await chrome.tabs.create({ url: YOUTUBE_LOGIN_URL });
                    pendingTabs[tab.id] = { provider: 'youtube', expiresAt: Date.now() + 300000 };
                    sendResponse({ ok: true, message: 'Открыта страница входа YouTube' });
                    return;
                  }
                  if (prov === 'vk') {
                    const cookieHeader = await readVkCookies();
                    if (cookieHeader) {
                      const res = await postBridgeAuth({ provider: 'vk', cookieHeader });
                      sendResponse(res);
                      return;
                    }
                    const tab = await chrome.tabs.create({ url: VK_LOGIN_URL });
                    pendingTabs[tab.id] = { provider: 'vk', expiresAt: Date.now() + 300000 };
                    sendResponse({ ok: true, message: 'Войдите в открывшейся вкладке ВК' });
                    return;
                  }
                  if (prov === 'yandex') {
                    await chrome.tabs.create({ url: YANDEX_OAUTH_URL });
                    sendResponse({ ok: true, message: 'Подтвердите вход в Яндекс' });
                    return;
                  }
                  if (prov === 'all') {
                    const parts = [];
                    const vkCk = await readVkCookies();
                    if (vkCk) {
                      const r0 = await postBridgeAuth({ provider: 'vk', cookieHeader: vkCk });
                      if (r0 && r0.message) parts.push(r0.message);
                    }
                    const spDc = await readSpotifySpDc();
                    if (spDc) {
                      const r1 = await postBridgeAuth({ provider: 'spotify', spDc });
                      if (r1 && r1.message) parts.push(r1.message);
                    }
                    const yt = await readYouTubeCookies();
                    if (yt && yt.length > 0) {
                      const r2 = await postBridgeAuth({ provider: 'youtube', cookies: yt });
                      if (r2 && r2.message) parts.push(r2.message);
                    }
                    sendResponse({
                      ok: parts.length > 0,
                      message: parts.length > 0 ? parts.join(' • ') : 'Войдите в ВК, Spotify или YouTube в этом браузере.'
                    });
                    return;
                  }
                }
                sendResponse({ ok: false });
              })();
              return true;
            });

            chrome.runtime.onInstalled.addListener(() => {
              findActivePort();
              try { chrome.alarms.create('omniwave-ping', { periodInMinutes: 0.5 }); } catch (e) {}
            });
            chrome.runtime.onStartup.addListener(() => {
              findActivePort();
              try { chrome.alarms.create('omniwave-ping', { periodInMinutes: 0.5 }); } catch (e) {}
            });
            chrome.alarms.onAlarm.addListener((alarm) => {
              if (alarm && alarm.name === 'omniwave-ping') findActivePort();
            });
            try { chrome.alarms.create('omniwave-ping', { periodInMinutes: 0.5 }); } catch (e) {}
            findActivePort();
            """;

            var popupHtml = """
            <!doctype html>
            <html lang="ru">
            <head>
              <meta charset="utf-8" />
              <title>OmniWave Bridge</title>
              <style>
                body {
                  margin: 0;
                  width: 270px;
                  padding: 12px;
                  background: #0A0B0E;
                  color: #F3F4F6;
                  font-family: 'Segoe UI', system-ui, sans-serif;
                }
                .card {
                  background: #14161D;
                  border: 1px solid rgba(255,255,255,0.1);
                  border-radius: 12px;
                  padding: 12px;
                  display: flex;
                  flex-direction: column;
                  gap: 8px;
                }
                .head {
                  display: flex;
                  align-items: center;
                  justify-content: space-between;
                  font-size: 13px;
                  font-weight: 600;
                }
                .status {
                  display: inline-flex;
                  align-items: center;
                  gap: 6px;
                  font-size: 11px;
                  color: #9CA3AF;
                }
                .dot {
                  width: 8px;
                  height: 8px;
                  border-radius: 50%;
                  background: #6B7280;
                }
                .dot.ok { background: #4ADE80; }
                button {
                  width: 100%;
                  border: 1px solid rgba(255,255,255,0.12);
                  background: rgba(255,255,255,0.06);
                  color: #F3F4F6;
                  border-radius: 8px;
                  padding: 7px 10px;
                  font-size: 12px;
                  cursor: pointer;
                  text-align: left;
                }
                button:hover { background: rgba(255,255,255,0.12); }
                button.primary {
                  background: rgba(74,222,128,0.16);
                  border-color: rgba(74,222,128,0.45);
                  color: #FFFFFF;
                  font-weight: 600;
                }
                .msg {
                  font-size: 11px;
                  color: #9CA3AF;
                  line-height: 1.35;
                  min-height: 15px;
                }
              </style>
            </head>
            <body>
              <div class="card">
                <div class="head">
                  <span>OmniWave Bridge</span>
                  <span class="status"><span id="dot" class="dot"></span><span id="st">Поиск...</span></span>
                </div>
                <button class="primary" data-prov="all">⚡ Подключить ВК + Spotify + YouTube</button>
                <button data-prov="yandex">Яндекс Музыка</button>
                <button data-prov="vk">VK Музыка</button>
                <button data-prov="spotify">Spotify (из сессии браузера)</button>
                <button data-prov="youtube">YouTube (передать cookies)</button>
                <div id="msg" class="msg"></div>
              </div>
              <script src="popup.js"></script>
            </body>
            </html>
            """;

            var popupJs = """
            const dot = document.getElementById('dot');
            const st = document.getElementById('st');
            const msgEl = document.getElementById('msg');

            function refreshStatus() {
              chrome.runtime.sendMessage({ action: 'getStatus' }, (res) => {
                if (res && res.ok) {
                  dot.className = 'dot ok';
                  st.textContent = 'Подключено :' + res.port;
                } else {
                  dot.className = 'dot';
                  st.textContent = 'Отключено';
                }
              });
            }

            document.querySelectorAll('button[data-prov]').forEach((btn) => {
              btn.addEventListener('click', () => {
                const prov = btn.getAttribute('data-prov');
                msgEl.textContent = 'Синхронизация...';
                chrome.runtime.sendMessage({ action: 'syncProvider', provider: prov }, (res) => {
                  msgEl.textContent = (res && res.message) ? res.message : (res && res.ok ? 'Готово!' : 'Ошибка');
                  refreshStatus();
                });
              });
            });

            refreshStatus();
            """;

            File.WriteAllText(ManifestPath, manifestJson, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(ExtensionDirectory, "background.js"), backgroundJs, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(ExtensionDirectory, "popup.html"), popupHtml, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(ExtensionDirectory, "popup.js"), popupJs, new UTF8Encoding(false));
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _cts.Dispose(); } catch { }
    }
}
