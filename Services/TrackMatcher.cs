using System.Text.RegularExpressions;

namespace DynamicIsland.Services;

/// <summary>
/// Точная сверка треков по логике BuevichRoman/yandex2spotify (pick_match / _names_overlap / label_key):
/// длительность (±3 с), пересечение имён артистов, нормализованное название и отсев live/karaoke/cover.
/// </summary>
public static class TrackMatcher
{
    public const long DurationToleranceMs = 3000;

    private static readonly Regex WhitespaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex ArtistSplitRegex = new(
        @"(?:\s*,\s*|\s*&\s*|\s*;\s*|\s+(?:feat\.?|ft\.?|featuring|при уч\.?|x|×)\s+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] NoiseWords =
    {
        "karaoke", "караоке", "cover", "кавер", "tribute", "трибьют", "instrumental", "минус"
    };

    public static string NormalizeKey(string? text)
        => WhitespaceRegex.Replace(text ?? "", " ").Trim().ToLowerInvariant();

    public static List<string> SplitArtists(string? artists)
    {
        var raw = artists ?? "";
        if (string.IsNullOrWhiteSpace(raw)) return new List<string>();

        return ArtistSplitRegex.Split(raw)
            .Select(NormalizeKey)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct()
            .ToList();
    }

    public static string GetPrimaryArtist(string? artists)
    {
        var raw = (artists ?? "").Trim();
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var parts = ArtistSplitRegex.Split(raw);
        return parts.Length > 0 ? parts[0].Trim() : raw;
    }

    public static bool HasMultipleArtists(string? artists)
        => SplitArtists(artists).Count > 1;

    public static string BuildSearchQuery(string? artist, string? title)
    {
        var raw = string.IsNullOrWhiteSpace(artist) ? (title ?? "") : $"{artist} {title}";
        var cleaned = raw.Replace("- ", "").Trim();
        if (cleaned.Length > 100)
            cleaned = cleaned[..100].Trim();
        return WhitespaceRegex.Replace(cleaned, " ");
    }

    public static bool ArtistsOverlap(string? targetArtists, string? candidateArtists, string? candidateTitle = null)
    {
        var ya = SplitArtists(targetArtists);
        var sp = SplitArtists(candidateArtists);

        foreach (var y in ya)
        {
            foreach (var s in sp)
            {
                if (string.IsNullOrEmpty(y) || string.IsNullOrEmpty(s)) continue;
                if (y == s) return true;
                if (Math.Min(y.Length, s.Length) >= 3 && (y.Contains(s) || s.Contains(y)))
                    return true;
            }
        }

        // На YouTube/SoundCloud артист часто указан только внутри заголовка («Исполнитель - Трек»)
        var normTitle = NormalizeKey(candidateTitle);
        if (!string.IsNullOrEmpty(normTitle))
        {
            foreach (var y in ya)
            {
                if (y.Length >= 3 && normTitle.Contains(y))
                    return true;
            }
        }

        return false;
    }

    public static TrackItem? PickBestMatch(TrackItem target, IEnumerable<TrackItem> candidates)
    {
        var targetTitle = NormalizeKey(target.Title);
        var targetDurationMs = target.Duration > TimeSpan.Zero ? (long)target.Duration.TotalMilliseconds : 0;

        TrackItem? best = null;
        int bestScore = 0;

        foreach (var cand in candidates)
        {
            if (cand == null) continue;
            int score = 0;

            if (targetDurationMs > 0 && cand.Duration > TimeSpan.Zero)
            {
                long diff = Math.Abs((long)cand.Duration.TotalMilliseconds - targetDurationMs);
                if (diff <= DurationToleranceMs)
                    score += 2;
            }

            if (ArtistsOverlap(target.Artist, cand.Artist, cand.Title))
                score += 2;

            var candTitle = NormalizeKey(cand.Title);
            if (!string.IsNullOrEmpty(targetTitle) && !string.IsNullOrEmpty(candTitle))
            {
                if (candTitle == targetTitle)
                    score += 1;
                else if (candTitle.Contains(targetTitle) || targetTitle.Contains(candTitle))
                    score += 1;
            }

            foreach (var noise in NoiseWords)
            {
                if (candTitle.Contains(noise) && !targetTitle.Contains(noise))
                {
                    score -= 2;
                    break;
                }
            }

            if (score > bestScore)
            {
                bestScore = score;
                best = cand;
            }
        }

        return bestScore >= 2 ? best : null;
    }
}
