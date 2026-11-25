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
        Vk
    }

    private readonly AuthMode _mode;
    private readonly string _targetUrl;
    private readonly bool _clearSession;
    private bool _isFinished;
    private bool _isCapturingSession;
    private string? _pendingVkToken;
    private long _pendingVkUserId;
    private bool _redirectedVkAfterOAuth;

    public string? ResultToken { get; private set; }
    public string? ResultCookieHeader { get; private set; }
    public long ResultUserId { get; private set; }
    public string? ResultUserName { get; private set; }

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
        else
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
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "OmniWave",
                "WebView2Auth");
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

            AuthWebView.NavigationStarting += (_, ev) =>
            {
                if (_isFinished) return;
                TxtStatus.Text = _mode == AuthMode.Yandex ? "Загрузка Яндекс ID..." : "Загрузка ВКонтакте...";

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
                else
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
                    ? (_mode == AuthMode.Yandex
                        ? "Войдите и разрешите доступ — токен перехватится автоматически"
                        : "Войдите в аккаунт ВКонтакте — музыка подключится автоматически")
                    : $"Ошибка загрузки ({ev.WebErrorStatus})";

                var currentUri = AuthWebView.Source?.ToString();
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
                else
                {
                    if (TryStashVkTokenFromUrl(currentUri))
                    {
                        RedirectVkToAudiosAfterToken();
                        return;
                    }

                    if (IsVkAuthenticatedPage(currentUri))
                        await TryCaptureVkSessionAsync(currentUri);
                }
            };

            TxtStatus.Text = _mode == AuthMode.Yandex
                ? "Открываем страницу OAuth Яндекса..."
                : "Открываем страницу музыки ВКонтакте...";
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

    private void ManualFallback_Click(object sender, RoutedEventArgs e)
    {
        ManualPanel.Visibility = Visibility.Visible;
        TxtStatus.Text = _mode == AuthMode.Yandex
            ? "Вставьте OAuth-токен (y0_...) или итоговый URL с access_token= ниже."
            : "Вставьте cookie remixsid=... или токен ВКонтакте ниже.";
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
