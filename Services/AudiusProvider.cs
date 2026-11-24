using System.Net.Http;
using System.Text.Json;

namespace DynamicIsland.Services;

public class AudiusProvider : IMusicProvider, IDisposable
{
    private const string BrandColorHex = "#CC0FF0";
    private const string DefaultNode = "https://discoveryprovider.audius.co";
    private const string AppName = "OmniWave";

    private readonly HttpClient _http;
    private readonly YtDlpMusicProvider _ytDlpFallback;
    private readonly SemaphoreSlim _nodeLock = new(1, 1);

    private string _cachedNode = DefaultNode;
    private DateTime _nodeCachedUntilUtc = DateTime.MinValue;

    public string Name => "Audius";
    public string BrandKey => "audius";

    public AudiusProvider()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("OmniWave/1.0");
        _ytDlpFallback = new YtDlpMusicProvider(Name, BrandKey, "ytsearch", BrandColorHex);
    }

    private async Task<string> GetDiscoveryNodeAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow < _nodeCachedUntilUtc && !string.IsNullOrWhiteSpace(_cachedNode))
            return _cachedNode;

        await _nodeLock.WaitAsync(ct);
        try
        {
            if (DateTime.UtcNow < _nodeCachedUntilUtc && !string.IsNullOrWhiteSpace(_cachedNode))
                return _cachedNode;

            using var resp = await _http.GetAsync("https://api.audius.co", ct);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("data", out var dataArr) &&
                    dataArr.ValueKind == JsonValueKind.Array)
                {
                    var nodes = dataArr.EnumerateArray()
                        .Where(e => e.ValueKind == JsonValueKind.String)
                        .Select(e => e.GetString()?.Trim().TrimEnd('/'))
                        .Where(u => !string.IsNullOrWhiteSpace(u) &&
                                    u!.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (nodes.Count > 0)
                    {
                        _cachedNode = nodes[Random.Shared.Next(nodes.Count)]!;
                        _nodeCachedUntilUtc = DateTime.UtcNow.AddMinutes(30);
                        return _cachedNode;
                    }
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        finally
        {
            _nodeLock.Release();
        }

        _cachedNode = DefaultNode;
        _nodeCachedUntilUtc = DateTime.UtcNow.AddMinutes(5);
        return _cachedNode;
    }

    public async Task<List<TrackItem>> SearchAsync(string query, int limit = 8, CancellationToken ct = default)
    {
        var results = new List<TrackItem>();
        if (string.IsNullOrWhiteSpace(query)) return results;

        try
        {
            var node = await GetDiscoveryNodeAsync(ct);
            var url = $"{node}/v1/tracks/search?query={Uri.EscapeDataString(query.Trim())}&limit={limit}&app_name={AppName}";
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return results;

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("data", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return results;

            foreach (var el in arr.EnumerateArray())
            {
                var id = ReadString(el, "id");
                var title = ReadString(el, "title");
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
                    continue;

                string artist = Name;
                if (el.TryGetProperty("user", out var userObj) && userObj.ValueKind == JsonValueKind.Object)
                {
                    artist = ReadString(userObj, "name")
                             ?? ReadString(userObj, "handle")
                             ?? Name;
                }

                string coverUrl = "";
                if (el.TryGetProperty("artwork", out var artObj) && artObj.ValueKind == JsonValueKind.Object)
                {
                    coverUrl = ReadString(artObj, "480x480")
                               ?? ReadString(artObj, "150x150")
                               ?? ReadString(artObj, "1000x1000")
                               ?? "";
                }

                var permalink = ReadString(el, "permalink");
                string pageUrl = string.IsNullOrWhiteSpace(permalink)
                    ? $"https://audius.co/tracks/{Uri.EscapeDataString(id)}"
                    : (permalink.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                        ? permalink
                        : $"https://audius.co{(permalink.StartsWith('/') ? "" : "/")}{permalink}");

                double sec = el.TryGetProperty("duration", out var dEl) && dEl.ValueKind == JsonValueKind.Number
                    ? dEl.GetDouble()
                    : 0;

                results.Add(new TrackItem
                {
                    Id = id,
                    Source = Name,
                    BrandKey = BrandKey,
                    BrandColor = BrandColorHex,
                    Title = title.Trim(),
                    Artist = artist.Trim(),
                    PageUrl = pageUrl,
                    CoverUrl = coverUrl,
                    Duration = sec > 0 ? TimeSpan.FromSeconds(sec) : TimeSpan.Zero
                });
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        return results;
    }

    public async Task<string?> ResolveStreamUrlAsync(TrackItem track, CancellationToken ct = default)
    {
        try
        {
            var node = await GetDiscoveryNodeAsync(ct);
            var id = track.Id?.Trim();

            if (string.IsNullOrWhiteSpace(id) &&
                !string.IsNullOrWhiteSpace(track.PageUrl) &&
                track.PageUrl.Contains("audius.co", StringComparison.OrdinalIgnoreCase))
            {
                id = await ResolveTrackIdFromUrlAsync(node, track.PageUrl, ct);
            }

            if (!string.IsNullOrWhiteSpace(id))
            {
                var streamUrl = $"{node}/v1/tracks/{Uri.EscapeDataString(id)}/stream?app_name={AppName}";
                using var req = new HttpRequestMessage(HttpMethod.Get, streamUrl);
                using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (resp.IsSuccessStatusCode)
                    return resp.RequestMessage?.RequestUri?.ToString() ?? streamUrl;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        if (!string.IsNullOrWhiteSpace(track.PageUrl))
        {
            try
            {
                var ytDlpUrl = await _ytDlpFallback.ResolveStreamUrlAsync(track, ct);
                if (!string.IsNullOrWhiteSpace(ytDlpUrl))
                    return ytDlpUrl;
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }

        return null;
    }

    private async Task<string?> ResolveTrackIdFromUrlAsync(string node, string pageUrl, CancellationToken ct)
    {
        try
        {
            var resolveUrl = $"{node}/v1/resolve?url={Uri.EscapeDataString(pageUrl)}&app_name={AppName}";
            using var resp = await _http.GetAsync(resolveUrl, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
                return ReadString(data, "id");
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        return null;
    }

    private static string? ReadString(JsonElement el, string prop)
    {
        if (el.TryGetProperty(prop, out var val) && val.ValueKind == JsonValueKind.String)
        {
            var s = val.GetString();
            if (!string.IsNullOrWhiteSpace(s)) return s;
        }
        return null;
    }

    public void Dispose() => _http.Dispose();
}
