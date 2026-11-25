using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;

namespace DynamicIsland.Services;

public sealed record ProxyParsedResult(
    string Type,
    string Host,
    int? Port,
    string? Username,
    string? Password);

/// <summary>
/// Единый менеджер прокси для OmniWave:
/// - динамически управляет HttpClient.DefaultProxy без перезапуска (HTTP / HTTPS / SOCKS5 / SOCKS4),
/// - предоставляет аргументы прокси для yt-dlp и WebView2,
/// - поднимает локальный HTTP CONNECT-мост для LibVLC и WebView2 (включая аутентификацию и SOCKS5/SOCKS4),
/// - умеет парсить строки прокси любого формата и проверять соединение.
/// </summary>
public static class ProxyService
{
    public static readonly string[] SupportedTypes = { "http", "https", "socks5", "socks4" };

    private static readonly string[] RussianMusicDomains =
    {
        "yandex.ru",
        "yandex.net",
        "yandex.com",
        "ya.ru",
        "vk.com",
        "vk.ru",
        "vkuseraudio.net",
        "vk-cdn.net",
        "userapi.com",
        "mycdn.me",
        "vkontakte.ru"
    };

    private static readonly IWebProxy SystemFallbackProxy;
    private static readonly DynamicWebProxy Router = new();
    private static readonly object BridgeInitLock = new();
    private static readonly ConcurrentDictionary<string, StreamHeaderEntry> StreamHeaders = new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, RelayTarget> RelayEntries = new(StringComparer.Ordinal);
    private static readonly HttpClient RelayHttpClient;

    private static volatile ProxySnapshot _current = ProxySnapshot.Disabled;
    private static LocalProxyBridge? _bridge;

    public static event Action? ProxyChanged;

    // Внутренний переключатель для unit/integration-тестов с локальными mock-серверами.
    internal static bool BypassLoopbackInBridge { get; set; } = true;

    static ProxyService()
    {
        SystemFallbackProxy = HttpClient.DefaultProxy;
        HttpClient.DefaultProxy = Router;

        var relayHandler = new SocketsHttpHandler
        {
            UseProxy = true,
            Proxy = Router,
            AutomaticDecompression = DecompressionMethods.None,
            AllowAutoRedirect = true,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2)
        };
        RelayHttpClient = new HttpClient(relayHandler)
        {
            Timeout = TimeSpan.FromMinutes(20)
        };
    }

    public static void Apply(AppSettings settings)
    {
        var previous = _current;
        ProxySnapshot next;

        if (settings == null)
        {
            next = ProxySnapshot.Disabled;
        }
        else
        {
            string type = NormalizeType(settings.ProxyType);
            string host = (settings.ProxyHost ?? "").Trim();
            int port = NormalizePort(type, settings.ProxyPort);
            string user = (settings.ProxyUsername ?? "").Trim();
            string pass = settings.ProxyPassword ?? "";
            bool enabled = settings.ProxyEnabled && !string.IsNullOrWhiteSpace(host);

            Uri? proxyUri = enabled ? BuildUri(type, host, port) : null;
            if (!enabled || proxyUri == null)
            {
                next = ProxySnapshot.Disabled;
            }
            else
            {
                ICredentials? creds = (!string.IsNullOrEmpty(user) || !string.IsNullOrEmpty(pass))
                    ? new NetworkCredential(user, pass)
                    : null;

                next = new ProxySnapshot(
                    Enabled: true,
                    Type: type,
                    Host: host,
                    Port: port,
                    Username: user,
                    Password: pass,
                    BypassRuServices: settings.ProxyBypassRuServices,
                    ProxyUri: proxyUri,
                    Credentials: creds);

                // Запускаем локальный мост заранее, если прокси активен
                _ = EnsureBridgePort();
            }
        }

        _current = next;
        bool changed = previous.Enabled != next.Enabled ||
                       !string.Equals(previous.Type, next.Type, StringComparison.Ordinal) ||
                       !string.Equals(previous.Host, next.Host, StringComparison.OrdinalIgnoreCase) ||
                       previous.Port != next.Port ||
                       !string.Equals(previous.Username, next.Username, StringComparison.Ordinal) ||
                       !string.Equals(previous.Password, next.Password, StringComparison.Ordinal) ||
                       previous.BypassRuServices != next.BypassRuServices;

        if (changed)
        {
            try { ProxyChanged?.Invoke(); } catch { }
        }
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
        int port = EnsureBridgePort();
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
        LocalProxyBridge? bridge;
        lock (BridgeInitLock)
        {
            bridge = _bridge;
            _bridge = null;
        }
        bridge?.Dispose();
    }

    public static string NormalizeType(string? rawType)
    {
        var t = (rawType ?? "").Trim().ToLowerInvariant();
        return t switch
        {
            "https" => "https",
            "socks5" or "socks5h" or "socks" => "socks5",
            "socks4" or "socks4a" => "socks4",
            _ => "http"
        };
    }

    public static int NormalizePort(string type, int port)
    {
        if (port is >= 1 and <= 65535) return port;
        return NormalizeType(type).StartsWith("socks", StringComparison.Ordinal) ? 1080 : 8080;
    }

    public static bool IsActive => _current.Enabled && _current.ProxyUri != null;

    public static bool ShouldProxyUrl(string? url)
    {
        var snap = _current;
        if (!snap.Enabled || snap.ProxyUri == null) return false;
        if (string.IsNullOrWhiteSpace(url)) return true;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return true;
        return ShouldProxyHost(snap, uri.Host, uri.Port);
    }

    private static bool ShouldProxyHost(ProxySnapshot snap, string? host, int port = -1, bool isBridgeRequest = false)
    {
        if (!snap.Enabled || snap.ProxyUri == null) return false;
        if (string.IsNullOrWhiteSpace(host)) return false;

        if (_bridge != null && port == _bridge.Port && IsLoopbackHost(host))
            return false;

        if (!isBridgeRequest || BypassLoopbackInBridge)
        {
            if (IsLoopbackHost(host))
                return false;
        }

        if (snap.BypassRuServices && IsRussianMusicHost(host))
            return false;

        return true;
    }

    public static bool IsRussianMusicHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        var h = host.Trim().TrimEnd('.').ToLowerInvariant();
        foreach (var domain in RussianMusicDomains)
        {
            if (h == domain || h.EndsWith("." + domain, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    public static bool IsLoopbackHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        var h = host.Trim().Trim('[', ']');
        if (string.Equals(h, "localhost", StringComparison.OrdinalIgnoreCase))
            return true;
        return IPAddress.TryParse(h, out var ip) && IPAddress.IsLoopback(ip);
    }

    /// <summary>
    /// Возвращает фрагмент аргументов "--proxy \"...\" " (с хвостовым пробелом) для yt-dlp или "".
    /// </summary>
    public static string GetYtDlpProxyArgument(string? urlOrArgs)
    {
        var snap = _current;
        if (!snap.Enabled || snap.ProxyUri == null) return "";

        if (snap.BypassRuServices && !string.IsNullOrWhiteSpace(urlOrArgs))
        {
            foreach (var token in urlOrArgs.Split(new[] { ' ', '"', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (Uri.TryCreate(token, UriKind.Absolute, out var uri) &&
                    (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
                    IsRussianMusicHost(uri.Host))
                {
                    return "";
                }
            }
        }

        string scheme = snap.Type switch
        {
            "socks5" => "socks5h",
            "socks4" => "socks4a",
            "https" => "https",
            _ => "http"
        };

        string hostPart = snap.Host.Contains(':', StringComparison.Ordinal) && !snap.Host.StartsWith('[')
            ? $"[{snap.Host}]"
            : snap.Host;

        string authPart = "";
        if (!string.IsNullOrEmpty(snap.Username) || !string.IsNullOrEmpty(snap.Password))
        {
            authPart = $"{Uri.EscapeDataString(snap.Username)}:{Uri.EscapeDataString(snap.Password)}@";
        }

        string proxyArgUri = $"{scheme}://{authPart}{hostPart}:{snap.Port}";
        string escaped = proxyArgUri.Replace("\"", "\\\"", StringComparison.Ordinal);
        return $"--proxy \"{escaped}\" ";
    }

    /// <summary>
    /// Опция :http-proxy=... для LibVLC Media (работает с HTTP, HTTPS, SOCKS5 и SOCKS4 через локальный мост).
    /// </summary>
    public static string? TryGetLibVlcProxyOption(string? streamUrl)
    {
        var snap = _current;
        if (!snap.Enabled || snap.ProxyUri == null) return null;
        if (!ShouldProxyUrl(streamUrl)) return null;

        int bridgePort = EnsureBridgePort();
        if (bridgePort <= 0) return null;
        return $":http-proxy=http://127.0.0.1:{bridgePort}";
    }

    /// <summary>
    /// Аргументы командной строки Chromium для WebView2 CreationProperties.AdditionalBrowserArguments.
    /// </summary>
    public static string? GetWebView2ProxyArguments()
    {
        var snap = _current;
        if (!snap.Enabled || snap.ProxyUri == null) return null;

        int bridgePort = EnsureBridgePort();
        if (bridgePort <= 0) return null;

        var args = $"--proxy-server=\"http://127.0.0.1:{bridgePort}\"";
        if (snap.BypassRuServices)
        {
            var bypass = string.Join(";", RussianMusicDomains.Select(d => $"*.{d};{d}"));
            args += $" --proxy-bypass-list=\"<-loopback>;{bypass}\"";
        }
        return args;
    }

    public static bool TryParseProxyInput(string? raw, out ProxyParsedResult result)
    {
        result = new ProxyParsedResult("http", "", null, null, null);
        var text = (raw ?? "").Trim();
        if (string.IsNullOrWhiteSpace(text))
            return false;

        string detectedType = "http";
        bool hasExplicitScheme = false;
        string remainder = text;

        int schemeIdx = remainder.IndexOf("://", StringComparison.Ordinal);
        if (schemeIdx > 0)
        {
            string schemeRaw = remainder[..schemeIdx].Trim().ToLowerInvariant();
            detectedType = schemeRaw switch
            {
                "https" => "https",
                "socks5" or "socks5h" or "socks" => "socks5",
                "socks4" or "socks4a" => "socks4",
                "http" => "http",
                _ => "http"
            };
            hasExplicitScheme = true;
            remainder = remainder[(schemeIdx + 3)..];
        }

        string? user = null;
        string? pass = null;

        // Сначала проверяем популярный формат выгрузки host:port:user:pass (включая пароли с @ или :)
        if (!hasExplicitScheme && !remainder.StartsWith('[') && !remainder.Contains("::", StringComparison.Ordinal))
        {
            var quickParts = remainder.Split(':', 4);
            if (quickParts.Length == 4 &&
                !string.IsNullOrWhiteSpace(quickParts[0]) &&
                !quickParts[0].Contains('@', StringComparison.Ordinal) &&
                int.TryParse(quickParts[1].Trim(), out int quickPort) &&
                quickPort is >= 1 and <= 65535)
            {
                result = new ProxyParsedResult(
                    Type: "",
                    Host: quickParts[0].Trim(),
                    Port: quickPort,
                    Username: quickParts[2],
                    Password: quickParts[3]);
                return true;
            }
        }

        int atIdx = remainder.LastIndexOf('@');
        if (atIdx > 0)
        {
            string authSegment = remainder[..atIdx];
            remainder = remainder[(atIdx + 1)..];
            int colonIdx = authSegment.IndexOf(':');
            if (colonIdx >= 0)
            {
                user = authSegment[..colonIdx];
                pass = authSegment[(colonIdx + 1)..];
            }
            else
            {
                user = authSegment;
                pass = "";
            }

            if (hasExplicitScheme)
            {
                try { user = Uri.UnescapeDataString(user); } catch { }
                try { pass = Uri.UnescapeDataString(pass); } catch { }
            }
        }

        // Убираем хвост пути/query, если вставили полный URL
        int slashIdx = remainder.IndexOfAny(new[] { '/', '?', '#' });
        if (slashIdx >= 0)
            remainder = remainder[..slashIdx];

        remainder = remainder.Trim();
        if (string.IsNullOrWhiteSpace(remainder))
            return false;

        string host;
        int? port = null;

        if (remainder.StartsWith('['))
        {
            int closeBracket = remainder.IndexOf(']');
            if (closeBracket <= 1) return false;
            host = remainder[1..closeBracket].Trim();
            string afterBracket = remainder[(closeBracket + 1)..].Trim();
            if (afterBracket.StartsWith(':') &&
                int.TryParse(afterBracket[1..], out int parsedPort) &&
                parsedPort is >= 1 and <= 65535)
            {
                port = parsedPort;
            }
        }
        else
        {
            var colonParts = remainder.Split(':');
            if (colonParts.Length == 1)
            {
                host = colonParts[0].Trim();
            }
            else if (colonParts.Length == 2 &&
                     int.TryParse(colonParts[1].Trim(), out int p2) &&
                     p2 is >= 1 and <= 65535)
            {
                host = colonParts[0].Trim();
                port = p2;
            }
            else if (colonParts.Length == 4 &&
                     !remainder.Contains("::", StringComparison.Ordinal) &&
                     int.TryParse(colonParts[1].Trim(), out int p4) &&
                     p4 is >= 1 and <= 65535)
            {
                // Популярный формат выгрузки прокси-листов: host:port:user:pass
                host = colonParts[0].Trim();
                port = p4;
                user ??= colonParts[2];
                pass ??= colonParts[3];
            }
            else
            {
                // Нескобочный IPv6 или нестандартный хост
                host = remainder;
            }
        }

        if (string.IsNullOrWhiteSpace(host))
            return false;

        if (!port.HasValue && hasExplicitScheme)
        {
            port = detectedType switch
            {
                "https" => 443,
                "socks5" or "socks4" => 1080,
                _ => 8080
            };
        }

        result = new ProxyParsedResult(
            Type: hasExplicitScheme ? detectedType : "",
            Host: host,
            Port: port,
            Username: user,
            Password: pass);
        return true;
    }

    public static async Task<(bool Ok, string Message)> TestProxyAsync(
        string? rawType,
        string? rawHost,
        int port,
        string? username,
        string? password,
        CancellationToken ct = default)
    {
        string type = NormalizeType(rawType);
        string host = (rawHost ?? "").Trim();
        if (string.IsNullOrWhiteSpace(host))
            return (false, "Введите адрес прокси-сервера.");

        if (port is < 1 or > 65535)
            return (false, "Укажите корректный порт (от 1 до 65535).");

        var uri = BuildUri(type, host, port);
        if (uri == null)
            return (false, "Некорректный адрес прокси-сервера.");

        ICredentials? creds = (!string.IsNullOrWhiteSpace(username) || !string.IsNullOrEmpty(password))
            ? new NetworkCredential((username ?? "").Trim(), password ?? "")
            : null;

        try
        {
            using var handler = new SocketsHttpHandler
            {
                UseProxy = true,
                Proxy = new WebProxy(uri) { Credentials = creds },
                ConnectTimeout = TimeSpan.FromSeconds(7),
                PooledConnectionLifetime = TimeSpan.Zero
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("OmniWave/1.0");

            var sw = Stopwatch.StartNew();
            try
            {
                using var resp = await http.GetAsync("https://api.ipify.org?format=text", ct);
                sw.Stop();
                if (resp.IsSuccessStatusCode)
                {
                    var ip = (await resp.Content.ReadAsStringAsync(ct)).Trim();
                    var ipInfo = ip.Length is > 3 and <= 48 && !ip.Contains('<') ? $" • IP: {ip}" : "";
                    return (true, $"Подключено ({sw.ElapsedMilliseconds} мс){ipInfo}");
                }

                if ((int)resp.StatusCode == 407)
                    return (false, "Ошибка авторизации на прокси (407). Проверьте логин и пароль.");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException ex) when (IsAuthError(ex))
            {
                return (false, "Прокси отклонил логин/пароль (ошибка авторизации).");
            }
            catch
            {
                // Резервная проверка на Google generate_204, если ipify недоступен
                sw.Restart();
                using var resp2 = await http.GetAsync("https://www.gstatic.com/generate_204", ct);
                sw.Stop();
                if (resp2.IsSuccessStatusCode)
                    return (true, $"Подключено ({sw.ElapsedMilliseconds} мс)");
                if ((int)resp2.StatusCode == 407)
                    return (false, "Ошибка авторизации на прокси (407). Проверьте логин и пароль.");
                return (false, $"Прокси ответил кодом HTTP {(int)resp2.StatusCode}.");
            }

            return (false, "Не удалось получить успешный ответ через прокси.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, "Таймаут подключения к прокси-серверу.");
        }
        catch (OperationCanceledException)
        {
            return (false, "Проверка отменена.");
        }
        catch (HttpRequestException ex)
        {
            if (IsAuthError(ex))
                return (false, "Ошибка авторизации прокси. Проверьте логин и пароль.");
            return (false, $"Нет связи с прокси: {ShortenError(ex.InnerException?.Message ?? ex.Message)}");
        }
        catch (Exception ex)
        {
            return (false, $"Ошибка проверки: {ShortenError(ex.Message)}");
        }
    }

    internal static int EnsureBridgePort()
    {
        lock (BridgeInitLock)
        {
            if (_bridge != null && _bridge.IsListening)
                return _bridge.Port;

            _bridge?.Dispose();
            _bridge = new LocalProxyBridge();
            return _bridge.Port;
        }
    }

    private static Uri? BuildUri(string type, string host, int port)
    {
        string scheme = NormalizeType(type);
        string formattedHost = host.Contains(':', StringComparison.Ordinal) && !host.StartsWith('[')
            ? $"[{host}]"
            : host;

        return Uri.TryCreate($"{scheme}://{formattedHost}:{port}", UriKind.Absolute, out var uri)
            ? uri
            : null;
    }

    private static bool IsAuthError(Exception ex)
    {
        var msg = (ex.InnerException?.Message ?? ex.Message ?? "").ToLowerInvariant();
        return msg.Contains("407") ||
               msg.Contains("auth") ||
               msg.Contains("username") ||
               msg.Contains("password") ||
               msg.Contains("credential");
    }

    private static string ShortenError(string? msg)
    {
        var s = (msg ?? "неизвестная ошибка").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return s.Length > 90 ? s[..87] + "..." : s;
    }

    private sealed record StreamHeaderEntry(string? UserAgent, string? Referer, DateTime ExpiresUtc);

    private sealed record RelayTarget(string TargetUrl, string? UserAgent, string? Referer, DateTime ExpiresUtc);

    private sealed record ProxySnapshot(
        bool Enabled,
        string Type,
        string Host,
        int Port,
        string Username,
        string Password,
        bool BypassRuServices,
        Uri? ProxyUri,
        ICredentials? Credentials)
    {
        public static readonly ProxySnapshot Disabled = new(
            Enabled: false,
            Type: "http",
            Host: "",
            Port: 8080,
            Username: "",
            Password: "",
            BypassRuServices: true,
            ProxyUri: null,
            Credentials: null);
    }

    private sealed class DynamicWebProxy : IWebProxy
    {
        public ICredentials? Credentials
        {
            get
            {
                var snap = _current;
                return snap.Enabled && snap.ProxyUri != null
                    ? snap.Credentials
                    : SystemFallbackProxy.Credentials;
            }
            set { }
        }

        public Uri? GetProxy(Uri destination)
        {
            var snap = _current;
            if (!snap.Enabled || snap.ProxyUri == null)
                return SystemFallbackProxy.GetProxy(destination);

            if (!ShouldProxyHost(snap, destination.Host, destination.Port))
                return destination;

            return snap.ProxyUri;
        }

        public bool IsBypassed(Uri host)
        {
            var snap = _current;
            if (!snap.Enabled || snap.ProxyUri == null)
                return SystemFallbackProxy.IsBypassed(host);

            return !ShouldProxyHost(snap, host.Host, host.Port);
        }
    }

    /// <summary>
    /// Лёгкий локальный HTTP-прокси (127.0.0.1:randomPort), который прозрачно транслирует
    /// HTTP CONNECT и HTTP-запросы в активный HTTP/HTTPS/SOCKS5/SOCKS4 апстрим (или напрямую).
    /// Используется плеером LibVLC и встроенным браузером WebView2.
    /// </summary>
    private sealed class LocalProxyBridge : IDisposable
    {
        private readonly TcpListener _listener;
        private readonly CancellationTokenSource _cts = new();

        public int Port { get; }
        public bool IsListening { get; private set; }

        public LocalProxyBridge()
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
                    var (headerBytes, overflow) = await ReadHttpHeaderBlockAsync(clientStream, ct);
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
                    string version = reqParts[2];

                    var snap = _current;

                    if (method == "CONNECT")
                    {
                        if (!TryParseHostPort(target, 443, out string targetHost, out int targetPort))
                        {
                            await WriteSimpleErrorAsync(clientStream, "400 Bad Request", ct);
                            return;
                        }

                        await using var tunnel = await OpenTargetTunnelAsync(targetHost, targetPort, snap, ct);
                        var okBytes = Encoding.ASCII.GetBytes($"{version} 200 Connection Established\r\n\r\n");
                        await clientStream.WriteAsync(okBytes, ct);
                        await clientStream.FlushAsync(ct);

                        await RelayBidirectionalAsync(clientStream, overflow, tunnel.Stream, ct);
                        return;
                    }

                    const string relayPrefix = "/__omniwave_relay/";
                    string? relayId = null;
                    if (target.StartsWith(relayPrefix, StringComparison.Ordinal))
                        relayId = target[relayPrefix.Length..];
                    else if (Uri.TryCreate(target, UriKind.Absolute, out var absRelayUri) &&
                             absRelayUri.AbsolutePath.StartsWith(relayPrefix, StringComparison.Ordinal))
                        relayId = absRelayUri.AbsolutePath[relayPrefix.Length..];

                    if (!string.IsNullOrWhiteSpace(relayId) && (method == "GET" || method == "HEAD"))
                    {
                        await HandleMediaRelayRequestAsync(clientStream, method, relayId, lines, ct);
                        return;
                    }

                    if (!Uri.TryCreate(target, UriKind.Absolute, out var reqUri) || string.IsNullOrWhiteSpace(reqUri.Host))
                    {
                        await WriteSimpleErrorAsync(clientStream, "400 Bad Request", ct);
                        return;
                    }

                    bool shouldProxy = ShouldProxyHost(snap, reqUri.Host, reqUri.Port, isBridgeRequest: true);
                    if (shouldProxy && snap.Type is "http" or "https")
                    {
                        // Для обычного HTTP через HTTP/HTTPS-прокси пересылаем абсолютный URL напрямую
                        // (некоторые HTTP-прокси запрещают CONNECT на порты, отличные от 443).
                        await using var proxyTunnel = await OpenUpstreamProxyChannelAsync(snap, ct);
                        var sbProxy = new StringBuilder();
                        sbProxy.Append(reqParts[0]).Append(' ').Append(target).Append(' ').Append(version).Append("\r\n");
                        bool hasProxyHost = false;
                        for (int i = 1; i < lines.Length; i++)
                        {
                            string line = lines[i];
                            if (string.IsNullOrEmpty(line)) continue;
                            if (line.StartsWith("Proxy-Connection:", StringComparison.OrdinalIgnoreCase) ||
                                line.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }
                            if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                                hasProxyHost = true;
                            sbProxy.Append(line).Append("\r\n");
                        }
                        if (!hasProxyHost)
                            sbProxy.Append("Host: ").Append(reqUri.Authority).Append("\r\n");
                        if (!string.IsNullOrEmpty(snap.Username) || !string.IsNullOrEmpty(snap.Password))
                        {
                            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{snap.Username}:{snap.Password}"));
                            sbProxy.Append("Proxy-Authorization: Basic ").Append(token).Append("\r\n");
                        }
                        sbProxy.Append("\r\n");

                        await proxyTunnel.Stream.WriteAsync(Encoding.Latin1.GetBytes(sbProxy.ToString()), ct);
                        await proxyTunnel.Stream.FlushAsync(ct);
                        await RelayBidirectionalAsync(clientStream, overflow, proxyTunnel.Stream, ct);
                        return;
                    }

                    await using var httpTunnel = await OpenTargetTunnelAsync(reqUri.Host, reqUri.Port, snap, ct);

                    var sb = new StringBuilder();
                    sb.Append(reqParts[0]).Append(' ').Append(reqUri.PathAndQuery).Append(' ').Append(version).Append("\r\n");
                    bool hasHostHeader = false;
                    for (int i = 1; i < lines.Length; i++)
                    {
                        string line = lines[i];
                        if (string.IsNullOrEmpty(line)) continue;
                        if (line.StartsWith("Proxy-Connection:", StringComparison.OrdinalIgnoreCase) ||
                            line.StartsWith("Proxy-Authorization:", StringComparison.OrdinalIgnoreCase))
                        {
                            continue;
                        }
                        if (line.StartsWith("Host:", StringComparison.OrdinalIgnoreCase))
                            hasHostHeader = true;
                        sb.Append(line).Append("\r\n");
                    }
                    if (!hasHostHeader)
                        sb.Append("Host: ").Append(reqUri.Authority).Append("\r\n");
                    sb.Append("\r\n");

                    var forwardedHeaders = Encoding.Latin1.GetBytes(sb.ToString());
                    await httpTunnel.Stream.WriteAsync(forwardedHeaders, ct);
                    await httpTunnel.Stream.FlushAsync(ct);

                    await RelayBidirectionalAsync(clientStream, overflow, httpTunnel.Stream, ct);
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

        private sealed class TunnelHandle : IAsyncDisposable
        {
            public TcpClient Client { get; }
            public Stream Stream { get; }

            public TunnelHandle(TcpClient client, Stream stream)
            {
                Client = client;
                Stream = stream;
            }

            public async ValueTask DisposeAsync()
            {
                try { await Stream.DisposeAsync(); } catch { }
                try { Client.Dispose(); } catch { }
            }
        }

        private static async Task<TunnelHandle> OpenUpstreamProxyChannelAsync(ProxySnapshot snap, CancellationToken ct)
        {
            var proxyTcp = new TcpClient { NoDelay = true };
            try
            {
                await proxyTcp.ConnectAsync(snap.Host, snap.Port, ct);
                Stream proxyStream = proxyTcp.GetStream();

                if (snap.Type == "https")
                {
                    var ssl = new SslStream(proxyStream, leaveInnerStreamOpen: false);
                    await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                    {
                        TargetHost = snap.Host,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                    }, ct);
                    proxyStream = ssl;
                }

                return new TunnelHandle(proxyTcp, proxyStream);
            }
            catch
            {
                proxyTcp.Dispose();
                throw;
            }
        }

        private static async Task<TunnelHandle> OpenTargetTunnelAsync(
            string targetHost,
            int targetPort,
            ProxySnapshot snap,
            CancellationToken ct)
        {
            if (!ShouldProxyHost(snap, targetHost, targetPort, isBridgeRequest: true))
            {
                var direct = new TcpClient { NoDelay = true };
                try
                {
                    await direct.ConnectAsync(targetHost, targetPort, ct);
                    return new TunnelHandle(direct, direct.GetStream());
                }
                catch
                {
                    direct.Dispose();
                    throw;
                }
            }

            var handle = await OpenUpstreamProxyChannelAsync(snap, ct);
            try
            {
                var proxyStream = handle.Stream;

                if (snap.Type is "http" or "https")
                {
                    string authority = targetHost.Contains(':', StringComparison.Ordinal) && !targetHost.StartsWith('[')
                        ? $"[{targetHost}]:{targetPort}"
                        : $"{targetHost}:{targetPort}";

                    var req = new StringBuilder();
                    req.Append($"CONNECT {authority} HTTP/1.1\r\nHost: {authority}\r\n");
                    if (!string.IsNullOrEmpty(snap.Username) || !string.IsNullOrEmpty(snap.Password))
                    {
                        var token = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{snap.Username}:{snap.Password}"));
                        req.Append($"Proxy-Authorization: Basic {token}\r\n");
                    }
                    req.Append("\r\n");

                    await proxyStream.WriteAsync(Encoding.ASCII.GetBytes(req.ToString()), ct);
                    await proxyStream.FlushAsync(ct);

                    var statusLine = await ReadHttpHeaderStatusLineExactAsync(proxyStream, ct);
                    if (!statusLine.Contains(" 200", StringComparison.Ordinal))
                        throw new IOException($"Upstream CONNECT failed: {statusLine}");

                    return handle;
                }

                if (snap.Type == "socks5")
                {
                    await PerformSocks5ConnectAsync(proxyStream, targetHost, targetPort, snap.Username, snap.Password, ct);
                    return handle;
                }

                if (snap.Type == "socks4")
                {
                    await PerformSocks4aConnectAsync(proxyStream, targetHost, targetPort, snap.Username, ct);
                    return handle;
                }

                return handle;
            }
            catch
            {
                await handle.DisposeAsync();
                throw;
            }
        }

        private static async Task PerformSocks5ConnectAsync(
            Stream stream,
            string targetHost,
            int targetPort,
            string username,
            string password,
            CancellationToken ct)
        {
            bool hasAuth = !string.IsNullOrEmpty(username) || !string.IsNullOrEmpty(password);
            byte[] greeting = hasAuth
                ? new byte[] { 0x05, 0x02, 0x00, 0x02 }
                : new byte[] { 0x05, 0x01, 0x00 };
            await stream.WriteAsync(greeting, ct);
            await stream.FlushAsync(ct);

            var methodReply = new byte[2];
            await ReadExactAsync(stream, methodReply, ct);
            if (methodReply[0] != 0x05 || methodReply[1] == 0xFF)
                throw new IOException("SOCKS5 handshake rejected.");

            if (methodReply[1] == 0x02)
            {
                var uBytes = Encoding.UTF8.GetBytes(username);
                var pBytes = Encoding.UTF8.GetBytes(password);
                if (uBytes.Length > 255 || pBytes.Length > 255)
                    throw new IOException("SOCKS5 credentials too long.");

                var authBuf = new byte[3 + uBytes.Length + pBytes.Length];
                authBuf[0] = 0x01;
                authBuf[1] = (byte)uBytes.Length;
                Buffer.BlockCopy(uBytes, 0, authBuf, 2, uBytes.Length);
                authBuf[2 + uBytes.Length] = (byte)pBytes.Length;
                Buffer.BlockCopy(pBytes, 0, authBuf, 3 + uBytes.Length, pBytes.Length);

                await stream.WriteAsync(authBuf, ct);
                await stream.FlushAsync(ct);

                var authReply = new byte[2];
                await ReadExactAsync(stream, authReply, ct);
                if (authReply[1] != 0x00)
                    throw new IOException("SOCKS5 authentication failed.");
            }
            else if (methodReply[1] != 0x00)
            {
                throw new IOException($"Unsupported SOCKS5 auth method: {methodReply[1]}");
            }

            byte[] req;
            if (IPAddress.TryParse(targetHost, out var ip))
            {
                var addrBytes = ip.GetAddressBytes();
                byte atyp = ip.AddressFamily == AddressFamily.InterNetwork ? (byte)0x01 : (byte)0x04;
                req = new byte[4 + addrBytes.Length + 2];
                req[0] = 0x05;
                req[1] = 0x01;
                req[2] = 0x00;
                req[3] = atyp;
                Buffer.BlockCopy(addrBytes, 0, req, 4, addrBytes.Length);
                req[4 + addrBytes.Length] = (byte)(targetPort >> 8);
                req[5 + addrBytes.Length] = (byte)(targetPort & 0xFF);
            }
            else
            {
                var hostBytes = Encoding.ASCII.GetBytes(targetHost);
                if (hostBytes.Length > 255)
                    throw new IOException("Target host too long for SOCKS5.");

                req = new byte[4 + 1 + hostBytes.Length + 2];
                req[0] = 0x05;
                req[1] = 0x01;
                req[2] = 0x00;
                req[3] = 0x03;
                req[4] = (byte)hostBytes.Length;
                Buffer.BlockCopy(hostBytes, 0, req, 5, hostBytes.Length);
                req[5 + hostBytes.Length] = (byte)(targetPort >> 8);
                req[6 + hostBytes.Length] = (byte)(targetPort & 0xFF);
            }

            await stream.WriteAsync(req, ct);
            await stream.FlushAsync(ct);

            var replyHead = new byte[4];
            await ReadExactAsync(stream, replyHead, ct);
            if (replyHead[0] != 0x05 || replyHead[1] != 0x00)
                throw new IOException($"SOCKS5 CONNECT failed with code {replyHead[1]}");

            int tailLen = replyHead[3] switch
            {
                0x01 => 4 + 2,
                0x04 => 16 + 2,
                0x03 => await ReadByteExactAsync(stream, ct) + 2,
                _ => throw new IOException($"Invalid SOCKS5 reply ATYP {replyHead[3]}")
            };
            if (tailLen > 0)
            {
                var discard = new byte[tailLen];
                await ReadExactAsync(stream, discard, ct);
            }
        }

        private static async Task PerformSocks4aConnectAsync(
            Stream stream,
            string targetHost,
            int targetPort,
            string username,
            CancellationToken ct)
        {
            var userBytes = Encoding.UTF8.GetBytes(username);
            byte[] ipBytes;
            byte[]? domainBytes = null;

            if (IPAddress.TryParse(targetHost, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork)
            {
                ipBytes = ip.GetAddressBytes();
            }
            else
            {
                ipBytes = new byte[] { 0x00, 0x00, 0x00, 0x01 };
                domainBytes = Encoding.ASCII.GetBytes(targetHost);
            }

            int totalLen = 8 + userBytes.Length + 1 + (domainBytes != null ? domainBytes.Length + 1 : 0);
            var req = new byte[totalLen];
            req[0] = 0x04;
            req[1] = 0x01;
            req[2] = (byte)(targetPort >> 8);
            req[3] = (byte)(targetPort & 0xFF);
            Buffer.BlockCopy(ipBytes, 0, req, 4, 4);
            Buffer.BlockCopy(userBytes, 0, req, 8, userBytes.Length);
            req[8 + userBytes.Length] = 0x00;
            if (domainBytes != null)
            {
                int domStart = 9 + userBytes.Length;
                Buffer.BlockCopy(domainBytes, 0, req, domStart, domainBytes.Length);
                req[domStart + domainBytes.Length] = 0x00;
            }

            await stream.WriteAsync(req, ct);
            await stream.FlushAsync(ct);

            var reply = new byte[8];
            await ReadExactAsync(stream, reply, ct);
            if (reply[1] != 0x5A)
                throw new IOException($"SOCKS4 CONNECT failed with code {reply[1]}");
        }

        private static async Task<(byte[] HeaderBytes, byte[] Overflow)> ReadHttpHeaderBlockAsync(
            Stream stream,
            CancellationToken ct)
        {
            var buffer = new byte[8192];
            using var ms = new MemoryStream();
            while (ms.Length < 65536)
            {
                int read = await stream.ReadAsync(buffer, 0, buffer.Length, ct);
                if (read <= 0) break;
                ms.Write(buffer, 0, read);

                if (TrySplitHttpHeaderBlock(ms.GetBuffer(), (int)ms.Length, out var split))
                    return split;
            }
            return (Array.Empty<byte>(), Array.Empty<byte>());
        }

        private static bool TrySplitHttpHeaderBlock(
            byte[] buf,
            int totalLen,
            out (byte[] HeaderBytes, byte[] Overflow) result)
        {
            var span = buf.AsSpan(0, totalLen);
            int idx = span.IndexOf("\r\n\r\n"u8);
            if (idx >= 0)
            {
                int endHeader = idx + 4;
                result = (span[..endHeader].ToArray(), span[endHeader..].ToArray());
                return true;
            }
            result = default;
            return false;
        }

        private static async Task<string> ReadHttpHeaderStatusLineExactAsync(Stream stream, CancellationToken ct)
        {
            // Читаем по байту, чтобы не проглотить начало TLS ServerHello, если оно придёт в том же TCP-сегменте
            var oneByte = new byte[1];
            var bytes = new List<byte>(256);
            int state = 0;
            while (bytes.Count < 16384)
            {
                int read = await stream.ReadAsync(oneByte, 0, 1, ct);
                if (read <= 0) break;
                byte b = oneByte[0];
                bytes.Add(b);
                state = (state, b) switch
                {
                    (0, (byte)'\r') => 1,
                    (1, (byte)'\n') => 2,
                    (2, (byte)'\r') => 3,
                    (3, (byte)'\n') => 4,
                    (_, (byte)'\r') => 1,
                    _ => 0
                };
                if (state == 4) break;
            }

            var text = Encoding.Latin1.GetString(bytes.ToArray());
            int crlf = text.IndexOf("\r\n", StringComparison.Ordinal);
            return crlf >= 0 ? text[..crlf] : text;
        }

        private static async Task RelayBidirectionalAsync(
            Stream client,
            byte[] initialClientOverflow,
            Stream upstream,
            CancellationToken ct)
        {
            if (initialClientOverflow.Length > 0)
            {
                await upstream.WriteAsync(initialClientOverflow, ct);
                await upstream.FlushAsync(ct);
            }

            var c2u = client.CopyToAsync(upstream, 32768, ct);
            var u2c = upstream.CopyToAsync(client, 32768, ct);
            await Task.WhenAny(c2u, u2c);
        }

        private static async Task ReadExactAsync(Stream stream, byte[] buffer, CancellationToken ct)
        {
            int offset = 0;
            while (offset < buffer.Length)
            {
                int read = await stream.ReadAsync(buffer, offset, buffer.Length - offset, ct);
                if (read <= 0)
                    throw new EndOfStreamException("Unexpected EOF during proxy handshake.");
                offset += read;
            }
        }

        private static async Task<int> ReadByteExactAsync(Stream stream, CancellationToken ct)
        {
            var buf = new byte[1];
            await ReadExactAsync(stream, buf, ct);
            return buf[0];
        }

        private static bool TryParseHostPort(string target, int defaultPort, out string host, out int port)
        {
            host = "";
            port = defaultPort;
            if (string.IsNullOrWhiteSpace(target)) return false;

            target = target.Trim();
            if (target.StartsWith('['))
            {
                int close = target.IndexOf(']');
                if (close <= 1) return false;
                host = target[1..close];
                string tail = target[(close + 1)..];
                if (tail.StartsWith(':') && int.TryParse(tail[1..], out int p) && p is >= 1 and <= 65535)
                    port = p;
                return !string.IsNullOrWhiteSpace(host);
            }

            int lastColon = target.LastIndexOf(':');
            if (lastColon > 0 && int.TryParse(target[(lastColon + 1)..], out int parsed) && parsed is >= 1 and <= 65535)
            {
                host = target[..lastColon];
                port = parsed;
            }
            else
            {
                host = target;
            }
            return !string.IsNullOrWhiteSpace(host);
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
