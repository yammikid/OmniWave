using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using DynamicIsland.Services;
using Microsoft.Web.WebView2.Wpf;

namespace DynamicIsland;

public partial class PlayerWindow : Window
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly List<IMusicProvider> _providers;
    private readonly AudioPlayerService _player;
    private readonly MediaControllerService _controller;
    private readonly LibraryService _library;
    private readonly LyricsService _lyrics;
    private readonly SettingsService _settings;
    private readonly DownloadService _downloads;
    private readonly YandexMusicProvider _yandex;
    private readonly VkMusicProvider _vk;
    private readonly Action _openSettingsAction;
    private readonly Action _openAccountsAction;
    private readonly Func<Task<string>> _syncAccountsAction;

    private readonly Dictionary<string, TrackItem> _trackIndex = new(StringComparer.OrdinalIgnoreCase);

    private CancellationTokenSource? _searchCts;
    private CancellationTokenSource? _lyricsCts;
    private List<TrackItem> _currentResults = new();
    private List<TrackItem> _favorites = new();

    private string _lastQuery = "";
    private string _activeSearchFilter = "all";
    private int _currentLimitPerProvider = 10;
    private int _savedVolumeBeforeMute = 80;
    private bool _webViewReady;
    private bool _isPlaying;
    private bool _isExportingToYandex;
    private string? _lastExternalVisualKey;
    private TrackItem? _externalActiveTrack;
    private long _currentMs;
    private long _totalMs;
    private long _lastPosPushTick;
    private string? _lastCoverDataUrl;
    private Color _lastAccentColor = Color.FromRgb(255, 204, 0);

    public bool AllowClose { get; set; }

    public PlayerWindow(
        List<IMusicProvider> providers,
        AudioPlayerService player,
        MediaControllerService controller,
        LibraryService library,
        LyricsService lyrics,
        SettingsService settings,
        DownloadService downloads,
        YandexMusicProvider yandex,
        VkMusicProvider vk,
        Action openSettingsAction,
        Action openAccountsAction,
        Func<Task<string>> syncAccountsAction)
    {
        InitializeComponent();
        _providers = providers;
        _player = player;
        _controller = controller;
        _library = library;
        _lyrics = lyrics;
        _settings = settings;
        _downloads = downloads;
        _yandex = yandex;
        _vk = vk;
        _openSettingsAction = openSettingsAction;
        _openAccountsAction = openAccountsAction;
        _syncAccountsAction = syncAccountsAction;

        Loaded += async (_, _) => await InitWebViewAsync();
        StateChanged += (_, _) => PostToWeb("windowState", new { maximized = WindowState == WindowState.Maximized });

        _player.PositionChanged += OnPositionChanged;
        _player.PlaybackStateChanged += OnPlaybackStateChanged;
        _player.TrackChanged += OnTrackChanged;
        _player.RepeatModeChanged += mode => Dispatcher.BeginInvoke(() =>
            PostToWeb("repeatMode", new { mode = (int)mode }));
        _player.StatusMessage += msg => Dispatcher.BeginInvoke(() =>
            PostToWeb("status", new { message = msg }));
        _player.QueueChanged += () => Dispatcher.BeginInvoke(PushQueueUpdate);
        _player.VolumeChanged += v => Dispatcher.BeginInvoke(() =>
            PostToWeb("volume", new { volume = v }));
        _library.LibraryChanged += () => Dispatcher.BeginInvoke(PushLibraryUpdate);
        _downloads.DownloadCompleted += _ => Dispatcher.BeginInvoke(SyncAllDownloadedStates);
        _settings.SettingsChanged += _ => Dispatcher.BeginInvoke(PushAccountsUpdate);
        _controller.StateChanged += state => Dispatcher.BeginInvoke(() => OnControllerStateChanged(state));
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowBlurHelper.ApplyDarkAcrylic(this, true, extendFrame: true);
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!AllowClose)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        try { PlayerWebView?.Dispose(); } catch { }
        base.OnClosing(e);
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

            PlayerWebView.DefaultBackgroundColor = System.Drawing.Color.Transparent;
            PlayerWebView.CreationProperties = new CoreWebView2CreationProperties
            {
                UserDataFolder = userDataFolder
            };

            await PlayerWebView.EnsureCoreWebView2Async();
            PlayerWebView.DefaultBackgroundColor = System.Drawing.Color.Transparent;

            var wvSettings = PlayerWebView.CoreWebView2.Settings;
            wvSettings.AreDefaultContextMenusEnabled = false;
            wvSettings.IsStatusBarEnabled = false;
            wvSettings.IsZoomControlEnabled = false;

            PlayerWebView.WebMessageReceived += (_, ev) =>
            {
                try { HandleWebMessage(ev.WebMessageAsJson); } catch { }
            };

            FallbackLoadingText.Visibility = Visibility.Collapsed;
            PlayerWebView.NavigateToString(PlayerLiquidGlassHtml.Html);
        }
        catch (Exception ex)
        {
            FallbackLoadingText.Text = $"WebView2 недоступен: {ex.Message}";
        }
    }

    public void OpenPlaylistsTabAndFocusNew()
    {
        PostToWeb("focusNewPlaylist", new { });
    }

    private void PostToWeb(string type, object payload)
    {
        if (!_webViewReady || PlayerWebView.CoreWebView2 == null) return;

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
                WindowState = WindowState.Minimized;
                break;

            case "window.maximizeToggle":
                WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
                break;

            case "window.hideToIsland":
                Hide();
                break;

            case "window.openSettings":
                _openSettingsAction();
                break;

            case "window.openAccounts":
                _openAccountsAction();
                break;

            case "yandex.startWave":
                await StartYandexMyWaveAsync();
                break;

            case "vk.startMix":
                await StartVkMixAsync();
                break;

            case "sidebar.setCollapsed":
                if (root.TryGetProperty("collapsed", out var colEl))
                {
                    bool collapsed = colEl.GetBoolean();
                    _settings.Update(s => s.IsSidebarCollapsed = collapsed, notify: false);
                }
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

            case "playlists.addTrack":
            {
                var plId = ReadStr(root, "playlistId");
                var key = ReadStr(root, "trackKey");
                if (!string.IsNullOrWhiteSpace(plId) && TryResolveTrack(key, out var track))
                {
                    var pl = _library.GetLocalPlaylists().FirstOrDefault(p => p.Id == plId);
                    if (_library.AddTrackToPlaylist(plId, track) && pl != null)
                        PostToWeb("status", new { message = $"Добавлено в «{pl.Name}»" });
                }
                break;
            }

            case "playlists.removeTrack":
            {
                var plId = ReadStr(root, "playlistId");
                var key = ReadStr(root, "trackKey");
                if (!string.IsNullOrWhiteSpace(plId) && !string.IsNullOrWhiteSpace(key))
                    _library.RemoveTrackFromPlaylist(plId, key);
                break;
            }

            case "playlists.quickCreateAndAdd":
            {
                var key = ReadStr(root, "trackKey");
                if (TryResolveTrack(key, out var track))
                {
                    var created = _library.CreatePlaylist($"Микс • {DateTime.Now:dd.MM}");
                    _library.AddTrackToPlaylist(created.Id, track);
                    PostToWeb("status", new { message = $"Создан «{created.Name}» и добавлен трек" });
                }
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

            case "player.seekMs":
                if (root.TryGetProperty("ms", out var msEl) && msEl.ValueKind == JsonValueKind.Number)
                {
                    long ms = (long)msEl.GetDouble();
                    if (_controller.IsLocalActive)
                        _player.SeekToMs(ms);
                    else if (_totalMs > 0)
                        await _controller.SeekFractionAsync((double)ms / _totalMs);
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

            case "player.shuffle":
                _player.ShuffleQueue();
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
        }
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

        var (accentHex, accentRgb, orb2Rgb, orb3Rgb) = ComputePalette(_lastAccentColor);
        var s = _settings.Current;

        PostToWeb("initState", new
        {
            sidebarCollapsed = s.IsSidebarCollapsed,
            yandexConnected = _yandex.IsAuthenticated,
            yandexUserName = s.YandexUserName ?? "",
            vkConnected = _vk.IsAuthenticated,
            vkUserName = s.VkUserName ?? "",
            favorites = _favorites.Select(ToDto).ToList(),
            playlists = playlists.Select(ToPlaylistDto).ToList(),
            queue = queueItems.Select(ToDto).ToList(),
            currentTrack = cur != null ? ToDto(cur) : null,
            coverDataUrl = _lastCoverDataUrl,
            accentHex,
            accentRgb,
            orb2Rgb,
            orb3Rgb,
            isPlaying = _isPlaying,
            currentMs = _currentMs,
            totalMs = _totalMs,
            volume = _player.Volume,
            repeatMode = (int)_player.RepeatMode,
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
            windowMaximized = WindowState == WindowState.Maximized
        });

        if (cur != null)
            _ = LoadLyricsForTrackAsync(cur);
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
        _downloads.SyncDownloadedFlags(_currentResults);

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
            vkUserName = s.VkUserName ?? ""
        });
    }

    private void PushQueueUpdate()
    {
        var (items, _) = _player.GetQueueSnapshot();
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
        _downloads.SyncDownloadedFlags(_currentResults);
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
        allAvailable.AddRange(_providers.Where(p => p.BrandKey is "soundcloud" or "youtube" or "audius" or "bandcamp" or "open_catalog" or "radio"));

        if (_activeSearchFilter == "all")
            return allAvailable;

        return allAvailable
            .Where(p => string.Equals(p.BrandKey, _activeSearchFilter, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private async Task PerformSearchAsync(string query)
    {
        if (string.IsNullOrEmpty(query) && _activeSearchFilter != "radio") return;

        if (TryBuildDirectUrlTrack(query, out var directTrack))
        {
            _searchCts?.Cancel();
            _library.SyncLikedFlags(new[] { directTrack });
            _downloads.SyncDownloadedFlags(new[] { directTrack });
            RegisterTrack(directTrack);

            _currentResults = new List<TrackItem> { directTrack };
            PostToWeb("searchResults", new
            {
                items = _currentResults.Select(ToDto).ToList(),
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
                items = _currentResults.Select(ToDto).ToList(),
                status = "Выберите хотя бы один источник",
                canLoadMore = false,
                isLoading = false
            });
            return;
        }

        _lastQuery = query;
        _currentLimitPerProvider = 10;

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
            var merged = await QueryMergedAsync(query, _currentLimitPerProvider, activeProviders, ct);
            if (ct.IsCancellationRequested) return;

            _library.SyncLikedFlags(merged);
            _downloads.SyncDownloadedFlags(merged);
            RegisterTracks(merged);
            _currentResults = merged;

            PostToWeb("searchResults", new
            {
                items = _currentResults.Select(ToDto).ToList(),
                status = merged.Count > 0 ? $"Найдено: {merged.Count}. Двойной клик — включить" : "Ничего не найдено",
                canLoadMore = merged.Count > 0 && _currentLimitPerProvider < 50,
                isLoading = false
            });
        }
        catch (OperationCanceledException) { }
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
            items = _currentResults.Select(ToDto).ToList(),
            status = "Загружаем ещё...",
            canLoadMore = false,
            isLoading = true
        });

        try
        {
            var expanded = await QueryMergedAsync(_lastQuery, nextLimit, activeProviders, ct);
            if (ct.IsCancellationRequested) return;

            var existingKeys = _currentResults.Select(t => t.UniqueKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var newlyAdded = expanded.Where(item => existingKeys.Add(item.UniqueKey)).ToList();

            if (newlyAdded.Count > 0)
            {
                _library.SyncLikedFlags(newlyAdded);
                _downloads.SyncDownloadedFlags(newlyAdded);
                RegisterTracks(newlyAdded);
                _currentResults.AddRange(newlyAdded);
            }

            PostToWeb("searchResults", new
            {
                items = _currentResults.Select(ToDto).ToList(),
                status = $"Показано треков: {_currentResults.Count}",
                canLoadMore = nextLimit < 50 && newlyAdded.Count > 0,
                isLoading = false
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            PostToWeb("searchResults", new
            {
                items = _currentResults.Select(ToDto).ToList(),
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
        var tasks = providers.Select(p => p.SearchAsync(query, limitPerProvider, ct)).ToList();
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
            _openAccountsAction();
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

    #endregion

    #region События плеера, обложка и тексты

    private void OnTrackChanged(TrackItem track)
    {
        _externalActiveTrack = null;
        _lastExternalVisualKey = null;
        _currentMs = 0;
        _totalMs = track.Duration > TimeSpan.Zero ? (long)track.Duration.TotalMilliseconds : 0;
        RegisterTrack(track);
        _downloads.TryGetLocalFile(track);
        _library.SyncLikedFlags(new[] { track });

        var brandFallback = ColorUtils.ParseHex(track.BrandColor, Color.FromRgb(255, 204, 0));
        _lastAccentColor = brandFallback;
        _lastCoverDataUrl = null;
        var (accentHex, accentRgb, orb2Rgb, orb3Rgb) = ComputePalette(brandFallback);

        Dispatcher.BeginInvoke(() =>
        {
            PostToWeb("trackChanged", new
            {
                track = ToDto(track),
                coverDataUrl = (string?)null,
                accentHex,
                accentRgb,
                orb2Rgb,
                orb3Rgb
            });
        });

        _ = LoadLyricsForTrackAsync(track);
        _ = LoadCoverAndGlowAsync(track, brandFallback);
    }

    private async Task LoadCoverAndGlowAsync(TrackItem track, Color brandFallback)
    {
        var bytes = await _controller.DownloadCoverAsync(track.CoverUrl);
        if (_player.CurrentTrack?.UniqueKey != track.UniqueKey) return;

        string? dataUrl = null;
        Color glowColor = brandFallback;

        if (bytes != null && bytes.Length > 0)
        {
            var bmp = ColorUtils.BytesToBitmap(bytes);
            if (bmp != null)
            {
                glowColor = ColorUtils.ExtractAmbientColor(bmp, brandFallback);
                int chroma = Math.Max(glowColor.R, Math.Max(glowColor.G, glowColor.B)) -
                             Math.Min(glowColor.R, Math.Min(glowColor.G, glowColor.B));
                if (chroma < 32)
                    glowColor = brandFallback;
            }
            dataUrl = "data:image/jpeg;base64," + Convert.ToBase64String(bytes);
        }

        _lastAccentColor = glowColor;
        _lastCoverDataUrl = dataUrl;
        var (accentHex, accentRgb, orb2Rgb, orb3Rgb) = ComputePalette(glowColor);

        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_player.CurrentTrack?.UniqueKey != track.UniqueKey) return;
            PostToWeb("trackChanged", new
            {
                track = ToDto(track),
                coverDataUrl = dataUrl,
                accentHex,
                accentRgb,
                orb2Rgb,
                orb3Rgb
            });
        });
    }

    private void OnPositionChanged(long currentMs, long totalMs)
    {
        if (!_controller.IsLocalActive && _externalActiveTrack != null) return;
        _currentMs = currentMs;
        if (totalMs > 0) _totalMs = totalMs;

        long now = Environment.TickCount64;
        if (_player.IsPlaying && now - _lastPosPushTick < 180) return;
        _lastPosPushTick = now;

        Dispatcher.BeginInvoke(() =>
            PostToWeb("position", new { currentMs = _currentMs, totalMs = _totalMs }));
    }

    public void NotifySeekVisual(long targetMs, long deltaMs)
    {
        if (!_webViewReady) return;
        Dispatcher.BeginInvoke(() =>
            PostToWeb("seekFeedback", new { targetMs, deltaMs }));
    }

    private void OnPlaybackStateChanged(bool isPlaying)
    {
        if (!_controller.IsLocalActive && _externalActiveTrack != null) return;
        _isPlaying = isPlaying;
        Dispatcher.BeginInvoke(() => PostToWeb("playbackState", new { isPlaying }));
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
                    PostToWeb("playbackState", new { isPlaying = _player.IsPlaying });
                    PostToWeb("position", new { currentMs = _player.CurrentTimeMs, totalMs = _player.TotalLengthMs });
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
            var brandFallback = ColorUtils.ParseHex(extTrack.BrandColor, Color.FromRgb(255, 204, 0));

            if (trackChanged)
            {
                _lastAccentColor = brandFallback;
                _lastCoverDataUrl = null;
            }

            if (state.CoverBytes != null && state.CoverBytes.Length > 0)
            {
                var bmp = ColorUtils.BytesToBitmap(state.CoverBytes);
                if (bmp != null)
                {
                    var glow = ColorUtils.ExtractAmbientColor(bmp, brandFallback);
                    int chroma = Math.Max(glow.R, Math.Max(glow.G, glow.B)) -
                                 Math.Min(glow.R, Math.Min(glow.G, glow.B));
                    _lastAccentColor = chroma < 32 ? brandFallback : glow;
                }
                _lastCoverDataUrl = "data:image/jpeg;base64," + Convert.ToBase64String(state.CoverBytes);
            }

            var (accentHex, accentRgb, orb2Rgb, orb3Rgb) = ComputePalette(_lastAccentColor);
            PostToWeb("trackChanged", new
            {
                track = ToDto(extTrack),
                coverDataUrl = _lastCoverDataUrl,
                accentHex,
                accentRgb,
                orb2Rgb,
                orb3Rgb
            });

            if (trackChanged)
                _ = LoadLyricsForTrackAsync(extTrack);
        }

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
        PostToWeb("showDrawerTab", new { tab = 1 });
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
        PostToWeb("showDrawerTab", new { tab = 1 });
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

    private async Task LoadLyricsForTrackAsync(TrackItem track)
    {
        _lyricsCts?.Cancel();
        _lyricsCts = new CancellationTokenSource();
        var ct = _lyricsCts.Token;

        _ = Dispatcher.BeginInvoke(() => PostToWeb("lyricsUpdate", new
        {
            title = track.Title,
            sourceInfo = "Поиск текста на LRCLIB...",
            hasSynced = false,
            syncedLines = Array.Empty<object>(),
            plainText = "",
            emptyMessage = "Загружаем текст песни..."
        }));

        try
        {
            var res = await _lyrics.GetLyricsAsync(track, ct);
            if (ct.IsCancellationRequested) return;

            _ = Dispatcher.BeginInvoke(() => PostToWeb("lyricsUpdate", new
            {
                title = track.Title,
                sourceInfo = res.SourceInfo,
                hasSynced = res.HasSynced,
                syncedLines = res.SyncedLines.Select(l => new
                {
                    timestampMs = (long)l.Timestamp.TotalMilliseconds,
                    text = l.Text
                }).ToList(),
                plainText = res.PlainText ?? "",
                emptyMessage = res.HasLyrics ? "" : "Текст песни для этого трека не найден."
            }));
        }
        catch (OperationCanceledException) { }
        catch
        {
            _ = Dispatcher.BeginInvoke(() => PostToWeb("lyricsUpdate", new
            {
                title = track.Title,
                sourceInfo = "",
                hasSynced = false,
                syncedLines = Array.Empty<object>(),
                plainText = "",
                emptyMessage = "Не удалось загрузить текст песни."
            }));
        }
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

    private static (string Hex, string Rgb, string Orb2Rgb, string Orb3Rgb) ComputePalette(Color c)
    {
        string hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        string rgb = $"{c.R}, {c.G}, {c.B}";
        int r2 = Math.Clamp((int)(c.B * 0.78 + 30), 30, 235);
        int g2 = Math.Clamp((int)(c.R * 0.52 + 45), 35, 235);
        int b2 = Math.Clamp((int)(c.G * 0.88 + 40), 50, 245);
        int r3 = Math.Clamp((int)(c.G * 0.65 + 45), 40, 235);
        int g3 = Math.Clamp((int)(c.B * 0.45 + 30), 30, 210);
        int b3 = Math.Clamp((int)(c.R * 0.85 + 65), 60, 245);
        return (hex, rgb, $"{r2}, {g2}, {b2}", $"{r3}, {g3}, {b3}");
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
        isDownloading = t.IsDownloading,
        downloadProgress = t.DownloadProgress,
        isDownloaded = t.IsDownloaded
    };

    private static object ToPlaylistDto(UserPlaylist pl) => new
    {
        id = pl.Id,
        name = pl.Name,
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
