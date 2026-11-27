using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using DynamicIsland.Services;
using Microsoft.Web.WebView2.Wpf;

namespace DynamicIsland;

public partial class PlayerWindow : Window
{
    public const double ExpandedWidth = 400.0;
    public const double ExpandedHeight = 604.0;
    public const double CompactWidth = 348.0;
    public const double CompactHeight = 84.0;
    private const double EdgePeekPx = 10.0;
    private const double EdgeSnapThresholdPx = 22.0;
    private const double EdgeAnimDurationMs = 210.0;
    private const int WM_ENTERSIZEMOVE = 0x0231;
    private const int WM_EXITSIZEMOVE = 0x0232;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private sealed class SearchTrackGroup
    {
        public required string GroupKey { get; init; }
        public required List<TrackItem> Variants { get; init; }
        public int SelectedIndex { get; set; }
        public TrackItem ActiveTrack => Variants[Math.Clamp(SelectedIndex, 0, Variants.Count - 1)];
    }

    private readonly List<IMusicProvider> _providers;
    private readonly AudioPlayerService _player;
    private readonly MediaControllerService _controller;
    private readonly LibraryService _library;
    private readonly SettingsService _settings;
    private readonly DownloadService _downloads;
    private readonly YandexMusicProvider _yandex;
    private readonly VkMusicProvider _vk;
    private readonly SpotifyMusicProvider _spotify;
    private readonly Action _openSettingsAction;
    private readonly Action _openAccountsAction;
    private readonly Func<Task<string>> _syncAccountsAction;
    private readonly Action _requestExitAction;
    private readonly Action _trayStateSyncAction;
    private readonly uint _restoreMsgId;

    private readonly Dictionary<string, TrackItem> _trackIndex = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _posSaveTimer;
    private readonly DispatcherTimer _telemetryTimer;
    private readonly DispatcherTimer _suspendTimer;
    private readonly DispatcherTimer _edgeAnimTimer;
    private readonly DispatcherTimer _edgeHoverTimer;
    private readonly DispatcherTimer _clipboardWatchTimer;
    private readonly Process _selfProcess = Process.GetCurrentProcess();

    private HwndSource? _hwndSource;
    private VkAuthWindow? _activeAuthWindow;
    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _proxyTestCts;
    private CancellationTokenSource? _authFlowCts;
    private List<SearchTrackGroup> _currentSearchGroups = new();
    private List<TrackItem> _rawSearchTracks = new();
    private List<TrackItem> _currentResults = new();
    private List<TrackItem> _favorites = new();

    private string _lastQuery = "";
    private string _activeSearchFilter = "all";
    private string _activeTab = "search";
    private int _currentLimitPerProvider = 10;
    private int _savedVolumeBeforeMute = 80;
    private bool _webViewReady;
    private bool _isPlaying;
    private bool _isUiSuspended;
    private bool _isWebViewSuspended;
    private bool _isExportingToYandex;
    private string? _lastExternalVisualKey;
    private TrackItem? _externalActiveTrack;
    private long _currentMs;
    private long _totalMs;
    private long _lastPosPushTick;
    private string? _lastCoverDataUrl;
    private bool _isCompactMode;
    private bool _isApplyingGeometry;
    private bool _isSettingsApplying;
    private string _dockedEdge = "";
    private bool _isEdgeCollapsed;
    private bool _isDraggingWindow;
    private bool _isWebPointerHeld;
    private long _edgeLastHoverTick;
    private long _edgeSuppressExpandUntilTick;
    private double _animStartLeft;
    private double _animStartTop;
    private double _animTargetLeft;
    private double _animTargetTop;
    private long _animStartTick;
    private string? _pendingBrowserAuthProvider;
    private string _clipboardBeforeAuth = "";
    private string _lastTestedClipboard = "";
    private DateTime _clipboardWatchDeadlineUtc = DateTime.MinValue;
    private bool _clipboardWatchBusy;
    private bool _isAuthBusy;
    private bool _vkClearedSession;
    private bool _youtubeClearedSession;
    private bool _systemCheckInitialized;
    private TimeSpan _lastCpuTime;
    private DateTime _lastCpuSampleUtc = DateTime.UtcNow;

    public bool AllowClose { get; set; }
    public bool IsCompactMode => _isCompactMode;

    public PlayerWindow(
        List<IMusicProvider> providers,
        AudioPlayerService player,
        MediaControllerService controller,
        LibraryService library,
        SettingsService settings,
        DownloadService downloads,
        YandexMusicProvider yandex,
        VkMusicProvider vk,
        SpotifyMusicProvider spotify,
        Action openSettingsAction,
        Action openAccountsAction,
        Func<Task<string>> syncAccountsAction,
        Action requestExitAction,
        Action trayStateSyncAction,
        uint restoreMsgId = 0)
    {
        InitializeComponent();
        _providers = providers;
        _player = player;
        _controller = controller;
        _library = library;
        _settings = settings;
        _downloads = downloads;
        _yandex = yandex;
        _vk = vk;
        _spotify = spotify;
        _openSettingsAction = openSettingsAction;
        _openAccountsAction = openAccountsAction;
        _syncAccountsAction = syncAccountsAction;
        _requestExitAction = requestExitAction;
        _trayStateSyncAction = trayStateSyncAction;
        _restoreMsgId = restoreMsgId;

        _posSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _posSaveTimer.Tick += (_, _) =>
        {
            _posSaveTimer.Stop();
            SaveCurrentWindowPosition();
        };

        _lastCpuTime = _selfProcess.TotalProcessorTime;
        _lastCpuSampleUtc = DateTime.UtcNow;
        _telemetryTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(3000) };
        _telemetryTimer.Tick += (_, _) => PushTelemetrySample();

        _suspendTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
        _suspendTimer.Tick += async (_, _) => await SuspendWebViewInTrayAsync();

        _edgeAnimTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        _edgeAnimTimer.Tick += (_, _) => OnEdgeAnimTick();

        _edgeHoverTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(85) };
        _edgeHoverTimer.Tick += (_, _) => OnEdgeHoverTick();

        _clipboardWatchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _clipboardWatchTimer.Tick += async (_, _) => await OnClipboardWatchTickAsync();

        RestoreSavedGeometry();

        PreviewKeyDown += PlayerWindow_PreviewKeyDown;
        Activated += PlayerWindow_Activated;
        Loaded += async (_, _) => await InitWebViewAsync();
        LocationChanged += (_, _) =>
        {
            if (_isApplyingGeometry || _isEdgeCollapsed || !IsVisible || WindowState == WindowState.Minimized) return;
            _posSaveTimer.Stop();
            _posSaveTimer.Start();
        };
        SizeChanged += (_, _) =>
        {
            WindowBlurHelper.UpdateWin10RoundedRegion(this, _isCompactMode ? 12 : 16);
        };
        IsVisibleChanged += (_, _) => UpdateSuspensionState();
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Maximized)
                WindowState = WindowState.Normal;
            UpdateSuspensionState();
            if (!_isUiSuspended && !_isWebViewSuspended)
                PostToWeb("windowState", new { maximized = false, compact = _isCompactMode });
        };

        _player.PositionChanged += OnPositionChanged;
        _player.PlaybackStateChanged += OnPlaybackStateChanged;
        _player.TrackChanged += OnTrackChanged;
        _player.RepeatModeChanged += OnRepeatModeChanged;
        _player.ShuffleChanged += OnShuffleChanged;
        _player.StatusMessage += OnStatusMessage;
        _player.QueueChanged += OnQueueChanged;
        _player.VolumeChanged += OnVolumeChanged;
        _library.LibraryChanged += OnLibraryChanged;
        _downloads.DownloadCompleted += OnDownloadCompleted;
        _settings.SettingsChanged += OnSettingsChanged;
        _controller.StateChanged += OnMediaControllerStateChanged;
    }

    private void OnRepeatModeChanged(RepeatMode mode) => Dispatcher.BeginInvoke(() =>
    {
        if (!_isUiSuspended) PostToWeb("repeatMode", new { mode = (int)mode });
    });

    private void OnShuffleChanged(bool enabled) => Dispatcher.BeginInvoke(() =>
    {
        if (!_isUiSuspended) PostToWeb("shuffleState", new { enabled });
    });

    private void OnStatusMessage(string msg) => Dispatcher.BeginInvoke(() =>
    {
        if (!_isUiSuspended) PostToWeb("status", new { message = msg });
    });

    private void OnQueueChanged() => Dispatcher.BeginInvoke(() =>
    {
        if (!_isUiSuspended) PushQueueUpdate();
    });

    private void OnVolumeChanged(int v) => Dispatcher.BeginInvoke(() =>
    {
        if (!_isUiSuspended) PostToWeb("volume", new { volume = v });
    });

    private void OnLibraryChanged() => Dispatcher.BeginInvoke(() =>
    {
        if (!_isUiSuspended) PushLibraryUpdate();
    });

    private void OnDownloadCompleted(TrackItem _) => Dispatcher.BeginInvoke(() =>
    {
        if (!_isUiSuspended) SyncAllDownloadedStates();
    });

    private void OnSettingsChanged(AppSettings _) => Dispatcher.BeginInvoke(() =>
    {
        ApplyWindowSettingsFromConfig();
        if (!_isUiSuspended)
        {
            PushAccountsUpdate();
            PushWindowSettingsUpdate();
        }
    });

    private void OnMediaControllerStateChanged(MediaState state) => Dispatcher.BeginInvoke(() => OnControllerStateChanged(state));

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero)
        {
            _hwndSource = HwndSource.FromHwnd(hwnd);
            _hwndSource?.AddHook(WindowHwndProc);
        }
        WindowBlurHelper.ApplyChromaWindowStyle(this, _isCompactMode ? 12 : 16);
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () => WindowBlurHelper.UpdateWin10RoundedRegion(this, _isCompactMode ? 12 : 16));
        WindowBlurHelper.SetWindowAlpha(this, _settings.Current.WindowOpacityPercent / 100.0);
        Topmost = _settings.Current.WindowAlwaysOnTop;
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        WindowBlurHelper.UpdateWin10RoundedRegion(this, _isCompactMode ? 12 : 16);
        if (_isApplyingGeometry || !IsLoaded) return;

        if (_isCompactMode && !string.IsNullOrEmpty(_dockedEdge))
        {
            var work = WindowBlurHelper.GetMonitorWorkAreaForWindow(this);
            var (el, et) = ComputeEdgePosition(_dockedEdge, _isEdgeCollapsed, work);
            _isApplyingGeometry = true;
            try
            {
                Left = el;
                Top = et;
            }
            finally
            {
                _isApplyingGeometry = false;
            }
            return;
        }

        var (clampedLeft, clampedTop) = WindowBlurHelper.ClampToVisibleMonitors(this, Left, Top, Width, Height);
        if (Math.Abs(clampedLeft - Left) > 0.5 || Math.Abs(clampedTop - Top) > 0.5)
        {
            _isApplyingGeometry = true;
            try
            {
                Left = clampedLeft;
                Top = clampedTop;
            }
            finally
            {
                _isApplyingGeometry = false;
            }
        }
    }

    private IntPtr WindowHwndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_restoreMsgId != 0 && msg == (int)_restoreMsgId)
        {
            Dispatcher.BeginInvoke(RestoreFromTray);
            handled = true;
        }
        else if (msg == WM_ENTERSIZEMOVE)
        {
            _isDraggingWindow = true;
            _edgeAnimTimer.Stop();
        }
        else if (msg == WM_EXITSIZEMOVE)
        {
            _isDraggingWindow = false;
            if (_isCompactMode && _settings.Current.CompactEdgeAutoHide)
                Dispatcher.BeginInvoke(EvaluateEdgeDockAfterDrag);
        }
        return IntPtr.Zero;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            if (_settings.Current.MinimizeToTray)
                HideToTray();
            else
                _requestExitAction();
            return;
        }

        _posSaveTimer.Stop();
        _telemetryTimer.Stop();
        _suspendTimer.Stop();
        _edgeAnimTimer.Stop();
        _edgeHoverTimer.Stop();
        _clipboardWatchTimer.Stop();
        Activated -= PlayerWindow_Activated;

        _player.PositionChanged -= OnPositionChanged;
        _player.PlaybackStateChanged -= OnPlaybackStateChanged;
        _player.TrackChanged -= OnTrackChanged;
        _player.RepeatModeChanged -= OnRepeatModeChanged;
        _player.ShuffleChanged -= OnShuffleChanged;
        _player.StatusMessage -= OnStatusMessage;
        _player.QueueChanged -= OnQueueChanged;
        _player.VolumeChanged -= OnVolumeChanged;
        _library.LibraryChanged -= OnLibraryChanged;
        _downloads.DownloadCompleted -= OnDownloadCompleted;
        _settings.SettingsChanged -= OnSettingsChanged;
        _controller.StateChanged -= OnMediaControllerStateChanged;
        _hwndSource?.RemoveHook(WindowHwndProc);
        _hwndSource = null;

        try { _authFlowCts?.Cancel(); _authFlowCts?.Dispose(); } catch { }
        try { _activeAuthWindow?.Close(); _activeAuthWindow = null; } catch { }
        try { _searchCts?.Cancel(); _searchCts?.Dispose(); } catch { }
        try { _proxyTestCts?.Cancel(); _proxyTestCts?.Dispose(); } catch { }
        try { _selfProcess.Dispose(); } catch { }
        try { PlayerWebView?.Dispose(); } catch { }
        base.OnClosing(e);
    }

    private void RestoreSavedGeometry()
    {
        var s = _settings.Current;
        _isCompactMode = s.IsCompactMode;
        double w = _isCompactMode ? CompactWidth : ExpandedWidth;
        double h = _isCompactMode ? CompactHeight : ExpandedHeight;

        _isApplyingGeometry = true;
        try
        {
            Width = w;
            Height = h;
            ShowInTaskbar = !_isCompactMode;
            Topmost = s.WindowAlwaysOnTop;

            var area = SystemParameters.WorkArea;
            double rawLeft = s.PlayerWindowLeft ?? (area.Left + (area.Width - w) / 2.0);
            double rawTop = s.PlayerWindowTop ?? (area.Top + (area.Height - h) / 2.0);
            var (clampedLeft, clampedTop) = WindowBlurHelper.ClampToVisibleMonitors(this, rawLeft, rawTop, w, h);
            Left = clampedLeft;
            Top = clampedTop;

            if (_isCompactMode && s.CompactEdgeAutoHide && !string.IsNullOrEmpty(s.CompactDockedEdge))
            {
                _dockedEdge = s.CompactDockedEdge;
                _isEdgeCollapsed = false;
                var (eLeft, eTop) = ComputeEdgePosition(_dockedEdge, false, WindowBlurHelper.GetMonitorWorkAreaForWindow(this));
                Left = eLeft;
                Top = eTop;
                _edgeLastHoverTick = Environment.TickCount64 + 900;
                _edgeHoverTimer.Start();
            }
        }
        finally
        {
            _isApplyingGeometry = false;
        }
    }

    private void ApplyWindowSettingsFromConfig()
    {
        if (_isSettingsApplying) return;
        var s = _settings.Current;
        if (_isCompactMode != s.IsCompactMode)
            SetCompactMode(s.IsCompactMode, save: false);
        if (Topmost != s.WindowAlwaysOnTop)
            SetAlwaysOnTop(s.WindowAlwaysOnTop, save: false);
        WindowBlurHelper.SetWindowAlpha(this, s.WindowOpacityPercent / 100.0);
    }

    private void SaveCurrentWindowPosition()
    {
        if (_isCompactMode && !string.IsNullOrEmpty(_dockedEdge))
        {
            if (_isEdgeCollapsed) return;
            var work = WindowBlurHelper.GetMonitorWorkAreaForWindow(this);
            var (eLeft, eTop) = ComputeEdgePosition(_dockedEdge, false, work);
            _isSettingsApplying = true;
            try
            {
                _settings.Update(s =>
                {
                    s.PlayerWindowLeft = eLeft;
                    s.PlayerWindowTop = eTop;
                }, notify: false);
            }
            finally
            {
                _isSettingsApplying = false;
            }
            return;
        }

        var (clampedLeft, clampedTop) = WindowBlurHelper.ClampToVisibleMonitors(this, Left, Top, Width, Height);
        if (Math.Abs(clampedLeft - Left) > 0.5 || Math.Abs(clampedTop - Top) > 0.5)
        {
            _isApplyingGeometry = true;
            try
            {
                Left = clampedLeft;
                Top = clampedTop;
            }
            finally
            {
                _isApplyingGeometry = false;
            }
        }

        _isSettingsApplying = true;
        try
        {
            _settings.Update(s =>
            {
                s.PlayerWindowLeft = Left;
                s.PlayerWindowTop = Top;
            }, notify: false);
        }
        finally
        {
            _isSettingsApplying = false;
        }
    }

    public void SetCompactMode(bool compact, bool save = true)
    {
        _isCompactMode = compact;
        double targetW = compact ? CompactWidth : ExpandedWidth;
        double targetH = compact ? CompactHeight : ExpandedHeight;

        _edgeAnimTimer.Stop();
        _isApplyingGeometry = true;
        try
        {
            if (ShowInTaskbar != !compact)
                ShowInTaskbar = !compact;
            Width = targetW;
            Height = targetH;

            if (compact && _settings.Current.CompactEdgeAutoHide && !string.IsNullOrEmpty(_settings.Current.CompactDockedEdge))
            {
                _dockedEdge = _settings.Current.CompactDockedEdge;
                _isEdgeCollapsed = false;
                var work = WindowBlurHelper.GetMonitorWorkAreaForWindow(this);
                var (eLeft, eTop) = ComputeEdgePosition(_dockedEdge, false, work);
                Left = eLeft;
                Top = eTop;
                _edgeLastHoverTick = Environment.TickCount64 + 800;
                _edgeHoverTimer.Start();
            }
            else
            {
                _dockedEdge = "";
                _isEdgeCollapsed = false;
                _edgeHoverTimer.Stop();
                var (clampedLeft, clampedTop) = WindowBlurHelper.ClampToVisibleMonitors(this, Left, Top, targetW, targetH);
                Left = clampedLeft;
                Top = clampedTop;
            }
        }
        finally
        {
            _isApplyingGeometry = false;
        }

        Topmost = _settings.Current.WindowAlwaysOnTop;
        WindowBlurHelper.ApplyChromaWindowStyle(this, compact ? 12 : 16);
        WindowBlurHelper.SetWindowAlpha(this, _settings.Current.WindowOpacityPercent / 100.0);
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () => WindowBlurHelper.UpdateWin10RoundedRegion(this, _isCompactMode ? 12 : 16));

        if (save)
        {
            _isSettingsApplying = true;
            try
            {
                _settings.Update(s =>
                {
                    s.IsCompactMode = compact;
                    s.PlayerWindowLeft = Left;
                    s.PlayerWindowTop = Top;
                }, notify: true);
            }
            finally
            {
                _isSettingsApplying = false;
            }
        }

        _trayStateSyncAction();
        PostToWeb("windowMode", new { compact });
        PostToWeb("edgeDockState", new { dockedEdge = _dockedEdge, collapsed = _isEdgeCollapsed });
    }

    private (double Left, double Top) ComputeEdgePosition(string edge, bool collapsed, Rect work)
    {
        double w = Width > 0 ? Width : CompactWidth;
        double h = Height > 0 ? Height : CompactHeight;
        double safeTop = Math.Clamp(Top, work.Top + 4, Math.Max(work.Top + 4, work.Bottom - h - 4));
        double safeLeft = Math.Clamp(Left, work.Left + 4, Math.Max(work.Left + 4, work.Right - w - 4));

        return edge switch
        {
            "left" => (collapsed ? work.Left - w + EdgePeekPx : work.Left + 6, safeTop),
            "right" => (collapsed ? work.Right - EdgePeekPx : work.Right - w - 6, safeTop),
            "top" => (safeLeft, collapsed ? work.Top - h + EdgePeekPx : work.Top + 6),
            _ => (safeLeft, safeTop)
        };
    }

    private void EvaluateEdgeDockAfterDrag()
    {
        if (!_isCompactMode || !_settings.Current.CompactEdgeAutoHide)
            return;

        var work = WindowBlurHelper.GetMonitorWorkAreaForWindow(this);
        double w = Width > 0 ? Width : CompactWidth;
        double h = Height > 0 ? Height : CompactHeight;

        double dLeft = Left - work.Left;
        double dRight = work.Right - (Left + w);
        double dTop = Top - work.Top;

        string detected = "";
        double minDist = double.MaxValue;
        if (dLeft <= EdgeSnapThresholdPx && dLeft < minDist) { minDist = dLeft; detected = "left"; }
        if (dRight <= EdgeSnapThresholdPx && dRight < minDist) { minDist = dRight; detected = "right"; }
        if (dTop <= EdgeSnapThresholdPx && dTop < minDist) { detected = "top"; }

        if (!string.IsNullOrEmpty(detected))
        {
            _dockedEdge = detected;
            _isEdgeCollapsed = false;
            _edgeLastHoverTick = Environment.TickCount64 + 450;
            _edgeHoverTimer.Start();

            _isSettingsApplying = true;
            try { _settings.Update(s => s.CompactDockedEdge = detected, notify: false); }
            finally { _isSettingsApplying = false; }

            var (eLeft, eTop) = ComputeEdgePosition(detected, false, work);
            PostToWeb("edgeDockState", new { dockedEdge = _dockedEdge, collapsed = false });
            StartEdgeAnimation(eLeft, eTop);
        }
        else if (!string.IsNullOrEmpty(_dockedEdge) &&
                 dLeft > EdgeSnapThresholdPx * 1.8 &&
                 dRight > EdgeSnapThresholdPx * 1.8 &&
                 dTop > EdgeSnapThresholdPx * 1.8)
        {
            UndockFromEdge(animateInside: false);
        }
    }

    private void ToggleCompactEdgeDock()
    {
        if (!_isCompactMode) return;

        if (!string.IsNullOrEmpty(_dockedEdge))
        {
            UndockFromEdge(animateInside: true);
            return;
        }

        var work = WindowBlurHelper.GetMonitorWorkAreaForWindow(this);
        double w = Width > 0 ? Width : CompactWidth;

        double dLeft = Math.Max(0, Left - work.Left);
        double dRight = Math.Max(0, work.Right - (Left + w));
        double dTop = Math.Max(0, Top - work.Top);

        string edge = "right";
        double best = dRight;
        if (dLeft < best) { best = dLeft; edge = "left"; }
        if (dTop < best) { edge = "top"; }

        _dockedEdge = edge;
        _isSettingsApplying = true;
        try
        {
            _settings.Update(s =>
            {
                s.CompactEdgeAutoHide = true;
                s.CompactDockedEdge = edge;
            }, notify: false);
        }
        finally
        {
            _isSettingsApplying = false;
        }

        _edgeLastHoverTick = Environment.TickCount64;
        _edgeSuppressExpandUntilTick = Environment.TickCount64 + 650;
        _edgeHoverTimer.Start();
        AnimateEdgeCollapsed(true);
        PushWindowSettingsUpdate();
    }

    private void UndockFromEdge(bool animateInside)
    {
        var prevEdge = _dockedEdge;
        _dockedEdge = "";
        _isEdgeCollapsed = false;
        _edgeHoverTimer.Stop();

        _isSettingsApplying = true;
        try { _settings.Update(s => s.CompactDockedEdge = "", notify: false); }
        finally { _isSettingsApplying = false; }

        PostToWeb("edgeDockState", new { dockedEdge = "", collapsed = false });

        if (animateInside && !string.IsNullOrEmpty(prevEdge))
        {
            var work = WindowBlurHelper.GetMonitorWorkAreaForWindow(this);
            var (tLeft, tTop) = ComputeEdgePosition(prevEdge, false, work);
            if (prevEdge == "left") tLeft += 14;
            else if (prevEdge == "right") tLeft -= 14;
            else if (prevEdge == "top") tTop += 14;
            StartEdgeAnimation(tLeft, tTop);
        }
    }

    private void AnimateEdgeCollapsed(bool collapse)
    {
        if (string.IsNullOrEmpty(_dockedEdge)) return;
        if (_isEdgeCollapsed == collapse && _edgeAnimTimer.IsEnabled) return;

        _isEdgeCollapsed = collapse;
        var work = WindowBlurHelper.GetMonitorWorkAreaForWindow(this);
        var (targetLeft, targetTop) = ComputeEdgePosition(_dockedEdge, collapse, work);
        PostToWeb("edgeDockState", new { dockedEdge = _dockedEdge, collapsed = _isEdgeCollapsed });
        StartEdgeAnimation(targetLeft, targetTop);
    }

    private void StartEdgeAnimation(double targetLeft, double targetTop)
    {
        _edgeAnimTimer.Stop();
        _animStartLeft = Left;
        _animStartTop = Top;
        _animTargetLeft = targetLeft;
        _animTargetTop = targetTop;
        _animStartTick = Environment.TickCount64;

        if (Math.Abs(_animTargetLeft - _animStartLeft) < 0.5 && Math.Abs(_animTargetTop - _animStartTop) < 0.5)
        {
            _isApplyingGeometry = true;
            try { Left = targetLeft; Top = targetTop; }
            finally { _isApplyingGeometry = false; }
            return;
        }

        _edgeAnimTimer.Start();
    }

    private void OnEdgeAnimTick()
    {
        double raw = Math.Clamp((Environment.TickCount64 - _animStartTick) / EdgeAnimDurationMs, 0.0, 1.0);
        double eased = 1.0 - Math.Pow(1.0 - raw, 3);

        _isApplyingGeometry = true;
        try
        {
            Left = _animStartLeft + (_animTargetLeft - _animStartLeft) * eased;
            Top = _animStartTop + (_animTargetTop - _animStartTop) * eased;
        }
        finally
        {
            _isApplyingGeometry = false;
        }

        if (raw >= 1.0)
            _edgeAnimTimer.Stop();
    }

    private void OnEdgeHoverTick()
    {
        if (!IsVisible || WindowState == WindowState.Minimized || !_isCompactMode || string.IsNullOrEmpty(_dockedEdge) || _isDraggingWindow)
            return;

        if (!WindowBlurHelper.TryGetCursorScreenDipPosition(this, out var pt))
            return;

        double pad = _isEdgeCollapsed ? 4.0 : 8.0;
        double w = Width > 0 ? Width : CompactWidth;
        double h = Height > 0 ? Height : CompactHeight;
        bool cursorInside = pt.X >= Left - pad && pt.X <= Left + w + pad &&
                            pt.Y >= Top - pad && pt.Y <= Top + h + pad;

        long now = Environment.TickCount64;
        if (_isEdgeCollapsed)
        {
            if (cursorInside && now >= _edgeSuppressExpandUntilTick)
            {
                _edgeLastHoverTick = now;
                AnimateEdgeCollapsed(false);
            }
        }
        else
        {
            if (cursorInside || _isWebPointerHeld)
                _edgeLastHoverTick = now;
            else if (now - _edgeLastHoverTick >= 550)
                AnimateEdgeCollapsed(true);
        }
    }

    public void ShowSettingsSection(string section = "settings")
    {
        RestoreFromTray();
        if (_isCompactMode)
            SetCompactMode(false, save: true);
        PostToWeb("showTab", new { tab = "settings", section });
    }

    public void SetAlwaysOnTop(bool onTop, bool save = true)
    {
        Topmost = onTop;
        if (save)
        {
            _isSettingsApplying = true;
            try
            {
                _settings.Update(s =>
                {
                    s.WindowAlwaysOnTop = onTop;
                }, notify: true);
            }
            finally
            {
                _isSettingsApplying = false;
            }
        }

        _trayStateSyncAction();
    }

    public void SetWindowOpacity(int percent, bool save = true)
    {
        int clamped = Math.Clamp(percent, 75, 100);
        WindowBlurHelper.SetWindowAlpha(this, clamped / 100.0);
        if (save)
        {
            _isSettingsApplying = true;
            try
            {
                _settings.Update(s => s.WindowOpacityPercent = clamped, notify: false);
            }
            finally
            {
                _isSettingsApplying = false;
            }
        }
    }

    public void CenterOnPrimaryMonitor()
    {
        _edgeAnimTimer.Stop();
        _edgeHoverTimer.Stop();
        _dockedEdge = "";
        _isEdgeCollapsed = false;
        _isSettingsApplying = true;
        try { _settings.Update(s => s.CompactDockedEdge = "", notify: false); }
        finally { _isSettingsApplying = false; }

        var area = SystemParameters.WorkArea;
        _isApplyingGeometry = true;
        try
        {
            double targetLeft = area.Left + (area.Width - Width) / 2.0;
            double targetTop = area.Top + (area.Height - Height) / 2.0;
            var (clampedLeft, clampedTop) = WindowBlurHelper.ClampToVisibleMonitors(this, targetLeft, targetTop, Width, Height);
            Left = clampedLeft;
            Top = clampedTop;
        }
        finally
        {
            _isApplyingGeometry = false;
        }

        SaveCurrentWindowPosition();
        PostToWeb("edgeDockState", new { dockedEdge = "", collapsed = false });
        if (!IsVisible)
            RestoreFromTray();
    }

    public void HideToTray()
    {
        if (IsVisible)
            Hide();
        UpdateSuspensionState(forceSuspended: true);
    }

    private async void PlayerWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Если фокус внутри WebView2, клавиши обрабатывает сам Chromium-слой (с учётом <input>)
        if (PlayerWebView != null && PlayerWebView.IsKeyboardFocusWithin)
            return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var mods = Keyboard.Modifiers;

        switch (key)
        {
            case Key.Space when mods == ModifierKeys.None:
                e.Handled = true;
                if (_controller.IsLocalActive || (_player.CurrentTrack != null && _externalActiveTrack == null))
                    _player.TogglePause();
                else
                    await _controller.SendCommandAsync("toggle");
                break;

            case Key.Right when mods == ModifierKeys.Shift:
                e.Handled = true;
                await _controller.SeekRelativeMsAsync(15000);
                break;

            case Key.Left when mods == ModifierKeys.Shift:
                e.Handled = true;
                await _controller.SeekRelativeMsAsync(-15000);
                break;

            case Key.Right when mods == ModifierKeys.None:
                e.Handled = true;
                if (_controller.IsLocalActive || (_player.CurrentTrack != null && _externalActiveTrack == null))
                    await _player.NextAsync();
                else
                    await _controller.SendCommandAsync("next");
                break;

            case Key.Left when mods == ModifierKeys.None:
                e.Handled = true;
                if (_controller.IsLocalActive || (_player.CurrentTrack != null && _externalActiveTrack == null))
                    await _player.PrevAsync();
                else
                    await _controller.SendCommandAsync("prev");
                break;

            case Key.M when mods == ModifierKeys.None:
                e.Handled = true;
                SetCompactMode(!_isCompactMode, save: true);
                break;
        }
    }

    public void RestoreFromTray()
    {
        ShowInTaskbar = !_isCompactMode;
        Show();
        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;
        Activate();
        Topmost = _settings.Current.WindowAlwaysOnTop;
        WindowBlurHelper.ApplyChromaWindowStyle(this, _isCompactMode ? 12 : 16);
        WindowBlurHelper.SetWindowAlpha(this, _settings.Current.WindowOpacityPercent / 100.0);
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () => WindowBlurHelper.UpdateWin10RoundedRegion(this, _isCompactMode ? 12 : 16));
        UpdateSuspensionState();
    }

    private void UpdateSuspensionState(bool forceSuspended = false)
    {
        bool visible = !forceSuspended && IsVisible && WindowState != WindowState.Minimized;
        bool nextSuspended = !visible;
        if (_isUiSuspended == nextSuspended)
        {
            if (visible && _activeTab == "settings" && !_telemetryTimer.IsEnabled)
                _telemetryTimer.Start();
            return;
        }

        _isUiSuspended = nextSuspended;
        _controller.SetUiVisible(visible);

        if (_isUiSuspended)
        {
            _telemetryTimer.Stop();
            _edgeHoverTimer.Stop();
            _edgeAnimTimer.Stop();
            PostToWeb("uiVisibility", new { visible = false });
            _suspendTimer.Stop();
            _suspendTimer.Start();
        }
        else
        {
            _suspendTimer.Stop();
            if (PlayerWebView.Visibility != Visibility.Visible)
                PlayerWebView.Visibility = Visibility.Visible;

            if (_isWebViewSuspended && PlayerWebView.CoreWebView2 != null)
            {
                try { PlayerWebView.CoreWebView2.Resume(); } catch { }
                _isWebViewSuspended = false;
            }

            if (_isCompactMode && !string.IsNullOrEmpty(_dockedEdge))
            {
                _edgeLastHoverTick = Environment.TickCount64 + 600;
                _edgeHoverTimer.Start();
            }

            PostToWeb("uiVisibility", new { visible = true });
            PushResumeSnapshot();
            if (_activeTab == "settings")
            {
                PushTelemetrySample();
                _telemetryTimer.Start();
            }
        }
    }

    private async void PlayerWindow_Activated(object? sender, EventArgs e)
    {
        var pending = _pendingBrowserAuthProvider;
        if (string.IsNullOrEmpty(pending) || _isAuthBusy || _clipboardWatchBusy)
            return;

        await Task.Delay(150);
        if (_pendingBrowserAuthProvider != pending || _isAuthBusy || _clipboardWatchBusy)
            return;

        await ProcessCapturedClipboardAsync(pending, SafeGetClipboardText().Trim());
    }

    private async Task OnClipboardWatchTickAsync()
    {
        if (_clipboardWatchBusy || _isAuthBusy)
            return;

        var pending = _pendingBrowserAuthProvider;
        if (string.IsNullOrEmpty(pending))
        {
            _clipboardWatchTimer.Stop();
            return;
        }

        if (DateTime.UtcNow > _clipboardWatchDeadlineUtc)
        {
            _clipboardWatchTimer.Stop();
            _pendingBrowserAuthProvider = null;
            return;
        }

        await ProcessCapturedClipboardAsync(pending, SafeGetClipboardText().Trim());
    }

    private async Task ProcessCapturedClipboardAsync(string provider, string clip)
    {
        if (_clipboardWatchBusy || _isAuthBusy || string.IsNullOrWhiteSpace(clip))
            return;
        if (string.Equals(clip, _clipboardBeforeAuth, StringComparison.Ordinal) ||
            string.Equals(clip, _lastTestedClipboard, StringComparison.Ordinal))
        {
            return;
        }

        if (!LooksLikeAuthPayload(provider, clip))
            return;

        _clipboardWatchBusy = true;
        _lastTestedClipboard = clip;
        try
        {
            if (provider == "youtube")
            {
                var dir = Path.GetDirectoryName(YtDlpMusicProvider.YouTubeCookiesPath)!;
                Directory.CreateDirectory(dir);
                File.WriteAllText(
                    YtDlpMusicProvider.YouTubeCookiesPath,
                    clip,
                    new System.Text.UTF8Encoding(false));
                _pendingBrowserAuthProvider = null;
                _clipboardWatchTimer.Stop();
                RestoreClipboardAfterCapture(provider);
                PushAccountsUpdate();
                const string ytMsg = "YouTube cookies автоматически подключены из буфера обмена.";
                PostToWeb("authResult", new { provider = "youtube", ok = true, message = ytMsg });
                PostToWeb("status", new { message = ytMsg });
                return;
            }

            var (ok, _) = await CompleteProviderAuthInternalAsync(provider, clip);
            if (ok)
                RestoreClipboardAfterCapture(provider);
        }
        finally
        {
            _clipboardWatchBusy = false;
        }
    }

    private void RestoreClipboardAfterCapture(string provider)
    {
        try
        {
            if (!string.IsNullOrEmpty(_clipboardBeforeAuth) && !LooksLikeAuthPayload(provider, _clipboardBeforeAuth))
                Clipboard.SetText(_clipboardBeforeAuth);
            else
                Clipboard.Clear();
        }
        catch { }
    }

    private static string SafeGetClipboardText()
    {
        try
        {
            return Clipboard.ContainsText() ? (Clipboard.GetText() ?? "") : "";
        }
        catch
        {
            return "";
        }
    }

    private static void SafeSetClipboardText(string text)
    {
        try
        {
            Clipboard.SetText(text ?? "");
        }
        catch { }
    }

    private static bool LooksLikeAuthPayload(string provider, string text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length < 16) return false;
        var t = text.Trim();
        return provider switch
        {
            "yandex" => t.Contains("access_token=", StringComparison.OrdinalIgnoreCase) ||
                        t.StartsWith("y0_", StringComparison.OrdinalIgnoreCase),
            "vk" => t.Contains("access_token=", StringComparison.OrdinalIgnoreCase) ||
                    t.StartsWith("vk1.a", StringComparison.OrdinalIgnoreCase) ||
                    t.Contains("remixsid=", StringComparison.OrdinalIgnoreCase),
            "spotify" => t.Contains("sp_dc=", StringComparison.OrdinalIgnoreCase) ||
                         (t.StartsWith("AQ", StringComparison.Ordinal) && t.Length >= 80 && !t.Contains(' ')) ||
                         (t.StartsWith("BQ", StringComparison.Ordinal) && t.Length >= 40 && !t.Contains(' ')),
            "youtube" => (text.Contains(".youtube.com\t", StringComparison.OrdinalIgnoreCase) ||
                          text.Contains(".google.com\t", StringComparison.OrdinalIgnoreCase)) &&
                         (text.Contains("SAPISID", StringComparison.OrdinalIgnoreCase) ||
                          text.Contains("__Secure-3PAPISID", StringComparison.OrdinalIgnoreCase) ||
                          text.Contains("\tSID\t", StringComparison.OrdinalIgnoreCase)),
            _ => false
        };
    }

    private void StartClipboardWatcher(string provider)
    {
        _pendingBrowserAuthProvider = provider;
        _clipboardBeforeAuth = SafeGetClipboardText().Trim();
        _lastTestedClipboard = "";
        _clipboardWatchDeadlineUtc = DateTime.UtcNow.AddMinutes(5);
        _clipboardWatchTimer.Start();
    }

    private void CancelActiveAuthFlow()
    {
        var prev = _authFlowCts;
        _authFlowCts = null;
        if (prev != null)
        {
            try { prev.Cancel(); } catch { }
            try { prev.Dispose(); } catch { }
        }
        _clipboardWatchTimer.Stop();
        _pendingBrowserAuthProvider = null;
        _lastTestedClipboard = "";
    }

    private void OpenExternalBrowserAuth(string provider)
    {
        CancelActiveAuthFlow();
        _authFlowCts = new CancellationTokenSource();
        var ct = _authFlowCts.Token;

        switch (provider)
        {
            case "yandex":
                StartClipboardWatcher("yandex");
                _ = StartYandexDeviceAuthUiFlowAsync(ct);
                break;

            case "vk":
                Dispatcher.BeginInvoke(DispatcherPriority.Background, () => _ = OpenVkBrowserAuthWindowAsync());
                break;

            case "spotify":
                StartClipboardWatcher("spotify");
                _ = StartSpotifyPkceUiFlowAsync(ct);
                break;

            case "youtube":
                Dispatcher.BeginInvoke(DispatcherPriority.Background, () => _ = OpenYouTubeBrowserAuthWindowAsync());
                break;
        }
    }

    private async Task OpenVkBrowserAuthWindowAsync()
    {
        if (_activeAuthWindow != null)
        {
            try { _activeAuthWindow.Activate(); } catch { }
            return;
        }
        if (_isAuthBusy) return;
        CancelActiveAuthFlow();

        PostToWeb("authPending", new { provider = "vk", active = false, busy = false });
        PostToWeb("status", new { message = "Откройте QR-код или войдите в аккаунт VK в отдельном окне..." });

        BrowserAuthResult res;
        try
        {
            res = await VkAuthWindow.ShowAuthAsync(
                this,
                BrowserAuthMode.Vk,
                clearSession: _vkClearedSession,
                onCreated: w => _activeAuthWindow = w);
        }
        finally
        {
            _activeAuthWindow = null;
        }

        if (!res.Completed || (string.IsNullOrWhiteSpace(res.Token) && string.IsNullOrWhiteSpace(res.CookieHeader)))
        {
            PostToWeb("status", new { message = "Вход в VK Музыку отменён" });
            return;
        }

        _isAuthBusy = true;
        bool ok = false;
        string message = "Не удалось выполнить вход в VK Музыку";
        PostToWeb("status", new { message = "Проверка сессии VK Музыки..." });

        try
        {
            (ok, message) = await _vk.VerifyAndSaveSessionAsync(res.Token, res.CookieHeader, res.UserId, res.UserName);
            if (ok)
            {
                _vkClearedSession = false;
                PostToWeb("status", new { message = $"{message}. Загрузка плейлистов ВК..." });
                var pls = await _vk.FetchUserPlaylistsAsync();
                _library.ReplaceExternalPlaylists("vk", pls);
                message = $"{message} • Плейлистов: {pls.Count}, треков: {pls.Sum(p => p.Tracks.Count)}";
            }
        }
        catch (Exception ex)
        {
            ok = false;
            message = $"Ошибка авторизации VK: {ex.Message}";
        }
        finally
        {
            _isAuthBusy = false;
        }

        PushAccountsUpdate();
        PostToWeb("authResult", new { provider = "vk", ok, message });
        PostToWeb("status", new { message });
    }

    private async Task OpenYouTubeBrowserAuthWindowAsync()
    {
        if (_activeAuthWindow != null)
        {
            try { _activeAuthWindow.Activate(); } catch { }
            return;
        }
        if (_isAuthBusy) return;
        CancelActiveAuthFlow();

        PostToWeb("authPending", new { provider = "youtube", active = false, busy = false });
        PostToWeb("status", new { message = "Войдите в аккаунт Google / YouTube в отдельном окне..." });

        BrowserAuthResult res;
        try
        {
            res = await VkAuthWindow.ShowAuthAsync(
                this,
                BrowserAuthMode.YouTube,
                clearSession: _youtubeClearedSession,
                onCreated: w => _activeAuthWindow = w);
        }
        finally
        {
            _activeAuthWindow = null;
        }

        if (!res.Completed)
        {
            PostToWeb("status", new { message = "Вход в YouTube отменён" });
            return;
        }

        _youtubeClearedSession = false;
        PushAccountsUpdate();
        PostToWeb("authResult", new { provider = "youtube", ok = true, message = "YouTube cookies сохранены" });
        PostToWeb("status", new { message = "YouTube cookies подключены" });
    }

    private bool TryOpenUrlInBrowser(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            PostToWeb("status", new { message = $"Не удалось открыть браузер: {ex.Message}" });
            return false;
        }
    }

    private async Task StartYandexDeviceAuthUiFlowAsync(CancellationToken ct)
    {
        try
        {
            PostToWeb("authPending", new { provider = "yandex", active = true, busy = true, mode = "device" });
            PostToWeb("status", new { message = "Запрос одноразового кода входа Яндекс..." });

            var session = await _yandex.StartDeviceAuthAsync(ct);
            if (ct.IsCancellationRequested) return;

            SafeSetClipboardText(session.UserCode);
            _clipboardBeforeAuth = session.UserCode;

            TryOpenUrlInBrowser(session.VerificationUrl);

            PostToWeb("authPending", new
            {
                provider = "yandex",
                active = true,
                busy = false,
                mode = "device",
                userCode = session.UserCode,
                verificationUrl = session.VerificationUrl
            });
            PostToWeb("status", new { message = $"Код {session.UserCode} скопирован! Вставьте его (Ctrl+V) в открывшейся вкладке Яндекса" });

            var token = await _yandex.PollDeviceTokenAsync(session, ct);
            if (ct.IsCancellationRequested) return;

            await CompleteProviderAuthInternalAsync("yandex", token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            PostToWeb("authResult", new { provider = "yandex", ok = false, message = ex.Message });
            PostToWeb("status", new { message = ex.Message });
        }
    }

    private async Task StartSpotifyPkceUiFlowAsync(CancellationToken ct)
    {
        try
        {
            PostToWeb("authPending", new { provider = "spotify", active = true, busy = false, mode = "pkce" });
            PostToWeb("status", new { message = "Подтвердите вход Spotify в открывшемся окне браузера..." });

            var (ok, msg) = await _spotify.AuthenticateViaSystemBrowserPkceAsync(
                url => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }),
                ct);

            if (ct.IsCancellationRequested) return;

            if (ok)
            {
                _pendingBrowserAuthProvider = null;
                _clipboardWatchTimer.Stop();
                PostToWeb("status", new { message = $"{msg}. Загрузка медиатеки Spotify..." });
                try
                {
                    var pls = await _spotify.FetchUserPlaylistsAsync();
                    _library.ReplaceExternalPlaylists("spotify", pls);
                    msg = $"{msg} • Плейлистов: {pls.Count}, треков: {pls.Sum(p => p.Tracks.Count)}";
                }
                catch { }
            }

            PushAccountsUpdate();
            PostToWeb("authResult", new { provider = "spotify", ok, message = msg });
            PostToWeb("status", new { message = msg });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            PushAccountsUpdate();
            var errMsg = $"Ошибка Spotify OAuth: {ex.Message}";
            PostToWeb("authResult", new { provider = "spotify", ok = false, message = errMsg });
            PostToWeb("status", new { message = errMsg });
        }
    }

    private Task CompleteProviderAuthAsync(string provider, string rawInput)
        => CompleteProviderAuthInternalAsync(provider, rawInput);

    private async Task<(bool Ok, string Message)> CompleteProviderAuthInternalAsync(string provider, string rawInput)
    {
        if (_isAuthBusy)
            return (false, "Проверка авторизации уже выполняется.");

        var clean = (rawInput ?? "").Trim();
        if (string.IsNullOrWhiteSpace(clean))
        {
            const string emptyMsg = "Вставьте токен, cookie или итоговую ссылку из браузера.";
            PostToWeb("authResult", new { provider, ok = false, message = emptyMsg });
            return (false, emptyMsg);
        }

        _isAuthBusy = true;
        PostToWeb("authPending", new { provider, active = true, busy = true });
        PostToWeb("status", new { message = $"Проверка авторизации ({provider})..." });

        bool ok = false;
        string message = "Неизвестный сервис";

        try
        {
            switch (provider)
            {
                case "yandex":
                    (ok, message) = await _yandex.VerifyAndSaveTokenAsync(clean);
                    if (ok)
                    {
                        CancelActiveAuthFlow();
                        PostToWeb("status", new { message = $"{message}. Загрузка плейлистов..." });
                        var pls = await _yandex.FetchUserPlaylistsAsync();
                        _library.ReplaceExternalPlaylists("yandex", pls);
                        message = $"{message} • Плейлистов: {pls.Count}, треков: {pls.Sum(p => p.Tracks.Count)}";
                    }
                    break;

                case "vk":
                    (ok, message) = await _vk.VerifyAndSaveSessionAsync(clean, null, 0, null);
                    if (ok)
                    {
                        CancelActiveAuthFlow();
                        PostToWeb("status", new { message = $"{message}. Загрузка плейлистов ВК..." });
                        var pls = await _vk.FetchUserPlaylistsAsync();
                        _library.ReplaceExternalPlaylists("vk", pls);
                        message = $"{message} • Плейлистов: {pls.Count}, треков: {pls.Sum(p => p.Tracks.Count)}";
                    }
                    break;

                case "spotify":
                    (ok, message) = await _spotify.VerifyAndSaveSessionAsync(clean, null, 0, null);
                    if (ok)
                    {
                        CancelActiveAuthFlow();
                        PostToWeb("status", new { message = $"{message}. Загрузка медиатеки Spotify..." });
                        var pls = await _spotify.FetchUserPlaylistsAsync();
                        _library.ReplaceExternalPlaylists("spotify", pls);
                        message = $"{message} • Плейлистов: {pls.Count}, треков: {pls.Sum(p => p.Tracks.Count)}";
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            ok = false;
            message = $"Ошибка авторизации: {ex.Message}";
        }
        finally
        {
            _isAuthBusy = false;
        }

        PushAccountsUpdate();
        PostToWeb("authResult", new { provider, ok, message });
        PostToWeb("status", new { message });
        return (ok, message);
    }

    private async Task SuspendWebViewInTrayAsync()
    {
        _suspendTimer.Stop();
        if (!_isUiSuspended || !_webViewReady || _isWebViewSuspended || PlayerWebView.CoreWebView2 == null)
            return;

        try
        {
            PlayerWebView.Visibility = Visibility.Collapsed;
            bool ok = await PlayerWebView.CoreWebView2.TrySuspendAsync();
            if (_isUiSuspended)
            {
                _isWebViewSuspended = ok;
                _controller.TrimCaches();
                WindowBlurHelper.TrimWorkingSet();
            }
            else if (ok)
            {
                try { PlayerWebView.CoreWebView2.Resume(); } catch { }
                PlayerWebView.Visibility = Visibility.Visible;
            }
        }
        catch { }
    }

    private async Task InitWebViewAsync()
    {
        try
        {
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "OmniWave",
                "WebView2Player");
            Directory.CreateDirectory(userDataFolder);

            PlayerWebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 17, 18, 22);
            const string perfArgs = "--disable-features=msWebOOUI,msPdfOOUI,msSmartScreenProtection,Translate --disable-component-update --disable-domain-reliability --disable-background-networking";
            var wvProxyArgs = ProxyService.GetWebView2ProxyArguments();
            string combinedArgs = string.IsNullOrWhiteSpace(wvProxyArgs) ? perfArgs : $"{perfArgs} {wvProxyArgs}";

            PlayerWebView.CreationProperties = new CoreWebView2CreationProperties
            {
                UserDataFolder = userDataFolder,
                AdditionalBrowserArguments = combinedArgs
            };

            await PlayerWebView.EnsureCoreWebView2Async();
            PlayerWebView.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 17, 18, 22);

            var wvSettings = PlayerWebView.CoreWebView2.Settings;
            wvSettings.AreDefaultContextMenusEnabled = false;
            wvSettings.IsStatusBarEnabled = false;
            wvSettings.IsZoomControlEnabled = false;
            wvSettings.IsGeneralAutofillEnabled = false;
            wvSettings.IsPasswordAutosaveEnabled = false;
            wvSettings.AreBrowserAcceleratorKeysEnabled = false;
#if !DEBUG
            wvSettings.AreDevToolsEnabled = false;
#endif

            PlayerWebView.WebMessageReceived += (_, ev) =>
            {
                try { HandleWebMessage(ev.WebMessageAsJson); } catch { }
            };

            FallbackLoadingText.Visibility = Visibility.Collapsed;
            PlayerWebView.NavigateToString(PlayerChromaCardHtml.GetHtml(WindowBlurHelper.IsWindows11OrLater));
        }
        catch (Exception ex)
        {
            FallbackLoadingText.Text = $"WebView2 недоступен: {ex.Message}";
        }
    }

    private void PostToWeb(string type, object payload)
    {
        if (!_webViewReady || _isWebViewSuspended || PlayerWebView.CoreWebView2 == null) return;

        try
        {
            var payloadJson = JsonSerializer.SerializeToElement(payload, JsonOptions);
            var dict = new Dictionary<string, object?> { ["type"] = type };
            if (payloadJson.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in payloadJson.EnumerateObject())
                    dict[prop.Name] = prop.Value;
            }
            var json = JsonSerializer.Serialize(dict, JsonOptions);
            PlayerWebView.CoreWebView2.PostWebMessageAsString(json);
        }
        catch { }
    }

    private async void HandleWebMessage(string rawJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(rawJson);
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.String)
            {
                var inner = root.GetString();
                if (string.IsNullOrWhiteSpace(inner)) return;
                using var innerDoc = JsonDocument.Parse(inner);
                await DispatchWebCommandAsync(innerDoc.RootElement);
            }
            else if (root.ValueKind == JsonValueKind.Object)
            {
                await DispatchWebCommandAsync(root);
            }
        }
        catch { }
    }

    private async Task DispatchWebCommandAsync(JsonElement root)
    {
        if (!root.TryGetProperty("type", out var typeEl) || typeEl.ValueKind != JsonValueKind.String)
            return;

        var type = typeEl.GetString() ?? "";
        switch (type)
        {
            case "ready":
                _webViewReady = true;
                PushFullInitState();
                break;

            case "window.dragStart":
                WindowBlurHelper.StartWindowDrag(this);
                break;

            case "window.minimize":
                if (_settings.Current.MinimizeToTray)
                    HideToTray();
                else
                    WindowState = WindowState.Minimized;
                break;

            case "window.hideToTray":
                HideToTray();
                break;

            case "window.closeAction":
                if (_settings.Current.MinimizeToTray)
                    HideToTray();
                else
                    _requestExitAction();
                break;

            case "window.setCompactMode":
                if (root.TryGetProperty("compact", out var cmpEl))
                    SetCompactMode(cmpEl.GetBoolean(), save: true);
                else
                    SetCompactMode(!_isCompactMode, save: true);
                break;

            case "window.setAlwaysOnTop":
                if (root.TryGetProperty("alwaysOnTop", out var topEl))
                    SetAlwaysOnTop(topEl.GetBoolean(), save: true);
                else
                    SetAlwaysOnTop(!_settings.Current.WindowAlwaysOnTop, save: true);
                break;

            case "window.setOpacity":
                if (root.TryGetProperty("opacity", out var opEl) && opEl.ValueKind == JsonValueKind.Number)
                    SetWindowOpacity((int)Math.Round(opEl.GetDouble()), save: true);
                break;

            case "window.setChromaIntensity":
                if (root.TryGetProperty("intensity", out var chEl) && chEl.ValueKind == JsonValueKind.Number)
                {
                    int val = Math.Clamp((int)Math.Round(chEl.GetDouble()), 0, 100);
                    _isSettingsApplying = true;
                    try { _settings.Update(s => s.ChromaIntensity = val, notify: false); }
                    finally { _isSettingsApplying = false; }
                }
                break;

            case "window.setMinimizeToTray":
                if (root.TryGetProperty("enabled", out var mtEl))
                {
                    bool enabled = mtEl.GetBoolean();
                    _isSettingsApplying = true;
                    try { _settings.Update(s => s.MinimizeToTray = enabled, notify: true); }
                    finally { _isSettingsApplying = false; }
                }
                break;

            case "window.setAutoStart":
                if (root.TryGetProperty("enabled", out var asEl))
                {
                    _settings.SetAutoStart(asEl.GetBoolean());
                    PushWindowSettingsUpdate();
                }
                break;

            case "window.setZeroBlur":
                if (root.TryGetProperty("enabled", out var zbEl))
                {
                    bool enabled = zbEl.GetBoolean();
                    _isSettingsApplying = true;
                    try { _settings.Update(s => s.ZeroBlurMode = enabled, notify: true); }
                    finally { _isSettingsApplying = false; }
                }
                break;

            case "ui.tabChanged":
                _activeTab = ReadStr(root, "tab") ?? "search";
                if (_activeTab == "settings" && !_isUiSuspended)
                {
                    PushTelemetrySample();
                    _telemetryTimer.Start();
                    if (!_systemCheckInitialized)
                    {
                        _systemCheckInitialized = true;
                        _ = CheckYtDlpForWebAsync();
                        _ = CheckUpdateForWebAsync();
                    }
                }
                else
                {
                    _telemetryTimer.Stop();
                }
                break;

            case "window.setPointerHold":
                _isWebPointerHeld = root.TryGetProperty("active", out var phEl) && phEl.ValueKind == JsonValueKind.True;
                if (!_isWebPointerHeld && _isCompactMode && !string.IsNullOrEmpty(_dockedEdge))
                    _edgeLastHoverTick = Environment.TickCount64;
                break;

            case "window.toggleEdgeDock":
                ToggleCompactEdgeDock();
                break;

            case "window.setCompactEdgeAutoHide":
                if (root.TryGetProperty("enabled", out var ehEl))
                {
                    bool enabled = ehEl.ValueKind == JsonValueKind.True;
                    _isSettingsApplying = true;
                    try { _settings.Update(s => s.CompactEdgeAutoHide = enabled, notify: false); }
                    finally { _isSettingsApplying = false; }
                    if (!enabled && !string.IsNullOrEmpty(_dockedEdge))
                        UndockFromEdge(animateInside: true);
                    PushWindowSettingsUpdate();
                }
                break;

            case "window.setStartMinimized":
                if (root.TryGetProperty("enabled", out var smEl))
                {
                    bool enabled = smEl.ValueKind == JsonValueKind.True;
                    _isSettingsApplying = true;
                    try { _settings.Update(s => s.StartMinimizedToIsland = enabled, notify: false); }
                    finally { _isSettingsApplying = false; }
                    PushWindowSettingsUpdate();
                }
                break;

            case "window.setAutoPauseOnSwitch":
                if (root.TryGetProperty("enabled", out var apEl))
                {
                    bool enabled = apEl.ValueKind == JsonValueKind.True;
                    _isSettingsApplying = true;
                    try { _settings.Update(s => s.AutoPauseOnSwitch = enabled, notify: false); }
                    finally { _isSettingsApplying = false; }
                    PushWindowSettingsUpdate();
                }
                break;

            case "window.center":
                CenterOnPrimaryMonitor();
                break;

            case "window.openSettings":
                ShowSettingsSection("proxy");
                break;

            case "window.openAccounts":
                ShowSettingsSection("accounts");
                break;

            case "yandex.startWave":
                await StartYandexMyWaveAsync();
                break;

            case "vk.startMix":
                await StartVkMixAsync();
                break;

            case "search.run":
            {
                var q = ReadStr(root, "query") ?? "";
                var filter = ReadStr(root, "filter") ?? "all";
                _activeSearchFilter = filter;
                await PerformSearchAsync(q);
                break;
            }

            case "search.loadMore":
            {
                var filter = ReadStr(root, "filter") ?? "all";
                _activeSearchFilter = filter;
                await LoadMoreAsync();
                break;
            }

            case "search.selectVariant":
            {
                var groupKey = ReadStr(root, "groupKey");
                var trackKey = ReadStr(root, "trackKey");
                bool play = root.TryGetProperty("play", out var plEl) && plEl.ValueKind == JsonValueKind.True;
                int gIdx = _currentSearchGroups.FindIndex(g => string.Equals(g.GroupKey, groupKey, StringComparison.OrdinalIgnoreCase));
                if (gIdx >= 0)
                {
                    var grp = _currentSearchGroups[gIdx];
                    int vIdx = grp.Variants.FindIndex(v => string.Equals(v.UniqueKey, trackKey, StringComparison.OrdinalIgnoreCase));
                    if (vIdx >= 0)
                    {
                        grp.SelectedIndex = vIdx;
                        _currentResults = _currentSearchGroups.Select(g => g.ActiveTrack).ToList();
                        if (play)
                            await _player.PlayFromQueueAsync(_currentResults, gIdx);
                    }
                }
                break;
            }

            case "search.reorder":
            {
                var src = ReadStr(root, "sourceKey");
                var dst = ReadStr(root, "targetKey");
                if (!string.IsNullOrWhiteSpace(src) && !string.IsNullOrWhiteSpace(dst) &&
                    !string.Equals(src, dst, StringComparison.OrdinalIgnoreCase))
                {
                    int srcIdx = _currentSearchGroups.FindIndex(g =>
                        string.Equals(g.GroupKey, src, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(g.ActiveTrack.UniqueKey, src, StringComparison.OrdinalIgnoreCase));
                    int dstIdx = _currentSearchGroups.FindIndex(g =>
                        string.Equals(g.GroupKey, dst, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(g.ActiveTrack.UniqueKey, dst, StringComparison.OrdinalIgnoreCase));
                    if (srcIdx >= 0 && dstIdx >= 0 && srcIdx != dstIdx)
                    {
                        var item = _currentSearchGroups[srcIdx];
                        _currentSearchGroups.RemoveAt(srcIdx);
                        _currentSearchGroups.Insert(Math.Clamp(dstIdx, 0, _currentSearchGroups.Count), item);
                        _currentResults = _currentSearchGroups.Select(g => g.ActiveTrack).ToList();
                    }
                }
                break;
            }

            case "search.playTrack":
            {
                var list = ReadStr(root, "list") ?? "search";
                var playlistId = ReadStr(root, "playlistId");
                var trackKey = ReadStr(root, "trackKey");
                int index = ReadInt(root, "index", -1);
                await PlayFromWebListAsync(list, playlistId, trackKey, index);
                break;
            }

            case "favorites.playAll":
                if (_favorites.Count > 0)
                    await _player.PlayFromQueueAsync(_favorites, 0);
                break;

            case "favorites.shuffle":
                if (_favorites.Count > 0)
                {
                    var shuffled = _favorites.OrderBy(_ => Random.Shared.Next()).ToList();
                    await _player.PlayFromQueueAsync(shuffled, 0);
                }
                break;

            case "favorites.exportYandex":
                await ExportFavoritesToYandexAsync();
                break;

            case "favorites.reorder":
            {
                var src = ReadStr(root, "sourceKey");
                var dst = ReadStr(root, "targetKey");
                if (!string.IsNullOrWhiteSpace(src) && !string.IsNullOrWhiteSpace(dst))
                    _library.ReorderFavoriteTrack(src, dst);
                break;
            }

            case "track.exportYandex":
            {
                var key = ReadStr(root, "trackKey");
                if (TryResolveTrack(key, out var track))
                    await ExportSingleTrackToYandexAsync(track);
                break;
            }

            case "playlists.create":
            {
                var name = ReadStr(root, "name")?.Trim();
                if (!string.IsNullOrEmpty(name))
                    _library.CreatePlaylist(name);
                break;
            }

            case "playlists.delete":
            {
                var id = ReadStr(root, "id");
                if (!string.IsNullOrWhiteSpace(id))
                    _library.DeletePlaylist(id);
                break;
            }

            case "playlists.togglePin":
            {
                var id = ReadStr(root, "id");
                if (!string.IsNullOrWhiteSpace(id))
                    _library.TogglePinPlaylist(id);
                break;
            }

            case "playlists.reorder":
            {
                var src = ReadStr(root, "sourceId");
                var dst = ReadStr(root, "targetId");
                if (!string.IsNullOrWhiteSpace(src) && !string.IsNullOrWhiteSpace(dst))
                    _library.ReorderPlaylist(src, dst);
                break;
            }

            case "playlists.reorderTrack":
            {
                var plId = ReadStr(root, "playlistId");
                var src = ReadStr(root, "sourceKey");
                var dst = ReadStr(root, "targetKey");
                if (!string.IsNullOrWhiteSpace(plId) && !string.IsNullOrWhiteSpace(src) && !string.IsNullOrWhiteSpace(dst))
                    _library.ReorderPlaylistTrack(plId, src, dst);
                break;
            }

            case "playlists.sync":
            {
                PostToWeb("status", new { message = "Синхронизация плейлистов из аккаунтов..." });
                var statusMsg = await _syncAccountsAction();
                PostToWeb("status", new { message = statusMsg });
                break;
            }

            case "playlists.play":
            {
                var id = ReadStr(root, "id");
                var pl = _library.GetPlaylists().FirstOrDefault(p => p.Id == id);
                if (pl != null && pl.Tracks.Count > 0)
                    await _player.PlayFromQueueAsync(pl.Tracks, 0);
                break;
            }

            case "playlists.removeTrack":
            {
                var plId = ReadStr(root, "playlistId");
                var key = ReadStr(root, "trackKey");
                if (!string.IsNullOrWhiteSpace(plId) && !string.IsNullOrWhiteSpace(key))
                    await RemoveTrackFromPlaylistSyncedAsync(plId, key);
                break;
            }


            case "track.toggleLike":
            {
                var key = ReadStr(root, "trackKey");
                if (TryResolveTrack(key, out var track))
                    _library.ToggleLike(track);
                break;
            }

            case "track.download":
            {
                var key = ReadStr(root, "trackKey");
                if (TryResolveTrack(key, out var track))
                    await HandleDownloadOrRevealAsync(track);
                break;
            }

            case "track.addToQueue":
            {
                var key = ReadStr(root, "trackKey");
                bool playNext = root.TryGetProperty("playNext", out var pn) && pn.GetBoolean();
                if (TryResolveTrack(key, out var track))
                    _player.AddToQueue(track, playNext);
                break;
            }

            case "player.togglePause":
                if (_controller.IsLocalActive || (_player.CurrentTrack != null && _externalActiveTrack == null))
                    _player.TogglePause();
                else
                    await _controller.SendCommandAsync("toggle");
                break;

            case "player.prev":
                if (_controller.IsLocalActive || (_player.CurrentTrack != null && _externalActiveTrack == null))
                    await _player.PrevAsync();
                else
                    await _controller.SendCommandAsync("prev");
                break;

            case "player.next":
                if (_controller.IsLocalActive || (_player.CurrentTrack != null && _externalActiveTrack == null))
                    await _player.NextAsync();
                else
                    await _controller.SendCommandAsync("next");
                break;

            case "player.seekFraction":
                if (root.TryGetProperty("fraction", out var fracEl) && fracEl.ValueKind == JsonValueKind.Number)
                {
                    if (_controller.IsLocalActive)
                        _player.SeekFraction(fracEl.GetDouble());
                    else
                        await _controller.SeekFractionAsync(fracEl.GetDouble());
                }
                break;


            case "player.seekRelative":
                if (root.TryGetProperty("deltaMs", out var dEl) && dEl.ValueKind == JsonValueKind.Number)
                    await _controller.SeekRelativeMsAsync((long)dEl.GetDouble());
                break;

            case "player.setVolume":
                if (root.TryGetProperty("volume", out var volEl) && volEl.ValueKind == JsonValueKind.Number)
                {
                    int vol = Math.Clamp((int)Math.Round(volEl.GetDouble()), 0, 100);
                    _controller.SetAppVolume(vol);
                }
                break;

            case "player.toggleMute":
                if (_player.Volume > 0)
                {
                    _savedVolumeBeforeMute = _player.Volume;
                    _controller.SetAppVolume(0);
                }
                else
                {
                    int restore = _savedVolumeBeforeMute > 0 ? _savedVolumeBeforeMute : 80;
                    _controller.SetAppVolume(restore);
                }
                break;

            case "player.toggleShuffle":
                _player.ToggleShuffle();
                break;

            case "player.cycleRepeat":
                _player.CycleRepeatMode();
                break;

            case "queue.playIndex":
            {
                int idx = ReadInt(root, "index", -1);
                if (idx >= 0) await _player.PlayQueueIndexAsync(idx);
                break;
            }

            case "queue.move":
            {
                int from = ReadInt(root, "from", -1);
                int to = ReadInt(root, "to", -1);
                if (from >= 0 && to >= 0) _player.MoveInQueue(from, to);
                break;
            }

            case "queue.remove":
            {
                int idx = ReadInt(root, "index", -1);
                if (idx >= 0) _player.RemoveFromQueue(idx);
                break;
            }

            case "queue.clear":
                _player.ClearQueue();
                break;

            case "eq.applyPreset":
            {
                var presetName = ReadStr(root, "preset");
                if (!string.IsNullOrWhiteSpace(presetName) &&
                    AudioPlayerService.EqPresets.TryGetValue(presetName, out var p))
                {
                    _player.ApplyEqualizer(true, p.Preamp, p.Bands);
                    _settings.Update(s =>
                    {
                        s.EqualizerEnabled = true;
                        s.EqualizerPreset = presetName;
                        s.EqualizerPreamp = p.Preamp;
                        s.EqualizerBands = p.Bands.ToArray();
                    }, notify: false);
                    PushEqState();
                }
                break;
            }

            case "eq.update":
            {
                bool enabled = root.TryGetProperty("enabled", out var enEl) && enEl.GetBoolean();
                var preset = ReadStr(root, "preset") ?? "Custom";
                float preamp = root.TryGetProperty("preamp", out var prEl) && prEl.ValueKind == JsonValueKind.Number
                    ? (float)prEl.GetDouble()
                    : 0f;
                var bands = new float[10];
                if (root.TryGetProperty("bands", out var bArr) && bArr.ValueKind == JsonValueKind.Array)
                {
                    int i = 0;
                    foreach (var el in bArr.EnumerateArray())
                    {
                        if (i >= 10) break;
                        bands[i++] = el.ValueKind == JsonValueKind.Number ? (float)el.GetDouble() : 0f;
                    }
                }
                _player.ApplyEqualizer(enabled, preamp, bands);
                _settings.Update(s =>
                {
                    s.EqualizerEnabled = enabled;
                    s.EqualizerPreset = preset;
                    s.EqualizerPreamp = preamp;
                    s.EqualizerBands = bands;
                }, notify: false);
                PushEqState();
                break;
            }

            case "eq.setRate":
                if (root.TryGetProperty("rate", out var rEl) && rEl.ValueKind == JsonValueKind.Number)
                {
                    float rate = Math.Clamp((float)rEl.GetDouble(), 0.75f, 1.5f);
                    _player.SetRate(rate);
                    _settings.Update(s => s.PlaybackRate = rate, notify: false);
                    PushEqState();
                }
                break;

            case "settings.saveProxy":
                SaveProxyFromWeb(root);
                break;

            case "settings.testProxy":
                await TestProxyFromWebAsync(root);
                break;

            case "settings.setDownloadFolder":
            {
                var path = ReadStr(root, "path")?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(path))
                    path = AppSettings.DefaultDownloadFolder;
                _settings.Update(s => s.DownloadFolder = path, notify: false);
                PushWindowSettingsUpdate();
                PostToWeb("status", new { message = $"Папка загрузок: {_settings.Current.DownloadFolder}" });
                break;
            }

            case "settings.pickDownloadFolder":
            {
                var dlg = new Microsoft.Win32.OpenFolderDialog
                {
                    Title = "Выберите папку для скачанных треков",
                    InitialDirectory = Directory.Exists(_settings.Current.DownloadFolder)
                        ? _settings.Current.DownloadFolder
                        : AppSettings.DefaultDownloadFolder
                };
                if (dlg.ShowDialog(this) == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
                {
                    _settings.Update(s => s.DownloadFolder = dlg.FolderName, notify: false);
                    PushWindowSettingsUpdate();
                    PostToWeb("status", new { message = $"Папка загрузок: {_settings.Current.DownloadFolder}" });
                }
                break;
            }

            case "settings.openDownloadFolder":
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
                break;

            case "system.checkYtDlp":
                await CheckYtDlpForWebAsync();
                break;

            case "system.checkUpdate":
                await CheckUpdateForWebAsync();
                break;

            case "system.installUpdate":
                await InstallUpdateForWebAsync();
                break;

            case "auth.openBrowser":
            {
                var prov = ReadStr(root, "provider") ?? "";
                OpenExternalBrowserAuth(prov);
                break;
            }

            case "auth.cancelBrowser":
                CancelActiveAuthFlow();
                PostToWeb("authPending", new { provider = ReadStr(root, "provider") ?? "", active = false });
                break;

            case "auth.submitInput":
            {
                var prov = ReadStr(root, "provider") ?? "";
                var input = ReadStr(root, "input") ?? "";
                await CompleteProviderAuthAsync(prov, input);
                break;
            }

            case "auth.pasteAndSubmit":
            {
                var prov = ReadStr(root, "provider") ?? "";
                var clip = SafeGetClipboardText().Trim();
                await CompleteProviderAuthAsync(prov, clip);
                break;
            }

            case "auth.copyCode":
            {
                var code = ReadStr(root, "code") ?? "";
                if (!string.IsNullOrWhiteSpace(code))
                {
                    SafeSetClipboardText(code);
                    PostToWeb("status", new { message = $"Код {code} скопирован в буфер обмена" });
                }
                break;
            }

            case "auth.disconnect":
            {
                var prov = ReadStr(root, "provider") ?? "";
                DisconnectProvider(prov);
                break;
            }

            case "auth.syncProvider":
            {
                var prov = ReadStr(root, "provider") ?? "";
                await SyncSingleProviderAsync(prov);
                break;
            }

            case "auth.importYandexJson":
                ImportYandexJsonFromWeb();
                break;

            case "auth.importYoutubeCookies":
                ImportYoutubeCookiesFromWeb();
                break;
        }
    }

    private object BuildWindowSettingsDto()
    {
        var s = _settings.Current;
        return new
        {
            compact = _isCompactMode,
            alwaysOnTop = s.WindowAlwaysOnTop,
            minimizeToTray = s.MinimizeToTray,
            compactEdgeAutoHide = s.CompactEdgeAutoHide,
            dockedEdge = _dockedEdge,
            edgeCollapsed = _isEdgeCollapsed,
            startMinimized = s.StartMinimizedToIsland,
            autoPauseOnSwitch = s.AutoPauseOnSwitch,
            autoStart = _settings.IsAutoStartEnabled(),
            chromaIntensity = s.ChromaIntensity,
            windowOpacity = s.WindowOpacityPercent,
            zeroBlurMode = s.ZeroBlurMode,
            downloadFolder = s.DownloadFolder
        };
    }

    private object BuildProxyDto(AppSettings s)
    {
        string text;
        string kind;
        if (!s.ProxyEnabled)
        {
            if (!string.IsNullOrWhiteSpace(s.ProxyHost))
            {
                text = $"Сохранено ({s.ProxyType.ToUpperInvariant()} {s.ProxyHost}:{s.ProxyPort}), но прокси выключен.";
                kind = "warn";
            }
            else
            {
                text = "Прокси выключен — используется системное подключение.";
                kind = "off";
            }
        }
        else if (string.IsNullOrWhiteSpace(s.ProxyHost))
        {
            text = "Укажите адрес прокси-сервера и нажмите «Применить».";
            kind = "warn";
        }
        else
        {
            string ruNote = s.ProxyBypassRuServices ? " • Яндекс и ВК напрямую" : " • весь трафик";
            text = $"Активен: {s.ProxyType.ToUpperInvariant()} {s.ProxyHost}:{s.ProxyPort}{ruNote}";
            kind = "ok";
        }

        return new
        {
            enabled = s.ProxyEnabled,
            type = s.ProxyType,
            host = s.ProxyHost,
            port = s.ProxyPort,
            username = s.ProxyUsername,
            password = s.ProxyPassword,
            bypassRu = s.ProxyBypassRuServices,
            text,
            kind
        };
    }

    private void SaveProxyFromWeb(JsonElement root)
    {
        bool enabled = root.TryGetProperty("enabled", out var enEl) && enEl.ValueKind == JsonValueKind.True;
        bool bypassRu = !root.TryGetProperty("bypassRu", out var brEl) || brEl.ValueKind == JsonValueKind.True;
        string rawType = ReadStr(root, "proxyType") ?? "http";
        string rawHost = (ReadStr(root, "host") ?? "").Trim();
        string rawPort = root.TryGetProperty("port", out var pEl)
            ? (pEl.ValueKind == JsonValueKind.Number ? pEl.GetInt32().ToString() : (pEl.GetString() ?? ""))
            : "";
        string rawUser = (ReadStr(root, "username") ?? "").Trim();
        string rawPass = ReadStr(root, "password") ?? "";

        if (!string.IsNullOrWhiteSpace(rawHost) &&
            (rawHost.Contains("://", StringComparison.Ordinal) ||
             rawHost.Contains('@', StringComparison.Ordinal) ||
             (rawHost.Contains(':', StringComparison.Ordinal) && !rawHost.Contains("::", StringComparison.Ordinal))) &&
            ProxyService.TryParseProxyInput(rawHost, out var parsed) &&
            !string.IsNullOrWhiteSpace(parsed.Host))
        {
            if (!string.IsNullOrWhiteSpace(parsed.Type)) rawType = parsed.Type;
            rawHost = parsed.Host;
            if (parsed.Port.HasValue) rawPort = parsed.Port.Value.ToString();
            if (parsed.Username != null) rawUser = parsed.Username;
            if (parsed.Password != null) rawPass = parsed.Password;
        }

        string type = ProxyService.NormalizeType(rawType);
        int port;
        if (string.IsNullOrWhiteSpace(rawPort))
        {
            port = ProxyService.NormalizePort(type, 0);
        }
        else if (!int.TryParse(rawPort.Trim(), out port) || port < 1 || port > 65535)
        {
            PostToWeb("proxyStatus", new { text = "Ошибка: порт должен быть числом от 1 до 65535.", kind = "error" });
            return;
        }

        if (enabled && string.IsNullOrWhiteSpace(rawHost))
        {
            PostToWeb("proxyStatus", new { text = "Введите адрес прокси-сервера перед применением.", kind = "error" });
            return;
        }

        _settings.Update(s =>
        {
            s.ProxyEnabled = enabled;
            s.ProxyType = type;
            s.ProxyHost = rawHost;
            s.ProxyPort = port;
            s.ProxyUsername = rawUser;
            s.ProxyPassword = rawPass;
            s.ProxyBypassRuServices = bypassRu;
        }, notify: true);

        PostToWeb("proxyStatus", BuildProxyDto(_settings.Current));
        PushAccountsUpdate();
        PostToWeb("status", new { message = enabled ? $"Прокси применён: {type.ToUpperInvariant()} {rawHost}:{port}" : "Прокси отключён" });
    }

    private async Task TestProxyFromWebAsync(JsonElement root)
    {
        string rawType = ReadStr(root, "proxyType") ?? "http";
        string rawHost = (ReadStr(root, "host") ?? "").Trim();
        string rawPort = root.TryGetProperty("port", out var pEl)
            ? (pEl.ValueKind == JsonValueKind.Number ? pEl.GetInt32().ToString() : (pEl.GetString() ?? ""))
            : "";
        string rawUser = (ReadStr(root, "username") ?? "").Trim();
        string rawPass = ReadStr(root, "password") ?? "";

        if (!string.IsNullOrWhiteSpace(rawHost) &&
            (rawHost.Contains("://", StringComparison.Ordinal) ||
             rawHost.Contains('@', StringComparison.Ordinal) ||
             (rawHost.Contains(':', StringComparison.Ordinal) && !rawHost.Contains("::", StringComparison.Ordinal))) &&
            ProxyService.TryParseProxyInput(rawHost, out var parsed) &&
            !string.IsNullOrWhiteSpace(parsed.Host))
        {
            if (!string.IsNullOrWhiteSpace(parsed.Type)) rawType = parsed.Type;
            rawHost = parsed.Host;
            if (parsed.Port.HasValue) rawPort = parsed.Port.Value.ToString();
            if (parsed.Username != null) rawUser = parsed.Username;
            if (parsed.Password != null) rawPass = parsed.Password;
        }

        string type = ProxyService.NormalizeType(rawType);
        int port = string.IsNullOrWhiteSpace(rawPort)
            ? ProxyService.NormalizePort(type, 0)
            : (int.TryParse(rawPort.Trim(), out int p) && p >= 1 && p <= 65535 ? p : 0);

        if (port == 0)
        {
            PostToWeb("proxyStatus", new { text = "Укажите корректный порт (от 1 до 65535).", kind = "error" });
            return;
        }
        if (string.IsNullOrWhiteSpace(rawHost))
        {
            PostToWeb("proxyStatus", new { text = "Введите адрес прокси-сервера.", kind = "error" });
            return;
        }

        var nextCts = new CancellationTokenSource();
        var prev = Interlocked.Exchange(ref _proxyTestCts, nextCts);
        if (prev != null) { try { prev.Cancel(); } catch { } try { prev.Dispose(); } catch { } }
        var ct = nextCts.Token;

        PostToWeb("proxyStatus", new { text = $"Проверка {type.ToUpperInvariant()} {rawHost}:{port}...", kind = "busy", testing = true });
        try
        {
            var (ok, message) = await ProxyService.TestProxyAsync(type, rawHost, port, rawUser, rawPass, ct);
            if (!ct.IsCancellationRequested)
                PostToWeb("proxyStatus", new { text = message, kind = ok ? "ok" : "error", testing = false });
        }
        catch (OperationCanceledException) { }
    }

    private async Task CheckYtDlpForWebAsync()
    {
        PostToWeb("ytDlpStatus", new { text = "Проверка yt-dlp --version...", ok = false, busy = true });
        var (ok, info) = await YtDlpMusicProvider.CheckYtDlpAsync();
        PostToWeb("ytDlpStatus", new { text = info, ok, busy = false });
    }

    private async Task CheckUpdateForWebAsync()
    {
        PostToWeb("updateStatus", new { text = "Проверка последней версии на GitHub...", checkedOk = false, isAvailable = false, busy = true, progress = 0 });
        var res = await AppUpdateService.CheckForUpdateAsync();
        var text = string.IsNullOrWhiteSpace(res.CommitMessage) || !res.IsUpdateAvailable
            ? res.StatusMessage
            : $"{res.StatusMessage} {res.CommitMessage}";
        PostToWeb("updateStatus", new
        {
            text,
            checkedOk = res.CheckedOk,
            isAvailable = res.IsUpdateAvailable,
            busy = false,
            progress = 0
        });
    }

    private async Task InstallUpdateForWebAsync()
    {
        PostToWeb("updateStatus", new { text = "Загрузка обновления с GitHub...", checkedOk = true, isAvailable = true, busy = true, progress = 5 });
        var (ok, err) = await AppUpdateService.DownloadAndApplyUpdateAsync((pct, msg) =>
        {
            Dispatcher.BeginInvoke(() => PostToWeb("updateStatus", new
            {
                text = msg,
                checkedOk = true,
                isAvailable = true,
                busy = true,
                progress = pct
            }));
        });
        if (!ok)
        {
            PostToWeb("updateStatus", new { text = err, checkedOk = false, isAvailable = true, busy = false, progress = 0 });
            PostToWeb("status", new { message = err });
        }
    }

    private void DisconnectProvider(string provider)
    {
        switch (provider)
        {
            case "yandex":
                _yandex.Disconnect();
                _library.RemoveExternalPlaylists("yandex");
                PostToWeb("status", new { message = "Вы вышли из аккаунта Яндекс Музыки." });
                break;
            case "vk":
                _vkClearedSession = true;
                _vk.Disconnect();
                _library.RemoveExternalPlaylists("vk");
                PostToWeb("status", new { message = "Вы вышли из аккаунта ВКонтакте." });
                break;
            case "spotify":
                _spotify.Disconnect();
                _library.RemoveExternalPlaylists("spotify");
                PostToWeb("status", new { message = "Вы вышли из аккаунта Spotify." });
                break;
            case "youtube":
                _youtubeClearedSession = true;
                YtDlpMusicProvider.ClearYouTubeCookies();
                PostToWeb("status", new { message = "YouTube cookies удалены." });
                break;
        }
        PushAccountsUpdate();
    }

    private async Task SyncSingleProviderAsync(string provider)
    {
        try
        {
            switch (provider)
            {
                case "yandex" when _yandex.IsAuthenticated:
                {
                    PostToWeb("status", new { message = "Синхронизация Яндекс Музыки..." });
                    var pls = await _yandex.FetchUserPlaylistsAsync();
                    _library.ReplaceExternalPlaylists("yandex", pls);
                    PostToWeb("status", new { message = $"Яндекс Музыка: плейлистов {pls.Count}, треков {pls.Sum(p => p.Tracks.Count)}" });
                    break;
                }
                case "vk" when _vk.IsAuthenticated:
                {
                    PostToWeb("status", new { message = "Синхронизация ВК Музыки..." });
                    var pls = await _vk.FetchUserPlaylistsAsync();
                    _library.ReplaceExternalPlaylists("vk", pls);
                    PostToWeb("status", new { message = $"ВК Музыка: плейлистов {pls.Count}, треков {pls.Sum(p => p.Tracks.Count)}" });
                    break;
                }
                case "spotify" when _spotify.IsAuthenticated:
                {
                    PostToWeb("status", new { message = "Синхронизация Spotify..." });
                    var pls = await _spotify.FetchUserPlaylistsAsync();
                    _library.ReplaceExternalPlaylists("spotify", pls);
                    PostToWeb("status", new { message = $"Spotify: плейлистов {pls.Count}, треков {pls.Sum(p => p.Tracks.Count)}" });
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            PostToWeb("status", new { message = $"Ошибка синхронизации: {ex.Message}" });
        }
    }

    private void ImportYandexJsonFromWeb()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Импорт списка треков (формат yandex2spotify JSON)",
            Filter = "JSON файлы (*.json)|*.json|Все файлы (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var json = File.ReadAllText(dlg.FileName);
            var tracks = YandexMusicProvider.ParseYandex2SpotifyJson(json);
            if (tracks.Count == 0)
            {
                PostToWeb("status", new { message = "В JSON-файле не найдено треков формата [{\"artist\":\"...\",\"track\":\"...\"}]." });
                return;
            }
            var name = $"Импорт • {Path.GetFileNameWithoutExtension(dlg.FileName)}";
            _library.AddImportedPlaylist(name, tracks);
            PostToWeb("status", new { message = $"Импортирован плейлист «{name}» ({tracks.Count} треков)." });
        }
        catch (Exception ex)
        {
            PostToWeb("status", new { message = $"Ошибка импорта JSON: {ex.Message}" });
        }
    }

    private void ImportYoutubeCookiesFromWeb()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Импорт Netscape cookies.txt для YouTube",
            Filter = "Cookies файлы (*.txt)|*.txt|Все файлы (*.*)|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;

        try
        {
            var content = File.ReadAllText(dlg.FileName);
            if (!content.Contains("youtube.com", StringComparison.OrdinalIgnoreCase) &&
                !content.Contains(".google.com", StringComparison.OrdinalIgnoreCase))
            {
                PostToWeb("status", new { message = "Выбранный файл не содержит cookies YouTube / Google." });
                return;
            }
            var dir = Path.GetDirectoryName(YtDlpMusicProvider.YouTubeCookiesPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(YtDlpMusicProvider.YouTubeCookiesPath, content, new System.Text.UTF8Encoding(false));
            PushAccountsUpdate();
            PostToWeb("status", new { message = "Файл cookies.txt для YouTube подключён." });
        }
        catch (Exception ex)
        {
            PostToWeb("status", new { message = $"Ошибка импорта cookies.txt: {ex.Message}" });
        }
    }

    private object SampleTelemetryDto()
    {
        try
        {
            _selfProcess.Refresh();
            long ramMb = Math.Max(1, _selfProcess.WorkingSet64 / (1024L * 1024L));
            var nowUtc = DateTime.UtcNow;
            var curCpu = _selfProcess.TotalProcessorTime;
            double elapsedMs = Math.Max(1.0, (nowUtc - _lastCpuSampleUtc).TotalMilliseconds);
            double cpuDeltaMs = Math.Max(0.0, (curCpu - _lastCpuTime).TotalMilliseconds);
            _lastCpuTime = curCpu;
            _lastCpuSampleUtc = nowUtc;

            double cpuPct = Math.Clamp(
                cpuDeltaMs / (elapsedMs * Math.Max(1, Environment.ProcessorCount)) * 100.0,
                0.0,
                100.0);
            return new
            {
                ramMb,
                cpuPercent = Math.Round(cpuPct, 1),
                gpuPercent = 0
            };
        }
        catch
        {
            return new { ramMb = 24L, cpuPercent = 0.2, gpuPercent = 0 };
        }
    }

    private void PushTelemetrySample()
    {
        if (_isUiSuspended || _activeTab != "settings") return;
        PostToWeb("telemetry", SampleTelemetryDto());
    }

    private void PushWindowSettingsUpdate()
    {
        PostToWeb("windowSettings", BuildWindowSettingsDto());
    }

    private void PushResumeSnapshot()
    {
        var playlists = _library.GetPlaylists();
        var (queueItems, _) = _player.GetQueueSnapshot();
        var cur = _controller.IsLocalActive ? _player.CurrentTrack : (_externalActiveTrack ?? _player.CurrentTrack);

        PostToWeb("resumeSnapshot", new
        {
            favorites = _favorites.Select(ToDto).ToList(),
            playlists = playlists.Select(ToPlaylistDto).ToList(),
            queue = queueItems.Select(ToDto).ToList(),
            currentTrack = cur != null ? ToDto(cur) : null,
            coverDataUrl = _lastCoverDataUrl,
            isPlaying = _isPlaying,
            currentMs = _currentMs,
            totalMs = _totalMs,
            volume = _player.Volume,
            repeatMode = (int)_player.RepeatMode,
            shuffle = _player.ShuffleEnabled,
            windowSettings = BuildWindowSettingsDto(),
            proxy = BuildProxyDto(_settings.Current),
            youtubeCookiesConnected = YtDlpMusicProvider.HasYouTubeCookies,
            externalSource = !_controller.IsLocalActive ? _controller.CurrentMediaState?.ActiveBrand : null,
            proxyActive = ProxyService.IsActive
        });
    }

    private void PushFullInitState()
    {
        _favorites = _library.GetFavorites();
        _downloads.SyncDownloadedFlags(_favorites);
        RegisterTracks(_favorites);

        var playlists = _library.GetPlaylists();
        foreach (var pl in playlists)
        {
            _downloads.SyncDownloadedFlags(pl.Tracks);
            RegisterTracks(pl.Tracks);
        }

        var (queueItems, _) = _player.GetQueueSnapshot();
        RegisterTracks(queueItems);

        if (!_controller.IsLocalActive && _externalActiveTrack == null && _controller.CurrentMediaState != null)
            OnControllerStateChanged(_controller.CurrentMediaState);

        var cur = _controller.IsLocalActive ? _player.CurrentTrack : (_externalActiveTrack ?? _player.CurrentTrack);
        if (cur != null) RegisterTrack(cur);

        var s = _settings.Current;

        PostToWeb("initState", new
        {
            yandexConnected = _yandex.IsAuthenticated,
            yandexUserName = s.YandexUserName ?? "",
            vkConnected = _vk.IsAuthenticated,
            vkUserName = s.VkUserName ?? "",
            spotifyConnected = _spotify.IsAuthenticated,
            spotifyUserName = s.SpotifyUserName ?? "",
            youtubeCookiesConnected = YtDlpMusicProvider.HasYouTubeCookies,
            favorites = _favorites.Select(ToDto).ToList(),
            playlists = playlists.Select(ToPlaylistDto).ToList(),
            queue = queueItems.Select(ToDto).ToList(),
            currentTrack = cur != null ? ToDto(cur) : null,
            coverDataUrl = _lastCoverDataUrl,
            isPlaying = _isPlaying,
            currentMs = _currentMs,
            totalMs = _totalMs,
            volume = _player.Volume,
            repeatMode = (int)_player.RepeatMode,
            shuffle = _player.ShuffleEnabled,
            compact = _isCompactMode,
            windowSettings = BuildWindowSettingsDto(),
            proxy = BuildProxyDto(s),
            telemetry = SampleTelemetryDto(),
            externalSource = !_controller.IsLocalActive ? _controller.CurrentMediaState?.ActiveBrand : null,
            proxyActive = ProxyService.IsActive,
            eq = new
            {
                enabled = s.EqualizerEnabled,
                preset = s.EqualizerPreset,
                preamp = s.EqualizerPreamp,
                bands = s.EqualizerBands,
                rate = s.PlaybackRate,
                presets = AudioPlayerService.EqPresets.ToDictionary(
                    kv => kv.Key,
                    kv => new { preamp = kv.Value.Preamp, bands = kv.Value.Bands })
            },
            windowMaximized = false
        });

        if (_isUiSuspended)
            PostToWeb("uiVisibility", new { visible = false });
    }

    private void PushLibraryUpdate()
    {
        _favorites = _library.GetFavorites();
        _downloads.SyncDownloadedFlags(_favorites);
        RegisterTracks(_favorites);

        var playlists = _library.GetPlaylists();
        foreach (var pl in playlists)
        {
            _downloads.SyncDownloadedFlags(pl.Tracks);
            RegisterTracks(pl.Tracks);
        }

        _library.SyncLikedFlags(_currentResults);
        var allSearchVariants = _currentSearchGroups.SelectMany(g => g.Variants).ToList();
        if (allSearchVariants.Count > 0)
        {
            _library.SyncLikedFlags(allSearchVariants);
            _downloads.SyncDownloadedFlags(allSearchVariants);
        }
        else
        {
            _library.SyncLikedFlags(_currentResults);
            _downloads.SyncDownloadedFlags(_currentResults);
        }

        var cur = _controller.IsLocalActive ? _player.CurrentTrack : (_externalActiveTrack ?? _player.CurrentTrack);
        PostToWeb("libraryUpdate", new
        {
            favorites = _favorites.Select(ToDto).ToList(),
            playlists = playlists.Select(ToPlaylistDto).ToList(),
            currentTrackLiked = cur != null && _library.IsLiked(cur),
            currentTrackDownloaded = cur != null && (_downloads.TryGetLocalFile(cur) != null || cur.IsDownloaded)
        });
    }

    private void PushAccountsUpdate()
    {
        var s = _settings.Current;
        PostToWeb("accountsUpdate", new
        {
            yandexConnected = _yandex.IsAuthenticated,
            yandexUserName = s.YandexUserName ?? "",
            vkConnected = _vk.IsAuthenticated,
            vkUserName = s.VkUserName ?? "",
            spotifyConnected = _spotify.IsAuthenticated,
            spotifyUserName = s.SpotifyUserName ?? "",
            youtubeCookiesConnected = YtDlpMusicProvider.HasYouTubeCookies,
            proxyActive = ProxyService.IsActive
        });
    }

    private void PushQueueUpdate()
    {
        var (items, _) = _player.GetQueueSnapshot();
        _library.SyncLikedFlags(items);
        _downloads.SyncDownloadedFlags(items);
        RegisterTracks(items);
        PostToWeb("queueUpdate", new { items = items.Select(ToDto).ToList() });
    }

    private void PushEqState()
    {
        var s = _settings.Current;
        PostToWeb("eqState", new
        {
            enabled = s.EqualizerEnabled,
            preset = s.EqualizerPreset,
            preamp = s.EqualizerPreamp,
            bands = s.EqualizerBands,
            rate = s.PlaybackRate
        });
    }

    private void SyncAllDownloadedStates()
    {
        var allSearchVariants = _currentSearchGroups.SelectMany(g => g.Variants).ToList();
        _downloads.SyncDownloadedFlags(allSearchVariants.Count > 0 ? allSearchVariants : _currentResults);
        _downloads.SyncDownloadedFlags(_favorites);
        foreach (var pl in _library.GetPlaylists())
            _downloads.SyncDownloadedFlags(pl.Tracks);

        if (_player.CurrentTrack != null)
            _downloads.TryGetLocalFile(_player.CurrentTrack);

        PushLibraryUpdate();
    }

    #region Поиск и воспроизведение

    private List<IMusicProvider> GetActiveProviders()
    {
        var allAvailable = new List<IMusicProvider>();
        if (_yandex.IsAuthenticated) allAvailable.Add(_yandex);
        if (_vk.IsAuthenticated) allAvailable.Add(_vk);
        allAvailable.Add(_spotify);
        allAvailable.AddRange(_providers.Where(p => p.BrandKey is "soundcloud" or "youtube" or "audius" or "bandcamp" or "open_catalog" or "radio"));

        if (_activeSearchFilter == "all")
            return allAvailable;

        return allAvailable
            .Where(p => string.Equals(p.BrandKey, _activeSearchFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private async Task PerformSearchAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query) && _activeSearchFilter != "radio")
        {
            _searchCts?.Cancel();
            _lastQuery = "";
            _rawSearchTracks.Clear();
            _currentSearchGroups.Clear();
            _currentResults.Clear();
            PostToWeb("searchResults", new
            {
                items = Array.Empty<object>(),
                status = "",
                canLoadMore = false,
                isLoading = false
            });
            return;
        }

        if (SpotifyMusicProvider.IsSpotifyUrl(query))
        {
            _searchCts?.Cancel();
            _searchCts = new CancellationTokenSource();
            var linkCt = _searchCts.Token;

            PostToWeb("searchResults", new
            {
                items = new List<object>(),
                status = "Загрузка ссылки Spotify...",
                canLoadMore = false,
                isLoading = true
            });

            var resolved = await _spotify.TryResolvePublicLinkAsync(query, linkCt);
            if (linkCt.IsCancellationRequested) return;
            if (resolved.Ok && resolved.Tracks.Count > 0)
            {
                _library.SyncLikedFlags(resolved.Tracks);
                _downloads.SyncDownloadedFlags(resolved.Tracks);
                RegisterTracks(resolved.Tracks);
                _rawSearchTracks = resolved.Tracks.ToList();
                _currentSearchGroups = resolved.Tracks.Select(t => new SearchTrackGroup
                {
                    GroupKey = t.UniqueKey,
                    Variants = new List<TrackItem> { t }
                }).ToList();
                _currentResults = resolved.Tracks;

                bool isCollection = resolved.Kind is "playlist" or "album" || resolved.Tracks.Count > 1;
                PostToWeb("searchResults", new
                {
                    items = _currentSearchGroups.Select(ToSearchGroupDto).ToList(),
                    status = isCollection
                        ? $"Spotify • {resolved.CollectionName ?? "Плейлист"} ({resolved.Tracks.Count} треков)"
                        : "Прямая ссылка • Spotify",
                    canLoadMore = false,
                    isLoading = false
                });

                if (!isCollection)
                    await _player.PlayFromQueueAsync(_currentResults, 0);
                return;
            }
        }

        if (TryBuildDirectUrlTrack(query, out var directTrack))
        {
            _searchCts?.Cancel();
            _library.SyncLikedFlags(new[] { directTrack });
            _downloads.SyncDownloadedFlags(new[] { directTrack });
            RegisterTrack(directTrack);

            _rawSearchTracks = new List<TrackItem> { directTrack };
            _currentSearchGroups = new List<SearchTrackGroup>
            {
                new SearchTrackGroup
                {
                    GroupKey = directTrack.UniqueKey,
                    Variants = new List<TrackItem> { directTrack }
                }
            };
            _currentResults = new List<TrackItem> { directTrack };
            PostToWeb("searchResults", new
            {
                items = _currentSearchGroups.Select(ToSearchGroupDto).ToList(),
                status = $"Прямая ссылка • {directTrack.Source}",
                canLoadMore = false,
                isLoading = false
            });
            await _player.PlayFromQueueAsync(_currentResults, 0);
            return;
        }

        var activeProviders = GetActiveProviders();
        if (activeProviders.Count == 0)
        {
            PostToWeb("searchResults", new
            {
                items = _currentSearchGroups.Select(ToSearchGroupDto).ToList(),
                status = "Выберите хотя бы один источник",
                canLoadMore = false,
                isLoading = false
            });
            return;
        }

        _lastQuery = query;
        _currentLimitPerProvider = 12;

        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        PostToWeb("searchResults", new
        {
            items = new List<object>(),
            status = "Ищем треки...",
            canLoadMore = false,
            isLoading = true
        });

        try
        {
            var rawMerged = await QueryMergedAsync(query, _currentLimitPerProvider, activeProviders, ct);
            if (ct.IsCancellationRequested) return;

            var groups = StructureAndGroupSearchResults(query, rawMerged);
            var allVariants = groups.SelectMany(g => g.Variants).ToList();
            _library.SyncLikedFlags(allVariants);
            _downloads.SyncDownloadedFlags(allVariants);
            RegisterTracks(allVariants);

            _rawSearchTracks = rawMerged;
            _currentSearchGroups = groups;
            _currentResults = groups.Select(g => g.ActiveTrack).ToList();

            string statusText = groups.Count > 0
                ? (allVariants.Count > groups.Count
                    ? $"Найдено: {groups.Count} (из {allVariants.Count} версий)"
                    : $"Найдено: {groups.Count}. Клик — включить")
                : "Ничего не найдено";

            PostToWeb("searchResults", new
            {
                items = _currentSearchGroups.Select(ToSearchGroupDto).ToList(),
                status = statusText,
                canLoadMore = groups.Count > 0 && _currentLimitPerProvider < 50,
                isLoading = false
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            PostToWeb("searchResults", new
            {
                items = new List<object>(),
                status = ex.Message,
                canLoadMore = false,
                isLoading = false
            });
        }
    }

    private async Task LoadMoreAsync()
    {
        if (string.IsNullOrWhiteSpace(_lastQuery) && _activeSearchFilter != "radio") return;
        var activeProviders = GetActiveProviders();
        if (activeProviders.Count == 0) return;

        int nextLimit = Math.Min(50, _currentLimitPerProvider + 14);
        if (nextLimit == _currentLimitPerProvider) return;
        _currentLimitPerProvider = nextLimit;

        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        PostToWeb("searchResults", new
        {
            items = _currentSearchGroups.Select(ToSearchGroupDto).ToList(),
            status = "Загружаем ещё...",
            canLoadMore = false,
            isLoading = true
        });

        try
        {
            var expanded = await QueryMergedAsync(_lastQuery, nextLimit, activeProviders, ct);
            if (ct.IsCancellationRequested) return;

            var prevChoiceByGroup = _currentSearchGroups.ToDictionary(
                g => g.GroupKey,
                g => g.ActiveTrack.BrandKey,
                StringComparer.OrdinalIgnoreCase);

            int prevCount = _currentSearchGroups.Count;
            var mergedRaw = _rawSearchTracks.Concat(expanded).ToList();
            var combinedGroups = StructureAndGroupSearchResults(_lastQuery, mergedRaw);

            foreach (var grp in combinedGroups)
            {
                if (prevChoiceByGroup.TryGetValue(grp.GroupKey, out var chosenBrand))
                {
                    int idx = grp.Variants.FindIndex(v => string.Equals(v.BrandKey, chosenBrand, StringComparison.OrdinalIgnoreCase));
                    if (idx >= 0) grp.SelectedIndex = idx;
                }
            }

            int addedCount = Math.Max(0, combinedGroups.Count - prevCount);
            var allVariants = combinedGroups.SelectMany(g => g.Variants).ToList();
            _library.SyncLikedFlags(allVariants);
            _downloads.SyncDownloadedFlags(allVariants);
            RegisterTracks(allVariants);

            _rawSearchTracks = mergedRaw;
            _currentSearchGroups = combinedGroups;
            _currentResults = combinedGroups.Select(g => g.ActiveTrack).ToList();

            PostToWeb("searchResults", new
            {
                items = _currentSearchGroups.Select(ToSearchGroupDto).ToList(),
                status = $"Показано треков: {_currentSearchGroups.Count}",
                canLoadMore = nextLimit < 50 && addedCount > 0,
                isLoading = false
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            PostToWeb("searchResults", new
            {
                items = _currentSearchGroups.Select(ToSearchGroupDto).ToList(),
                status = ex.Message,
                canLoadMore = false,
                isLoading = false
            });
        }
    }

    private static async Task<List<TrackItem>> QueryMergedAsync(
        string query,
        int limitPerProvider,
        List<IMusicProvider> providers,
        CancellationToken ct)
    {
        int baseTimeoutSec = ProxyService.IsActive ? 14 : 8;
        bool singleProviderMode = providers.Count == 1;
        var tasks = providers.Select(async p =>
        {
            int sec = baseTimeoutSec;
            if (string.Equals(p.BrandKey, "spotify", StringComparison.OrdinalIgnoreCase))
            {
                if (singleProviderMode || ProxyService.IsActive || (p is SpotifyMusicProvider sp && sp.IsAuthenticated))
                    sec = Math.Max(baseTimeoutSec, 16);
                else
                    sec = Math.Max(baseTimeoutSec, 10);
            }

            using var providerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            providerCts.CancelAfter(TimeSpan.FromSeconds(sec));
            try
            {
                return await p.SearchAsync(query, limitPerProvider, providerCts.Token);
            }
            catch
            {
                return new List<TrackItem>();
            }
        }).ToList();
        var batches = await Task.WhenAll(tasks);

        var merged = new List<TrackItem>();
        int maxLen = batches.Length == 0 ? 0 : batches.Max(b => b.Count);
        for (int i = 0; i < maxLen; i++)
        {
            foreach (var batch in batches)
            {
                if (i < batch.Count)
                    merged.Add(batch[i]);
            }
        }
        return merged;
    }

    private static readonly System.Text.RegularExpressions.Regex SearchWordCleanRegex = new(
        @"[^\p{L}\p{Nd}\*\s]+",
        System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly string[] CoverOrBeatNoiseWords =
    {
        "type beat", "instrumental", "karaoke", "караоке", "минус", "tribute", "трибьют"
    };

    private sealed class SearchTrackMeta
    {
        public required TrackItem Track { get; init; }
        public required int OriginalIndex { get; init; }
        public required string CanonicalSongKey { get; init; }
        public required string CensoredPatternTitle { get; init; }
        public required string VariantTag { get; init; }
        public required string EffectiveArtistString { get; init; }
        public required List<string> EffectiveArtists { get; init; }
        public required bool ArtistMatchesQuery { get; init; }
        public required int QueryTier { get; init; }
        public required int TrackScore { get; init; }
        public required int PlatformPriority { get; init; }
    }

    private sealed class SongCluster
    {
        public required string CanonicalSongKey { get; init; }
        public required string VariantTag { get; init; }
        public required List<SearchTrackMeta> Items { get; set; }
        public int ClusterTier { get; set; }
        public int BestTrackScore { get; set; }
        public int MinOriginalIndex { get; set; }
    }

    private static int GetPlatformSearchPriority(string? brandKey) => (brandKey ?? "").ToLowerInvariant() switch
    {
        "yandex" => 0,
        "vk" => 1,
        "spotify" => 2,
        "soundcloud" => 3,
        "youtube" => 4,
        "audius" => 5,
        "bandcamp" => 6,
        "open_catalog" => 7,
        "radio" => 8,
        _ => 9
    };

    private static List<SearchTrackGroup> StructureAndGroupSearchResults(string query, IEnumerable<TrackItem> rawItems)
    {
        var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deduped = new List<TrackItem>();
        foreach (var item in rawItems)
        {
            if (item != null && !string.IsNullOrWhiteSpace(item.UniqueKey) && seenKeys.Add(item.UniqueKey))
                deduped.Add(item);
        }

        if (deduped.Count <= 1)
        {
            return deduped.Select(t => new SearchTrackGroup
            {
                GroupKey = t.UniqueKey,
                Variants = new List<TrackItem> { t }
            }).ToList();
        }

        string normQuery = TrackMatcher.NormalizeKey(SearchWordCleanRegex.Replace(TrackMatcher.NormalizeKey(query), " "));
        string queryNoStars = TrackMatcher.BuildCanonicalSongKey(query);
        string queryVariant = TrackMatcher.ExtractVariantTag(query);
        var queryWords = normQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        bool queryWantsNoise = CoverOrBeatNoiseWords.Any(w => normQuery.Contains(w, StringComparison.Ordinal));

        var metas = new List<SearchTrackMeta>(deduped.Count);
        for (int i = 0; i < deduped.Count; i++)
        {
            var tr = deduped[i];
            string brand = (tr.BrandKey ?? "").ToLowerInvariant();
            bool allowSplitFromTitle = brand != "yandex" && brand != "vk" && brand != "spotify";

            var (cleanArtist, cleanTitle, effectiveArtistString) = TrackMatcher.NormalizeArtistAndTitle(
                tr.Artist,
                tr.Title,
                publisherArtist: null,
                allowSplitArtistFromTitle: allowSplitFromTitle,
                stripDisplayVideoNoise: false);

            if (string.Equals(cleanArtist, tr.Source, StringComparison.OrdinalIgnoreCase))
                cleanArtist = "";
            if (string.Equals(effectiveArtistString, tr.Source, StringComparison.OrdinalIgnoreCase))
                effectiveArtistString = "";

            var effectiveArtists = TrackMatcher.SplitArtists(effectiveArtistString);
            string primaryArtistNorm = TrackMatcher.NormalizeKey(TrackMatcher.GetPrimaryArtist(effectiveArtistString));
            string canonicalSongKey = TrackMatcher.BuildCanonicalSongKey(cleanTitle);
            string censoredPattern = TrackMatcher.BuildCensoredPatternTitle(cleanTitle);
            string variantTag = TrackMatcher.ExtractVariantTag(tr.Title);

            int queryTier = 3;
            int matchBonus = 0;
            bool artistMatchesQuery = false;

            if (!string.IsNullOrEmpty(normQuery))
            {
                bool fullArtistExact = false;
                bool wordBoundaryArtist = false;
                bool partialArtistWords = false;
                bool queryContainsArtist = false;
                bool isPrimaryExact = false;
                string? matchedArtistInCombined = null;

                foreach (var art in effectiveArtists)
                {
                    if (string.IsNullOrWhiteSpace(art)) continue;
                    string artClean = TrackMatcher.NormalizeKey(SearchWordCleanRegex.Replace(art, " "));
                    if (string.IsNullOrWhiteSpace(artClean)) continue;

                    if (artClean == normQuery)
                    {
                        fullArtistExact = true;
                        if (primaryArtistNorm == art || primaryArtistNorm == artClean)
                            isPrimaryExact = true;
                    }
                    else if (IsWordBoundaryMatch(artClean, normQuery))
                    {
                        wordBoundaryArtist = true;
                    }
                    else if (queryWords.Length > 0 && queryWords.All(w => artClean.Contains(w, StringComparison.Ordinal)))
                    {
                        partialArtistWords = true;
                    }

                    if (artClean.Length >= 2 && queryWords.Length >= 2 && IsWordBoundaryMatch(normQuery, artClean))
                    {
                        queryContainsArtist = true;
                        if (matchedArtistInCombined == null || artClean.Length > matchedArtistInCombined.Length)
                            matchedArtistInCombined = artClean;
                    }
                }

                bool fullSongExact = false;
                if (!string.IsNullOrEmpty(queryNoStars) &&
                    TrackMatcher.AreSameSongTitle(canonicalSongKey, censoredPattern, queryNoStars, normQuery, out bool songExact) &&
                    songExact)
                {
                    fullSongExact = true;
                }

                bool wordBoundarySong = !string.IsNullOrEmpty(queryNoStars) && IsWordBoundaryMatch(canonicalSongKey, queryNoStars);

                bool combinedArtistAndSongExact = false;
                bool combinedArtistAndSongStrong = false;
                if (matchedArtistInCombined != null)
                {
                    var artWordSet = matchedArtistInCombined.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    var remainingList = new List<string>(queryWords);
                    foreach (var aw in artWordSet)
                        remainingList.Remove(aw);

                    string remQuery = string.Join(" ", remainingList);
                    string remNoStars = remQuery.Replace("*", "", StringComparison.Ordinal).Trim();
                    if (!string.IsNullOrWhiteSpace(remQuery))
                    {
                        if (TrackMatcher.AreSameSongTitle(canonicalSongKey, censoredPattern, remNoStars, remQuery, out bool remExact) &&
                            remExact)
                        {
                            combinedArtistAndSongExact = true;
                        }
                        else if (remainingList.All(w =>
                                     canonicalSongKey.Contains(w, StringComparison.Ordinal) ||
                                     censoredPattern.Contains(w, StringComparison.Ordinal)))
                        {
                            combinedArtistAndSongStrong = true;
                        }
                    }
                }

                artistMatchesQuery = fullArtistExact || wordBoundaryArtist || partialArtistWords || queryContainsArtist;

                if (combinedArtistAndSongExact)
                {
                    queryTier = 0;
                    matchBonus = 420;
                }
                else if (combinedArtistAndSongStrong)
                {
                    queryTier = 0;
                    matchBonus = 340;
                }
                else if (fullArtistExact)
                {
                    queryTier = 0;
                    matchBonus = isPrimaryExact ? 300 : 210;
                }
                else if (fullSongExact)
                {
                    queryTier = 0;
                    matchBonus = 240;
                }
                else if (wordBoundaryArtist || partialArtistWords || queryContainsArtist)
                {
                    queryTier = 1;
                    matchBonus = wordBoundaryArtist ? 140 : 100;
                }
                else if (wordBoundarySong)
                {
                    queryTier = 1;
                    matchBonus = 90;
                }
                else if (queryWords.Length > 0 &&
                         queryWords.All(w =>
                             canonicalSongKey.Contains(w, StringComparison.Ordinal) ||
                             censoredPattern.Contains(w, StringComparison.Ordinal) ||
                             effectiveArtistString.ToLowerInvariant().Replace('ё', 'е').Contains(w, StringComparison.Ordinal)))
                {
                    queryTier = 2;
                    matchBonus = 60;
                }
            }

            string lowerTitle = (tr.Title ?? "").ToLowerInvariant().Replace('ё', 'е');
            bool hasNoisePenalty = !queryWantsNoise && CoverOrBeatNoiseWords.Any(w => lowerTitle.Contains(w, StringComparison.Ordinal));
            if (hasNoisePenalty)
                queryTier = Math.Min(4, queryTier + 2);

            if (!string.IsNullOrEmpty(variantTag) && variantTag != queryVariant)
                matchBonus -= 40;

            int score = (4 - queryTier) * 1000 + matchBonus + Math.Max(0, 30 - i);

            metas.Add(new SearchTrackMeta
            {
                Track = tr,
                OriginalIndex = i,
                CanonicalSongKey = canonicalSongKey,
                CensoredPatternTitle = censoredPattern,
                VariantTag = variantTag,
                EffectiveArtistString = effectiveArtistString,
                EffectiveArtists = effectiveArtists,
                ArtistMatchesQuery = artistMatchesQuery,
                QueryTier = queryTier,
                TrackScore = score,
                PlatformPriority = GetPlatformSearchPriority(tr.BrandKey)
            });
        }

        var clusters = new List<SongCluster>();
        foreach (var meta in metas)
        {
            SongCluster? targetCluster = null;
            if (meta.CanonicalSongKey.Length >= 2 || meta.CensoredPatternTitle.Contains('*', StringComparison.Ordinal))
            {
                foreach (var c in clusters)
                {
                    if (ClusterMatchesTrack(c, meta))
                    {
                        targetCluster = c;
                        break;
                    }
                }
            }

            if (targetCluster == null)
            {
                targetCluster = new SongCluster
                {
                    CanonicalSongKey = meta.CanonicalSongKey,
                    VariantTag = meta.VariantTag,
                    Items = new List<SearchTrackMeta> { meta },
                    ClusterTier = meta.QueryTier,
                    BestTrackScore = meta.TrackScore,
                    MinOriginalIndex = meta.OriginalIndex
                };
                clusters.Add(targetCluster);
            }
            else
            {
                targetCluster.Items.Add(meta);
                if (meta.QueryTier < targetCluster.ClusterTier)
                    targetCluster.ClusterTier = meta.QueryTier;
                if (meta.TrackScore > targetCluster.BestTrackScore)
                    targetCluster.BestTrackScore = meta.TrackScore;
                if (meta.OriginalIndex < targetCluster.MinOriginalIndex)
                    targetCluster.MinOriginalIndex = meta.OriginalIndex;
            }
        }

        foreach (var c in clusters)
        {
            c.Items.Sort((a, b) =>
            {
                int p = a.PlatformPriority.CompareTo(b.PlatformPriority);
                if (p != 0) return p;
                int s = b.TrackScore.CompareTo(a.TrackScore);
                if (s != 0) return s;
                return a.OriginalIndex.CompareTo(b.OriginalIndex);
            });

            // Оставляем по одному лучшему представителю с каждой площадки внутри одного кластера песни
            // (повторные перезаливы на той же площадке с близкой длительностью убираем)
            if (c.Items.Count > 1)
            {
                var uniqueByPlatform = new List<SearchTrackMeta>(c.Items.Count);
                foreach (var item in c.Items)
                {
                    string b = item.Track.BrandKey ?? "";
                    bool duplicateSamePlatform = uniqueByPlatform.Any(kept =>
                        string.Equals(kept.Track.BrandKey, b, StringComparison.OrdinalIgnoreCase) &&
                        (kept.Track.Duration <= TimeSpan.Zero ||
                         item.Track.Duration <= TimeSpan.Zero ||
                         Math.Abs((long)(kept.Track.Duration - item.Track.Duration).TotalMilliseconds) <= 5000));

                    if (!duplicateSamePlatform)
                        uniqueByPlatform.Add(item);
                }
                c.Items = uniqueByPlatform;
                c.BestTrackScore = c.Items.Max(x => x.TrackScore);
                c.ClusterTier = c.Items.Min(x => x.QueryTier);
            }

            int distinctPlatforms = c.Items
                .Select(x => x.Track.BrandKey ?? "")
                .Where(b => !string.IsNullOrEmpty(b))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            int crossPlatformBonus = Math.Min(4, Math.Max(0, distinctPlatforms - 1)) * 45;
            c.BestTrackScore += crossPlatformBonus;
        }

        clusters.Sort((a, b) =>
        {
            int t = a.ClusterTier.CompareTo(b.ClusterTier);
            if (t != 0) return t;
            int s = b.BestTrackScore.CompareTo(a.BestTrackScore);
            if (s != 0) return s;
            return a.MinOriginalIndex.CompareTo(b.MinOriginalIndex);
        });

        var groups = new List<SearchTrackGroup>(clusters.Count);
        var usedGroupKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in clusters)
        {
            var variants = c.Items.Select(m => m.Track).ToList();
            if (variants.Count == 0) continue;

            string primaryArt = TrackMatcher.NormalizeKey(TrackMatcher.GetPrimaryArtist(c.Items[0].EffectiveArtistString));
            string baseKey = !string.IsNullOrWhiteSpace(c.CanonicalSongKey)
                ? $"{c.CanonicalSongKey}|{c.VariantTag}|{primaryArt}"
                : variants[0].UniqueKey;
            string groupKey = usedGroupKeys.Add(baseKey) ? baseKey : $"{baseKey}|{variants[0].UniqueKey}";

            groups.Add(new SearchTrackGroup
            {
                GroupKey = groupKey,
                Variants = variants
            });
        }
        return groups;
    }

    private static bool ClusterMatchesTrack(SongCluster cluster, SearchTrackMeta meta)
    {
        if (!string.Equals(cluster.VariantTag, meta.VariantTag, StringComparison.Ordinal))
            return false;

        foreach (var existing in cluster.Items)
        {
            if (!TrackMatcher.AreSameSongTitle(
                    existing.CanonicalSongKey,
                    existing.CensoredPatternTitle,
                    meta.CanonicalSongKey,
                    meta.CensoredPatternTitle,
                    out bool exactOrCensored))
            {
                continue;
            }

            bool artistsOverlap = TrackMatcher.ArtistsOverlap(existing.EffectiveArtistString, meta.EffectiveArtistString);
            bool bothMatchQueryArtist = existing.ArtistMatchesQuery && meta.ArtistMatchesQuery;
            bool durationKnown = existing.Track.Duration > TimeSpan.Zero && meta.Track.Duration > TimeSpan.Zero;
            long durationDiffMs = durationKnown
                ? Math.Abs((long)(existing.Track.Duration - meta.Track.Duration).TotalMilliseconds)
                : long.MaxValue;

            if (exactOrCensored)
            {
                if (artistsOverlap || bothMatchQueryArtist)
                    return true;
                if (meta.CanonicalSongKey.Length >= 4 && durationKnown && durationDiffMs <= 5000)
                    return true;
            }
            else
            {
                if (artistsOverlap && durationKnown && durationDiffMs <= 3500)
                    return true;
            }
        }
        return false;
    }

    private static bool IsWordBoundaryMatch(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(haystack) || string.IsNullOrEmpty(needle)) return false;
        int idx = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (idx >= 0)
        {
            bool leftOk = idx == 0 || !char.IsLetterOrDigit(haystack[idx - 1]);
            int afterIdx = idx + needle.Length;
            bool rightOk = afterIdx >= haystack.Length || !char.IsLetterOrDigit(haystack[afterIdx]);
            if (leftOk && rightOk) return true;
            idx = haystack.IndexOf(needle, idx + 1, StringComparison.Ordinal);
        }
        return false;
    }

    private async Task PlayFromWebListAsync(string listKind, string? playlistId, string? trackKey, int index)
    {
        List<TrackItem> queue;
        if (listKind == "favorites")
        {
            queue = _favorites;
        }
        else if (listKind == "playlist" && !string.IsNullOrWhiteSpace(playlistId))
        {
            var pl = _library.GetPlaylists().FirstOrDefault(p => p.Id == playlistId);
            queue = pl?.Tracks ?? new List<TrackItem>();
        }
        else
        {
            queue = _currentResults;
        }

        int resolvedIdx = -1;
        if (!string.IsNullOrWhiteSpace(trackKey))
            resolvedIdx = queue.FindIndex(t => string.Equals(t.UniqueKey, trackKey, StringComparison.OrdinalIgnoreCase));
        if (resolvedIdx < 0 && index >= 0 && index < queue.Count)
            resolvedIdx = index;

        if (resolvedIdx >= 0)
            await _player.PlayFromQueueAsync(queue, resolvedIdx);
        else if (TryResolveTrack(trackKey, out var fallbackTrack))
            await _player.PlayFromQueueAsync(new List<TrackItem> { fallbackTrack }, 0);
    }

    private static bool TryBuildDirectUrlTrack(string input, out TrackItem track)
    {
        track = null!;
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

        var host = uri.Host.ToLowerInvariant();
        string brandKey;
        string source;
        string brandColor;
        string id = "";

        if (host.Contains("music.yandex."))
        {
            brandKey = "yandex"; source = "Яндекс"; brandColor = YandexMusicProvider.BrandColorHex;
            var m = System.Text.RegularExpressions.Regex.Match(input, @"/track/(\d+)");
            if (m.Success) id = m.Groups[1].Value;
        }
        else if (host.Contains("vk.com"))
        {
            brandKey = "vk"; source = "ВК"; brandColor = VkMusicProvider.BrandColorHex;
            var m = System.Text.RegularExpressions.Regex.Match(input, @"audio(-?\d+_\d+)");
            if (m.Success) id = m.Groups[1].Value;
        }
        else if (host.Contains("open.spotify.com"))
        {
            brandKey = "spotify"; source = "Spotify"; brandColor = SpotifyMusicProvider.BrandColorHex;
        }
        else if (host.Contains("soundcloud.com") || host.Contains("sndcdn.com"))
        {
            brandKey = "soundcloud"; source = "SoundCloud"; brandColor = "#FF5500";
        }
        else if (host.Contains("youtube.com") || host.Contains("youtu.be"))
        {
            brandKey = "youtube"; source = "YouTube"; brandColor = "#FF0033";
            id = YouTubeFastProvider.ExtractVideoId(input) ?? "";
        }
        else if (host.Contains("audius.co"))
        {
            brandKey = "audius"; source = "Audius"; brandColor = "#CC0FF0";
        }
        else if (host.Contains("bandcamp.com"))
        {
            brandKey = "bandcamp"; source = "Bandcamp"; brandColor = "#1DA0C3";
        }
        else if (host.Contains("archive.org") || host.Contains("hearthis.at"))
        {
            brandKey = "open_catalog";
            source = host.Contains("archive.org") ? "Archive.org" : "Hearthis";
            brandColor = "#22C55E";
        }
        else
        {
            brandKey = "web"; source = "Интернет"; brandColor = "#22C55E";
        }

        string fileName = Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath));
        string title = !string.IsNullOrWhiteSpace(fileName) && fileName != "/" ? fileName : uri.Host;

        track = new TrackItem
        {
            Id = id,
            Source = source,
            BrandKey = brandKey,
            BrandColor = brandColor,
            Title = title,
            Artist = uri.Host,
            PageUrl = input
        };
        return true;
    }

    private async Task HandleDownloadOrRevealAsync(TrackItem track)
    {
        var local = _downloads.TryGetLocalFile(track);
        if (!string.IsNullOrWhiteSpace(local) && File.Exists(local))
        {
            DownloadService.RevealInExplorer(local);
            return;
        }

        if (track.IsDownloading) return;

        TimeSpan? maxDuration = null;
        if (DownloadService.IsLiveStream(track))
        {
            var answer = MessageBox.Show(
                this,
                "Это прямой поток без конечной длительности. Записать фрагмент эфира длительностью 60 секунд?",
                "Запись потока",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (answer != MessageBoxResult.Yes)
                return;

            maxDuration = TimeSpan.FromSeconds(60);
        }

        void Handler(object? _, PropertyChangedEventArgs ev)
        {
            if (ev.PropertyName is nameof(TrackItem.IsDownloading) or nameof(TrackItem.DownloadProgress) or nameof(TrackItem.IsDownloaded))
            {
                Dispatcher.BeginInvoke(() => PostToWeb("trackProgress", new
                {
                    uniqueKey = track.UniqueKey,
                    isDownloading = track.IsDownloading,
                    downloadProgress = track.DownloadProgress,
                    isDownloaded = track.IsDownloaded
                }));
            }
        }

        track.PropertyChanged += Handler;
        PostToWeb("status", new
        {
            message = maxDuration.HasValue ? $"Запись эфира (60 сек): {track.Title}..." : $"Скачивание: {track.Title}..."
        });

        try
        {
            var saved = await _downloads.DownloadTrackAsync(track, maxRecordDuration: maxDuration);
            PostToWeb("status", new
            {
                message = !string.IsNullOrWhiteSpace(saved)
                    ? $"Сохранено: {Path.GetFileName(saved)}"
                    : $"Не удалось скачать «{track.Title}»"
            });
        }
        finally
        {
            track.PropertyChanged -= Handler;
        }
    }

    private async Task ExportFavoritesToYandexAsync()
    {
        if (_isExportingToYandex) return;

        if (!_yandex.IsAuthenticated)
        {
            const string authMsg = "Войдите в Яндекс Музыку, чтобы перенести треки из «Любимого»";
            PostToWeb("status", new { message = authMsg });
            PostToWeb("favoritesTransferProgress", new
            {
                active = false,
                done = true,
                ok = false,
                current = 0,
                total = 0,
                message = authMsg
            });
            _openAccountsAction();
            return;
        }

        var snapshot = _favorites.ToList();
        if (snapshot.Count == 0)
        {
            const string emptyMsg = "Во вкладке «Любимое» пока нет треков для переноса.";
            PostToWeb("status", new { message = emptyMsg });
            PostToWeb("favoritesTransferProgress", new
            {
                active = false,
                done = true,
                ok = false,
                current = 0,
                total = 0,
                message = emptyMsg
            });
            return;
        }

        _isExportingToYandex = true;
        try
        {
            PostToWeb("favoritesTransferProgress", new
            {
                active = true,
                done = false,
                ok = true,
                current = 0,
                total = snapshot.Count,
                message = $"Подготовка переноса {snapshot.Count} треков в Яндекс Музыку..."
            });

            var result = await _yandex.ExportTracksToYandexLikesAsync(
                snapshot,
                (current, total, label) =>
                {
                    Dispatcher.BeginInvoke(() =>
                    {
                        var progText = $"В Яндекс ({current}/{total}): {label}";
                        PostToWeb("favoritesTransferProgress", new
                        {
                            active = true,
                            done = false,
                            ok = true,
                            current,
                            total,
                            message = progText
                        });
                        PostToWeb("status", new { message = progText });
                    });
                });

            if (result.AddedCount > 0)
            {
                try { await _syncAccountsAction(); } catch { }
            }

            PostToWeb("favoritesTransferProgress", new
            {
                active = false,
                done = true,
                ok = result.Ok,
                current = snapshot.Count,
                total = snapshot.Count,
                message = result.Message
            });
            PostToWeb("status", new { message = result.Message });
        }
        catch (Exception ex)
        {
            var errMsg = $"Ошибка переноса в Яндекс Музыку: {ex.Message}";
            PostToWeb("favoritesTransferProgress", new
            {
                active = false,
                done = true,
                ok = false,
                current = 0,
                total = snapshot.Count,
                message = errMsg
            });
            PostToWeb("status", new { message = errMsg });
        }
        finally
        {
            _isExportingToYandex = false;
        }
    }

    private async Task ExportSingleTrackToYandexAsync(TrackItem track)
    {
        if (!_yandex.IsAuthenticated)
        {
            PostToWeb("status", new { message = "Подключите аккаунт Яндекс Музыки для переноса трека" });
            ShowSettingsSection("accounts");
            return;
        }

        PostToWeb("status", new { message = $"Отправка в Яндекс Музыку: {track.Artist} — {track.Title}..." });
        try
        {
            var res = await _yandex.ExportTracksToYandexLikesAsync(new[] { track });
            PostToWeb("status", new { message = res.Message });
            if (res.AddedCount > 0)
                _ = _syncAccountsAction();
        }
        catch (Exception ex)
        {
            PostToWeb("status", new { message = $"Ошибка Яндекс Музыки: {ex.Message}" });
        }
    }

    private async Task RemoveTrackFromPlaylistSyncedAsync(string playlistId, string trackKey)
    {
        var pl = _library.GetPlaylists().FirstOrDefault(p => p.Id == playlistId);
        if (pl == null) return;

        TrackItem? track = pl.Tracks.FirstOrDefault(t => string.Equals(t.UniqueKey, trackKey, StringComparison.OrdinalIgnoreCase));
        if (track == null && TryResolveTrack(trackKey, out var resolved))
            track = resolved;

        if (!pl.IsExternal)
        {
            _library.RemoveTrackFromPlaylist(playlistId, trackKey);
            PostToWeb("status", new { message = $"Удалено из «{pl.Name}»" });
            return;
        }

        if (track == null)
        {
            PostToWeb("status", new { message = "Не найден трек для удаления." });
            return;
        }

        PostToWeb("status", new { message = $"Удаление из {pl.PlatformBadgeText}..." });
        (bool ok, string msg) res;
        switch ((pl.PlatformBrand ?? "").ToLowerInvariant())
        {
            case "yandex":
                res = await _yandex.RemoveTrackFromPlaylistAsync(pl.ExternalId, track);
                break;
            case "vk":
                res = await _vk.RemoveTrackFromPlaylistAsync(pl.ExternalId, pl.Name, track);
                break;
            case "spotify":
                res = await _spotify.RemoveTrackFromPlaylistAsync(pl.ExternalId, track);
                break;
            default:
                _library.RemoveTrackFromPlaylist(playlistId, trackKey);
                return;
        }

        if (res.ok)
            _library.RemoveTrackFromPlaylist(playlistId, trackKey);

        PostToWeb("status", new { message = res.msg });
    }

    #endregion

    #region События плеера и обложка

    private void OnTrackChanged(TrackItem track)
    {
        _externalActiveTrack = null;
        _lastExternalVisualKey = null;
        _currentMs = 0;
        _totalMs = track.Duration > TimeSpan.Zero ? (long)track.Duration.TotalMilliseconds : 0;
        RegisterTrack(track);
        _downloads.TryGetLocalFile(track);
        _library.SyncLikedFlags(new[] { track });

        _lastCoverDataUrl = null;

        Dispatcher.BeginInvoke(() =>
        {
            if (_isUiSuspended) return;
            PostToWeb("trackChanged", new
            {
                track = ToDto(track),
                coverDataUrl = (string?)null
            });
        });

        _ = LoadCoverDataUrlAsync(track);
    }

    private async Task LoadCoverDataUrlAsync(TrackItem track)
    {
        var bytes = await _controller.DownloadCoverAsync(track.CoverUrl);
        if (_player.CurrentTrack?.UniqueKey != track.UniqueKey) return;

        string? dataUrl = null;
        if (bytes != null && bytes.Length > 0 && bytes.Length <= 512 * 1024)
            dataUrl = "data:image/jpeg;base64," + Convert.ToBase64String(bytes);

        _lastCoverDataUrl = dataUrl;

        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_isUiSuspended || _player.CurrentTrack?.UniqueKey != track.UniqueKey) return;
            PostToWeb("trackChanged", new
            {
                track = ToDto(track),
                coverDataUrl = dataUrl
            });
        });
    }

    private void OnPositionChanged(long currentMs, long totalMs)
    {
        if (!_controller.IsLocalActive && _externalActiveTrack != null) return;
        _currentMs = currentMs;
        if (totalMs > 0) _totalMs = totalMs;

        if (_isUiSuspended) return;

        long now = Environment.TickCount64;
        if (_player.IsPlaying && now - _lastPosPushTick < 180) return;
        _lastPosPushTick = now;

        Dispatcher.BeginInvoke(() =>
        {
            if (!_isUiSuspended)
                PostToWeb("position", new { currentMs = _currentMs, totalMs = _totalMs });
        });
    }


    private void OnPlaybackStateChanged(bool isPlaying)
    {
        if (!_controller.IsLocalActive && _externalActiveTrack != null) return;
        _isPlaying = isPlaying;
        if (_isUiSuspended) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (!_isUiSuspended)
                PostToWeb("playbackState", new { isPlaying });
        });
    }

    private void OnControllerStateChanged(MediaState state)
    {
        if (_controller.IsLocalActive)
        {
            if (_externalActiveTrack != null)
            {
                _externalActiveTrack = null;
                _lastExternalVisualKey = null;
                if (_player.CurrentTrack != null)
                {
                    OnTrackChanged(_player.CurrentTrack);
                    if (!_isUiSuspended)
                    {
                        PostToWeb("playbackState", new { isPlaying = _player.IsPlaying });
                        PostToWeb("position", new { currentMs = _player.CurrentTimeMs, totalMs = _player.TotalLengthMs });
                    }
                }
            }
            return;
        }

        if (string.IsNullOrWhiteSpace(state.ActiveId) ||
            string.IsNullOrWhiteSpace(state.Title) ||
            state.Title == "Ничего не играет")
        {
            return;
        }

        bool isYandexWave = string.Equals(state.SourceBadgeText, "Моя волна", StringComparison.OrdinalIgnoreCase);
        TrackItem extTrack = BuildExternalTrackItem(state);

        _library.SyncLikedFlags(new[] { extTrack });
        _downloads.SyncDownloadedFlags(new[] { extTrack });
        RegisterTrack(extTrack);
        _externalActiveTrack = extTrack;

        _isPlaying = state.IsPlaying;
        _currentMs = state.CurrentMs;
        _totalMs = state.TotalMs > 0 ? state.TotalMs : (extTrack.Duration > TimeSpan.Zero ? (long)extTrack.Duration.TotalMilliseconds : 0);

        string visualKey = $"{state.ActiveId}|{state.ActiveBrand}|{extTrack.Title}|{extTrack.Artist}|{state.CoverUrl}";
        bool trackChanged = visualKey != _lastExternalVisualKey;

        if (trackChanged || (state.CoverUpdated && state.CoverBytes != null && state.CoverBytes.Length > 0))
        {
            _lastExternalVisualKey = visualKey;

            if (trackChanged)
                _lastCoverDataUrl = null;

            if (state.CoverBytes != null && state.CoverBytes.Length > 0 && state.CoverBytes.Length <= 512 * 1024)
                _lastCoverDataUrl = "data:image/jpeg;base64," + Convert.ToBase64String(state.CoverBytes);

            if (!_isUiSuspended)
            {
                PostToWeb("trackChanged", new
                {
                    track = ToDto(extTrack),
                    coverDataUrl = _lastCoverDataUrl
                });
            }
        }

        if (_isUiSuspended) return;

        PostToWeb("playbackState", new { isPlaying = _isPlaying });
        PostToWeb("position", new { currentMs = _currentMs, totalMs = _totalMs });
        string srcLabel = !string.IsNullOrWhiteSpace(state.SourceBadgeText)
            ? state.SourceBadgeText
            : (isYandexWave ? "Яндекс Музыка • Моя волна" : extTrack.Source);
        PostToWeb("status", new { message = $"{srcLabel} • {(_isPlaying ? "Воспроизведение" : "Пауза")}" });
    }

    private async Task StartYandexMyWaveAsync()
    {
        if (!_yandex.IsAuthenticated)
        {
            PostToWeb("status", new { message = "Войдите в Яндекс Музыку для запуска «Моей волны»" });
            _openAccountsAction();
            return;
        }

        _controller.SelectSource(MediaControllerService.LocalSourceId);
        PostToWeb("status", new { message = "Запуск «Моей волны» Яндекса..." });

        bool ok = await _player.StartYandexWaveAsync();
        if (!ok)
        {
            PostToWeb("status", new { message = "Не удалось загрузить «Мою волну»" });
            return;
        }

        var (waveItems, _) = _player.GetQueueSnapshot();
        _library.SyncLikedFlags(waveItems);
        _downloads.SyncDownloadedFlags(waveItems);
        foreach (var t in waveItems) RegisterTrack(t);

        PushQueueUpdate();
        PostToWeb("showTab", new { tab = "queue" });
        PostToWeb("status", new { message = "Яндекс Музыка • Моя волна • Воспроизведение" });
    }

    private async Task StartVkMixAsync()
    {
        if (!_vk.IsAuthenticated)
        {
            PostToWeb("status", new { message = "Войдите в ВК Музыку для запуска «VK Микс»" });
            _openAccountsAction();
            return;
        }

        _controller.SelectSource(MediaControllerService.LocalSourceId);
        PostToWeb("status", new { message = "Запуск «VK Микс»..." });

        bool ok = await _player.StartVkMixAsync();
        if (!ok)
        {
            PostToWeb("status", new { message = "Не удалось загрузить «VK Микс»" });
            return;
        }

        var (mixItems, _) = _player.GetQueueSnapshot();
        _library.SyncLikedFlags(mixItems);
        _downloads.SyncDownloadedFlags(mixItems);
        foreach (var t in mixItems) RegisterTrack(t);

        PushQueueUpdate();
        PostToWeb("showTab", new { tab = "queue" });
        PostToWeb("status", new { message = "ВК Музыка • VK Микс • Воспроизведение" });
    }

    private static TrackItem BuildExternalTrackItem(MediaState state)
    {
        var brand = string.IsNullOrWhiteSpace(state.ActiveBrand) ? "default" : state.ActiveBrand;
        var (sourceName, brandColor) = brand switch
        {
            "yandex" => ("Яндекс Музыка", YandexMusicProvider.BrandColorHex),
            "vk" => ("ВК Музыка", VkMusicProvider.BrandColorHex),
            "soundcloud" => ("SoundCloud", "#FF5500"),
            "youtube" => ("YouTube", "#FF0033"),
            "audius" => ("Audius", "#CC0FF0"),
            "bandcamp" => ("Bandcamp", "#1DA0C3"),
            "spotify" => ("Spotify", "#1DB954"),
            _ => ("Плеер", "#FFCC00")
        };

        return new TrackItem
        {
            Id = $"{brand}:{state.Title}",
            Source = sourceName,
            BrandKey = brand,
            BrandColor = brandColor,
            Title = state.Title,
            Artist = state.Artist,
            PageUrl = $"external:{brand}:{state.Artist}:{state.Title}",
            CoverUrl = state.CoverUrl ?? "",
            Duration = state.TotalMs > 0 ? TimeSpan.FromMilliseconds(state.TotalMs) : TimeSpan.Zero
        };
    }


    #endregion

    #region Helpers & DTO mapping

    private void RegisterTracks(IEnumerable<TrackItem> tracks)
    {
        foreach (var t in tracks) RegisterTrack(t);
    }

    private void RegisterTrack(TrackItem track)
    {
        if (!string.IsNullOrWhiteSpace(track.UniqueKey))
            _trackIndex[track.UniqueKey] = track;
    }

    private bool TryResolveTrack(string? trackKey, out TrackItem track)
    {
        if (!string.IsNullOrWhiteSpace(trackKey) && _trackIndex.TryGetValue(trackKey, out var found))
        {
            track = found;
            return true;
        }
        if (_player.CurrentTrack != null &&
            string.Equals(_player.CurrentTrack.UniqueKey, trackKey, StringComparison.OrdinalIgnoreCase))
        {
            track = _player.CurrentTrack;
            return true;
        }
        track = null!;
        return false;
    }


    private static object ToSearchGroupDto(SearchTrackGroup g)
    {
        var t = g.ActiveTrack;
        return new
        {
            id = t.Id,
            uniqueKey = t.UniqueKey,
            source = t.Source,
            brandKey = t.BrandKey,
            brandColor = t.BrandColor,
            title = t.Title,
            artist = t.Artist,
            pageUrl = t.PageUrl,
            coverUrl = t.CoverUrl,
            durationText = t.DurationText,
            durationMs = t.Duration > TimeSpan.Zero ? (long)t.Duration.TotalMilliseconds : 0,
            isLiked = t.IsLiked,
            isCurrentlyPlaying = t.IsCurrentlyPlaying,
            isDownloading = t.IsDownloading,
            downloadProgress = t.DownloadProgress,
            isDownloaded = t.IsDownloaded,
            groupKey = g.GroupKey,
            selectedIndex = g.SelectedIndex,
            variants = g.Variants.Select(ToDto).ToList()
        };
    }

    private static object ToDto(TrackItem t) => new
    {
        id = t.Id,
        uniqueKey = t.UniqueKey,
        source = t.Source,
        brandKey = t.BrandKey,
        brandColor = t.BrandColor,
        title = t.Title,
        artist = t.Artist,
        pageUrl = t.PageUrl,
        coverUrl = t.CoverUrl,
        durationText = t.DurationText,
        durationMs = t.Duration > TimeSpan.Zero ? (long)t.Duration.TotalMilliseconds : 0,
        isLiked = t.IsLiked,
        isCurrentlyPlaying = t.IsCurrentlyPlaying,
        isDownloading = t.IsDownloading,
        downloadProgress = t.DownloadProgress,
        isDownloaded = t.IsDownloaded
    };

    private static object ToPlaylistDto(UserPlaylist pl) => new
    {
        id = pl.Id,
        name = pl.Name,
        isPinned = pl.IsPinned,
        isExternal = pl.IsExternal,
        platformBrand = pl.PlatformBrand,
        countText = pl.CountText,
        showPlatformBadge = pl.ShowPlatformBadge,
        platformBadgeText = pl.PlatformBadgeText,
        platformBadgeColor = pl.PlatformBadgeColor,
        platformBadgeForeground = pl.PlatformBadgeForeground,
        coverUrl = !string.IsNullOrWhiteSpace(pl.CoverUrl) ? pl.CoverUrl : (pl.Tracks.FirstOrDefault()?.CoverUrl ?? ""),
        tracks = pl.Tracks.Select(ToDto).ToList()
    };

    private static string? ReadStr(JsonElement root, string prop)
        => root.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static int ReadInt(JsonElement root, string prop, int fallback)
        => root.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.Number ? el.GetInt32() : fallback;

    #endregion
}
