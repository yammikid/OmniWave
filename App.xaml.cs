using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using DynamicIsland.Services;

namespace DynamicIsland;

public partial class App : Application
{
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    private Mutex? _singleInstanceMutex;
    private uint _restoreMsgId;
    private SettingsService _settings = null!;
    private LibraryService _library = null!;
    private YandexMusicProvider _yandex = null!;
    private VkMusicProvider _vk = null!;
    private SpotifyMusicProvider _spotify = null!;
    private List<IMusicProvider> _providers = new();
    private DownloadService _downloads = null!;
    private AudioPlayerService _audioPlayer = null!;
    private MediaControllerService _controller = null!;
    private GlobalHotkeyService? _hotkeys;
    private TrayIconService? _trayIcon;

    private PlayerWindow? _playerWindow;
    private MenuItem? _trayAlwaysOnTopMenuItem;

    private bool _isShuttingDown;
    private bool _startupUpdateChecked;

    protected override void OnStartup(StartupEventArgs e)
    {
        WindowBlurHelper.EnablePerMonitorDpiAwareness();
        _restoreMsgId = WindowBlurHelper.RegisterRestoreMessage();

        _singleInstanceMutex = new Mutex(initiallyOwned: true, @"Local\OmniWave_SingleInstance_Mutex", out bool createdNew);
        if (!createdNew)
        {
            WindowBlurHelper.BroadcastRestoreMessage(_restoreMsgId);
            try { _singleInstanceMutex.Dispose(); } catch { }
            _singleInstanceMutex = null;
            Shutdown(0);
            return;
        }

        base.OnStartup(e);

        _settings = new SettingsService();
        _library = new LibraryService();

        var soundCloud = new SoundCloudFastProvider();
        var youTube = new YouTubeFastProvider();
        var audius = new AudiusProvider();
        var bandcamp = new BandcampProvider();
        var openCatalog = new OpenCatalogProvider();
        var radio = new RadioBrowserProvider();
        var web = new WebMusicProvider();
        _vk = new VkMusicProvider(_settings);
        _spotify = new SpotifyMusicProvider(_settings);
        _yandex = new YandexMusicProvider(_settings, new IMusicProvider[] { soundCloud, youTube, audius, _vk });
        _vk.SetFallbackProviders(new IMusicProvider[] { soundCloud, youTube, audius, _yandex });
        _spotify.SetFallbackProviders(new IMusicProvider[] { _yandex, _vk, soundCloud, youTube, audius });

        _providers = new List<IMusicProvider>
        {
            soundCloud,
            youTube,
            audius,
            bandcamp,
            openCatalog,
            radio,
            web,
            _yandex,
            _vk,
            _spotify
        };

        _downloads = new DownloadService(_providers, _settings);
        _audioPlayer = new AudioPlayerService(_providers, _downloads);
        _audioPlayer.SetWaveReplenishProvider(lastId => _yandex.FetchMyWaveTracksAsync(lastId));
        _audioPlayer.SetVkMixReplenishProvider(() => _vk.FetchVkMixTracksAsync());
        ApplySavedAudioSettings();

        _controller = new MediaControllerService(_audioPlayer, _settings, _providers);
        _controller.Start();

        _playerWindow = new PlayerWindow(
            _providers,
            _audioPlayer,
            _controller,
            _library,
            _settings,
            _downloads,
            _yandex,
            _vk,
            _spotify,
            OpenSettingsTab,
            OpenAccountsWindow,
            SyncExternalAccountsAsync,
            ShutdownApp,
            SyncTrayChecks,
            _restoreMsgId);

        MainWindow = _playerWindow;

        // Гарантируем создание HWND до привязки трея, Single-Instance hook и глобальных хоткеев
        _ = new WindowInteropHelper(_playerWindow).EnsureHandle();

        _hotkeys = new GlobalHotkeyService(() => _controller.IsLocalActive || _audioPlayer.CurrentTrack != null);
        _hotkeys.HotkeyTriggered += OnGlobalHotkeyTriggered;
        _hotkeys.Attach(_playerWindow);
        _hotkeys.SetVoiceHotkeyEnabled(_settings.Current.VoiceHotkeyEnabled);
        _settings.SettingsChanged += s => _ = Dispatcher.BeginInvoke(() => _hotkeys?.SetVoiceHotkeyEnabled(s.VoiceHotkeyEnabled));

        _trayIcon = new TrayIconService(_playerWindow, BuildTrayContextMenu(), ToggleMainWindowVisibility);
        SyncTrayChecks();

        AppUpdateService.CleanupOldUpdateArtifacts();

        if (!_settings.Current.StartMinimizedToIsland)
        {
            _playerWindow.Show();
            _playerWindow.Activate();
        }
        else
        {
            _playerWindow.HideToTray();
        }

        if (_yandex.IsAuthenticated || _vk.IsAuthenticated || _spotify.IsAuthenticated)
            _ = Task.Run(SyncExternalAccountsAsync);

        if (!_startupUpdateChecked)
        {
            _startupUpdateChecked = true;
            _ = CheckForAppUpdateOnStartupAsync();
        }
    }

    private void ApplySavedAudioSettings()
    {
        var s = _settings.Current;
        _audioPlayer.SetVolume(s.Volume);
        _audioPlayer.SetRate(s.PlaybackRate);
        _audioPlayer.ApplyEqualizer(s.EqualizerEnabled, s.EqualizerPreamp, s.EqualizerBands);
    }

    private ContextMenu BuildTrayContextMenu()
    {
        var menu = new ContextMenu
        {
            Style = (Style)FindResource("DarkContextMenu")
        };
        var itemStyle = (Style)FindResource("DarkMenuItem");
        var sepStyle = (Style)FindResource("DarkMenuSeparator");

        var miShowHide = new MenuItem { Header = "Показать / Скрыть", Style = itemStyle };
        miShowHide.Click += (_, _) => ToggleMainWindowVisibility();

        _trayAlwaysOnTopMenuItem = new MenuItem
        {
            Header = "Поверх всех окон",
            IsCheckable = true,
            IsChecked = _settings.Current.WindowAlwaysOnTop,
            Style = itemStyle
        };
        _trayAlwaysOnTopMenuItem.Click += (_, _) =>
        {
            _playerWindow?.SetAlwaysOnTop(_trayAlwaysOnTopMenuItem.IsChecked, save: true);
        };

        var miPlayPause = new MenuItem { Header = "Пуск / Пауза", Style = itemStyle };
        miPlayPause.Click += (_, _) => OnGlobalHotkeyTriggered(HotkeyAction.PlayPause);

        var miNext = new MenuItem { Header = "Следующий трек", Style = itemStyle };
        miNext.Click += (_, _) => OnGlobalHotkeyTriggered(HotkeyAction.NextTrack);

        var miPrev = new MenuItem { Header = "Предыдущий трек", Style = itemStyle };
        miPrev.Click += (_, _) => OnGlobalHotkeyTriggered(HotkeyAction.PrevTrack);

        var miSettings = new MenuItem { Header = "Настройки (прокси и обновления)", Style = itemStyle };
        miSettings.Click += (_, _) => _playerWindow?.ShowSettingsSection("settings");

        var miAccounts = new MenuItem { Header = "Аккаунты", Style = itemStyle };
        miAccounts.Click += (_, _) => _playerWindow?.ShowSettingsSection("accounts");

        var miExit = new MenuItem { Header = "Выход", Style = itemStyle };
        miExit.Click += (_, _) => ShutdownApp();

        menu.Items.Add(miShowHide);
        menu.Items.Add(_trayAlwaysOnTopMenuItem);
        menu.Items.Add(new Separator { Style = sepStyle });
        menu.Items.Add(miPlayPause);
        menu.Items.Add(miNext);
        menu.Items.Add(miPrev);
        menu.Items.Add(new Separator { Style = sepStyle });
        menu.Items.Add(miSettings);
        menu.Items.Add(miAccounts);
        menu.Items.Add(new Separator { Style = sepStyle });
        menu.Items.Add(miExit);
        return menu;
    }

    public void SyncTrayChecks()
    {
        if (_trayAlwaysOnTopMenuItem != null)
            _trayAlwaysOnTopMenuItem.IsChecked = _settings.Current.WindowAlwaysOnTop;
    }

    private void ToggleMainWindowVisibility()
    {
        if (_playerWindow == null) return;
        if (_playerWindow.IsVisible && _playerWindow.WindowState != WindowState.Minimized)
            _playerWindow.HideToTray();
        else
            _playerWindow.RestoreFromTray();
    }

    private void OpenSettingsTab()
        => _playerWindow?.ShowSettingsSection("proxy");

    private void OpenAccountsWindow()
        => _playerWindow?.ShowSettingsSection("accounts");

    private async Task<string> SyncExternalAccountsAsync()
    {
        await _syncLock.WaitAsync();
        try
        {
            var parts = new List<string>();

            if (_yandex.IsAuthenticated)
            {
                try
                {
                    var yandexPls = await _yandex.FetchUserPlaylistsAsync();
                    _library.ReplaceExternalPlaylists("yandex", yandexPls);
                    parts.Add($"Яндекс: {yandexPls.Count}");
                }
                catch
                {
                    parts.Add("Яндекс: ошибка");
                }
            }

            if (_vk.IsAuthenticated)
            {
                try
                {
                    var vkPls = await _vk.FetchUserPlaylistsAsync();
                    _library.ReplaceExternalPlaylists("vk", vkPls);
                    parts.Add($"ВК: {vkPls.Count}");
                }
                catch
                {
                    parts.Add("ВК: ошибка");
                }
            }

            if (_spotify.IsAuthenticated)
            {
                try
                {
                    var spPls = await _spotify.FetchUserPlaylistsAsync();
                    _library.ReplaceExternalPlaylists("spotify", spPls);
                    parts.Add($"Spotify: {spPls.Count}");
                }
                catch
                {
                    parts.Add("Spotify: ошибка");
                }
            }

            return parts.Count > 0
                ? $"Синхронизировано плейлистов — {string.Join(", ", parts)}"
                : "Подключите Яндекс Музыку, ВК или Spotify";
        }
        finally
        {
            _syncLock.Release();
        }
    }

    private async Task CheckForAppUpdateOnStartupAsync()
    {
        try
        {
            await Task.Delay(1500);
            var res = await AppUpdateService.CheckForUpdateAsync();
            if (res.CheckedOk && res.IsUpdateAvailable && !_isShuttingDown)
            {
                _ = Dispatcher.BeginInvoke(() =>
                {
                    if (!_isShuttingDown)
                        AppUpdateService.ShowStartupUpdateDialog(res);
                });
            }
        }
        catch { }
    }

    private async void OnGlobalHotkeyTriggered(HotkeyAction action)
    {
        if (_isShuttingDown) return;
        bool hasExternal = _controller.CurrentMediaState?.ActiveId != null;

        switch (action)
        {
            case HotkeyAction.PlayPause:
                if (_controller.IsLocalActive || (_audioPlayer.CurrentTrack != null && !hasExternal))
                    _audioPlayer.TogglePause();
                else
                    await _controller.SendCommandAsync("toggle");
                break;

            case HotkeyAction.Stop:
                if (_controller.IsLocalActive || _audioPlayer.CurrentTrack != null)
                    _audioPlayer.Pause();
                else
                    await _controller.SendCommandAsync("pause");
                break;

            case HotkeyAction.NextTrack:
                if (_controller.IsLocalActive || (_audioPlayer.CurrentTrack != null && !hasExternal))
                    await _audioPlayer.NextAsync();
                else
                    await _controller.SendCommandAsync("next");
                break;

            case HotkeyAction.PrevTrack:
                if (_controller.IsLocalActive || (_audioPlayer.CurrentTrack != null && !hasExternal))
                    await _audioPlayer.PrevAsync();
                else
                    await _controller.SendCommandAsync("prev");
                break;

            case HotkeyAction.VolumeUp:
                _controller.AdjustVolumeStep(1);
                break;

            case HotkeyAction.VolumeDown:
                _controller.AdjustVolumeStep(-1);
                break;

            case HotkeyAction.VoiceAssistant:
                _playerWindow?.TriggerVoiceAssistantFromHotkey();
                break;
        }
    }

    private void ShutdownApp()
    {
        if (_isShuttingDown) return;
        _isShuttingDown = true;

        if (_playerWindow != null)
        {
            _playerWindow.AllowClose = true;
            try { _playerWindow.Close(); } catch { }
        }

        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _isShuttingDown = true;
        try { _hotkeys?.Dispose(); } catch { }
        try { _trayIcon?.Dispose(); } catch { }
        try { _syncLock.Dispose(); } catch { }
        try { _controller?.Dispose(); } catch { }
        try { _audioPlayer?.Dispose(); } catch { }
        try { _downloads?.Dispose(); } catch { }
        foreach (var provider in _providers)
        {
            if (provider is IDisposable d)
            {
                try { d.Dispose(); } catch { }
            }
        }
        ProxyService.Shutdown();
        if (_singleInstanceMutex != null)
        {
            try { _singleInstanceMutex.ReleaseMutex(); } catch { }
            try { _singleInstanceMutex.Dispose(); } catch { }
            _singleInstanceMutex = null;
        }
        base.OnExit(e);
    }
}
