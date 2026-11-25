using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace DynamicIsland.Services;

/// <summary>
/// Интеграция Яндекс Музыки на базе механики BuevichRoman/yandex2spotify:
/// - официальный OAuth-клиент Яндекс Музыки (access_token y0_...),
/// - экпоненциальные ретраи (1, 2, 4, 8, 16 с),
/// - загрузка «Мне нравится» в исходном порядке и всех плейлистов с обложками,
/// - 2-этапное воспроизведение: прямой поток Яндекса + верифицированный подбор (TrackMatcher ±3 с).
/// </summary>
public sealed class YandexMusicProvider : IMusicProvider, IDisposable
{
    public const string BrandColorHex = "#FFCC00";
    public const string OAuthAuthorizeUrl =
        "https://oauth.yandex.ru/authorize?response_type=token&client_id=23cabbbdc6cd418abb4b39c32c41195d";

    private const string ApiBase = "https://api.music.yandex.net";
    private const string ClientHeader = "YandexMusicAndroid/24023621";
    private const string SignSalt = "XGRlBW9FXlekgbPrRHuSiA";
    private const int MaxRetries = 5;

    private static readonly Regex TokenInUrlRegex = new(
        @"access_token=([^&\s""#]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex TrackIdInUrlRegex = new(
        @"music\.yandex\.[^/]+/(?:album/\d+/)?track/(\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex AlbumAndTrackIdInUrlRegex = new(
        @"music\.yandex\.[^/]+/album/(\d+)/track/(\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex VideoNoiseTagRegex = new(
        @"\s*[\(\[](?:official\s*(?:music\s*)?(?:video|audio|clip|lyric\s*video|visualizer)|lyrics?|audio|hd|hq|4k|prod\.?[^\)\]]+|mv|клип|live)[\)\]]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public sealed record YandexTransferResult(
        bool Ok,
        int AddedCount,
        int AlreadyLikedCount,
        int NotFoundCount,
        string Message);

    private readonly SettingsService _settings;
    private readonly List<IMusicProvider> _fallbackProviders;
    private readonly HttpClient _http;
    private readonly string _waveHistoryPath;
    private readonly object _waveLock = new();
    private readonly List<string> _waveSeenOrder = new();
    private readonly HashSet<string> _waveSeenIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _cachedUserLikedSeedIds = new();
    private string? _lastWaveCompositeId;
    private const int MaxWaveHistorySize = 350;

    private sealed class WaveHistoryState
    {
        public string? LastCompositeId { get; set; }
        public List<string>? SeenTrackIds { get; set; }
    }

    public string Name => "Яндекс";
    public string BrandKey => "yandex";

    public bool IsAuthenticated =>
        !string.IsNullOrWhiteSpace(_settings.Current.YandexToken) &&
        _settings.Current.YandexUserId > 0;

    public YandexMusicProvider(SettingsService settings, IEnumerable<IMusicProvider> fallbackProviders)
    {
        _settings = settings;
        _fallbackProviders = fallbackProviders
            .Where(p => !string.Equals(p.BrandKey, BrandKey, StringComparison.OrdinalIgnoreCase))
            .ToList();

        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.TryAddWithoutValidation("X-Yandex-Music-Client", ClientHeader);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "ru");

        var appDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OmniWave");
        Directory.CreateDirectory(appDir);
        _waveHistoryPath = Path.Combine(appDir, "wave_history.json");
        LoadWaveHistory();
    }

    public static string ExtractToken(string raw)
    {
        var trimmed = (raw ?? "").Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return "";

        var m = TokenInUrlRegex.Match(trimmed);
        if (m.Success)
            return Uri.UnescapeDataString(m.Groups[1].Value).Trim();

        if (trimmed.StartsWith("OAuth ", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[6..].Trim();
        else if (trimmed.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[7..].Trim();

        return trimmed;
    }

    public async Task<(bool Ok, string Message)> VerifyAndSaveTokenAsync(string rawInput, CancellationToken ct = default)
    {
        var token = ExtractToken(rawInput);
        if (string.IsNullOrWhiteSpace(token))
            return (false, "Токен Яндекс Музыки не найден.");

        try
        {
            using var doc = await GetJsonAsync("/account/status", token, ct);
            if (doc == null || !doc.RootElement.TryGetProperty("result", out var result) ||
                !result.TryGetProperty("account", out var acc))
            {
                return (false, "Неверный или устаревший OAuth-токен Яндекс Музыки.");
            }

            long uid = acc.TryGetProperty("uid", out var uidEl) && uidEl.ValueKind == JsonValueKind.Number
                ? uidEl.GetInt64()
                : 0;
            if (uid <= 0)
                return (false, "Не удалось получить ID аккаунта Яндекс.");

            string displayName = ReadFirstNonEmpty(acc, "displayName", "fullName", "login") ?? $"uid{uid}";

            _settings.Update(s =>
            {
                s.YandexToken = token;
                s.YandexUserId = uid;
                s.YandexUserName = displayName;
            }, notify: true);

            return (true, $"Подключён: {displayName}");
        }
        catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            return (false, "Яндекс отклонил токен (401/403). Получите новый через окно входа.");
        }
        catch (Exception ex)
        {
            return (false, $"Ошибка проверки Яндекс Музыки: {ex.Message}");
        }
    }

    public void Disconnect()
    {
        lock (_waveLock)
        {
            _waveSeenOrder.Clear();
            _waveSeenIds.Clear();
            _cachedUserLikedSeedIds.Clear();
            _lastWaveCompositeId = null;
            try { if (File.Exists(_waveHistoryPath)) File.Delete(_waveHistoryPath); } catch { }
        }

        _settings.Update(s =>
        {
            s.YandexToken = null;
            s.YandexUserId = 0;
            s.YandexUserName = null;
        }, notify: true);
    }

    public async Task<List<TrackItem>> SearchAsync(string query, int limit = 8, CancellationToken ct = default)
    {
        var results = new List<TrackItem>();
        if (!IsAuthenticated || string.IsNullOrWhiteSpace(query))
            return results;

        var token = _settings.Current.YandexToken!;
        try
        {
            var path = $"/search?type=track&text={Uri.EscapeDataString(query.Trim())}&page=0&nocorrect=false";
            using var doc = await GetJsonAsync(path, token, ct);
            if (doc == null) return results;

            if (doc.RootElement.TryGetProperty("result", out var res) &&
                res.TryGetProperty("tracks", out var tracksObj) &&
                tracksObj.TryGetProperty("results", out var arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var itemEl in arr.EnumerateArray())
                {
                    var tr = ParseYandexTrack(itemEl);
                    if (tr != null)
                    {
                        results.Add(tr);
                        if (results.Count >= limit) break;
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { }

        return results;
    }

    public async Task<List<UserPlaylist>> FetchUserPlaylistsAsync(CancellationToken ct = default)
    {
        var playlists = new List<UserPlaylist>();
        if (!IsAuthenticated) return playlists;

        var s = _settings.Current;
        var uid = s.YandexUserId;
        var token = s.YandexToken!;

        // 1. «Мне нравится» (как в importer.py: users_likes_tracks -> POST /tracks пачками, исходный порядок)
        try
        {
            using var likesDoc = await GetJsonAsync($"/users/{uid}/likes/tracks", token, ct);
            if (likesDoc != null &&
                likesDoc.RootElement.TryGetProperty("result", out var likesRes) &&
                likesRes.TryGetProperty("library", out var lib) &&
                lib.TryGetProperty("tracks", out var likeEntries) &&
                likeEntries.ValueKind == JsonValueKind.Array)
            {
                var ids = new List<string>();
                foreach (var entry in likeEntries.EnumerateArray())
                {
                    var tid = ReadStringOrNumber(entry, "id");
                    var aid = ReadStringOrNumber(entry, "albumId");
                    if (string.IsNullOrWhiteSpace(tid)) continue;
                    ids.Add(string.IsNullOrWhiteSpace(aid) ? tid : $"{tid}:{aid}");
                }

                if (ids.Count > 0)
                {
                    var likedTracks = await FetchTracksByIdsAsync(ids, token, ct);
                    if (likedTracks.Count > 0)
                    {
                        playlists.Add(new UserPlaylist
                        {
                            Id = $"yandex_likes_{uid}",
                            Name = "Яндекс • Мне нравится",
                            IsExternal = true,
                            PlatformBrand = "yandex",
                            ExternalId = "likes",
                            CoverUrl = likedTracks.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.CoverUrl))?.CoverUrl ?? "",
                            Tracks = likedTracks
                        });
                    }
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        // 2. Пользовательские плейлисты + обложки
        try
        {
            using var listDoc = await GetJsonAsync($"/users/{uid}/playlists/list", token, ct);
            if (listDoc != null &&
                listDoc.RootElement.TryGetProperty("result", out var listArr) &&
                listArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var plSummary in listArr.EnumerateArray())
                {
                    ct.ThrowIfCancellationRequested();

                    long kind = plSummary.TryGetProperty("kind", out var kEl) && kEl.ValueKind == JsonValueKind.Number
                        ? kEl.GetInt64()
                        : -1;
                    if (kind < 0) continue;

                    string title = ReadStringOrNumber(plSummary, "title") ?? $"Плейлист #{kind}";
                    string plCover = ExtractCoverUrl(plSummary, "400x400");

                    using var detailDoc = await GetJsonAsync($"/users/{uid}/playlists/{kind}", token, ct);
                    if (detailDoc == null || !detailDoc.RootElement.TryGetProperty("result", out var plDetail))
                        continue;

                    if (string.IsNullOrWhiteSpace(plCover))
                        plCover = ExtractCoverUrl(plDetail, "400x400");

                    var tracks = await ParsePlaylistTracksAsync(plDetail, token, ct);
                    if (tracks.Count > 0)
                    {
                        playlists.Add(new UserPlaylist
                        {
                            Id = $"yandex_pl_{uid}_{kind}",
                            Name = title,
                            IsExternal = true,
                            PlatformBrand = "yandex",
                            ExternalId = kind.ToString(),
                            CoverUrl = !string.IsNullOrWhiteSpace(plCover)
                                ? plCover
                                : (tracks.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.CoverUrl))?.CoverUrl ?? ""),
                            Tracks = tracks
                        });
                    }
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        return playlists;
    }

    public async Task<List<TrackItem>> FetchMyWaveTracksAsync(string? lastTrackId = null, CancellationToken ct = default)
    {
        var collected = new List<TrackItem>();
        if (!IsAuthenticated) return collected;

        const int TargetWaveBatchCount = 16;
        var token = _settings.Current.YandexToken!.Trim();
        var collectedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var allReturnedFallback = new List<TrackItem>();

        string? queueSeed = NormalizeWaveQueueSeed(lastTrackId);
        bool isFreshStart = string.IsNullOrWhiteSpace(queueSeed);
        if (isFreshStart)
        {
            lock (_waveLock)
            {
                queueSeed = _lastWaveCompositeId;
            }

            _ = SendRotorFeedbackAsync("user:onyourwave", null, new
            {
                type = "radioStarted",
                timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                from = "mobile-home-rup_main-user-onyourwave-default"
            }, token, ct);
        }

        // 1. Делаем до 3 последовательных запросов в Rotor с обратной связью trackFinished,
        // чтобы обойти ограничение в ~5 треков на ответ и продвинуть серверный курсор.
        for (int round = 0; round < 3 && collected.Count < TargetWaveBatchCount; round++)
        {
            ct.ThrowIfCancellationRequested();
            var url = "/rotor/station/user:onyourwave/tracks?settings2=true";
            if (!string.IsNullOrWhiteSpace(queueSeed))
                url += $"&queue={Uri.EscapeDataString(queueSeed)}";

            try
            {
                using var doc = await GetJsonAsync(url, token, ct);
                if (doc == null || !doc.RootElement.TryGetProperty("result", out var result))
                    break;

                string? batchId = ReadStringOrNumber(result, "batchId");
                if (!result.TryGetProperty("sequence", out var seq) || seq.ValueKind != JsonValueKind.Array)
                    break;

                var batchTracks = ParseRotorSequence(seq);
                if (batchTracks.Count == 0) break;

                allReturnedFallback.AddRange(batchTracks);
                AddUnseenWaveTracks(batchTracks, collected, collectedIds);

                var tailTrack = batchTracks[^1];
                string compLast = GetCompositeTrackId(tailTrack);
                if (string.IsNullOrWhiteSpace(compLast) ||
                    string.Equals(queueSeed, compLast, StringComparison.OrdinalIgnoreCase))
                {
                    break;
                }

                queueSeed = compLast;
                lock (_waveLock)
                {
                    _lastWaveCompositeId = compLast;
                }

                double playSec = Math.Max(30.0, tailTrack.Duration.TotalSeconds);
                await SendRotorFeedbackAsync("user:onyourwave", batchId, new
                {
                    type = "trackFinished",
                    timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    trackId = compLast,
                    totalPlayedSeconds = Math.Round(playSec, 1)
                }, token, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { break; }
        }

        // 2. Если «Моя волна» отдала мало новых треков (повторы или дневной лимит ротора),
        // расширяем поток через станции по любимым трекам пользователя и /similar.
        if (collected.Count < 12)
        {
            await EnsureLikedSeedIdsAsync(token, ct);
            var seedPool = BuildWaveSeedPool(allReturnedFallback);

            for (int sIdx = 0; sIdx < seedPool.Count && sIdx < 2 && collected.Count < TargetWaveBatchCount; sIdx++)
            {
                ct.ThrowIfCancellationRequested();
                string seedId = seedPool[sIdx];

                try
                {
                    using var stDoc = await GetJsonAsync($"/rotor/station/track:{Uri.EscapeDataString(seedId)}/tracks?settings2=true", token, ct);
                    if (stDoc != null && stDoc.RootElement.TryGetProperty("result", out var stRes) &&
                        stRes.TryGetProperty("sequence", out var stSeq) && stSeq.ValueKind == JsonValueKind.Array)
                    {
                        var stTracks = ParseRotorSequence(stSeq);
                        allReturnedFallback.AddRange(stTracks);
                        AddUnseenWaveTracks(stTracks, collected, collectedIds);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { }

                if (collected.Count >= 12) continue;

                try
                {
                    using var simDoc = await GetJsonAsync($"/tracks/{Uri.EscapeDataString(seedId)}/similar", token, ct);
                    if (simDoc != null && simDoc.RootElement.TryGetProperty("result", out var simRes) &&
                        simRes.TryGetProperty("similarTracks", out var simArr) && simArr.ValueKind == JsonValueKind.Array)
                    {
                        var simTracks = new List<TrackItem>();
                        foreach (var el in simArr.EnumerateArray())
                        {
                            var parsed = ParseYandexTrack(el);
                            if (parsed != null) simTracks.Add(parsed);
                        }
                        ShuffleInPlace(simTracks);
                        allReturnedFallback.AddRange(simTracks);
                        AddUnseenWaveTracks(simTracks, collected, collectedIds);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { }
            }
        }

        if (collected.Count == 0 && allReturnedFallback.Count > 0)
        {
            lock (_waveLock)
            {
                _waveSeenIds.Clear();
                _waveSeenOrder.Clear();
            }
            AddUnseenWaveTracks(allReturnedFallback, collected, collectedIds);
        }

        if (collected.Count > TargetWaveBatchCount)
            collected.RemoveRange(TargetWaveBatchCount, collected.Count - TargetWaveBatchCount);

        lock (_waveLock)
        {
            SaveWaveHistoryUnlocked();
        }

        return collected;
    }

    private List<TrackItem> ParseRotorSequence(JsonElement seq)
    {
        var list = new List<TrackItem>();
        foreach (var item in seq.EnumerateArray())
        {
            if (item.TryGetProperty("track", out var trackEl))
            {
                var parsed = ParseYandexTrack(trackEl);
                if (parsed != null) list.Add(parsed);
            }
        }
        return list;
    }

    private void AddUnseenWaveTracks(List<TrackItem> source, List<TrackItem> dest, HashSet<string> destIds)
    {
        lock (_waveLock)
        {
            foreach (var t in source)
            {
                if (string.IsNullOrWhiteSpace(t.Id)) continue;
                if (_waveSeenIds.Contains(t.Id) || !destIds.Add(t.Id)) continue;
                MarkWaveTrackSeenUnlocked(t.Id);
                dest.Add(t);
            }
        }
    }

    private async Task EnsureLikedSeedIdsAsync(string token, CancellationToken ct)
    {
        lock (_waveLock)
        {
            if (_cachedUserLikedSeedIds.Count > 0) return;
        }

        try
        {
            var uid = _settings.Current.YandexUserId;
            using var likesDoc = await GetJsonAsync($"/users/{uid}/likes/tracks", token, ct);
            if (likesDoc != null &&
                likesDoc.RootElement.TryGetProperty("result", out var likesRes) &&
                likesRes.TryGetProperty("library", out var lib) &&
                lib.TryGetProperty("tracks", out var likeEntries) &&
                likeEntries.ValueKind == JsonValueKind.Array)
            {
                var ids = new List<string>();
                foreach (var entry in likeEntries.EnumerateArray())
                {
                    var tid = ReadStringOrNumber(entry, "id");
                    if (!string.IsNullOrWhiteSpace(tid))
                        ids.Add(tid);
                    if (ids.Count >= 150) break;
                }

                lock (_waveLock)
                {
                    if (_cachedUserLikedSeedIds.Count == 0 && ids.Count > 0)
                        _cachedUserLikedSeedIds.AddRange(ids);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { }
    }

    private List<string> BuildWaveSeedPool(List<TrackItem> fallbackTracks)
    {
        var candidates = new List<string>();
        lock (_waveLock)
        {
            candidates.AddRange(_cachedUserLikedSeedIds);
            foreach (var t in fallbackTracks)
            {
                if (!string.IsNullOrWhiteSpace(t.Id))
                    candidates.Add(t.Id);
            }
            if (candidates.Count == 0)
                candidates.AddRange(_waveSeenOrder);
        }

        candidates = candidates.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        ShuffleInPlace(candidates);
        return candidates;
    }

    private static void ShuffleInPlace<T>(IList<T> items)
    {
        for (int i = items.Count - 1; i > 0; i--)
        {
            int j = Random.Shared.Next(i + 1);
            (items[i], items[j]) = (items[j], items[i]);
        }
    }

    private string? NormalizeWaveQueueSeed(string? rawSeed)
    {
        if (string.IsNullOrWhiteSpace(rawSeed)) return null;
        var trimmed = rawSeed.Trim();
        var mBoth = AlbumAndTrackIdInUrlRegex.Match(trimmed);
        if (mBoth.Success)
            return $"{mBoth.Groups[2].Value}:{mBoth.Groups[1].Value}";
        var mTrack = TrackIdInUrlRegex.Match(trimmed);
        if (mTrack.Success)
            return mTrack.Groups[1].Value;
        return trimmed;
    }

    private static string GetCompositeTrackId(TrackItem track)
    {
        if (TryExtractYandexTrackAndAlbumId(track, out var tid, out var aid))
            return !string.IsNullOrWhiteSpace(aid) ? $"{tid}:{aid}" : tid;
        return track.Id ?? "";
    }

    private void LoadWaveHistory()
    {
        lock (_waveLock)
        {
            try
            {
                if (!File.Exists(_waveHistoryPath)) return;
                var json = File.ReadAllText(_waveHistoryPath);
                var data = JsonSerializer.Deserialize<WaveHistoryState>(json);
                if (data == null) return;

                _lastWaveCompositeId = string.IsNullOrWhiteSpace(data.LastCompositeId) ? null : data.LastCompositeId.Trim();
                if (data.SeenTrackIds != null)
                {
                    foreach (var id in data.SeenTrackIds)
                    {
                        if (!string.IsNullOrWhiteSpace(id) && _waveSeenIds.Add(id))
                            _waveSeenOrder.Add(id);
                    }
                    TrimWaveHistoryUnlocked();
                }
            }
            catch { }
        }
    }

    private void MarkWaveTrackSeenUnlocked(string trackId)
    {
        if (_waveSeenIds.Add(trackId))
        {
            _waveSeenOrder.Add(trackId);
            TrimWaveHistoryUnlocked();
        }
    }

    private void TrimWaveHistoryUnlocked()
    {
        if (_waveSeenOrder.Count <= MaxWaveHistorySize) return;
        int removeCount = Math.Min(120, _waveSeenOrder.Count - (MaxWaveHistorySize - 120));
        if (removeCount <= 0) return;

        for (int i = 0; i < removeCount; i++)
            _waveSeenIds.Remove(_waveSeenOrder[i]);
        _waveSeenOrder.RemoveRange(0, removeCount);
    }

    private void SaveWaveHistoryUnlocked()
    {
        try
        {
            var data = new WaveHistoryState
            {
                LastCompositeId = _lastWaveCompositeId,
                SeenTrackIds = _waveSeenOrder.ToList()
            };
            File.WriteAllText(_waveHistoryPath, JsonSerializer.Serialize(data));
        }
        catch { }
    }

    private async Task SendRotorFeedbackAsync(string stationId, string? batchId, object payload, string token, CancellationToken ct)
    {
        try
        {
            var rel = $"/rotor/station/{stationId}/feedback";
            if (!string.IsNullOrWhiteSpace(batchId))
                rel += $"?batch-id={Uri.EscapeDataString(batchId)}";
            var json = JsonSerializer.Serialize(payload);
            await PostJsonAsync(rel, json, token, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { }
    }

    public async Task<string?> ResolveStreamUrlAsync(TrackItem track, CancellationToken ct = default)
    {
        // Этап 1: прямой поток Яндекс Музыки (при наличии действующего токена и ID трека)
        var trackId = ExtractNumericTrackId(track);
        if (IsAuthenticated && !string.IsNullOrWhiteSpace(trackId))
        {
            var directUrl = await TryResolveDirectYandexStreamAsync(trackId, _settings.Current.YandexToken!, ct);
            if (!string.IsNullOrWhiteSpace(directUrl))
                return directUrl;
        }

        // Этап 2: верифицированный подбор как в BuevichRoman/yandex2spotify (TrackMatcher ±3 с + артист)
        return await ResolveViaVerifiedMatchAsync(track, ct);
    }

    private async Task<List<TrackItem>> ParsePlaylistTracksAsync(JsonElement plDetail, string token, CancellationToken ct)
    {
        var inlineTracks = new List<TrackItem>();
        var missingIds = new List<string>();
        bool collective = plDetail.TryGetProperty("collective", out var colEl) && colEl.ValueKind == JsonValueKind.True;

        if (!plDetail.TryGetProperty("tracks", out var tracksArr) || tracksArr.ValueKind != JsonValueKind.Array)
            return inlineTracks;

        foreach (var entry in tracksArr.EnumerateArray())
        {
            if (!collective && entry.TryGetProperty("track", out var trObj) && trObj.ValueKind == JsonValueKind.Object)
            {
                var parsed = ParseYandexTrack(trObj);
                if (parsed != null)
                {
                    inlineTracks.Add(parsed);
                    continue;
                }
            }

            var tid = ReadStringOrNumber(entry, "id");
            if (string.IsNullOrWhiteSpace(tid) && entry.TryGetProperty("track", out var innerTr))
                tid = ReadStringOrNumber(innerTr, "id");

            var aid = ReadStringOrNumber(entry, "albumId");
            if (!string.IsNullOrWhiteSpace(tid))
                missingIds.Add(string.IsNullOrWhiteSpace(aid) ? tid : $"{tid}:{aid}");
        }

        if (!collective && missingIds.Count == 0)
            return inlineTracks;

        if (missingIds.Count > 0)
        {
            var fetched = await FetchTracksByIdsAsync(missingIds, token, ct);
            if (collective) return fetched;
            inlineTracks.AddRange(fetched);
        }

        return inlineTracks;
    }

    public async Task<TrackItem?> GetTrackByIdAsync(string trackOrCompositeId, CancellationToken ct = default)
    {
        if (!IsAuthenticated || string.IsNullOrWhiteSpace(trackOrCompositeId))
            return null;

        try
        {
            var token = _settings.Current.YandexToken!;
            var list = await FetchTracksByIdsAsync(new List<string> { trackOrCompositeId }, token, ct);
            return list.FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private async Task<List<TrackItem>> FetchTracksByIdsAsync(List<string> ids, string token, CancellationToken ct)
    {
        var result = new List<TrackItem>(ids.Count);
        const int batchSize = 150;

        for (int i = 0; i < ids.Count; i += batchSize)
        {
            ct.ThrowIfCancellationRequested();
            var chunk = ids.Skip(i).Take(batchSize);
            var form = $"track-ids={Uri.EscapeDataString(string.Join(",", chunk))}";

            using var doc = await PostFormJsonAsync("/tracks", form, token, ct);
            if (doc != null &&
                doc.RootElement.TryGetProperty("result", out var arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    var tr = ParseYandexTrack(item);
                    if (tr != null) result.Add(tr);
                }
            }
        }

        return result;
    }

    private async Task<string?> TryResolveDirectYandexStreamAsync(string trackId, string token, CancellationToken ct)
    {
        try
        {
            using var infoDoc = await GetJsonAsync($"/tracks/{trackId}/download-info", token, ct);
            if (infoDoc == null ||
                !infoDoc.RootElement.TryGetProperty("result", out var variants) ||
                variants.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            string? bestInfoUrl = null;
            int bestBitrate = -1;

            foreach (var v in variants.EnumerateArray())
            {
                string codec = ReadStringOrNumber(v, "codec") ?? "";
                int bitrate = v.TryGetProperty("bitrateInKbps", out var brEl) && brEl.ValueKind == JsonValueKind.Number
                    ? brEl.GetInt32()
                    : 0;
                string? dlUrl = ReadStringOrNumber(v, "downloadInfoUrl");
                if (string.IsNullOrWhiteSpace(dlUrl)) continue;

                int score = codec.Contains("flac", StringComparison.OrdinalIgnoreCase)
                    ? bitrate + 2000
                    : (codec.Equals("mp3", StringComparison.OrdinalIgnoreCase) ||
                       (codec.Contains("aac", StringComparison.OrdinalIgnoreCase) && bitrate >= 192))
                        ? bitrate + 1000
                        : bitrate;
                if (score > bestBitrate)
                {
                    bestBitrate = score;
                    bestInfoUrl = dlUrl;
                }
            }

            if (string.IsNullOrWhiteSpace(bestInfoUrl)) return null;

            using var xmlResp = await SendWithRetryAsync(() =>
            {
                var req = new HttpRequestMessage(HttpMethod.Get, bestInfoUrl);
                req.Headers.TryAddWithoutValidation("Authorization", $"OAuth {token}");
                return req;
            }, ct);

            if (!xmlResp.IsSuccessStatusCode) return null;
            var xmlStr = await xmlResp.Content.ReadAsStringAsync(ct);
            var xdoc = XDocument.Parse(xmlStr);
            var root = xdoc.Root;
            if (root == null) return null;

            string host = root.Element("host")?.Value ?? "";
            string path = root.Element("path")?.Value ?? "";
            string ts = root.Element("ts")?.Value ?? "";
            string s = root.Element("s")?.Value ?? "";
            if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(path) || string.IsNullOrEmpty(ts) || string.IsNullOrEmpty(s))
                return null;

            string cleanPath = path.StartsWith('/') ? path[1..] : path;
            byte[] hash = MD5.HashData(Encoding.UTF8.GetBytes(SignSalt + cleanPath + s));
            string sign = Convert.ToHexString(hash).ToLowerInvariant();
            return $"https://{host}/get-mp3/{sign}/{ts}{path}";
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> ResolveViaVerifiedMatchAsync(TrackItem track, CancellationToken ct)
    {
        var activeFallbacks = _fallbackProviders
            .Where(p => p is not VkMusicProvider vk || vk.IsAuthenticated)
            .ToList();

        if (activeFallbacks.Count == 0) return null;

        var query = TrackMatcher.BuildSearchQuery(track.Artist, track.Title);
        var (winner, provider, firstPlayable) = await SearchAndMatchAcrossFallbacksAsync(track, query, activeFallbacks, ct);

        if (winner == null && TrackMatcher.HasMultipleArtists(track.Artist))
        {
            var shortQuery = TrackMatcher.BuildSearchQuery(TrackMatcher.GetPrimaryArtist(track.Artist), track.Title);
            if (!string.Equals(shortQuery, query, StringComparison.OrdinalIgnoreCase))
            {
                (winner, provider, var secondFirst) = await SearchAndMatchAcrossFallbacksAsync(track, shortQuery, activeFallbacks, ct);
                if (firstPlayable.Track == null)
                    firstPlayable = secondFirst;
            }
        }

        if (winner != null && provider != null)
        {
            var url = await provider.ResolveStreamUrlAsync(winner, ct);
            if (!string.IsNullOrWhiteSpace(url)) return url;
        }

        // Для треков без длительности (например, из JSON-списка) пробуем лучший найденный источник
        if (track.Duration <= TimeSpan.Zero && firstPlayable.Track != null && firstPlayable.Provider != null)
        {
            var url = await firstPlayable.Provider.ResolveStreamUrlAsync(firstPlayable.Track, ct);
            if (!string.IsNullOrWhiteSpace(url)) return url;
        }

        return null;
    }

    private async Task<(TrackItem? Winner, IMusicProvider? Provider, (TrackItem? Track, IMusicProvider? Provider) FirstPlayable)>
        SearchAndMatchAcrossFallbacksAsync(
            TrackItem target,
            string query,
            List<IMusicProvider> providers,
            CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
            return (null, null, (null, null));

        var tasks = providers.Select(async p =>
        {
            try
            {
                var items = await p.SearchAsync(query, 6, ct);
                return (Provider: p, Items: items);
            }
            catch
            {
                return (Provider: p, Items: new List<TrackItem>());
            }
        }).ToList();

        var batches = await Task.WhenAll(tasks);

        var candidateToProvider = new Dictionary<TrackItem, IMusicProvider>();
        var allCandidates = new List<TrackItem>();
        (TrackItem? Track, IMusicProvider? Provider) firstPlayable = (null, null);

        foreach (var (prov, items) in batches)
        {
            foreach (var item in items)
            {
                firstPlayable.Track ??= item;
                if (firstPlayable.Provider == null) firstPlayable.Provider = prov;
                candidateToProvider[item] = prov;
                allCandidates.Add(item);
            }
        }

        var winner = TrackMatcher.PickBestMatch(target, allCandidates);
        if (winner != null && candidateToProvider.TryGetValue(winner, out var winProv))
            return (winner, winProv, firstPlayable);

        return (null, null, firstPlayable);
    }

    private TrackItem? ParseYandexTrack(JsonElement el)
    {
        try
        {
            var id = ReadStringOrNumber(el, "id");
            if (string.IsNullOrWhiteSpace(id)) return null;

            string title = ReadStringOrNumber(el, "title") ?? "Без названия";
            string version = ReadStringOrNumber(el, "version") ?? "";
            if (!string.IsNullOrWhiteSpace(version))
                title = $"{title} ({version})";

            var artistsList = new List<string>();
            if (el.TryGetProperty("artists", out var artistsArr) && artistsArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in artistsArr.EnumerateArray())
                {
                    var name = ReadStringOrNumber(a, "name");
                    if (!string.IsNullOrWhiteSpace(name))
                        artistsList.Add(name);
                }
            }
            string artist = artistsList.Count > 0 ? string.Join(", ", artistsList) : "Яндекс Музыка";

            long durationMs = el.TryGetProperty("durationMs", out var dEl) && dEl.ValueKind == JsonValueKind.Number
                ? dEl.GetInt64()
                : 0;

            string? albumId = null;
            string coverUrl = ExtractCoverUrl(el, "300x300");
            if (el.TryGetProperty("albums", out var albumsArr) && albumsArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var alb in albumsArr.EnumerateArray())
                {
                    albumId ??= ReadStringOrNumber(alb, "id");
                    if (string.IsNullOrWhiteSpace(coverUrl))
                        coverUrl = ExtractCoverUrl(alb, "300x300");
                    break;
                }
            }

            string pageUrl = !string.IsNullOrWhiteSpace(albumId)
                ? $"https://music.yandex.ru/album/{albumId}/track/{id}"
                : $"https://music.yandex.ru/track/{id}";

            return new TrackItem
            {
                Id = id,
                Source = "Яндекс",
                BrandKey = "yandex",
                BrandColor = BrandColorHex,
                Title = title,
                Artist = artist,
                PageUrl = pageUrl,
                CoverUrl = coverUrl,
                Duration = durationMs > 0 ? TimeSpan.FromMilliseconds(durationMs) : TimeSpan.Zero
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Разбирает JSON-список формата BuevichRoman/yandex2spotify (-j):
    /// [{"artist": "...", "track": "..."}]
    /// </summary>
    public static List<TrackItem> ParseYandex2SpotifyJson(string jsonText)
    {
        var tracks = new List<TrackItem>();
        using var doc = JsonDocument.Parse(jsonText);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return tracks;

        int idx = 0;
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            idx++;
            string artist = ReadFirstNonEmpty(el, "artist", "artists", "author") ?? "";
            string title = ReadFirstNonEmpty(el, "track", "title", "name") ?? "";
            if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(artist))
                continue;

            tracks.Add(new TrackItem
            {
                Id = $"json_{idx}",
                Source = "Яндекс",
                BrandKey = "yandex",
                BrandColor = BrandColorHex,
                Title = string.IsNullOrWhiteSpace(title) ? artist : title,
                Artist = string.IsNullOrWhiteSpace(artist) ? "Импорт" : artist,
                PageUrl = $"yandex:json:{artist}:{title}:{idx}"
            });
        }

        return tracks;
    }

    private async Task<JsonDocument?> GetJsonAsync(string relativeUrl, string token, CancellationToken ct)
    {
        if (!relativeUrl.StartsWith('/'))
            relativeUrl = "/" + relativeUrl;

        using var resp = await SendWithRetryAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, ApiBase + relativeUrl);
            req.Headers.TryAddWithoutValidation("Authorization", $"OAuth {token}");
            return req;
        }, ct);

        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new HttpRequestException($"Yandex API auth error {(int)resp.StatusCode}", null, resp.StatusCode);

        if (!resp.IsSuccessStatusCode) return null;
        var json = await resp.Content.ReadAsStringAsync(ct);
        return JsonDocument.Parse(json);
    }

    private async Task<JsonDocument?> PostFormJsonAsync(string relativeUrl, string formBody, string token, CancellationToken ct)
    {
        if (!relativeUrl.StartsWith('/'))
            relativeUrl = "/" + relativeUrl;

        using var resp = await SendWithRetryAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, ApiBase + relativeUrl);
            req.Headers.TryAddWithoutValidation("Authorization", $"OAuth {token}");
            req.Content = new StringContent(formBody, Encoding.UTF8, "application/x-www-form-urlencoded");
            return req;
        }, ct);

        if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            throw new HttpRequestException($"Yandex API auth error {(int)resp.StatusCode}", null, resp.StatusCode);

        if (!resp.IsSuccessStatusCode) return null;
        var json = await resp.Content.ReadAsStringAsync(ct);
        return JsonDocument.Parse(json);
    }

    private async Task PostJsonAsync(string relativeUrl, string jsonBody, string token, CancellationToken ct)
    {
        if (!relativeUrl.StartsWith('/'))
            relativeUrl = "/" + relativeUrl;

        using var resp = await SendWithRetryAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, ApiBase + relativeUrl);
            req.Headers.TryAddWithoutValidation("Authorization", $"OAuth {token}");
            req.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
            return req;
        }, ct);
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(Func<HttpRequestMessage> buildReq, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using var req = buildReq();
                var resp = await _http.SendAsync(req, ct);
                if ((int)resp.StatusCode >= 500 && attempt < MaxRetries)
                {
                    resp.Dispose();
                    int delaySec = Math.Min(1 << attempt, 16);
                    await Task.Delay(TimeSpan.FromSeconds(delaySec), ct);
                    continue;
                }
                return resp;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception) when (attempt < MaxRetries)
            {
                int delaySec = Math.Min(1 << attempt, 16);
                await Task.Delay(TimeSpan.FromSeconds(delaySec), ct);
            }
        }
    }

    /// <summary>
    /// Переносит список треков (например, из вкладки «Любимое» или выбранный трек)
    /// напрямую в «Мне нравится» Яндекс Музыки.
    /// Для треков из других источников (ВК, SoundCloud, YouTube, Audius, Bandcamp и т.д.)
    /// автоматически находит точное совпадение в каталоге Яндекс Музыки.
    /// </summary>
    public async Task<YandexTransferResult> ExportTracksToYandexLikesAsync(
        IReadOnlyList<TrackItem> tracks,
        Action<int, int, string>? onProgress = null,
        CancellationToken ct = default)
    {
        if (!IsAuthenticated)
        {
            return new YandexTransferResult(
                false, 0, 0, 0,
                "Сначала подключите аккаунт Яндекс Музыки (кнопка «Яндекс • ВК» сверху).");
        }

        var validTracks = tracks?.Where(t => t != null).ToList() ?? new List<TrackItem>();
        if (validTracks.Count == 0)
        {
            return new YandexTransferResult(
                false, 0, 0, 0,
                "В списке «Любимое» пока нет треков для переноса.");
        }

        var token = _settings.Current.YandexToken!;
        var uid = _settings.Current.YandexUserId;

        var existingLikedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using var likesDoc = await GetJsonAsync($"/users/{uid}/likes/tracks", token, ct);
            if (likesDoc != null &&
                likesDoc.RootElement.TryGetProperty("result", out var likesRes) &&
                likesRes.TryGetProperty("library", out var lib) &&
                lib.TryGetProperty("tracks", out var likeEntries) &&
                likeEntries.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in likeEntries.EnumerateArray())
                {
                    var tid = ReadStringOrNumber(entry, "id");
                    if (!string.IsNullOrWhiteSpace(tid))
                        existingLikedIds.Add(tid);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        var specsToAdd = new List<string>();
        int alreadyLiked = 0;
        int notFound = 0;

        for (int i = 0; i < validTracks.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var tr = validTracks[i];
            onProgress?.Invoke(i + 1, validTracks.Count, $"{tr.Artist} — {tr.Title}");

            string trackId = "";
            string albumId = "";

            if (TryExtractYandexTrackAndAlbumId(tr, out var directTid, out var directAid))
            {
                trackId = directTid;
                albumId = directAid;
            }
            else
            {
                var matched = await FindBestYandexMatchAsync(tr, ct);
                if (matched != null && TryExtractYandexTrackAndAlbumId(matched, out var mTid, out var mAid))
                {
                    trackId = mTid;
                    albumId = mAid;
                }
            }

            if (string.IsNullOrWhiteSpace(trackId))
            {
                notFound++;
                continue;
            }

            if (existingLikedIds.Contains(trackId))
            {
                alreadyLiked++;
                continue;
            }

            existingLikedIds.Add(trackId);
            specsToAdd.Add(!string.IsNullOrWhiteSpace(albumId) ? $"{trackId}:{albumId}" : trackId);
        }

        if (specsToAdd.Count > 0)
        {
            // В Яндекс Музыке новые лайки встают в начало списка, поэтому отправляем в обратном порядке,
            // чтобы сохранить порядок треков из вкладки «Любимое».
            specsToAdd.Reverse();
            const int chunkSize = 50;

            for (int i = 0; i < specsToAdd.Count; i += chunkSize)
            {
                ct.ThrowIfCancellationRequested();
                var chunk = specsToAdd.Skip(i).Take(chunkSize);
                var form = $"track-ids={Uri.EscapeDataString(string.Join(",", chunk))}";
                using var _ = await PostFormJsonAsync($"/users/{uid}/likes/tracks/add-multiple", form, token, ct);
            }
        }

        int added = specsToAdd.Count;
        string msg;
        if (validTracks.Count == 1)
        {
            var single = validTracks[0];
            if (added == 1)
                msg = $"Добавлено в Яндекс «Мне нравится»: {single.Artist} — {single.Title}";
            else if (alreadyLiked == 1)
                msg = $"Уже есть в Яндекс «Мне нравится»: {single.Artist} — {single.Title}";
            else
                msg = $"Не найден в каталоге Яндекс Музыки: {single.Artist} — {single.Title}";
        }
        else if (added > 0)
        {
            var parts = new List<string> { $"Перенесено в Яндекс Музыку: {added} из {validTracks.Count}" };
            if (alreadyLiked > 0) parts.Add($"уже были: {alreadyLiked}");
            if (notFound > 0) parts.Add($"не найдено: {notFound}");
            msg = string.Join(" • ", parts);
        }
        else if (alreadyLiked > 0)
        {
            msg = $"Все найденные треки ({alreadyLiked}) уже есть в Яндекс «Мне нравится»" +
                  (notFound > 0 ? $" • не найдено: {notFound}" : "");
        }
        else
        {
            msg = "Не удалось найти совпадающие треки в каталоге Яндекс Музыки.";
        }

        return new YandexTransferResult(added > 0 || alreadyLiked > 0, added, alreadyLiked, notFound, msg);
    }

    private async Task<TrackItem?> FindBestYandexMatchAsync(TrackItem target, CancellationToken ct)
    {
        var cleanTitle = VideoNoiseTagRegex.Replace(target.Title ?? "", "").Trim();
        var cleanArtist = (target.Artist ?? "").Trim();

        // Если в заголовке записано «Артист - Название» (часто на YouTube / SoundCloud)
        string? splitArtist = null;
        string? splitTitle = null;
        foreach (var sep in new[] { " — ", " – ", " - " })
        {
            int idx = cleanTitle.IndexOf(sep, StringComparison.Ordinal);
            if (idx > 1 && idx + sep.Length < cleanTitle.Length)
            {
                splitArtist = cleanTitle[..idx].Trim();
                splitTitle = cleanTitle[(idx + sep.Length)..].Trim();
                break;
            }
        }

        var normalizedTarget = new TrackItem
        {
            Title = splitTitle ?? cleanTitle,
            Artist = !string.IsNullOrWhiteSpace(splitArtist) ? $"{cleanArtist}, {splitArtist}" : cleanArtist,
            Duration = target.Duration
        };

        var queries = new List<string>();
        void AddQuery(string q)
        {
            if (!string.IsNullOrWhiteSpace(q) && !queries.Contains(q, StringComparer.OrdinalIgnoreCase))
                queries.Add(q);
        }

        AddQuery(TrackMatcher.BuildSearchQuery(cleanArtist, cleanTitle));
        if (!string.IsNullOrWhiteSpace(splitArtist) && !string.IsNullOrWhiteSpace(splitTitle))
            AddQuery(TrackMatcher.BuildSearchQuery(splitArtist, splitTitle));
        if (TrackMatcher.HasMultipleArtists(cleanArtist))
            AddQuery(TrackMatcher.BuildSearchQuery(TrackMatcher.GetPrimaryArtist(cleanArtist), cleanTitle));
        if (!string.IsNullOrWhiteSpace(cleanTitle))
            AddQuery(TrackMatcher.BuildSearchQuery("", cleanTitle));

        TrackItem? fallbackCandidate = null;

        foreach (var q in queries)
        {
            var candidates = await SearchAsync(q, 10, ct);
            if (candidates.Count == 0) continue;

            fallbackCandidate ??= candidates[0];

            var match = TrackMatcher.PickBestMatch(target, candidates)
                        ?? TrackMatcher.PickBestMatch(normalizedTarget, candidates);
            if (match != null)
                return match;

            // Фолбэк, если длительность на YouTube/SoundCloud отличалась > 3 с (клип vs студийная версия),
            // но артист и название совпадают
            var targetNormTitle = TrackMatcher.NormalizeKey(normalizedTarget.Title);
            foreach (var cand in candidates)
            {
                var candNormTitle = TrackMatcher.NormalizeKey(cand.Title);
                bool titleMatch = !string.IsNullOrEmpty(targetNormTitle) &&
                                  (candNormTitle == targetNormTitle ||
                                   candNormTitle.Contains(targetNormTitle) ||
                                   targetNormTitle.Contains(candNormTitle));
                if (titleMatch && TrackMatcher.ArtistsOverlap(normalizedTarget.Artist, cand.Artist, cand.Title))
                    return cand;
            }
        }

        return fallbackCandidate;
    }

    private static bool TryExtractYandexTrackAndAlbumId(TrackItem track, out string trackId, out string albumId)
    {
        trackId = "";
        albumId = "";
        if (track == null) return false;

        var mBoth = AlbumAndTrackIdInUrlRegex.Match(track.PageUrl ?? "");
        if (mBoth.Success)
        {
            albumId = mBoth.Groups[1].Value;
            trackId = mBoth.Groups[2].Value;
            return true;
        }

        var mTrack = TrackIdInUrlRegex.Match(track.PageUrl ?? "");
        if (mTrack.Success)
        {
            trackId = mTrack.Groups[1].Value;
            return true;
        }

        if (string.Equals(track.BrandKey, "yandex", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(track.Id) &&
            track.Id.All(char.IsDigit))
        {
            trackId = track.Id;
            return true;
        }

        return false;
    }

    private static string ExtractNumericTrackId(TrackItem track)
    {
        return TryExtractYandexTrackAndAlbumId(track, out var tid, out _) ? tid : "";
    }

    private static string ExtractCoverUrl(JsonElement obj, string size)
    {
        string? rawUri = ReadStringOrNumber(obj, "coverUri") ?? ReadStringOrNumber(obj, "ogImage");
        if (string.IsNullOrWhiteSpace(rawUri) && obj.TryGetProperty("cover", out var cov))
            rawUri = ReadStringOrNumber(cov, "uri");

        if (string.IsNullOrWhiteSpace(rawUri)) return "";
        string formatted = rawUri.Replace("%%", size);
        return formatted.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? formatted
            : "https://" + formatted.TrimStart('/');
    }

    private static string? ReadFirstNonEmpty(JsonElement el, params string[] keys)
    {
        foreach (var k in keys)
        {
            var val = ReadStringOrNumber(el, k);
            if (!string.IsNullOrWhiteSpace(val)) return val.Trim();
        }
        return null;
    }

    private static string? ReadStringOrNumber(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var val)) return null;
        return val.ValueKind switch
        {
            JsonValueKind.String => val.GetString(),
            JsonValueKind.Number => val.GetRawText(),
            _ => null
        };
    }

    public void Dispose() => _http.Dispose();
}
