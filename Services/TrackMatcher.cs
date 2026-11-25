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
        @"\b(?:speed\s*up|speedup|sped\s*up|nightcore)\b",
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
    private static readonly Regex VariantCoverRegex = new(
        @"\b(?:cover|кавер|tribute|трибьют)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex VariantMashupRegex = new(
        @"\b(?:mashup|мэшап)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex Variant8DRegex = new(
        @"\b(?:8d\s*audio|bass\s*boosted|pitched)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex VariantLiveRegex = new(
        @"[\(\[]\s*live\b[^\)\]]*[\)\]]|\s+-\s+live\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex ParenWithFeatRegex = new(
        @"\s*[\(\[]\s*with\s+([^\)\]]+?)[\)\]]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex EditionNoiseParenRegex = new(
        @"\s*[\(\[]\s*(?:(?:\d{4}\s+)?remaster(?:ed)?(?:\s*\d{4})?|explicit|clean(?:\s*version)?|radio\s*edit|single\s*version|album\s*version|deluxe(?:\s*edition)?|bonus\s*track|from\s+[^\)\]]+)[\)\]]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex TrailingEditionDashRegex = new(
        @"\s*[-–—]\s*(?:(?:\d{4}\s+)?remaster(?:ed)?(?:\s*\d{4})?|radio\s*edit|single\s*version|album\s*version|bonus\s*track|explicit|clean\s*version|from\s+.*)$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly string[] TitleSplitSeparators = { " — ", " – ", " - ", " | " };
    private static readonly string[] VariantOrPartRightPrefixes =
    {
        "remix", "ремикс", "slowed", "speed up", "sped up", "nightcore",
        "live", "acoustic", "акустика", "instrumental", "karaoke", "караоке",
        "минус", "cover", "кавер", "part ", "pt.", "pt ", "chapter", "intro",
        "outro", "interlude", "version", "ver.", "edit", "mix",
        "remaster", "radio", "single", "album", "explicit", "clean", "bonus", "deluxe", "from "
    };

    private static readonly HashSet<string> HarmlessExtraTitleTokens = new(StringComparer.Ordinal)
    {
        "премьера", "трека", "трек", "клип", "official", "audio", "lyrics", "lyric",
        "video", "music", "full", "version", "оригинал", "original", "текст", "песни",
        "hq", "hd", "4k", "mv", "track", "song", "prod", "by", "feat", "ft",
        "featuring", "при", "уч", "explicit", "clean", "remaster", "remastered",
        "radio", "edit", "single", "album", "deluxe", "bonus", "visualizer"
    };

    public readonly record struct TrackMatchResult(
        int Score,
        bool IsValid,
        bool IsHighConfidence,
        bool ExactOrCensoredTitle,
        bool ArtistMatched,
        long DurationDiffMs);

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

    public static bool IsGenericSourceName(string? artist)
    {
        var norm = NormalizeKey(artist);
        if (string.IsNullOrWhiteSpace(norm)) return true;
        return norm is "spotify" or "youtube" or "soundcloud" or "audius"
            or "yandex" or "vk" or "bandcamp" or "radio" or "web"
            or "яндекс музыка" or "яндекс" or "vk музыка" or "вк"
            or "open.spotify.com" or "various artists";
    }

    public static List<string> BuildCleanLookupQueries(string? artist, string? title)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddQuery(string? q)
        {
            if (string.IsNullOrWhiteSpace(q)) return;
            var normalized = WhitespaceRegex.Replace(q, " ").Trim();
            if (!string.IsNullOrWhiteSpace(normalized) && seen.Add(normalized))
                result.Add(normalized);
        }

        string CombineBounded(string? artistPart, string titlePart)
        {
            string t = WhitespaceRegex.Replace(titlePart.Replace(" - ", " ").Replace("- ", " "), " ").Trim();
            if (string.IsNullOrWhiteSpace(t))
                t = WhitespaceRegex.Replace(titlePart, " ").Trim();

            if (t.Length > 85)
                t = t[..85].Trim();

            if (string.IsNullOrWhiteSpace(artistPart))
                return t.Length > 100 ? t[..100].Trim() : t;

            string a = WhitespaceRegex.Replace(artistPart.Replace(" - ", " ").Replace("- ", " "), " ").Trim();
            if (string.IsNullOrWhiteSpace(a))
                return t.Length > 100 ? t[..100].Trim() : t;

            int maxArtistLen = Math.Max(0, 100 - t.Length - 1);
            if (a.Length > maxArtistLen)
                a = maxArtistLen >= 3 ? a[..maxArtistLen].Trim() : "";

            return string.IsNullOrWhiteSpace(a) ? t : $"{a} {t}";
        }

        string rawTitle = WhitespaceRegex.Replace(title ?? "", " ").Trim();
        string cleanSearchTitle = rawTitle;
        if (!string.IsNullOrWhiteSpace(cleanSearchTitle))
        {
            string stripped = ParenWithFeatRegex.Replace(cleanSearchTitle, " ");
            stripped = FeatStripRegex.Replace(stripped, "");
            stripped = EditionNoiseParenRegex.Replace(stripped, " ");
            stripped = TrailingEditionDashRegex.Replace(stripped, "");
            stripped = DisplayVideoNoiseRegex.Replace(stripped, " ");
            stripped = WhitespaceRegex.Replace(stripped, " ").Trim();
            if (!string.IsNullOrWhiteSpace(stripped))
                cleanSearchTitle = stripped;
        }

        bool hasArtist = !IsGenericSourceName(artist);
        var rawArtistParts = new List<string>();
        if (hasArtist && !string.IsNullOrWhiteSpace(artist))
        {
            var seenArt = new HashSet<string>(StringComparer.Ordinal);
            foreach (var part in ArtistSplitRegex.Split(artist))
            {
                var p = WhitespaceRegex.Replace(part, " ").Trim();
                var pk = NormalizeKey(p);
                if (!string.IsNullOrWhiteSpace(p) && !string.IsNullOrWhiteSpace(pk) && seenArt.Add(pk))
                    rawArtistParts.Add(p);
            }
        }

        string? primaryArtist = rawArtistParts.Count > 0 ? rawArtistParts[0] : (hasArtist ? GetPrimaryArtist(artist) : null);

        // Запрос 1: первый исполнитель + очищенное название
        AddQuery(CombineBounded(primaryArtist, cleanSearchTitle));

        // Запрос 2: второй исполнитель + очищенное название (если на треке несколько артистов)
        if (rawArtistParts.Count > 1)
            AddQuery(CombineBounded(rawArtistParts[1], cleanSearchTitle));

        // Запрос 3: если исходное название заметно отличалось после очистки
        if (!string.IsNullOrWhiteSpace(rawTitle) &&
            !string.Equals(rawTitle, cleanSearchTitle, StringComparison.OrdinalIgnoreCase))
        {
            AddQuery(CombineBounded(primaryArtist, rawTitle));
        }

        return result;
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

    public static TrackMatchResult ScoreMatch(TrackItem target, TrackItem candidate)
    {
        if (target == null || candidate == null)
            return new TrackMatchResult(0, false, false, false, false, long.MaxValue);

        bool targetArtistMissing = IsGenericSourceName(target.Artist);
        var (tCleanArtist, tCleanTitle, tEffArtist) = NormalizeArtistAndTitle(
            target.Artist,
            target.Title,
            allowSplitArtistFromTitle: targetArtistMissing,
            stripDisplayVideoNoise: true);

        if (string.IsNullOrWhiteSpace(tCleanTitle))
            tCleanTitle = target.Title ?? "";

        bool candIsUnstructured =
            !string.Equals(candidate.BrandKey, "yandex", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(candidate.BrandKey, "spotify", StringComparison.OrdinalIgnoreCase);

        var (cRawArtist, cRawTitle, cRawEffArtist) = NormalizeArtistAndTitle(
            candidate.Artist,
            candidate.Title,
            allowSplitArtistFromTitle: false,
            stripDisplayVideoNoise: true);

        var bestResult = ScoreNormalizedForm(
            target,
            tCleanTitle,
            tEffArtist,
            candidate,
            cRawArtist,
            cRawTitle,
            cRawEffArtist);

        if (candIsUnstructured)
        {
            var (cSplitArtist, cSplitTitle, cSplitEffArtist) = NormalizeArtistAndTitle(
                candidate.Artist,
                candidate.Title,
                allowSplitArtistFromTitle: true,
                stripDisplayVideoNoise: true);

            if (!string.Equals(cSplitArtist, cRawArtist, StringComparison.Ordinal) ||
                !string.Equals(cSplitTitle, cRawTitle, StringComparison.Ordinal))
            {
                var splitResult = ScoreNormalizedForm(
                    target,
                    tCleanTitle,
                    tEffArtist,
                    candidate,
                    cSplitArtist,
                    cSplitTitle,
                    cSplitEffArtist);

                if (splitResult.Score > bestResult.Score ||
                    (splitResult.Score == bestResult.Score && splitResult.IsValid && !bestResult.IsValid))
                {
                    bestResult = splitResult;
                }
            }
        }

        return bestResult;
    }

    private static TrackMatchResult ScoreNormalizedForm(
        TrackItem target,
        string tCleanTitle,
        string tEffArtist,
        TrackItem candidate,
        string cCleanArtist,
        string cCleanTitle,
        string cEffArtist)
    {
        TrackMatchResult Invalid(long d = long.MaxValue) => new(0, false, false, false, false, d);

        string tCanon = BuildCanonicalSongKey(tCleanTitle);
        string tPattern = BuildCensoredPatternTitle(tCleanTitle);
        string tVariant = ExtractVariantTag(tCleanTitle);
        if (string.IsNullOrEmpty(tVariant))
            tVariant = ExtractVariantTag(target.Title);

        string effectiveCandTitle = !string.IsNullOrWhiteSpace(cCleanTitle) ? cCleanTitle : (candidate.Title ?? "");
        string cCanon = BuildCanonicalSongKey(effectiveCandTitle);
        string cPattern = BuildCensoredPatternTitle(effectiveCandTitle);
        string cVariant = ExtractVariantTag(effectiveCandTitle);
        if (string.IsNullOrEmpty(cVariant))
            cVariant = ExtractVariantTag(candidate.Title);

        // Gate 1: Строгое совпадение варианта (Slowed / Speed Up / Remix / Cover / Instrumental / Live)
        if (!string.Equals(tVariant, cVariant, StringComparison.Ordinal))
            return Invalid();

        // Gate 2: Здравая проверка длительности
        bool durationKnown = target.Duration > TimeSpan.Zero && candidate.Duration > TimeSpan.Zero;
        long diffMs = durationKnown
            ? Math.Abs((long)(candidate.Duration - target.Duration).TotalMilliseconds)
            : long.MaxValue;

        if (durationKnown)
        {
            if (target.Duration.TotalSeconds >= 60 && candidate.Duration.TotalSeconds <= 35)
                return Invalid(diffMs);
            if (diffMs > 45_000)
                return Invalid(diffMs);
        }

        // Gate 3: Пересечение артистов
        bool hasTargetArtist = !IsGenericSourceName(target.Artist) || !IsGenericSourceName(tEffArtist);
        string effectiveTargetArtists = !IsGenericSourceName(tEffArtist) ? tEffArtist : (target.Artist ?? "");
        bool artistMatched = false;
        bool primaryArtistMatched = false;

        if (hasTargetArtist)
        {
            string effCandArtists = !IsGenericSourceName(cEffArtist)
                ? cEffArtist
                : (!IsGenericSourceName(cCleanArtist) ? cCleanArtist : (candidate.Artist ?? ""));
            artistMatched =
                ArtistsOverlap(effectiveTargetArtists, effCandArtists, candidate.Title) ||
                ArtistsOverlap(target.Artist, candidate.Artist, candidate.Title);
            if (artistMatched)
            {
                string targetPrimary = GetPrimaryArtist(effectiveTargetArtists);
                if (string.IsNullOrWhiteSpace(targetPrimary))
                    targetPrimary = GetPrimaryArtist(target.Artist);
                primaryArtistMatched =
                    ArtistsOverlap(targetPrimary, effCandArtists, candidate.Title) ||
                    ArtistsOverlap(targetPrimary, candidate.Artist, candidate.Title);
            }
        }

        // Gate 4: ОБЯЗАТЕЛЬНОЕ совпадение названия трека
        if (string.IsNullOrWhiteSpace(tCanon) || string.IsNullOrWhiteSpace(cCanon))
            return Invalid(diffMs);

        bool exactOrCensored = string.Equals(tCanon, cCanon, StringComparison.Ordinal);
        bool sameSong = exactOrCensored || AreSameSongTitle(tCanon, tPattern, cCanon, cPattern, out exactOrCensored);

        var tTokens = tCanon.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var cTokens = cCanon.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (tTokens.Length == 0 || cTokens.Length == 0)
            return Invalid(diffMs);

        var tTokenSet = new HashSet<string>(tTokens, StringComparer.Ordinal);
        var cTokenSet = new HashSet<string>(cTokens, StringComparer.Ordinal);

        int titleScore = 0;
        if (exactOrCensored)
        {
            titleScore = 10;
        }
        else
        {
            bool allTargetInCand = tTokenSet.All(cTokenSet.Contains);
            bool eligibleForTier2 = tTokens.Length >= 2 || (tTokens.Length == 1 && tTokens[0].Length >= 4);

            if (sameSong || (eligibleForTier2 && allTargetInCand))
            {
                var targetArtistTokenSet = BuildArtistTokenSet(effectiveTargetArtists);
                var extraTokens = cTokenSet.Where(tok => !tTokenSet.Contains(tok)).ToList();

                bool onlyHarmlessOrArtistExtras = allTargetInCand && extraTokens.All(tok =>
                    targetArtistTokenSet.Contains(tok) ||
                    HarmlessExtraTitleTokens.Contains(tok) ||
                    IsYearToken(tok));

                if (onlyHarmlessOrArtistExtras ||
                    (sameSong && extraTokens.Count == 0) ||
                    (artistMatched && durationKnown && diffMs <= 5000 && extraTokens.Count <= 4))
                {
                    titleScore = 6;
                }
            }

            if (titleScore == 0 && tTokens.Length >= 2 && artistMatched && durationKnown && diffMs <= 5000)
            {
                int shared = tTokenSet.Count(cTokenSet.Contains);
                double dice = (2.0 * shared) / (tTokenSet.Count + cTokenSet.Count);
                if (dice >= 0.75)
                    titleScore = 4;
            }
        }

        if (titleScore == 0)
            return Invalid(diffMs);

        int score = titleScore;

        // Gate 5: Требование по артисту и итоговый подсчёт
        if (hasTargetArtist)
        {
            if (artistMatched)
            {
                score += 6;
                if (primaryArtistMatched)
                    score += 2;
            }
            else
            {
                bool distinctiveExactNoArtist =
                    exactOrCensored &&
                    durationKnown &&
                    diffMs <= 2500 &&
                    (tCanon.Length >= 9 || (tCanon.Length >= 6 && tTokens.Length >= 2));
                if (!distinctiveExactNoArtist)
                    return Invalid(diffMs);

                score -= 4;
            }
        }
        else
        {
            if (!exactOrCensored || (durationKnown && diffMs > 5000))
                return Invalid(diffMs);

            if (!durationKnown && tCanon.Length >= 3)
                score += 2;
        }

        // Очки за длительность
        if (durationKnown)
        {
            if (diffMs <= 3000)
                score += 6;
            else if (diffMs <= 6000)
                score += 4;
            else if (diffMs <= 12000)
                score += 1;
            else if (diffMs <= 25000)
            {
                if (!(exactOrCensored && artistMatched))
                    return Invalid(diffMs);
                score -= 2;
            }
            else
            {
                if (!(exactOrCensored && artistMatched))
                    return Invalid(diffMs);
                score -= 6;
            }
        }

        bool isValid = score >= 12;
        bool isHighConfidence =
            isValid &&
            exactOrCensored &&
            (!hasTargetArtist || artistMatched) &&
            (!durationKnown || diffMs <= 5000);

        return new TrackMatchResult(
            score,
            isValid,
            isHighConfidence,
            exactOrCensored,
            artistMatched,
            diffMs);
    }

    private static HashSet<string> BuildArtistTokenSet(string? artists)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(artists)) return set;
        string cleaned = NormalizeKey(NonWordPunctRegex.Replace(artists, " "));
        foreach (var tok in cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            set.Add(tok);
        return set;
    }

    private static bool IsYearToken(string token)
        => token.Length == 4 &&
           int.TryParse(token, out int yr) &&
           yr is >= 1950 and <= 2035;

    public static TrackItem? PickBestMatch(TrackItem target, IEnumerable<TrackItem> candidates)
    {
        if (target == null || candidates == null) return null;

        TrackItem? best = null;
        TrackMatchResult bestMatch = default;

        foreach (var cand in candidates)
        {
            if (cand == null) continue;
            var res = ScoreMatch(target, cand);
            if (!res.IsValid) continue;

            if (best == null ||
                res.Score > bestMatch.Score ||
                (res.Score == bestMatch.Score && res.DurationDiffMs < bestMatch.DurationDiffMs))
            {
                best = cand;
                bestMatch = res;
            }
        }

        return best;
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

            foreach (Match withMatch in ParenWithFeatRegex.Matches(cleanTitle))
            {
                if (withMatch.Groups.Count > 1)
                {
                    string withPart = withMatch.Groups[1].Value.Trim();
                    if (!string.IsNullOrWhiteSpace(withPart))
                        artistParts.Add(withPart);
                }
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
        if (VariantCoverRegex.IsMatch(lower)) return "cover";
        if (VariantMashupRegex.IsMatch(lower)) return "mashup";
        if (Variant8DRegex.IsMatch(lower)) return "8d";
        if (VariantLiveRegex.IsMatch(lower)) return "live";
        return "";
    }

    private static string StripCanonicalTitleNoise(string cleanTitle)
    {
        string s = TrailingProdRegex.Replace(cleanTitle.Normalize(System.Text.NormalizationForm.FormC), "");
        s = CanonicalNoiseTagRegex.Replace(s, " ");
        s = ParenWithFeatRegex.Replace(s, " ");
        s = EditionNoiseParenRegex.Replace(s, " ");
        s = FeatStripRegex.Replace(s, "");
        s = TrailingEditionDashRegex.Replace(s, "");
        s = TrailingVariantNoiseRegex.Replace(s, "");
        return s;
    }

    public static string BuildCanonicalSongKey(string? cleanTitle)
    {
        if (string.IsNullOrWhiteSpace(cleanTitle)) return "";
        string s = StripCanonicalTitleNoise(cleanTitle);
        string norm = NormalizeKey(NonWordPunctRegex.Replace(s, " "));
        if (!string.IsNullOrWhiteSpace(norm))
            return norm;
        return NormalizeKey(NonWordPunctRegex.Replace(cleanTitle, " "));
    }

    public static string BuildCensoredPatternTitle(string? cleanTitle)
    {
        if (string.IsNullOrWhiteSpace(cleanTitle)) return "";
        string s = StripCanonicalTitleNoise(cleanTitle);
        string norm = NormalizeKey(NonWordKeepStarRegex.Replace(s, " "));
        if (!string.IsNullOrWhiteSpace(norm))
            return norm;
        return NormalizeKey(NonWordKeepStarRegex.Replace(cleanTitle, " "));
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
