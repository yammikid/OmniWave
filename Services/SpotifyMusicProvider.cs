using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DynamicIsland.Services;

/// <summary>
/// Интеграция Spotify Web API + Web Player (через OAuth PKCE в системном браузере и/или сессию WebView2 с sp_dc):
/// - авторизация через системный браузер по OAuth2 PKCE (127.0.0.1 loopback) или ручную вставку sp_dc / Bearer,
/// - загрузка «Мне нравится» и всех плейлистов пользователя с обложками,
/// - поиск треков и артистов, разбор публичных ссылок open.spotify.com (в том числе без входа),
/// - воспроизведение через верифицированный кросс-площадочный подбор (TrackMatcher ±3 с).
/// </summary>
public sealed class SpotifyMusicProvider : IMusicProvider, IDisposable
{
    public const string BrandColorHex = "#1DB954";
    public const string OpenSpotifyUrl = "https://open.spotify.com/";
    public const string LoginUrl = "https://accounts.spotify.com/login?continue=https%3A%2F%2Fopen.spotify.com%2F";
    public const string DesktopPkceClientId = "65b708073fc0480ea92a077233ca87bd";

    private const string PkceTokenEndpoint = "https://accounts.spotify.com/api/token";
    private const string PkceScopes =
        "user-read-private user-read-email user-library-read user-library-modify " +
        "playlist-read-private playlist-read-collaborative playlist-modify-public playlist-modify-private";
    private const string ApiBase = "https://api.spotify.com/v1";

    private static readonly Regex SpotifyUrlRegex = new(
        @"https?://open\.spotify\.com/(?:intl-[a-z-]+/)?(track|playlist|album)/([a-zA-Z0-9]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SpDcCookieRegex = new(
        @"(?:^|[\s;])sp_dc=([^;\s""']+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex NextDataRegex = new(
        @"<script\s+id=""__NEXT_DATA__""[^>]*>([\s\S]*?)</script>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly SettingsService _settings;
    private readonly HttpClient _http;
    private readonly SpotifyWebSessionBridge _bridge = new(isGuestProfile: false);
    private readonly SpotifyWebSessionBridge _guestBridge = new(isGuestProfile: true);
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private List<IMusicProvider> _fallbackProviders;
    private string? _guestAccessToken;
    private long _guestTokenExpiresMs;
    private string? _guestClientToken;
    private DateTime _apiRateLimitedUntilUtc = DateTime.MinValue;

    public string Name => "Spotify";
    public string BrandKey => "spotify";
    public string? LastResolveError { get; private set; }
    public TrackItem? LastMatchedCandidate { get; private set; }

    public bool IsAuthenticated =>
        !string.IsNullOrWhiteSpace(_settings.Current.SpotifyAccessToken) ||
        !string.IsNullOrWhiteSpace(_settings.Current.SpotifyRefreshToken) ||
        !string.IsNullOrWhiteSpace(_settings.Current.SpotifySpDcCookie);

    public SpotifyMusicProvider(SettingsService settings, IEnumerable<IMusicProvider>? fallbackProviders = null)
    {
        _settings = settings;
        _fallbackProviders = fallbackProviders?
            .Where(p => !string.Equals(p.BrandKey, BrandKey, StringComparison.OrdinalIgnoreCase))
            .ToList() ?? new List<IMusicProvider>();

        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "ru-RU,ru;q=0.9,en-US;q=0.8");
    }

    public void SetFallbackProviders(IEnumerable<IMusicProvider> fallbackProviders)
    {
        _fallbackProviders = fallbackProviders
            .Where(p => !string.Equals(p.BrandKey, BrandKey, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public static bool IsSpotifyUrl(string? url)
        => !string.IsNullOrWhiteSpace(url) && SpotifyUrlRegex.IsMatch(url);

    public static (string? BearerToken, string? SpDc) ExtractSessionInputs(string? rawInput)
    {
        var trimmed = (rawInput ?? "").Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return (null, null);

        var spDcMatch = SpDcCookieRegex.Match(trimmed);
        if (spDcMatch.Success)
            return (null, spDcMatch.Groups[1].Value.Trim());

        if (trimmed.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[7..].Trim();

        // Обычная cookie sp_dc начинается с AQ... и длиннее ~80 символов
        if (trimmed.StartsWith("AQ", StringComparison.Ordinal) && trimmed.Length >= 80 && !trimmed.Contains(' '))
            return (null, trimmed);

        if (trimmed.Length >= 25 && !trimmed.Contains(' '))
            return (trimmed, null);

        return (null, null);
    }

    private string? GetActiveClientToken()
        => !string.IsNullOrWhiteSpace(_bridge.LastClientToken)
            ? _bridge.LastClientToken
            : _settings.Current.SpotifyClientToken;

    private static void ApplyWebApiHeaders(HttpRequestMessage req, string token, string? clientToken)
    {
        req.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        if (!string.IsNullOrWhiteSpace(clientToken))
            req.Headers.TryAddWithoutValidation("client-token", clientToken);
        req.Headers.TryAddWithoutValidation("app-platform", "WebPlayer");
        req.Headers.TryAddWithoutValidation("Origin", "https://open.spotify.com");
        req.Headers.TryAddWithoutValidation("Referer", "https://open.spotify.com/");
    }

    public async Task<(bool Ok, string Message)> VerifyAndSaveSessionAsync(
        string? rawTokenOrInput,
        string? explicitSpDc,
        long expiresUnixMs,
        string? hintUserName,
        string? hintUserId = null,
        string? clientToken = null,
        string? refreshToken = null,
        bool allowSaveOnRateLimit = false,
        CancellationToken ct = default)
    {
        var (parsedToken, parsedSpDc) = ExtractSessionInputs(rawTokenOrInput);
        string? token = parsedToken;
        string? spDc = !string.IsNullOrWhiteSpace(explicitSpDc) ? explicitSpDc.Trim() : parsedSpDc;
        string? cleanRefresh = string.IsNullOrWhiteSpace(refreshToken) ? null : refreshToken.Trim();
        string? activeClientToken = !string.IsNullOrWhiteSpace(clientToken) ? clientToken.Trim() : _bridge.LastClientToken;
        long expMs = expiresUnixMs > 0
            ? expiresUnixMs
            : DateTimeOffset.UtcNow.AddMinutes(55).ToUnixTimeMilliseconds();

        if (string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(spDc))
        {
            var refreshed = await _bridge.AcquireTokenAsync(spDc, allowAnonymous: false, ct);
            if (!refreshed.IsAnonymous && !string.IsNullOrWhiteSpace(refreshed.AccessToken))
                token = refreshed.AccessToken;
            if (refreshed.ExpiresUnixMs > 0) expMs = refreshed.ExpiresUnixMs;
            if (!string.IsNullOrWhiteSpace(refreshed.ClientToken)) activeClientToken ??= refreshed.ClientToken;
            if (!string.IsNullOrWhiteSpace(refreshed.SpDcCookie)) spDc = refreshed.SpDcCookie;
        }

        HttpStatusCode status = HttpStatusCode.Unauthorized;
        string? uid = string.IsNullOrWhiteSpace(hintUserId) ? null : hintUserId.Trim();
        string? name = string.IsNullOrWhiteSpace(hintUserName) ? null : hintUserName.Trim();
        string? err = null;

        if (!string.IsNullOrWhiteSpace(token))
        {
            (status, var apiUid, var apiName, err) = await FetchCurrentUserAsync(token, activeClientToken, ct);
            if (!string.IsNullOrWhiteSpace(apiUid)) uid = apiUid;
            if (!string.IsNullOrWhiteSpace(apiName)) name = apiName;

            if (status == HttpStatusCode.Unauthorized && !string.IsNullOrWhiteSpace(spDc))
            {
                var refreshed = await _bridge.AcquireTokenAsync(spDc, allowAnonymous: false, ct);
                if (!refreshed.IsAnonymous && !string.IsNullOrWhiteSpace(refreshed.AccessToken))
                {
                    token = refreshed.AccessToken;
                    expMs = refreshed.ExpiresUnixMs > 0 ? refreshed.ExpiresUnixMs : expMs;
                    if (!string.IsNullOrWhiteSpace(refreshed.ClientToken)) activeClientToken ??= refreshed.ClientToken;
                    spDc = refreshed.SpDcCookie ?? spDc;

                    (status, apiUid, apiName, err) = await FetchCurrentUserAsync(token, activeClientToken, ct);
                    if (!string.IsNullOrWhiteSpace(apiUid)) uid = apiUid;
                    if (!string.IsNullOrWhiteSpace(apiName)) name = apiName;
                }
            }
        }

        if (status == HttpStatusCode.TooManyRequests &&
            allowSaveOnRateLimit &&
            !string.IsNullOrWhiteSpace(token))
        {
            _apiRateLimitedUntilUtc = DateTime.UtcNow.AddMinutes(2);
            status = HttpStatusCode.OK;
        }
        else if (status != HttpStatusCode.OK &&
                 status != HttpStatusCode.Unauthorized &&
                 !string.IsNullOrWhiteSpace(spDc))
        {
            if (status == HttpStatusCode.TooManyRequests)
                _apiRateLimitedUntilUtc = DateTime.UtcNow.AddMinutes(3);

            // Если Web API ограничил запросы (429 / 403 / 503), но сессия браузера с sp_dc живая — принимаем вход
            bool verifiedViaBrowserHint = !string.IsNullOrWhiteSpace(token) && !string.IsNullOrWhiteSpace(name);
            if (!verifiedViaBrowserHint)
            {
                var prof = await _bridge.FetchAuthenticatedProfileAsync(spDc, ct);
                if (prof.IsLoggedIn)
                {
                    if (!string.IsNullOrWhiteSpace(prof.AccessToken)) token = prof.AccessToken;
                    if (prof.ExpiresMs > 0) expMs = prof.ExpiresMs;
                    if (!string.IsNullOrWhiteSpace(prof.ClientToken)) activeClientToken ??= prof.ClientToken;
                    if (!string.IsNullOrWhiteSpace(prof.SpDc)) spDc = prof.SpDc;
                    if (!string.IsNullOrWhiteSpace(prof.UserId)) uid ??= prof.UserId;
                    if (!string.IsNullOrWhiteSpace(prof.DisplayName)) name ??= prof.DisplayName;
                    verifiedViaBrowserHint = true;
                }
            }

            if (verifiedViaBrowserHint)
                status = HttpStatusCode.OK;
        }

        if (string.IsNullOrWhiteSpace(token) && status != HttpStatusCode.OK)
        {
            return (false, "Не удалось получить токен Spotify. Войдите через браузер или проверьте sp_dc / прокси.");
        }

        if (status != HttpStatusCode.OK)
        {
            return status switch
            {
                HttpStatusCode.Unauthorized => (false, "Токен Spotify недействителен или истёк."),
                HttpStatusCode.TooManyRequests => (false, "Spotify ограничил запросы Web API (429). Войдите через Мост браузера или вставьте cookie sp_dc."),
                HttpStatusCode.Forbidden => (false, "Spotify отклонил запрос (403). Если сервис недоступен в регионе, включите прокси в Настройках."),
                _ => (false, $"Не удалось подключить Spotify: {err ?? status.ToString()}")
            };
        }

        string finalName = !string.IsNullOrWhiteSpace(name)
            ? name!
            : (!string.IsNullOrWhiteSpace(uid) ? uid! : "Spotify User");

        _settings.Update(s =>
        {
            s.SpotifyAccessToken = token;
            if (!string.IsNullOrWhiteSpace(cleanRefresh))
            {
                s.SpotifyRefreshToken = cleanRefresh;
                s.SpotifySpDcCookie = null;
                s.SpotifyClientToken = null;
            }
            else if (!string.IsNullOrWhiteSpace(spDc))
            {
                s.SpotifySpDcCookie = spDc;
                s.SpotifyClientToken = activeClientToken;
                s.SpotifyRefreshToken = null;
            }
            else
            {
                s.SpotifyClientToken = activeClientToken;
                s.SpotifySpDcCookie = null;
                s.SpotifyRefreshToken = null;
            }
            s.SpotifyTokenExpiresUnixMs = expMs;
            s.SpotifyUserId = uid ?? "";
            s.SpotifyUserName = finalName;
        });

        return (true, $"Spotify подключён ({finalName})");
    }

    public void Disconnect()
    {
        _apiRateLimitedUntilUtc = DateTime.MinValue;
        _settings.Update(s =>
        {
            s.SpotifyAccessToken = null;
            s.SpotifyClientToken = null;
            s.SpotifySpDcCookie = null;
            s.SpotifyRefreshToken = null;
            s.SpotifyTokenExpiresUnixMs = 0;
            s.SpotifyUserId = null;
            s.SpotifyUserName = null;
        });
        _bridge.ResetSession();
    }

    private async Task<(HttpStatusCode Status, string? UserId, string? DisplayName, string? Error)> FetchCurrentUserAsync(
        string token,
        string? clientToken,
        CancellationToken ct)
    {
        try
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/me");
                ApplyWebApiHeaders(req, token, clientToken);
                using var resp = await _http.SendAsync(req, ct);

                if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    if (attempt == 0 && resp.Headers.RetryAfter?.Delta is TimeSpan d && d.TotalSeconds is >= 1 and <= 4)
                    {
                        await Task.Delay(d, ct);
                        continue;
                    }
                    return (HttpStatusCode.TooManyRequests, null, null, "HTTP 429");
                }

                if (!resp.IsSuccessStatusCode)
                    return (resp.StatusCode, null, null, $"HTTP {(int)resp.StatusCode}");

                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                string? id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                string? display = root.TryGetProperty("display_name", out var dnEl) ? dnEl.GetString() : null;
                return (HttpStatusCode.OK, id, display ?? id, null);
            }

            return (HttpStatusCode.TooManyRequests, null, null, "HTTP 429");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (HttpStatusCode.ServiceUnavailable, null, null, ex.Message);
        }
    }

    private async Task<string?> EnsureValidTokenAsync(bool forceRefresh, CancellationToken ct)
    {
        var snap = _settings.Current;
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (!forceRefresh &&
            !string.IsNullOrWhiteSpace(snap.SpotifyAccessToken) &&
            (snap.SpotifyTokenExpiresUnixMs <= 0 || snap.SpotifyTokenExpiresUnixMs > nowMs + 120_000))
        {
            return snap.SpotifyAccessToken;
        }

        if (string.IsNullOrWhiteSpace(snap.SpotifyRefreshToken) &&
            string.IsNullOrWhiteSpace(snap.SpotifySpDcCookie))
        {
            return forceRefresh ? null : snap.SpotifyAccessToken;
        }

        await _tokenLock.WaitAsync(ct);
        try
        {
            snap = _settings.Current;
            nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (!forceRefresh &&
                !string.IsNullOrWhiteSpace(snap.SpotifyAccessToken) &&
                (snap.SpotifyTokenExpiresUnixMs <= 0 || snap.SpotifyTokenExpiresUnixMs > nowMs + 120_000))
            {
                return snap.SpotifyAccessToken;
            }

            if (!string.IsNullOrWhiteSpace(snap.SpotifyRefreshToken))
            {
                var pkce = await RefreshPkceTokenAsync(snap.SpotifyRefreshToken, ct);
                if (!string.IsNullOrWhiteSpace(pkce.AccessToken))
                {
                    long pkceExp = pkce.ExpiresUnixMs > 0
                        ? pkce.ExpiresUnixMs
                        : DateTimeOffset.UtcNow.AddMinutes(55).ToUnixTimeMilliseconds();
                    _settings.Update(s =>
                    {
                        s.SpotifyAccessToken = pkce.AccessToken;
                        if (!string.IsNullOrWhiteSpace(pkce.RefreshToken))
                            s.SpotifyRefreshToken = pkce.RefreshToken;
                        s.SpotifyTokenExpiresUnixMs = pkceExp;
                    }, notify: false);
                    return pkce.AccessToken;
                }
            }

            if (!string.IsNullOrWhiteSpace(snap.SpotifySpDcCookie))
            {
                var acq = await _bridge.AcquireTokenAsync(snap.SpotifySpDcCookie, allowAnonymous: false, ct);
                if (!acq.IsAnonymous && !string.IsNullOrWhiteSpace(acq.AccessToken))
                {
                    long finalExp = acq.ExpiresUnixMs > 0
                        ? acq.ExpiresUnixMs
                        : DateTimeOffset.UtcNow.AddMinutes(55).ToUnixTimeMilliseconds();
                    _settings.Update(s =>
                    {
                        s.SpotifyAccessToken = acq.AccessToken;
                        if (!string.IsNullOrWhiteSpace(acq.ClientToken))
                            s.SpotifyClientToken = acq.ClientToken;
                        s.SpotifyTokenExpiresUnixMs = finalExp;
                        if (!string.IsNullOrWhiteSpace(acq.SpDcCookie))
                            s.SpotifySpDcCookie = acq.SpDcCookie;
                    }, notify: false);
                    return acq.AccessToken;
                }
            }

            return forceRefresh ? null : snap.SpotifyAccessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task<(string? AccessToken, string? RefreshToken, long ExpiresUnixMs)> RefreshPkceTokenAsync(
        string refreshToken,
        CancellationToken ct)
    {
        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = DesktopPkceClientId,
                ["refresh_token"] = refreshToken
            });

            using var resp = await _http.PostAsync(PkceTokenEndpoint, content, ct);
            var json = await resp.Content.ReadAsStringAsync(ct);
            if (!resp.IsSuccessStatusCode || string.IsNullOrWhiteSpace(json))
                return (null, null, 0);

            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? access = ReadString(root, "access_token");
            string? nextRefresh = ReadString(root, "refresh_token");
            int expiresIn = root.TryGetProperty("expires_in", out var expEl) && expEl.ValueKind == JsonValueKind.Number
                ? expEl.GetInt32()
                : 3600;
            long expMs = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(expiresIn - 60, 60, 86400)).ToUnixTimeMilliseconds();
            return (access, nextRefresh, expMs);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return (null, null, 0);
        }
    }

    public async Task<(bool Ok, string Message)> AuthenticateViaSystemBrowserPkceAsync(
        Action<string> openBrowserUrl,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(openBrowserUrl);

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        string redirectUri = $"http://127.0.0.1:{port}/login";

        string codeVerifier = Base64UrlEncode(RandomNumberGenerator.GetBytes(48));
        string codeChallenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier)));
        string expectedState = Base64UrlEncode(RandomNumberGenerator.GetBytes(18));

        string authorizeUrl =
            "https://accounts.spotify.com/authorize" +
            $"?client_id={Uri.EscapeDataString(DesktopPkceClientId)}" +
            "&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            "&code_challenge_method=S256" +
            $"&code_challenge={Uri.EscapeDataString(codeChallenge)}" +
            $"&state={Uri.EscapeDataString(expectedState)}" +
            $"&scope={Uri.EscapeDataString(PkceScopes)}";

        openBrowserUrl(authorizeUrl);

        string? authCode = null;
        string? authState = null;
        string? authError = null;

        using var waitCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        waitCts.CancelAfter(TimeSpan.FromSeconds(180));

        try
        {
            while (!waitCts.IsCancellationRequested)
            {
                using var tcpClient = await listener.AcceptTcpClientAsync(waitCts.Token);
                using var stream = tcpClient.GetStream();

                var requestLine = await ReadHttpRequestLineAsync(stream, waitCts.Token);
                if (string.IsNullOrWhiteSpace(requestLine))
                    continue;

                var parts = requestLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2)
                {
                    await SendHttpTextResponseAsync(stream, "400 Bad Request", "text/plain; charset=utf-8", "Bad Request", waitCts.Token);
                    continue;
                }

                string target = parts[1];
                int qIdx = target.IndexOf('?');
                string path = qIdx >= 0 ? target[..qIdx] : target;
                string query = qIdx >= 0 ? target[(qIdx + 1)..] : "";

                if (path.StartsWith("/favicon", StringComparison.OrdinalIgnoreCase))
                {
                    await SendHttpNoContentResponseAsync(stream, waitCts.Token);
                    continue;
                }

                if (!string.Equals(path, "/login", StringComparison.OrdinalIgnoreCase))
                {
                    await SendHttpTextResponseAsync(stream, "404 Not Found", "text/plain; charset=utf-8", "Not Found", waitCts.Token);
                    continue;
                }

                var queryParams = ParseQueryParameters(query);
                queryParams.TryGetValue("code", out authCode);
                queryParams.TryGetValue("state", out authState);
                queryParams.TryGetValue("error", out authError);

                bool isSuccessHtml = string.IsNullOrWhiteSpace(authError) && !string.IsNullOrWhiteSpace(authCode);
                string html = BuildPkceCallbackHtml(isSuccessHtml, authError);
                await SendHttpTextResponseAsync(stream, "200 OK", "text/html; charset=utf-8", html, waitCts.Token);
                break;
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, "Время ожидания входа Spotify истекло (3 мин). Нажмите «Войти» ещё раз.");
        }
        finally
        {
            try { listener.Stop(); } catch { }
        }

        if (!string.IsNullOrWhiteSpace(authError))
            return (false, $"Spotify отклонил вход: {authError}");

        if (!string.Equals(authState, expectedState, StringComparison.Ordinal))
            return (false, "Ошибка проверки параметра state при входе Spotify.");

        if (string.IsNullOrWhiteSpace(authCode))
            return (false, "Spotify не передал код авторизации.");

        try
        {
            using var tokenContent = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = DesktopPkceClientId,
                ["code"] = authCode,
                ["redirect_uri"] = redirectUri,
                ["code_verifier"] = codeVerifier
            });

            using var tokenResp = await _http.PostAsync(PkceTokenEndpoint, tokenContent, ct);
            var tokenJson = await tokenResp.Content.ReadAsStringAsync(ct);
            if (!tokenResp.IsSuccessStatusCode || string.IsNullOrWhiteSpace(tokenJson))
            {
                return (false, $"Spotify отклонил обмен кода (HTTP {(int)tokenResp.StatusCode}). Проверьте прокси.");
            }

            using var doc = JsonDocument.Parse(tokenJson);
            var root = doc.RootElement;
            string? accessToken = ReadString(root, "access_token");
            string? refreshToken = ReadString(root, "refresh_token");
            int expiresIn = root.TryGetProperty("expires_in", out var expEl) && expEl.ValueKind == JsonValueKind.Number
                ? expEl.GetInt32()
                : 3600;
            long expMs = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(expiresIn - 60, 60, 86400)).ToUnixTimeMilliseconds();

            if (string.IsNullOrWhiteSpace(accessToken))
                return (false, "Spotify не вернул access_token.");

            return await VerifyAndSaveSessionAsync(
                accessToken,
                explicitSpDc: null,
                expiresUnixMs: expMs,
                hintUserName: null,
                hintUserId: null,
                clientToken: null,
                refreshToken: refreshToken,
                allowSaveOnRateLimit: true,
                ct: ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, $"Ошибка обмена кода Spotify: {ex.Message}");
        }
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static async Task<string?> ReadHttpRequestLineAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        int total = 0;
        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct);
            if (read <= 0) break;
            total += read;
            var text = Encoding.ASCII.GetString(buffer, 0, total);
            int nl = text.IndexOf('\n');
            if (nl >= 0)
                return text[..nl].TrimEnd('\r', '\n');
        }
        return total > 0 ? Encoding.ASCII.GetString(buffer, 0, total).Trim() : null;
    }

    private static async Task SendHttpNoContentResponseAsync(NetworkStream stream, CancellationToken ct)
    {
        var bytes = Encoding.ASCII.GetBytes("HTTP/1.1 204 No Content\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    private static async Task SendHttpTextResponseAsync(
        NetworkStream stream,
        string status,
        string contentType,
        string body,
        CancellationToken ct)
    {
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var header =
            $"HTTP/1.1 {status}\r\n" +
            $"Content-Type: {contentType}\r\n" +
            $"Content-Length: {bodyBytes.Length}\r\n" +
            "Cache-Control: no-store\r\n" +
            "Connection: close\r\n\r\n";
        var headerBytes = Encoding.ASCII.GetBytes(header);
        await stream.WriteAsync(headerBytes, ct);
        await stream.WriteAsync(bodyBytes, ct);
        await stream.FlushAsync(ct);
    }

    private static Dictionary<string, string> ParseQueryParameters(string query)
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(query)) return dict;
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            string k = Uri.UnescapeDataString(pair[..eq].Replace('+', ' '));
            string v = Uri.UnescapeDataString(pair[(eq + 1)..].Replace('+', ' '));
            dict[k] = v;
        }
        return dict;
    }

    private static string BuildPkceCallbackHtml(bool ok, string? error)
    {
        string border = ok ? "rgba(29,185,84,0.48)" : "rgba(251,113,133,0.48)";
        string accent = ok ? "#4ADE80" : "#FB7185";
        string title = ok ? "✓ Spotify подключён к OmniWave" : "Не удалось завершить вход в Spotify";
        string sub = ok
            ? "Авторизация прошла успешно — эту вкладку можно закрыть и вернуться в плеер."
            : WebUtility.HtmlEncode(error ?? "Попробуйте войти ещё раз из настроек OmniWave.");

        return
            "<!doctype html><html lang=\"ru\"><head><meta charset=\"utf-8\"><title>OmniWave • Spotify</title>" +
            "<style>body{margin:0;min-height:100vh;display:flex;align-items:center;justify-content:center;background:#0A0B0E;color:#F3F4F6;font-family:'Segoe UI',system-ui,sans-serif;}" +
            $".card{{max-width:420px;padding:24px 26px;border-radius:16px;background:#14161D;border:1px solid {border};box-shadow:0 16px 40px rgba(0,0,0,0.45);text-align:center;}}" +
            $"h1{{margin:0 0 10px;font-size:18px;color:{accent};font-weight:600;}}" +
            "p{margin:0;font-size:13.5px;line-height:1.5;color:#9CA3AF;}</style></head>" +
            $"<body><div class=\"card\"><h1>{title}</h1><p>{sub}</p></div>" +
            "<script>setTimeout(function(){try{window.close();}catch(e){}},1200);</script></body></html>";
    }

    private async Task<JsonDocument?> GetAuthorizedJsonAsync(string url, CancellationToken ct)
    {
        if (DateTime.UtcNow < _apiRateLimitedUntilUtc)
            return null;

        var token = await EnsureValidTokenAsync(false, ct);
        if (string.IsNullOrWhiteSpace(token)) return null;

        for (int attempt = 0; attempt < 2; attempt++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            ApplyWebApiHeaders(req, token, GetActiveClientToken());
            using var resp = await _http.SendAsync(req, ct);

            if (resp.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
            {
                token = await EnsureValidTokenAsync(true, ct);
                if (string.IsNullOrWhiteSpace(token)) return null;
                continue;
            }

            if (resp.StatusCode == HttpStatusCode.TooManyRequests)
            {
                if (attempt == 0 && resp.Headers.RetryAfter?.Delta is TimeSpan d && d.TotalSeconds is >= 1 and <= 4)
                {
                    await Task.Delay(d, ct);
                    continue;
                }
                _apiRateLimitedUntilUtc = DateTime.UtcNow.AddMinutes(3);
                return null;
            }

            if (!resp.IsSuccessStatusCode)
                return null;

            var json = await resp.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(json)) return null;
            return JsonDocument.Parse(json);
        }

        return null;
    }

    public async Task<List<TrackItem>> SearchAsync(string query, int limit = 8, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return new List<TrackItem>();

        if (IsAuthenticated)
        {
            var authResults = await SearchViaWebApiAsync(query, limit, useGuestToken: false, ct);
            if (authResults.Count > 0)
                return authResults;
        }

        // Поиск Spotify в гостевом режиме (без аккаунта)
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (!string.IsNullOrWhiteSpace(_guestAccessToken) &&
            (_guestTokenExpiresMs <= 0 || _guestTokenExpiresMs > nowMs + 60_000))
        {
            var fastGuest = await SearchViaWebApiAsync(query, limit, useGuestToken: true, ct);
            if (fastGuest.Count > 0)
                return fastGuest;
        }

        try
        {
            var guestTracks = await _guestBridge.SearchGuestTracksAsync(query, limit, ct);
            if (!string.IsNullOrWhiteSpace(_guestBridge.LastAccessToken))
            {
                _guestAccessToken = _guestBridge.LastAccessToken;
                _guestTokenExpiresMs = _guestBridge.LastTokenExpiresMs > 0
                    ? _guestBridge.LastTokenExpiresMs
                    : DateTimeOffset.UtcNow.AddMinutes(40).ToUnixTimeMilliseconds();
                if (!string.IsNullOrWhiteSpace(_guestBridge.LastClientToken))
                    _guestClientToken = _guestBridge.LastClientToken;
            }
            if (guestTracks.Count > 0)
                return guestTracks;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { }

        return new List<TrackItem>();
    }

    private async Task<List<TrackItem>> SearchViaWebApiAsync(
        string query,
        int limit,
        bool useGuestToken,
        CancellationToken ct)
    {
        var results = new List<TrackItem>();
        try
        {
            int fetchLimit = Math.Clamp(limit, 5, 25);
            var url = $"{ApiBase}/search?q={Uri.EscapeDataString(query.Trim())}&type=track,artist&limit={fetchLimit}";

            JsonDocument? doc = null;
            if (useGuestToken)
            {
                if (!string.IsNullOrWhiteSpace(_guestAccessToken))
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    ApplyWebApiHeaders(req, _guestAccessToken, _guestClientToken);
                    using var resp = await _http.SendAsync(req, ct);
                    if (resp.IsSuccessStatusCode)
                    {
                        var json = await resp.Content.ReadAsStringAsync(ct);
                        if (!string.IsNullOrWhiteSpace(json))
                            doc = JsonDocument.Parse(json);
                    }
                    else if ((int)resp.StatusCode is 401 or 403 or 429)
                    {
                        _guestAccessToken = null;
                    }
                }
            }
            else if (DateTime.UtcNow >= _apiRateLimitedUntilUtc)
            {
                doc = await GetAuthorizedJsonAsync(url, ct);
            }

            if (doc == null) return results;
            using (doc)
            {
                var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                void AddUnique(TrackItem? t)
                {
                    if (t == null || string.IsNullOrWhiteSpace(t.Id)) return;
                    if (seenIds.Add(t.Id)) results.Add(t);
                }

                // Приоритет треков искомого артиста (только при авторизованной сессии Web API)
                if (!useGuestToken &&
                    doc.RootElement.TryGetProperty("artists", out var artsObj) &&
                    artsObj.TryGetProperty("items", out var artItems) &&
                    artItems.ValueKind == JsonValueKind.Array &&
                    artItems.GetArrayLength() > 0)
                {
                    string qNorm = TrackMatcher.NormalizeKey(query);
                    string? matchedArtistId = null;
                    foreach (var aEl in artItems.EnumerateArray())
                    {
                        string aName = aEl.TryGetProperty("name", out var nEl) ? (nEl.GetString() ?? "") : "";
                        string aNorm = TrackMatcher.NormalizeKey(aName);
                        if (aNorm == qNorm || aNorm.StartsWith(qNorm, StringComparison.Ordinal))
                        {
                            matchedArtistId = aEl.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                            break;
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(matchedArtistId))
                    {
                        try
                        {
                            using var topDoc = await GetAuthorizedJsonAsync(
                                $"{ApiBase}/artists/{Uri.EscapeDataString(matchedArtistId)}/top-tracks", ct);
                            if (topDoc != null &&
                                topDoc.RootElement.TryGetProperty("tracks", out var topTracks) &&
                                topTracks.ValueKind == JsonValueKind.Array)
                            {
                                foreach (var tEl in topTracks.EnumerateArray())
                                    AddUnique(ParseSpotifyTrack(tEl));
                            }
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch { }
                    }
                }

                if (doc.RootElement.TryGetProperty("tracks", out var tracksObj) &&
                    tracksObj.TryGetProperty("items", out var trackItems) &&
                    trackItems.ValueKind == JsonValueKind.Array)
                {
                    foreach (var itemEl in trackItems.EnumerateArray())
                        AddUnique(ParseSpotifyTrack(itemEl));
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { }

        return results;
    }

    public async Task<List<UserPlaylist>> FetchUserPlaylistsAsync(CancellationToken ct = default)
    {
        var playlists = new List<UserPlaylist>();
        if (!IsAuthenticated) return playlists;

        // 1. Официальный Web API (если не под лимитом 429)
        if (DateTime.UtcNow >= _apiRateLimitedUntilUtc)
        {
            var likedTracks = new List<TrackItem>();
            string? nextLikesUrl = $"{ApiBase}/me/tracks?limit=50";
            while (!string.IsNullOrWhiteSpace(nextLikesUrl) && likedTracks.Count < 500)
            {
                using var doc = await GetAuthorizedJsonAsync(nextLikesUrl, ct);
                if (doc == null) break;

                if (doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var item in items.EnumerateArray())
                    {
                        if (item.TryGetProperty("track", out var tEl) && tEl.ValueKind == JsonValueKind.Object)
                        {
                            var tr = ParseSpotifyTrack(tEl);
                            if (tr != null) likedTracks.Add(tr);
                        }
                    }
                }

                nextLikesUrl = doc.RootElement.TryGetProperty("next", out var nEl) && nEl.ValueKind == JsonValueKind.String
                    ? nEl.GetString()
                    : null;
            }

            if (likedTracks.Count > 0)
            {
                playlists.Add(new UserPlaylist
                {
                    Name = "Мне нравится (Spotify)",
                    IsExternal = true,
                    PlatformBrand = BrandKey,
                    ExternalId = "spotify:likes",
                    CoverUrl = likedTracks.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.CoverUrl))?.CoverUrl ?? "",
                    Tracks = likedTracks
                });
            }

            string? nextPlUrl = $"{ApiBase}/me/playlists?limit=50";
            var rawPlaylists = new List<(string Id, string Name, string CoverUrl)>();
            while (!string.IsNullOrWhiteSpace(nextPlUrl) && rawPlaylists.Count < 35)
            {
                using var doc = await GetAuthorizedJsonAsync(nextPlUrl, ct);
                if (doc == null) break;

                if (doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    foreach (var plEl in items.EnumerateArray())
                    {
                        if (plEl.ValueKind != JsonValueKind.Object) continue;
                        var plId = plEl.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                        var plName = plEl.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                        if (string.IsNullOrWhiteSpace(plId) || string.IsNullOrWhiteSpace(plName)) continue;

                        var cover = ReadSpotifyCover(plEl);
                        rawPlaylists.Add((plId, plName.Trim(), cover));
                    }
                }

                nextPlUrl = doc.RootElement.TryGetProperty("next", out var nEl) && nEl.ValueKind == JsonValueKind.String
                    ? nEl.GetString()
                    : null;
            }

            foreach (var (plId, plName, plCover) in rawPlaylists)
            {
                var tracks = await FetchPlaylistTracksApiAsync(plId, ct);
                playlists.Add(new UserPlaylist
                {
                    Name = plName,
                    IsExternal = true,
                    PlatformBrand = BrandKey,
                    ExternalId = $"spotify:pl:{plId}",
                    CoverUrl = !string.IsNullOrWhiteSpace(plCover)
                        ? plCover
                        : (tracks.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.CoverUrl))?.CoverUrl ?? ""),
                    Tracks = tracks
                });
            }
        }

        // 2. Фолбэк через живую WebView-сессию sp_dc (если Web API вернул HTTP 429 / пусто)
        var spDc = _settings.Current.SpotifySpDcCookie;
        if (playlists.Count == 0 && !string.IsNullOrWhiteSpace(spDc))
        {
            var (bridgeLiked, bridgePls) = await _bridge.FetchAuthenticatedLibraryAsync(spDc, ct);
            if (bridgeLiked.Count > 0)
            {
                playlists.Add(new UserPlaylist
                {
                    Name = "Мне нравится (Spotify)",
                    IsExternal = true,
                    PlatformBrand = BrandKey,
                    ExternalId = "spotify:likes",
                    CoverUrl = bridgeLiked.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.CoverUrl))?.CoverUrl ?? "",
                    Tracks = bridgeLiked
                });
            }

            foreach (var (plId, plName, plCover) in bridgePls.Take(15))
            {
                if (ct.IsCancellationRequested) break;
                var embedRes = await TryResolvePublicEmbedAsync("playlist", plId, null, ct);
                var tracks = embedRes.Ok && embedRes.Tracks.Count > 0
                    ? embedRes.Tracks
                    : await _bridge.FetchAuthenticatedPlaylistTracksAsync(spDc, plId, ct);

                if (tracks.Count == 0) continue;
                playlists.Add(new UserPlaylist
                {
                    Name = plName,
                    IsExternal = true,
                    PlatformBrand = BrandKey,
                    ExternalId = $"spotify:pl:{plId}",
                    CoverUrl = !string.IsNullOrWhiteSpace(plCover)
                        ? plCover
                        : (tracks.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.CoverUrl))?.CoverUrl ?? ""),
                    Tracks = tracks
                });
            }
        }

        return playlists;
    }

    private async Task<List<TrackItem>> FetchPlaylistTracksApiAsync(string playlistId, CancellationToken ct)
    {
        var tracks = new List<TrackItem>();
        string? nextUrl = $"{ApiBase}/playlists/{Uri.EscapeDataString(playlistId)}/tracks?limit=100";

        while (!string.IsNullOrWhiteSpace(nextUrl) && tracks.Count < 400)
        {
            using var doc = await GetAuthorizedJsonAsync(nextUrl, ct);
            if (doc == null) break;

            if (doc.RootElement.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in items.EnumerateArray())
                {
                    if (entry.TryGetProperty("is_local", out var locEl) && locEl.ValueKind == JsonValueKind.True)
                        continue;

                    if (entry.TryGetProperty("track", out var tEl) && tEl.ValueKind == JsonValueKind.Object)
                    {
                        var parsed = ParseSpotifyTrack(tEl);
                        if (parsed != null) tracks.Add(parsed);
                    }
                }
            }

            nextUrl = doc.RootElement.TryGetProperty("next", out var nEl) && nEl.ValueKind == JsonValueKind.String
                ? nEl.GetString()
                : null;
        }

        return tracks;
    }

    /// <summary>
    /// Разбирает публичную ссылку open.spotify.com/track|playlist|album/{id}
    /// через авторизованный Web API либо через публичную embed-страницу Spotify (__NEXT_DATA__).
    /// </summary>
    public async Task<(bool Ok, string Kind, string? CollectionName, List<TrackItem> Tracks)> TryResolvePublicLinkAsync(
        string url,
        CancellationToken ct = default)
    {
        var m = SpotifyUrlRegex.Match(url ?? "");
        if (!m.Success)
            return (false, "", null, new List<TrackItem>());

        string kind = m.Groups[1].Value.ToLowerInvariant();
        string id = m.Groups[2].Value;

        if (IsAuthenticated)
        {
            try
            {
                if (kind == "track")
                {
                    using var doc = await GetAuthorizedJsonAsync($"{ApiBase}/tracks/{id}", ct);
                    var tr = doc != null ? ParseSpotifyTrack(doc.RootElement) : null;
                    if (tr != null) return (true, kind, tr.Title, new List<TrackItem> { tr });
                }
                else if (kind == "playlist")
                {
                    using var doc = await GetAuthorizedJsonAsync($"{ApiBase}/playlists/{id}", ct);
                    if (doc != null)
                    {
                        string name = doc.RootElement.TryGetProperty("name", out var nEl)
                            ? (nEl.GetString() ?? "Плейлист Spotify")
                            : "Плейлист Spotify";
                        var list = await FetchPlaylistTracksApiAsync(id, ct);
                        if (list.Count > 0) return (true, kind, name, list);
                    }
                }
                else if (kind == "album")
                {
                    using var doc = await GetAuthorizedJsonAsync($"{ApiBase}/albums/{id}", ct);
                    if (doc != null)
                    {
                        var root = doc.RootElement;
                        string name = root.TryGetProperty("name", out var nEl) ? (nEl.GetString() ?? "Альбом Spotify") : "Альбом Spotify";
                        string albCover = ReadSpotifyCover(root);
                        string albArtist = ReadSpotifyArtists(root);
                        var list = new List<TrackItem>();
                        if (root.TryGetProperty("tracks", out var tObj) &&
                            tObj.TryGetProperty("items", out var arr) &&
                            arr.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var tEl in arr.EnumerateArray())
                            {
                                var tr = ParseSpotifyTrack(tEl, fallbackCover: albCover, fallbackAlbum: name, fallbackArtist: albArtist);
                                if (tr != null) list.Add(tr);
                            }
                        }
                        if (list.Count > 0) return (true, kind, name, list);
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { }
        }

        // Публичный embed-фолбэк работает без авторизации
        return await TryResolvePublicEmbedAsync(kind, id, url, ct);
    }

    private async Task<(bool Ok, string Kind, string? CollectionName, List<TrackItem> Tracks)> TryResolvePublicEmbedAsync(
        string kind,
        string id,
        string? url,
        CancellationToken ct)
    {
        try
        {
            var embedUrl = $"https://open.spotify.com/embed/{kind}/{id}";
            using var resp = await _http.GetAsync(embedUrl, ct);
            if (!resp.IsSuccessStatusCode)
                return (false, kind, null, new List<TrackItem>());

            var html = await resp.Content.ReadAsStringAsync(ct);
            var nm = NextDataRegex.Match(html);
            if (!nm.Success)
                return (false, kind, null, new List<TrackItem>());

            using var doc = JsonDocument.Parse(nm.Groups[1].Value);
            if (!doc.RootElement.TryGetProperty("props", out var props) ||
                !props.TryGetProperty("pageProps", out var pp) ||
                !pp.TryGetProperty("state", out var st) ||
                !st.TryGetProperty("data", out var data) ||
                !data.TryGetProperty("entity", out var entity))
            {
                return (false, kind, null, new List<TrackItem>());
            }

            string entityTitle = ReadString(entity, "name") ?? ReadString(entity, "title") ?? "";
            string entityCover = ReadEmbedCover(entity);

            if (kind == "track")
            {
                var tr = ParseEmbedTrackEntity(entity, id, entityCover);
                if (tr != null)
                    return (true, kind, tr.Title, new List<TrackItem> { tr });
            }
            else if (entity.TryGetProperty("trackList", out var tList) && tList.ValueKind == JsonValueKind.Array)
            {
                var tracks = new List<TrackItem>();
                foreach (var item in tList.EnumerateArray())
                {
                    var uri = ReadString(item, "uri") ?? "";
                    var itemId = uri.Contains(":track:", StringComparison.OrdinalIgnoreCase)
                        ? uri.Split(':').Last()
                        : (ReadString(item, "uid") ?? "");
                    var title = ReadString(item, "title") ?? ReadString(item, "name");
                    var subtitle = ReadString(item, "subtitle");
                    long durMs = item.TryGetProperty("duration", out var dEl) && dEl.ValueKind == JsonValueKind.Number
                        ? dEl.GetInt64()
                        : 0;

                    if (string.IsNullOrWhiteSpace(title)) continue;
                    tracks.Add(new TrackItem
                    {
                        Id = itemId,
                        Source = Name,
                        BrandKey = BrandKey,
                        BrandColor = BrandColorHex,
                        Title = title,
                        Artist = string.IsNullOrWhiteSpace(subtitle) ? Name : subtitle,
                        CoverUrl = entityCover,
                        PageUrl = !string.IsNullOrWhiteSpace(itemId)
                            ? $"https://open.spotify.com/track/{itemId}"
                            : (url ?? OpenSpotifyUrl),
                        Duration = TimeSpan.FromMilliseconds(durMs)
                    });
                }

                if (tracks.Count > 0)
                    return (true, kind, entityTitle, tracks);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { }

        return (false, kind, null, new List<TrackItem>());
    }

    public async Task<string?> ResolveStreamUrlAsync(TrackItem track, CancellationToken ct = default)
    {
        LastResolveError = null;
        LastMatchedCandidate = null;

        if (IsSpotifyUrl(track.PageUrl) &&
            (string.IsNullOrWhiteSpace(track.Title) ||
             string.Equals(track.Artist, "open.spotify.com", StringComparison.OrdinalIgnoreCase) ||
             track.Duration <= TimeSpan.Zero))
        {
            var resolved = await TryResolvePublicLinkAsync(track.PageUrl, ct);
            if (resolved.Ok && resolved.Tracks.Count > 0)
            {
                var meta = resolved.Tracks[0];
                if (!string.IsNullOrWhiteSpace(meta.Id)) track.Id = meta.Id;
                if (!string.IsNullOrWhiteSpace(meta.Title)) track.Title = meta.Title;
                if (!string.IsNullOrWhiteSpace(meta.Artist)) track.Artist = meta.Artist;
                if (!string.IsNullOrWhiteSpace(meta.CoverUrl)) track.CoverUrl = meta.CoverUrl;
                if (meta.Duration > TimeSpan.Zero) track.Duration = meta.Duration;
            }
        }

        if (string.IsNullOrWhiteSpace(track.Title))
            return null;

        var queries = TrackMatcher.BuildCleanLookupQueries(track.Artist, track.Title);
        if (queries.Count == 0)
            return null;

        static int GetFallbackPriority(IMusicProvider p)
            => p.BrandKey.ToLowerInvariant() switch
            {
                "yandex" => 0,
                "vk" => 1,
                "soundcloud" => 2,
                "youtube" => 3,
                "audius" => 4,
                "bandcamp" => 5,
                _ => 6
            };

        var orderedFallbacks = _fallbackProviders
            .Where(p => p is not YandexMusicProvider ym || ym.IsAuthenticated)
            .Where(p => p is not VkMusicProvider vk || vk.IsAuthenticated)
            .Select(p => (Provider: p, Priority: GetFallbackPriority(p)))
            .OrderBy(x => x.Priority)
            .ToList();

        var verifiedMatches = new List<(IMusicProvider Provider, TrackItem Candidate, TrackMatcher.TrackMatchResult Match, int Priority)>();
        var triedStreamKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        static string StreamTryKey(IMusicProvider p, TrackItem c) => $"{p.BrandKey}:{c.UniqueKey}";

        // Проход 1: строгий верифицированный подбор с мгновенным возвратом при высокой уверенности
        foreach (var (fb, priority) in orderedFallbacks)
        {
            if (ct.IsCancellationRequested) break;
            try
            {
                var localValid = new Dictionary<string, (TrackItem Cand, TrackMatcher.TrackMatchResult Match)>(StringComparer.OrdinalIgnoreCase);
                TrackItem? bestProviderCand = null;
                TrackMatcher.TrackMatchResult bestProviderMatch = default;

                void EvaluateBatch(List<TrackItem> batch)
                {
                    foreach (var cand in batch)
                    {
                        if (cand == null) continue;
                        var m = TrackMatcher.ScoreMatch(track, cand);
                        if (!m.IsValid) continue;

                        if (!localValid.TryGetValue(cand.UniqueKey, out var prev) ||
                            m.Score > prev.Match.Score ||
                            (m.Score == prev.Match.Score && m.DurationDiffMs < prev.Match.DurationDiffMs))
                        {
                            localValid[cand.UniqueKey] = (cand, m);
                        }

                        if (bestProviderCand == null ||
                            m.Score > bestProviderMatch.Score ||
                            (m.Score == bestProviderMatch.Score && m.DurationDiffMs < bestProviderMatch.DurationDiffMs))
                        {
                            bestProviderCand = cand;
                            bestProviderMatch = m;
                        }
                    }
                }

                var primaryCandidates = await fb.SearchAsync(queries[0], 10, ct);
                EvaluateBatch(primaryCandidates);

                if ((bestProviderCand == null || !bestProviderMatch.IsHighConfidence) && queries.Count > 1)
                {
                    var secondaryCandidates = await fb.SearchAsync(queries[1], 8, ct);
                    EvaluateBatch(secondaryCandidates);
                }

                if (bestProviderCand != null && bestProviderMatch.IsHighConfidence)
                {
                    triedStreamKeys.Add(StreamTryKey(fb, bestProviderCand));
                    var stream = await fb.ResolveStreamUrlAsync(bestProviderCand, ct);
                    if (!string.IsNullOrWhiteSpace(stream))
                    {
                        if (string.IsNullOrWhiteSpace(track.CoverUrl) && !string.IsNullOrWhiteSpace(bestProviderCand.CoverUrl))
                            track.CoverUrl = bestProviderCand.CoverUrl;
                        LastMatchedCandidate = bestProviderCand;
                        return stream;
                    }
                }

                foreach (var entry in localValid.Values)
                    verifiedMatches.Add((fb, entry.Cand, entry.Match, priority));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { }
        }

        // Проход 2: глобально лучший верифицированный кандидат среди всех площадок (без слепых fallback-ов)
        if (verifiedMatches.Count > 0)
        {
            var ranked = verifiedMatches
                .OrderByDescending(x => x.Match.Score)
                .ThenBy(x => x.Match.DurationDiffMs)
                .ThenBy(x => x.Priority);

            int attempts = 0;
            foreach (var entry in ranked)
            {
                if (ct.IsCancellationRequested || attempts >= 5) break;
                var key = StreamTryKey(entry.Provider, entry.Candidate);
                if (!triedStreamKeys.Add(key)) continue;

                attempts++;
                try
                {
                    var stream = await entry.Provider.ResolveStreamUrlAsync(entry.Candidate, ct);
                    if (!string.IsNullOrWhiteSpace(stream))
                    {
                        if (string.IsNullOrWhiteSpace(track.CoverUrl) && !string.IsNullOrWhiteSpace(entry.Candidate.CoverUrl))
                            track.CoverUrl = entry.Candidate.CoverUrl;
                        LastMatchedCandidate = entry.Candidate;
                        return stream;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { }
            }
        }

        LastResolveError = $"Не удалось найти точный аудиопоток для «{track.Artist} — {track.Title}»";
        return null;
    }

    public async Task<(bool Ok, string Message)> RemoveTrackFromPlaylistAsync(
        string externalPlaylistId,
        TrackItem track,
        CancellationToken ct = default)
    {
        if (!IsAuthenticated)
            return (false, "Подключите аккаунт Spotify в Настройках.");

        var trackId = ExtractSpotifyTrackId(track);
        if (string.IsNullOrWhiteSpace(trackId))
            return (false, "Не удалось определить Spotify Track ID.");

        bool isLikes = string.Equals(externalPlaylistId, "spotify:likes", StringComparison.OrdinalIgnoreCase);
        bool isPlaylist = externalPlaylistId.StartsWith("spotify:pl:", StringComparison.OrdinalIgnoreCase);
        if (!isLikes && !isPlaylist)
            return (false, "Некорректный идентификатор плейлиста Spotify.");

        string apiUrl;
        string? jsonBody = null;
        if (isLikes)
        {
            apiUrl = $"{ApiBase}/me/tracks?ids={Uri.EscapeDataString(trackId)}";
        }
        else
        {
            var plId = externalPlaylistId["spotify:pl:".Length..];
            apiUrl = $"{ApiBase}/playlists/{Uri.EscapeDataString(plId)}/tracks";
            jsonBody = JsonSerializer.Serialize(new
            {
                tracks = new[] { new { uri = $"spotify:track:{trackId}" } }
            });
        }

        if (DateTime.UtcNow < _apiRateLimitedUntilUtc && !string.IsNullOrWhiteSpace(_settings.Current.SpotifySpDcCookie))
        {
            int brStatus = await _bridge.SendAuthenticatedDeleteAsync(
                apiUrl,
                jsonBody,
                _settings.Current.SpotifySpDcCookie!,
                ct);
            if (brStatus is >= 200 and < 300)
                return (true, isLikes
                    ? $"Удалено из Spotify «Мне нравится»: {track.Title}"
                    : $"Удалено из плейлиста Spotify: {track.Title}");
            if (brStatus == 403)
                return (false, "Нет прав на удаление из этого плейлиста Spotify (возможно, плейлист чужой).");
            return (false, $"Spotify отклонил удаление (HTTP {brStatus}).");
        }

        var token = await EnsureValidTokenAsync(false, ct);
        if (!string.IsNullOrWhiteSpace(token))
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                using var req = new HttpRequestMessage(HttpMethod.Delete, apiUrl);
                ApplyWebApiHeaders(req, token, GetActiveClientToken());
                if (jsonBody != null)
                    req.Content = new StringContent(jsonBody, System.Text.Encoding.UTF8, "application/json");

                using var resp = await _http.SendAsync(req, ct);
                if (resp.StatusCode == HttpStatusCode.Unauthorized && attempt == 0)
                {
                    token = await EnsureValidTokenAsync(true, ct);
                    if (string.IsNullOrWhiteSpace(token)) break;
                    continue;
                }

                if ((int)resp.StatusCode is >= 200 and < 300)
                {
                    return (true, isLikes
                        ? $"Удалено из Spotify «Мне нравится»: {track.Title}"
                        : $"Удалено из плейлиста Spotify: {track.Title}");
                }

                if (resp.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    _apiRateLimitedUntilUtc = DateTime.UtcNow.AddMinutes(3);
                    break;
                }

                if (resp.StatusCode == HttpStatusCode.Forbidden)
                    return (false, "Нет прав на удаление из этого плейлиста Spotify (возможно, плейлист чужой).");

                return (false, $"Spotify вернул ошибку удаления (HTTP {(int)resp.StatusCode}).");
            }
        }

        var spDc = _settings.Current.SpotifySpDcCookie;
        if (!string.IsNullOrWhiteSpace(spDc))
        {
            int brStatus = await _bridge.SendAuthenticatedDeleteAsync(apiUrl, jsonBody, spDc, ct);
            if (brStatus is >= 200 and < 300)
                return (true, isLikes
                    ? $"Удалено из Spotify «Мне нравится»: {track.Title}"
                    : $"Удалено из плейлиста Spotify: {track.Title}");
            if (brStatus == 403)
                return (false, "Нет прав на удаление из этого плейлиста Spotify (возможно, плейлист чужой).");
            return (false, $"Spotify отклонил удаление (HTTP {brStatus}).");
        }

        return (false, "Не удалось удалить трек из Spotify (нет активной сессии).");
    }

    private static string ExtractSpotifyTrackId(TrackItem track)
    {
        if (!string.IsNullOrWhiteSpace(track.Id) &&
            !track.Id.Contains(':') &&
            !track.Id.Contains('/') &&
            track.Id.Length >= 10)
        {
            return track.Id.Trim();
        }

        var m = SpotifyUrlRegex.Match(track.PageUrl ?? "");
        if (m.Success && string.Equals(m.Groups[1].Value, "track", StringComparison.OrdinalIgnoreCase))
            return m.Groups[2].Value.Trim();

        return "";
    }

    private TrackItem? ParseSpotifyTrack(
        JsonElement el,
        string? fallbackCover = null,
        string? fallbackAlbum = null,
        string? fallbackArtist = null)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;

        string id = ReadString(el, "id") ?? "";
        string title = ReadString(el, "name") ?? "";
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
            return null;

        string artist = ReadSpotifyArtists(el);
        if (string.IsNullOrWhiteSpace(artist))
            artist = !string.IsNullOrWhiteSpace(fallbackArtist) ? fallbackArtist! : Name;

        string coverUrl = fallbackCover ?? "";
        if (el.TryGetProperty("album", out var albEl) && albEl.ValueKind == JsonValueKind.Object)
        {
            var c = ReadSpotifyCover(albEl);
            if (!string.IsNullOrWhiteSpace(c)) coverUrl = c;
        }
        else if (string.IsNullOrWhiteSpace(coverUrl))
        {
            coverUrl = ReadSpotifyCover(el);
        }

        long durationMs = el.TryGetProperty("duration_ms", out var dEl) && dEl.ValueKind == JsonValueKind.Number
            ? dEl.GetInt64()
            : 0;

        string pageUrl = $"https://open.spotify.com/track/{id}";
        if (el.TryGetProperty("external_urls", out var extEl) &&
            extEl.TryGetProperty("spotify", out var spUrlEl) &&
            spUrlEl.ValueKind == JsonValueKind.String &&
            !string.IsNullOrWhiteSpace(spUrlEl.GetString()))
        {
            pageUrl = spUrlEl.GetString()!;
        }

        return new TrackItem
        {
            Id = id,
            Source = Name,
            BrandKey = BrandKey,
            BrandColor = BrandColorHex,
            Title = title,
            Artist = artist,
            CoverUrl = coverUrl,
            PageUrl = pageUrl,
            Duration = TimeSpan.FromMilliseconds(durationMs)
        };
    }

    private TrackItem? ParseEmbedTrackEntity(JsonElement entity, string id, string fallbackCover)
    {
        string? title = ReadString(entity, "name") ?? ReadString(entity, "title");
        if (string.IsNullOrWhiteSpace(title)) return null;

        string artist = ReadSpotifyArtists(entity);
        if (string.IsNullOrWhiteSpace(artist))
            artist = ReadString(entity, "subtitle") ?? Name;

        long durMs = entity.TryGetProperty("duration", out var dEl) && dEl.ValueKind == JsonValueKind.Number
            ? dEl.GetInt64()
            : 0;

        return new TrackItem
        {
            Id = id,
            Source = Name,
            BrandKey = BrandKey,
            BrandColor = BrandColorHex,
            Title = title,
            Artist = artist,
            CoverUrl = !string.IsNullOrWhiteSpace(fallbackCover) ? fallbackCover : ReadEmbedCover(entity),
            PageUrl = $"https://open.spotify.com/track/{id}",
            Duration = TimeSpan.FromMilliseconds(durMs)
        };
    }

    private static string ReadSpotifyArtists(JsonElement el)
    {
        if (!el.TryGetProperty("artists", out var arr) || arr.ValueKind != JsonValueKind.Array)
            return "";
        var names = new List<string>();
        foreach (var a in arr.EnumerateArray())
        {
            var n = ReadString(a, "name");
            if (!string.IsNullOrWhiteSpace(n))
                names.Add(n);
        }
        return string.Join(", ", names);
    }

    private static string ReadSpotifyCover(JsonElement el)
    {
        if (!el.TryGetProperty("images", out var imgs) || imgs.ValueKind != JsonValueKind.Array || imgs.GetArrayLength() == 0)
            return "";

        string? preferred = null;
        string? fallback = null;
        foreach (var img in imgs.EnumerateArray())
        {
            var u = ReadString(img, "url");
            if (string.IsNullOrWhiteSpace(u)) continue;
            fallback ??= u;
            int w = img.TryGetProperty("width", out var wEl) && wEl.ValueKind == JsonValueKind.Number ? wEl.GetInt32() : 0;
            if (w is >= 250 and <= 640)
            {
                preferred = u;
                break;
            }
        }
        return preferred ?? fallback ?? "";
    }

    private static string ReadEmbedCover(JsonElement entity)
    {
        if (entity.TryGetProperty("visualIdentity", out var vi) &&
            vi.TryGetProperty("image", out var vImg) &&
            vImg.ValueKind == JsonValueKind.Array &&
            vImg.GetArrayLength() > 0)
        {
            var u = ReadString(vImg[0], "url");
            if (!string.IsNullOrWhiteSpace(u)) return u;
        }

        if (entity.TryGetProperty("coverArt", out var ca) &&
            ca.TryGetProperty("sources", out var srcs) &&
            srcs.ValueKind == JsonValueKind.Array &&
            srcs.GetArrayLength() > 0)
        {
            var u = ReadString(srcs[0], "url");
            if (!string.IsNullOrWhiteSpace(u)) return u;
        }

        return ReadSpotifyCover(entity);
    }

    private static string? ReadString(JsonElement obj, string prop)
        => obj.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String
            ? el.GetString()
            : null;

    public void Dispose()
    {
        _bridge.Dispose();
        _guestBridge.Dispose();
        _http.Dispose();
        _tokenLock.Dispose();
    }
}
