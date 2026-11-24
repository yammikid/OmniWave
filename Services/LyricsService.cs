using System.Collections.Concurrent;
using System.ComponentModel;
using System.Globalization;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DynamicIsland.Services;

public class LyricLine : INotifyPropertyChanged
{
    private bool _isActive;

    public TimeSpan Timestamp { get; }
    public string Text { get; }

    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            OnPropertyChanged();
        }
    }

    public LyricLine(TimeSpan timestamp, string text)
    {
        Timestamp = timestamp;
        Text = text;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public class LyricsResult
{
    public List<LyricLine> SyncedLines { get; set; } = new();
    public string? PlainText { get; set; }
    public bool HasSynced => SyncedLines.Count > 0;
    public bool HasLyrics => HasSynced || !string.IsNullOrWhiteSpace(PlainText);
    public string SourceInfo { get; set; } = "";
}

public class LyricsService : IDisposable
{
    private static readonly Regex TagBracketRegex = new(
        @"(\[(?:[^\]]*(?:official|video|audio|lyric|lyrics|hd|4k|hq|mv|slowed|reverb|remix|sped\s*up|nightcore|live|explicit|visualizer|prod\.?)[^\]]*)\]|\((?:[^)]*(?:official|video|audio|lyric|lyrics|hd|4k|hq|mv|slowed|reverb|remix|sped\s*up|nightcore|live|explicit|visualizer|feat\.?|ft\.?|prod\.?)[^)]*)\))",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex FeatTailRegex = new(
        @"\s+(?:feat\.?|ft\.?)\s+.*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LrcTimestampRegex = new(
        @"\[(\d{1,2}):(\d{2})(?:[.:](\d{1,3}))?\]",
        RegexOptions.Compiled);

    private static readonly Regex MultiSpaceRegex = new(@"\s{2,}", RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, LyricsResult> _cache = new();

    public LyricsService()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("OmniWave/1.0 (+https://github.com/omniwave)");
    }

    public async Task<LyricsResult> GetLyricsAsync(TrackItem track, CancellationToken ct = default)
    {
        if (_cache.TryGetValue(track.UniqueKey, out var cached))
            return CloneResult(cached);

        var (artist, title) = ParseArtistAndTitle(track.Artist, track.Title);
        LyricsResult? result = null;

        if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(title))
        {
            result = await QueryDirectAsync(artist, title, track.Duration, ct);
        }

        if (result == null || !result.HasLyrics)
        {
            var query = string.IsNullOrWhiteSpace(artist) ? title : $"{artist} {title}";
            if (!string.IsNullOrWhiteSpace(query))
                result = await QuerySearchAsync(query, track.Duration, ct);
        }

        result ??= new LyricsResult { SourceInfo = "Текст не найден" };
        if (_cache.Count > 60) _cache.Clear();
        _cache[track.UniqueKey] = result;
        return CloneResult(result);
    }

    private async Task<LyricsResult?> QueryDirectAsync(string artist, string title, TimeSpan duration, CancellationToken ct)
    {
        try
        {
            var url = $"https://lrclib.net/api/get?artist_name={Uri.EscapeDataString(artist)}&track_name={Uri.EscapeDataString(title)}";
            if (duration.TotalSeconds > 10)
                url += $"&duration={(int)Math.Round(duration.TotalSeconds)}";

            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            return ParseLrcLibElement(doc.RootElement);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private async Task<LyricsResult?> QuerySearchAsync(string query, TimeSpan duration, CancellationToken ct)
    {
        try
        {
            var url = $"https://lrclib.net/api/search?q={Uri.EscapeDataString(query)}";
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return null;

            LyricsResult? bestSynced = null;
            double bestSyncedDiff = double.MaxValue;
            LyricsResult? bestPlain = null;
            double bestPlainDiff = double.MaxValue;

            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var candidate = ParseLrcLibElement(el);
                if (!candidate.HasLyrics) continue;

                double candDur = el.TryGetProperty("duration", out var dEl) && dEl.ValueKind == JsonValueKind.Number
                    ? dEl.GetDouble()
                    : 0;
                double diff = duration.TotalSeconds > 0 && candDur > 0
                    ? Math.Abs(candDur - duration.TotalSeconds)
                    : 0;

                if (candidate.HasSynced)
                {
                    if (diff < bestSyncedDiff)
                    {
                        bestSyncedDiff = diff;
                        bestSynced = candidate;
                    }
                }
                else if (diff < bestPlainDiff)
                {
                    bestPlainDiff = diff;
                    bestPlain = candidate;
                }
            }

            return bestSynced ?? bestPlain;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private static LyricsResult ParseLrcLibElement(JsonElement el)
    {
        var syncedRaw = el.TryGetProperty("syncedLyrics", out var sEl) && sEl.ValueKind == JsonValueKind.String
            ? sEl.GetString()
            : null;
        var plainRaw = el.TryGetProperty("plainLyrics", out var pEl) && pEl.ValueKind == JsonValueKind.String
            ? pEl.GetString()
            : null;
        var trackName = el.TryGetProperty("trackName", out var tEl) && tEl.ValueKind == JsonValueKind.String
            ? tEl.GetString()
            : "";
        var artistName = el.TryGetProperty("artistName", out var aEl) && aEl.ValueKind == JsonValueKind.String
            ? aEl.GetString()
            : "";

        var lines = ParseLrc(syncedRaw);
        var info = !string.IsNullOrWhiteSpace(artistName) && !string.IsNullOrWhiteSpace(trackName)
            ? $"LRCLIB • {artistName} — {trackName}"
            : "LRCLIB";

        return new LyricsResult
        {
            SyncedLines = lines,
            PlainText = string.IsNullOrWhiteSpace(plainRaw) ? null : plainRaw.Trim(),
            SourceInfo = lines.Count > 0 ? $"{info} (синхронный)" : info
        };
    }

    public static List<LyricLine> ParseLrc(string? lrcContent)
    {
        var list = new List<LyricLine>();
        if (string.IsNullOrWhiteSpace(lrcContent)) return list;

        foreach (var rawLine in lrcContent.Split(new[] { "\r\n", "\n" }, StringSplitOptions.RemoveEmptyEntries))
        {
            var matches = LrcTimestampRegex.Matches(rawLine);
            if (matches.Count == 0) continue;

            var text = LrcTimestampRegex.Replace(rawLine, "").Trim();
            if (string.IsNullOrWhiteSpace(text)) continue;

            foreach (Match m in matches)
            {
                if (!int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int min))
                    continue;
                if (!int.TryParse(m.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int sec))
                    continue;

                int ms = 0;
                if (m.Groups[3].Success)
                {
                    var frac = m.Groups[3].Value;
                    if (frac.Length == 1) frac += "00";
                    else if (frac.Length == 2) frac += "0";
                    int.TryParse(frac[..3], NumberStyles.Integer, CultureInfo.InvariantCulture, out ms);
                }

                var ts = new TimeSpan(0, 0, min, sec, ms);
                list.Add(new LyricLine(ts, text));
            }
        }

        list.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        return list;
    }

    public static (string Artist, string Title) ParseArtistAndTitle(string rawArtist, string rawTitle)
    {
        string cleanTitle = CleanFragment(rawTitle);
        string cleanArtist = CleanFragment(rawArtist);
        cleanArtist = Regex.Replace(cleanArtist, @"\s*-\s*Topic$", "", RegexOptions.IgnoreCase).Trim();

        // Если в названии уже есть формат "Artist - Title", разбиваем его
        var separators = new[] { " - ", " — ", " – " };
        foreach (var sep in separators)
        {
            int idx = cleanTitle.IndexOf(sep, StringComparison.Ordinal);
            if (idx > 0 && idx < cleanTitle.Length - sep.Length)
            {
                var left = cleanTitle[..idx].Trim();
                var right = cleanTitle[(idx + sep.Length)..].Trim();
                if (!string.IsNullOrWhiteSpace(left) && !string.IsNullOrWhiteSpace(right))
                {
                    cleanArtist = CleanFragment(left);
                    cleanTitle = CleanFragment(right);
                    break;
                }
            }
        }

        cleanTitle = FeatTailRegex.Replace(cleanTitle, "").Trim();
        cleanArtist = FeatTailRegex.Replace(cleanArtist, "").Trim();

        if (cleanArtist.Equals("SoundCloud", StringComparison.OrdinalIgnoreCase) ||
            cleanArtist.Equals("YouTube", StringComparison.OrdinalIgnoreCase))
        {
            cleanArtist = "";
        }

        return (cleanArtist, cleanTitle);
    }

    private static string CleanFragment(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return "";
        var cleaned = TagBracketRegex.Replace(input, " ");
        cleaned = MultiSpaceRegex.Replace(cleaned, " ").Trim(' ', '-', '—', '–');
        return cleaned;
    }

    private static LyricsResult CloneResult(LyricsResult src) => new()
    {
        SyncedLines = src.SyncedLines.Select(l => new LyricLine(l.Timestamp, l.Text)).ToList(),
        PlainText = src.PlainText,
        SourceInfo = src.SourceInfo
    };

    public void Dispose() => _http.Dispose();
}
