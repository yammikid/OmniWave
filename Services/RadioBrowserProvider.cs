using System.Net.Http;
using System.Text.Json;

namespace DynamicIsland.Services;

public class RadioBrowserProvider : IMusicProvider, IDisposable
{
    private const string BrandColorHex = "#F59E0B";
    private const string DefaultMirror = "https://de1.api.radio-browser.info";

    private static readonly HashSet<string> GenreTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "lofi", "synthwave", "phonk", "rock", "dnb", "ambient", "jazz", "hiphop"
    };

    private readonly HttpClient _http;
    private readonly SemaphoreSlim _mirrorLock = new(1, 1);

    private string _cachedMirror = DefaultMirror;
    private DateTime _mirrorCachedUntilUtc = DateTime.MinValue;

    public string Name => "Радио";
    public string BrandKey => "radio";

    public RadioBrowserProvider()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("OmniWave/1.0 (+https://github.com/omniwave)");
    }

    private async Task<string> GetMirrorBaseUrlAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow < _mirrorCachedUntilUtc && !string.IsNullOrWhiteSpace(_cachedMirror))
            return _cachedMirror;

        await _mirrorLock.WaitAsync(ct);
        try
        {
            if (DateTime.UtcNow < _mirrorCachedUntilUtc && !string.IsNullOrWhiteSpace(_cachedMirror))
                return _cachedMirror;

            using var resp = await _http.GetAsync("https://all.api.radio-browser.info/json/servers", ct);
            if (resp.IsSuccessStatusCode)
            {
                var json = await resp.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind == JsonValueKind.Array)
                {
                    var hosts = doc.RootElement.EnumerateArray()
                        .Select(e => e.TryGetProperty("name", out var nEl) ? nEl.GetString()?.Trim() : null)
                        .Where(h => !string.IsNullOrWhiteSpace(h))
                        .ToList();

                    if (hosts.Count > 0)
                    {
                        _cachedMirror = $"https://{hosts[Random.Shared.Next(hosts.Count)]}";
                        _mirrorCachedUntilUtc = DateTime.UtcNow.AddMinutes(30);
                        return _cachedMirror;
                    }
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }
        finally
        {
            _mirrorLock.Release();
        }

        _cachedMirror = DefaultMirror;
        _mirrorCachedUntilUtc = DateTime.UtcNow.AddMinutes(5);
        return _cachedMirror;
    }

    public async Task<List<TrackItem>> SearchAsync(string query, int limit = 8, CancellationToken ct = default)
    {
        var results = new List<TrackItem>();
        var clean = (query ?? "").Trim();

        try
        {
            var baseUrl = await GetMirrorBaseUrlAsync(ct);
            string url;
            if (string.IsNullOrWhiteSpace(clean))
            {
                url = $"{baseUrl}/json/stations/bytagexact/lofi?limit={limit}&hidebroken=true&order=clickcount&reverse=true";
            }
            else if (GenreTags.Contains(clean))
            {
                url = $"{baseUrl}/json/stations/bytagexact/{Uri.EscapeDataString(clean.ToLowerInvariant())}?limit={limit}&hidebroken=true&order=clickcount&reverse=true";
            }
            else
            {
                url = $"{baseUrl}/json/stations/search?name={Uri.EscapeDataString(clean)}&limit={limit}&hidebroken=true&order=clickcount&reverse=true";
            }

            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return results;

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return results;

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var name = ReadString(el, "name");
                var streamUrl = ReadString(el, "url_resolved") ?? ReadString(el, "url");
                if (string.IsNullOrWhiteSpace(name) ||
                    string.IsNullOrWhiteSpace(streamUrl) ||
                    !streamUrl.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    continue;

                var stationId = ReadString(el, "stationuuid") ?? streamUrl;
                var country = ReadString(el, "country") ?? "";
                var tags = ReadString(el, "tags") ?? "";
                var favicon = ReadString(el, "favicon") ?? "";
                if (!favicon.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                    favicon = "";

                string artist;
                if (string.IsNullOrWhiteSpace(country) && string.IsNullOrWhiteSpace(tags))
                    artist = "Интернет-радио";
                else if (string.IsNullOrWhiteSpace(country))
                    artist = tags;
                else if (string.IsNullOrWhiteSpace(tags))
                    artist = country;
                else
                    artist = $"{country} • {tags}";

                results.Add(new TrackItem
                {
                    Id = stationId,
                    Source = Name,
                    BrandKey = BrandKey,
                    BrandColor = BrandColorHex,
                    Title = name.Trim(),
                    Artist = artist.Trim(),
                    PageUrl = streamUrl,
                    CoverUrl = favicon,
                    Duration = TimeSpan.Zero
                });
            }
        }
        catch (OperationCanceledException) { throw; }
        catch { }

        return results;
    }

    public Task<string?> ResolveStreamUrlAsync(TrackItem track, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(track.PageUrl) &&
            (track.PageUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
             track.PageUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
        {
            return Task.FromResult<string?>(track.PageUrl);
        }

        return Task.FromResult<string?>(null);
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
