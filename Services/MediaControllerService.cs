using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using Windows.Media.Control;

namespace DynamicIsland.Services;

/// <summary>
/// Агрегатор медиа-источников:
/// 1. Встроенный плеер OmniWave (local:omniwave — включая «Мою волну», «VK Микс» и все провайдеры)
/// 2. Десктопные небраузерные сессии Windows GSMTC (gsmtc:* — Spotify Desktop, Telegram и др.)
/// </summary>
public class MediaControllerService : IDisposable
{
    public const string LocalSourceId = "local:omniwave";

    private static readonly Regex SiteSuffixRegex = new(
        @"\s*(?:[—–\-|•]\s*)(?:Яндекс\s*Музыка|Yandex\s*Music|ВК\s*Музыка|ВКонтакте|VK\s*Music|YouTube\s*Music|YouTube|SoundCloud|Spotify|Audius|Bandcamp)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly AudioPlayerService _localPlayer;
    private readonly SettingsService _settings;
    private readonly IReadOnlyList<IMusicProvider> _providers;
    private readonly HttpClient _http = new(new SocketsHttpHandler
    {
        UseProxy = true,
        PooledConnectionLifetime = TimeSpan.FromMinutes(2)
    })
    {
        Timeout = TimeSpan.FromSeconds(5)
    };
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _pollWakeup = new(0, 1);
    private readonly ConcurrentDictionary<string, byte[]> _coverCache = new();
    private readonly ConcurrentDictionary<string, string> _resolvedCoverUrlCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (long AnchorMs, long AnchorTick, bool WasPlaying)> _gsmtcTimelineFallbackAnchors = new(StringComparer.OrdinalIgnoreCase);

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private string? _selectedId;
    private string? _lastTrackKey;
    private string _lastCoverUrl = "";
    private string? _lastEmittedStateSig;
    private DateTime _manualSelectLockUntilUtc = DateTime.MinValue;
    private volatile bool _disposed;

    public bool IsLocalActive => _selectedId == LocalSourceId;
    public MediaState? CurrentMediaState { get; private set; }
    public event Action<MediaState>? StateChanged;

    public MediaControllerService(
        AudioPlayerService localPlayer,
        SettingsService settings,
        IReadOnlyList<IMusicProvider>? providers = null)
    {
        _localPlayer = localPlayer;
        _settings = settings;
        _providers = providers ?? Array.Empty<IMusicProvider>();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0");

        _localPlayer.TrackChanged += track =>
        {
            _ = track;
            var prevId = _selectedId;
            _selectedId = LocalSourceId;
            _lastTrackKey = null;

            if (!string.IsNullOrEmpty(prevId) &&
                prevId != LocalSourceId &&
                _settings.Current.AutoPauseOnSwitch)
            {
                _ = PauseSourceAsync(prevId);
            }
        };

        _localPlayer.PlaybackStateChanged += isPlaying =>
        {
            if (!isPlaying) return;

            var prevId = _selectedId;
            if (prevId != LocalSourceId)
            {
                _selectedId = LocalSourceId;
                _lastTrackKey = null;
                if (!string.IsNullOrEmpty(prevId) && _settings.Current.AutoPauseOnSwitch)
                    _ = PauseSourceAsync(prevId);
            }
        };
    }

    public void Start() => _ = Task.Run(() => PollLoopAsync(_cts.Token));

    public void ForceRefresh()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var s = await FetchStateAsync(_cts.Token);
                PublishState(s, force: true);
            }
            catch { }
        });
    }

    public void SelectSource(string sourceId)
    {
        var prevId = _selectedId;
        _selectedId = sourceId;
        _lastTrackKey = null;
        _manualSelectLockUntilUtc = DateTime.UtcNow.AddSeconds(8);

        if (!string.IsNullOrEmpty(prevId) &&
            prevId != _selectedId &&
            _settings.Current.AutoPauseOnSwitch)
        {
            _ = PauseSourceAsync(prevId);
        }

        ForceRefresh();
    }

    public async Task PauseSourceAsync(string? sourceId)
    {
        if (string.IsNullOrEmpty(sourceId)) return;

        if (sourceId == LocalSourceId)
        {
            _localPlayer.Pause();
            return;
        }

        if (_manager == null || !sourceId.StartsWith("gsmtc:", StringComparison.Ordinal)) return;
        var appId = sourceId["gsmtc:".Length..];
        var session = _manager.GetSessions()
            .FirstOrDefault(s => s.SourceAppUserModelId == appId);
        if (session == null) return;

        await SendGsmtcCommandAsync(session, "pause");
    }

    public async Task SendCommandAsync(string cmd)
    {
        if (string.IsNullOrEmpty(_selectedId)) return;

        // 1. Встроенный плеер OmniWave
        if (_selectedId == LocalSourceId)
        {
            if (cmd == "toggle") _localPlayer.TogglePause();
            else if (cmd == "pause") _localPlayer.Pause();
            else if (cmd == "play") _localPlayer.Play();
            else if (cmd == "next") await _localPlayer.NextAsync();
            else if (cmd == "prev") await _localPlayer.PrevAsync();
            return;
        }

        // 2. Системная небраузерная сессия Windows GSMTC
        if (_manager == null || !_selectedId.StartsWith("gsmtc:", StringComparison.Ordinal)) return;
        var appId = _selectedId["gsmtc:".Length..];
        var session = _manager.GetSessions()
                              .FirstOrDefault(s => s.SourceAppUserModelId == appId);
        if (session == null) return;

        await SendGsmtcCommandAsync(session, cmd);
    }

    public async Task SeekFractionAsync(double fraction)
    {
        fraction = Math.Clamp(fraction, 0.0, 1.0);

        if (_selectedId == LocalSourceId)
        {
            _localPlayer.SeekFraction(fraction);
            return;
        }

        if (_manager == null || string.IsNullOrEmpty(_selectedId) || !_selectedId.StartsWith("gsmtc:", StringComparison.Ordinal))
            return;

        var appId = _selectedId["gsmtc:".Length..];
        var session = _manager.GetSessions()
                              .FirstOrDefault(s => s.SourceAppUserModelId == appId);
        if (session == null) return;

        await SeekGsmtcFractionAsync(session, fraction);
    }

    public async Task SeekRelativeMsAsync(long deltaMs)
    {
        if (_selectedId == LocalSourceId)
        {
            _localPlayer.SeekRelativeMs(deltaMs);
            return;
        }

        if (_manager == null || string.IsNullOrEmpty(_selectedId) || !_selectedId.StartsWith("gsmtc:", StringComparison.Ordinal))
            return;

        var appId = _selectedId["gsmtc:".Length..];
        var session = _manager.GetSessions()
                              .FirstOrDefault(s => s.SourceAppUserModelId == appId);
        if (session == null) return;

        await SeekGsmtcRelativeMsAsync(session, deltaMs);
    }

    private static bool IsGsmtcSessionPlaying(GlobalSystemMediaTransportControlsSession session)
    {
        try
        {
            return session.GetPlaybackInfo().PlaybackStatus ==
                   GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> SendGsmtcCommandAsync(
        GlobalSystemMediaTransportControlsSession session,
        string cmd)
    {
        try
        {
            if (cmd == "toggle")
            {
                var status = session.GetPlaybackInfo().PlaybackStatus;
                bool ok = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing
                    ? await session.TryPauseAsync()
                    : await session.TryPlayAsync();

                if (!ok)
                    ok = await session.TryTogglePlayPauseAsync();
                return ok;
            }
            if (cmd == "pause")
                return await session.TryPauseAsync();
            if (cmd == "play")
                return await session.TryPlayAsync();
            if (cmd == "stop")
                return await session.TryPauseAsync();
            if (cmd == "next")
                return await session.TrySkipNextAsync();
            if (cmd == "prev")
                return await session.TrySkipPreviousAsync();
        }
        catch { }
        return false;
    }

    private async Task<bool> SeekGsmtcFractionAsync(
        GlobalSystemMediaTransportControlsSession session,
        double fraction)
    {
        try
        {
            var tl = session.GetTimelineProperties();
            var duration = tl.EndTime - tl.StartTime;
            if (duration.TotalMilliseconds <= 0) return false;

            var target = tl.StartTime + TimeSpan.FromMilliseconds(duration.TotalMilliseconds * Math.Clamp(fraction, 0.0, 1.0));
            return await session.TryChangePlaybackPositionAsync(target.Ticks);
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> SeekGsmtcRelativeMsAsync(
        GlobalSystemMediaTransportControlsSession session,
        long deltaMs)
    {
        try
        {
            bool playing = IsGsmtcSessionPlaying(session);
            var (curMs, totalMs) = ReadGsmtcTimeline(session, playing);
            var tl = session.GetTimelineProperties();
            long maxMs = totalMs > 0 ? totalMs : Math.Max(0, curMs + deltaMs);
            long targetMs = Math.Clamp(curMs + deltaMs, 0, maxMs);
            var target = tl.StartTime + TimeSpan.FromMilliseconds(targetMs);
            return await session.TryChangePlaybackPositionAsync(target.Ticks);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Регулировка громкости приложения (не меняет общий микшер Windows!).
    /// </summary>
    public string AdjustVolumeStep(int deltaSteps)
    {
        int next = Math.Clamp(_localPlayer.Volume + deltaSteps * 5, 0, 100);
        SetAppVolume(next);
        return $"{next}%";
    }

    /// <summary>
    /// Устанавливает программную громкость внутри OmniWave (0..100%), не затрагивая общий микшер Windows.
    /// </summary>
    public void SetAppVolume(int volume0To100)
    {
        int clamped = Math.Clamp(volume0To100, 0, 100);
        _localPlayer.SetVolume(clamped);
        _settings.Update(s => s.Volume = clamped, notify: false);
    }

    private volatile bool _isUiVisible = true;
    private Windows.Foundation.TypedEventHandler<GlobalSystemMediaTransportControlsSessionManager, SessionsChangedEventArgs>? _sessionsChangedHandler;

    public void SetUiVisible(bool visible)
    {
        _isUiVisible = visible;
        if (visible)
            WakePollLoop();
    }

    private void WakePollLoop()
    {
        if (_disposed) return;
        try
        {
            if (_pollWakeup.CurrentCount == 0)
                _pollWakeup.Release();
        }
        catch { }
    }

    private void PublishState(MediaState state, bool force)
    {
        CurrentMediaState = state;

        bool hasCoverBytes = state.CoverUpdated && state.CoverBytes != null && state.CoverBytes.Length > 0;
        string sourcesSig = state.Sources.Count == 0
            ? ""
            : string.Join(",", state.Sources.Select(x => x.SourceId));

        string sig = string.IsNullOrEmpty(state.ActiveId)
            ? $"EMPTY|{sourcesSig}"
            : state.ActiveId == LocalSourceId
                ? $"LOCAL|{state.ActiveBrand}|{state.SourceBadgeText}|{state.Title}|{state.Artist}|{state.IsPlaying}|{sourcesSig}"
                : $"EXT|{state.ActiveId}|{state.ActiveBrand}|{state.Title}|{state.Artist}|{state.IsPlaying}|{state.CurrentMs}|{state.TotalMs}|{sourcesSig}";

        if (!force && !hasCoverBytes && string.Equals(sig, _lastEmittedStateSig, StringComparison.Ordinal))
            return;

        _lastEmittedStateSig = sig;
        StateChanged?.Invoke(state);
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_manager != null)
            {
                _sessionsChangedHandler = (_, _) => WakePollLoop();
                _manager.SessionsChanged += _sessionsChangedHandler;
            }
        }
        catch { }

        while (!ct.IsCancellationRequested)
        {
            MediaState? state = null;
            try
            {
                state = await FetchStateAsync(ct);
                PublishState(state, force: false);
            }
            catch { }

            int delayMs;
            if (!_isUiVisible)
                delayMs = IsLocalActive ? 5000 : 3000;
            else if (IsLocalActive && _localPlayer.IsActiveOrLoading)
                delayMs = 2000;
            else if (state != null && !string.IsNullOrEmpty(state.ActiveId) && state.ActiveId != LocalSourceId && state.IsPlaying)
                delayMs = 450;
            else
                delayMs = 1200;

            try { await _pollWakeup.WaitAsync(delayMs, ct); }
            catch { break; }
        }
    }

    private async Task<MediaState> FetchStateAsync(CancellationToken ct)
    {
        var sources = new List<SourceItem>();
        var gsmtcMap = new Dictionary<string, GlobalSystemMediaTransportControlsSession>();
        var gsmtcPropsMap = new Dictionary<string, GlobalSystemMediaTransportControlsSessionMediaProperties?>();

        // 1. Вкладка OmniWave (встроенный плеер приложения — включая «Мою волну» и «VK Микс»)
        var localTrack = _localPlayer.CurrentTrack;
        if (localTrack != null)
        {
            sources.Add(new SourceItem(LocalSourceId, "OmniWave", localTrack.BrandKey));
        }

        // 2. Сканируем небраузерные сессии Windows GSMTC (Spotify Desktop, Telegram и др.)
        if (_manager != null)
        {
            foreach (var session in _manager.GetSessions())
            {
                var appId = session.SourceAppUserModelId;
                if (string.IsNullOrWhiteSpace(appId)) continue;
                if (IsBrowser(appId)) continue;

                var sid = $"gsmtc:{appId}";
                if (gsmtcMap.ContainsKey(sid))
                    continue;

                GlobalSystemMediaTransportControlsSessionMediaProperties? props = null;
                try { props = await session.TryGetMediaPropertiesAsync(); } catch { }

                var (name, brand) = DetectSourceAndBrand(appId, props);
                gsmtcMap[sid] = session;
                gsmtcPropsMap[sid] = props;
                sources.Add(new SourceItem(sid, name, brand));
            }
        }

        if (sources.Count == 0)
        {
            _selectedId = null;
            _lastTrackKey = null;
            return new MediaState { CoverUpdated = true };
        }

        bool isManuallyLocked = DateTime.UtcNow < _manualSelectLockUntilUtc &&
                                _selectedId != null &&
                                sources.Any(s => s.SourceId == _selectedId);
        bool currentIsPlaying = IsSourceCurrentlyPlaying(_selectedId, localTrack, gsmtcMap);

        if (!isManuallyLocked && (_selectedId == null || sources.All(s => s.SourceId != _selectedId) || !currentIsPlaying))
        {
            string? playingId = null;
            if (localTrack != null && _localPlayer.IsActiveOrLoading)
            {
                playingId = LocalSourceId;
            }
            else
            {
                playingId = gsmtcMap.FirstOrDefault(kv => IsGsmtcSessionPlaying(kv.Value)).Key;
            }

            if (!string.IsNullOrEmpty(playingId) && playingId != _selectedId)
            {
                _selectedId = playingId;
                _lastTrackKey = null;
            }
            else if (_selectedId == null || sources.All(s => s.SourceId != _selectedId))
            {
                _selectedId = sources[0].SourceId;
                _lastTrackKey = null;
            }
        }

        var active = sources.First(s => s.SourceId == _selectedId);

        // --- Источник 1: встроенный плеер OmniWave ---
        if (_selectedId == LocalSourceId && localTrack != null)
        {
            var key = $"{LocalSourceId}|{localTrack.Id}|{localTrack.CoverUrl}";
            bool updated = key != _lastTrackKey;
            if (updated)
            {
                _lastTrackKey = key;
                _lastCoverUrl = localTrack.CoverUrl;
            }

            string localBadge = _localPlayer.IsYandexWaveActive
                ? "Моя волна"
                : (_localPlayer.IsVkMixActive ? "VK Микс" : "");

            return new MediaState
            {
                Sources = sources,
                ActiveId = LocalSourceId,
                ActiveBrand = localTrack.BrandKey,
                SourceBadgeText = localBadge,
                Title = localTrack.Title,
                Artist = localTrack.Artist,
                IsPlaying = _localPlayer.IsPlaying,
                CoverUrl = localTrack.CoverUrl,
                CoverBytes = null,
                CoverUpdated = updated,
                CurrentMs = _localPlayer.CurrentTimeMs,
                TotalMs = _localPlayer.TotalLengthMs
            };
        }

        // --- Источник 2: Windows GSMTC (небраузерные приложения) ---
        if (string.IsNullOrEmpty(_selectedId) || !gsmtcMap.TryGetValue(_selectedId, out var gsmtc))
            return new MediaState { Sources = sources, CoverUpdated = true };

        bool isPlaying = IsGsmtcSessionPlaying(gsmtc);
        var (currentMs, totalMs) = ReadGsmtcTimeline(gsmtc, isPlaying);

        string title = "Без названия";
        string artist = active.Name;
        byte[]? gsmtcCover = null;
        bool gsmtcUpdated = false;

        try
        {
            var props = gsmtcPropsMap.TryGetValue(_selectedId, out var cachedProps) && cachedProps != null
                ? cachedProps
                : await gsmtc.TryGetMediaPropertiesAsync();

            var (cleanTitle, cleanArtist) = NormalizeGsmtcTitleAndArtist(props?.Title, props?.Artist, active.Name);
            title = cleanTitle;
            artist = cleanArtist;

            var key = $"{_selectedId}|{active.Brand}|{title}|{artist}";
            if (key != _lastTrackKey)
            {
                _lastTrackKey = key;
                gsmtcUpdated = true;
                _lastCoverUrl = "";

                if (props?.Thumbnail != null)
                {
                    using var stream = await props.Thumbnail.OpenReadAsync();
                    using var netStream = stream.AsStreamForRead();
                    using var ms = new MemoryStream();
                    await netStream.CopyToAsync(ms, ct);
                    gsmtcCover = ms.ToArray();
                }

                if ((gsmtcCover == null || gsmtcCover.Length == 0) &&
                    !string.IsNullOrWhiteSpace(title) &&
                    title != "Без названия")
                {
                    var (fallbackBytes, fallbackUrl) = await TryResolveFallbackCoverAsync(active.Brand, artist, title, ct);
                    if (fallbackBytes != null && fallbackBytes.Length > 0)
                    {
                        gsmtcCover = fallbackBytes;
                        _lastCoverUrl = fallbackUrl;
                    }
                }
                else if (!string.IsNullOrWhiteSpace(title) && title != "Без названия")
                {
                    var cacheKey = $"{active.Brand}|{artist}|{title}";
                    if (_resolvedCoverUrlCache.TryGetValue(cacheKey, out var cachedUrl))
                        _lastCoverUrl = cachedUrl;
                    else
                        _ = Task.Run(() => WarmupCoverUrlAsync(active.Brand, artist, title));
                }
            }
        }
        catch { }

        return new MediaState
        {
            Sources = sources,
            ActiveId = _selectedId,
            ActiveBrand = active.Brand,
            Title = title,
            Artist = artist,
            IsPlaying = isPlaying,
            CoverUrl = _lastCoverUrl,
            CoverBytes = gsmtcCover,
            CoverUpdated = gsmtcUpdated,
            CurrentMs = currentMs,
            TotalMs = totalMs
        };
    }

    private bool IsSourceCurrentlyPlaying(
        string? sourceId,
        TrackItem? localTrack,
        Dictionary<string, GlobalSystemMediaTransportControlsSession> gsmtcMap)
    {
        if (string.IsNullOrEmpty(sourceId)) return false;
        if (sourceId == LocalSourceId) return localTrack != null && _localPlayer.IsActiveOrLoading;
        if (gsmtcMap.TryGetValue(sourceId, out var s))
            return IsGsmtcSessionPlaying(s);
        return false;
    }

    private (long CurrentMs, long TotalMs) ReadGsmtcTimeline(
        GlobalSystemMediaTransportControlsSession session,
        bool isPlaying)
    {
        try
        {
            var tl = session.GetTimelineProperties();
            var total = tl.EndTime - tl.StartTime;
            long totalMs = total.TotalMilliseconds > 0 ? (long)total.TotalMilliseconds : 0;
            if (totalMs <= 0) return (0, 0);

            long rawPosMs = Math.Clamp((long)(tl.Position - tl.StartTime).TotalMilliseconds, 0, totalMs);
            long curMs = rawPosMs;
            long nowTick = Environment.TickCount64;

            if (tl.LastUpdatedTime != default)
            {
                var elapsed = DateTimeOffset.UtcNow - tl.LastUpdatedTime;
                if (isPlaying && elapsed.TotalSeconds is > 0 and < 14400)
                    curMs = Math.Clamp(rawPosMs + (long)elapsed.TotalMilliseconds, 0, totalMs);
            }
            else
            {
                string key = $"{session.SourceAppUserModelId}|{totalMs}|{rawPosMs}";
                if (_gsmtcTimelineFallbackAnchors.Count >= 32 && !_gsmtcTimelineFallbackAnchors.ContainsKey(key))
                {
                    foreach (var oldKey in _gsmtcTimelineFallbackAnchors.Keys.Take(16))
                        _gsmtcTimelineFallbackAnchors.TryRemove(oldKey, out _);
                }
                var prev = _gsmtcTimelineFallbackAnchors.GetOrAdd(key, _ => (rawPosMs, nowTick, isPlaying));
                if (isPlaying)
                {
                    long anchorMs = prev.WasPlaying ? prev.AnchorMs : rawPosMs;
                    long elapsedMs = Math.Max(0, nowTick - prev.AnchorTick);
                    if (elapsedMs < 14_400_000)
                        curMs = Math.Clamp(anchorMs + elapsedMs, 0, totalMs);
                }
                else if (prev.WasPlaying)
                {
                    long frozen = Math.Clamp(prev.AnchorMs + Math.Max(0, nowTick - prev.AnchorTick), 0, totalMs);
                    _gsmtcTimelineFallbackAnchors[key] = (frozen, nowTick, false);
                    curMs = frozen;
                }
            }

            return (curMs, totalMs);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static (string Title, string Artist) NormalizeGsmtcTitleAndArtist(
        string? rawTitle,
        string? rawArtist,
        string fallbackSourceName)
    {
        string title = SiteSuffixRegex.Replace((rawTitle ?? "").Trim(), "").Trim();
        string artist = SiteSuffixRegex.Replace((rawArtist ?? "").Trim(), "").Trim();

        if (string.IsNullOrWhiteSpace(title))
            title = "Без названия";

        if (string.IsNullOrWhiteSpace(artist) ||
            string.Equals(artist, fallbackSourceName, StringComparison.OrdinalIgnoreCase) ||
            artist.Contains("vk.com", StringComparison.OrdinalIgnoreCase) ||
            artist.Contains("vk.ru", StringComparison.OrdinalIgnoreCase) ||
            artist.Contains("music.yandex", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var sep in new[] { " — ", " – ", " - " })
            {
                int idx = title.IndexOf(sep, StringComparison.Ordinal);
                if (idx > 1 && idx + sep.Length < title.Length)
                {
                    artist = title[..idx].Trim();
                    title = title[(idx + sep.Length)..].Trim();
                    break;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(artist))
            artist = fallbackSourceName;

        return (title, artist);
    }

    private async Task<(byte[]? Bytes, string Url)> TryResolveFallbackCoverAsync(
        string brand,
        string artist,
        string title,
        CancellationToken ct)
    {
        var (bytes, url, _) = await TryResolveFallbackMetaAsync(brand, artist, title, ct);
        return (bytes, url);
    }

    private async Task<(byte[]? Bytes, string Url, long DurationMs)> TryResolveFallbackMetaAsync(
        string brand,
        string artist,
        string title,
        CancellationToken ct)
    {
        var cacheKey = $"{brand}|{artist}|{title}";
        if (_resolvedCoverUrlCache.TryGetValue(cacheKey, out var cachedUrl) && !string.IsNullOrWhiteSpace(cachedUrl))
        {
            var bytes = await DownloadCoverAsync(cachedUrl);
            if (bytes != null) return (bytes, cachedUrl, 0);
        }

        var (url, durationMs) = await ResolveTrackMetaFromProvidersAsync(brand, artist, title, ct);
        if (!string.IsNullOrWhiteSpace(url))
        {
            CacheResolvedCoverUrl(cacheKey, url);
            var bytes = await DownloadCoverAsync(url);
            return (bytes, url, durationMs);
        }

        return (null, "", durationMs);
    }

    private async Task WarmupCoverUrlAsync(string brand, string artist, string title)
    {
        try
        {
            var cacheKey = $"{brand}|{artist}|{title}";
            if (_resolvedCoverUrlCache.ContainsKey(cacheKey)) return;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            var (url, _) = await ResolveTrackMetaFromProvidersAsync(brand, artist, title, cts.Token);
            if (!string.IsNullOrWhiteSpace(url))
                CacheResolvedCoverUrl(cacheKey, url);
        }
        catch { }
    }

    private void CacheResolvedCoverUrl(string cacheKey, string url)
    {
        if (_resolvedCoverUrlCache.Count >= 128 && !_resolvedCoverUrlCache.ContainsKey(cacheKey))
        {
            foreach (var oldKey in _resolvedCoverUrlCache.Keys.Take(64))
                _resolvedCoverUrlCache.TryRemove(oldKey, out _);
        }
        _resolvedCoverUrlCache[cacheKey] = url;
    }

    private async Task<(string CoverUrl, long DurationMs)> ResolveTrackMetaFromProvidersAsync(
        string brand,
        string artist,
        string title,
        CancellationToken ct)
    {
        if (_providers.Count == 0) return ("", 0);

        var query = TrackMatcher.BuildSearchQuery(artist, title);
        if (string.IsNullOrWhiteSpace(query)) return ("", 0);

        var ordered = _providers
            .OrderByDescending(p => string.Equals(p.BrandKey, brand, StringComparison.OrdinalIgnoreCase))
            .ThenBy(p => p.BrandKey switch
            {
                "yandex" => 0,
                "soundcloud" => 1,
                "youtube" => 2,
                "audius" => 3,
                "vk" => 4,
                _ => 9
            })
            .Take(3);

        string bestCover = "";
        long bestDurationMs = 0;

        foreach (var provider in ordered)
        {
            try
            {
                var results = await provider.SearchAsync(query, 3, ct);
                if (results.Count == 0) continue;

                var withCover = results.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.CoverUrl));
                if (withCover != null && string.IsNullOrWhiteSpace(bestCover))
                    bestCover = withCover.CoverUrl;

                var withDur = results.FirstOrDefault(r => r.Duration > TimeSpan.Zero);
                if (withDur != null && bestDurationMs <= 0)
                    bestDurationMs = (long)withDur.Duration.TotalMilliseconds;

                if (!string.IsNullOrWhiteSpace(bestCover))
                    return (bestCover, bestDurationMs);
            }
            catch { }
        }

        return (bestCover, bestDurationMs);
    }

    public async Task<byte[]?> DownloadCoverAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url) || !url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return null;
        if (_coverCache.TryGetValue(url, out var cached)) return cached;
        try
        {
            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, _cts.Token);
            if (!resp.IsSuccessStatusCode) return null;
            if (resp.Content.Headers.ContentLength is > 512 * 1024)
                return null;

            var data = await resp.Content.ReadAsByteArrayAsync(_cts.Token);
            if (data.Length == 0 || data.Length > 512 * 1024)
                return null;

            if (_coverCache.Count >= 40)
            {
                foreach (var oldKey in _coverCache.Keys.Take(20))
                    _coverCache.TryRemove(oldKey, out _);
            }
            _coverCache[url] = data;
            return data;
        }
        catch { return null; }
    }

    public void TrimCaches()
    {
        _coverCache.Clear();
        if (_resolvedCoverUrlCache.Count > 64)
        {
            foreach (var oldKey in _resolvedCoverUrlCache.Keys.Take(_resolvedCoverUrlCache.Count - 64))
                _resolvedCoverUrlCache.TryRemove(oldKey, out _);
        }
        _gsmtcTimelineFallbackAnchors.Clear();
    }

    private static bool IsBrowser(string appId)
    {
        var low = appId.ToLowerInvariant();
        if (low.Contains("yandexmusic") || low.Contains("yandex.music") || low.Contains("desktop.music"))
            return false;
        return low.Contains("chrome") || low.Contains("msedge") ||
               low.Contains("firefox") || low.Contains("opera") ||
               low.Contains("brave") || low.Contains("vivaldi") ||
               low.Contains("chromium") || low.Contains("waterfox") ||
               low.Contains("yabrowser") || low.Contains("browser") ||
               low.Contains("yandex");
    }

    private static (string Name, string Brand) DetectSourceAndBrand(
        string appId,
        GlobalSystemMediaTransportControlsSessionMediaProperties? props)
    {
        var low = appId.ToLowerInvariant();

        if (low.Contains("yandexmusic") || low.Contains("yandex.music") || low.Contains("desktop.music"))
            return ("Яндекс Музыка", "yandex");
        if (low.Contains("vk"))
            return ("ВК Музыка", "vk");
        if (low.Contains("spotify"))
            return ("Spotify", "spotify");
        if (low.Contains("telegram"))
            return ("Telegram", "default");

        string title = (props?.Title ?? "").Trim();
        string artist = (props?.Artist ?? "").Trim();
        string album = (props?.AlbumTitle ?? "").Trim();
        string subtitle = (props?.Subtitle ?? "").Trim();
        string combinedMeta = $"{title} | {artist} | {album} | {subtitle}".ToLowerInvariant();

        if (combinedMeta.Contains("soundcloud"))
            return ("SoundCloud", "soundcloud");
        if (combinedMeta.Contains("youtube"))
            return ("YouTube", "youtube");
        if (combinedMeta.Contains("spotify"))
            return ("Spotify", "spotify");
        if (combinedMeta.Contains("audius"))
            return ("Audius", "audius");
        if (combinedMeta.Contains("bandcamp"))
            return ("Bandcamp", "bandcamp");

        var clean = appId.Split('!').Last().Replace(".exe", "", StringComparison.OrdinalIgnoreCase);
        return (clean.Length > 12 ? clean[..12] : clean, "default");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_manager != null && _sessionsChangedHandler != null)
        {
            try { _manager.SessionsChanged -= _sessionsChangedHandler; } catch { }
            _sessionsChangedHandler = null;
        }
        _cts.Cancel();
        try { _pollWakeup.Release(); } catch { }
        _coverCache.Clear();
        _resolvedCoverUrlCache.Clear();
        _gsmtcTimelineFallbackAnchors.Clear();
        _http.Dispose();
    }
}
