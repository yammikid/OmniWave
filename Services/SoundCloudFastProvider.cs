using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DynamicIsland.Services;

public class SoundCloudFastProvider : IMusicProvider, IDisposable
{
    private const string BrandColorHex = "#FF5500";
    private static readonly string[] FallbackClientIds =
    {
        "Hpp4j7peQTPimFlKMjTX3y3mK2946b7y",
        "UDcvt5Vv7xCSpTVJffx4o3I1xttD2dCo"
    };

    private static readonly Regex ScriptUrlRegex = new(
        @"<script[^>]+src=[""'](https://a-v2\.sndcdn\.com/assets/[^""']+\.js)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ClientIdRegex = new(
        @"client_id\s*[:=]\s*[""']([a-zA-Z0-9]{32})[""']",
        RegexOptions.Compiled);
    private static readonly Regex HlsMapRegex = new(
        @"#EXT-X-MAP:URI=""([^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private sealed record ScTranscoding(string Url, string Protocol, string Preset, int Priority);
    private sealed record CachedScMedia(string? TrackAuthorization, List<ScTranscoding> Transcodings);

    private readonly HttpClient _http;
    private readonly YtDlpMusicProvider _fallback;
    private readonly SemaphoreSlim _cidLock = new(1, 1);
    private readonly string _cidFilePath;
    private readonly string _scCacheDir;
    private readonly ConcurrentDictionary<string, CachedScMedia> _mediaCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, byte> _invalidatedCids = new(StringComparer.Ordinal);
    private string? _cachedClientId;

    public string Name => "SoundCloud";
    public string BrandKey => "soundcloud";

    public SoundCloudFastProvider()
    {
        _http = new HttpClient(new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.All
        })
        {
            Timeout = TimeSpan.FromSeconds(8)
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");

        _fallback = new YtDlpMusicProvider(Name, BrandKey, "scsearch", BrandColorHex);

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OmniWave");
        Directory.CreateDirectory(dir);
        _cidFilePath = Path.Combine(dir, "sc_client_id.txt");

        _scCacheDir = Path.Combine(Path.GetTempPath(), "OmniWave", "sc_cache");
        Directory.CreateDirectory(_scCacheDir);
    }

    public async Task<List<TrackItem>> SearchAsync(string query, int limit = 8, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return new List<TrackItem>();

        try
        {
            int fetchLimit = Math.Clamp(limit * 2 + 4, 16, 40);
            var json = await GetApiJsonWithRetryAsync(
                cid => $"https://api-v2.soundcloud.com/search/tracks?q={Uri.EscapeDataString(query.Trim())}&client_id={cid}&limit={fetchLimit}&offset=0",
                ct);

            if (!string.IsNullOrWhiteSpace(json))
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("collection", out var coll) &&
                    coll.ValueKind == JsonValueKind.Array)
                {
                    var items = new List<TrackItem>();
                    foreach (var el in coll.EnumerateArray())
                    {
                        var track = ParseTrackElement(el);
                        if (track != null)
                        {
                            items.Add(track);
                            if (items.Count >= limit) break;
                        }
                    }

                    if (items.Count > 0)
                        return items;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { }

        try
        {
            return await _fallback.SearchAsync(query, limit, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            return new List<TrackItem>();
        }
    }

    public async Task<string?> ResolveStreamUrlAsync(TrackItem track, CancellationToken ct = default)
    {
        var cachedLocal = TryGetCachedScAudio(track);
        if (cachedLocal != null)
            return cachedLocal;

        try
        {
            CachedScMedia? scMedia = null;
            if (!string.IsNullOrWhiteSpace(track.Id) && _mediaCache.TryGetValue(track.Id, out var cachedById))
                scMedia = cachedById;
            else if (!string.IsNullOrWhiteSpace(track.PageUrl) && _mediaCache.TryGetValue(track.PageUrl, out var cachedByUrl))
                scMedia = cachedByUrl;

            if (scMedia == null || scMedia.Transcodings.Count == 0)
            {
                string? trackJson = null;
                if (string.IsNullOrWhiteSpace(track.Id) && !string.IsNullOrWhiteSpace(track.PageUrl))
                {
                    trackJson = await GetApiJsonWithRetryAsync(
                        cid => $"https://api-v2.soundcloud.com/resolve?url={Uri.EscapeDataString(track.PageUrl)}&client_id={cid}",
                        ct);
                }
                else if (!string.IsNullOrWhiteSpace(track.Id))
                {
                    trackJson = await GetApiJsonWithRetryAsync(
                        cid => $"https://api-v2.soundcloud.com/tracks/{Uri.EscapeDataString(track.Id)}?client_id={cid}",
                        ct);
                }

                if (!string.IsNullOrWhiteSpace(trackJson))
                {
                    using var doc = JsonDocument.Parse(trackJson);
                    var root = doc.RootElement;

                    var parsed = ParseTrackElement(root, allowMissingTranscodings: true);
                    if (parsed != null)
                    {
                        if (string.IsNullOrWhiteSpace(track.Id)) track.Id = parsed.Id;
                        if (string.IsNullOrWhiteSpace(track.Title) || track.Title.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                            track.Title = parsed.Title;
                        if (string.IsNullOrWhiteSpace(track.Artist) || track.Artist == Name)
                            track.Artist = parsed.Artist;
                        if (string.IsNullOrWhiteSpace(track.CoverUrl))
                            track.CoverUrl = parsed.CoverUrl;
                        if (track.Duration <= TimeSpan.Zero && parsed.Duration > TimeSpan.Zero)
                            track.Duration = parsed.Duration;
                    }

                    scMedia = ExtractScMedia(root);
                }
            }

            if (scMedia != null && scMedia.Transcodings.Count > 0)
            {
                foreach (var tr in scMedia.Transcodings)
                {
                    ct.ThrowIfCancellationRequested();
                    string? streamJson = await GetApiJsonWithRetryAsync(cid =>
                    {
                        var sep = tr.Url.Contains('?') ? "&" : "?";
                        var url = $"{tr.Url}{sep}client_id={cid}";
                        if (!string.IsNullOrWhiteSpace(scMedia.TrackAuthorization))
                            url += $"&track_authorization={Uri.EscapeDataString(scMedia.TrackAuthorization)}";
                        return url;
                    }, ct);

                    if (string.IsNullOrWhiteSpace(streamJson)) continue;

                    string? finalUrl = null;
                    using (var streamDoc = JsonDocument.Parse(streamJson))
                    {
                        if (streamDoc.RootElement.TryGetProperty("url", out var finalUrlEl) &&
                            finalUrlEl.ValueKind == JsonValueKind.String)
                        {
                            finalUrl = finalUrlEl.GetString();
                        }
                    }

                    if (string.IsNullOrWhiteSpace(finalUrl)) continue;

                    if (!finalUrl.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
                        return finalUrl;

                    var assembledPath = await TryAssembleHlsToLocalM4aAsync(finalUrl, track, ct);
                    if (!string.IsNullOrWhiteSpace(assembledPath))
                        return assembledPath;

                    return finalUrl;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { }

        try
        {
            return await _fallback.ResolveStreamUrlAsync(track, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            return null;
        }
    }

    private TrackItem? ParseTrackElement(JsonElement el, bool allowMissingTranscodings = false)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;

        if (el.TryGetProperty("policy", out var policyEl) && policyEl.ValueKind == JsonValueKind.String)
        {
            var policy = policyEl.GetString() ?? "";
            if (string.Equals(policy, "BLOCK", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(policy, "SNIP", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        var scMedia = ExtractScMedia(el);
        if (!allowMissingTranscodings &&
            el.TryGetProperty("media", out var mediaEl) &&
            mediaEl.TryGetProperty("transcodings", out var transArr) &&
            transArr.ValueKind == JsonValueKind.Array &&
            transArr.GetArrayLength() > 0 &&
            scMedia.Transcodings.Count == 0)
        {
            return null;
        }

        string id = "";
        if (el.TryGetProperty("id", out var idEl))
        {
            id = idEl.ValueKind == JsonValueKind.Number
                ? idEl.GetInt64().ToString()
                : (idEl.GetString() ?? "");
        }

        string title = el.TryGetProperty("title", out var tEl) && tEl.ValueKind == JsonValueKind.String
            ? (tEl.GetString() ?? "Без названия")
            : "Без названия";

        string artist = Name;
        string avatarUrl = "";
        if (el.TryGetProperty("user", out var userEl) && userEl.ValueKind == JsonValueKind.Object)
        {
            if (userEl.TryGetProperty("username", out var uEl) && !string.IsNullOrWhiteSpace(uEl.GetString()))
                artist = uEl.GetString()!;
            if (userEl.TryGetProperty("avatar_url", out var avEl) && avEl.ValueKind == JsonValueKind.String)
                avatarUrl = avEl.GetString() ?? "";
        }

        string? publisherArtist = null;
        if (el.TryGetProperty("publisher_metadata", out var pubEl) &&
            pubEl.ValueKind == JsonValueKind.Object &&
            pubEl.TryGetProperty("artist", out var pubArtEl) &&
            pubArtEl.ValueKind == JsonValueKind.String)
        {
            var candidatePubArtist = pubArtEl.GetString()?.Trim();
            if (!string.IsNullOrWhiteSpace(candidatePubArtist) &&
                !candidatePubArtist.Equals("Various Artists", StringComparison.OrdinalIgnoreCase))
            {
                publisherArtist = candidatePubArtist;
            }
        }

        var (cleanArtist, cleanTitle, _) = TrackMatcher.NormalizeArtistAndTitle(
            artist,
            title,
            publisherArtist,
            allowSplitArtistFromTitle: true,
            stripDisplayVideoNoise: true);
        if (!string.IsNullOrWhiteSpace(cleanTitle))
            title = cleanTitle;
        if (!string.IsNullOrWhiteSpace(cleanArtist))
            artist = cleanArtist;

        string pageUrl = el.TryGetProperty("permalink_url", out var pEl) && pEl.ValueKind == JsonValueKind.String
            ? (pEl.GetString() ?? "")
            : "";
        if (string.IsNullOrWhiteSpace(pageUrl)) return null;

        if (scMedia.Transcodings.Count > 0)
        {
            if (!string.IsNullOrWhiteSpace(id)) _mediaCache[id] = scMedia;
            _mediaCache[pageUrl] = scMedia;
        }

        string coverUrl = el.TryGetProperty("artwork_url", out var artEl) && artEl.ValueKind == JsonValueKind.String
            ? (artEl.GetString() ?? "")
            : "";
        if (string.IsNullOrWhiteSpace(coverUrl))
            coverUrl = avatarUrl;
        if (!string.IsNullOrWhiteSpace(coverUrl))
            coverUrl = coverUrl
                .Replace("-large.", "-t500x500.", StringComparison.OrdinalIgnoreCase)
                .Replace("-original.", "-t500x500.", StringComparison.OrdinalIgnoreCase);

        double durationMs = el.TryGetProperty("duration", out var dEl) && dEl.ValueKind == JsonValueKind.Number
            ? dEl.GetDouble()
            : 0;

        return new TrackItem
        {
            Id = id,
            Source = Name,
            BrandKey = BrandKey,
            BrandColor = BrandColorHex,
            Title = title,
            Artist = artist,
            PageUrl = pageUrl,
            CoverUrl = coverUrl,
            Duration = TimeSpan.FromMilliseconds(durationMs)
        };
    }

    private static CachedScMedia ExtractScMedia(JsonElement root)
    {
        string? trackAuth = root.TryGetProperty("track_authorization", out var authEl) && authEl.ValueKind == JsonValueKind.String
            ? authEl.GetString()
            : null;

        var list = new List<ScTranscoding>();
        if (root.TryGetProperty("media", out var mediaEl) &&
            mediaEl.TryGetProperty("transcodings", out var transcodings) &&
            transcodings.ValueKind == JsonValueKind.Array)
        {
            foreach (var tr in transcodings.EnumerateArray())
            {
                if (tr.TryGetProperty("snipped", out var snEl) && snEl.ValueKind == JsonValueKind.True)
                    continue;

                var trUrl = tr.TryGetProperty("url", out var uEl) ? uEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(trUrl)) continue;

                var preset = tr.TryGetProperty("preset", out var prEl) && prEl.ValueKind == JsonValueKind.String
                    ? (prEl.GetString() ?? "")
                    : "";
                if (string.Equals(preset, "abr_sq", StringComparison.OrdinalIgnoreCase))
                    continue;

                var protocol = tr.TryGetProperty("format", out var fmtEl) &&
                               fmtEl.TryGetProperty("protocol", out var pEl)
                    ? (pEl.GetString() ?? "")
                    : "";
                if (protocol.Contains("encrypted", StringComparison.OrdinalIgnoreCase))
                    continue;

                int priority;
                if (string.Equals(protocol, "progressive", StringComparison.OrdinalIgnoreCase))
                {
                    priority = 1000;
                }
                else if (string.Equals(protocol, "hls", StringComparison.OrdinalIgnoreCase))
                {
                    if (string.Equals(preset, "aac_160k", StringComparison.OrdinalIgnoreCase))
                        priority = 800;
                    else if (preset.StartsWith("mp3", StringComparison.OrdinalIgnoreCase) ||
                             preset.StartsWith("opus", StringComparison.OrdinalIgnoreCase))
                        priority = 750;
                    else if (string.Equals(preset, "aac_96k", StringComparison.OrdinalIgnoreCase))
                        priority = 700;
                    else
                        priority = 500;
                }
                else
                {
                    continue;
                }

                list.Add(new ScTranscoding(trUrl, protocol, preset, priority));
            }
        }

        list.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        return new CachedScMedia(trackAuth, list);
    }

    private string BuildCacheFilePath(TrackItem track, string ext = ".m4a")
    {
        var rawKey = !string.IsNullOrWhiteSpace(track.Id)
            ? track.Id
            : Math.Abs(track.UniqueKey.GetHashCode()).ToString("X");
        var safe = new string(rawKey.Where(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_').ToArray());
        if (string.IsNullOrWhiteSpace(safe)) safe = "track";
        return Path.Combine(_scCacheDir, $"sc_v2_{safe}{ext}");
    }

    private string? TryGetCachedScAudio(TrackItem track)
    {
        try
        {
            foreach (var ext in new[] { ".m4a", ".mp3" })
            {
                var path = BuildCacheFilePath(track, ext);
                if (File.Exists(path) && new FileInfo(path).Length > 10_000)
                    return path;
            }
        }
        catch { }
        return null;
    }

    private void PruneOldScCache()
    {
        try
        {
            foreach (var f in Directory.EnumerateFiles(_scCacheDir))
            {
                try
                {
                    var name = Path.GetFileName(f);
                    if (!name.StartsWith("sc_v2_", StringComparison.OrdinalIgnoreCase) ||
                        DateTime.UtcNow - File.GetLastWriteTimeUtc(f) > TimeSpan.FromHours(2))
                    {
                        File.Delete(f);
                    }
                }
                catch { }
            }
        }
        catch { }
    }

    private async Task<string?> TryAssembleHlsToLocalM4aAsync(string m3u8Url, TrackItem track, CancellationToken ct)
    {
        string? partPath = null;
        try
        {
            var m3u8Text = await _http.GetStringAsync(m3u8Url, ct);
            if (m3u8Text.Contains("#EXT-X-KEY", StringComparison.OrdinalIgnoreCase))
                return null;

            var baseUri = new Uri(m3u8Url);
            var mapMatch = HlsMapRegex.Match(m3u8Text);
            bool hasInitMap = mapMatch.Success;
            var chunkUrls = new List<string>();
            if (hasInitMap)
                chunkUrls.Add(new Uri(baseUri, mapMatch.Groups[1].Value).AbsoluteUri);

            foreach (var rawLine in m3u8Text.Split('\n'))
            {
                var line = rawLine.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith('#')) continue;
                chunkUrls.Add(new Uri(baseUri, line).AbsoluteUri);
            }

            bool isMp3Hls = !hasInitMap &&
                chunkUrls.Count > 0 &&
                chunkUrls.Any(u => u.Contains(".mp3", StringComparison.OrdinalIgnoreCase) ||
                                   u.Contains("/mp3/", StringComparison.OrdinalIgnoreCase));

            if (hasInitMap && chunkUrls.Count <= 1) return null;
            if (!hasInitMap && !isMp3Hls) return null;

            PruneOldScCache();
            var destPath = BuildCacheFilePath(track, isMp3Hls ? ".mp3" : ".m4a");
            partPath = $"{destPath}.{Guid.NewGuid():N}.part";

            var results = new byte[chunkUrls.Count][];
            using var gate = new SemaphoreSlim(6, 6);
            var tasks = Enumerable.Range(0, chunkUrls.Count).Select(async idx =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    results[idx] = await _http.GetByteArrayAsync(chunkUrls[idx], ct);
                }
                finally
                {
                    gate.Release();
                }
            });

            await Task.WhenAll(tasks);

            for (int i = 0; i < results.Length; i++)
            {
                if (results[i] == null || results[i].Length == 0)
                    return null;
            }

            if (hasInitMap)
            {
                // Переупаковываем фрагментированный fMP4 в стандартный перематываемый .m4a (ftyp + moov + mdat)
                if (!Mp4FragmentRepacker.TryRepackToStandardM4a(results, partPath))
                {
                    await using var fs = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                    for (int i = 0; i < results.Length; i++)
                        await fs.WriteAsync(results[i], ct);
                }
            }
            else
            {
                await using var fs = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
                for (int i = 0; i < results.Length; i++)
                    await fs.WriteAsync(results[i], ct);
            }

            if (!File.Exists(partPath) || new FileInfo(partPath).Length <= 10_000)
                return null;

            if (File.Exists(destPath)) File.Delete(destPath);
            File.Move(partPath, destPath);
            partPath = null;
            return destPath;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            return null;
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(partPath) && File.Exists(partPath))
            {
                try { File.Delete(partPath); } catch { }
            }
        }
    }

    private async Task<string?> GetApiJsonWithRetryAsync(Func<string, string> buildUrl, CancellationToken ct)
    {
        var cid = await GetClientIdAsync(forceRefresh: false, ct);
        if (string.IsNullOrWhiteSpace(cid)) return null;

        try
        {
            using var resp = await _http.GetAsync(buildUrl(cid), ct);
            if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
            {
                InvalidateClientId(cid);
                cid = await GetClientIdAsync(forceRefresh: true, ct);
                if (string.IsNullOrWhiteSpace(cid)) return null;

                using var retryResp = await _http.GetAsync(buildUrl(cid), ct);
                if (!retryResp.IsSuccessStatusCode) return null;
                return await retryResp.Content.ReadAsStringAsync(ct);
            }

            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            return null;
        }
    }

    private void InvalidateClientId(string staleCid)
    {
        _invalidatedCids[staleCid] = 1;
        if (_cachedClientId == staleCid)
            _cachedClientId = null;

        try
        {
            if (File.Exists(_cidFilePath) && File.ReadAllText(_cidFilePath).Trim() == staleCid)
                File.Delete(_cidFilePath);
        }
        catch { }
    }

    private async Task<string?> GetClientIdAsync(bool forceRefresh, CancellationToken ct)
    {
        if (!forceRefresh && !string.IsNullOrWhiteSpace(_cachedClientId))
            return _cachedClientId;

        await _cidLock.WaitAsync(ct);
        try
        {
            if (!forceRefresh)
            {
                if (!string.IsNullOrWhiteSpace(_cachedClientId))
                    return _cachedClientId;

                try
                {
                    if (File.Exists(_cidFilePath))
                    {
                        var diskCid = File.ReadAllText(_cidFilePath).Trim();
                        if (diskCid.Length == 32 && !_invalidatedCids.ContainsKey(diskCid))
                        {
                            _cachedClientId = diskCid;
                            return diskCid;
                        }
                    }
                }
                catch { }

                var presetCid = FallbackClientIds.FirstOrDefault(f => !_invalidatedCids.ContainsKey(f));
                if (!string.IsNullOrWhiteSpace(presetCid))
                {
                    _cachedClientId = presetCid;
                    return presetCid;
                }
            }
            else
            {
                var nextPreset = FallbackClientIds.FirstOrDefault(f => !_invalidatedCids.ContainsKey(f));
                if (!string.IsNullOrWhiteSpace(nextPreset))
                {
                    _cachedClientId = nextPreset;
                    try { File.WriteAllText(_cidFilePath, nextPreset); } catch { }
                    return nextPreset;
                }
            }

            using var scrapeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            scrapeCts.CancelAfter(TimeSpan.FromSeconds(5));
            var sct = scrapeCts.Token;

            var html = await _http.GetStringAsync("https://soundcloud.com", sct);
            var scriptMatches = ScriptUrlRegex.Matches(html);
            for (int i = scriptMatches.Count - 1; i >= 0; i--)
            {
                sct.ThrowIfCancellationRequested();
                var scriptUrl = scriptMatches[i].Groups[1].Value;
                try
                {
                    var js = await _http.GetStringAsync(scriptUrl, sct);
                    var m = ClientIdRegex.Match(js);
                    if (m.Success)
                    {
                        var found = m.Groups[1].Value;
                        _invalidatedCids.TryRemove(found, out _);
                        _cachedClientId = found;
                        try { File.WriteAllText(_cidFilePath, found); } catch { }
                        return found;
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch { }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { }
        finally
        {
            _cidLock.Release();
        }

        return FallbackClientIds.FirstOrDefault();
    }

    public void Dispose()
    {
        _http.Dispose();
        _cidLock.Dispose();
    }
}
