using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DynamicIsland.Services;

public class BandcampProvider : IMusicProvider, IDisposable
{
    private const string BrandColorHex = "#1DA0C3";

    private static readonly Regex SearchResultRegex = new(
        @"<li\s+class=""searchresult[^""]*""[^>]*>(.*?)</li>\s*(?=<li\s+class=""searchresult|</ul>)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex ArtImgRegex = new(
        @"<div\s+class=""art""[^>]*>.*?<img[^>]+src=""([^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex HeadingRegex = new(
        @"<div\s+class=""heading""[^>]*>\s*<a[^>]+href=""([^""]+)""[^>]*>(.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex SubheadRegex = new(
        @"<div\s+class=""subhead""[^>]*>(.*?)</div>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex DataTralbumRegex = new(
        @"data-tralbum=""([^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex StreamFallbackRegex = new(
        @"""mp3-(?:320|128)""\s*:\s*""(https?:[^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex HtmlTagRegex = new(
        @"<[^>]+>",
        RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly YtDlpMusicProvider _ytDlpFallback;

    public string Name => "Bandcamp";
    public string BrandKey => "bandcamp";

    public BandcampProvider()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        _ytDlpFallback = new YtDlpMusicProvider(Name, BrandKey, "ytsearch", BrandColorHex);
    }

    public async Task<List<TrackItem>> SearchAsync(string query, int limit = 8, CancellationToken ct = default)
    {
        var results = new List<TrackItem>();
        if (string.IsNullOrWhiteSpace(query)) return results;

        var clean = query.Trim();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await FetchSearchTypeAsync(clean, "t", limit, results, seen, ct);
        if (results.Count < limit)
            await FetchSearchTypeAsync(clean, "a", limit, results, seen, ct);

        return results;
    }

    private async Task FetchSearchTypeAsync(
        string query,
        string itemType,
        int limit,
        List<TrackItem> results,
        HashSet<string> seen,
        CancellationToken ct)
    {
        try
        {
            var url = $"https://bandcamp.com/search?q={Uri.EscapeDataString(query)}&item_type={itemType}";
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return;

            var html = await resp.Content.ReadAsStringAsync(ct);
            foreach (Match m in SearchResultRegex.Matches(html))
            {
                if (results.Count >= limit) break;
                var block = m.Groups[1].Value;

                var headingMatch = HeadingRegex.Match(block);
                if (!headingMatch.Success) continue;

                var rawUrl = WebUtility.HtmlDecode(headingMatch.Groups[1].Value).Trim();
                var cleanUrl = StripQuery(rawUrl);
                if (string.IsNullOrWhiteSpace(cleanUrl) ||
                    !cleanUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    continue;

                var title = CleanHtmlText(headingMatch.Groups[2].Value);
                if (string.IsNullOrWhiteSpace(title)) continue;

                string coverUrl = "";
                var artMatch = ArtImgRegex.Match(block);
                if (artMatch.Success)
                    coverUrl = WebUtility.HtmlDecode(artMatch.Groups[1].Value).Trim();

                string artist = Name;
                var subMatch = SubheadRegex.Match(block);
                if (subMatch.Success)
                {
                    var sub = CleanHtmlText(subMatch.Groups[1].Value);
                    var byIdx = sub.IndexOf("by ", StringComparison.OrdinalIgnoreCase);
                    var parsedArtist = byIdx >= 0 ? sub[(byIdx + 3)..].Trim() : sub.Trim();
                    if (!string.IsNullOrWhiteSpace(parsedArtist))
                        artist = parsedArtist;
                }

                var track = new TrackItem
                {
                    Id = cleanUrl,
                    Source = Name,
                    BrandKey = BrandKey,
                    BrandColor = BrandColorHex,
                    Title = title,
                    Artist = artist,
                    PageUrl = cleanUrl,
                    CoverUrl = coverUrl,
                    Duration = TimeSpan.Zero
                };

                if (seen.Add(track.UniqueKey))
                    results.Add(track);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
    }

    public async Task<string?> ResolveStreamUrlAsync(TrackItem track, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(track.PageUrl))
            return null;

        try
        {
            using var resp = await _http.GetAsync(track.PageUrl, ct);
            if (resp.IsSuccessStatusCode)
            {
                var html = await resp.Content.ReadAsStringAsync(ct);

                var tralbumMatch = DataTralbumRegex.Match(html);
                if (tralbumMatch.Success)
                {
                    var json = WebUtility.HtmlDecode(tralbumMatch.Groups[1].Value);
                    var fromJson = ExtractStreamFromTralbumJson(json);
                    if (!string.IsNullOrWhiteSpace(fromJson))
                        return fromJson;
                }

                var directMatch = StreamFallbackRegex.Match(html);
                if (directMatch.Success)
                {
                    var raw = directMatch.Groups[1].Value.Replace(@"\/", "/");
                    if (raw.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                        return raw;
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }

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

    private static string? ExtractStreamFromTralbumJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("trackinfo", out var tracksArr) ||
                tracksArr.ValueKind != JsonValueKind.Array)
                return null;

            foreach (var t in tracksArr.EnumerateArray())
            {
                if (!t.TryGetProperty("file", out var fileObj) || fileObj.ValueKind != JsonValueKind.Object)
                    continue;

                if (fileObj.TryGetProperty("mp3-320", out var mp3High) && mp3High.ValueKind == JsonValueKind.String)
                {
                    var url = mp3High.GetString();
                    if (!string.IsNullOrWhiteSpace(url)) return url;
                }

                if (fileObj.TryGetProperty("mp3-128", out var mp3Std) && mp3Std.ValueKind == JsonValueKind.String)
                {
                    var url = mp3Std.GetString();
                    if (!string.IsNullOrWhiteSpace(url)) return url;
                }

                foreach (var prop in fileObj.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var url = prop.Value.GetString();
                        if (!string.IsNullOrWhiteSpace(url) &&
                            url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                            return url;
                    }
                }
            }
        }
        catch { }
        return null;
    }

    private static string StripQuery(string url)
    {
        var idx = url.IndexOf('?');
        return idx > 0 ? url[..idx] : url;
    }

    private static string CleanHtmlText(string raw)
    {
        var withoutTags = HtmlTagRegex.Replace(raw, " ");
        var decoded = WebUtility.HtmlDecode(withoutTags);
        return Regex.Replace(decoded, @"\s+", " ").Trim();
    }

    public void Dispose() => _http.Dispose();
}
