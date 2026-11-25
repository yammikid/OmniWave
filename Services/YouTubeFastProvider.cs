using System.Globalization;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DynamicIsland.Services;

public class YouTubeFastProvider : IMusicProvider, IDisposable
{
    private const string BrandColorHex = "#FF0033";
    private const string SearchApiUrl = "https://www.youtube.com/youtubei/v1/search?prettyPrint=false";
    private const string PlayerApiUrl = "https://www.youtube.com/youtubei/v1/player?prettyPrint=false";
    private const string ChromeUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private static readonly Regex VideoIdRegex = new(
        @"(?:v=|youtu\.be/|youtube\.com/(?:shorts|embed)/)([a-zA-Z0-9_-]{11})",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly YtDlpMusicProvider _fallback;

    public string Name => "YouTube";
    public string BrandKey => "youtube";
    public string? LastResolveError { get; private set; }

    public YouTubeFastProvider()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(18) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(ChromeUserAgent);

        _fallback = new YtDlpMusicProvider(Name, BrandKey, "ytsearch", BrandColorHex);
    }

    public static string? ExtractVideoId(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var m = VideoIdRegex.Match(url);
        return m.Success ? m.Groups[1].Value : null;
    }

    public async Task<List<TrackItem>> SearchAsync(string query, int limit = 8, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return new List<TrackItem>();

        try
        {
            var results = new List<TrackItem>();
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? continuation = null;

            for (int page = 0; page < 3 && results.Count < limit; page++)
            {
                string gl = ProxyService.IsActive ? "US" : "RU";
                var payload = page == 0
                    ? (object)new
                    {
                        context = new
                        {
                            client = new
                            {
                                clientName = "WEB",
                                clientVersion = "2.20261101.00.00",
                                hl = "ru",
                                gl
                            }
                        },
                        query = query.Trim()
                    }
                    : new
                    {
                        context = new
                        {
                            client = new
                            {
                                clientName = "WEB",
                                clientVersion = "2.20261101.00.00",
                                hl = "ru",
                                gl
                            }
                        },
                        continuation
                    };

                var bodyJson = JsonSerializer.Serialize(payload);
                using var content = new StringContent(bodyJson, Encoding.UTF8, "application/json");
                using var resp = await _http.PostAsync(SearchApiUrl, content, ct);
                if (!resp.IsSuccessStatusCode) break;

                var respJson = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(respJson);

                string? nextContinuation = null;
                CollectVideoRenderers(doc.RootElement, results, seenIds, limit, ref nextContinuation);

                if (string.IsNullOrWhiteSpace(nextContinuation) || results.Count >= limit)
                    break;
                continuation = nextContinuation;
            }

            if (results.Count > 0)
                return results;
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        return await _fallback.SearchAsync(query, limit, ct);
    }

    public async Task<string?> ResolveStreamUrlAsync(TrackItem track, CancellationToken ct = default)
    {
        LastResolveError = null;
        var videoId = !string.IsNullOrWhiteSpace(track.Id)
            ? track.Id
            : ExtractVideoId(track.PageUrl);

        if (!string.IsNullOrWhiteSpace(videoId))
        {
            try
            {
                var url = await TryPlayerEndpointAsync(videoId, track, useAndroidVr: true, ct)
                          ?? await TryPlayerEndpointAsync(videoId, track, useAndroidVr: false, ct);
                if (!string.IsNullOrWhiteSpace(url))
                {
                    LastResolveError = null;
                    return url;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }

        var fallbackUrl = await _fallback.ResolveStreamUrlAsync(track, ct);
        if (!string.IsNullOrWhiteSpace(fallbackUrl))
        {
            LastResolveError = null;
            ProxyService.RegisterStreamHeaders(fallbackUrl, ChromeUserAgent, "https://www.youtube.com/");
            return fallbackUrl;
        }

        var ytErr = YtDlpMusicProvider.LastError;
        if (!string.IsNullOrWhiteSpace(ytErr))
        {
            if (ytErr.Contains("Sign in to confirm", StringComparison.OrdinalIgnoreCase) ||
                ytErr.Contains("not a bot", StringComparison.OrdinalIgnoreCase) ||
                ytErr.Contains("LOGIN_REQUIRED", StringComparison.OrdinalIgnoreCase))
            {
                LastResolveError = "YouTube запросил подтверждение входа для IP этого прокси (войдите в YouTube в «Аккаунтах» или смените прокси).";
            }
            else if (string.IsNullOrWhiteSpace(LastResolveError))
            {
                LastResolveError = ytErr;
            }
        }

        return null;
    }

    private async Task<string?> TryPlayerEndpointAsync(
        string videoId,
        TrackItem track,
        bool useAndroidVr,
        CancellationToken ct)
    {
        string gl = ProxyService.IsActive ? "US" : "RU";
        object payload = useAndroidVr
            ? new
            {
                videoId,
                context = new
                {
                    client = new
                    {
                        clientName = "ANDROID_VR",
                        clientVersion = "1.65.10",
                        androidSdkVersion = 32,
                        deviceMake = "Oculus",
                        deviceModel = "Quest 3",
                        hl = "ru",
                        gl
                    }
                }
            }
            : new
            {
                videoId,
                context = new
                {
                    client = new
                    {
                        clientName = "IOS",
                        clientVersion = "20.10.4",
                        deviceMake = "Apple",
                        deviceModel = "iPhone16,2",
                        osName = "iPhone",
                        osVersion = "18.3.2.22D82",
                        hl = "ru",
                        gl
                    }
                }
            };

        var jsonBody = JsonSerializer.Serialize(payload);
        string endpointUa = useAndroidVr
            ? "com.google.android.apps.youtube.vr.oculus/1.65.10 (Linux; U; Android 12L; Quest 3 Build/SQ3A.220605.009.A1) gzip"
            : "com.google.ios.youtube/20.10.4 (iPhone16,2; U; CPU iOS 18_3_2 like Mac OS X;)";

        using var req = new HttpRequestMessage(HttpMethod.Post, PlayerApiUrl)
        {
            Content = new StringContent(jsonBody, Encoding.UTF8, "application/json")
        };
        req.Headers.TryAddWithoutValidation("User-Agent", endpointUa);

        using var resp = await _http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode) return null;

        var respText = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(respText);
        var root = doc.RootElement;

        if (root.TryGetProperty("videoDetails", out var vd) && vd.ValueKind == JsonValueKind.Object)
        {
            if (string.IsNullOrWhiteSpace(track.Id))
                track.Id = videoId;
            if ((string.IsNullOrWhiteSpace(track.Title) || track.Title.StartsWith("http", StringComparison.OrdinalIgnoreCase)) &&
                vd.TryGetProperty("title", out var tEl) && tEl.ValueKind == JsonValueKind.String)
                track.Title = tEl.GetString() ?? track.Title;
            if ((string.IsNullOrWhiteSpace(track.Artist) || track.Artist == Name) &&
                vd.TryGetProperty("author", out var aEl) && aEl.ValueKind == JsonValueKind.String)
                track.Artist = aEl.GetString() ?? track.Artist;
            if (track.Duration <= TimeSpan.Zero &&
                vd.TryGetProperty("lengthSeconds", out var lenEl) &&
                int.TryParse(lenEl.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int secs) &&
                secs > 0)
                track.Duration = TimeSpan.FromSeconds(secs);
            if (string.IsNullOrWhiteSpace(track.CoverUrl) &&
                vd.TryGetProperty("thumbnail", out var thObj) &&
                thObj.TryGetProperty("thumbnails", out var thArr) &&
                thArr.ValueKind == JsonValueKind.Array && thArr.GetArrayLength() > 0)
            {
                var last = thArr[thArr.GetArrayLength() - 1];
                if (last.TryGetProperty("url", out var uEl))
                    track.CoverUrl = uEl.GetString() ?? "";
            }
        }

        if (root.TryGetProperty("playabilityStatus", out var ps) &&
            ps.TryGetProperty("status", out var stEl) &&
            !string.Equals(stEl.GetString(), "OK", StringComparison.OrdinalIgnoreCase))
        {
            var status = stEl.GetString() ?? "";
            var reason = ps.TryGetProperty("reason", out var rEl) ? (rEl.GetString() ?? "") : "";
            if (string.Equals(status, "LOGIN_REQUIRED", StringComparison.OrdinalIgnoreCase))
                LastResolveError = "YouTube запросил подтверждение входа для IP этого прокси (войдите в YouTube в «Аккаунтах» или смените прокси).";
            else if (!string.IsNullOrWhiteSpace(reason))
                LastResolveError = $"YouTube: {reason}";
            return null;
        }

        if (!root.TryGetProperty("streamingData", out var sd) || sd.ValueKind != JsonValueKind.Object)
            return null;

        string? bestMp4Url = null;
        long bestMp4Bitrate = -1;
        string? bestWebmUrl = null;
        long bestWebmBitrate = -1;

        if (sd.TryGetProperty("adaptiveFormats", out var adaptive) && adaptive.ValueKind == JsonValueKind.Array)
        {
            foreach (var fmt in adaptive.EnumerateArray())
            {
                if (!fmt.TryGetProperty("url", out var urlEl) || urlEl.ValueKind != JsonValueKind.String)
                    continue;
                var url = urlEl.GetString();
                if (string.IsNullOrWhiteSpace(url)) continue;

                var mime = fmt.TryGetProperty("mimeType", out var mEl) ? (mEl.GetString() ?? "") : "";
                long bitrate = fmt.TryGetProperty("bitrate", out var bEl) && bEl.ValueKind == JsonValueKind.Number
                    ? bEl.GetInt64()
                    : 0;

                if (mime.StartsWith("audio/mp4", StringComparison.OrdinalIgnoreCase))
                {
                    if (bitrate > bestMp4Bitrate)
                    {
                        bestMp4Bitrate = bitrate;
                        bestMp4Url = url;
                    }
                }
                else if (mime.StartsWith("audio/webm", StringComparison.OrdinalIgnoreCase))
                {
                    if (bitrate > bestWebmBitrate)
                    {
                        bestWebmBitrate = bitrate;
                        bestWebmUrl = url;
                    }
                }
            }
        }

        string? selectedUrl = null;
        if (!string.IsNullOrWhiteSpace(bestWebmUrl) && !string.IsNullOrWhiteSpace(bestMp4Url))
            selectedUrl = bestWebmBitrate > bestMp4Bitrate ? bestWebmUrl : bestMp4Url;
        else if (!string.IsNullOrWhiteSpace(bestWebmUrl))
            selectedUrl = bestWebmUrl;
        else if (!string.IsNullOrWhiteSpace(bestMp4Url))
            selectedUrl = bestMp4Url;

        if (string.IsNullOrWhiteSpace(selectedUrl) &&
            sd.TryGetProperty("formats", out var formats) &&
            formats.ValueKind == JsonValueKind.Array)
        {
            foreach (var fmt in formats.EnumerateArray())
            {
                if (fmt.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String)
                {
                    var url = urlEl.GetString();
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        selectedUrl = url;
                        break;
                    }
                }
            }
        }

        if (string.IsNullOrWhiteSpace(selectedUrl))
            return null;

        if (!await VerifyStreamReadableAsync(selectedUrl, endpointUa, ct))
            return null;

        ProxyService.RegisterStreamHeaders(selectedUrl, endpointUa, "https://www.youtube.com/");
        return selectedUrl;
    }

    private async Task<bool> VerifyStreamReadableAsync(string streamUrl, string userAgent, CancellationToken ct)
    {
        try
        {
            using var checkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            checkCts.CancelAfter(TimeSpan.FromSeconds(5));
            using var req = new HttpRequestMessage(HttpMethod.Get, streamUrl);
            req.Headers.TryAddWithoutValidation("User-Agent", userAgent);
            req.Headers.TryAddWithoutValidation("Referer", "https://www.youtube.com/");
            req.Headers.TryAddWithoutValidation("Range", "bytes=0-1");

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, checkCts.Token);
            int code = (int)resp.StatusCode;
            return resp.IsSuccessStatusCode || code == 206;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private void CollectVideoRenderers(
        JsonElement node,
        List<TrackItem> results,
        HashSet<string> seenIds,
        int limit,
        ref string? continuationToken)
    {
        if (results.Count >= limit) return;

        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("videoRenderer", out var vr) && vr.ValueKind == JsonValueKind.Object)
            {
                var track = ParseVideoRenderer(vr);
                if (track != null && seenIds.Add(track.Id))
                {
                    results.Add(track);
                    if (results.Count >= limit) return;
                }
            }

            if (continuationToken == null &&
                node.TryGetProperty("continuationCommand", out var cc) &&
                cc.ValueKind == JsonValueKind.Object &&
                cc.TryGetProperty("token", out var tokEl) &&
                tokEl.ValueKind == JsonValueKind.String)
            {
                continuationToken = tokEl.GetString();
            }

            foreach (var prop in node.EnumerateObject())
            {
                if (prop.Name == "videoRenderer") continue;
                CollectVideoRenderers(prop.Value, results, seenIds, limit, ref continuationToken);
                if (results.Count >= limit) return;
            }
        }
        else if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in node.EnumerateArray())
            {
                CollectVideoRenderers(el, results, seenIds, limit, ref continuationToken);
                if (results.Count >= limit) return;
            }
        }
    }

    private TrackItem? ParseVideoRenderer(JsonElement vr)
    {
        if (!vr.TryGetProperty("videoId", out var idEl) || idEl.ValueKind != JsonValueKind.String)
            return null;
        var videoId = idEl.GetString();
        if (string.IsNullOrWhiteSpace(videoId)) return null;

        string title = ReadTextRun(vr, "title");
        if (string.IsNullOrWhiteSpace(title)) title = "Без названия";

        string artist = ReadTextRun(vr, "ownerText");
        if (string.IsNullOrWhiteSpace(artist)) artist = ReadTextRun(vr, "longBylineText");
        if (string.IsNullOrWhiteSpace(artist)) artist = Name;

        var (cleanArtist, cleanTitle, _) = TrackMatcher.NormalizeArtistAndTitle(
            artist,
            title,
            publisherArtist: null,
            allowSplitArtistFromTitle: true,
            stripDisplayVideoNoise: true);
        if (!string.IsNullOrWhiteSpace(cleanTitle))
            title = cleanTitle;
        if (!string.IsNullOrWhiteSpace(cleanArtist))
            artist = cleanArtist;

        string lengthText = ReadTextRun(vr, "lengthText");
        var duration = ParseDurationText(lengthText);

        string coverUrl = "";
        if (vr.TryGetProperty("thumbnail", out var thObj) &&
            thObj.TryGetProperty("thumbnails", out var thArr) &&
            thArr.ValueKind == JsonValueKind.Array &&
            thArr.GetArrayLength() > 0)
        {
            var last = thArr[thArr.GetArrayLength() - 1];
            if (last.TryGetProperty("url", out var uEl))
                coverUrl = uEl.GetString() ?? "";
        }

        return new TrackItem
        {
            Id = videoId,
            Source = Name,
            BrandKey = BrandKey,
            BrandColor = BrandColorHex,
            Title = title,
            Artist = artist,
            PageUrl = $"https://www.youtube.com/watch?v={videoId}",
            CoverUrl = coverUrl,
            Duration = duration
        };
    }

    private static string ReadTextRun(JsonElement parent, string propName)
    {
        if (!parent.TryGetProperty(propName, out var obj) || obj.ValueKind != JsonValueKind.Object)
            return "";

        if (obj.TryGetProperty("runs", out var runs) && runs.ValueKind == JsonValueKind.Array)
        {
            var sb = new StringBuilder();
            foreach (var r in runs.EnumerateArray())
            {
                if (r.TryGetProperty("text", out var tEl) && tEl.ValueKind == JsonValueKind.String)
                    sb.Append(tEl.GetString());
            }
            if (sb.Length > 0) return sb.ToString();
        }

        if (obj.TryGetProperty("simpleText", out var sEl) && sEl.ValueKind == JsonValueKind.String)
            return sEl.GetString() ?? "";

        return "";
    }

    private static TimeSpan ParseDurationText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return TimeSpan.Zero;
        var parts = text.Trim().Split(':');
        if (parts.Length == 2 &&
            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int m) &&
            int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int s))
        {
            return new TimeSpan(0, m, s);
        }

        if (parts.Length == 3 &&
            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int h) &&
            int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int m3) &&
            int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int s3))
        {
            return new TimeSpan(h, m3, s3);
        }

        return TimeSpan.Zero;
    }

    public void Dispose() => _http.Dispose();
}
