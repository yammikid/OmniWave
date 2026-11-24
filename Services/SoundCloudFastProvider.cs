using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DynamicIsland.Services;

public class SoundCloudFastProvider : IMusicProvider, IDisposable
{
    private const string BrandColorHex = "#FF5500";
    private static readonly Regex ScriptUrlRegex = new(
        @"<script[^>]+src=[""'](https://a-v2\.sndcdn\.com/assets/[^""']+\.js)[""']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ClientIdRegex = new(
        @"client_id\s*[:=]\s*[""']([a-zA-Z0-9]{32})[""']",
        RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly YtDlpMusicProvider _fallback;
    private readonly SemaphoreSlim _cidLock = new(1, 1);
    private readonly string _cidFilePath;
    private string? _cachedClientId;

    public string Name => "SoundCloud";
    public string BrandKey => "soundcloud";

    public SoundCloudFastProvider()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");

        _fallback = new YtDlpMusicProvider(Name, BrandKey, "scsearch", BrandColorHex);

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OmniWave");
        Directory.CreateDirectory(dir);
        _cidFilePath = Path.Combine(dir, "sc_client_id.txt");
    }

    public async Task<List<TrackItem>> SearchAsync(string query, int limit = 8, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return new List<TrackItem>();

        try
        {
            var json = await GetApiJsonWithRetryAsync(
                cid => $"https://api-v2.soundcloud.com/search/tracks?q={Uri.EscapeDataString(query.Trim())}&client_id={cid}&limit={limit}&offset=0",
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
                            items.Add(track);
                    }

                    if (items.Count > 0)
                        return items;
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        return await _fallback.SearchAsync(query, limit, ct);
    }

    public async Task<string?> ResolveStreamUrlAsync(TrackItem track, CancellationToken ct = default)
    {
        try
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

                var parsed = ParseTrackElement(root);
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

                string? trackAuth = root.TryGetProperty("track_authorization", out var authEl) && authEl.ValueKind == JsonValueKind.String
                    ? authEl.GetString()
                    : null;

                if (root.TryGetProperty("media", out var mediaEl) &&
                    mediaEl.TryGetProperty("transcodings", out var transcodings) &&
                    transcodings.ValueKind == JsonValueKind.Array)
                {
                    string? progressiveUrl = null;
                    string? hlsUrl = null;

                    foreach (var tr in transcodings.EnumerateArray())
                    {
                        var trUrl = tr.TryGetProperty("url", out var uEl) ? uEl.GetString() : null;
                        if (string.IsNullOrWhiteSpace(trUrl)) continue;

                        var protocol = tr.TryGetProperty("format", out var fmtEl) &&
                                       fmtEl.TryGetProperty("protocol", out var pEl)
                            ? pEl.GetString()
                            : null;

                        if (string.Equals(protocol, "progressive", StringComparison.OrdinalIgnoreCase))
                        {
                            progressiveUrl ??= trUrl;
                        }
                        else if (string.Equals(protocol, "hls", StringComparison.OrdinalIgnoreCase))
                        {
                            hlsUrl ??= trUrl;
                        }
                    }

                    var chosenTranscoding = progressiveUrl ?? hlsUrl;
                    if (!string.IsNullOrWhiteSpace(chosenTranscoding))
                    {
                        var streamJson = await GetApiJsonWithRetryAsync(cid =>
                        {
                            var sep = chosenTranscoding.Contains('?') ? "&" : "?";
                            var url = $"{chosenTranscoding}{sep}client_id={cid}";
                            if (!string.IsNullOrWhiteSpace(trackAuth))
                                url += $"&track_authorization={Uri.EscapeDataString(trackAuth)}";
                            return url;
                        }, ct);

                        if (!string.IsNullOrWhiteSpace(streamJson))
                        {
                            using var streamDoc = JsonDocument.Parse(streamJson);
                            if (streamDoc.RootElement.TryGetProperty("url", out var finalUrlEl) &&
                                finalUrlEl.ValueKind == JsonValueKind.String)
                            {
                                var finalUrl = finalUrlEl.GetString();
                                if (!string.IsNullOrWhiteSpace(finalUrl))
                                    return finalUrl;
                            }
                        }
                    }
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        return await _fallback.ResolveStreamUrlAsync(track, ct);
    }

    private TrackItem? ParseTrackElement(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;

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

        string pageUrl = el.TryGetProperty("permalink_url", out var pEl) && pEl.ValueKind == JsonValueKind.String
            ? (pEl.GetString() ?? "")
            : "";
        if (string.IsNullOrWhiteSpace(pageUrl)) return null;

        string coverUrl = el.TryGetProperty("artwork_url", out var artEl) && artEl.ValueKind == JsonValueKind.String
            ? (artEl.GetString() ?? "")
            : "";
        if (string.IsNullOrWhiteSpace(coverUrl))
            coverUrl = avatarUrl;
        if (!string.IsNullOrWhiteSpace(coverUrl))
            coverUrl = coverUrl.Replace("-large.", "-t500x500.", StringComparison.OrdinalIgnoreCase);

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

    private async Task<string?> GetApiJsonWithRetryAsync(Func<string, string> buildUrl, CancellationToken ct)
    {
        var cid = await GetClientIdAsync(forceRefresh: false, ct);
        if (string.IsNullOrWhiteSpace(cid)) return null;

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

    private void InvalidateClientId(string staleCid)
    {
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
                        if (diskCid.Length == 32)
                        {
                            _cachedClientId = diskCid;
                            return diskCid;
                        }
                    }
                }
                catch { }
            }

            var html = await _http.GetStringAsync("https://soundcloud.com", ct);
            var scriptMatches = ScriptUrlRegex.Matches(html);
            for (int i = scriptMatches.Count - 1; i >= 0; i--)
            {
                ct.ThrowIfCancellationRequested();
                var scriptUrl = scriptMatches[i].Groups[1].Value;
                try
                {
                    var js = await _http.GetStringAsync(scriptUrl, ct);
                    var m = ClientIdRegex.Match(js);
                    if (m.Success)
                    {
                        var found = m.Groups[1].Value;
                        _cachedClientId = found;
                        try { File.WriteAllText(_cidFilePath, found); } catch { }
                        return found;
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        finally
        {
            _cidLock.Release();
        }

        return null;
    }

    public void Dispose()
    {
        _http.Dispose();
        _cidLock.Dispose();
    }
}
