using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DynamicIsland.Services;

namespace DynamicIsland;

public partial class SettingsWindow : Window
{
    private readonly SettingsService _settings;
    private readonly Action _resetIslandPosition;
    private readonly Action _openAccountsAction;
    private bool _isInitializing = true;
    private string _selectedProxyType = "http";
    private bool _isPasswordVisible;
    private bool _isSyncingPassword;
    private bool _suppressNextExternalReload;
    private CancellationTokenSource? _proxyTestCts;

    public SettingsWindow(SettingsService settings, Action resetIslandPosition, Action openAccountsAction)
    {
        InitializeComponent();
        _settings = settings;
        _resetIslandPosition = resetIslandPosition;
        _openAccountsAction = openAccountsAction;

        LoadFromSettings();
        _settings.SettingsChanged += OnExternalSettingsChanged;
        Loaded += async (_, _) =>
        {
            await Task.WhenAll(RunYtDlpCheckAsync(), RunAppUpdateCheckAsync());
        };
    }

    private void OnExternalSettingsChanged(AppSettings _)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (_suppressNextExternalReload)
            {
                _suppressNextExternalReload = false;
                return;
            }
            if (!_isInitializing)
                LoadFromSettings();
        });
    }

    private void LoadFromSettings()
    {
        _isInitializing = true;
        var s = _settings.Current;

        ChkAutoStart.IsChecked = _settings.IsAutoStartEnabled();
        ChkStartMinimized.IsChecked = s.StartMinimizedToIsland;
        ChkAutoPause.IsChecked = s.AutoPauseOnSwitch;
        ChkIslandAlwaysOnTop.IsChecked = s.IslandAlwaysOnTop;
        ChkShowEq.IsChecked = s.ShowEqualizerOnIsland;
        ChkCoverGlow.IsChecked = s.CoverGlowOnIsland;

        ChkEnableIslandAcrylic.IsChecked = s.EnableIslandAcrylic;
        ChkEnableIslandMarquee.IsChecked = s.EnableIslandMarquee;

        SliderScale.Value = s.IslandScale;
        TxtScaleValue.Text = $"{Math.Round(s.IslandScale * 100)}%";

        SliderOpacity.Value = s.IslandOpacity;
        TxtOpacityValue.Text = $"{Math.Round(s.IslandOpacity * 100)}%";

        TxtDownloadFolder.Text = s.DownloadFolder;

        ChkProxyEnabled.IsChecked = s.ProxyEnabled;
        ChkProxyBypassRu.IsChecked = s.ProxyBypassRuServices;
        SelectProxyType(s.ProxyType, adjustDefaultPort: false);
        TxtProxyHost.Text = s.ProxyHost;
        TxtProxyPort.Text = s.ProxyPort.ToString();
        TxtProxyUser.Text = s.ProxyUsername;
        SetProxyPassword(s.ProxyPassword);
        UpdateProxyStatusLabel(s);

        _isInitializing = false;
    }

    private void ChkAutoStart_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _settings.SetAutoStart(ChkAutoStart.IsChecked == true);
    }

    private void ToggleSetting_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        _settings.Update(s =>
        {
            s.StartMinimizedToIsland = ChkStartMinimized.IsChecked == true;
            s.AutoPauseOnSwitch = ChkAutoPause.IsChecked == true;
            s.IslandAlwaysOnTop = ChkIslandAlwaysOnTop.IsChecked == true;
            s.ShowEqualizerOnIsland = ChkShowEq.IsChecked == true;
            s.CoverGlowOnIsland = ChkCoverGlow.IsChecked == true;
            s.EnableIslandAcrylic = ChkEnableIslandAcrylic.IsChecked == true;
            s.EnableIslandMarquee = ChkEnableIslandMarquee.IsChecked == true;
        });
    }

    private void SliderScale_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        TxtScaleValue.Text = $"{Math.Round(e.NewValue * 100)}%";
        if (_isInitializing) return;

        _settings.Update(s => s.IslandScale = e.NewValue);
    }

    private void SliderOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        TxtOpacityValue.Text = $"{Math.Round(e.NewValue * 100)}%";
        if (_isInitializing) return;

        _settings.Update(s => s.IslandOpacity = e.NewValue);
    }

    private void ResetIslandPosition_Click(object sender, RoutedEventArgs e)
    {
        _resetIslandPosition();
    }

    private void OpenAccounts_Click(object sender, RoutedEventArgs e) => _openAccountsAction();

    private void SelectProxyType(string? rawType, bool adjustDefaultPort)
    {
        var prevDefault = ProxyService.NormalizePort(_selectedProxyType, 0);
        _selectedProxyType = ProxyService.NormalizeType(rawType);

        var activeBg = new SolidColorBrush(Color.FromRgb(255, 204, 0));
        var activeFg = new SolidColorBrush(Color.FromRgb(17, 17, 22));
        var normalBg = new SolidColorBrush(Color.FromArgb(31, 255, 255, 255));
        var normalFg = Brushes.White;

        var buttons = new (Button Btn, string Key)[]
        {
            (BtnProxyTypeHttp, "http"),
            (BtnProxyTypeHttps, "https"),
            (BtnProxyTypeSocks5, "socks5"),
            (BtnProxyTypeSocks4, "socks4")
        };

        foreach (var (btn, key) in buttons)
        {
            bool active = _selectedProxyType == key;
            btn.Background = active ? activeBg : normalBg;
            btn.Foreground = active ? activeFg : normalFg;
        }

        if (adjustDefaultPort)
        {
            var curPortText = (TxtProxyPort.Text ?? "").Trim();
            if (string.IsNullOrWhiteSpace(curPortText) ||
                (int.TryParse(curPortText, out int p) && (p == prevDefault || p is 8080 or 1080 or 443)))
            {
                TxtProxyPort.Text = ProxyService.NormalizePort(_selectedProxyType, 0).ToString();
            }
        }
    }

    private string GetProxyPassword()
        => _isPasswordVisible ? (TxtProxyPassVisible.Text ?? "") : (TxtProxyPass.Password ?? "");

    private void SetProxyPassword(string? password)
    {
        _isSyncingPassword = true;
        var val = password ?? "";
        TxtProxyPass.Password = val;
        TxtProxyPassVisible.Text = val;
        _isSyncingPassword = false;
    }

    private void TxtProxyPass_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_isSyncingPassword) return;
        _isSyncingPassword = true;
        TxtProxyPassVisible.Text = TxtProxyPass.Password;
        _isSyncingPassword = false;
    }

    private void TxtProxyPassVisible_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_isSyncingPassword) return;
        _isSyncingPassword = true;
        TxtProxyPass.Password = TxtProxyPassVisible.Text;
        _isSyncingPassword = false;
    }

    private void ToggleProxyPassVisibility_Click(object sender, RoutedEventArgs e)
    {
        _isPasswordVisible = !_isPasswordVisible;
        if (_isPasswordVisible)
        {
            TxtProxyPassVisible.Text = TxtProxyPass.Password;
            TxtProxyPass.Visibility = Visibility.Collapsed;
            TxtProxyPassVisible.Visibility = Visibility.Visible;
            BtnToggleProxyPass.Foreground = new SolidColorBrush(Color.FromRgb(255, 204, 0));
        }
        else
        {
            TxtProxyPass.Password = TxtProxyPassVisible.Text;
            TxtProxyPassVisible.Visibility = Visibility.Collapsed;
            TxtProxyPass.Visibility = Visibility.Visible;
            BtnToggleProxyPass.Foreground = new SolidColorBrush(Color.FromRgb(136, 137, 156));
        }
    }

    private void ProxyTypeBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string typeTag)
            SelectProxyType(typeTag, adjustDefaultPort: true);
    }

    private void TryApplySmartPasteFromHostInput()
    {
        var raw = (TxtProxyHost.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(raw)) return;

        bool hasCompositeSyntax = raw.Contains("://", StringComparison.Ordinal) ||
                                  raw.Contains('@', StringComparison.Ordinal) ||
                                  (raw.Contains(':', StringComparison.Ordinal) && !raw.Contains("::", StringComparison.Ordinal));
        if (!hasCompositeSyntax) return;

        if (!ProxyService.TryParseProxyInput(raw, out var parsed) || string.IsNullOrWhiteSpace(parsed.Host))
            return;

        if (!string.IsNullOrWhiteSpace(parsed.Type))
            SelectProxyType(parsed.Type, adjustDefaultPort: false);

        TxtProxyHost.Text = parsed.Host;
        if (parsed.Port.HasValue)
            TxtProxyPort.Text = parsed.Port.Value.ToString();
        if (parsed.Username != null)
            TxtProxyUser.Text = parsed.Username;
        if (parsed.Password != null)
            SetProxyPassword(parsed.Password);
    }

    private void TxtProxyHost_LostFocus(object sender, RoutedEventArgs e)
        => TryApplySmartPasteFromHostInput();

    private void TxtProxyHost_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            TryApplySmartPasteFromHostInput();
            SaveProxySettings(requireHostIfEnabled: true);
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private void TxtProxyField_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SaveProxySettings(requireHostIfEnabled: true);
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private void ProxyToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        TryApplySmartPasteFromHostInput();
        SaveProxySettings(requireHostIfEnabled: false);
    }

    private void ProxyBypassRu_Changed(object sender, RoutedEventArgs e)
    {
        if (_isInitializing) return;
        SaveProxySettings(requireHostIfEnabled: false);
    }

    private void ApplyProxy_Click(object sender, RoutedEventArgs e)
    {
        TryApplySmartPasteFromHostInput();
        SaveProxySettings(requireHostIfEnabled: true);
    }

    private bool TryReadProxyInputs(out string type, out string host, out int port, out string user, out string pass, out string? error)
    {
        TryApplySmartPasteFromHostInput();
        type = ProxyService.NormalizeType(_selectedProxyType);
        host = (TxtProxyHost.Text ?? "").Trim();
        user = (TxtProxyUser.Text ?? "").Trim();
        pass = GetProxyPassword();
        error = null;

        var portRaw = (TxtProxyPort.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(portRaw))
        {
            port = ProxyService.NormalizePort(type, 0);
            TxtProxyPort.Text = port.ToString();
        }
        else if (!int.TryParse(portRaw, out port) || port < 1 || port > 65535)
        {
            port = 0;
            error = "Укажите корректный порт (от 1 до 65535).";
            return false;
        }

        if (string.IsNullOrWhiteSpace(host))
        {
            error = "Введите адрес прокси-сервера.";
            return false;
        }

        return true;
    }

    private void SaveProxySettings(bool requireHostIfEnabled)
    {
        if (_isInitializing) return;

        bool enabled = ChkProxyEnabled.IsChecked == true;
        bool bypassRu = ChkProxyBypassRu.IsChecked == true;
        TryApplySmartPasteFromHostInput();

        string type = ProxyService.NormalizeType(_selectedProxyType);
        string host = (TxtProxyHost.Text ?? "").Trim();
        string user = (TxtProxyUser.Text ?? "").Trim();
        string pass = GetProxyPassword();

        int port;
        var portRaw = (TxtProxyPort.Text ?? "").Trim();
        if (string.IsNullOrWhiteSpace(portRaw))
        {
            port = ProxyService.NormalizePort(type, 0);
            TxtProxyPort.Text = port.ToString();
        }
        else if (!int.TryParse(portRaw, out port) || port < 1 || port > 65535)
        {
            TxtProxyStatus.Text = "Ошибка: порт должен быть числом от 1 до 65535.";
            TxtProxyStatus.Foreground = new SolidColorBrush(Color.FromRgb(255, 110, 110));
            return;
        }

        if (enabled && string.IsNullOrWhiteSpace(host))
        {
            if (requireHostIfEnabled)
            {
                TxtProxyStatus.Text = "Введите адрес прокси-сервера перед применением.";
                TxtProxyStatus.Foreground = new SolidColorBrush(Color.FromRgb(255, 110, 110));
                return;
            }
        }

        _suppressNextExternalReload = true;
        _settings.Update(s =>
        {
            s.ProxyEnabled = enabled;
            s.ProxyType = type;
            s.ProxyHost = host;
            s.ProxyPort = port;
            s.ProxyUsername = user;
            s.ProxyPassword = pass;
            s.ProxyBypassRuServices = bypassRu;
        });

        UpdateProxyStatusLabel(_settings.Current);
    }

    private void UpdateProxyStatusLabel(AppSettings s)
    {
        if (!s.ProxyEnabled)
        {
            if (!string.IsNullOrWhiteSpace(s.ProxyHost))
            {
                TxtProxyStatus.Text = $"Сохранено ({s.ProxyType.ToUpperInvariant()} {s.ProxyHost}:{s.ProxyPort}), но прокси выключен.";
                TxtProxyStatus.Foreground = new SolidColorBrush(Color.FromRgb(255, 204, 0));
            }
            else
            {
                TxtProxyStatus.Text = "Прокси выключен — используется системное подключение.";
                TxtProxyStatus.Foreground = new SolidColorBrush(Color.FromRgb(136, 137, 156));
            }
            return;
        }

        if (string.IsNullOrWhiteSpace(s.ProxyHost))
        {
            TxtProxyStatus.Text = "Укажите адрес прокси-сервера и нажмите «Применить».";
            TxtProxyStatus.Foreground = new SolidColorBrush(Color.FromRgb(255, 204, 0));
            return;
        }

        string ruNote = s.ProxyBypassRuServices ? " • Яндекс и ВК напрямую" : " • весь трафик";
        TxtProxyStatus.Text = $"Активен: {s.ProxyType.ToUpperInvariant()} {s.ProxyHost}:{s.ProxyPort}{ruNote}";
        TxtProxyStatus.Foreground = new SolidColorBrush(Color.FromRgb(110, 224, 145));
    }

    private async void TestProxy_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadProxyInputs(out var type, out var host, out var port, out var user, out var pass, out var err))
        {
            TxtProxyStatus.Text = err!;
            TxtProxyStatus.Foreground = new SolidColorBrush(Color.FromRgb(255, 110, 110));
            return;
        }

        _proxyTestCts?.Cancel();
        _proxyTestCts = new CancellationTokenSource();
        var ct = _proxyTestCts.Token;

        BtnTestProxy.IsEnabled = false;
        BtnTestProxy.Content = "Проверка...";
        TxtProxyStatus.Text = $"Проверка {type.ToUpperInvariant()} {host}:{port}...";
        TxtProxyStatus.Foreground = new SolidColorBrush(Color.FromRgb(160, 162, 180));

        try
        {
            var (ok, message) = await ProxyService.TestProxyAsync(type, host, port, user, pass, ct);
            if (!ct.IsCancellationRequested)
            {
                TxtProxyStatus.Text = message;
                TxtProxyStatus.Foreground = ok
                    ? new SolidColorBrush(Color.FromRgb(110, 224, 145))
                    : new SolidColorBrush(Color.FromRgb(255, 110, 110));
            }
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                BtnTestProxy.IsEnabled = true;
                BtnTestProxy.Content = "Проверить";
            }
        }
    }

    private void TxtDownloadFolder_LostFocus(object sender, RoutedEventArgs e) => SaveDownloadFolder();

    private void TxtDownloadFolder_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            SaveDownloadFolder();
            Keyboard.ClearFocus();
            e.Handled = true;
        }
    }

    private void SaveDownloadFolder()
    {
        if (_isInitializing) return;
        var path = TxtDownloadFolder.Text.Trim();
        if (string.IsNullOrWhiteSpace(path))
            path = AppSettings.DefaultDownloadFolder;

        _settings.Update(s => s.DownloadFolder = path, notify: false);
        TxtDownloadFolder.Text = _settings.Current.DownloadFolder;
    }

    private void OpenDownloadFolder_Click(object sender, RoutedEventArgs e)
    {
        SaveDownloadFolder();
        try
        {
            var folder = _settings.Current.DownloadFolder;
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{folder}\"",
                UseShellExecute = true
            });
        }
        catch { }
    }

    private async void CheckYtDlp_Click(object sender, RoutedEventArgs e)
    {
        await RunYtDlpCheckAsync();
    }

    private async Task RunYtDlpCheckAsync()
    {
        BtnCheckYtDlp.IsEnabled = false;
        TxtYtDlpStatus.Text = "Проверка наличия yt-dlp --version...";
        TxtYtDlpStatus.Foreground = new SolidColorBrush(Color.FromRgb(160, 162, 180));

        var (ok, info) = await YtDlpMusicProvider.CheckYtDlpAsync();
        TxtYtDlpStatus.Text = info;
        TxtYtDlpStatus.Foreground = ok
            ? new SolidColorBrush(Color.FromRgb(110, 224, 145))
            : new SolidColorBrush(Color.FromRgb(255, 110, 110));
        BtnCheckYtDlp.IsEnabled = true;
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
        => await RunAppUpdateCheckAsync();

    private async Task RunAppUpdateCheckAsync()
    {
        BtnCheckUpdate.IsEnabled = false;
        BtnInstallUpdate.IsEnabled = false;
        TxtUpdateStatus.Text = "Проверка последней версии на GitHub...";
        TxtUpdateStatus.Foreground = new SolidColorBrush(Color.FromRgb(160, 162, 180));

        var res = await AppUpdateService.CheckForUpdateAsync();
        TxtUpdateStatus.Text = string.IsNullOrWhiteSpace(res.CommitMessage) || !res.IsUpdateAvailable
            ? res.StatusMessage
            : $"{res.StatusMessage} {res.CommitMessage}";

        if (!res.CheckedOk)
        {
            TxtUpdateStatus.Foreground = new SolidColorBrush(Color.FromRgb(255, 110, 110));
            BtnInstallUpdate.Content = "Обновить с GitHub";
            BtnInstallUpdate.IsEnabled = false;
            BtnInstallUpdate.Background = new SolidColorBrush(Color.FromArgb(31, 255, 255, 255));
            BtnInstallUpdate.Foreground = Brushes.White;
        }
        else if (res.IsUpdateAvailable)
        {
            TxtUpdateStatus.Foreground = new SolidColorBrush(Color.FromRgb(255, 204, 0));
            BtnInstallUpdate.Content = "Обновить с GitHub";
            BtnInstallUpdate.IsEnabled = true;
            BtnInstallUpdate.Background = new SolidColorBrush(Color.FromRgb(255, 204, 0));
            BtnInstallUpdate.Foreground = new SolidColorBrush(Color.FromRgb(17, 17, 22));
        }
        else
        {
            TxtUpdateStatus.Foreground = new SolidColorBrush(Color.FromRgb(110, 224, 145));
            BtnInstallUpdate.Content = "Версия актуальна";
            BtnInstallUpdate.IsEnabled = false;
            BtnInstallUpdate.Background = new SolidColorBrush(Color.FromArgb(31, 255, 255, 255));
            BtnInstallUpdate.Foreground = Brushes.White;
        }

        BtnCheckUpdate.IsEnabled = true;
    }

    private async void InstallUpdate_Click(object sender, RoutedEventArgs e)
    {
        BtnCheckUpdate.IsEnabled = false;
        BtnInstallUpdate.IsEnabled = false;
        PbUpdateProgress.Value = 0;
        PbUpdateProgress.Visibility = Visibility.Visible;
        TxtUpdateStatus.Foreground = new SolidColorBrush(Color.FromRgb(255, 204, 0));

        var (ok, err) = await AppUpdateService.DownloadAndApplyUpdateAsync((pct, msg) =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                PbUpdateProgress.Value = pct;
                TxtUpdateStatus.Text = msg;
            });
        });

        if (!ok)
        {
            PbUpdateProgress.Visibility = Visibility.Collapsed;
            TxtUpdateStatus.Text = err;
            TxtUpdateStatus.Foreground = new SolidColorBrush(Color.FromRgb(255, 110, 110));
            BtnCheckUpdate.IsEnabled = true;
            BtnInstallUpdate.IsEnabled = true;
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { }
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        try { _proxyTestCts?.Cancel(); _proxyTestCts?.Dispose(); } catch { }
        _settings.SettingsChanged -= OnExternalSettingsChanged;
        base.OnClosed(e);
    }
}
