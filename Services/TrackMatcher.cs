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
    private static readonly Regex TopicChannelSuffixRegex = new(
        @"\s*[-–—]\s*topic\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex VevoChannelSuffixRegex = new(
        @"(?<=\S)VEVO$",
        RegexOptions.Compiled);
    private static readonly Regex DisplayVideoNoiseRegex = new(
        @"\s*[\(\[](?:official\s*(?:music\s*)?(?:video|audio|clip|lyric\s*video|visualizer)|lyrics?|audio|hd|hq|4k|mv|клип)[\)\]]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex CanonicalNoiseTagRegex = new(
        @"\s*[\(\[](?:official\s*(?:music\s*)?(?:video|audio|clip|lyric\s*video|visualizer)|lyrics?|audio|hd|hq|4k|prod\.?[^\)\]]*|mv|клип|slowed(?:\s*(?:\+|&|and)?\s*reverb)?|reverb|speed\s*up|sped\s*up|nightcore|remix|ремикс|bootleg|vip|live|acoustic|акустика|instrumental|type\s*beat|karaoke|караоке|минус)[\)\]]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TrailingVariantNoiseRegex = new(
        @"\s+(?:slowed(?:\s*(?:\+|&|and)\s*reverb)?|sped\s*up|speed\s*up|nightcore)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TrailingProdRegex = new(
        @"\s+(?:prod\.?\s+by\b|prod\.?\s*:\s*).*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex FeatExtractRegex = new(
        @"\s*[\(\[]?(?:feat\.?|ft\.?|featuring|при уч\.?)\s+([^\)\]]+?)[\)\]]?\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex FeatStripRegex = new(
        @"\s*[\(\[]?(?:feat\.?|ft\.?|featuring|при уч\.?)\b.*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex NonWordPunctRegex = new(
        @"[^\p{L}\p{Nd}\s]+",
        RegexOptions.Compiled);
    private static readonly Regex NonWordKeepStarRegex = new(
        @"[^\p{L}\p{Nd}\*\s]+",
        RegexOptions.Compiled);
    private static readonly Regex VariantSlowedRegex = new(
        @"\b(?:slowed|reverb)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex VariantSpeedUpRegex = new(
        @"\b(?:speed\s*up|sped\s*up|nightcore)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex VariantRemixRegex = new(
        @"\b(?:remix|ремикс|bootleg|vip\s*mix)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex VariantInstrumentalRegex = new(
        @"\b(?:instrumental|type\s*beat|karaoke|караоке|минус)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex VariantAcousticRegex = new(
        @"\b(?:acoustic|акустика)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex VariantLiveRegex = new(
        @"[\(\[]\s*live\b[^\)\]]*[\)\]]|\s+-\s+live\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly string[] TitleSplitSeparators = { " — ", " – ", " - ", " | " };
    private static readonly string[] VariantOrPartRightPrefixes =
    {
        "remix", "ремикс", "slowed", "speed up", "sped up", "nightcore",
        "live", "acoustic", "акустика", "instrumental", "karaoke", "караоке",
        "минус", "cover", "кавер", "part ", "pt.", "pt ", "chapter", "intro",
        "outro", "interlude", "version", "ver.", "edit", "mix"
    };

    private static readonly string[] NoiseWords =
    {
        "karaoke", "караоке", "cover", "кавер", "tribute", "трибьют", "instrumental", "минус"
    };

    public static string NormalizeKey(string? text)
        => WhitespaceRegex.Replace((text ?? "").Normalize(System.Text.NormalizationForm.FormC), " ")
            .Trim()
            .ToLowerInvariant()
            .Replace('ё', 'е');

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
                string yCompact = y.Replace(" ", "", StringComparison.Ordinal);
                string sCompact = s.Replace(" ", "", StringComparison.Ordinal);
                if (yCompact.Length >= 4 && yCompact == sCompact)
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

    public static (string CleanArtist, string CleanTitle, string EffectiveArtist) NormalizeArtistAndTitle(
        string? rawArtist,
        string? rawTitle,
        string? publisherArtist = null,
        bool allowSplitArtistFromTitle = true,
        bool stripDisplayVideoNoise = true)
    {
        string cleanArtist = WhitespaceRegex.Replace(rawArtist ?? "", " ").Trim();
        cleanArtist = TopicChannelSuffixRegex.Replace(cleanArtist, "").Trim();
        cleanArtist = VevoChannelSuffixRegex.Replace(cleanArtist, "").Trim();
        if (string.IsNullOrWhiteSpace(cleanArtist))
            cleanArtist = WhitespaceRegex.Replace(rawArtist ?? "", " ").Trim();

        string cleanPubArtist = WhitespaceRegex.Replace(publisherArtist ?? "", " ").Trim();
        cleanPubArtist = TopicChannelSuffixRegex.Replace(cleanPubArtist, "").Trim();
        cleanPubArtist = VevoChannelSuffixRegex.Replace(cleanPubArtist, "").Trim();

        string cleanTitle = WhitespaceRegex.Replace(rawTitle ?? "", " ").Trim();
        if (stripDisplayVideoNoise && !string.IsNullOrWhiteSpace(cleanTitle))
        {
            string stripped = WhitespaceRegex.Replace(DisplayVideoNoiseRegex.Replace(cleanTitle, " "), " ").Trim();
            if (!string.IsNullOrWhiteSpace(stripped))
                cleanTitle = stripped;
        }

        string baseArtist = !string.IsNullOrWhiteSpace(cleanPubArtist) ? cleanPubArtist : cleanArtist;
        string? extractedArtist = null;

        if (!string.IsNullOrWhiteSpace(cleanTitle))
        {
            foreach (var sep in TitleSplitSeparators)
            {
                int sepIdx = cleanTitle.IndexOf(sep, StringComparison.Ordinal);
                if (sepIdx < 1 || sepIdx > 60 || sepIdx + sep.Length >= cleanTitle.Length)
                    continue;

                string left = cleanTitle[..sepIdx].Trim();
                string right = cleanTitle[(sepIdx + sep.Length)..].Trim();
                if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
                    continue;

                string rightLower = NormalizeKey(right);
                bool rightIsVariantOrPart = VariantOrPartRightPrefixes.Any(p =>
                    rightLower.StartsWith(p, StringComparison.Ordinal));
                if (rightIsVariantOrPart)
                    break;

                bool leftMatchesBase = !string.IsNullOrWhiteSpace(baseArtist) && ArtistsOverlap(baseArtist, left);
                bool rightMatchesBase = !string.IsNullOrWhiteSpace(baseArtist) && ArtistsOverlap(baseArtist, right);

                if (rightMatchesBase && !leftMatchesBase)
                {
                    // Редкий формат «Название - Исполнитель»
                    cleanTitle = left;
                    extractedArtist = right;
                }
                else if (leftMatchesBase || allowSplitArtistFromTitle)
                {
                    extractedArtist = left;
                    cleanTitle = right;
                }
                break;
            }
        }

        if (!string.IsNullOrWhiteSpace(extractedArtist))
            cleanArtist = extractedArtist;
        else if (!string.IsNullOrWhiteSpace(cleanPubArtist))
            cleanArtist = cleanPubArtist;

        var artistParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(cleanArtist))
            artistParts.Add(cleanArtist);
        if (!string.IsNullOrWhiteSpace(cleanPubArtist) &&
            !artistParts.Any(a => a.Equals(cleanPubArtist, StringComparison.OrdinalIgnoreCase)))
            artistParts.Add(cleanPubArtist);

        if (!string.IsNullOrWhiteSpace(cleanTitle))
        {
            var featMatch = FeatExtractRegex.Match(cleanTitle);
            if (featMatch.Success && featMatch.Groups.Count > 1)
            {
                string featPart = featMatch.Groups[1].Value.Trim();
                if (!string.IsNullOrWhiteSpace(featPart))
                    artistParts.Add(featPart);
            }
        }

        string effectiveArtist = string.Join(", ", artistParts);
        return (cleanArtist, cleanTitle, effectiveArtist);
    }

    public static string ExtractVariantTag(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        string lower = NormalizeKey(title);
        if (VariantSlowedRegex.IsMatch(lower)) return "slowed";
        if (VariantSpeedUpRegex.IsMatch(lower)) return "speedup";
        if (VariantRemixRegex.IsMatch(lower)) return "remix";
        if (VariantInstrumentalRegex.IsMatch(lower)) return "instrumental";
        if (VariantAcousticRegex.IsMatch(lower)) return "acoustic";
        if (VariantLiveRegex.IsMatch(lower)) return "live";
        return "";
    }

    public static string BuildCanonicalSongKey(string? cleanTitle)
    {
        if (string.IsNullOrWhiteSpace(cleanTitle)) return "";
        string s = TrailingProdRegex.Replace(cleanTitle.Normalize(System.Text.NormalizationForm.FormC), "");
        s = CanonicalNoiseTagRegex.Replace(s, " ");
        s = FeatStripRegex.Replace(s, "");
        s = TrailingVariantNoiseRegex.Replace(s, "");
        string norm = NormalizeKey(NonWordPunctRegex.Replace(s, " "));
        if (!string.IsNullOrWhiteSpace(norm))
            return norm;
        return NormalizeKey(NonWordPunctRegex.Replace(cleanTitle, " "));
    }

    public static string BuildCensoredPatternTitle(string? cleanTitle)
    {
        if (string.IsNullOrWhiteSpace(cleanTitle)) return "";
        string s = TrailingProdRegex.Replace(cleanTitle.Normalize(System.Text.NormalizationForm.FormC), "");
        s = CanonicalNoiseTagRegex.Replace(s, " ");
        s = FeatStripRegex.Replace(s, "");
        s = TrailingVariantNoiseRegex.Replace(s, "");
        return NormalizeKey(NonWordKeepStarRegex.Replace(s, " "));
    }

    public static bool AreSameSongTitle(
        string? canonicalA,
        string? patternA,
        string? canonicalB,
        string? patternB,
        out bool exactOrCensoredMatch)
    {
        exactOrCensoredMatch = false;
        string ca = NormalizeKey(canonicalA);
        string cb = NormalizeKey(canonicalB);

        if (ca.Length >= 2 && cb.Length >= 2 && string.Equals(ca, cb, StringComparison.Ordinal))
        {
            exactOrCensoredMatch = true;
            return true;
        }

        string pa = NormalizeKey(patternA);
        string pb = NormalizeKey(patternB);
        if ((pa.Contains('*') || pb.Contains('*')) &&
            (MatchCensoredPattern(pa, cb) || MatchCensoredPattern(pb, ca) || MatchCensoredPattern(pa, pb)))
        {
            exactOrCensoredMatch = true;
            return true;
        }

        string compactA = ca.Replace(" ", "", StringComparison.Ordinal);
        string compactB = cb.Replace(" ", "", StringComparison.Ordinal);
        if (compactA.Length >= 4 && compactB.Length >= 4 &&
            string.Equals(compactA, compactB, StringComparison.Ordinal))
        {
            exactOrCensoredMatch = true;
            return true;
        }

        if (ca.Length >= 4 && cb.Length >= 4)
        {
            string shorter = ca.Length <= cb.Length ? ca : cb;
            string longer = ca.Length <= cb.Length ? cb : ca;
            if (longer.StartsWith(shorter + " ", StringComparison.Ordinal) ||
                longer.EndsWith(" " + shorter, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    public static bool MatchCensoredPattern(string? maskedPattern, string? targetNorm)
    {
        if (string.IsNullOrWhiteSpace(maskedPattern) || string.IsNullOrWhiteSpace(targetNorm))
            return false;

        string masked = NormalizeKey(maskedPattern);
        string target = NormalizeKey(targetNorm);
        if (!masked.Contains('*', StringComparison.Ordinal))
            return string.Equals(masked, target, StringComparison.Ordinal);

        int nonWildcardAlphaNum = masked.Count(char.IsLetterOrDigit);
        if (nonWildcardAlphaNum < 2)
            return false;

        var maskedTokens = masked.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var targetTokens = target.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (maskedTokens.Length == 0 || maskedTokens.Length != targetTokens.Length)
            return false;

        for (int i = 0; i < maskedTokens.Length; i++)
        {
            string mt = maskedTokens[i];
            string tt = targetTokens[i];
            if (mt == tt) continue;

            if (mt.Contains('*', StringComparison.Ordinal) && tt.Contains('*', StringComparison.Ordinal))
            {
                string p1 = mt.TrimEnd('*');
                string p2 = tt.TrimEnd('*');
                if (p1.Length >= 1 && p2.Length >= 1 && (p1.StartsWith(p2, StringComparison.Ordinal) || p2.StartsWith(p1, StringComparison.Ordinal)))
                    continue;
                return false;
            }

            if (mt.Contains('*', StringComparison.Ordinal))
            {
                if (!TokenMatchesWildcard(mt, tt)) return false;
            }
            else if (tt.Contains('*', StringComparison.Ordinal))
            {
                if (!TokenMatchesWildcard(tt, mt)) return false;
            }
            else
            {
                return false;
            }
        }

        return true;
    }

    private static bool TokenMatchesWildcard(string wildcardToken, string plainToken)
    {
        var sb = new System.Text.StringBuilder("^");
        int i = 0;
        while (i < wildcardToken.Length)
        {
            if (wildcardToken[i] == '*')
            {
                while (i < wildcardToken.Length && wildcardToken[i] == '*')
                    i++;
                sb.Append(@"[\p{L}\p{Nd}]*");
            }
            else
            {
                sb.Append(Regex.Escape(wildcardToken[i].ToString()));
                i++;
            }
        }
        sb.Append('$');
        return Regex.IsMatch(plainToken, sb.ToString(), RegexOptions.CultureInvariant);
    }
}
