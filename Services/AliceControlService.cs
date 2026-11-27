using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Windows.Globalization;
using Windows.Media.SpeechRecognition;

namespace DynamicIsland.Services;

public enum AliceCommandType
{
    Help,
    Status,
    Play,
    Pause,
    Toggle,
    Next,
    Prev,
    ShuffleOn,
    ShuffleOff,
    ShuffleToggle,
    RepeatOne,
    RepeatAll,
    RepeatOff,
    RepeatCycle,
    Mute,
    Unmute,
    SetVolume,
    VolumeUp,
    VolumeDown,
    SeekRelative,
    SetCompact,
    HideToTray,
    ShowWindow,
    Like,
    Unlike,
    YandexWave,
    VkMix,
    PlayFavorites,
    ShuffleFavorites,
    PlayPlaylist,
    PlayRadio,
    SetSleepTimer,
    CancelSleepTimer,
    WhatTime,
    ScaleWindow,
    RepeatLastReply,
    SearchAndPlay
}

public sealed record AliceParsedCommand(
    AliceCommandType Type,
    string Query = "",
    int NumberValue = 0,
    bool BoolValue = false);

public sealed record AliceCommandResult(
    bool Ok,
    string Reply,
    string Action,
    string? Query = null,
    object? Status = null);

/// <summary>
/// Парсер естественных голосовых и текстовых команд для Алисы (RU / EN).
/// </summary>
public static class AliceCommandParser
{
    private static readonly Regex PunctRegex = new(@"[^\p{L}\p{N}\s%-]+", RegexOptions.Compiled);
    private static readonly Regex SpacesRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex DigitsRegex = new(@"\d+", RegexOptions.Compiled);

    private static readonly string[] WakePrefixes =
    {
        "привет алиса", "слушай алиса", "эй алиса", "алиса",
        "яндекс", "омнивейв", "omniwave", "плеер", "музыка",
        "пожалуйста", "можешь"
    };

    private static readonly string[] PoliteSuffixes =
    {
        "пожалуйста", "плиз", "сейчас", "прямо сейчас"
    };

    private static readonly Dictionary<string, int> RussianNumbers = new(StringComparer.Ordinal)
    {
        ["ноль"] = 0, ["нуль"] = 0, ["минимум"] = 10, ["один"] = 1, ["одна"] = 1, ["одну"] = 1,
        ["два"] = 2, ["две"] = 2, ["три"] = 3, ["четыре"] = 4, ["пять"] = 5,
        ["шесть"] = 6, ["семь"] = 7, ["восемь"] = 8, ["девять"] = 9, ["десять"] = 10,
        ["пятнадцать"] = 15, ["двадцать"] = 20, ["тридцать"] = 30,
        ["сорок"] = 40, ["пятьдесят"] = 50, ["половина"] = 50, ["половину"] = 50, ["средняя"] = 50, ["среднюю"] = 50,
        ["шестьдесят"] = 60, ["семьдесят"] = 70, ["восемьдесят"] = 80, ["девяносто"] = 90,
        ["сто"] = 100, ["максимум"] = 100, ["полная"] = 100, ["полную"] = 100, ["на полную"] = 100
    };

    private static readonly Dictionary<string, int> RussianTens = new(StringComparer.Ordinal)
    {
        ["двадцать"] = 20, ["тридцать"] = 30, ["сорок"] = 40, ["пятьдесят"] = 50,
        ["шестьдесят"] = 60, ["семьдесят"] = 70, ["восемьдесят"] = 80, ["девяносто"] = 90
    };

    private static readonly Dictionary<string, int> RussianUnits = new(StringComparer.Ordinal)
    {
        ["один"] = 1, ["одна"] = 1, ["два"] = 2, ["две"] = 2, ["три"] = 3,
        ["четыре"] = 4, ["пять"] = 5, ["шесть"] = 6, ["семь"] = 7, ["восемь"] = 8, ["девять"] = 9
    };

    public static bool IsFastControlCommand(AliceCommandType type)
        => type is not (AliceCommandType.SearchAndPlay or AliceCommandType.PlayPlaylist or AliceCommandType.PlayRadio);

    public static IReadOnlyList<string> ExpandWakeAliases(string? wakeWordsCsv)
    {
        var list = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrWhiteSpace(wakeWordsCsv))
        {
            foreach (var raw in wakeWordsCsv.Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var w = PunctRegex.Replace(raw.Trim().ToLowerInvariant().Replace('ё', 'е'), " ");
                w = SpacesRegex.Replace(w, " ").Trim();
                if (w.Length < 2) continue;
                list.Add(w);

                if (w == "омни")
                {
                    list.Add("омне");
                    list.Add("оми");
                    list.Add("умни");
                    list.Add("помни");
                    list.Add("он мне");
                    list.Add("о мне");
                    list.Add("комна");
                    list.Add("комни");
                    list.Add("амни");
                    list.Add("хомни");
                    list.Add("ом ни");
                    list.Add("омнивейв");
                }
                else if (w == "волна")
                {
                    list.Add("волны");
                    list.Add("волну");
                    list.Add("вална");
                    list.Add("волно");
                }
                else if (w == "алиса")
                {
                    list.Add("алисы");
                    list.Add("алису");
                    list.Add("алис");
                    list.Add("олиса");
                }
                else if (w == "ирина")
                {
                    list.Add("ирины");
                    list.Add("ирину");
                    list.Add("ирине");
                    list.Add("ирин");
                    list.Add("ира");
                    list.Add("ирочка");
                    list.Add("арина");
                    list.Add("ерина");
                    list.Add("ярина");
                    list.Add("рина");
                }
            }
        }
        return list.OrderByDescending(x => x.Length).ToList();
    }

    public static string StripWakeWords(string? raw, IEnumerable<string>? customWakeWords = null)
        => NormalizePhrase(raw, customWakeWords);

    public static AliceParsedCommand Parse(string? rawPhrase, IEnumerable<string>? customWakeWords = null)
    {
        string text = NormalizePhrase(rawPhrase, customWakeWords);
        if (string.IsNullOrWhiteSpace(text))
            return new(AliceCommandType.Help);

        if (MatchesAny(text, "привет", "помощь", "команды", "что ты умеешь", "что умеешь", "help"))
            return new(AliceCommandType.Help);

        if (MatchesAny(text, "повтори", "повтори ответ", "что ты сказал", "что ты сказала", "еще раз"))
            return new(AliceCommandType.RepeatLastReply);

        if (MatchesAny(text, "сколько времени", "который час", "какое время", "время сейчас", "сколько время", "время"))
            return new(AliceCommandType.WhatTime);

        if (MatchesAny(text, "что сейчас играет", "что играет", "какой трек", "что за трек", "что за песня", "кто поет", "статус", "status", "now playing"))
            return new(AliceCommandType.Status);

        if (MatchesAny(text, "отмени таймер", "отмени таймер сна", "отменить таймер", "выключи таймер", "выключи таймер сна", "убери таймер", "сбрось таймер"))
            return new(AliceCommandType.CancelSleepTimer);

        if (text.Contains("таймер", StringComparison.Ordinal) ||
            text.StartsWith("выключи музыку через", StringComparison.Ordinal) ||
            text.StartsWith("выключи через", StringComparison.Ordinal) ||
            text.StartsWith("останови через", StringComparison.Ordinal) ||
            text.StartsWith("останови музыку через", StringComparison.Ordinal))
        {
            int mins = ExtractSleepMinutes(text, defaultMinutes: 15);
            return new(AliceCommandType.SetSleepTimer, NumberValue: mins);
        }

        if (MatchesAny(text, "обычный размер окна", "сбрось размер окна", "размер окна 100", "масштаб 100"))
            return new(AliceCommandType.ScaleWindow, NumberValue: 100, BoolValue: true);

        if (MatchesAny(text, "увеличь окно", "сделай окно больше", "больше окно", "крупнее окно"))
            return new(AliceCommandType.ScaleWindow, NumberValue: 10, BoolValue: false);

        if (MatchesAny(text, "уменьши окно", "сделай окно меньше", "меньше окно"))
            return new(AliceCommandType.ScaleWindow, NumberValue: -10, BoolValue: false);

        if (MatchesAny(text, "перемешай избранное", "перемешать избранное", "избранное вперемешку", "случайное избранное", "shuffle favorites"))
            return new(AliceCommandType.ShuffleFavorites);

        if (MatchesAny(text, "включи избранное", "включи избранные", "играй избранное", "любимые треки", "избранное", "мои треки", "play favorites") ||
            IsCloseSingleWord(text, "избранное"))
            return new(AliceCommandType.PlayFavorites);

        if (MatchesAny(text, "моя волна", "мою волну", "включи мою волну", "запусти мою волну", "включи волну", "запусти волну", "волна", "yandex wave"))
            return new(AliceCommandType.YandexWave);

        if (MatchesAny(text, "вк микс", "vk микс", "vk mix", "микс вк", "включи вк микс", "запусти вк микс", "включи микс"))
            return new(AliceCommandType.VkMix);

        if (TryExtractAfterKeyword(text, out var playlistName, "включи плейлист", "запусти плейлист", "поставь плейлист", "открой плейлист", "плейлист"))
            return new(AliceCommandType.PlayPlaylist, Query: playlistName);

        if (MatchesAny(text, "включи радио", "запусти радио", "поставь радио", "радио"))
            return new(AliceCommandType.PlayRadio, Query: "");

        if (TryExtractAfterKeyword(text, out var radioQuery, "включи радио", "запусти радио", "поставь радио", "найди радио", "радио"))
            return new(AliceCommandType.PlayRadio, Query: radioQuery);

        if (MatchesAny(text, "без звука", "выключи звук", "отключи звук", "убери звук", "звук выкл", "мут", "замуть", "mute"))
            return new(AliceCommandType.Mute);

        if (MatchesAny(text, "включи звук", "верни звук", "звук вкл", "размуть", "unmute"))
            return new(AliceCommandType.Unmute);

        if (MatchesAny(text, "выключи перемешивание", "отключи перемешивание", "без перемешивания", "по порядку", "шафл выкл", "shuffle off"))
            return new(AliceCommandType.ShuffleOff);

        if (MatchesAny(text, "перемешай", "перемешать", "перемешать треки", "включи перемешивание", "вперемешку", "случайный порядок", "шафл", "shuffle", "shuffle on") ||
            IsCloseSingleWord(text, "перемешай"))
            return new(AliceCommandType.ShuffleOn);

        if (MatchesAny(text, "выключи повтор", "отключи повтор", "без повтора", "не повторяй", "repeat off"))
            return new(AliceCommandType.RepeatOff);

        if (MatchesAny(text, "повтор трека", "повтор одного", "повторяй этот трек", "повторяй песню", "зацикли трек", "repeat one"))
            return new(AliceCommandType.RepeatOne);

        if (MatchesAny(text, "повтор очереди", "повтор всех", "включи повтор", "повторяй все", "repeat all"))
            return new(AliceCommandType.RepeatAll);

        if (MatchesAny(text, "повтор", "режим повтора", "зацикли", "repeat") ||
            IsCloseSingleWord(text, "повтор"))
            return new(AliceCommandType.RepeatCycle);

        if (MatchesAny(text, "убери лайк", "сними лайк", "удали лайк", "удали из избранного", "не нравится", "дизлайк", "unlike"))
            return new(AliceCommandType.Unlike);

        if (MatchesAny(text, "лайк", "лайкни", "поставь лайк", "добавь лайк", "нравится", "добавь в избранное", "в избранное", "сохрани трек", "like"))
            return new(AliceCommandType.Like);

        if (MatchesAny(text, "компактный режим", "включи компактный режим", "пилюля", "режим пилюли", "мини режим", "сверни в пилюлю", "compact"))
            return new(AliceCommandType.SetCompact, BoolValue: true);

        if (MatchesAny(text, "полный режим", "обычный режим", "разверни", "разверни окно", "выключи компактный режим", "покажи плеер", "покажи окно", "expand"))
            return new(AliceCommandType.SetCompact, BoolValue: false);

        if (MatchesAny(text, "сверни в трей", "скрой окно", "спрячь окно", "спрячь в трей", "hide"))
            return new(AliceCommandType.HideToTray);

        if (ContainsWordPrefix(text, "перемотай", "промотай", "отмотай") ||
            (ContainsWordPrefix(text, "вперед", "назад") && ContainsNumberOrUnit(text)))
        {
            int sec = ExtractSeconds(text, defaultSec: 15);
            bool backward = text.Contains("назад", StringComparison.Ordinal) || text.Contains("отмот", StringComparison.Ordinal);
            return new(AliceCommandType.SeekRelative, NumberValue: backward ? -sec : sec);
        }

        if (text.StartsWith("громче", StringComparison.Ordinal) ||
            text.StartsWith("погромче", StringComparison.Ordinal) ||
            text.StartsWith("сделай громче", StringComparison.Ordinal) ||
            text.StartsWith("сделай погромче", StringComparison.Ordinal) ||
            text.StartsWith("прибавь", StringComparison.Ordinal) ||
            text.StartsWith("добавь громк", StringComparison.Ordinal) ||
            text.StartsWith("добавь звук", StringComparison.Ordinal) ||
            text.StartsWith("увеличь громк", StringComparison.Ordinal) ||
            IsCloseSingleWord(text, "громче") ||
            text == "volume up")
        {
            int delta = ExtractNumber0To100(text, out int num) ? Math.Clamp(num, 1, 100) : 15;
            return new(AliceCommandType.VolumeUp, NumberValue: delta);
        }

        if (text.StartsWith("тише", StringComparison.Ordinal) ||
            text.StartsWith("потише", StringComparison.Ordinal) ||
            text.StartsWith("сделай потише", StringComparison.Ordinal) ||
            text.StartsWith("убавь", StringComparison.Ordinal) ||
            text.StartsWith("уменьши громк", StringComparison.Ordinal) ||
            text.StartsWith("уменьши звук", StringComparison.Ordinal) ||
            text.StartsWith("сделай тише", StringComparison.Ordinal) ||
            IsCloseSingleWord(text, "тише") ||
            text == "volume down")
        {
            int delta = ExtractNumber0To100(text, out int num) ? Math.Clamp(num, 1, 100) : 15;
            return new(AliceCommandType.VolumeDown, NumberValue: delta);
        }

        if (text.Contains("громкост", StringComparison.Ordinal) ||
            text.Contains("звук", StringComparison.Ordinal) ||
            text.StartsWith("volume", StringComparison.Ordinal))
        {
            if (ExtractNumber0To100(text, out int vol))
                return new(AliceCommandType.SetVolume, NumberValue: Math.Clamp(vol, 0, 100));
        }

        if (MatchesAny(text, "следующий", "следующий трек", "следующая песня", "следущая", "следущий", "дальше", "дальше трек", "пропусти", "пропусти трек", "скип", "next", "skip") ||
            IsCloseSingleWord(text, "следующий"))
            return new(AliceCommandType.Next);

        if (MatchesAny(text, "предыдущий", "предыдущий трек", "предыдущая песня", "предыдущая", "назад", "верни", "верни трек", "prev", "previous") ||
            IsCloseSingleWord(text, "предыдущий"))
            return new(AliceCommandType.Prev);

        if (MatchesAny(text, "пауза", "паузу", "паузе", "на паузу", "поставь на паузу", "стоп", "останови", "останови музыку", "приостанови", "выключи музыку", "выключить музыку", "тишина", "хватит", "pause", "stop") ||
            IsCloseSingleWord(text, "пауза"))
            return new(AliceCommandType.Pause);

        if (MatchesAny(text, "продолжи", "продолжай", "продолжить", "сними с паузы", "играй", "пуск", "возобнови", "включи музыку", "включить музыку", "play", "resume") ||
            IsCloseSingleWord(text, "продолжи"))
            return new(AliceCommandType.Play);

        if (MatchesAny(text, "пауза или пуск", "переключи паузу", "toggle"))
            return new(AliceCommandType.Toggle);

        string searchQuery = StripSearchVerbPrefix(text);
        return new(AliceCommandType.SearchAndPlay, Query: searchQuery);
    }

    private static string NormalizePhrase(string? raw, IEnumerable<string>? customWakeWords)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        string s = raw.Trim().ToLowerInvariant().Replace('ё', 'е');
        s = PunctRegex.Replace(s, " ");
        s = SpacesRegex.Replace(s, " ").Trim();

        List<string> prefixes = customWakeWords != null
            ? customWakeWords.Concat(WakePrefixes).Where(w => !string.IsNullOrWhiteSpace(w)).Distinct(StringComparer.Ordinal).OrderByDescending(w => w.Length).ToList()
            : WakePrefixes.ToList();

        bool changed = true;
        while (changed && s.Length > 0)
        {
            changed = false;
            foreach (var p in prefixes)
            {
                if (s == p) return "";
                if (s.StartsWith(p + " ", StringComparison.Ordinal))
                {
                    s = s[(p.Length + 1)..].TrimStart();
                    changed = true;
                    break;
                }
            }
        }

        foreach (var suf in PoliteSuffixes)
        {
            if (s.EndsWith(" " + suf, StringComparison.Ordinal))
                s = s[..^(suf.Length + 1)].TrimEnd();
        }

        return s;
    }

    private static bool TryExtractAfterKeyword(string text, out string extracted, params string[] prefixes)
    {
        foreach (var p in prefixes)
        {
            if (text.StartsWith(p + " ", StringComparison.Ordinal))
            {
                extracted = text[(p.Length + 1)..].Trim();
                if (!string.IsNullOrWhiteSpace(extracted))
                    return true;
            }
        }
        extracted = "";
        return false;
    }

    private static int ExtractSleepMinutes(string text, int defaultMinutes)
    {
        if (text.Contains("полчаса", StringComparison.Ordinal) || text.Contains("пол часа", StringComparison.Ordinal))
            return 30;
        if (text.Contains("полтора часа", StringComparison.Ordinal))
            return 90;

        bool isHours = text.Contains("час", StringComparison.Ordinal);
        if (ExtractNumber0To100(text, out int num))
        {
            int mins = isHours ? num * 60 : num;
            return Math.Clamp(mins, 1, 480);
        }
        return isHours ? 60 : defaultMinutes;
    }

    private static string StripSearchVerbPrefix(string text)
    {
        string[] verbs =
        {
            "хочу послушать", "поставь трек", "поставь песню", "включи трек", "включи песню",
            "сыграй трек", "сыграй песню", "найди трек", "найди песню", "найти трек",
            "включи", "включай", "включить", "поставь", "поставить",
            "сыграй", "найди", "поищи", "запусти", "play"
        };

        foreach (var v in verbs)
        {
            if (text.StartsWith(v + " ", StringComparison.Ordinal))
            {
                string q = text[(v.Length + 1)..].Trim();
                if (!string.IsNullOrWhiteSpace(q))
                    return q;
            }
        }

        return text;
    }

    private static bool MatchesAny(string text, params string[] variants)
    {
        foreach (var v in variants)
        {
            if (string.Equals(text, v, StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    internal static bool IsCloseSingleWord(string text, string target)
    {
        if (string.IsNullOrEmpty(text) || text.Contains(' ', StringComparison.Ordinal) || text.Length < 5)
            return false;
        if (Math.Abs(text.Length - target.Length) > 1)
            return false;

        int n = text.Length;
        int m = target.Length;
        var d = new int[n + 1, m + 1];
        for (int i = 0; i <= n; i++) d[i, 0] = i;
        for (int j = 0; j <= m; j++) d[0, j] = j;

        for (int i = 1; i <= n; i++)
        {
            for (int j = 1; j <= m; j++)
            {
                int cost = text[i - 1] == target[j - 1] ? 0 : 1;
                int val = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && text[i - 1] == target[j - 2] && text[i - 2] == target[j - 1])
                    val = Math.Min(val, d[i - 2, j - 2] + 1);
                d[i, j] = val;
            }
        }
        return d[n, m] <= 1;
    }

    private static bool ContainsWordPrefix(string text, params string[] prefixes)
    {
        foreach (var word in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var p in prefixes)
            {
                if (word.StartsWith(p, StringComparison.Ordinal))
                    return true;
            }
        }
        return false;
    }

    private static bool ContainsNumberOrUnit(string text)
        => DigitsRegex.IsMatch(text) ||
           text.Contains("секунд", StringComparison.Ordinal) ||
           text.Contains("минут", StringComparison.Ordinal);

    private static int ExtractSeconds(string text, int defaultSec)
    {
        var m = DigitsRegex.Match(text);
        if (m.Success && int.TryParse(m.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int val))
        {
            if (text.Contains("мин", StringComparison.Ordinal))
                val *= 60;
            return Math.Clamp(val, 1, 600);
        }

        if (ExtractNumber0To100(text, out int wordVal) && wordVal > 0)
        {
            if (text.Contains("мин", StringComparison.Ordinal))
                wordVal *= 60;
            return Math.Clamp(wordVal, 1, 600);
        }

        return defaultSec;
    }

    private static bool ExtractNumber0To100(string text, out int value)
    {
        var m = DigitsRegex.Match(text);
        if (m.Success && int.TryParse(m.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            return true;

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < words.Length - 1; i++)
        {
            if (RussianTens.TryGetValue(words[i], out int tens) &&
                RussianUnits.TryGetValue(words[i + 1], out int unit))
            {
                value = tens + unit;
                return true;
            }
        }

        foreach (var kv in RussianNumbers.OrderByDescending(k => k.Key.Length))
        {
            if (text.Contains(kv.Key, StringComparison.Ordinal))
            {
                value = kv.Value;
                return true;
            }
        }

        value = 0;
        return false;
    }
}

/// <summary>
/// Захват голосовой команды: WinRT SpeechRecognizer (при наличии пакета распознавания)
/// и нативный Windows 10/11 Voice Typing (Win + H) как надёжный фолбэк.
/// </summary>
public static class AliceVoiceRecognizer
{
    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const ushort VK_LWIN = 0x5B;
    private const ushort VK_H = 0x48;

    public static bool HasWindowsSpeechPack()
    {
        try
        {
            return SpeechRecognizer.SupportedTopicLanguages.Count > 0 ||
                   SpeechRecognizer.SupportedGrammarLanguages.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    public static async Task<(bool Ok, string Text, string? FallbackReason)> RecognizeOnceWinRtAsync(CancellationToken ct)
    {
        SpeechRecognizer? recognizer = null;
        try
        {
            Language? lang = SpeechRecognizer.SupportedTopicLanguages
                .FirstOrDefault(l => l.LanguageTag.StartsWith("ru", StringComparison.OrdinalIgnoreCase))
                ?? SpeechRecognizer.SupportedTopicLanguages.FirstOrDefault()
                ?? SpeechRecognizer.SupportedGrammarLanguages.FirstOrDefault();

            if (lang == null)
                return (false, "", "no-speech-pack");

            recognizer = new SpeechRecognizer(lang);
            recognizer.Timeouts.InitialSilenceTimeout = TimeSpan.FromSeconds(5);
            recognizer.Timeouts.BabbleTimeout = TimeSpan.FromSeconds(3);
            recognizer.Timeouts.EndSilenceTimeout = TimeSpan.FromMilliseconds(900);

            var compilation = await recognizer.CompileConstraintsAsync().AsTask(ct);
            if (compilation.Status != SpeechRecognitionResultStatus.Success)
                return (false, "", "compile-failed");

            var op = recognizer.RecognizeAsync();
            using var reg = ct.Register(() =>
            {
                try { op.Cancel(); } catch { }
            });
            var res = await op.AsTask(ct);
            if (res.Status == SpeechRecognitionResultStatus.Success && !string.IsNullOrWhiteSpace(res.Text))
                return (true, res.Text.Trim(), null);

            return (false, "", res.Status.ToString());
        }
        catch (OperationCanceledException)
        {
            return (false, "", "cancelled");
        }
        catch (COMException)
        {
            return (false, "", "no-speech-pack");
        }
        catch (Exception ex)
        {
            return (false, "", ex.Message);
        }
        finally
        {
            try { recognizer?.Dispose(); } catch { }
        }
    }

    /// <summary>
    /// Открывает встроенный в Windows 10/11 голосовой ввод (Win + H) для диктовки в сфокусированное поле.
    /// </summary>
    public static bool TriggerWindowsVoiceTyping()
    {
        try
        {
            var inputs = new INPUT[]
            {
                new() { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_LWIN } } },
                new() { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_H } } },
                new() { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_H, dwFlags = KEYEVENTF_KEYUP } } },
                new() { type = INPUT_KEYBOARD, U = new InputUnion { ki = new KEYBDINPUT { wVk = VK_LWIN, dwFlags = KEYEVENTF_KEYUP } } }
            };
            uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
            return sent == inputs.Length;
        }
        catch
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public HARDWAREINPUT hi;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx;
        public int dy;
        public uint mouseData;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public nuint dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct HARDWAREINPUT
    {
        public uint uMsg;
        public ushort wParamL;
        public ushort wParamH;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, [MarshalAs(UnmanagedType.LPArray), In] INPUT[] pInputs, int cbSize);
}
