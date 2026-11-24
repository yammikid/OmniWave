using System.Diagnostics;
using System.IO;
using System.Windows;
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
        _settings.SettingsChanged -= OnExternalSettingsChanged;
        base.OnClosed(e);
    }
}
