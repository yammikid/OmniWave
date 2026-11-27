using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using DynamicIsland.Services;
using Microsoft.Web.WebView2.Wpf;

namespace DynamicIsland;

public partial class VkAuthWindow : Window
{
    private const string VkWebAudiosUrl = "https://vk.ru/audios";

    private static readonly Regex AudiosPathUidRegex = new(
        @"/audios(-?\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly bool _clearSession;
    private readonly DispatcherTimer _pollTimer;
    private bool _isFinished;
    private bool _isCapturingSession;
    private bool _redirectedToAudiosAfterAuth;
    private string? _pendingVkToken;
    private long _pendingVkUserId;

    public string? ResultToken { get; private set; }
    public string? ResultCookieHeader { get; private set; }
    public long ResultUserId { get; private set; }
    public string? ResultUserName { get; private set; }

    public VkAuthWindow(bool clearSession = false)
    {
        InitializeComponent();
        _clearSession = clearSession;

        var work = SystemParameters.WorkArea;
        Width = Math.Min(980, Math.Max(MinWidth, work.Width - 48));
        Height = Math.Min(740, Math.Max(MinHeight, work.Height - 48));

        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1200)
        };
        _pollTimer.Tick += async (_, _) => await PollSessionTickAsync();

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

    /// <summary>
    /// Открывает отдельное масштабируемое окно браузера для входа в VK (QR-код или логин).
    /// </summary>
    public static (string? Token, string? CookieHeader, long UserId, string? UserName) ShowVkAuth(
        Window? owner,
        bool clearSession = false)
    {
        var win = new VkAuthWindow(clearSession);
        if (owner != null && owner.IsVisible)
            win.Owner = owner;

        win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        win.ShowDialog();
        return (win.ResultToken, win.ResultCookieHeader, win.ResultUserId, win.ResultUserName);
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
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "OmniWave",
                "WebView2Auth");
            Directory.CreateDirectory(userDataFolder);

            // Общий профиль с VkWebSessionBridge без AdditionalBrowserArguments во избежание 0x8007139F
            AuthWebView.CreationProperties = new CoreWebView2CreationProperties
            {
                UserDataFolder = userDataFolder
            };
            await AuthWebView.EnsureCoreWebView2Async();

            var core = AuthWebView.CoreWebView2;
            if (core == null)
            {
                EnableManualFallback("Не удалось инициализировать WebView2. Вставьте cookie или токен вручную.");
                return;
            }

            var settings = core.Settings;
            settings.IsZoomControlEnabled = true;
            settings.AreDefaultContextMenusEnabled = true;
            settings.IsStatusBarEnabled = false;
            settings.AreDevToolsEnabled = false;

            AuthWebView.ZoomFactorChanged += (_, _) => UpdateZoomLabel();
            UpdateZoomLabel();

            if (_clearSession)
            {
                await ClearVkCookiesAsync();
            }

            AuthWebView.NavigationStarting += (_, ev) =>
            {
                if (TryStashVkTokenFromUrl(ev.Uri))
                {
                    ev.Cancel = true;
                    RedirectVkAfterToken();
                    return;
                }
                TxtStatus.Text = "Загрузка страницы ВКонтакте...";
            };

            AuthWebView.SourceChanged += async (_, _) =>
            {
                var uri = AuthWebView.Source?.ToString();
                if (TryStashVkTokenFromUrl(uri))
                {
                    RedirectVkAfterToken();
                    return;
                }
                if (IsVkAuthenticatedPage(uri))
                    await TryCaptureVkSessionAsync(uri);
            };

            core.HistoryChanged += async (_, _) =>
            {
                var uri = AuthWebView.Source?.ToString();
                if (IsVkAuthenticatedPage(uri))
                    await TryCaptureVkSessionAsync(uri);
            };

            AuthWebView.NavigationCompleted += async (_, ev) =>
            {
                var currentUri = AuthWebView.Source?.ToString();
                if (!ev.IsSuccess)
                {
                    TxtStatus.Text = $"Ошибка загрузки ({ev.WebErrorStatus}). Можно обновить страницу или ввести данные вручную.";
                }
                else
                {
                    TxtStatus.Text = "Отсканируйте QR-код или войдите в VK — окно закроется автоматически.";
                }

                if (TryStashVkTokenFromUrl(currentUri))
                {
                    RedirectVkAfterToken();
                    return;
                }

                if (IsVkAuthenticatedPage(currentUri))
                    await TryCaptureVkSessionAsync(currentUri);
            };

            _pollTimer.Start();
            TxtStatus.Text = "Открываем страницу ВКонтакте — отсканируйте QR-код или введите логин...";
            core.Navigate(VkWebAudiosUrl);
        }
        catch (Exception ex)
        {
            EnableManualFallback($"WebView2 недоступен ({ex.Message}). Воспользуйтесь вводом вручную.");
        }
    }

    private async Task ClearVkCookiesAsync()
    {
        var core = AuthWebView.CoreWebView2;
        if (core == null) return;

        try
        {
            var cm = core.CookieManager;
            var urls = new[]
            {
                "https://vk.ru",
                "https://m.vk.ru",
                "https://id.vk.ru",
                "https://login.vk.ru",
                "https://oauth.vk.ru",
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

    private void EnableManualFallback(string message)
    {
        TxtStatus.Text = message;
        ManualPanel.Visibility = Visibility.Visible;
    }

    private async Task PollSessionTickAsync()
    {
        if (_isFinished || _isCapturingSession || AuthWebView.CoreWebView2 == null)
            return;

        var currentUri = AuthWebView.Source?.ToString();
        if (TryStashVkTokenFromUrl(currentUri))
        {
            RedirectVkAfterToken();
            return;
        }

        if (IsVkAuthenticatedPage(currentUri))
            await TryCaptureVkSessionAsync(currentUri);
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

    private void RedirectVkAfterToken()
    {
        if (_redirectedToAudiosAfterAuth || AuthWebView.CoreWebView2 == null) return;
        _redirectedToAudiosAfterAuth = true;
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
        if (path.StartsWith("/login") ||
            path.StartsWith("/restore") ||
            path.StartsWith("/join") ||
            path.StartsWith("/authorize") ||
            path.StartsWith("/auth"))
        {
            return false;
        }

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
                cm.GetCookiesAsync("https://login.vk.com"),
                cm.GetCookiesAsync("https://id.vk.ru"),
                cm.GetCookiesAsync("https://id.vk.com"));

            var merged = cookieLists.SelectMany(x => x)
                .Where(c => !string.IsNullOrWhiteSpace(c.Name) && !string.IsNullOrWhiteSpace(c.Value))
                .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            bool hasSession = merged.Any(c =>
                string.Equals(c.Name, "remixsid", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.Name, "remixnsid", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(c.Name, "remixstlid", StringComparison.OrdinalIgnoreCase));

            if (!hasSession)
                return;

            // Дублируем remix*-cookie и технические cookie на .vk.ru и .vk.com для VkWebSessionBridge
            foreach (var dom in new[] { ".vk.ru", ".vk.com" })
            {
                foreach (var c in merged.Where(x => x.Name.StartsWith("remix", StringComparison.OrdinalIgnoreCase)))
                {
                    var copy = cm.CreateCookie(c.Name, c.Value, dom, "/");
                    copy.IsSecure = true;
                    cm.AddOrUpdateCookie(copy);
                }

                var alertCookie = cm.CreateCookie("remixaudio_show_alert_today", "0", dom, "/");
                alertCookie.IsSecure = true;
                cm.AddOrUpdateCookie(alertCookie);

                var devCookie = cm.CreateCookie("remixmdevice", "1920/1080/2/!!-!!!!", dom, "/");
                devCookie.IsSecure = true;
                cm.AddOrUpdateCookie(devCookie);
            }

            long uid = _pendingVkUserId;
            string? uname = null;

            try
            {
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
            }
            catch { }

            if (uid <= 0 && !string.IsNullOrWhiteSpace(currentUrl))
            {
                var pm = AudiosPathUidRegex.Match(currentUrl);
                if (pm.Success && long.TryParse(pm.Groups[1].Value, out long pathUid) && pathUid > 0)
                    uid = pathUid;
            }

            // Если вход завершился (например, через QR на id.vk.ru -> vk.ru), но UID ещё не проявился,
            // один раз переходим на /audios чтобы VK отдал /audios<uid>
            if (uid <= 0 && !_redirectedToAudiosAfterAuth)
            {
                _redirectedToAudiosAfterAuth = true;
                TxtStatus.Text = "Сессия найдена, открываем раздел музыки ВКонтакте...";
                AuthWebView.CoreWebView2.Navigate(VkWebAudiosUrl);
                return;
            }

            var cookieHeader = VkMusicProvider.EnsureRequiredAudioCookies(
                string.Join("; ", merged.Select(c => $"{c.Name}={c.Value}")));
            if (string.IsNullOrWhiteSpace(cookieHeader))
                return;

            // Даже если uid <= 0 после редиректа, передаём cookieHeader: VerifyAndSaveSessionAsync определит uid через ProbeUserIdentityAsync
            CompleteVkSession(_pendingVkToken, cookieHeader, uid, uname);
        }
        catch { }
        finally
        {
            _isCapturingSession = false;
        }
    }

    private void CompleteVkSession(string? token, string? cookieHeader, long userId, string? userName)
    {
        if (_isFinished) return;
        _isFinished = true;
        _pollTimer.Stop();
        ResultToken = token;
        ResultCookieHeader = cookieHeader;
        ResultUserId = userId;
        ResultUserName = userName;
        try { DialogResult = true; } catch { }
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
        bool open = ManualPanel.Visibility != Visibility.Visible;
        ManualPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        if (open)
        {
            TxtStatus.Text = "Вставьте cookie (remixsid=...) или итоговый URL с access_token= ниже.";
            TxtManualInput.Focus();
        }
        else
        {
            TxtStatus.Text = "Отсканируйте QR-код или войдите в VK — окно закроется автоматически.";
        }
    }

    private void ApplyManual_Click(object sender, RoutedEventArgs e)
    {
        var input = TxtManualInput.Text.Trim();
        if (string.IsNullOrWhiteSpace(input)) return;

        var vkToken = VkMusicProvider.ExtractAccessToken(input);
        var vkUid = VkMusicProvider.ExtractUserIdFromUrl(input);
        if (!string.IsNullOrWhiteSpace(vkToken))
            CompleteVkSession(vkToken, null, vkUid, null);
        else
            CompleteVkSession(null, input, vkUid, null);
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaxRestore_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (_isFinished) return;
        _isFinished = true;
        _pollTimer.Stop();
        try { DialogResult = false; } catch { }
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
        _pollTimer.Stop();
        try { AuthWebView?.Dispose(); } catch { }
    }
}
