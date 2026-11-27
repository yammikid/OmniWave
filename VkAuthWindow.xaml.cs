using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DynamicIsland.Services;
using Microsoft.Web.WebView2.Wpf;

namespace DynamicIsland;

public enum BrowserAuthMode
{
    Vk,
    YouTube
}

public sealed record BrowserAuthResult(
    bool Completed,
    string? Token,
    string? CookieHeader,
    long UserId,
    string? UserName);

public partial class VkAuthWindow : Window
{
    private const string VkWebAudiosUrl = "https://vk.ru/audios";
    private const string YouTubeLoginUrl =
        "https://accounts.google.com/ServiceLogin?service=youtube&uilel=3&passive=true&continue=https%3A%2F%2Fwww.youtube.com%2Fsignin%3Faction_handle_signin%3Dtrue%26next%3Dhttps%253A%252F%252Fwww.youtube.com%252F";

    private static readonly Regex AudiosPathUidRegex = new(
        @"/audios(-?\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly BrowserAuthMode _mode;
    private readonly bool _clearSession;
    private bool _isFinished;
    private bool _isCapturingSession;
    private bool _redirectedVkAfterOAuth;
    private string? _pendingVkToken;
    private long _pendingVkUserId;

    public bool ResultCompleted { get; private set; }
    public string? ResultToken { get; private set; }
    public string? ResultCookieHeader { get; private set; }
    public long ResultUserId { get; private set; }
    public string? ResultUserName { get; private set; }

    public VkAuthWindow(BrowserAuthMode mode = BrowserAuthMode.Vk, bool clearSession = false)
    {
        InitializeComponent();
        _mode = mode;
        _clearSession = clearSession;

        ApplyHeaderAppearance();

        var work = SystemParameters.WorkArea;
        Width = Math.Min(980, Math.Max(MinWidth, work.Width - 48));
        Height = Math.Min(740, Math.Max(MinHeight, work.Height - 48));

        PreviewKeyDown += VkAuthWindow_PreviewKeyDown;
        SizeChanged += (_, _) =>
        {
            if (WindowState == WindowState.Normal)
                WindowBlurHelper.UpdateWin10RoundedRegion(this, 12);
        };
        StateChanged += VkAuthWindow_StateChanged;
        Loaded += VkAuthWindow_Loaded;
        Closed += VkAuthWindow_Closed;
    }

    private void ApplyHeaderAppearance()
    {
        if (_mode == BrowserAuthMode.YouTube)
        {
            Title = "Вход в YouTube — OmniWave";
            HeaderDot.Fill = new SolidColorBrush(Color.FromRgb(255, 59, 48));
            TxtHeaderTitle.Text = "Вход в YouTube (cookies)";
            TxtHeaderHint.Text = "• Войдите в аккаунт Google — cookies сохранятся автоматически";
            BtnManualToggle.Visibility = Visibility.Collapsed;
            TxtStatus.Text = "Инициализация встроенного браузера...";
        }
        else
        {
            Title = "Вход в VK Музыку — OmniWave";
            HeaderDot.Fill = new SolidColorBrush(Color.FromRgb(38, 136, 235));
            TxtHeaderTitle.Text = "Вход в VK Музыку";
            TxtHeaderHint.Text = "• QR-код или логин (Ctrl + колесо для масштаба)";
            BtnManualToggle.Visibility = Visibility.Visible;
            TxtStatus.Text = "Инициализация встроенного браузера...";
        }
    }

    /// <summary>
    /// Немодально открывает отдельное масштабируемое окно WebView2 для входа (VK QR / YouTube Google)
    /// и возвращает результат после завершения авторизации или закрытия окна.
    /// Не использует ShowDialog(), чтобы не блокировать COM-коллбэк WebMessageReceived.
    /// </summary>
    public static Task<BrowserAuthResult> ShowAuthAsync(
        Window? owner,
        BrowserAuthMode mode,
        bool clearSession = false,
        Action<VkAuthWindow>? onCreated = null)
    {
        var tcs = new TaskCompletionSource<BrowserAuthResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var win = new VkAuthWindow(mode, clearSession);
        if (owner != null && owner.IsVisible)
            win.Owner = owner;

        win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        win.Closed += (_, _) =>
        {
            tcs.TrySetResult(new BrowserAuthResult(
                win.ResultCompleted,
                win.ResultToken,
                win.ResultCookieHeader,
                win.ResultUserId,
                win.ResultUserName));
        };

        onCreated?.Invoke(win);
        win.Show();
        win.Activate();
        return tcs.Task;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowBlurHelper.ApplyChromaWindowStyle(this, 12);
        UpdateChromeCorners();
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            if (WindowState == WindowState.Normal)
                WindowBlurHelper.UpdateWin10RoundedRegion(this, 12);
        });
    }

    private void VkAuthWindow_StateChanged(object? sender, EventArgs e)
    {
        UpdateChromeCorners();
    }

    private void UpdateChromeCorners()
    {
        if (WindowState == WindowState.Maximized)
        {
            RootBorder.Margin = new Thickness(7);
            RootBorder.CornerRadius = new CornerRadius(0);
            BtnMaxRestore.Content = "❐";
            WindowBlurHelper.ClearRoundedRegion(this);
        }
        else
        {
            RootBorder.Margin = new Thickness(0);
            RootBorder.CornerRadius = new CornerRadius(WindowBlurHelper.IsWindows11OrLater ? 8 : 12);
            BtnMaxRestore.Content = "□";
            WindowBlurHelper.UpdateWin10RoundedRegion(this, 12);
        }
    }

    private async void VkAuthWindow_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            TxtStatus.Text = "Инициализация встроенного браузера...";

            var profileFolder = _mode == BrowserAuthMode.YouTube ? "WebView2YouTube" : "WebView2Auth";
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "OmniWave",
                profileFolder);
            Directory.CreateDirectory(userDataFolder);

            AuthWebView.CoreWebView2InitializationCompleted += (_, ev) =>
            {
                if (!ev.IsSuccess)
                {
                    var errText = $"Ошибка WebView2: {ev.InitializationException?.Message ?? "не удалось инициализировать"}";
                    if (_mode == BrowserAuthMode.Vk)
                        EnableManualFallback(errText);
                    else
                        TxtStatus.Text = errText;
                }
            };

            if (_mode == BrowserAuthMode.YouTube)
            {
                var wvProxyArgs = ProxyService.GetWebView2ProxyArguments();
                AuthWebView.CreationProperties = new CoreWebView2CreationProperties
                {
                    UserDataFolder = userDataFolder,
                    AdditionalBrowserArguments = string.IsNullOrWhiteSpace(wvProxyArgs) ? null : wvProxyArgs
                };
            }
            else
            {
                // Общий профиль с VkWebSessionBridge без AdditionalBrowserArguments во избежание 0x8007139F
                AuthWebView.CreationProperties = new CoreWebView2CreationProperties
                {
                    UserDataFolder = userDataFolder
                };
            }

            await AuthWebView.EnsureCoreWebView2Async();

            var core = AuthWebView.CoreWebView2;
            if (core == null)
            {
                if (_mode == BrowserAuthMode.Vk)
                    EnableManualFallback("Не удалось инициализировать WebView2. Вставьте cookie или токен вручную.");
                else
                    TxtStatus.Text = "Не удалось инициализировать WebView2.";
                return;
            }

            core.Settings.IsStatusBarEnabled = false;

            AuthWebView.ZoomFactorChanged += (_, _) => UpdateZoomLabel();
            UpdateZoomLabel();

            if (_clearSession)
            {
                await ClearModeCookiesAsync();
            }

            AuthWebView.NavigationStarting += (_, ev) =>
            {
                if (_isFinished) return;
                TxtStatus.Text = _mode == BrowserAuthMode.Vk ? "Загрузка ВКонтакте..." : "Загрузка Google / YouTube...";

                if (_mode == BrowserAuthMode.Vk && TryStashVkTokenFromUrl(ev.Uri))
                {
                    ev.Cancel = true;
                    RedirectVkToAudiosAfterToken();
                }
            };

            AuthWebView.SourceChanged += (_, _) =>
            {
                if (_isFinished) return;
                var currentUri = AuthWebView.Source?.ToString();
                if (_mode == BrowserAuthMode.Vk)
                {
                    if (TryStashVkTokenFromUrl(currentUri))
                    {
                        RedirectVkToAudiosAfterToken();
                        return;
                    }

                    if (IsVkAuthenticatedPage(currentUri))
                        _ = TryCaptureVkSessionAsync(currentUri);
                }
                else if (IsYouTubeUrl(currentUri))
                {
                    _ = TryCaptureYouTubeCookiesAsync();
                }
            };

            AuthWebView.NavigationCompleted += async (_, ev) =>
            {
                if (_isFinished) return;

                TxtStatus.Text = ev.IsSuccess
                    ? (_mode == BrowserAuthMode.Vk
                        ? "Войдите в аккаунт ВКонтакте — музыка подключится автоматически"
                        : "Войдите в Google-аккаунт — окно закроется после перехода на YouTube.")
                    : $"Ошибка загрузки ({ev.WebErrorStatus})";

                var currentUri = AuthWebView.Source?.ToString();
                if (_mode == BrowserAuthMode.Vk)
                {
                    if (TryStashVkTokenFromUrl(currentUri))
                    {
                        RedirectVkToAudiosAfterToken();
                        return;
                    }

                    if (IsVkAuthenticatedPage(currentUri))
                        await TryCaptureVkSessionAsync(currentUri);
                }
                else if (IsYouTubeUrl(currentUri))
                {
                    await TryCaptureYouTubeCookiesAsync();
                }
            };

            if (_mode == BrowserAuthMode.YouTube)
            {
                TxtStatus.Text = "Открываем вход Google / YouTube...";
                core.Navigate(YouTubeLoginUrl);
            }
            else
            {
                TxtStatus.Text = "Открываем страницу музыки ВКонтакте...";
                core.Navigate(VkWebAudiosUrl);
            }
        }
        catch (Exception ex)
        {
            if (_mode == BrowserAuthMode.Vk)
                EnableManualFallback($"WebView2 недоступен ({ex.Message}). Воспользуйтесь вводом вручную.");
            else
                TxtStatus.Text = $"WebView2 недоступен: {ex.Message}";
        }
    }

    private async Task ClearModeCookiesAsync()
    {
        var core = AuthWebView.CoreWebView2;
        if (core == null) return;

        try
        {
            var cm = core.CookieManager;
            var urls = _mode == BrowserAuthMode.YouTube
                ? new[]
                {
                    "https://www.youtube.com",
                    "https://music.youtube.com",
                    "https://accounts.google.com",
                    "https://youtube.com"
                }
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
                    "https://oauth.vk.com",
                    "https://oauth.vk.ru"
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

    private void EnableManualFallback(string message)
    {
        TxtStatus.Text = message;
        if (_mode == BrowserAuthMode.Vk)
            ManualPanel.Visibility = Visibility.Visible;
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

    private static bool IsYouTubeUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;

        var host = uri.Host.ToLowerInvariant();
        return host is "www.youtube.com" or "youtube.com" or "music.youtube.com" or "m.youtube.com";
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
                .Where(c => !string.IsNullOrWhiteSpace(c.Name) && !string.IsNullOrWhiteSpace(c.Value))
                .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            bool hasSession = merged.Any(c =>
                (string.Equals(c.Name, "remixsid", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(c.Name, "remixnsid", StringComparison.OrdinalIgnoreCase)) &&
                !c.Value.Equals("DELETED", StringComparison.OrdinalIgnoreCase));

            if (!hasSession && string.IsNullOrWhiteSpace(_pendingVkToken))
                return;

            var rawInfo = await AuthWebView.ExecuteScriptAsync(@"(function(){
                try {
                    document.cookie = 'remixaudio_show_alert_today=0; path=/; domain=.vk.ru';
                    document.cookie = 'remixmdevice=1920/1080/2/!!-!!!!; path=/; domain=.vk.ru';
                    var hasVkObj = Boolean(window.vk && typeof window.vk.id !== 'undefined');
                    var id = hasVkObj ? (Number(window.vk.id) || 0) : 0;
                    if (!id && !hasVkObj && window.cur && Number(window.cur.oid) > 0) id = Number(window.cur.oid);
                    if (!id) {
                        var pm = window.location.pathname.match(/\/audios(-?\d+)/);
                        if (pm && Number(pm[1]) > 0) id = Number(pm[1]);
                    }
                    if (!id && !hasVkObj) {
                        var m = document.documentElement.innerHTML.match(/""(?:id|uid)""\s*:\s*(\d{3,15})/);
                        if (m) id = Number(m[1]);
                    }
                    var nameEl = document.querySelector('.TopNavBtn__profileName, .top_profile_name, [data-testid=""top-profile-name""]');
                    var img = document.querySelector('img.TopNavBtn__profileImg');
                    var name = (nameEl && nameEl.textContent) || (img && img.getAttribute('alt')) || '';
                    return JSON.stringify({ uid: id > 0 ? id : 0, name: (name || '').trim() });
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

            CompleteAuthSuccess(_pendingVkToken, cookieHeader, uid, uname);
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
            var rootCookies = await cm.GetCookiesAsync("https://youtube.com");
            var merged = ytCookies.Concat(musicCookies).Concat(rootCookies)
                .Where(c => !string.IsNullOrWhiteSpace(c.Domain) && !string.IsNullOrWhiteSpace(c.Name))
                .GroupBy(c => $"{c.Domain}|{c.Path}|{c.Name}", StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            bool signedIn = merged.Any(c =>
                (c.Value?.Length ?? 0) > 8 &&
                (string.Equals(c.Name, "SAPISID", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(c.Name, "__Secure-3PAPISID", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(c.Name, "SID", StringComparison.OrdinalIgnoreCase)));

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
            CompleteAuthSuccess("ok", null, 0, null);
        }
        catch { }
        finally
        {
            _isCapturingSession = false;
        }
    }

    private void CompleteAuthSuccess(string? token, string? cookieHeader, long userId, string? userName)
    {
        if (_isFinished) return;
        _isFinished = true;
        ResultCompleted = true;
        ResultToken = token;
        ResultCookieHeader = cookieHeader;
        ResultUserId = userId;
        ResultUserName = userName;
        Close();
    }

    private void UpdateZoomLabel()
    {
        try
        {
            double pct = Math.Round((AuthWebView?.ZoomFactor ?? 1.0) * 100.0);
            BtnZoomReset.Content = $"{pct:0}%";
        }
        catch { }
    }

    private void SetZoomFactorClamped(double factor)
    {
        if (AuthWebView == null) return;
        AuthWebView.ZoomFactor = Math.Clamp(Math.Round(factor, 2), 0.5, 2.0);
        UpdateZoomLabel();
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e)
        => SetZoomFactorClamped((AuthWebView?.ZoomFactor ?? 1.0) + 0.1);

    private void ZoomOut_Click(object sender, RoutedEventArgs e)
        => SetZoomFactorClamped((AuthWebView?.ZoomFactor ?? 1.0) - 0.1);

    private void ZoomReset_Click(object sender, RoutedEventArgs e)
        => SetZoomFactorClamped(1.0);

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (AuthWebView.CoreWebView2 != null)
                AuthWebView.Reload();
        }
        catch { }
    }

    private void ManualToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_mode != BrowserAuthMode.Vk) return;
        bool open = ManualPanel.Visibility != Visibility.Visible;
        ManualPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (open)
        {
            TxtStatus.Text = "Вставьте cookie (remixsid=...) или итоговый URL с access_token= ниже.";
            TxtManualInput.Focus();
        }
        else
        {
            TxtStatus.Text = "Войдите в аккаунт ВКонтакте — музыка подключится автоматически";
        }
    }

    private void ApplyManual_Click(object sender, RoutedEventArgs e)
    {
        if (_mode != BrowserAuthMode.Vk) return;
        var input = TxtManualInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(input)) return;

        var vkToken = VkMusicProvider.ExtractAccessToken(input);
        var vkUid = VkMusicProvider.ExtractUserIdFromUrl(input);
        if (!string.IsNullOrWhiteSpace(vkToken))
            CompleteAuthSuccess(vkToken, null, vkUid, null);
        else
            CompleteAuthSuccess(null, input, vkUid, null);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaxRestore_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_isFinished) return;
        _isFinished = true;
        Close();
    }

    private void VkAuthWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Close_Click(this, new RoutedEventArgs());
            return;
        }

        if ((Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
        {
            if (e.Key is Key.OemPlus or Key.Add)
            {
                e.Handled = true;
                ZoomIn_Click(this, new RoutedEventArgs());
            }
            else if (e.Key is Key.OemMinus or Key.Subtract)
            {
                e.Handled = true;
                ZoomOut_Click(this, new RoutedEventArgs());
            }
            else if (e.Key is Key.D0 or Key.NumPad0)
            {
                e.Handled = true;
                ZoomReset_Click(this, new RoutedEventArgs());
            }
        }
    }

    private void VkAuthWindow_Closed(object? sender, EventArgs e)
    {
        _isFinished = true;
        try { AuthWebView?.Dispose(); } catch { }
    }
}
