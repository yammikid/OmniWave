using System.Collections.Concurrent;
using System.IO;
using LibVLCSharp.Shared;

namespace DynamicIsland.Services;

public enum RepeatMode
{
    Off,
    All,
    One
}

public class AudioPlayerService : IDisposable
{
    public static readonly string[] EqBandLabels =
    {
        "60", "170", "310", "600", "1k", "3k", "6k", "12k", "14k", "16k"
    };

    public static readonly IReadOnlyDictionary<string, (float Preamp, float[] Bands)> EqPresets =
        new Dictionary<string, (float Preamp, float[] Bands)>
        {
            ["Flat"] = (0f, new float[] { 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }),
            ["Bass Boost"] = (-1.5f, new float[] { 6.5f, 5.5f, 4.0f, 2.0f, 0.5f, 0, 0.5f, 1.0f, 1.0f, 1.0f }),
            ["Rock"] = (-1.0f, new float[] { 4.5f, 3.0f, -1.5f, -2.5f, -0.5f, 2.0f, 4.0f, 5.0f, 5.0f, 4.5f }),
            ["Electronic"] = (-1.5f, new float[] { 5.0f, 4.0f, 1.5f, 0, -1.5f, 1.5f, 3.0f, 4.5f, 5.0f, 5.5f }),
            ["Vocal"] = (-0.5f, new float[] { -2.5f, -2.0f, -0.5f, 2.5f, 4.5f, 4.5f, 3.0f, 1.0f, 0, -1.0f }),
            ["Club"] = (-1.0f, new float[] { 3.5f, 3.5f, 2.0f, 1.0f, 1.5f, 2.5f, 3.0f, 2.5f, 2.0f, 1.5f }),
            ["Acoustic"] = (-0.5f, new float[] { 3.0f, 2.5f, 1.5f, 0.5f, 1.5f, 2.0f, 3.0f, 3.5f, 3.0f, 2.0f }),
            ["Late Night"] = (1.5f, new float[] { -2.5f, -1.0f, 1.5f, 2.5f, 3.0f, 2.5f, 1.5f, 0, -1.5f, -2.5f })
        };

    private static readonly TimeSpan StreamCacheTtl = TimeSpan.FromMinutes(25);

    private readonly LibVLC _libVlc;
    private readonly MediaPlayer _player;
    private readonly Dictionary<string, IMusicProvider> _providers;
    private readonly DownloadService _downloads;
    private readonly ConcurrentDictionary<string, (string Url, DateTime ExpiresUtc)> _streamUrlCache = new();
    private readonly object _queueLock = new();
    private readonly object _fxLock = new();

    private Equalizer? _equalizer;
    private Media? _currentMedia;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _preResolveCts;
    private Func<string?, Task<List<TrackItem>>>? _waveReplenishProvider;
    private Func<Task<List<TrackItem>>>? _vkMixReplenishProvider;
    private int _waveReplenishing;
    private bool _isLoadingTrack;
    private long _pendingSeekMs = -1;
    private readonly Dictionary<string, (List<TrackItem> Queue, int Index, long PositionMs, bool IsWave)> _savedBrandQueues = new(StringComparer.OrdinalIgnoreCase);
    private List<TrackItem> _queue = new();
    private int _currentIndex = -1;
    private int _softwareVolume = 80;
    private long _lastTimeMs;
    private long _cachedLengthMs;
    private RepeatMode _repeatMode = RepeatMode.Off;

    public TrackItem? CurrentTrack { get; private set; }
    public bool IsYandexWaveActive { get; private set; }
    public bool IsVkMixActive { get; private set; }
    public bool IsPlaying => _player.IsPlaying;
    public bool IsActiveOrLoading => _player.IsPlaying || _isLoadingTrack;
    public int Volume => _softwareVolume;
    public long CurrentTimeMs => _lastTimeMs;
    public long TotalLengthMs => _cachedLengthMs;
    public float PlaybackRate { get; private set; } = 1.0f;
    public bool EqualizerEnabled { get; private set; }
    public float EqualizerPreamp { get; private set; }
    public float[] EqualizerBands { get; private set; } = new float[10];

    public RepeatMode RepeatMode
    {
        get => _repeatMode;
        set
        {
            if (_repeatMode == value) return;
            _repeatMode = value;
            RepeatModeChanged?.Invoke(_repeatMode);
        }
    }

    public int CurrentIndex
    {
        get { lock (_queueLock) return _currentIndex; }
    }

    public event Action<long, long>? PositionChanged;      // (currentMs, totalMs)
    public event Action<bool>? PlaybackStateChanged;
    public event Action<TrackItem>? TrackChanged;
    public event Action<string>? StatusMessage;
    public event Action? QueueChanged;
    public event Action<RepeatMode>? RepeatModeChanged;
    public event Action<int>? VolumeChanged;

    public AudioPlayerService(IEnumerable<IMusicProvider> providers, DownloadService downloads)
    {
        _providers = providers.ToDictionary(p => p.BrandKey, StringComparer.OrdinalIgnoreCase);
        _downloads = downloads;

        var libvlcDir = Path.Combine(AppContext.BaseDirectory, "libvlc", "win-x64");
        if (Directory.Exists(libvlcDir))
            Core.Initialize(libvlcDir);
        else
            Core.Initialize();

        _libVlc = new LibVLC("--no-video", "--quiet", "--aout=directsound");
        _player = new MediaPlayer(_libVlc) { Volume = _softwareVolume };
        ApplyAudioFxToPlayer();
        ProxyService.ProxyChanged += () => _streamUrlCache.Clear();

        // Не вызываем методы/свойства MediaPlayer синхронно внутри нативных коллбэков LibVLC
        _player.LengthChanged += (_, e) =>
        {
            if (e.Length > 0)
                _cachedLengthMs = e.Length;
        };
        _player.TimeChanged += (_, e) =>
        {
            _lastTimeMs = e.Time;
            PositionChanged?.Invoke(e.Time, _cachedLengthMs);
        };
        _player.Playing += (_, _) =>
        {
            long pending = Interlocked.Exchange(ref _pendingSeekMs, -1);
            if (pending >= 0)
            {
                _lastTimeMs = pending;
                _ = Task.Run(() =>
                {
                    try
                    {
                        if (Math.Abs(_player.Time - pending) > 1500)
                            _player.Time = pending;
                    }
                    catch { }
                });
            }
            PlaybackStateChanged?.Invoke(true);
            _ = Task.Run(ApplyAudioFxToPlayer);
        };
        _player.Paused += (_, _) =>
        {
            SaveCurrentBrandState();
            PlaybackStateChanged?.Invoke(false);
        };
        _player.Stopped += (_, _) => PlaybackStateChanged?.Invoke(false);
        _player.EndReached += (_, _) =>
        {
            _ = Task.Run(HandleEndReachedAsync);
        };
    }

    public (List<TrackItem> Items, int CurrentIndex) GetQueueSnapshot()
    {
        lock (_queueLock)
        {
            var list = new List<TrackItem>(_queue.Count);
            for (int i = 0; i < _queue.Count; i++)
            {
                var item = _queue[i];
                item.IsCurrentlyPlaying = i == _currentIndex;
                list.Add(item);
            }
            return (list, _currentIndex);
        }
    }

    public void SetWaveReplenishProvider(Func<string?, Task<List<TrackItem>>>? provider)
        => _waveReplenishProvider = provider;

    public void SetVkMixReplenishProvider(Func<Task<List<TrackItem>>>? provider)
        => _vkMixReplenishProvider = provider;

    public async Task<bool> StartYandexWaveAsync()
    {
        if (_waveReplenishProvider == null) return false;
        StatusMessage?.Invoke("Запуск «Моей волны» Яндекса...");
        try
        {
            var tracks = await _waveReplenishProvider(null);
            if (tracks == null || tracks.Count == 0)
            {
                StatusMessage?.Invoke("Не удалось загрузить треки «Моей волны»");
                return false;
            }
            await PlayFromQueueAsync(tracks, 0, isWave: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> StartVkMixAsync()
    {
        if (_vkMixReplenishProvider == null) return false;
        StatusMessage?.Invoke("Запуск «VK Микс»...");
        try
        {
            var tracks = await _vkMixReplenishProvider();
            if (tracks == null || tracks.Count == 0)
            {
                StatusMessage?.Invoke("Не удалось загрузить треки «VK Микс»");
                return false;
            }
            await PlayFromQueueAsync(tracks, 0, isWave: false, startPositionMs: 0, isVkMix: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task EnsureWaveReplenishedIfNeededAsync()
    {
        bool wantWave = IsYandexWaveActive && _waveReplenishProvider != null;
        bool wantVkMix = IsVkMixActive && _vkMixReplenishProvider != null;
        if (!wantWave && !wantVkMix) return;

        int remaining;
        string? lastId;
        HashSet<string> existingIds;
        HashSet<string> existingKeys;
        lock (_queueLock)
        {
            if (_queue.Count == 0) return;
            remaining = (_queue.Count - 1) - Math.Max(0, _currentIndex);
            var tail = _queue[^1];
            lastId = !string.IsNullOrWhiteSpace(tail.PageUrl) ? tail.PageUrl : tail.Id;
            existingIds = _queue.Select(t => t.Id).Where(id => !string.IsNullOrWhiteSpace(id)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            existingKeys = _queue.Select(t => t.UniqueKey).Where(k => !string.IsNullOrWhiteSpace(k)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        if (remaining > 10) return;
        if (Interlocked.CompareExchange(ref _waveReplenishing, 1, 0) != 0) return;

        try
        {
            var batch = wantWave
                ? await _waveReplenishProvider!(lastId)
                : await _vkMixReplenishProvider!();
            if (batch != null && batch.Count > 0)
            {
                var fresh = batch.Where(t =>
                    !existingKeys.Contains(t.UniqueKey) &&
                    (string.IsNullOrWhiteSpace(t.Id) || !existingIds.Contains(t.Id))).ToList();
                if (fresh.Count > 0)
                    AppendTracksToQueue(fresh);
            }
        }
        catch { }
        finally
        {
            Interlocked.Exchange(ref _waveReplenishing, 0);
        }
    }

    private void SaveCurrentBrandState()
    {
        lock (_queueLock)
        {
            if (_currentIndex < 0 || _currentIndex >= _queue.Count) return;
            var cur = _queue[_currentIndex];
            if (string.IsNullOrWhiteSpace(cur.BrandKey)) return;
            _savedBrandQueues[cur.BrandKey] = (
                _queue.Select(t => t.Clone()).ToList(),
                _currentIndex,
                _lastTimeMs,
                IsYandexWaveActive);
        }
    }

    public bool TryGetSavedBrandTrack(string brandKey, out TrackItem? track, out long posMs)
    {
        lock (_queueLock)
        {
            if (_savedBrandQueues.TryGetValue(brandKey, out var saved) &&
                saved.Index >= 0 && saved.Index < saved.Queue.Count)
            {
                track = saved.Queue[saved.Index];
                posMs = saved.PositionMs;
                return true;
            }
        }
        track = null;
        posMs = 0;
        return false;
    }

    public async Task<bool> ResumeSavedBrandAsync(string brandKey, long? overrideSeekMs = null, int indexDelta = 0)
    {
        List<TrackItem> queue;
        int idx;
        long pos;
        bool isWave;
        lock (_queueLock)
        {
            if (!_savedBrandQueues.TryGetValue(brandKey, out var saved) || saved.Queue.Count == 0)
                return false;
            queue = saved.Queue.Select(t => t.Clone()).ToList();
            idx = saved.Index;
            pos = saved.PositionMs;
            isWave = saved.IsWave;
        }

        if (indexDelta != 0)
        {
            idx = (idx + indexDelta % queue.Count + queue.Count) % queue.Count;
            pos = 0;
        }
        else if (overrideSeekMs.HasValue)
        {
            pos = Math.Max(0, overrideSeekMs.Value);
        }

        await PlayFromQueueAsync(queue, idx, isWave: isWave, startPositionMs: pos);
        return true;
    }

    public async Task PlayFromQueueAsync(List<TrackItem> queue, int index, bool isWave = false, long startPositionMs = 0, bool isVkMix = false)
    {
        SaveCurrentBrandState();
        IsYandexWaveActive = isWave;
        IsVkMixActive = isVkMix;
        TrackItem? target = null;
        lock (_queueLock)
        {
            _queue = queue.Select(t => t.Clone()).ToList();
            _currentIndex = (index >= 0 && index < _queue.Count) ? index : -1;
            UpdatePlayingFlagsUnlocked();
            if (_currentIndex >= 0)
                target = _queue[_currentIndex];
        }

        QueueChanged?.Invoke();
        if (target != null)
            await LoadAndPlayAsync(target, startPositionMs);
    }

    public async Task PlayQueueIndexAsync(int index)
    {
        TrackItem? target = null;
        lock (_queueLock)
        {
            if (index < 0 || index >= _queue.Count) return;
            _currentIndex = index;
            UpdatePlayingFlagsUnlocked();
            target = _queue[_currentIndex];
        }

        QueueChanged?.Invoke();
        _ = EnsureWaveReplenishedIfNeededAsync();
        if (target != null)
            await LoadAndPlayAsync(target);
    }

    public void AddToQueue(TrackItem track, bool playNext = false)
    {
        var copy = track.Clone();
        lock (_queueLock)
        {
            if (playNext)
            {
                int insertAt = (_currentIndex >= 0 && _currentIndex < _queue.Count) ? _currentIndex + 1 : 0;
                _queue.Insert(insertAt, copy);
            }
            else
            {
                _queue.Add(copy);
            }
            UpdatePlayingFlagsUnlocked();
        }

        QueueChanged?.Invoke();
        StatusMessage?.Invoke(playNext
            ? $"Включится следующим: {track.Title}"
            : $"Добавлено в очередь: {track.Title}");
    }

    public void AppendTracksToQueue(IEnumerable<TrackItem> tracks)
    {
        int count = 0;
        lock (_queueLock)
        {
            foreach (var t in tracks)
            {
                _queue.Add(t.Clone());
                count++;
            }
            UpdatePlayingFlagsUnlocked();
        }

        if (count > 0)
            QueueChanged?.Invoke();
    }

    public void MoveInQueue(int fromIndex, int toIndex)
    {
        bool changed = false;
        lock (_queueLock)
        {
            if (fromIndex < 0 || fromIndex >= _queue.Count ||
                toIndex < 0 || toIndex >= _queue.Count ||
                fromIndex == toIndex)
                return;

            var item = _queue[fromIndex];
            _queue.RemoveAt(fromIndex);
            _queue.Insert(toIndex, item);

            if (_currentIndex == fromIndex)
                _currentIndex = toIndex;
            else if (fromIndex < _currentIndex && toIndex >= _currentIndex)
                _currentIndex--;
            else if (fromIndex > _currentIndex && toIndex <= _currentIndex)
                _currentIndex++;

            UpdatePlayingFlagsUnlocked();
            changed = true;
        }

        if (changed)
            QueueChanged?.Invoke();
    }

    public void RemoveFromQueue(int index)
    {
        bool changed = false;
        lock (_queueLock)
        {
            if (index < 0 || index >= _queue.Count) return;

            _queue.RemoveAt(index);
            if (_queue.Count == 0)
            {
                _currentIndex = -1;
            }
            else if (index < _currentIndex)
            {
                _currentIndex--;
            }
            else if (index == _currentIndex)
            {
                if (_currentIndex >= _queue.Count)
                    _currentIndex = _queue.Count - 1;
            }

            UpdatePlayingFlagsUnlocked();
            changed = true;
        }

        if (changed)
            QueueChanged?.Invoke();
    }

    public void ShuffleQueue()
    {
        lock (_queueLock)
        {
            if (_queue.Count <= 1) return;

            TrackItem? current = (_currentIndex >= 0 && _currentIndex < _queue.Count)
                ? _queue[_currentIndex]
                : null;

            if (current != null)
            {
                _queue.RemoveAt(_currentIndex);
                FisherYatesShuffle(_queue);
                _queue.Insert(0, current);
                _currentIndex = 0;
            }
            else
            {
                FisherYatesShuffle(_queue);
            }

            UpdatePlayingFlagsUnlocked();
        }

        QueueChanged?.Invoke();
        StatusMessage?.Invoke("Очередь перемешана");
    }

    public void ClearQueue()
    {
        lock (_queueLock)
        {
            if (_queue.Count == 0) return;
            _queue.Clear();
            _currentIndex = -1;
        }

        QueueChanged?.Invoke();
        StatusMessage?.Invoke("Очередь очищена");
    }

    public RepeatMode CycleRepeatMode()
    {
        RepeatMode = RepeatMode switch
        {
            RepeatMode.Off => RepeatMode.All,
            RepeatMode.All => RepeatMode.One,
            _ => RepeatMode.Off
        };
        return RepeatMode;
    }

    private async Task HandleEndReachedAsync()
    {
        await EnsureWaveReplenishedIfNeededAsync();
        TrackItem? target = null;

        lock (_queueLock)
        {
            if (_queue.Count == 0) return;

            if (RepeatMode == RepeatMode.One)
            {
                if (_currentIndex < 0 || _currentIndex >= _queue.Count) _currentIndex = 0;
                target = _queue[_currentIndex];
            }
            else if (RepeatMode == RepeatMode.All)
            {
                _currentIndex = (_currentIndex + 1) % _queue.Count;
                target = _queue[_currentIndex];
            }
            else
            {
                if (_currentIndex + 1 < _queue.Count)
                {
                    _currentIndex++;
                    target = _queue[_currentIndex];
                }
            }

            UpdatePlayingFlagsUnlocked();
        }

        QueueChanged?.Invoke();

        if (target != null)
            await LoadAndPlayAsync(target);
        else
            PlaybackStateChanged?.Invoke(false);
    }

    public async Task NextAsync()
    {
        await EnsureWaveReplenishedIfNeededAsync();
        TrackItem? nextTrack = null;
        lock (_queueLock)
        {
            if (_queue.Count == 0) return;
            _currentIndex = (_currentIndex + 1) % _queue.Count;
            UpdatePlayingFlagsUnlocked();
            nextTrack = _queue[_currentIndex];
        }

        QueueChanged?.Invoke();
        await LoadAndPlayAsync(nextTrack);
    }

    public async Task PrevAsync()
    {
        if (CurrentTrack != null && (_lastTimeMs > 4000 || _player.Time > 4000))
        {
            _lastTimeMs = 0;
            _player.Time = 0;
            PositionChanged?.Invoke(0, _cachedLengthMs);
            return;
        }

        TrackItem? prevTrack = null;
        lock (_queueLock)
        {
            if (_queue.Count == 0) return;
            _currentIndex = (_currentIndex - 1 + _queue.Count) % _queue.Count;
            UpdatePlayingFlagsUnlocked();
            prevTrack = _queue[_currentIndex];
        }

        QueueChanged?.Invoke();
        await LoadAndPlayAsync(prevTrack);
    }

    private async Task LoadAndPlayAsync(TrackItem track, long startPositionMs = 0)
    {
        SaveCurrentBrandState();
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        _isLoadingTrack = true;
        _pendingSeekMs = startPositionMs > 0 ? startPositionMs : -1;
        _lastTimeMs = Math.Max(0, startPositionMs);
        _cachedLengthMs = (long)track.Duration.TotalMilliseconds;
        CurrentTrack = track;
        SaveCurrentBrandState();
        TrackChanged?.Invoke(track);

        try
        {
            // 1. Проверяем локальный скачанный файл в первую очередь для мгновенного офлайн-воспроизведения
            var localPath = _downloads.TryGetLocalFile(track);
            if (!string.IsNullOrWhiteSpace(localPath) && File.Exists(localPath))
            {
                _currentMedia?.Dispose();
                _currentMedia = new Media(_libVlc, localPath, FromType.FromPath);
                _player.Play(_currentMedia);
                ApplyAudioFxToPlayer();
                StatusMessage?.Invoke("Сохранено локально • Воспроизведение");
                _ = Task.Run(PreResolveNextAsync);
                return;
            }

            StatusMessage?.Invoke($"Загрузка потока ({track.Source})...");

            var streamUrl = await ResolveStreamUrlCachedAsync(track, ct);
            if (ct.IsCancellationRequested) return;

            if (string.IsNullOrWhiteSpace(streamUrl))
            {
                if (string.Equals(track.BrandKey, "youtube", StringComparison.OrdinalIgnoreCase) &&
                    _providers.TryGetValue("youtube", out var ytProv) &&
                    ytProv is YouTubeFastProvider ytFast &&
                    !string.IsNullOrWhiteSpace(ytFast.LastResolveError))
                {
                    StatusMessage?.Invoke(ytFast.LastResolveError);
                }
                else if (string.Equals(track.BrandKey, "spotify", StringComparison.OrdinalIgnoreCase) &&
                         _providers.TryGetValue("spotify", out var spProv) &&
                         spProv is SpotifyMusicProvider sp &&
                         !string.IsNullOrWhiteSpace(sp.LastResolveError))
                {
                    StatusMessage?.Invoke(sp.LastResolveError);
                }
                else
                {
                    StatusMessage?.Invoke($"Не удалось получить поток с {track.Source}");
                }
                return;
            }

            // Обновляем отображение (на случай вставки прямой ссылки и подтягивания метаданных)
            TrackChanged?.Invoke(track);

            _currentMedia?.Dispose();
            bool isLocalResolvedFile = File.Exists(streamUrl);
            bool isVkAudioHost = !isLocalResolvedFile &&
                string.Equals(track.BrandKey, "vk", StringComparison.OrdinalIgnoreCase) &&
                (streamUrl.Contains("vkuseraudio", StringComparison.OrdinalIgnoreCase) ||
                 streamUrl.Contains("userapi.com", StringComparison.OrdinalIgnoreCase) ||
                 streamUrl.Contains("vk.com", StringComparison.OrdinalIgnoreCase) ||
                 streamUrl.Contains("vk.ru", StringComparison.OrdinalIgnoreCase));

            bool needsRelay = !isLocalResolvedFile &&
                (ProxyService.ShouldProxyUrl(streamUrl) || ProxyService.HasRegisteredStreamHeaders(streamUrl));

            if (needsRelay)
            {
                ProxyService.TryGetStreamHeaders(streamUrl, out var regUa, out var regReferer);
                if (isVkAudioHost)
                {
                    regUa ??= VkMusicProvider.KateMobileUserAgent;
                    regReferer ??= "https://vk.com/";
                }

                var relayUrl = ProxyService.CreateLocalMediaRelayUrl(streamUrl, regUa, regReferer);
                _currentMedia = new Media(_libVlc, relayUrl, FromType.FromLocation);
            }
            else
            {
                _currentMedia = isLocalResolvedFile
                    ? new Media(_libVlc, streamUrl, FromType.FromPath)
                    : new Media(_libVlc, streamUrl, FromType.FromLocation);

                if (isVkAudioHost)
                {
                    _currentMedia.AddOption($":http-user-agent={VkMusicProvider.KateMobileUserAgent}");
                    _currentMedia.AddOption(":http-referrer=https://vk.com/");
                }
            }
            _player.Play(_currentMedia);
            ApplyAudioFxToPlayer();
            StatusMessage?.Invoke($"{track.Source} • Воспроизведение");

            // Фоновая предзагрузка следующего трека очереди
            _ = Task.Run(PreResolveNextAsync);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            StatusMessage?.Invoke(ex.Message);
        }
        finally
        {
            _isLoadingTrack = false;
        }
    }

    private async Task<string?> ResolveStreamUrlCachedAsync(TrackItem track, CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        if (_streamUrlCache.TryGetValue(track.UniqueKey, out var entry) &&
            entry.ExpiresUtc > now &&
            !string.IsNullOrWhiteSpace(entry.Url))
        {
            return entry.Url;
        }

        string? url = null;
        if (!track.PageUrl.StartsWith("external:", StringComparison.OrdinalIgnoreCase) &&
            _providers.TryGetValue(track.BrandKey, out var provider))
        {
            url = await provider.ResolveStreamUrlAsync(track, ct);

            // Если YouTube через IP прокси заблокировал прямой поток (LOGIN_REQUIRED / 403),
            // аккуратно ищем строгий верифицированный аналог (±3 с и совпадение артистов) на других источниках
            if (string.IsNullOrWhiteSpace(url) &&
                string.Equals(track.BrandKey, "youtube", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(track.Title) &&
                track.Duration > TimeSpan.Zero)
            {
                var query = TrackMatcher.BuildSearchQuery(track.Artist, track.Title);
                if (!string.IsNullOrWhiteSpace(query))
                {
                    foreach (var fallbackKey in new[] { "yandex", "vk", "soundcloud", "audius" })
                    {
                        if (ct.IsCancellationRequested) break;
                        if (!_providers.TryGetValue(fallbackKey, out var fb)) continue;
                        try
                        {
                            var candidates = await fb.SearchAsync(query, 6, ct);
                            var best = TrackMatcher.PickBestMatch(track, candidates);
                            if (best == null) continue;

                            var fbUrl = await fb.ResolveStreamUrlAsync(best, ct);
                            if (!string.IsNullOrWhiteSpace(fbUrl))
                            {
                                StatusMessage?.Invoke($"YouTube ограничил IP прокси • Запущено через {fb.Name}");
                                url = fbUrl;
                                break;
                            }
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                        catch { }
                    }
                }
            }
        }
        else
        {
            // Фолбэк для треков, перехваченных или сохранённых из внешнего/браузерного плеера (GSMTC)
            var query = TrackMatcher.BuildSearchQuery(track.Artist, track.Title);
            var order = new List<string>();
            if (!string.IsNullOrWhiteSpace(track.BrandKey) && _providers.ContainsKey(track.BrandKey))
                order.Add(track.BrandKey);
            foreach (var k in new[] { "yandex", "soundcloud", "youtube", "audius", "vk" })
            {
                if (!order.Contains(k, StringComparer.OrdinalIgnoreCase))
                    order.Add(k);
            }

            foreach (var fallbackKey in order)
            {
                if (!_providers.TryGetValue(fallbackKey, out var fb)) continue;
                var candidates = await fb.SearchAsync(query, 8, ct);
                if (candidates.Count > 0)
                {
                    var best = TrackMatcher.PickBestMatch(track, candidates);
                    if (best == null) continue;
                    if (string.IsNullOrWhiteSpace(track.CoverUrl) && !string.IsNullOrWhiteSpace(best.CoverUrl))
                        track.CoverUrl = best.CoverUrl;
                    url = await fb.ResolveStreamUrlAsync(best, ct);
                    if (!string.IsNullOrWhiteSpace(url)) break;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(url))
            _streamUrlCache[track.UniqueKey] = (url, now.Add(StreamCacheTtl));

        return url;
    }

    private async Task PreResolveNextAsync()
    {
        _ = EnsureWaveReplenishedIfNeededAsync();
        _preResolveCts?.Cancel();
        var cts = new CancellationTokenSource();
        _preResolveCts = cts;
        var ct = cts.Token;

        TrackItem? nextTrack = null;
        lock (_queueLock)
        {
            if (_queue.Count == 0 || _currentIndex < 0) return;

            int nextIdx = RepeatMode switch
            {
                RepeatMode.One => _currentIndex,
                RepeatMode.All => (_currentIndex + 1) % _queue.Count,
                _ => _currentIndex + 1 < _queue.Count ? _currentIndex + 1 : -1
            };

            if (nextIdx >= 0 && nextIdx < _queue.Count)
                nextTrack = _queue[nextIdx];
        }

        if (nextTrack == null) return;
        if (_downloads.TryGetLocalFile(nextTrack) != null) return;

        if (_streamUrlCache.TryGetValue(nextTrack.UniqueKey, out var cached) &&
            cached.ExpiresUtc > DateTime.UtcNow)
            return;

        try
        {
            await Task.Delay(350, ct);
            await ResolveStreamUrlCachedAsync(nextTrack, ct);
        }
        catch { }
    }

    public void TogglePause()
    {
        if (CurrentTrack == null) return;
        if (_player.IsPlaying) _player.Pause();
        else _player.Play();
    }

    public void Pause()
    {
        if (_player.IsPlaying)
            _player.Pause();
    }

    public void Play()
    {
        if (CurrentTrack != null && !_player.IsPlaying)
            _player.Play();
    }

    private void SeekInternalMs(long targetMs)
    {
        long len = _player.Length > 0 ? _player.Length : _cachedLengthMs;
        if (len <= 0) return;

        long clamped = Math.Clamp(targetMs, 0, len);
        _lastTimeMs = clamped;

        bool appliedNow = false;
        if (_player.Length > 0 && _player.IsSeekable)
        {
            try
            {
                _player.Time = clamped;
                appliedNow = true;
            }
            catch { }
        }

        if (!appliedNow && _player.IsSeekable)
        {
            try
            {
                _player.Position = Math.Clamp((float)((double)clamped / len), 0f, 1f);
                appliedNow = true;
            }
            catch { }
        }

        if (!appliedNow && _player.Length > 0)
        {
            try
            {
                _player.Time = clamped;
                appliedNow = true;
            }
            catch { }
        }

        // Оставляем отложенный seek только если поток ещё не начал играть или на паузе;
        // при активном воспроизведении очищаем _pendingSeekMs, чтобы Pause -> Play не откатил позицию назад.
        _pendingSeekMs = (!appliedNow || !_player.IsPlaying) ? clamped : -1;

        PositionChanged?.Invoke(clamped, len);
    }

    public void SeekFraction(double fraction)
    {
        fraction = Math.Clamp(fraction, 0.0, 1.0);
        long len = _player.Length > 0 ? _player.Length : _cachedLengthMs;
        if (len > 0)
            SeekInternalMs((long)(len * fraction));
    }

    public void SeekToMs(long ms) => SeekInternalMs(ms);

    public void SeekRelativeMs(long deltaMs)
    {
        long len = _player.Length > 0 ? _player.Length : _cachedLengthMs;
        if (len <= 0) return;

        long playerTime = _player.Time;
        long baseMs = _player.IsPlaying && playerTime >= 0
            ? playerTime
            : (_lastTimeMs >= 0 ? _lastTimeMs : Math.Max(0, playerTime));
        SeekInternalMs(baseMs + deltaMs);
    }

    public void SetVolume(int volume0To100)
    {
        int clamped = Math.Clamp(volume0To100, 0, 100);
        _softwareVolume = clamped;
        ApplyAudioFxToPlayer();
        VolumeChanged?.Invoke(clamped);
    }

    public void ApplyEqualizer(bool enabled, float preamp, float[] bands)
    {
        EqualizerEnabled = enabled;
        EqualizerPreamp = Math.Clamp(preamp, -12f, 12f);

        var copy = new float[10];
        if (bands != null)
        {
            for (int i = 0; i < Math.Min(10, bands.Length); i++)
                copy[i] = Math.Clamp(bands[i], -12f, 12f);
        }
        EqualizerBands = copy;

        ApplyAudioFxToPlayer();
    }

    public void SetRate(float rate)
    {
        PlaybackRate = Math.Clamp(rate, 0.75f, 1.5f);
        try { _player.SetRate(PlaybackRate); } catch { }
    }

    private void ApplyAudioFxToPlayer()
    {
        lock (_fxLock)
        {
            try
            {
                bool muted = _softwareVolume <= 0;
                _player.Mute = muted;
                _player.Volume = muted ? 0 : _softwareVolume;

                if (EqualizerEnabled)
                {
                    _equalizer ??= new Equalizer();
                    _equalizer.SetPreamp(Math.Clamp(EqualizerPreamp, -20f, 20f));
                    for (uint i = 0; i < 10; i++)
                    {
                        _equalizer.SetAmp(Math.Clamp(EqualizerBands[i], -20f, 20f), i);
                    }
                    _player.SetEqualizer(_equalizer);
                }
                else
                {
                    _player.UnsetEqualizer();
                }

                _player.SetRate(PlaybackRate);
            }
            catch { }
        }
    }

    private void UpdatePlayingFlagsUnlocked()
    {
        for (int i = 0; i < _queue.Count; i++)
            _queue[i].IsCurrentlyPlaying = i == _currentIndex;
    }

    private static void FisherYatesShuffle(List<TrackItem> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = Random.Shared.Next(i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    public void Dispose()
    {
        _loadCts?.Cancel();
        _preResolveCts?.Cancel();
        _streamUrlCache.Clear();
        try { _player.Stop(); } catch { }
        _currentMedia?.Dispose();
        _equalizer?.Dispose();
        _player.Dispose();
        _libVlc.Dispose();
    }
}
