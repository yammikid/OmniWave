using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace DynamicIsland.Services;

public class WebMusicProvider : IMusicProvider, IDisposable
{
    private const string BrandColorHex = "#22C55E";
    private static readonly string[] DirectAudioExtensions =
    {
        ".mp3", ".flac", ".ogg", ".wav", ".m4a", ".aac", ".opus", ".oga", ".weba"
    };

    private readonly HttpClient _http;
    private readonly YtDlpMusicProvider _ytDlpFallback;

    public string Name => "Интернет";
    public string BrandKey => "web";

    public WebMusicProvider()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("OmniWave/1.0 (+https://github.com/omniwave)");
        _ytDlpFallback = new YtDlpMusicProvider(Name, BrandKey, "ytsearch", BrandColorHex);
    }

    public static bool IsDirectAudioUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) return false;

        var path = uri.AbsolutePath.ToLowerInvariant();
        return DirectAudioExtensions.Any(ext => path.EndsWith(ext, StringComparison.Ordinal));
    }

    public async Task<List<TrackItem>> SearchAsync(string query, int limit = 8, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return new List<TrackItem>();
        var clean = query.Trim();

        var archiveTask = SearchArchiveOrgAsync(clean, limit, ct);
        var hearthisTask = SearchHearthisAsync(clean, limit, ct);
        await Task.WhenAll(archiveTask, hearthisTask);

        var a = await archiveTask;
        var h = await hearthisTask;

        var merged = new List<TrackItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int max = Math.Max(a.Count, h.Count);

        for (int i = 0; i < max && merged.Count < limit; i++)
        {
            if (i < h.Count && seen.Add(h[i].UniqueKey))
                merged.Add(h[i]);
            if (merged.Count >= limit) break;
            if (i < a.Count && seen.Add(a[i].UniqueKey))
                merged.Add(a[i]);
        }

        return merged;
    }

    private async Task<List<TrackItem>> SearchArchiveOrgAsync(string query, int limit, CancellationToken ct)
    {
        var results = new List<TrackItem>();
        try
        {
            var url = $"https://archive.org/advancedsearch.php?q={Uri.EscapeDataString(query)}+AND+mediatype:audio&fl[]=identifier,title,creator&rows={limit}&output=json";
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return results;

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("response", out var respObj) ||
                !respObj.TryGetProperty("docs", out var docs) ||
                docs.ValueKind != JsonValueKind.Array)
                return results;

            foreach (var el in docs.EnumerateArray())
            {
                var identifier = el.TryGetProperty("identifier", out var idEl) ? idEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(identifier)) continue;

                var title = ReadJsonStringOrFirstArrayItem(el, "title") ?? identifier;
                var creator = ReadJsonStringOrFirstArrayItem(el, "creator") ?? "Internet Archive";

                results.Add(new TrackItem
                {
                    Id = $"archive:{identifier}",
                    Source = Name,
                    BrandKey = BrandKey,
                    BrandColor = BrandColorHex,
                    Title = title.Trim(),
                    Artist = creator.Trim(),
                    PageUrl = $"https://archive.org/details/{identifier}",
                    CoverUrl = $"https://archive.org/services/img/{identifier}",
                    Duration = TimeSpan.Zero
                });
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        return results;
    }

    private async Task<List<TrackItem>> SearchHearthisAsync(string query, int limit, CancellationToken ct)
    {
        var results = new List<TrackItem>();
        try
        {
            var url = $"https://api-v2.hearthis.at/search/?t={Uri.EscapeDataString(query)}&count={limit}";
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return results;

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return results;

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var title = el.TryGetProperty("title", out var tEl) ? tEl.GetString() : null;
                var streamUrl = el.TryGetProperty("stream_url", out var sEl) ? sEl.GetString() : null;
                var pageUrl = el.TryGetProperty("permalink_url", out var pEl) ? pEl.GetString() : null;

                if (string.IsNullOrWhiteSpace(title) ||
                    string.IsNullOrWhiteSpace(streamUrl) ||
                    !streamUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    continue;

                string artist = Name;
                if (el.TryGetProperty("user", out var uObj) &&
                    uObj.ValueKind == JsonValueKind.Object &&
                    uObj.TryGetProperty("username", out var uEl) &&
                    !string.IsNullOrWhiteSpace(uEl.GetString()))
                {
                    artist = uEl.GetString()!;
                }

                string coverUrl = "";
                if (el.TryGetProperty("artwork_url", out var aEl) && aEl.ValueKind == JsonValueKind.String)
                    coverUrl = aEl.GetString() ?? "";
                if (string.IsNullOrWhiteSpace(coverUrl) &&
                    el.TryGetProperty("thumb", out var thEl) && thEl.ValueKind == JsonValueKind.String)
                    coverUrl = thEl.GetString() ?? "";

                double seconds = 0;
                if (el.TryGetProperty("duration", out var dEl))
                {
                    if (dEl.ValueKind == JsonValueKind.Number)
                        seconds = dEl.GetDouble();
                    else if (dEl.ValueKind == JsonValueKind.String)
                        double.TryParse(dEl.GetString(), NumberStyles.Any, CultureInfo.InvariantCulture, out seconds);
                }

                results.Add(new TrackItem
                {
                    Id = streamUrl,
                    Source = Name,
                    BrandKey = BrandKey,
                    BrandColor = BrandColorHex,
                    Title = title.Trim(),
                    Artist = artist.Trim(),
                    PageUrl = string.IsNullOrWhiteSpace(pageUrl) ? streamUrl : pageUrl,
                    CoverUrl = coverUrl,
                    Duration = seconds > 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero
                });
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        return results;
    }

    public async Task<string?> ResolveStreamUrlAsync(TrackItem track, CancellationToken ct = default)
    {
        if (IsDirectAudioUrl(track.PageUrl))
            return track.PageUrl;

        if (!string.IsNullOrWhiteSpace(track.Id) &&
            track.Id.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return track.Id;

        if (track.PageUrl.Contains("archive.org/details/", StringComparison.OrdinalIgnoreCase))
        {
            var resolved = await ResolveArchiveOrgStreamAsync(track, ct);
            if (!string.IsNullOrWhiteSpace(resolved))
                return resolved;
        }

        try
        {
            var ytDlpUrl = await _ytDlpFallback.ResolveStreamUrlAsync(track, ct);
            if (!string.IsNullOrWhiteSpace(ytDlpUrl))
                return ytDlpUrl;
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        if (track.PageUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
            track.PageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return track.PageUrl;
        }

        return null;
    }

    private async Task<string?> ResolveArchiveOrgStreamAsync(TrackItem track, CancellationToken ct)
    {
        try
        {
            var idx = track.PageUrl.IndexOf("archive.org/details/", StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return null;

            var identifier = track.PageUrl[(idx + "archive.org/details/".Length)..]
                .Split(new[] { '/', '?', '#' }, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(identifier)) return null;

            var metaUrl = $"https://archive.org/metadata/{Uri.EscapeDataString(identifier)}/files";
            using var resp = await _http.GetAsync(metaUrl, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("result", out var files) ||
                files.ValueKind != JsonValueKind.Array)
                return null;

            string? bestFile = null;
            int bestPriority = int.MaxValue;

            foreach (var f in files.EnumerateArray())
            {
                var name = f.TryGetProperty("name", out var nEl) ? nEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(name)) continue;

                var lower = name.ToLowerInvariant();
                int priority = Array.FindIndex(DirectAudioExtensions, ext => lower.EndsWith(ext, StringComparison.Ordinal));
                if (priority < 0) continue;

                if (priority < bestPriority)
                {
                    bestPriority = priority;
                    bestFile = name;
                    if (priority == 0) break;
                }
            }

            if (string.IsNullOrWhiteSpace(bestFile)) return null;

            var escapedPath = string.Join("/", bestFile.Split('/').Select(Uri.EscapeDataString));
            return $"https://archive.org/download/{Uri.EscapeDataString(identifier)}/{escapedPath}";
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private static string? ReadJsonStringOrFirstArrayItem(JsonElement el, string propName)
    {
        if (!el.TryGetProperty(propName, out var prop)) return null;
        if (prop.ValueKind == JsonValueKind.String) return prop.GetString();
        if (prop.ValueKind == JsonValueKind.Array && prop.GetArrayLength() > 0)
            return prop[0].GetString();
        return null;
    }

    public void Dispose() => _http.Dispose();
}
