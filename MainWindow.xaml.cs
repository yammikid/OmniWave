using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DynamicIsland.Services;
using MediaState = DynamicIsland.Services.MediaState;

namespace DynamicIsland;

public partial class MainWindow : Window
{
    private enum DockEdge
    {
        None,
        Left,
        Right,
        Top,
        Bottom
    }

    private const double BaseCompactW = 296, BaseCompactH = 46;
    private const double BaseExpandedW = 460, BaseExpandedH = 224;
    private const double GlowPad = 14;
    private const double DockSideW = 22, DockSidePeekW = 32, DockSideH = 90;
    private const double DockHorizW = 104, DockHorizH = 22, DockHorizPeekH = 32;
    private const double MagneticSideThreshold = 30;
    private const double MagneticTopThreshold = 6;

    private static readonly double[] SpectrumEnvelope =
    {
        0.42, 0.56, 0.72, 0.86, 0.96, 1.00, 1.00, 0.94, 0.82, 0.68, 0.52, 0.40
    };

    private readonly SettingsService _settings = new();
    private readonly LibraryService _library = new();
    private readonly LyricsService _lyrics = new();
    private readonly YandexMusicProvider _yandex;
    private readonly VkMusicProvider _vk;
    private readonly SpotifyMusicProvider _spotify;
    private readonly List<IMusicProvider> _providers;
    private readonly DownloadService _downloads;
    private readonly AudioPlayerService _audioPlayer;
    private readonly MediaControllerService _controller;
    private readonly GlobalHotkeyService _hotkeys;
    private readonly TrayIconService _trayIcon;
    private readonly Border[] _compactEqBars;
    private readonly Border[] _expandedEqBars;
    private readonly DispatcherTimer _collapseTimer;
    private readonly DispatcherTimer _eqTimer;
    private readonly DispatcherTimer _actionToastTimer;
    private readonly SemaphoreSlim _syncLock = new(1, 1);

    private PlayerWindow? _playerWindow;
    private SettingsWindow? _settingsWindow;
    private AccountWindow? _accountWindow;
    private MediaState? _lastMediaState;

    private bool _isExpanded;
    private bool _isPinned;
    private bool _isDragging;
    private bool _isMenuOpen;
    private bool _isPlaying;
    private double _anchorCenterX;
    private double _anchorTopY;
    private DockEdge _dockEdge = DockEdge.None;
    private bool _isDockedPeeked;
    private double _dockOffset;
    private long _undockSuppressExpandUntil;
    private string _lastSourcesSig = "";
    private double _eqPhase;
    private Color _currentAccentColor = Color.FromRgb(255, 204, 0);
    private bool _hasActiveCover;
    private bool _updatingVolumeSlider;
    private int _savedIslandVolumeBeforeMute = 80;
    private bool _isScrubbingIslandProgress;
    private MenuItem? _trayAlwaysOnTopMenuItem;
    private HwndSource? _islandHwndSource;
    private bool _isChangingZOrderBand;
    private bool _isFoldedForFullscreen;
    private int _fullscreenAnimGen;
    private bool _isClosing;
    private int _seekPreviewHideGen;
    private long _islandScrubStartMs;
    private bool _startupUpdateChecked;

    public MainWindow()
    {
        InitializeComponent();

        _compactEqBars = new[]
        {
            CEq0, CEq1, CEq2, CEq3, CEq4, CEq5, CEq6, CEq7, CEq8, CEq9, CEq10, CEq11
        };
        _expandedEqBars = new[]
        {
            EEq0, EEq1, EEq2, EEq3, EEq4, EEq5, EEq6, EEq7, EEq8, EEq9, EEq10, EEq11
        };

        _collapseTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(260) };
        _collapseTimer.Tick += (_, _) =>
        {
            _collapseTimer.Stop();
            if (!IsMouseOver && !_isPinned && !_isDragging && !_isMenuOpen)
                SetExpanded(false);
        };

        _eqTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(70) };
        _eqTimer.Tick += EqTimer_Tick;

        _actionToastTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        _actionToastTimer.Tick += (_, _) =>
        {
            _actionToastTimer.Stop();
            ActionPillToast.Visibility = Visibility.Collapsed;
        };

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
        SyncIslandVolumeUi(_audioPlayer.Volume);
        _audioPlayer.PositionChanged += OnLocalPositionChanged;
        _audioPlayer.RepeatModeChanged += mode => Dispatcher.BeginInvoke(() => UpdateRepeatButton(mode));
        _audioPlayer.TrackChanged += _ => Dispatcher.BeginInvoke(UpdateIslandLikeButton);
        _audioPlayer.VolumeChanged += vol => Dispatcher.BeginInvoke(() => SyncIslandVolumeUi(vol));
        _library.LibraryChanged += () => Dispatcher.BeginInvoke(UpdateIslandLikeButton);
        UpdateRepeatButton(_audioPlayer.RepeatMode);

        _controller = new MediaControllerService(_audioPlayer, _settings, _providers);
        _controller.StateChanged += state => Dispatcher.BeginInvoke(() => ApplyState(state));
        _controller.Start();

        _hotkeys = new GlobalHotkeyService(() => _controller.IsLocalActive || _audioPlayer.CurrentTrack != null);
        _hotkeys.HotkeyTriggered += OnGlobalHotkeyTriggered;

        _trayIcon = new TrayIconService(this, BuildTrayContextMenu(), OpenPlayerWindow);

        _settings.SettingsChanged += _ => Dispatcher.BeginInvoke(ApplyVisualSettings);

        if (IslandBorder.ContextMenu != null)
        {
            IslandBorder.ContextMenu.Opened += (_, _) => _isMenuOpen = true;
            IslandBorder.ContextMenu.Closed += (_, _) =>
            {
                _isMenuOpen = false;
                if (_dockEdge != DockEdge.None)
                {
                    if (!IsMouseOver) SetDockedPeek(false);
                    return;
                }
                if (!IsMouseOver && !_isPinned && !_isDragging)
                    _collapseTimer.Start();
            };
        }

        RestoreSavedGeometry();
        ApplyVisualSettings();

        Loaded += (_, _) =>
        {
            AppUpdateService.CleanupOldUpdateArtifacts();

            if (_dockEdge != DockEdge.None)
                ApplyDockedGeometry(animate: false);
            else if (_isPinned)
                SetExpanded(true);

            if (!_settings.Current.StartMinimizedToIsland)
                OpenPlayerWindow();

            if (_yandex.IsAuthenticated || _vk.IsAuthenticated || _spotify.IsAuthenticated)
                _ = Task.Run(SyncExternalAccountsAsync);

            if (!_startupUpdateChecked)
            {
                _startupUpdateChecked = true;
                _ = CheckForAppUpdateOnStartupAsync();
            }
        };
    }

    private async Task CheckForAppUpdateOnStartupAsync()
    {
        try
        {
            await Task.Delay(1500);
            var res = await AppUpdateService.CheckForUpdateAsync();
            if (res.CheckedOk && res.IsUpdateAvailable && !_isClosing)
            {
                _ = Dispatcher.BeginInvoke(() =>
                {
                    if (!_isClosing)
                        AppUpdateService.ShowStartupUpdateDialog(res);
                });
            }
        }
        catch { }
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

        var miOpen = new MenuItem { Header = "Открыть OmniWave", Style = itemStyle };
        miOpen.Click += (_, _) => OpenPlayerWindow();

        var miSettings = new MenuItem { Header = "Настройки", Style = itemStyle };
        miSettings.Click += (_, _) => OpenSettingsWindow();

        _trayAlwaysOnTopMenuItem = new MenuItem
        {
            Header = "Остров поверх всех окон",
            IsCheckable = true,
            IsChecked = _settings.Current.IslandAlwaysOnTop,
            Style = itemStyle
        };
        _trayAlwaysOnTopMenuItem.Click += (_, _) => SetAlwaysOnTop(_trayAlwaysOnTopMenuItem.IsChecked, showToast: false);

        var miCenter = new MenuItem { Header = "Вернуть Остров в центр", Style = itemStyle };
        miCenter.Click += (_, _) => ResetAnchorToTopCenter();

        var miExit = new MenuItem { Header = "Выход", Style = itemStyle };
        miExit.Click += (_, _) => Close();

        menu.Items.Add(miOpen);
        menu.Items.Add(miSettings);
        menu.Items.Add(_trayAlwaysOnTopMenuItem);
        menu.Items.Add(miCenter);
        menu.Items.Add(new Separator { Style = sepStyle });
        menu.Items.Add(miExit);
        return menu;
    }

    private void OpenPlayerWindow()
    {
        if (_playerWindow == null)
        {
            _playerWindow = new PlayerWindow(
                _providers,
                _audioPlayer,
                _controller,
                _library,
                _lyrics,
                _settings,
                _downloads,
                _yandex,
                _vk,
                _spotify,
                OpenSettingsWindow,
                OpenAccountsWindow,
                SyncExternalAccountsAsync);
            _playerWindow.StateChanged += (_, _) => UpdateIslandFullscreenVisibility();
            _playerWindow.IsVisibleChanged += (_, _) => UpdateIslandFullscreenVisibility();
            _playerWindow.Closed += (_, _) => UpdateIslandFullscreenVisibility();
        }

        _playerWindow.Show();
        if (_playerWindow.WindowState == WindowState.Minimized)
            _playerWindow.WindowState = WindowState.Normal;
        _playerWindow.Activate();
        UpdateIslandFullscreenVisibility();
    }

    private void OpenSettingsWindow()
    {
        if (_settingsWindow != null && _settingsWindow.IsVisible)
        {
            if (_settingsWindow.WindowState == WindowState.Minimized)
                _settingsWindow.WindowState = WindowState.Normal;
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_settings, () => ResetAnchorToTopCenter(), OpenAccountsWindow);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
        _settingsWindow.Activate();
    }

    private void OpenAccountsWindow()
    {
        if (_accountWindow != null && _accountWindow.IsVisible)
        {
            if (_accountWindow.WindowState == WindowState.Minimized)
                _accountWindow.WindowState = WindowState.Normal;
            _accountWindow.Activate();
            return;
        }

        _accountWindow = new AccountWindow(_settings, _yandex, _vk, _spotify, _library, SyncExternalAccountsAsync);
        _accountWindow.Closed += (_, _) => _accountWindow = null;
        _accountWindow.Show();
        _accountWindow.Activate();
    }

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

    private void RestoreSavedGeometry()
    {
        var s = _settings.Current;
        _isPinned = s.PinIslandExpanded;
        SyncPinVisual();

        if (Enum.TryParse<DockEdge>(s.IslandDockEdge, ignoreCase: true, out var savedEdge) &&
            savedEdge != DockEdge.None)
        {
            var area = SystemParameters.WorkArea;
            _anchorCenterX = s.AnchorCenterX ?? (area.Left + area.Width / 2.0);
            _anchorTopY = s.AnchorTopY ?? (area.Top + 12);
            _dockOffset = s.IslandDockOffset ?? (savedEdge is DockEdge.Left or DockEdge.Right
                ? area.Top + area.Height / 2.0
                : area.Left + area.Width / 2.0);
            DockToEdge(savedEdge, animate: false, save: false);
            return;
        }

        if (s.AnchorCenterX.HasValue && s.AnchorTopY.HasValue)
        {
            _anchorCenterX = s.AnchorCenterX.Value;
            _anchorTopY = s.AnchorTopY.Value;
            ApplyScaleAndPosition(animate: false);
        }
        else
        {
            ResetAnchorToTopCenter(save: false);
        }
    }

    private void ResetAnchorToTopCenter(bool save = true)
    {
        if (_dockEdge != DockEdge.None)
        {
            _dockEdge = DockEdge.None;
            _isDockedPeeked = false;
            ApplyDockedVisualMode(DockEdge.None);
        }

        var area = SystemParameters.WorkArea;
        _anchorCenterX = area.Left + area.Width / 2.0;
        _anchorTopY = area.Top + 12;

        ApplyScaleAndPosition(animate: false);

        if (save)
        {
            _settings.Update(s =>
            {
                s.AnchorCenterX = null;
                s.AnchorTopY = null;
                s.IslandDockEdge = "None";
                s.IslandDockOffset = null;
            }, notify: false);
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        _islandHwndSource = HwndSource.FromHwnd(hwnd);
        _islandHwndSource?.AddHook(IslandZOrderWndProc);
        ApplyAlwaysOnTopState();
        _hotkeys.Attach(this);
    }

    private void ApplyVisualSettings()
    {
        var s = _settings.Current;
        ApplyAlwaysOnTopState();
        double baseAlpha = s.EnableIslandAcrylic ? 152.0 : 230.0;
        byte bgAlpha = (byte)Math.Clamp(Math.Round(baseAlpha * (s.IslandOpacity / 0.94)), 85, 255);
        byte r = (byte)(s.EnableIslandAcrylic ? 14 : 16);
        byte g = (byte)(s.EnableIslandAcrylic ? 18 : 19);
        byte b = (byte)(s.EnableIslandAcrylic ? 28 : 28);
        IslandBorder.Background = new SolidColorBrush(Color.FromArgb(bgAlpha, r, g, b));
        IslandGlassSheen.Visibility = s.EnableIslandAcrylic ? Visibility.Visible : Visibility.Collapsed;

        if (_dockEdge != DockEdge.None)
        {
            IslandScaleTransform.ScaleX = 1.0;
            IslandScaleTransform.ScaleY = 1.0;
            ApplyDockedGeometry(animate: false);
        }
        else
        {
            IslandScaleTransform.ScaleX = s.IslandScale;
            IslandScaleTransform.ScaleY = s.IslandScale;
            ApplyScaleAndPosition(animate: false);
        }

        ApplyIslandGlow();
        UpdateEqVisualState();
        UpdateIslandLikeButton();
        UpdateAllMarquees();
    }

    private void ApplyScaleAndPosition(bool animate)
    {
        if (_dockEdge != DockEdge.None)
        {
            ApplyDockedGeometry(animate);
            return;
        }

        double scale = _settings.Current.IslandScale;
        double targetW = (_isExpanded ? BaseExpandedW : BaseCompactW) * scale + GlowPad * 2;
        double targetH = (_isExpanded ? BaseExpandedH : BaseCompactH) * scale + GlowPad * 2;

        double minX = SystemParameters.VirtualScreenLeft + 4;
        double maxX = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - targetW - 4;
        double minY = SystemParameters.VirtualScreenTop + 4;
        double maxY = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - targetH - 4;

        double targetLeft = Math.Clamp(_anchorCenterX - targetW / 2.0, minX, Math.Max(minX, maxX));
        double targetTop = Math.Clamp(_anchorTopY, minY, Math.Max(minY, maxY));

        if (!animate)
        {
            ClearWindowAnimations();
            Width = targetW;
            Height = targetH;
            Left = targetLeft;
            Top = targetTop;
            return;
        }

        AnimateProp(LeftProperty, Left, targetLeft, _isExpanded);
        AnimateProp(TopProperty, Top, targetTop, _isExpanded);
        AnimateProp(WidthProperty, Width, targetW, _isExpanded);
        AnimateProp(HeightProperty, Height, targetH, _isExpanded);
    }

    private void ClearWindowAnimations()
    {
        double l = Left, t = Top, w = Width, h = Height;
        BeginAnimation(LeftProperty, null);
        BeginAnimation(TopProperty, null);
        BeginAnimation(WidthProperty, null);
        BeginAnimation(HeightProperty, null);
        Left = l; Top = t; Width = w; Height = h;
    }

    private void SetExpanded(bool expand)
    {
        if (_dockEdge != DockEdge.None) return;
        if (_isExpanded == expand) return;
        _isExpanded = expand;

        CompactPanel.Visibility = expand ? Visibility.Collapsed : Visibility.Visible;
        ExpandedPanel.Visibility = expand ? Visibility.Visible : Visibility.Collapsed;
        var radius = new CornerRadius(expand ? 26 : 23);
        IslandBorder.CornerRadius = radius;
        IslandGlassSheen.CornerRadius = radius;
        ActionPillToast.CornerRadius = radius;

        ApplyScaleAndPosition(animate: true);
        _ = Dispatcher.BeginInvoke(UpdateAllMarquees, DispatcherPriority.Loaded);
    }

    private void AnimateProp(DependencyProperty prop, double from, double to, bool isExpanding = true)
    {
        SetValue(prop, to);
        IEasingFunction ease = isExpanding
            ? new BackEase { Amplitude = 0.16, EasingMode = EasingMode.EaseOut }
            : new CubicEase { EasingMode = EasingMode.EaseOut };

        var anim = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(isExpanding ? 290 : 230))
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop
        };
        BeginAnimation(prop, anim);
    }

    private void ApplyState(MediaState state)
    {
        _lastMediaState = state;
        _isPlaying = state.IsPlaying;

        string compactLabel = !string.IsNullOrWhiteSpace(state.Artist) &&
                              state.Title != "Ничего не играет" &&
                              !string.Equals(state.Artist, state.Title, StringComparison.OrdinalIgnoreCase)
            ? $"{state.Title} — {state.Artist}"
            : state.Title;

        bool titleChanged = CompactTitle.Text != compactLabel || ExpandedTitle.Text != state.Title;
        CompactTitle.Text = compactLabel;
        ExpandedTitle.Text = state.Title;
        ExpandedArtist.Text = state.Artist;
        BtnPlay.Content = state.IsPlaying ? "\uE769" : "\uE768";

        bool showProgress = state.ActiveId == MediaControllerService.LocalSourceId || state.TotalMs > 0;
        IslandProgressContainer.Visibility = showProgress ? Visibility.Visible : Visibility.Collapsed;
        if (state.ActiveId != MediaControllerService.LocalSourceId && state.TotalMs > 0 && !_isScrubbingIslandProgress)
        {
            UpdateIslandProgressBar(state.CurrentMs, state.TotalMs);
        }

        var (accentHex, fgHex) = GetBrandColors(state.ActiveBrand);
        var brandColor = ColorUtils.ParseHex(accentHex, Color.FromRgb(255, 204, 0));
        var brandFgColor = ColorUtils.ParseHex(fgHex, Colors.White);
        var friendlySource = !string.IsNullOrWhiteSpace(state.SourceBadgeText)
            ? state.SourceBadgeText
            : GetFriendlySourceLabel(state.ActiveBrand);

        ExpandedSourceBadge.Background = new SolidColorBrush(brandColor);
        ExpandedSourceBadgeText.Foreground = new SolidColorBrush(brandFgColor);
        ExpandedSourceBadgeText.Text = friendlySource;

        var activeDotBrush = state.IsPlaying
            ? new SolidColorBrush(brandColor)
            : new SolidColorBrush(Color.FromRgb(102, 102, 112));
        CompactDot.Fill = activeDotBrush;
        DockedPlayDot.Fill = activeDotBrush;
        DockedPlayDot.Opacity = state.IsPlaying ? 1.0 : 0.45;
        CompactSub.Text = friendlySource;

        RebuildSources(state.Sources, state.ActiveId);

        if (state.CoverUpdated)
        {
            var bmp = ColorUtils.BytesToBitmap(state.CoverBytes);
            if (bmp != null)
            {
                CompactCover.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
                ExpandedCover.Background = new ImageBrush(bmp) { Stretch = Stretch.UniformToFill };
                _currentAccentColor = ColorUtils.ExtractAmbientColor(bmp, brandColor);
                _hasActiveCover = true;
            }
            else
            {
                var placeholder = new SolidColorBrush(Color.FromRgb(42, 42, 53));
                CompactCover.Background = placeholder;
                ExpandedCover.Background = placeholder;
                _currentAccentColor = brandColor;
                _hasActiveCover = state.Sources.Count > 0 && state.ActiveBrand != "default";
            }

            ApplyIslandGlow();
        }

        UpdateEqVisualState();
        UpdateIslandLikeButton();
        if (titleChanged)
            UpdateAllMarquees();
    }

    private void ApplyIslandGlow()
    {
        var dur = TimeSpan.FromMilliseconds(350);

        if (_settings.Current.CoverGlowOnIsland && _hasActiveCover)
        {
            var c = _currentAccentColor;
            var borderBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1)
            };
            borderBrush.GradientStops.Add(new GradientStop(Color.FromArgb(150, 255, 255, 255), 0.0));
            borderBrush.GradientStops.Add(new GradientStop(Color.FromArgb(68, c.R, c.G, c.B), 0.45));
            borderBrush.GradientStops.Add(new GradientStop(Color.FromArgb(88, 255, 255, 255), 1.0));
            IslandBorder.BorderBrush = borderBrush;

            IslandGlowEffect.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.ColorProperty,
                new ColorAnimation(c, dur));
            IslandGlowEffect.BlurRadius = 20;
            IslandGlowEffect.Opacity = 0.48;

            var eqBrush = new SolidColorBrush(Color.FromRgb(
                (byte)Math.Clamp((int)c.R + 40, 120, 255),
                (byte)Math.Clamp((int)c.G + 40, 120, 255),
                (byte)Math.Clamp((int)c.B + 40, 120, 255)));
            SetSpectrumBrush(eqBrush);
            IslandProgressFill.Background = eqBrush;
            IslandProgressThumb.BorderBrush = eqBrush;
            IslandSeekPreviewText.Foreground = eqBrush;
            DockedAccentBar.Background = eqBrush;
            DockedAccentGlow.Color = c;
        }
        else
        {
            var borderBrush = new LinearGradientBrush
            {
                StartPoint = new Point(0, 0),
                EndPoint = new Point(1, 1)
            };
            borderBrush.GradientStops.Add(new GradientStop(Color.FromArgb(136, 255, 255, 255), 0.0));
            borderBrush.GradientStops.Add(new GradientStop(Color.FromArgb(56, 255, 255, 255), 0.5));
            borderBrush.GradientStops.Add(new GradientStop(Color.FromArgb(80, 255, 255, 255), 1.0));
            IslandBorder.BorderBrush = borderBrush;

            IslandGlowEffect.BeginAnimation(System.Windows.Media.Effects.DropShadowEffect.ColorProperty,
                new ColorAnimation(Colors.Black, dur));
            IslandGlowEffect.BlurRadius = 20;
            IslandGlowEffect.Opacity = 0.48;

            var goldBrush = new SolidColorBrush(Color.FromRgb(255, 204, 0));
            SetSpectrumBrush(goldBrush);
            IslandProgressFill.Background = goldBrush;
            IslandProgressThumb.BorderBrush = goldBrush;
            IslandSeekPreviewText.Foreground = goldBrush;
            DockedAccentBar.Background = goldBrush;
            DockedAccentGlow.Color = Color.FromRgb(255, 204, 0);
        }
    }

    private void SetSpectrumBrush(Brush brush)
    {
        foreach (var bar in _compactEqBars) bar.Background = brush;
        foreach (var bar in _expandedEqBars) bar.Background = brush;
    }

    private void UpdateEqVisualState()
    {
        bool showSpectrum = _settings.Current.ShowEqualizerOnIsland;
        CompactEqPanel.Visibility = showSpectrum ? Visibility.Visible : Visibility.Collapsed;
        ExpandedEqPanel.Visibility = showSpectrum ? Visibility.Visible : Visibility.Collapsed;

        if (_isPlaying && showSpectrum)
        {
            if (!_eqTimer.IsEnabled) _eqTimer.Start();
        }
        else
        {
            if (_eqTimer.IsEnabled) _eqTimer.Stop();
            for (int i = 0; i < 12; i++)
            {
                _compactEqBars[i].Height = 4.0;
                _expandedEqBars[i].Height = 4.0;
            }
        }
    }

    private void EqTimer_Tick(object? sender, EventArgs e)
    {
        _eqPhase += 0.32;
        for (int i = 0; i < 12; i++)
        {
            double wave = Math.Abs(Math.Sin(_eqPhase + i * 0.65) * Math.Cos(_eqPhase * 0.5 - i * 0.3));
            double level = Math.Clamp(SpectrumEnvelope[i] * (0.25 + wave * 0.85), 0.10, 1.0);

            _compactEqBars[i].Height = 4.0 + level * 14.0;
            _expandedEqBars[i].Height = 4.0 + level * 18.0;
        }
    }

    private void TitleClip_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateAllMarquees();

    private void UpdateAllMarquees()
    {
        UpdateTitleMarquee(CompactTitleClip, CompactTitle, CompactTitleTransform);
        UpdateTitleMarquee(ExpandedTitleClip, ExpandedTitle, ExpandedTitleTransform);
    }

    private void UpdateTitleMarquee(FrameworkElement clipContainer, TextBlock titleBlock, TranslateTransform transform)
    {
        transform.BeginAnimation(TranslateTransform.XProperty, null);

        if (!_settings.Current.EnableIslandMarquee)
        {
            titleBlock.TextTrimming = TextTrimming.CharacterEllipsis;
            if (clipContainer.ActualWidth > 10)
                titleBlock.MaxWidth = clipContainer.ActualWidth;
            transform.X = 0;
            return;
        }

        titleBlock.TextTrimming = TextTrimming.None;
        titleBlock.MaxWidth = double.PositiveInfinity;
        titleBlock.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));

        double textW = titleBlock.DesiredSize.Width;
        double boxW = clipContainer.ActualWidth;
        if (boxW <= 10 || textW <= boxW + 2)
        {
            transform.X = 0;
            return;
        }

        double shift = -(textW - boxW + 6);
        double scrollSecs = Math.Clamp((textW - boxW) / 32.0, 1.8, 8.0);

        var t1 = TimeSpan.FromSeconds(1.5);
        var t2 = t1 + TimeSpan.FromSeconds(scrollSecs);
        var t3 = t2 + TimeSpan.FromSeconds(1.5);
        var t4 = t3 + TimeSpan.FromSeconds(scrollSecs);

        var anim = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(t1)));
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(shift, KeyTime.FromTimeSpan(t2)));
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(shift, KeyTime.FromTimeSpan(t3)));
        anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(t4)));

        transform.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    private void OnLocalPositionChanged(long currentMs, long totalMs)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (!_controller.IsLocalActive || _isScrubbingIslandProgress) return;
            UpdateIslandProgressBar(currentMs, totalMs);
        });
    }

    private void UpdateIslandProgressBar(long currentMs, long totalMs)
    {
        IslandCurTime.Text = FormatTimeMs(currentMs);
        double trackW = IslandProgressTrack.ActualWidth;
        if (totalMs > 0)
        {
            IslandTotTime.Text = FormatTimeMs(totalMs);
            if (trackW > 0)
            {
                double ratio = Math.Clamp((double)currentMs / totalMs, 0.0, 1.0);
                double fillW = trackW * ratio;
                IslandProgressFill.Width = fillW;
                IslandThumbTransform.X = Math.Clamp(fillW - 6.0, -2.0, Math.Max(0.0, trackW - 10.0));
            }
        }
        else
        {
            IslandTotTime.Text = "0:00";
            IslandProgressFill.Width = 0;
            IslandThumbTransform.X = -2.0;
        }
    }

    private (long CurrentMs, long TotalMs) GetActiveIslandTimes()
    {
        if (_controller.IsLocalActive)
            return (_audioPlayer.CurrentTimeMs, _audioPlayer.TotalLengthMs);
        return (_lastMediaState?.CurrentMs ?? 0, _lastMediaState?.TotalMs ?? 0);
    }

    private static string FormatTimeMs(long ms)
    {
        if (ms < 0) ms = 0;
        var ts = TimeSpan.FromMilliseconds(ms);
        return ts.TotalHours >= 1 ? ts.ToString(@"h\:mm\:ss") : ts.ToString(@"m\:ss");
    }

    private double GetIslandProgressFraction(MouseEventArgs e)
    {
        if (IslandProgressTrack.ActualWidth <= 0) return 0;
        double x = e.GetPosition(IslandProgressTrack).X;
        return Math.Clamp(x / IslandProgressTrack.ActualWidth, 0.0, 1.0);
    }

    private double UpdateSeekPreviewAtPoint(MouseEventArgs e)
    {
        double trackW = IslandProgressTrack.ActualWidth;
        var (cur, total) = GetActiveIslandTimes();
        if (trackW <= 0 || total <= 0)
        {
            HideIslandSeekPreview();
            return 0;
        }

        double x = Math.Clamp(e.GetPosition(IslandProgressTrack).X, 0.0, trackW);
        double frac = x / trackW;
        long targetMs = (long)Math.Round(total * frac);
        long diffMs = targetMs - cur;
        string sign = diffMs >= 0 ? "+" : "-";
        string deltaStr = FormatTimeMs(Math.Abs(diffMs));

        IslandProgressHoverFill.Visibility = Visibility.Visible;
        IslandProgressHoverFill.Width = x;

        IslandSeekPreviewText.Text = Math.Abs(diffMs) >= 1000
            ? $"{FormatTimeMs(targetMs)} ({sign}{deltaStr})"
            : FormatTimeMs(targetMs);
        IslandSeekPreviewBadge.Visibility = Visibility.Visible;
        IslandSeekPreviewBadge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double badgeW = IslandSeekPreviewBadge.DesiredSize.Width;
        IslandSeekPreviewTransform.X = Math.Clamp(x - badgeW / 2.0, 0.0, Math.Max(0.0, trackW - badgeW));
        return frac;
    }

    private void HideIslandSeekPreview()
    {
        IslandSeekPreviewBadge.Visibility = Visibility.Collapsed;
        IslandProgressHoverFill.Visibility = Visibility.Collapsed;
        IslandProgressHoverFill.Width = 0;
    }

    private void IslandProgress_MouseEnter(object sender, MouseEventArgs e) => UpdateSeekPreviewAtPoint(e);

    private void IslandProgress_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_isScrubbingIslandProgress)
            HideIslandSeekPreview();
    }

    private void IslandProgress_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (IslandProgressTrack.ActualWidth <= 0) return;
        _isScrubbingIslandProgress = true;
        _islandScrubStartMs = GetActiveIslandTimes().CurrentMs;
        IslandProgressHitArea.CaptureMouse();
        double frac = UpdateSeekPreviewAtPoint(e);
        var (_, total) = GetActiveIslandTimes();
        if (total > 0)
            UpdateIslandProgressBar((long)(total * frac), total);
    }

    private void IslandProgress_MouseMove(object sender, MouseEventArgs e)
    {
        if (IslandProgressTrack.ActualWidth <= 0) return;
        double frac = UpdateSeekPreviewAtPoint(e);
        if (!_isScrubbingIslandProgress) return;
        var (_, total) = GetActiveIslandTimes();
        if (total > 0)
            UpdateIslandProgressBar((long)(total * frac), total);
    }

    private async void IslandProgress_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isScrubbingIslandProgress) return;
        e.Handled = true;
        _isScrubbingIslandProgress = false;
        if (IslandProgressHitArea.IsMouseCaptured)
            IslandProgressHitArea.ReleaseMouseCapture();

        double frac = UpdateSeekPreviewAtPoint(e);
        var (_, total) = GetActiveIslandTimes();
        await _controller.SeekFractionAsync(frac);
        if (total > 0)
        {
            long targetMs = (long)Math.Round(total * frac);
            long deltaMs = targetMs - _islandScrubStartMs;
            FlashIslandSeekPreview(targetMs, deltaMs, total);
            _playerWindow?.NotifySeekVisual(targetMs, deltaMs);
            ShowToast("\uE893", $"Перемотка → {FormatTimeMs(targetMs)}");
        }
        else if (!IslandProgressHitArea.IsMouseOver)
        {
            HideIslandSeekPreview();
        }
    }

    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Delta == 0) return;
        int steps = e.Delta > 0 ? 1 : -1;
        string toast = _controller.AdjustVolumeStep(steps);
        string label = toast.EndsWith("%") ? $"Громкость: {toast}" : toast;

        ShowToast("\uE767", label);
        e.Handled = true;
    }

    private async void OnGlobalHotkeyTriggered(HotkeyAction action)
    {
        switch (action)
        {
            case HotkeyAction.PlayPause:
                if (_controller.IsLocalActive || (_audioPlayer.CurrentTrack != null && _lastMediaState?.ActiveId == null))
                    _audioPlayer.TogglePause();
                else
                    await _controller.SendCommandAsync("toggle");
                if (!_isFoldedForFullscreen)
                    ShowToast(_isPlaying ? "\uE769" : "\uE768", _isPlaying ? "Пауза" : "Воспроизведение");
                break;

            case HotkeyAction.Stop:
                if (_controller.IsLocalActive || _audioPlayer.CurrentTrack != null)
                    _audioPlayer.Pause();
                else
                    await _controller.SendCommandAsync("pause");
                if (!_isFoldedForFullscreen)
                    ShowToast("\uE769", "Пауза");
                break;

            case HotkeyAction.NextTrack:
                if (_controller.IsLocalActive || (_audioPlayer.CurrentTrack != null && _lastMediaState?.ActiveId == null))
                    await _audioPlayer.NextAsync();
                else
                    await _controller.SendCommandAsync("next");
                if (!_isFoldedForFullscreen)
                    ShowToast("\uE893", "Следующий трек");
                break;

            case HotkeyAction.PrevTrack:
                if (_controller.IsLocalActive || (_audioPlayer.CurrentTrack != null && _lastMediaState?.ActiveId == null))
                    await _audioPlayer.PrevAsync();
                else
                    await _controller.SendCommandAsync("prev");
                if (!_isFoldedForFullscreen)
                    ShowToast("\uE892", "Предыдущий трек");
                break;

            case HotkeyAction.VolumeUp:
            {
                string toast = _controller.AdjustVolumeStep(1);
                if (!_isFoldedForFullscreen)
                    ShowToast("\uE767", toast.EndsWith("%") ? $"Громкость: {toast}" : toast);
                break;
            }

            case HotkeyAction.VolumeDown:
            {
                string toast = _controller.AdjustVolumeStep(-1);
                if (!_isFoldedForFullscreen)
                    ShowToast("\uE767", toast.EndsWith("%") ? $"Громкость: {toast}" : toast);
                break;
            }
        }
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;

        switch (key)
        {
            case Key.Space:
            case Key.K when mods == ModifierKeys.None:
                e.Handled = true;
                OnGlobalHotkeyTriggered(HotkeyAction.PlayPause);
                break;

            case Key.Right when mods == ModifierKeys.None:
                e.Handled = true;
                await SeekRelativeWithToastAsync(15000, "\uE893", "+15с");
                break;

            case Key.Left when mods == ModifierKeys.None:
                e.Handled = true;
                await SeekRelativeWithToastAsync(-15000, "\uE892", "-15с");
                break;

            case Key.Up when mods == ModifierKeys.None:
                e.Handled = true;
                OnGlobalHotkeyTriggered(HotkeyAction.VolumeUp);
                break;

            case Key.Down when mods == ModifierKeys.None:
                e.Handled = true;
                OnGlobalHotkeyTriggered(HotkeyAction.VolumeDown);
                break;

            case Key.L when mods == ModifierKeys.None:
                e.Handled = true;
                BtnIslandLike_Click(this, new RoutedEventArgs());
                break;

            case Key.R when mods == ModifierKeys.None:
                e.Handled = true;
                BtnRepeat_Click(this, new RoutedEventArgs());
                break;
        }
    }

    private void ShowToast(string glyph, string text)
    {
        ActionPillIcon.Text = glyph;
        ActionPillText.Text = text;
        ActionPillToast.Visibility = Visibility.Visible;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ActionPillToast.BeginAnimation(OpacityProperty, new DoubleAnimation(0.0, 1.0, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease });
        ToastScaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.90, 1.0, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease });
        ToastScaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.90, 1.0, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease });

        _actionToastTimer.Stop();
        _actionToastTimer.Start();
    }

    private void RebuildSources(List<SourceItem> sources, string? activeId)
    {
        var sig = string.Join(";", sources.Select(s => $"{s.SourceId}:{s.Name}")) + "|" + activeId;
        if (sig == _lastSourcesSig) return;
        _lastSourcesSig = sig;

        SourcesPanel.Children.Clear();
        if (sources.Count == 0)
        {
            SourcesPanel.Children.Add(new TextBlock
            {
                Text = "Нет активных источников",
                Foreground = new SolidColorBrush(Color.FromRgb(120, 120, 134)),
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center
            });
            return;
        }

        var conv = new BrushConverter();
        foreach (var src in sources.Take(5))
        {
            bool isActive = src.SourceId == activeId;
            var (bgHex, fgHex) = GetBrandColors(src.Brand);

            var btn = new Button
            {
                Content = src.Name,
                Margin = new Thickness(0, 0, 5, 0),
                Padding = new Thickness(9, 3, 9, 3),
                FontSize = 11,
                FontWeight = FontWeights.Bold,
                Cursor = Cursors.Hand,
                BorderThickness = new Thickness(0),
                Background = isActive
                    ? (Brush)conv.ConvertFromString(bgHex)!
                    : new SolidColorBrush(Color.FromArgb(30, 255, 255, 255)),
                Foreground = isActive
                    ? (Brush)conv.ConvertFromString(fgHex)!
                    : new SolidColorBrush(Color.FromRgb(220, 220, 232)),
                Template = CreatePillTemplate()
            };

            string sid = src.SourceId;
            string sname = src.Name;
            btn.Click += (_, _) =>
            {
                _controller.SelectSource(sid);
                ShowToast("\uE8D6", $"Источник: {sname}");
            };
            SourcesPanel.Children.Add(btn);
        }
    }

    private static ControlTemplate CreatePillTemplate()
    {
        var border = new FrameworkElementFactory(typeof(Border));
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background")
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
        });
        border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding")
        {
            RelativeSource = new System.Windows.Data.RelativeSource(System.Windows.Data.RelativeSourceMode.TemplatedParent)
        });

        var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
        presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(presenter);

        return new ControlTemplate(typeof(Button)) { VisualTree = border };
    }

    private static string GetFriendlySourceLabel(string brand) => brand switch
    {
        "vk" => "ВК Музыка",
        "yandex" => "Яндекс Музыка",
        "spotify" => "Spotify",
        "soundcloud" => "SoundCloud",
        "youtube" => "YouTube Music",
        "audius" => "Audius",
        "bandcamp" => "Bandcamp",
        "open_catalog" => "Archive / Hearthis",
        "radio" => "Интернет-радио",
        "web" => "Интернет",
        _ => "OmniWave"
    };

    private static (string Bg, string Fg) GetBrandColors(string brand) => brand switch
    {
        "vk" => ("#0077FF", "#FFFFFF"),
        "yandex" => ("#FFCC00", "#111111"),
        "soundcloud" => ("#FF5500", "#FFFFFF"),
        "youtube" => ("#FF0033", "#FFFFFF"),
        "audius" => ("#CC0FF0", "#FFFFFF"),
        "bandcamp" => ("#1DA0C3", "#FFFFFF"),
        "open_catalog" => ("#22C55E", "#FFFFFF"),
        "radio" => ("#F59E0B", "#111111"),
        "web" => ("#22C55E", "#FFFFFF"),
        "spotify" => ("#1DB954", "#0B0B0E"),
        _ => ("#EAEAF2", "#111111")
    };

    private static bool IsInsideInteractive(DependencyObject? source)
    {
        while (source != null)
        {
            if (source is Button ||
                (source is FrameworkElement fe && fe.Name == "IslandProgressContainer"))
                return true;
            source = source is Visual
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        return false;
    }

    private void IslandBorder_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_dockEdge != DockEdge.None)
        {
            e.Handled = true;
            UndockFromEdge(animate: true, save: true);
            return;
        }

        if (IsInsideInteractive(e.OriginalSource as DependencyObject)) return;

        _collapseTimer.Stop();
        _isDragging = true;
        ClearWindowAnimations();
        try { DragMove(); } catch { }
        _isDragging = false;

        if (TryDetectMagneticEdge(out var area, out var edge))
        {
            _dockOffset = edge is DockEdge.Left or DockEdge.Right
                ? Top + Height / 2.0
                : Left + Width / 2.0;

            DockToEdge(edge, animate: true, save: true, customArea: area);
            return;
        }

        _anchorCenterX = Left + Width / 2.0;
        _anchorTopY = Top;
        _settings.Update(s =>
        {
            s.AnchorCenterX = _anchorCenterX;
            s.AnchorTopY = _anchorTopY;
            s.IslandDockEdge = "None";
            s.IslandDockOffset = null;
        }, notify: false);
    }

    private void IslandBorder_MouseEnter(object sender, MouseEventArgs e)
    {
        _collapseTimer.Stop();
        if (_dockEdge != DockEdge.None)
        {
            SetDockedPeek(true);
            return;
        }

        if (!_isPinned && Environment.TickCount64 < _undockSuppressExpandUntil)
            return;

        SetExpanded(true);
    }

    private void IslandBorder_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_dockEdge != DockEdge.None)
        {
            if (!_isMenuOpen)
                SetDockedPeek(false);
            return;
        }

        if (!_isPinned && !_isDragging && !_isMenuOpen)
            _collapseTimer.Start();
    }

    private bool TryDetectMagneticEdge(out Rect area, out DockEdge matchedEdge)
    {
        area = WindowBlurHelper.GetMonitorWorkArea(this);
        matchedEdge = DockEdge.None;

        double visLeft = Left + GlowPad;
        double visRight = Left + Width - GlowPad;
        double visTop = Top + GlowPad;
        double visBottom = Top + Height - GlowPad;

        double dLeft = visLeft - area.Left;
        double dRight = area.Right - visRight;
        double dTop = visTop - area.Top;
        double dBottom = area.Bottom - visBottom;

        double bestDist = double.MaxValue;
        if (dLeft <= MagneticSideThreshold && dLeft < bestDist)
        {
            bestDist = dLeft;
            matchedEdge = DockEdge.Left;
        }
        if (dRight <= MagneticSideThreshold && dRight < bestDist)
        {
            bestDist = dRight;
            matchedEdge = DockEdge.Right;
        }
        if (dBottom <= MagneticSideThreshold && dBottom < bestDist)
        {
            bestDist = dBottom;
            matchedEdge = DockEdge.Bottom;
        }
        if (dTop <= MagneticTopThreshold && dTop < bestDist)
        {
            matchedEdge = DockEdge.Top;
        }

        return matchedEdge != DockEdge.None;
    }

    private void DockToEdge(DockEdge edge, bool animate, bool save, Rect? customArea = null)
    {
        if (edge == DockEdge.None) return;

        _collapseTimer.Stop();
        _dockEdge = edge;
        _isDockedPeeked = false;
        _isExpanded = false;

        ActionPillToast.Visibility = Visibility.Collapsed;
        ApplyDockedVisualMode(edge);
        IslandScaleTransform.ScaleX = 1.0;
        IslandScaleTransform.ScaleY = 1.0;

        ApplyDockedGeometry(animate, customArea);

        if (save)
        {
            _settings.Update(s =>
            {
                s.IslandDockEdge = edge.ToString();
                s.IslandDockOffset = _dockOffset;
            }, notify: false);
        }
    }

    private void ApplyDockedVisualMode(DockEdge edge)
    {
        if (edge == DockEdge.None)
        {
            DockedEdgePanel.Visibility = Visibility.Collapsed;
            CompactPanel.Visibility = _isExpanded ? Visibility.Collapsed : Visibility.Visible;
            ExpandedPanel.Visibility = _isExpanded ? Visibility.Visible : Visibility.Collapsed;
            IslandBorder.Margin = new Thickness(GlowPad);
            var radius = new CornerRadius(_isExpanded ? 26 : 23);
            IslandBorder.CornerRadius = radius;
            IslandGlassSheen.CornerRadius = radius;
            ActionPillToast.CornerRadius = radius;
            return;
        }

        CompactPanel.Visibility = Visibility.Collapsed;
        ExpandedPanel.Visibility = Visibility.Collapsed;
        DockedEdgePanel.Visibility = Visibility.Visible;

        bool isVertical = edge is DockEdge.Left or DockEdge.Right;
        DockedContentStack.Orientation = isVertical ? Orientation.Vertical : Orientation.Horizontal;

        if (isVertical)
        {
            DockedPlayDot.Margin = new Thickness(0, 0, 0, 6);
            DockedAccentBar.Width = 3;
            DockedAccentBar.Height = 32;
            DockedChevron.Margin = new Thickness(0, 6, 0, 0);
            DockedChevron.Text = edge == DockEdge.Left ? "\uE76C" : "\uE76B";

            IslandBorder.Margin = edge == DockEdge.Left
                ? new Thickness(0, GlowPad, GlowPad, GlowPad)
                : new Thickness(GlowPad, GlowPad, 0, GlowPad);
            var cr = edge == DockEdge.Left
                ? new CornerRadius(0, 14, 14, 0)
                : new CornerRadius(14, 0, 0, 14);
            IslandBorder.CornerRadius = cr;
            IslandGlassSheen.CornerRadius = cr;
        }
        else
        {
            DockedPlayDot.Margin = new Thickness(0, 0, 7, 0);
            DockedAccentBar.Width = 34;
            DockedAccentBar.Height = 3;
            DockedChevron.Margin = new Thickness(7, 0, 0, 0);
            DockedChevron.Text = edge == DockEdge.Top ? "\uE70D" : "\uE70E";

            IslandBorder.Margin = edge == DockEdge.Top
                ? new Thickness(GlowPad, 0, GlowPad, GlowPad)
                : new Thickness(GlowPad, GlowPad, GlowPad, 0);
            var cr = edge == DockEdge.Top
                ? new CornerRadius(0, 0, 14, 14)
                : new CornerRadius(14, 14, 0, 0);
            IslandBorder.CornerRadius = cr;
            IslandGlassSheen.CornerRadius = cr;
        }
    }

    private void ApplyDockedGeometry(bool animate, Rect? customArea = null)
    {
        if (_dockEdge == DockEdge.None) return;
        var area = customArea ?? WindowBlurHelper.GetMonitorWorkArea(this);
        var (targetLeft, targetTop, targetW, targetH) = ComputeDockedRect(area, _dockEdge, _isDockedPeeked);

        if (!animate)
        {
            ClearWindowAnimations();
            Width = targetW;
            Height = targetH;
            Left = targetLeft;
            Top = targetTop;
            return;
        }

        AnimateDockProp(LeftProperty, Left, targetLeft, _isDockedPeeked);
        AnimateDockProp(TopProperty, Top, targetTop, _isDockedPeeked);
        AnimateDockProp(WidthProperty, Width, targetW, _isDockedPeeked);
        AnimateDockProp(HeightProperty, Height, targetH, _isDockedPeeked);
    }

    private (double Left, double Top, double W, double H) ComputeDockedRect(Rect area, DockEdge edge, bool peek)
    {
        double w, h, left, top;

        if (edge is DockEdge.Left or DockEdge.Right)
        {
            double visibleW = peek ? DockSidePeekW : DockSideW;
            w = visibleW + GlowPad;
            h = DockSideH + GlowPad * 2;
            double minCenterY = area.Top + h / 2.0 + 12;
            double maxCenterY = area.Bottom - h / 2.0 - 12;
            double centerY = Math.Clamp(_dockOffset, minCenterY, Math.Max(minCenterY, maxCenterY));
            _dockOffset = centerY;
            top = centerY - h / 2.0;
            left = edge == DockEdge.Left ? area.Left : area.Right - w;
        }
        else
        {
            double visibleH = peek ? DockHorizPeekH : DockHorizH;
            w = DockHorizW + GlowPad * 2;
            h = visibleH + GlowPad;
            double minCenterX = area.Left + w / 2.0 + 12;
            double maxCenterX = area.Right - w / 2.0 - 12;
            double centerX = Math.Clamp(_dockOffset, minCenterX, Math.Max(minCenterX, maxCenterX));
            _dockOffset = centerX;
            left = centerX - w / 2.0;
            top = edge == DockEdge.Top ? area.Top : area.Bottom - h;
        }

        return (left, top, w, h);
    }

    private void SetDockedPeek(bool peek)
    {
        if (_dockEdge == DockEdge.None || _isDockedPeeked == peek) return;
        _isDockedPeeked = peek;
        ApplyDockedGeometry(animate: true);
        IslandGlowEffect.Opacity = peek ? 0.68 : 0.48;
    }

    private void UndockFromEdge(bool animate, bool save)
    {
        var previousEdge = _dockEdge;
        if (previousEdge == DockEdge.None) return;

        var area = WindowBlurHelper.GetMonitorWorkArea(this);
        double scale = _settings.Current.IslandScale;
        double targetW = BaseCompactW * scale + GlowPad * 2;
        double targetH = BaseCompactH * scale + GlowPad * 2;

        switch (previousEdge)
        {
            case DockEdge.Left:
                _anchorCenterX = area.Left + 18 - GlowPad + targetW / 2.0;
                _anchorTopY = Math.Clamp(_dockOffset - targetH / 2.0, area.Top + 12, Math.Max(area.Top + 12, area.Bottom - targetH - 12));
                break;
            case DockEdge.Right:
                _anchorCenterX = area.Right - 18 + GlowPad - targetW / 2.0;
                _anchorTopY = Math.Clamp(_dockOffset - targetH / 2.0, area.Top + 12, Math.Max(area.Top + 12, area.Bottom - targetH - 12));
                break;
            case DockEdge.Top:
                _anchorCenterX = Math.Clamp(_dockOffset, area.Left + targetW / 2.0 + 12, Math.Max(area.Left + targetW / 2.0 + 12, area.Right - targetW / 2.0 - 12));
                _anchorTopY = area.Top + 12;
                break;
            case DockEdge.Bottom:
                _anchorCenterX = Math.Clamp(_dockOffset, area.Left + targetW / 2.0 + 12, Math.Max(area.Left + targetW / 2.0 + 12, area.Right - targetW / 2.0 - 12));
                _anchorTopY = area.Bottom - targetH - 12;
                break;
        }

        _dockEdge = DockEdge.None;
        _isDockedPeeked = false;
        _isExpanded = false;
        _undockSuppressExpandUntil = Environment.TickCount64 + 420;

        IslandScaleTransform.ScaleX = scale;
        IslandScaleTransform.ScaleY = scale;
        ApplyDockedVisualMode(DockEdge.None);
        IslandGlowEffect.Opacity = 0.48;

        if (!animate)
        {
            ApplyScaleAndPosition(animate: false);
        }
        else
        {
            double targetLeft = Math.Clamp(_anchorCenterX - targetW / 2.0, area.Left + 4, Math.Max(area.Left + 4, area.Right - targetW - 4));
            double targetTop = Math.Clamp(_anchorTopY, area.Top + 4, Math.Max(area.Top + 4, area.Bottom - targetH - 4));
            AnimateProp(LeftProperty, Left, targetLeft, isExpanding: true);
            AnimateProp(TopProperty, Top, targetTop, isExpanding: true);
            AnimateProp(WidthProperty, Width, targetW, isExpanding: true);
            AnimateProp(HeightProperty, Height, targetH, isExpanding: true);
        }

        _ = Dispatcher.BeginInvoke(UpdateAllMarquees, DispatcherPriority.Loaded);

        if (save)
        {
            _settings.Update(s =>
            {
                s.IslandDockEdge = "None";
                s.IslandDockOffset = null;
                s.AnchorCenterX = _anchorCenterX;
                s.AnchorTopY = _anchorTopY;
            }, notify: false);
        }

        if (_isPinned)
        {
            _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                if (_isPinned && _dockEdge == DockEdge.None)
                    SetExpanded(true);
            });
        }
    }

    private void AnimateDockProp(DependencyProperty prop, double from, double to, bool peekOut)
    {
        SetValue(prop, to);
        IEasingFunction ease = peekOut
            ? new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut }
            : new CubicEase { EasingMode = EasingMode.EaseOut };

        var anim = new DoubleAnimation(from, to, TimeSpan.FromMilliseconds(peekOut ? 200 : 280))
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.Stop
        };
        BeginAnimation(prop, anim);
    }

    private void DockFromMenu(DockEdge edge)
    {
        var area = WindowBlurHelper.GetMonitorWorkArea(this);
        _dockOffset = edge is DockEdge.Left or DockEdge.Right
            ? (_dockEdge != DockEdge.None ? _dockOffset : Top + Height / 2.0)
            : (_dockEdge != DockEdge.None ? _dockOffset : Left + Width / 2.0);
        DockToEdge(edge, animate: true, save: true, customArea: area);
    }

    private void DockLeft_Click(object sender, RoutedEventArgs e) => DockFromMenu(DockEdge.Left);
    private void DockRight_Click(object sender, RoutedEventArgs e) => DockFromMenu(DockEdge.Right);
    private void DockTop_Click(object sender, RoutedEventArgs e) => DockFromMenu(DockEdge.Top);
    private void DockBottom_Click(object sender, RoutedEventArgs e) => DockFromMenu(DockEdge.Bottom);

    private TrackItem? GetCurrentIslandTrack()
    {
        if (_controller.IsLocalActive && _audioPlayer.CurrentTrack != null)
            return _audioPlayer.CurrentTrack;

        if (_lastMediaState == null || string.IsNullOrWhiteSpace(_lastMediaState.ActiveId) ||
            string.IsNullOrWhiteSpace(_lastMediaState.Title) ||
            _lastMediaState.Title == "Ничего не играет")
        {
            return null;
        }

        var brand = string.IsNullOrWhiteSpace(_lastMediaState.ActiveBrand) ? "default" : _lastMediaState.ActiveBrand;
        var (bgHex, _) = GetBrandColors(brand);
        string sourceName = brand switch
        {
            "vk" => "ВК",
            "yandex" => "Яндекс",
            "spotify" => "Spotify",
            "soundcloud" => "SoundCloud",
            "youtube" => "YouTube",
            "audius" => "Audius",
            "bandcamp" => "Bandcamp",
            "open_catalog" => "Каталоги",
            "radio" => "Радио",
            "web" => "Интернет",
            _ => "Плеер"
        };

        return new TrackItem
        {
            Id = $"{brand}:{_lastMediaState.Title}",
            Source = sourceName,
            BrandKey = brand,
            BrandColor = bgHex,
            Title = _lastMediaState.Title,
            Artist = _lastMediaState.Artist,
            PageUrl = $"external:{brand}:{_lastMediaState.Artist}:{_lastMediaState.Title}",
            CoverUrl = _lastMediaState.CoverUrl ?? "",
            Duration = _lastMediaState.TotalMs > 0 ? TimeSpan.FromMilliseconds(_lastMediaState.TotalMs) : TimeSpan.Zero
        };
    }

    private void UpdateRepeatButton(RepeatMode mode)
    {
        switch (mode)
        {
            case RepeatMode.All:
                BtnRepeat.Content = "\uE8EE";
                BtnRepeat.Foreground = new SolidColorBrush(Color.FromRgb(255, 204, 0));
                BtnRepeat.ToolTip = "Повтор: Вся очередь";
                IslandRepeat1Badge.Visibility = Visibility.Collapsed;
                break;
            case RepeatMode.One:
                BtnRepeat.Content = "\uE8EE";
                BtnRepeat.Foreground = new SolidColorBrush(Color.FromRgb(255, 204, 0));
                BtnRepeat.ToolTip = "Повтор: Один трек";
                IslandRepeat1Badge.Visibility = Visibility.Visible;
                break;
            default:
                BtnRepeat.Content = "\uE8EE";
                BtnRepeat.Foreground = new SolidColorBrush(Color.FromArgb(230, 255, 255, 255));
                BtnRepeat.ToolTip = "Повтор: Выкл";
                IslandRepeat1Badge.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private void UpdateIslandLikeButton()
    {
        var track = GetCurrentIslandTrack();
        bool liked = track != null && _library.IsLiked(track);
        BtnIslandLike.Content = liked ? "\uEB52" : "\uEB51";
        BtnIslandLike.Foreground = liked
            ? new SolidColorBrush(Color.FromRgb(255, 75, 114))
            : Brushes.White;
        BtnIslandLike.ToolTip = liked ? "Убрать из избранного" : "Добавить в избранное";
    }

    private void BtnRepeat_Click(object sender, RoutedEventArgs e)
    {
        var mode = _audioPlayer.CycleRepeatMode();
        UpdateRepeatButton(mode);
        var label = mode switch
        {
            RepeatMode.All => "Повтор: Очередь",
            RepeatMode.One => "Повтор: 1 трек",
            _ => "Повтор: Выкл"
        };
        ShowToast("\uE8EE", label);
    }

    private void BtnIslandLike_Click(object sender, RoutedEventArgs e)
    {
        var track = GetCurrentIslandTrack();
        if (track == null)
        {
            ShowToast("\uE783", "Нет активного трека");
            return;
        }

        bool nowLiked = _library.ToggleLike(track);
        UpdateIslandLikeButton();
        ShowToast(nowLiked ? "\uEB52" : "\uEB51", nowLiked ? "Добавлено в избранное" : "Удалено из избранного");
    }

    private void BtnIslandPlaylist_Click(object sender, RoutedEventArgs e)
    {
        var track = GetCurrentIslandTrack();
        var menu = new ContextMenu
        {
            Style = (Style)FindResource("DarkContextMenu"),
            FontFamily = new FontFamily("Segoe UI")
        };
        var itemStyle = (Style)FindResource("DarkMenuItem");
        var sepStyle = (Style)FindResource("DarkMenuSeparator");

        var miWave = new MenuItem
        {
            Header = "🌊 Запустить «Мою волну» Яндекса",
            Style = itemStyle
        };
        miWave.Click += async (_, _) =>
        {
            if (!_yandex.IsAuthenticated)
            {
                ShowToast("\uE77B", "Войдите в Яндекс Музыку");
                OpenAccountsWindow();
                return;
            }
            ShowToast("\uE768", "Запуск «Моей волны»...");
            bool ok = await _audioPlayer.StartYandexWaveAsync();
            if (!ok)
                ShowToast("\uE783", "Не удалось загрузить «Мою волну»");
        };
        menu.Items.Add(miWave);

        var miVkMix = new MenuItem
        {
            Header = "🔥 Запустить «VK Микс»",
            Style = itemStyle
        };
        miVkMix.Click += async (_, _) =>
        {
            if (!_vk.IsAuthenticated)
            {
                ShowToast("\uE77B", "Войдите в ВК Музыку");
                OpenAccountsWindow();
                return;
            }
            ShowToast("\uE768", "Запуск «VK Микс»...");
            bool ok = await _audioPlayer.StartVkMixAsync();
            if (!ok)
                ShowToast("\uE783", "Не удалось загрузить «VK Микс»");
        };
        menu.Items.Add(miVkMix);

        if (track == null)
        {
            menu.PlacementTarget = BtnIslandPlaylist;
            menu.IsOpen = true;
            return;
        }

        if (!_controller.IsLocalActive)
        {
            var miTakeOver = new MenuItem
            {
                Header = "▶ Перехватить во встроенный плеер OmniWave",
                Style = itemStyle
            };
            miTakeOver.Click += async (_, _) =>
            {
                ShowToast("\uE768", "Запуск в OmniWave...");
                await _audioPlayer.PlayFromQueueAsync(new List<TrackItem> { track }, 0);
            };
            menu.Items.Add(miTakeOver);
        }

        var miExportYandex = new MenuItem
        {
            Header = "✦ В Яндекс «Мне нравится»",
            Style = itemStyle
        };
        miExportYandex.Click += async (_, _) =>
        {
            if (!_yandex.IsAuthenticated)
            {
                ShowToast("\uE77B", "Войдите в Яндекс Музыку");
                OpenAccountsWindow();
                return;
            }
            ShowToast("\uE734", "Отправка в Яндекс...");
            var res = await _yandex.ExportTracksToYandexLikesAsync(new[] { track });
            ShowToast(res.Ok ? "\uE73E" : "\uE783", res.AddedCount > 0 ? "Добавлено в Яндекс ♥" : (res.AlreadyLikedCount > 0 ? "Уже в Яндекс ♥" : "Не найдено в Яндекс"));
            if (res.AddedCount > 0)
                _ = SyncExternalAccountsAsync();
        };
        menu.Items.Add(miExportYandex);
        menu.Items.Add(new Separator { Style = sepStyle });

        var localPlaylists = _library.GetLocalPlaylists();
        foreach (var pl in localPlaylists)
        {
            bool alreadyIn = pl.Tracks.Any(t => t.UniqueKey == track.UniqueKey);
            var mi = new MenuItem
            {
                Header = pl.Name,
                InputGestureText = pl.CountText,
                IsCheckable = true,
                IsChecked = alreadyIn,
                Style = itemStyle
            };
            string plId = pl.Id;
            string plName = pl.Name;
            mi.Click += (_, _) =>
            {
                if (_library.AddTrackToPlaylist(plId, track))
                    ShowToast("\uE73E", $"Добавлено в «{plName}»");
                else
                    ShowToast("\uE73E", $"Уже в «{plName}»");
            };
            menu.Items.Add(mi);
        }

        if (localPlaylists.Count > 0)
            menu.Items.Add(new Separator { Style = sepStyle });

        var miNew = new MenuItem
        {
            Header = "+ Новый плейлист...",
            Style = itemStyle
        };
        miNew.Click += (_, _) =>
        {
            OpenPlayerWindow();
            _playerWindow?.OpenPlaylistsTabAndFocusNew();
        };
        menu.Items.Add(miNew);

        menu.Opened += (_, _) => _isMenuOpen = true;
        menu.Closed += (_, _) =>
        {
            _isMenuOpen = false;
            if (!IsMouseOver && !_isPinned && !_isDragging)
                _collapseTimer.Start();
        };

        menu.PlacementTarget = BtnIslandPlaylist;
        menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        menu.IsOpen = true;
    }

    private void FlashIslandSeekPreview(long targetMs, long deltaMs, long totalMs)
    {
        UpdateIslandProgressBar(targetMs, totalMs);

        if (totalMs <= 0 || IslandProgressTrack.ActualWidth <= 0) return;

        double trackW = IslandProgressTrack.ActualWidth;
        double frac = Math.Clamp((double)targetMs / totalMs, 0.0, 1.0);
        double x = trackW * frac;
        long absDelta = Math.Abs(deltaMs);
        string sign = deltaMs >= 0 ? "+" : "-";
        string deltaStr = FormatTimeMs(absDelta);

        IslandProgressHoverFill.Visibility = Visibility.Visible;
        IslandProgressHoverFill.Width = x;

        IslandSeekPreviewText.Text = absDelta >= 1000
            ? $"{FormatTimeMs(targetMs)} ({sign}{deltaStr})"
            : FormatTimeMs(targetMs);
        IslandSeekPreviewBadge.Visibility = Visibility.Visible;
        IslandSeekPreviewBadge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double badgeW = IslandSeekPreviewBadge.DesiredSize.Width;
        IslandSeekPreviewTransform.X = Math.Clamp(x - badgeW / 2.0, 0.0, Math.Max(0.0, trackW - badgeW));

        int gen = ++_seekPreviewHideGen;
        _ = HideIslandSeekPreviewAfterDelayAsync(gen);
    }

    private async Task HideIslandSeekPreviewAfterDelayAsync(int gen)
    {
        await Task.Delay(900);
        if (gen == _seekPreviewHideGen && !_isScrubbingIslandProgress && !IslandProgressHitArea.IsMouseOver)
            HideIslandSeekPreview();
    }

    private async Task SeekRelativeWithToastAsync(long deltaMs, string glyph, string prefix)
    {
        var (cur, tot) = GetActiveIslandTimes();
        long target = tot > 0 ? Math.Clamp(cur + deltaMs, 0, tot) : Math.Max(0, cur + deltaMs);
        await _controller.SeekRelativeMsAsync(deltaMs);
        FlashIslandSeekPreview(target, deltaMs, tot);
        _playerWindow?.NotifySeekVisual(target, deltaMs);
        ShowToast(glyph, tot > 0 ? $"{prefix} → {FormatTimeMs(target)}" : $"{prefix}");
    }

    private async void BtnSeekBack15_Click(object sender, RoutedEventArgs e)
        => await SeekRelativeWithToastAsync(-15000, "\uE892", "-15с");

    private async void BtnPrev_Click(object sender, RoutedEventArgs e) => await _controller.SendCommandAsync("prev");
    private async void BtnPlay_Click(object sender, RoutedEventArgs e) => await _controller.SendCommandAsync("toggle");
    private async void BtnNext_Click(object sender, RoutedEventArgs e) => await _controller.SendCommandAsync("next");

    private async void BtnSeekFwd15_Click(object sender, RoutedEventArgs e)
        => await SeekRelativeWithToastAsync(15000, "\uE893", "+15с");

    private void SyncIslandVolumeUi(int volume)
    {
        if (IslandVolumeSlider == null || IslandVolumeText == null || BtnIslandMute == null) return;
        _updatingVolumeSlider = true;
        try
        {
            IslandVolumeSlider.Value = volume;
            IslandVolumeText.Text = $"{volume}%";
            BtnIslandMute.Content = volume <= 0 ? "\uE74F" : "\uE767";
        }
        finally
        {
            _updatingVolumeSlider = false;
        }
    }

    private void IslandVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingVolumeSlider || !IsLoaded) return;
        int vol = Math.Clamp((int)Math.Round(e.NewValue), 0, 100);
        if (IslandVolumeText != null)
            IslandVolumeText.Text = $"{vol}%";
        if (BtnIslandMute != null)
            BtnIslandMute.Content = vol <= 0 ? "\uE74F" : "\uE767";
        _controller.SetAppVolume(vol);
    }

    private void BtnIslandMute_Click(object sender, RoutedEventArgs e)
    {
        int cur = _audioPlayer.Volume;
        if (cur > 0)
        {
            _savedIslandVolumeBeforeMute = cur;
            _controller.SetAppVolume(0);
            ShowToast("\uE74F", "Без звука");
        }
        else
        {
            int restore = _savedIslandVolumeBeforeMute > 0 ? _savedIslandVolumeBeforeMute : 80;
            _controller.SetAppVolume(restore);
            ShowToast("\uE767", $"Громкость: {restore}%");
        }
    }

    private void OpenPlayer_Click(object sender, RoutedEventArgs e) => OpenPlayerWindow();
    private void OpenAccounts_Click(object sender, RoutedEventArgs e) => OpenAccountsWindow();
    private void OpenSettings_Click(object sender, RoutedEventArgs e) => OpenSettingsWindow();

    private void PinMenuItem_Click(object sender, RoutedEventArgs e)
    {
        _isPinned = PinMenuItem.IsChecked;
        SaveAndApplyPinState();
    }

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        _isPinned = !_isPinned;
        SaveAndApplyPinState();
        ShowToast("\uE718", _isPinned ? "Остров закреплён" : "Закрепление снято");
    }

    private void SaveAndApplyPinState()
    {
        if (_dockEdge != DockEdge.None)
            UndockFromEdge(animate: false, save: true);
        SyncPinVisual();
        _settings.Update(s => s.PinIslandExpanded = _isPinned, notify: false);
        if (_isPinned) SetExpanded(true);
    }

    private void SyncPinVisual()
    {
        PinMenuItem.IsChecked = _isPinned;
        BtnIslandPin.Foreground = _isPinned
            ? new SolidColorBrush(Color.FromRgb(255, 204, 0))
            : new SolidColorBrush(Color.FromArgb(191, 255, 255, 255));
    }

    private void AlwaysOnTopMenuItem_Click(object sender, RoutedEventArgs e)
        => SetAlwaysOnTop(AlwaysOnTopMenuItem.IsChecked, showToast: true);

    private void AlwaysOnTopButton_Click(object sender, RoutedEventArgs e)
        => SetAlwaysOnTop(!_settings.Current.IslandAlwaysOnTop, showToast: true);

    private void SetAlwaysOnTop(bool enabled, bool showToast)
    {
        _settings.Update(s => s.IslandAlwaysOnTop = enabled);
        ApplyAlwaysOnTopState();
        if (showToast && !_isFoldedForFullscreen)
        {
            ShowToast("\uE81E", enabled ? "Поверх всех окон" : "За всеми окнами");
        }
    }

    private void SyncAlwaysOnTopVisual(bool onTop)
    {
        AlwaysOnTopMenuItem.IsChecked = onTop;
        if (_trayAlwaysOnTopMenuItem != null)
            _trayAlwaysOnTopMenuItem.IsChecked = onTop;
        BtnIslandAlwaysOnTop.Foreground = onTop
            ? new SolidColorBrush(Color.FromRgb(255, 204, 0))
            : new SolidColorBrush(Color.FromArgb(145, 255, 255, 255));
        BtnIslandAlwaysOnTop.ToolTip = onTop
            ? "Поверх всех окон: Вкл (нажмите, чтобы убрать за окна)"
            : "Поверх всех окон: Выкл (Остров за всеми окнами)";
    }

    private void ApplyAlwaysOnTopState()
    {
        bool onTop = _settings.Current.IslandAlwaysOnTop;
        SyncAlwaysOnTopVisual(onTop);

        var hwnd = new WindowInteropHelper(this).Handle;
        if (onTop)
        {
            if (!Topmost) Topmost = true;
            if (hwnd != IntPtr.Zero)
            {
                _ = SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
            }
            return;
        }

        _isChangingZOrderBand = true;
        try
        {
            if (Topmost) Topmost = false;
            if (hwnd != IntPtr.Zero)
            {
                _ = SetWindowPos(hwnd, HWND_NOTOPMOST, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
            }
        }
        finally
        {
            _isChangingZOrderBand = false;
        }

        if (hwnd != IntPtr.Zero)
        {
            _ = SetWindowPos(hwnd, HWND_BOTTOM, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_NOOWNERZORDER);
        }
    }

    private IntPtr IslandZOrderWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (!_isChangingZOrderBand &&
            !_settings.Current.IslandAlwaysOnTop &&
            msg == WM_WINDOWPOSCHANGING &&
            lParam != IntPtr.Zero)
        {
            var wp = Marshal.PtrToStructure<WINDOWPOS>(lParam);
            wp.hwndInsertAfter = HWND_BOTTOM;
            wp.flags &= ~SWP_NOZORDER;
            wp.flags |= SWP_NOACTIVATE;
            Marshal.StructureToPtr(wp, lParam, false);
        }

        return IntPtr.Zero;
    }

    private void UpdateIslandFullscreenVisibility()
    {
        if (_isClosing) return;
        bool shouldFold = _playerWindow != null &&
                          _playerWindow.IsVisible &&
                          _playerWindow.WindowState != WindowState.Minimized;
        SetFoldedForFullscreen(shouldFold);
    }

    private void SetFoldedForFullscreen(bool fold)
    {
        if (_isFoldedForFullscreen == fold) return;
        _isFoldedForFullscreen = fold;
        int gen = ++_fullscreenAnimGen;

        var (slideX, slideY) = _dockEdge switch
        {
            DockEdge.Bottom => (0.0, 24.0),
            DockEdge.Left => (-24.0, 0.0),
            DockEdge.Right => (24.0, 0.0),
            _ => (0.0, -26.0)
        };

        IslandBorder.RenderTransformOrigin = _dockEdge switch
        {
            DockEdge.Bottom => new Point(0.5, 1.0),
            DockEdge.Left => new Point(0.0, 0.5),
            DockEdge.Right => new Point(1.0, 0.5),
            _ => new Point(0.5, 0.0)
        };

        if (fold)
        {
            _collapseTimer.Stop();
            IsHitTestVisible = false;

            var durFold = TimeSpan.FromMilliseconds(270);
            var easeIn = new CubicEase { EasingMode = EasingMode.EaseIn };

            IslandCollapseScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.56, durFold) { EasingFunction = easeIn });
            IslandCollapseScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.20, durFold) { EasingFunction = easeIn });
            IslandCollapseTranslate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(slideX, durFold) { EasingFunction = easeIn });
            IslandCollapseTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(slideY, durFold) { EasingFunction = easeIn });

            var opacityAnim = new DoubleAnimation(0.0, durFold) { EasingFunction = easeIn };
            opacityAnim.Completed += (_, _) =>
            {
                if (gen == _fullscreenAnimGen && _isFoldedForFullscreen)
                    Hide();
            };
            IslandBorder.BeginAnimation(OpacityProperty, opacityAnim);
            return;
        }

        if (!_isPinned && _isExpanded)
            SetExpanded(false);

        if (Visibility != Visibility.Visible)
        {
            IslandCollapseScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            IslandCollapseScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            IslandCollapseTranslate.BeginAnimation(TranslateTransform.XProperty, null);
            IslandCollapseTranslate.BeginAnimation(TranslateTransform.YProperty, null);
            IslandBorder.BeginAnimation(OpacityProperty, null);

            IslandCollapseScale.ScaleX = 0.56;
            IslandCollapseScale.ScaleY = 0.20;
            IslandCollapseTranslate.X = slideX;
            IslandCollapseTranslate.Y = slideY;
            IslandBorder.Opacity = 0.0;

            Show();
            ApplyAlwaysOnTopState();
        }

        IsHitTestVisible = true;
        var durUnfold = TimeSpan.FromMilliseconds(340);
        var easeBack = new BackEase { Amplitude = 0.22, EasingMode = EasingMode.EaseOut };
        var durFade = TimeSpan.FromMilliseconds(220);
        var easeOut = new CubicEase { EasingMode = EasingMode.EaseOut };

        IslandCollapseScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, durUnfold) { EasingFunction = easeBack });
        IslandCollapseScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, durUnfold) { EasingFunction = easeBack });
        IslandCollapseTranslate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0.0, durUnfold) { EasingFunction = easeBack });
        IslandCollapseTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0.0, durUnfold) { EasingFunction = easeBack });
        IslandBorder.BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, durFade) { EasingFunction = easeOut });
    }

    private void ResetPosition_Click(object sender, RoutedEventArgs e) => ResetAnchorToTopCenter();

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    protected override void OnClosed(EventArgs e)
    {
        _isClosing = true;
        _collapseTimer.Stop();
        _eqTimer.Stop();
        _actionToastTimer.Stop();
        _islandHwndSource?.RemoveHook(IslandZOrderWndProc);
        _hotkeys.Dispose();
        _trayIcon.Dispose();

        _settingsWindow?.Close();
        _accountWindow?.Close();
        _syncLock.Dispose();
        if (_playerWindow != null)
        {
            _playerWindow.AllowClose = true;
            _playerWindow.Close();
        }
        _controller.Dispose();
        _audioPlayer.Dispose();
        _downloads.Dispose();
        foreach (var provider in _providers)
        {
            if (provider is IDisposable d) d.Dispose();
        }
        _lyrics.Dispose();
        base.OnClosed(e);
    }

    private const int WM_WINDOWPOSCHANGING = 0x0046;
    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private static readonly IntPtr HWND_NOTOPMOST = new(-2);
    private static readonly IntPtr HWND_BOTTOM = new(1);
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOOWNERZORDER = 0x0200;

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWPOS
    {
        public IntPtr hwnd;
        public IntPtr hwndInsertAfter;
        public int x;
        public int y;
        public int cx;
        public int cy;
        public uint flags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
}
