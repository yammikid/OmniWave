using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace DynamicIsland.Services;

public class OpenCatalogProvider : IMusicProvider, IDisposable
{
    private const string BrandColorHex = "#22C55E";
    private static readonly string[] PreferredAudioExtensions =
    {
        ".mp3", ".ogg", ".flac", ".m4a", ".opus"
    };

    private readonly HttpClient _http;
    private readonly YtDlpMusicProvider _ytDlpFallback;

    public string Name => "Каталоги";
    public string BrandKey => "open_catalog";

    public OpenCatalogProvider()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("OmniWave/1.0 (+https://github.com/omniwave)");
        _ytDlpFallback = new YtDlpMusicProvider(Name, BrandKey, "ytsearch", BrandColorHex);
    }

    public async Task<List<TrackItem>> SearchAsync(string query, int limit = 8, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) return new List<TrackItem>();
        var clean = query.Trim();

        var archiveTask = SearchArchiveOrgAsync(clean, limit, ct);
        var hearthisTask = SearchHearthisAsync(clean, limit, ct);
        await Task.WhenAll(archiveTask, hearthisTask);

        var archiveItems = await archiveTask;
        var hearthisItems = await hearthisTask;

        var merged = new List<TrackItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int max = Math.Max(archiveItems.Count, hearthisItems.Count);

        for (int i = 0; i < max && merged.Count < limit; i++)
        {
            if (i < hearthisItems.Count && seen.Add(hearthisItems[i].UniqueKey))
                merged.Add(hearthisItems[i]);
            if (merged.Count >= limit) break;
            if (i < archiveItems.Count && seen.Add(archiveItems[i].UniqueKey))
                merged.Add(archiveItems[i]);
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
                    Source = "Archive.org",
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
        var urls = new[]
        {
            $"https://api-v2.hearthis.at/search/?q={Uri.EscapeDataString(query)}&type=tracks&page=1&count={limit}",
            $"https://api-v2.hearthis.at/search/?t={Uri.EscapeDataString(query)}&page=1&count={limit}"
        };

        foreach (var url in urls)
        {
            var parsed = await FetchHearthisArrayAsync(url, ct);
            if (parsed.Count > 0) return parsed;
        }

        return new List<TrackItem>();
    }

    private async Task<List<TrackItem>> FetchHearthisArrayAsync(string url, CancellationToken ct)
    {
        var results = new List<TrackItem>();
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return results;

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return results;

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var title = el.TryGetProperty("title", out var tEl) ? tEl.GetString() : null;
                var streamUrl = el.TryGetProperty("stream_url", out var sEl) ? sEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(streamUrl) && el.TryGetProperty("mp3_url", out var mp3El))
                    streamUrl = mp3El.GetString();

                var pageUrl = el.TryGetProperty("permalink_url", out var pEl) ? pEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(title) ||
                    string.IsNullOrWhiteSpace(streamUrl) ||
                    !streamUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    continue;

                string artist = "Hearthis";
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
                    Source = "Hearthis",
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
        if (WebMusicProvider.IsDirectAudioUrl(track.PageUrl))
            return track.PageUrl;

        if (!string.IsNullOrWhiteSpace(track.Id) &&
            track.Id.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return track.Id;

        if ((track.Id?.StartsWith("archive:", StringComparison.OrdinalIgnoreCase) ?? false) ||
            track.PageUrl.Contains("archive.org/details/", StringComparison.OrdinalIgnoreCase))
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

        return null;
    }

    private async Task<string?> ResolveArchiveOrgStreamAsync(TrackItem track, CancellationToken ct)
    {
        try
        {
            string? identifier = null;
            if (track.Id.StartsWith("archive:", StringComparison.OrdinalIgnoreCase))
                identifier = track.Id["archive:".Length..].Trim();

            if (string.IsNullOrWhiteSpace(identifier))
            {
                var idx = track.PageUrl.IndexOf("archive.org/details/", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    identifier = track.PageUrl[(idx + "archive.org/details/".Length)..]
                        .Split(new[] { '/', '?', '#' }, StringSplitOptions.RemoveEmptyEntries)
                        .FirstOrDefault();
                }
            }
            if (string.IsNullOrWhiteSpace(identifier)) return null;

            var metaUrl = $"https://archive.org/metadata/{Uri.EscapeDataString(identifier)}";
            using var resp = await _http.GetAsync(metaUrl, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("files", out var files) ||
                files.ValueKind != JsonValueKind.Array)
                return null;

            string? bestFile = null;
            int bestScore = int.MaxValue;

            foreach (var f in files.EnumerateArray())
            {
                var name = f.TryGetProperty("name", out var nEl) ? nEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(name)) continue;

                var lower = name.ToLowerInvariant();
                int priority = Array.FindIndex(PreferredAudioExtensions, ext => lower.EndsWith(ext, StringComparison.Ordinal));
                if (priority < 0) continue;

                int penalty = lower.Contains("_64kb", StringComparison.Ordinal) ? 10 : 0;
                int score = priority + penalty;
                if (score < bestScore)
                {
                    bestScore = score;
                    bestFile = name;
                    if (score == 0) break;
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
