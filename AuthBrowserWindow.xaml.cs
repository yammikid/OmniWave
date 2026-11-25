using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DynamicIsland.Services;
using Microsoft.Web.WebView2.Wpf;

namespace DynamicIsland;

public partial class AuthBrowserWindow : Window
{
    private const string VkWebAudiosUrl = "https://vk.ru/audios";

    private static readonly Regex AudiosPathUidRegex = new(
        @"/audios(-?\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private enum AuthMode
    {
        Yandex,
        Vk,
        Spotify,
        YouTube
    }

    private readonly AuthMode _mode;
    private readonly string _targetUrl;
    private readonly bool _clearSession;
    private bool _isFinished;
    private bool _isCapturingSession;
    private string? _pendingVkToken;
    private long _pendingVkUserId;
    private bool _redirectedVkAfterOAuth;
    private string? _capturedSpotifyClientToken;
    private string? _capturedSpotifyUserName;
    private string? _capturedSpotifyUserId;

    public string? ResultToken { get; private set; }
    public string? ResultCookieHeader { get; private set; }
    public long ResultUserId { get; private set; }
    public string? ResultUserName { get; private set; }
    public string? ResultUserIdStr { get; private set; }
    public string? ResultClientToken { get; private set; }
    public long ResultExpiresUnixMs { get; private set; }

    private AuthBrowserWindow(AuthMode mode, bool clearSession = false)
    {
        InitializeComponent();
        _mode = mode;
        _clearSession = clearSession;

        if (mode == AuthMode.Yandex)
        {
            _targetUrl = YandexMusicProvider.OAuthAuthorizeUrl;
            TxtTitle.Text = "Вход через Яндекс (OAuth)";
            HeaderLockIcon.Foreground = new SolidColorBrush(Color.FromRgb(255, 204, 0));
            BtnApplyManual.Background = new SolidColorBrush(Color.FromRgb(255, 204, 0));
            BtnApplyManual.Foreground = new SolidColorBrush(Color.FromRgb(17, 17, 22));
            TxtManualInput.CaretBrush = new SolidColorBrush(Color.FromRgb(255, 204, 0));
            BtnManualFallback.Content = "Вставить токен (y0_...) / URL или открыть внешний браузер";
            BtnOpenExternalBrowser.Visibility = Visibility.Visible;
        }
        else if (mode == AuthMode.Vk)
        {
            _targetUrl = VkWebAudiosUrl;
            TxtTitle.Text = "Вход в ВК Музыку";
            HeaderLockIcon.Foreground = new SolidColorBrush(Color.FromRgb(0, 119, 255));
            BtnApplyManual.Background = new SolidColorBrush(Color.FromRgb(0, 119, 255));
            BtnApplyManual.Foreground = Brushes.White;
            TxtManualInput.CaretBrush = new SolidColorBrush(Color.FromRgb(0, 119, 255));
            BtnManualFallback.Content = "Вставить cookie remixsid / токен вручную";
            BtnOpenExternalBrowser.Visibility = Visibility.Collapsed;
        }
        else if (mode == AuthMode.Spotify)
        {
            _targetUrl = SpotifyMusicProvider.LoginUrl;
            Width = 540;
            Height = 700;
            TxtTitle.Text = "Вход в Spotify";
            HeaderLockIcon.Foreground = new SolidColorBrush(Color.FromRgb(29, 185, 84));
            BtnApplyManual.Background = new SolidColorBrush(Color.FromRgb(29, 185, 84));
            BtnApplyManual.Foreground = new SolidColorBrush(Color.FromRgb(11, 11, 14));
            TxtManualInput.CaretBrush = new SolidColorBrush(Color.FromRgb(29, 185, 84));
            BtnManualFallback.Content = "Вставить токен (BQ...) или cookie sp_dc вручную";
            BtnOpenExternalBrowser.Visibility = Visibility.Collapsed;
        }
        else
        {
            _targetUrl = "https://accounts.google.com/ServiceLogin?service=youtube&uilel=3&passive=true&continue=https%3A%2F%2Fwww.youtube.com%2Fsignin%3Faction_handle_signin%3Dtrue%26next%3Dhttps%253A%252F%252Fwww.youtube.com%252F";
            Width = 540;
            Height = 700;
            TxtTitle.Text = "Вход в YouTube (для прокси)";
            HeaderLockIcon.Foreground = new SolidColorBrush(Color.FromRgb(255, 0, 51));
            BtnApplyManual.Background = new SolidColorBrush(Color.FromRgb(255, 0, 51));
            BtnApplyManual.Foreground = Brushes.White;
            TxtManualInput.CaretBrush = new SolidColorBrush(Color.FromRgb(255, 0, 51));
            BtnManualFallback.Visibility = Visibility.Collapsed;
            BtnOpenExternalBrowser.Visibility = Visibility.Collapsed;
        }

        Loaded += AuthBrowserWindow_Loaded;
        Closed += (_, _) =>
        {
            try { AuthWebView?.Dispose(); } catch { }
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowBlurHelper.ApplyAcrylic(this, false);
    }

    public static Task<string?> ShowYandexAuthAsync(Window owner, bool clearSession = false)
    {
        var win = new AuthBrowserWindow(AuthMode.Yandex, clearSession);
        ConfigureOwner(win, owner);
        win.ShowDialog();
        return Task.FromResult(win.ResultToken);
    }

    public static Task<(string? TokenOrInput, string? CookieHeader, long UserId, string? UserName)> ShowVkAuthAsync(
        Window owner,
        bool clearSession = false)
    {
        var win = new AuthBrowserWindow(AuthMode.Vk, clearSession);
        ConfigureOwner(win, owner);
        win.ShowDialog();
        return Task.FromResult((win.ResultToken, win.ResultCookieHeader, win.ResultUserId, win.ResultUserName));
    }

    public static Task<(string? AccessToken, string? SpDcCookie, long ExpiresUnixMs, string? UserName, string? UserId, string? ClientToken)> ShowSpotifyAuthAsync(
        Window owner,
        bool clearSession = false)
    {
        var win = new AuthBrowserWindow(AuthMode.Spotify, clearSession);
        ConfigureOwner(win, owner);
        win.ShowDialog();
        return Task.FromResult((
            win.ResultToken,
            win.ResultCookieHeader,
            win.ResultExpiresUnixMs,
            win.ResultUserName,
            win.ResultUserIdStr,
            win.ResultClientToken));
    }

    public static Task<bool> ShowYouTubeAuthAsync(Window owner, bool clearSession = false)
    {
        var win = new AuthBrowserWindow(AuthMode.YouTube, clearSession);
        ConfigureOwner(win, owner);
        win.ShowDialog();
        return Task.FromResult(win.ResultToken == "ok");
    }

    private static void ConfigureOwner(Window dialog, Window owner)
    {
        if (owner != null && owner.IsVisible)
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    private async void AuthBrowserWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var profileFolderName = _mode switch
            {
                AuthMode.Spotify => "WebView2Spotify",
                AuthMode.YouTube => "WebView2YouTube",
                _ => "WebView2Auth"
            };
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "OmniWave",
                profileFolderName);
            Directory.CreateDirectory(userDataFolder);

            var wvProxyArgs = ProxyService.GetWebView2ProxyArguments();
            AuthWebView.CreationProperties = new CoreWebView2CreationProperties
            {
                UserDataFolder = userDataFolder,
                AdditionalBrowserArguments = wvProxyArgs
            };
            await AuthWebView.EnsureCoreWebView2Async();

            if (_clearSession && AuthWebView.CoreWebView2 != null)
            {
                try
                {
                    var cm = AuthWebView.CoreWebView2.CookieManager;
                    var urls = _mode == AuthMode.Yandex
                        ? new[] { "https://passport.yandex.ru", "https://oauth.yandex.ru", "https://yandex.ru", "https://music.yandex.ru" }
                        : _mode == AuthMode.Spotify
                            ? new[] { "https://open.spotify.com", "https://accounts.spotify.com", "https://www.spotify.com" }
                            : _mode == AuthMode.YouTube
                                ? new[] { "https://www.youtube.com", "https://music.youtube.com", "https://accounts.google.com", "https://youtube.com" }
                                : new[]
                                {
                                    "https://vk.ru",
                                    "https://m.vk.ru",
                                    "https://id.vk.ru",
                                    "https://login.vk.ru",
                                    "https://vk.com",
                                    "https://m.vk.com",
                                    "https://id.vk.com",
                                    "https://login.vk.com",
                                    "https://oauth.vk.com"
                                };

                    foreach (var url in urls)
                    {
                        var existing = await cm.GetCookiesAsync(url);
                        foreach (var cookie in existing)
                            cm.DeleteCookie(cookie);
                    }
                }
                catch { }
            }

            if (_mode == AuthMode.Spotify && AuthWebView.CoreWebView2 != null)
            {
                await AuthWebView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(SpotifyWebSessionBridge.TokenHookScript);
                AuthWebView.WebMessageReceived += async (_, ev) =>
                {
                    if (_isFinished || AuthWebView.CoreWebView2 == null) return;
                    try
                    {
                        var msgJson = ev.TryGetWebMessageAsString();
                        if (string.IsNullOrWhiteSpace(msgJson)) return;
                        using var doc = JsonDocument.Parse(msgJson);
                        var root = doc.RootElement;
                        string? kind = root.TryGetProperty("kind", out var kEl) ? kEl.GetString() : null;

                        if (kind == "clientToken")
                        {
                            var ctVal = root.TryGetProperty("clientToken", out var cEl) ? cEl.GetString() : null;
                            if (!string.IsNullOrWhiteSpace(ctVal))
                                _capturedSpotifyClientToken = ctVal.Trim();
                            return;
                        }

                        if (kind == "pathfinderProfile")
                        {
                            if (root.TryGetProperty("profile", out var profEl) && profEl.ValueKind == JsonValueKind.Object)
                            {
                                var uid = profEl.TryGetProperty("username", out var uEl) ? uEl.GetString() : null;
                                var uname = profEl.TryGetProperty("name", out var nEl) ? nEl.GetString() : null;
                                if (!string.IsNullOrWhiteSpace(uid)) _capturedSpotifyUserId = uid.Trim();
                                if (!string.IsNullOrWhiteSpace(uname)) _capturedSpotifyUserName = uname.Trim();
                            }
                            return;
                        }

                        if (_isCapturingSession) return;

                        string? tok = root.TryGetProperty("token", out var tEl) ? tEl.GetString() : null;
                        long expMs = root.TryGetProperty("expiresMs", out var eEl) && eEl.ValueKind == JsonValueKind.Number
                            ? eEl.GetInt64()
                            : 0;
                        bool isAnon = root.TryGetProperty("isAnonymous", out var aEl) && aEl.ValueKind == JsonValueKind.True;

                        if (string.IsNullOrWhiteSpace(tok) || isAnon)
                            return;

                        _isCapturingSession = true;
                        try
                        {
                            var cookies = await AuthWebView.CoreWebView2.CookieManager.GetCookiesAsync("https://open.spotify.com");
                            var spDc = cookies.FirstOrDefault(c => string.Equals(c.Name, "sp_dc", StringComparison.OrdinalIgnoreCase))?.Value;
                            if (string.IsNullOrWhiteSpace(spDc) && kind is "fetchBearer" or "xhrBearer")
                                return;
                            if (kind is "apiToken" or "sessionTag" && string.IsNullOrWhiteSpace(spDc))
                            {
                                var accCookies = await AuthWebView.CoreWebView2.CookieManager.GetCookiesAsync("https://accounts.spotify.com");
                                spDc = accCookies.FirstOrDefault(c => string.Equals(c.Name, "sp_dc", StringComparison.OrdinalIgnoreCase))?.Value;
                            }

                            if (!string.IsNullOrWhiteSpace(spDc))
                            {
                                for (int step = 0; step < 12; step++)
                                {
                                    if (!string.IsNullOrWhiteSpace(_capturedSpotifyClientToken) &&
                                        !string.IsNullOrWhiteSpace(_capturedSpotifyUserName))
                                    {
                                        break;
                                    }

                                    if (string.IsNullOrWhiteSpace(_capturedSpotifyUserName) && AuthWebView?.CoreWebView2 != null)
                                    {
                                        try
                                        {
                                            var rawWidget = await AuthWebView.ExecuteScriptAsync(SpotifyWebSessionBridge.DomExtractUserWidgetJs);
                                            var parsedWidget = JsonSerializer.Deserialize<string>(rawWidget);
                                            if (!string.IsNullOrWhiteSpace(parsedWidget))
                                                _capturedSpotifyUserName = parsedWidget.Trim();
                                        }
                                        catch { }
                                    }

                                    if (!string.IsNullOrWhiteSpace(_capturedSpotifyClientToken) &&
                                        !string.IsNullOrWhiteSpace(_capturedSpotifyUserName))
                                    {
                                        break;
                                    }

                                    await Task.Delay(150);
                                }
                            }

                            CompleteSpotifySession(
                                tok,
                                spDc,
                                expMs,
                                _capturedSpotifyUserName,
                                _capturedSpotifyUserId,
                                _capturedSpotifyClientToken);
                        }
                        finally
                        {
                            _isCapturingSession = false;
                        }
                    }
                    catch { }
                };
            }

            AuthWebView.NavigationStarting += (_, ev) =>
            {
                if (_isFinished) return;
                TxtStatus.Text = _mode switch
                {
                    AuthMode.Yandex => "Загрузка Яндекс ID...",
                    AuthMode.Spotify => "Загрузка Spotify...",
                    AuthMode.YouTube => "Загрузка YouTube / Google...",
                    _ => "Загрузка ВКонтакте..."
                };

                if (_mode == AuthMode.Yandex && TryCaptureYandexTokenFromUrl(ev.Uri))
                {
                    ev.Cancel = true;
                    return;
                }

                if (_mode == AuthMode.Vk && TryStashVkTokenFromUrl(ev.Uri))
                {
                    ev.Cancel = true;
                    RedirectVkToAudiosAfterToken();
                }
            };

            AuthWebView.SourceChanged += (_, _) =>
            {
                if (_isFinished) return;
                var currentUri = AuthWebView.Source?.ToString();
                if (_mode == AuthMode.Yandex)
                {
                    TryCaptureYandexTokenFromUrl(currentUri);
                }
                else if (_mode == AuthMode.Vk)
                {
                    if (TryStashVkTokenFromUrl(currentUri))
                    {
                        RedirectVkToAudiosAfterToken();
                        return;
                    }

                    if (IsVkAuthenticatedPage(currentUri))
                        _ = TryCaptureVkSessionAsync(currentUri);
                }
            };

            AuthWebView.NavigationCompleted += async (_, ev) =>
            {
                if (_isFinished) return;

                TxtStatus.Text = ev.IsSuccess
                    ? _mode switch
                    {
                        AuthMode.Yandex => "Войдите и разрешите доступ — токен перехватится автоматически",
                        AuthMode.Spotify => "Войдите в аккаунт Spotify — подключение произойдёт автоматически",
                        AuthMode.YouTube => "Войдите в Google-аккаунт YouTube — cookies сохранятся автоматически",
                        _ => "Войдите в аккаунт ВКонтакте — музыка подключится автоматически"
                    }
                    : $"Ошибка загрузки ({ev.WebErrorStatus})";

                var currentUri = AuthWebView.Source?.ToString();
                if (_mode == AuthMode.YouTube &&
                    !string.IsNullOrWhiteSpace(currentUri) &&
                    currentUri.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) &&
                    !currentUri.Contains("accounts.google.", StringComparison.OrdinalIgnoreCase))
                {
                    await TryCaptureYouTubeCookiesAsync();
                    return;
                }
                if (_mode == AuthMode.Yandex)
                {
                    if (TryCaptureYandexTokenFromUrl(currentUri)) return;
                    if (AuthWebView.CoreWebView2 != null)
                    {
                        try
                        {
                            var hrefJson = await AuthWebView.ExecuteScriptAsync("window.location.href");
                            var href = JsonSerializer.Deserialize<string>(hrefJson);
                            if (TryCaptureYandexTokenFromUrl(href)) return;
                        }
                        catch { }
                    }
                }
                else if (_mode == AuthMode.Vk)
                {
                    if (TryStashVkTokenFromUrl(currentUri))
                    {
                        RedirectVkToAudiosAfterToken();
                        return;
                    }

                    if (IsVkAuthenticatedPage(currentUri))
                        await TryCaptureVkSessionAsync(currentUri);
                }
                else if (_mode == AuthMode.Spotify && AuthWebView.CoreWebView2 != null)
                {
                    // Если вход завершился и остался sp_dc, но токен ещё не пришёл со страницы, переходим на плеер
                    if (!string.IsNullOrWhiteSpace(currentUri) &&
                        currentUri.Contains("open.spotify.com", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var cookies = await AuthWebView.CoreWebView2.CookieManager.GetCookiesAsync("https://open.spotify.com");
                            var spDc = cookies.FirstOrDefault(c => string.Equals(c.Name, "sp_dc", StringComparison.OrdinalIgnoreCase))?.Value;
                            var sesJson = await AuthWebView.ExecuteScriptAsync(
                                "(function(){var el=document.getElementById('session');return el?el.textContent:'';})()");
                            var rawSes = JsonSerializer.Deserialize<string>(sesJson);
                            if (!string.IsNullOrWhiteSpace(rawSes))
                            {
                                using var doc = JsonDocument.Parse(rawSes);
                                bool isAnon = doc.RootElement.TryGetProperty("isAnonymous", out var aEl) && aEl.ValueKind == JsonValueKind.True;
                                string? tok = doc.RootElement.TryGetProperty("accessToken", out var tEl) ? tEl.GetString() : null;
                                long exp = doc.RootElement.TryGetProperty("accessTokenExpirationTimestampMs", out var eEl) && eEl.ValueKind == JsonValueKind.Number
                                    ? eEl.GetInt64()
                                    : 0;
                                if (!isAnon && !string.IsNullOrWhiteSpace(tok))
                                    CompleteSpotifySession(tok, spDc, exp);
                            }
                        }
                        catch { }
                    }
                }
            };

            TxtStatus.Text = _mode switch
            {
                AuthMode.Yandex => "Открываем страницу OAuth Яндекса...",
                AuthMode.Spotify => "Открываем плеер Spotify...",
                AuthMode.YouTube => "Открываем вход в YouTube...",
                _ => "Открываем страницу музыки ВКонтакте..."
            };
            AuthWebView.CoreWebView2?.Navigate(_targetUrl);
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"WebView2 недоступен ({ex.Message}). Воспользуйтесь вводом вручную.";
            ManualPanel.Visibility = Visibility.Visible;
        }
    }

    private bool TryCaptureYandexTokenFromUrl(string? url)
    {
        if (_isFinished || string.IsNullOrWhiteSpace(url)) return false;
        if (!url.Contains("access_token=", StringComparison.OrdinalIgnoreCase)) return false;

        var token = YandexMusicProvider.ExtractToken(url);
        if (string.IsNullOrWhiteSpace(token)) return false;

        CompleteToken(token);
        return true;
    }

    private bool TryStashVkTokenFromUrl(string? url)
    {
        if (_isFinished || string.IsNullOrWhiteSpace(url)) return false;
        if (!url.Contains("access_token=", StringComparison.OrdinalIgnoreCase)) return false;

        var token = VkMusicProvider.ExtractAccessToken(url);
        if (string.IsNullOrWhiteSpace(token)) return false;

        _pendingVkToken = token;
        var uid = VkMusicProvider.ExtractUserIdFromUrl(url);
        if (uid > 0) _pendingVkUserId = uid;
        return true;
    }

    private void RedirectVkToAudiosAfterToken()
    {
        if (_redirectedVkAfterOAuth || AuthWebView.CoreWebView2 == null) return;
        _redirectedVkAfterOAuth = true;
        var target = _pendingVkUserId > 0 ? $"https://vk.ru/audios{_pendingVkUserId}" : VkWebAudiosUrl;
        AuthWebView.CoreWebView2.Navigate(target);
    }

    private static bool IsVkAuthenticatedPage(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;

        var host = uri.Host.ToLowerInvariant();
        if (host is not ("vk.ru" or "www.vk.ru" or "m.vk.ru" or "vk.com" or "www.vk.com" or "m.vk.com"))
            return false;

        var path = uri.AbsolutePath.ToLowerInvariant();
        if (path.StartsWith("/login") || path.StartsWith("/restore") || path.StartsWith("/join") || path.StartsWith("/authorize"))
            return false;

        return true;
    }

    private async Task TryCaptureVkSessionAsync(string? currentUrl)
    {
        if (_isFinished || _isCapturingSession || AuthWebView.CoreWebView2 == null)
            return;

        _isCapturingSession = true;
        try
        {
            var cm = AuthWebView.CoreWebView2.CookieManager;
            var cookieLists = await Task.WhenAll(
                cm.GetCookiesAsync("https://vk.ru"),
                cm.GetCookiesAsync("https://m.vk.ru"),
                cm.GetCookiesAsync("https://vk.com"),
                cm.GetCookiesAsync("https://m.vk.com"),
                cm.GetCookiesAsync("https://login.vk.ru"),
                cm.GetCookiesAsync("https://login.vk.com"));

            var merged = cookieLists.SelectMany(x => x)
                .Where(c => !string.IsNullOrWhiteSpace(c.Name))
                .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            bool hasSession = merged.Any(c =>
                string.Equals(c.Name, "remixsid", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.Name, "remixnsid", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.Name, "remixstlid", StringComparison.OrdinalIgnoreCase));

            if (!hasSession)
                return;

            var rawInfo = await AuthWebView.ExecuteScriptAsync(@"(function(){
                try {
                    document.cookie = 'remixaudio_show_alert_today=0; path=/; domain=.vk.ru';
                    document.cookie = 'remixmdevice=1920/1080/2/!!-!!!!; path=/; domain=.vk.ru';
                    var id = (window.vk && Number(window.vk.id)) || 0;
                    if (!id && window.cur && Number(window.cur.oid) > 0) id = Number(window.cur.oid);
                    if (!id) {
                        var pm = window.location.pathname.match(/\/audios(-?\d+)/);
                        if (pm && Number(pm[1]) > 0) id = Number(pm[1]);
                    }
                    if (!id) {
                        var m = document.documentElement.innerHTML.match(/""(?:id|uid)""\s*:\s*(\d{3,15})/);
                        if (m) id = Number(m[1]);
                    }
                    var nameEl = document.querySelector('.TopNavBtn__profileName, .top_profile_name, [data-testid=""top-profile-name""]');
                    var img = document.querySelector('img.TopNavBtn__profileImg');
                    var name = (nameEl && nameEl.textContent) || (img && img.getAttribute('alt')) || '';
                    return JSON.stringify({ uid: id || 0, name: (name || '').trim() });
                } catch(e) { return JSON.stringify({ uid: 0, name: '' }); }
            })();");

            var unquoted = JsonSerializer.Deserialize<string>(rawInfo);
            long uid = _pendingVkUserId;
            string? uname = null;

            if (!string.IsNullOrWhiteSpace(unquoted))
            {
                using var doc = JsonDocument.Parse(unquoted);
                if (doc.RootElement.TryGetProperty("uid", out var uEl) && uEl.ValueKind == JsonValueKind.Number)
                {
                    long parsedUid = uEl.GetInt64();
                    if (parsedUid > 0) uid = parsedUid;
                }
                if (doc.RootElement.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String)
                    uname = nEl.GetString();
            }

            if (uid <= 0 && !string.IsNullOrWhiteSpace(currentUrl))
            {
                var pm = AudiosPathUidRegex.Match(currentUrl);
                if (pm.Success && long.TryParse(pm.Groups[1].Value, out long pathUid) && pathUid > 0)
                    uid = pathUid;
            }

            if (uid <= 0)
                return;

            var cookieHeader = VkMusicProvider.EnsureRequiredAudioCookies(
                string.Join("; ", merged.Select(c => $"{c.Name}={c.Value}")));
            if (string.IsNullOrWhiteSpace(cookieHeader))
                return;

            CompleteVkSession(_pendingVkToken, cookieHeader, uid, uname);
        }
        catch { }
        finally
        {
            _isCapturingSession = false;
        }
    }

    private async Task TryCaptureYouTubeCookiesAsync()
    {
        if (_isFinished || _isCapturingSession || AuthWebView.CoreWebView2 == null)
            return;

        _isCapturingSession = true;
        try
        {
            var cm = AuthWebView.CoreWebView2.CookieManager;
            var ytCookies = await cm.GetCookiesAsync("https://www.youtube.com");
            var musicCookies = await cm.GetCookiesAsync("https://music.youtube.com");
            var merged = ytCookies.Concat(musicCookies)
                .Where(c => !string.IsNullOrWhiteSpace(c.Domain) && !string.IsNullOrWhiteSpace(c.Name))
                .GroupBy(c => $"{c.Domain}|{c.Path}|{c.Name}", StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            bool signedIn = merged.Any(c =>
                string.Equals(c.Name, "SAPISID", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.Name, "__Secure-3PAPISID", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.Name, "SID", StringComparison.OrdinalIgnoreCase));

            if (!signedIn)
                return;

            var rows = merged.Select(c =>
            {
                var utcExp = c.Expires.Kind == DateTimeKind.Unspecified
                    ? DateTime.SpecifyKind(c.Expires, DateTimeKind.Utc)
                    : c.Expires.ToUniversalTime();
                long exp = utcExp.Year >= 1971
                    ? new DateTimeOffset(utcExp).ToUnixTimeSeconds()
                    : 0;
                return (c.Domain, c.Domain.StartsWith('.'), c.Path, c.IsSecure, exp, c.Name, c.Value);
            });

            YtDlpMusicProvider.SaveNetscapeCookies(rows);
            CompleteToken("ok");
        }
        catch { }
        finally
        {
            _isCapturingSession = false;
        }
    }

    private void CompleteToken(string token)
    {
        if (_isFinished) return;
        _isFinished = true;
        ResultToken = token;
        try { DialogResult = true; } catch { }
        Close();
    }

    private void CompleteVkSession(string? token, string? cookieHeader, long userId, string? userName)
    {
        if (_isFinished) return;
        _isFinished = true;
        ResultToken = token;
        ResultCookieHeader = cookieHeader;
        ResultUserId = userId;
        ResultUserName = userName;
        try { DialogResult = true; } catch { }
        Close();
    }

    private void CompleteSpotifySession(
        string? token,
        string? spDcCookie,
        long expiresUnixMs,
        string? userName = null,
        string? userId = null,
        string? clientToken = null)
    {
        if (_isFinished) return;
        _isFinished = true;
        ResultToken = token;
        ResultCookieHeader = spDcCookie;
        ResultExpiresUnixMs = expiresUnixMs;
        ResultUserName = userName;
        ResultUserIdStr = userId;
        ResultClientToken = clientToken;
        try { DialogResult = true; } catch { }
        Close();
    }

    private void ManualFallback_Click(object sender, RoutedEventArgs e)
    {
        ManualPanel.Visibility = Visibility.Visible;
        TxtStatus.Text = _mode switch
        {
            AuthMode.Yandex => "Вставьте OAuth-токен (y0_...) или итоговый URL с access_token= ниже.",
            AuthMode.Spotify => "Вставьте Bearer-токен (BQ...) или cookie sp_dc=... из Spotify ниже.",
            _ => "Вставьте cookie remixsid=... или токен ВКонтакте ниже."
        };
    }

    private void OpenExternalBrowser_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = _targetUrl, UseShellExecute = true });
            TxtStatus.Text = "После входа скопируйте весь URL из адресной строки (с access_token=) и вставьте ниже.";
        }
        catch (Exception ex)
        {
            TxtStatus.Text = $"Не удалось открыть браузер: {ex.Message}";
        }
    }

    private void ApplyManual_Click(object sender, RoutedEventArgs e)
    {
        var input = TxtManualInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(input)) return;

        if (_mode == AuthMode.Yandex)
        {
            var token = YandexMusicProvider.ExtractToken(input);
            if (!string.IsNullOrWhiteSpace(token))
                CompleteToken(token);
        }
        else if (_mode == AuthMode.Spotify)
        {
            var (tok, spDc) = SpotifyMusicProvider.ExtractSessionInputs(input);
            if (!string.IsNullOrWhiteSpace(tok) || !string.IsNullOrWhiteSpace(spDc))
                CompleteSpotifySession(tok, spDc, 0);
        }
        else
        {
            var vkToken = VkMusicProvider.ExtractAccessToken(input);
            var vkUid = VkMusicProvider.ExtractUserIdFromUrl(input);
            if (!string.IsNullOrWhiteSpace(vkToken))
                CompleteVkSession(vkToken, null, vkUid, null);
            else
                CompleteVkSession(null, input, 0, null);
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_isFinished) return;
        _isFinished = true;
        try { DialogResult = false; } catch { }
        Close();
    }
}
