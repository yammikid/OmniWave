using System.Collections.Concurrent;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DynamicIsland.Services;

/// <summary>
/// Интеграция ВК Музыки:
/// 1) Протокол python273/vk_api (vk_audio.py): m.vk.ru/audio (act=load_section, act=audio_playlists, act=reload_audio)
///    с обязательными куками remixaudio_show_alert_today=0 и remixmdevice=1920/1080/2/!!-!!!!,
/// 2) Desktop vk.ru/al_audio.php (act=section, act=load_catalog_section с sectionId/nextFrom, act=load_section),
/// 3) Живой сбор из браузерной сессии WebView2 (VkWebSessionBridge: same-origin fetch + DOM data-audio/.audio_row),
/// 4) Двухэтапное воспроизведение: прямой поток ВК (с деобфускацией и конвертацией .m3u8 -> .mp3) +
///    верифицированный подбор TrackMatcher (±3 с + артист, как в BuevichRoman/yandex2spotify).
/// </summary>
public sealed class VkMusicProvider : IMusicProvider, IDisposable
{
    public const string BrandColorHex = "#0077FF";
    public const string OAuthAuthorizeUrl =
        "https://oauth.vk.ru/authorize?client_id=2685278&scope=audio,offline&redirect_uri=https://oauth.vk.ru/blank.html&display=mobile&response_type=token&v=5.199";
    public const string KateMobileUserAgent =
        "KateMobileAndroid/56.1 lite-462 (Android 10; SDK 29; arm64-v8a; samsung SM-G973F; ru)";

    private const string DesktopUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";
    private const string RequiredAudioCookies =
        "remixaudio_show_alert_today=0; remixmdevice=1920/1080/2/!!-!!!!";
    private const string VkApiBase = "https://api.vk.com/method/";
    private const int MaxRetries = 5;

    private static readonly Regex AccessTokenInUrlRegex = new(
        @"access_token=([^&\s""#]+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex UserIdInUrlRegex = new(
        @"[?#&]user_id=(\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex AudiosRedirectUidRegex = new(
        @"/audios(\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex RawTokenRegex = new(
        @"^(?:vk1\.[a-zA-Z0-9._-]+|[a-fA-F0-9]{64,256})$",
        RegexOptions.Compiled);

    private static readonly Regex AudioPageIdRegex = new(
        @"audio(-?\d+)_(\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex VkUidRegex = new(
        @"(?:""id""\s*:\s*|vk\s*=\s*\{[^}]*\bid\s*:\s*|""uid""\s*:\s*)(\d{3,15})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex PlaylistHrefRegex = new(
        @"href=[""'][^""']*?/music/(?:playlist|album)/(-?\d+)_(\d+)_([a-zA-Z0-9]+)[""'][^>]*>([^<]{1,120})<",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex MobileAlbumItemRegex = new(
        @"act=audio_playlist(-?\d+)_(\d+)(?:&amp;|&)access_hash=([a-zA-Z0-9]+)[\s\S]{0,600}?audioPlaylistsPage__title[^>]*>([^<]+)<",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DataAudioAttrRegex = new(
        @"data-audio=[""'](\[[\s\S]*?\])[""']",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SectionIdRegex = new(
        @"""sectionId""\s*:\s*""([^""]+)""",
        RegexOptions.Compiled);

    private static readonly Regex NextFromRegex = new(
        @"""(?:nextFrom|next_from)""\s*:\s*""([^""]+)""",
        RegexOptions.Compiled);

    private static readonly Regex M3u8ToMp3Regex = new(
        @"/[0-9a-f]+(/audios)?/([0-9a-f]+)/index\.m3u8",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private sealed class VkAudioMeta
    {
        public long OwnerId { get; init; }
        public long AudioId { get; init; }
        public string AccessKey { get; init; } = "";
        public string ActionHash { get; init; } = "";
        public string UrlHash { get; init; } = "";
        public string RawUrl { get; set; } = "";
    }

    private sealed class VkPlaylistDescriptor
    {
        public long OwnerId { get; init; }
        public long PlaylistId { get; init; }
        public string AccessHash { get; init; } = "";
        public string Title { get; set; } = "Плейлист ВК";
        public List<TrackItem> InlineTracks { get; init; } = new();
    }

    private readonly SettingsService _settings;
    private readonly HttpClient _http;
    private readonly HttpClient _apiHttp;
    private readonly VkWebSessionBridge _bridge = new();
    private readonly ConcurrentDictionary<string, VkAudioMeta> _metaCache = new(StringComparer.OrdinalIgnoreCase);
    private List<IMusicProvider> _fallbackProviders;

    public string Name => "ВК";
    public string BrandKey => "vk";

    public bool IsAuthenticated =>
        _settings.Current.VkUserId > 0 &&
        (!string.IsNullOrWhiteSpace(_settings.Current.VkCookieHeader) ||
         !string.IsNullOrWhiteSpace(_settings.Current.VkAccessToken));

    public VkMusicProvider(
        SettingsService settings,
        IEnumerable<IMusicProvider>? fallbackProviders = null)
    {
        _settings = settings;
        _fallbackProviders = fallbackProviders?
            .Where(p => !string.Equals(p.BrandKey, BrandKey, StringComparison.OrdinalIgnoreCase))
            .ToList() ?? new List<IMusicProvider>();

        var webHandler = new HttpClientHandler
        {
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = false
        };
        _http = new HttpClient(webHandler) { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(DesktopUa);
        _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "ru-RU,ru;q=0.9,en-US;q=0.8,en;q=0.7");

        var apiHandler = new HttpClientHandler
        {
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.All
        };
        _apiHttp = new HttpClient(apiHandler) { Timeout = TimeSpan.FromSeconds(15) };
        _apiHttp.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", KateMobileUserAgent);
        _apiHttp.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "ru-RU,ru;q=0.9");
    }

    public void SetFallbackProviders(IEnumerable<IMusicProvider> fallbackProviders)
    {
        _fallbackProviders = fallbackProviders
            .Where(p => !string.Equals(p.BrandKey, BrandKey, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    public static string EnsureRequiredAudioCookies(string? cookieHeader)
    {
        var trimmed = (cookieHeader ?? "").Trim().TrimEnd(';');
        if (string.IsNullOrWhiteSpace(trimmed))
            return "";

        var sb = new StringBuilder(trimmed);
        if (!trimmed.Contains("remixaudio_show_alert_today=", StringComparison.OrdinalIgnoreCase))
            sb.Append("; remixaudio_show_alert_today=0");
        if (!trimmed.Contains("remixmdevice=", StringComparison.OrdinalIgnoreCase))
            sb.Append("; remixmdevice=1920/1080/2/!!-!!!!");
        return sb.ToString();
    }

    public static string ExtractAccessToken(string? raw)
    {
        var trimmed = (raw ?? "").Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return "";

        var m = AccessTokenInUrlRegex.Match(trimmed);
        if (m.Success)
            return Uri.UnescapeDataString(m.Groups[1].Value).Trim();

        if (trimmed.Contains("remixsid=", StringComparison.OrdinalIgnoreCase) ||
            (trimmed.Contains(';') && trimmed.Contains('=')))
            return "";

        if (trimmed.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[7..].Trim();

        return RawTokenRegex.IsMatch(trimmed) ? trimmed : "";
    }

    public static long ExtractUserIdFromUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return 0;
        var m = UserIdInUrlRegex.Match(raw);
        return m.Success && long.TryParse(m.Groups[1].Value, out long uid) ? uid : 0;
    }

    public async Task<(bool Ok, string Message)> VerifyAndSaveSessionAsync(
        string? rawInput,
        string? cookieHeader,
        long userId,
        string? userName,
        CancellationToken ct = default)
    {
        var token = ExtractAccessToken(rawInput);
        if (userId <= 0)
            userId = ExtractUserIdFromUrl(rawInput);

        var normalizedCookie = NormalizeCookieInput(
            !string.IsNullOrWhiteSpace(cookieHeader) ? cookieHeader : (string.IsNullOrWhiteSpace(token) ? rawInput : ""));

        // Сначала проверяем официальный Kate Mobile OAuth-токен (без запуска WebView2, если вход выполнен через системный браузер)
        if (!string.IsNullOrWhiteSpace(token) && (userId <= 0 || string.IsNullOrWhiteSpace(userName)))
        {
            var userDoc = await CallVkApiAsync("users.get", new Dictionary<string, string>(), token, "5.131", ct);
            if (userDoc != null &&
                userDoc.RootElement.TryGetProperty("response", out var respArr) &&
                respArr.ValueKind == JsonValueKind.Array &&
                respArr.GetArrayLength() > 0)
            {
                using (userDoc)
                {
                    var u = respArr[0];
                    if (userId <= 0 && u.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number)
                        userId = idEl.GetInt64();
                    var first = ReadStringOrNumber(u, "first_name") ?? "";
                    var last = ReadStringOrNumber(u, "last_name") ?? "";
                    var fullName = $"{first} {last}".Trim();
                    if (!string.IsNullOrWhiteSpace(fullName))
                        userName = fullName;
                }
            }
        }

        // Если cookies нужны (cookie-режим без токена), подтягиваем их из профиля или зондируем
        if (string.IsNullOrWhiteSpace(normalizedCookie) && string.IsNullOrWhiteSpace(token))
        {
            var bridgeCookies = await _bridge.RefreshCookiesAsync(ct);
            if (!string.IsNullOrWhiteSpace(bridgeCookies) &&
                bridgeCookies.Contains("remix", StringComparison.OrdinalIgnoreCase))
            {
                normalizedCookie = bridgeCookies;
            }
        }

        if ((userId <= 0 || string.IsNullOrWhiteSpace(userName)) &&
            (!string.IsNullOrWhiteSpace(normalizedCookie) || string.IsNullOrWhiteSpace(token)))
        {
            var (probedId, probedName) = await ProbeUserIdentityAsync(normalizedCookie, ct);
            if (userId <= 0 && probedId > 0) userId = probedId;
            if (string.IsNullOrWhiteSpace(userName) && !string.IsNullOrWhiteSpace(probedName))
                userName = probedName;

            if (string.IsNullOrWhiteSpace(normalizedCookie) && string.IsNullOrWhiteSpace(token))
            {
                var freshCookies = await _bridge.RefreshCookiesAsync(ct);
                if (!string.IsNullOrWhiteSpace(freshCookies))
                    normalizedCookie = freshCookies;
            }
        }

        if (userId <= 0)
            return (false, "Не удалось определить профиль ВКонтакте. Войдите в аккаунт в окне браузера.");

        normalizedCookie = EnsureRequiredAudioCookies(normalizedCookie);
        string displayName = string.IsNullOrWhiteSpace(userName) ? $"id{userId}" : userName.Trim();

        _settings.Update(s =>
        {
            if (!string.IsNullOrWhiteSpace(token))
                s.VkAccessToken = token;
            if (!string.IsNullOrWhiteSpace(normalizedCookie))
                s.VkCookieHeader = normalizedCookie;
            s.VkUserId = userId;
            s.VkUserName = displayName;
        }, notify: true);

        return (true, $"Подключён: {displayName}");
    }

    public void Disconnect()
    {
        _metaCache.Clear();
        _bridge.ResetSession();
        _settings.Update(s =>
        {
            s.VkAccessToken = null;
            s.VkCookieHeader = null;
            s.VkUserId = 0;
            s.VkUserName = null;
        }, notify: true);
    }

    public async Task<List<TrackItem>> SearchAsync(string query, int limit = 8, CancellationToken ct = default)
    {
        var result = new List<TrackItem>();
        if (!IsAuthenticated || string.IsNullOrWhiteSpace(query))
            return result;

        var s = _settings.Current;
        var uid = s.VkUserId;
        var token = s.VkAccessToken?.Trim();
        var hasExplicitCookie = !string.IsNullOrWhiteSpace(s.VkCookieHeader);

        if (hasExplicitCookie)
        {
            var cookie = await GetEffectiveCookieHeaderAsync(ct);
            try
            {
                string body = $"act=section&al=1&claim=0&is_layer=0&owner_id={uid}&q={Uri.EscapeDataString(query.Trim())}&section=search";
                string? raw = await PostAlAudioRawAsync("?act=section", body, cookie, uid, allowBridge: true, ct);
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    var extracted = ExtractAllTracksFromRawText(raw, uid, Math.Max(limit * 2, 24));
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    foreach (var tr in extracted)
                    {
                        if (seen.Add(tr.UniqueKey))
                        {
                            result.Add(tr);
                            if (result.Count >= limit) return result;
                        }
                    }
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { }
        }

        if (result.Count == 0 && !string.IsNullOrWhiteSpace(token))
        {
            try
            {
                using var doc = await CallVkApiAsync(
                    "audio.search",
                    new Dictionary<string, string>
                    {
                        ["q"] = query.Trim(),
                        ["auto_complete"] = "1",
                        ["count"] = Math.Clamp(limit, 1, 50).ToString()
                    },
                    token,
                    "5.131",
                    ct);
                if (doc != null && !TryGetVkApiError(doc.RootElement, out _))
                {
                    foreach (var tr in ExtractTracksFromVkApiResponse(doc.RootElement, limit))
                        result.Add(tr);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { }
        }

        return result;
    }

    public async Task<List<UserPlaylist>> FetchUserPlaylistsAsync(CancellationToken ct = default)
    {
        var playlists = new List<UserPlaylist>();
        if (!IsAuthenticated) return playlists;

        var s = _settings.Current;
        var uid = s.VkUserId;
        var token = s.VkAccessToken?.Trim();
        var hasExplicitCookie = !string.IsNullOrWhiteSpace(s.VkCookieHeader);

        // Если вход выполнен только через OAuth-токен (системный браузер), используем Kate Mobile API
        if (!string.IsNullOrWhiteSpace(token) && !hasExplicitCookie)
        {
            playlists = await FetchPlaylistsViaApiAsync(uid, token, ct);
            if (playlists.Count > 0)
                return playlists;
        }

        var cookie = await GetEffectiveCookieHeaderAsync(ct);

        // Слой 1: прямые HTTP-запросы к m.vk.ru/audio и vk.ru/al_audio.php (протокол python273/vk_api vk_audio.py)
        if (!string.IsNullOrWhiteSpace(cookie))
        {
            playlists = await FetchPlaylistsDirectHttpAsync(uid, cookie, ct);
        }

        // Фоллбек на Kate Mobile API при наличии OAuth-токена
        if (playlists.Count == 0 && !string.IsNullOrWhiteSpace(token))
        {
            playlists = await FetchPlaylistsViaApiAsync(uid, token, ct);
        }

        // Слой 2: живой сбор внутри браузерной сессии WebView2 (только если есть cookie-сессия или нет OAuth-токена)
        if (playlists.Count == 0 && (hasExplicitCookie || string.IsNullOrWhiteSpace(token)))
        {
            var bridgeJson = await _bridge.FetchFullLibraryJsonAsync(uid, ct);
            if (!string.IsNullOrWhiteSpace(bridgeJson))
            {
                playlists = ParseBridgeLibraryBundle(bridgeJson, uid);
                var freshCookie = await _bridge.RefreshCookiesAsync(ct);
                if (!string.IsNullOrWhiteSpace(freshCookie))
                    _settings.Update(st => st.VkCookieHeader = freshCookie, notify: false);
            }
        }

        return playlists;
    }

    private async Task<List<UserPlaylist>> FetchPlaylistsViaApiAsync(long uid, string token, CancellationToken ct)
    {
        var playlists = new List<UserPlaylist>();

        try
        {
            using var myDoc = await CallVkApiAsync(
                "audio.get",
                new Dictionary<string, string>
                {
                    ["owner_id"] = uid.ToString(),
                    ["count"] = "500"
                },
                token,
                "5.131",
                ct);
            if (myDoc != null && !TryGetVkApiError(myDoc.RootElement, out _))
            {
                var myTracks = ExtractTracksFromVkApiResponse(myDoc.RootElement, 500);
                if (myTracks.Count > 0)
                {
                    playlists.Add(new UserPlaylist
                    {
                        Name = "ВК • Моя музыка",
                        IsExternal = true,
                        PlatformBrand = "vk",
                        ExternalId = $"{uid}_-1",
                        CoverUrl = myTracks.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.CoverUrl))?.CoverUrl ?? "",
                        Tracks = myTracks
                    });
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { }

        try
        {
            using var plsDoc = await CallVkApiAsync(
                "audio.getPlaylists",
                new Dictionary<string, string>
                {
                    ["owner_id"] = uid.ToString(),
                    ["count"] = "50"
                },
                token,
                "5.131",
                ct);

            if (plsDoc != null &&
                !TryGetVkApiError(plsDoc.RootElement, out _) &&
                plsDoc.RootElement.TryGetProperty("response", out var respObj) &&
                respObj.TryGetProperty("items", out var plItems) &&
                plItems.ValueKind == JsonValueKind.Array)
            {
                foreach (var plEl in plItems.EnumerateArray())
                {
                    ct.ThrowIfCancellationRequested();
                    if (!long.TryParse(ReadStringOrNumber(plEl, "owner_id"), out long pOwner)) pOwner = uid;
                    if (!long.TryParse(ReadStringOrNumber(plEl, "id"), out long pId) || pId <= 0) continue;

                    string title = ReadStringOrNumber(plEl, "title") ?? $"Плейлист #{pId}";
                    string accessKey = ReadStringOrNumber(plEl, "access_key") ?? "";
                    string plCover = "";
                    if (plEl.TryGetProperty("photo", out var phEl))
                    {
                        plCover = ReadStringOrNumber(phEl, "photo_600")
                               ?? ReadStringOrNumber(phEl, "photo_300")
                               ?? ReadStringOrNumber(phEl, "photo_270")
                               ?? ReadStringOrNumber(phEl, "photo_1200")
                               ?? "";
                    }

                    var p = new Dictionary<string, string>
                    {
                        ["owner_id"] = pOwner.ToString(),
                        ["album_id"] = pId.ToString(),
                        ["count"] = "400"
                    };
                    if (!string.IsNullOrWhiteSpace(accessKey))
                        p["access_key"] = accessKey;

                    using var trDoc = await CallVkApiAsync("audio.get", p, token, "5.131", ct);
                    var tracks = trDoc != null && !TryGetVkApiError(trDoc.RootElement, out _)
                        ? ExtractTracksFromVkApiResponse(trDoc.RootElement, 400)
                        : new List<TrackItem>();

                    playlists.Add(new UserPlaylist
                    {
                        Name = title.Trim(),
                        IsExternal = true,
                        PlatformBrand = "vk",
                        ExternalId = string.IsNullOrWhiteSpace(accessKey)
                            ? $"{pOwner}_{pId}"
                            : $"{pOwner}_{pId}_{accessKey}",
                        CoverUrl = !string.IsNullOrWhiteSpace(plCover)
                            ? plCover
                            : (tracks.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.CoverUrl))?.CoverUrl ?? ""),
                        Tracks = tracks
                    });
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { }

        return playlists;
    }

    public async Task<List<TrackItem>> FetchVkMixTracksAsync(CancellationToken ct = default)
    {
        var tracks = new List<TrackItem>();
        if (!IsAuthenticated) return tracks;

        var token = _settings.Current.VkAccessToken?.Trim();
        if (!string.IsNullOrWhiteSpace(token))
        {
            try
            {
                using var doc = await CallVkApiAsync(
                    "audio.getStreamMixAudios",
                    new Dictionary<string, string>
                    {
                        ["mix_id"] = "common",
                        ["count"] = "15"
                    },
                    token,
                    "5.131",
                    ct);

                if (doc != null && !TryGetVkApiError(doc.RootElement, out _))
                    tracks.AddRange(ExtractTracksFromVkApiResponse(doc.RootElement, 15));
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }

        if (tracks.Count == 0)
        {
            try
            {
                var pls = await FetchUserPlaylistsAsync(ct);
                var pool = pls.SelectMany(p => p.Tracks).DistinctBy(t => t.Id).ToList();
                if (pool.Count > 0)
                    tracks.AddRange(pool.OrderBy(_ => Random.Shared.Next()).Take(15));
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }

        return tracks;
    }

    private TrackItem? ParseVkApiAudioItem(JsonElement item)
    {
        var oidStr = ReadStringOrNumber(item, "owner_id") ?? "";
        var idStr = ReadStringOrNumber(item, "id") ?? "";
        if (!long.TryParse(oidStr, out var ownerId) || !long.TryParse(idStr, out var audioId))
            return null;

        string artist = ReadStringOrNumber(item, "artist") ?? "ВК Музыка";
        string title = ReadStringOrNumber(item, "title") ?? "Без названия";
        int durSec = int.TryParse(ReadStringOrNumber(item, "duration"), out var d) ? d : 0;
        string url = NormalizePlayableVkStreamUrl(ReadStringOrNumber(item, "url")) ?? "";
        string ak = ReadStringOrNumber(item, "access_key") ?? "";

        string cover = "";
        if (item.TryGetProperty("album", out var alb) &&
            alb.TryGetProperty("thumb", out var thumb))
        {
            cover = ReadStringOrNumber(thumb, "photo_600")
                 ?? ReadStringOrNumber(thumb, "photo_300")
                 ?? ReadStringOrNumber(thumb, "photo_270")
                 ?? ReadStringOrNumber(thumb, "photo_1200")
                 ?? "";
        }

        string idKey = $"{ownerId}_{audioId}";
        _metaCache[idKey] = new VkAudioMeta
        {
            OwnerId = ownerId,
            AudioId = audioId,
            AccessKey = ak,
            RawUrl = url
        };

        string pageUrl = string.IsNullOrWhiteSpace(ak)
            ? $"https://vk.com/audio{ownerId}_{audioId}"
            : $"https://vk.com/audio{ownerId}_{audioId}?ak={Uri.EscapeDataString(ak)}";

        return new TrackItem
        {
            Id = idKey,
            Source = "ВК",
            BrandKey = "vk",
            BrandColor = BrandColorHex,
            Title = title,
            Artist = artist,
            PageUrl = pageUrl,
            CoverUrl = cover,
            Duration = durSec > 0 ? TimeSpan.FromSeconds(durSec) : TimeSpan.Zero
        };
    }

    private List<TrackItem> ExtractTracksFromVkApiResponse(JsonElement root, int maxCount)
    {
        var list = new List<TrackItem>();
        if (!root.TryGetProperty("response", out var resp)) return list;

        JsonElement arr = default;
        if (resp.ValueKind == JsonValueKind.Array)
            arr = resp;
        else if (resp.ValueKind == JsonValueKind.Object && resp.TryGetProperty("items", out var itemsEl) && itemsEl.ValueKind == JsonValueKind.Array)
            arr = itemsEl;
        else
            return list;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in arr.EnumerateArray())
        {
            var tr = ParseVkApiAudioItem(item);
            if (tr != null && seen.Add(tr.UniqueKey))
            {
                list.Add(tr);
                if (list.Count >= maxCount) break;
            }
        }
        return list;
    }

    private static bool TryGetVkApiError(JsonElement root, out string? errorMsg)
    {
        errorMsg = null;
        if (root.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.Object)
        {
            errorMsg = ReadStringOrNumber(errEl, "error_msg") ?? "Ошибка VK API";
            return true;
        }
        return false;
    }

    private async Task<string> GetEffectiveCookieHeaderAsync(CancellationToken ct)
    {
        var cookie = _settings.Current.VkCookieHeader;
        if (!string.IsNullOrWhiteSpace(cookie) && cookie.Contains("remix", StringComparison.OrdinalIgnoreCase))
            return EnsureRequiredAudioCookies(cookie);

        var refreshed = await _bridge.RefreshCookiesAsync(ct);
        if (!string.IsNullOrWhiteSpace(refreshed))
        {
            var withDefaults = EnsureRequiredAudioCookies(refreshed);
            _settings.Update(s => s.VkCookieHeader = withDefaults, notify: false);
            return withDefaults;
        }

        return EnsureRequiredAudioCookies(cookie);
    }

    private async Task<List<UserPlaylist>> FetchPlaylistsDirectHttpAsync(long uid, string cookie, CancellationToken ct)
    {
        var playlists = new List<UserPlaylist>();

        // 1. «ВК • Моя музыка» (m.vk.ru/audio act=load_section + vk.ru/al_audio.php act=section&section=all + load_catalog_section)
        var myTracks = await LoadUserMainTracksDirectAsync(uid, cookie, maxTracks: 200, ct);
        if (myTracks.Count > 0)
        {
            playlists.Add(new UserPlaylist
            {
                Id = $"vk_my_{uid}",
                Name = "ВК • Моя музыка",
                IsExternal = true,
                PlatformBrand = "vk",
                ExternalId = $"{uid}_-1",
                CoverUrl = myTracks.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.CoverUrl))?.CoverUrl ?? "",
                Tracks = myTracks
            });
        }

        // 2. Плейлисты пользователя (m.vk.ru/audio?act=audio_playlists{uid} + vk.ru/al_audio.php act=section&section=playlists)
        try
        {
            var descriptors = await LoadUserPlaylistDescriptorsAsync(uid, cookie, ct);
            foreach (var desc in descriptors.Take(15))
            {
                ct.ThrowIfCancellationRequested();
                if (desc.PlaylistId <= 0) continue;

                var tracks = await LoadPlaylistTracksDirectAsync(
                    desc.OwnerId,
                    desc.PlaylistId,
                    desc.AccessHash,
                    cookie,
                    uid,
                    maxTracks: 150,
                    ct);

                if (tracks.Count == 0 && desc.InlineTracks.Count > 0)
                    tracks = desc.InlineTracks;

                if (tracks.Count > 0)
                {
                    playlists.Add(new UserPlaylist
                    {
                        Id = $"vk_pl_{desc.OwnerId}_{desc.PlaylistId}",
                        Name = string.IsNullOrWhiteSpace(desc.Title) ? $"Плейлист #{desc.PlaylistId}" : desc.Title,
                        IsExternal = true,
                        PlatformBrand = "vk",
                        ExternalId = $"{desc.OwnerId}_{desc.PlaylistId}_{desc.AccessHash}",
                        CoverUrl = tracks.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.CoverUrl))?.CoverUrl ?? "",
                        Tracks = tracks
                    });
                }
            }
        }
        catch (OperationCanceledException) { }
        catch { }

        return playlists;
    }

    private async Task<List<TrackItem>> LoadUserMainTracksDirectAsync(long uid, string cookie, int maxTracks, CancellationToken ct)
    {
        var all = new List<TrackItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddUnique(IEnumerable<TrackItem> items)
        {
            foreach (var tr in items)
            {
                if (seen.Add(tr.UniqueKey))
                {
                    all.Add(tr);
                    if (all.Count >= maxTracks) break;
                }
            }
        }

        // 1а. Протокол vk_audio.py: POST https://m.vk.ru/audio (act=load_section, playlist_id=-1)
        var mobile = await LoadPlaylistTracksDirectAsync(uid, -1, "", cookie, uid, maxTracks, ct);
        AddUnique(mobile);
        if (all.Count > 0) return all;

        // 1б. Desktop al_audio.php: act=section&section=all + пагинация act=load_catalog_section (sectionId / nextFrom)
        string bodyAll = $"act=section&al=1&claim=0&is_layer=0&owner_id={uid}&section=all";
        string? rawAll = await PostAlAudioRawAsync("?act=section", bodyAll, cookie, uid, allowBridge: false, ct);
        if (!string.IsNullOrWhiteSpace(rawAll))
        {
            AddUnique(ExtractAllTracksFromRawText(rawAll, uid, maxTracks));

            var secMatch = SectionIdRegex.Match(rawAll);
            var nextMatch = NextFromRegex.Match(rawAll);
            string sectionId = secMatch.Success ? secMatch.Groups[1].Value : "";
            string nextFrom = nextMatch.Success ? nextMatch.Groups[1].Value : "";

            for (int page = 0; page < 4 && all.Count < maxTracks && !string.IsNullOrEmpty(sectionId) && !string.IsNullOrEmpty(nextFrom); page++)
            {
                ct.ThrowIfCancellationRequested();
                string catBody = $"al=1&act=load_catalog_section&section_id={Uri.EscapeDataString(sectionId)}&start_from={Uri.EscapeDataString(nextFrom)}";
                string? catRaw = await PostAlAudioRawAsync("?act=load_catalog_section", catBody, cookie, uid, allowBridge: false, ct);
                if (string.IsNullOrWhiteSpace(catRaw)) break;

                int before = all.Count;
                AddUnique(ExtractAllTracksFromRawText(catRaw, uid, maxTracks - all.Count));
                var nm = NextFromRegex.Match(catRaw);
                string nextVal = nm.Success ? nm.Groups[1].Value : "";
                if (all.Count == before || string.IsNullOrEmpty(nextVal) || nextVal == nextFrom)
                    break;
                nextFrom = nextVal;
            }
        }

        return all;
    }

    private async Task<List<TrackItem>> LoadPlaylistTracksDirectAsync(
        long ownerId,
        long playlistId,
        string accessHash,
        string cookie,
        long uid,
        int maxTracks,
        CancellationToken ct)
    {
        var all = new List<TrackItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Сначала пробуем m.vk.ru/audio (как в python273/vk_api vk_audio.py lines 110-123)
        string mobileForm = $"act=load_section&owner_id={ownerId}&playlist_id={playlistId}&offset=0&type=playlist&access_hash={Uri.EscapeDataString(accessHash ?? "")}&is_loading_all=1";
        string? mobileRaw = await PostMobileAudioDirectAsync(mobileForm, cookie, ct);
        if (!string.IsNullOrWhiteSpace(mobileRaw))
        {
            foreach (var tr in ExtractAllTracksFromRawText(mobileRaw, uid, maxTracks))
            {
                if (seen.Add(tr.UniqueKey))
                    all.Add(tr);
            }
            if (all.Count > 0) return all;
        }

        // 2. Затем пробуем vk.ru/al_audio.php (act=load_section)
        int offset = 0;
        for (int page = 0; page < 3 && all.Count < maxTracks; page++)
        {
            ct.ThrowIfCancellationRequested();
            var sb = new StringBuilder();
            sb.Append($"act=load_section&al=1&claim=0&is_loading_all=1&offset={offset}&owner_id={ownerId}&playlist_id={playlistId}&type=playlist");
            if (!string.IsNullOrWhiteSpace(accessHash))
                sb.Append($"&access_hash={Uri.EscapeDataString(accessHash)}");

            string? raw = await PostAlAudioRawAsync("?act=load_section", sb.ToString(), cookie, uid, allowBridge: false, ct);
            if (string.IsNullOrWhiteSpace(raw)) break;

            var batch = ExtractAllTracksFromRawText(raw, uid, maxTracks - all.Count);
            int added = 0;
            foreach (var tr in batch)
            {
                if (seen.Add(tr.UniqueKey))
                {
                    all.Add(tr);
                    added++;
                }
            }

            using var doc = ParseAlJson(raw);
            if (added == 0 || doc == null || !TryGetNextOffset(doc.RootElement, out int nextOffset, out bool hasMore) || !hasMore || nextOffset <= offset)
                break;

            offset = nextOffset;
        }

        return all;
    }

    private List<UserPlaylist> ParseBridgeLibraryBundle(string rawBundleOrArray, long fallbackUid)
    {
        var myTracks = new List<TrackItem>();
        var mySeen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var playlistsMap = new Dictionary<string, UserPlaylist>(StringComparer.OrdinalIgnoreCase);

        void ProcessSingleBundle(JsonElement bundleEl)
        {
            long uid = bundleEl.TryGetProperty("uid", out var uEl) && uEl.ValueKind == JsonValueKind.Number && uEl.GetInt64() > 0
                ? uEl.GetInt64()
                : fallbackUid;

            if (uid > 0 && _settings.Current.VkUserId <= 0)
                _settings.Update(s => s.VkUserId = uid, notify: false);

            var descTitles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (bundleEl.TryGetProperty("playlists", out var plsEl) && plsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in plsEl.EnumerateArray())
                {
                    long pOwner = p.TryGetProperty("ownerId", out var po) && po.ValueKind == JsonValueKind.Number ? po.GetInt64() : uid;
                    long pId = p.TryGetProperty("playlistId", out var pi) && pi.ValueKind == JsonValueKind.Number ? pi.GetInt64() : 0;
                    string pTitle = ReadStringOrNumber(p, "title")?.Trim() ?? "";
                    if (pId > 0 && !string.IsNullOrWhiteSpace(pTitle))
                        descTitles[$"{pOwner}_{pId}"] = WebUtility.HtmlDecode(pTitle);
                }
            }

            if (bundleEl.TryGetProperty("rawPayloads", out var rawsEl) && rawsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var rEl in rawsEl.EnumerateArray())
                {
                    if (rEl.ValueKind != JsonValueKind.String) continue;
                    var rawStr = rEl.GetString();
                    if (string.IsNullOrWhiteSpace(rawStr)) continue;

                    foreach (var tr in ExtractAllTracksFromRawText(rawStr, uid, 250))
                    {
                        if (mySeen.Add(tr.UniqueKey))
                            myTracks.Add(tr);
                    }

                    using var doc = ParseAlJson(rawStr);
                    if (doc != null)
                    {
                        var descMap = new Dictionary<string, VkPlaylistDescriptor>(StringComparer.OrdinalIgnoreCase);
                        CollectPlaylistDescriptors(doc.RootElement, uid, descMap);
                        foreach (var kv in descMap)
                        {
                            if (!string.IsNullOrWhiteSpace(kv.Value.Title))
                                descTitles[kv.Key] = kv.Value.Title;

                            if (kv.Value.InlineTracks.Count > 0 && !playlistsMap.ContainsKey(kv.Key))
                            {
                                playlistsMap[kv.Key] = new UserPlaylist
                                {
                                    Id = $"vk_pl_{kv.Key}",
                                    Name = kv.Value.Title,
                                    IsExternal = true,
                                    PlatformBrand = "vk",
                                    ExternalId = $"{kv.Value.OwnerId}_{kv.Value.PlaylistId}_{kv.Value.AccessHash}",
                                    CoverUrl = kv.Value.InlineTracks.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.CoverUrl))?.CoverUrl ?? "",
                                    Tracks = kv.Value.InlineTracks
                                };
                            }
                        }
                    }
                }
            }

            if (bundleEl.TryGetProperty("domTracks", out var domEl) && domEl.ValueKind == JsonValueKind.Array)
            {
                int domIdx = 0;
                foreach (var d in domEl.EnumerateArray())
                {
                    domIdx++;
                    string title = WebUtility.HtmlDecode(ReadStringOrNumber(d, "title") ?? "").Trim();
                    string artist = WebUtility.HtmlDecode(ReadStringOrNumber(d, "artist") ?? "").Trim();
                    if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(artist)) continue;

                    string fullId = ReadStringOrNumber(d, "fullId")?.Trim() ?? $"{uid}_{domIdx}";
                    string durText = ReadStringOrNumber(d, "durText")?.Trim() ?? "";
                    string cover = ReadStringOrNumber(d, "cover")?.Trim() ?? "";

                    var tr = new TrackItem
                    {
                        Id = fullId,
                        Source = "ВК",
                        BrandKey = "vk",
                        BrandColor = BrandColorHex,
                        Title = string.IsNullOrWhiteSpace(title) ? "Аудиозапись ВК" : title,
                        Artist = string.IsNullOrWhiteSpace(artist) ? "ВКонтакте" : artist,
                        PageUrl = $"https://vk.ru/audio{fullId}",
                        CoverUrl = cover,
                        Duration = ParseDurationText(durText)
                    };
                    if (mySeen.Add(tr.UniqueKey))
                        myTracks.Add(tr);
                }
            }

            if (bundleEl.TryGetProperty("playlistPayloads", out var plPayloadsEl) && plPayloadsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var plItem in plPayloadsEl.EnumerateArray())
                {
                    long pOwner = plItem.TryGetProperty("ownerId", out var po) && po.ValueKind == JsonValueKind.Number ? po.GetInt64() : uid;
                    long pId = plItem.TryGetProperty("playlistId", out var pi) && pi.ValueKind == JsonValueKind.Number ? pi.GetInt64() : 0;
                    if (pId <= 0) continue;

                    string pHash = ReadStringOrNumber(plItem, "accessHash") ?? "";
                    string pTitle = WebUtility.HtmlDecode(ReadStringOrNumber(plItem, "title") ?? "").Trim();
                    string pRaw = ReadStringOrNumber(plItem, "raw") ?? "";
                    if (string.IsNullOrWhiteSpace(pRaw)) continue;

                    string key = $"{pOwner}_{pId}";
                    if (string.IsNullOrWhiteSpace(pTitle) && descTitles.TryGetValue(key, out var mappedTitle))
                        pTitle = mappedTitle;
                    if (string.IsNullOrWhiteSpace(pTitle))
                        pTitle = $"Плейлист ВК #{pId}";

                    var plTracks = ExtractAllTracksFromRawText(pRaw, uid, 200);
                    if (plTracks.Count > 0)
                    {
                        playlistsMap[key] = new UserPlaylist
                        {
                            Id = $"vk_pl_{key}",
                            Name = pTitle,
                            IsExternal = true,
                            PlatformBrand = "vk",
                            ExternalId = $"{pOwner}_{pId}_{pHash}",
                            CoverUrl = plTracks.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.CoverUrl))?.CoverUrl ?? "",
                            Tracks = plTracks
                        };
                    }
                }
            }
        }

        try
        {
            using var outerDoc = JsonDocument.Parse(rawBundleOrArray);
            if (outerDoc.RootElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in outerDoc.RootElement.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        var innerJson = item.GetString();
                        if (!string.IsNullOrWhiteSpace(innerJson))
                        {
                            using var innerDoc = JsonDocument.Parse(innerJson);
                            ProcessSingleBundle(innerDoc.RootElement);
                        }
                    }
                    else if (item.ValueKind == JsonValueKind.Object)
                    {
                        ProcessSingleBundle(item);
                    }
                }
            }
            else if (outerDoc.RootElement.ValueKind == JsonValueKind.Object)
            {
                ProcessSingleBundle(outerDoc.RootElement);
            }
        }
        catch { }

        var result = new List<UserPlaylist>();
        if (myTracks.Count > 0)
        {
            result.Add(new UserPlaylist
            {
                Id = $"vk_my_{fallbackUid}",
                Name = "ВК • Моя музыка",
                IsExternal = true,
                PlatformBrand = "vk",
                ExternalId = $"{fallbackUid}_-1",
                CoverUrl = myTracks.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t.CoverUrl))?.CoverUrl ?? "",
                Tracks = myTracks
            });
        }

        result.AddRange(playlistsMap.Values);
        return result;
    }

    private static TimeSpan ParseDurationText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return TimeSpan.Zero;
        var parts = text.Trim().Split(':');
        if (parts.Length == 2 && int.TryParse(parts[0], out int m) && int.TryParse(parts[1], out int s))
            return TimeSpan.FromSeconds(m * 60 + s);
        if (parts.Length == 3 && int.TryParse(parts[0], out int h) && int.TryParse(parts[1], out int m2) && int.TryParse(parts[2], out int s2))
            return TimeSpan.FromSeconds(h * 3600 + m2 * 60 + s2);
        return TimeSpan.Zero;
    }

    public async Task<string?> ResolveStreamUrlAsync(TrackItem track, CancellationToken ct = default)
    {
        var s = _settings.Current;
        var uid = s.VkUserId;

        // Этап 1: прямой поток ВКонтакте (кэш / m.vk.ru reload_audio / vk.ru al_audio reload_audios)
        if (IsAuthenticated)
        {
            var meta = ResolveTrackMeta(track);
            if (meta != null)
            {
                if (!string.IsNullOrWhiteSpace(meta.RawUrl))
                {
                    var cachedUnmasked = NormalizePlayableVkStreamUrl(UnmaskAudioUrl(meta.RawUrl, uid));
                    if (IsPlayableUrl(cachedUnmasked))
                        return cachedUnmasked;
                }

                var token = s.VkAccessToken?.Trim();
                if (!string.IsNullOrWhiteSpace(token))
                {
                    try
                    {
                        string audioArg = !string.IsNullOrWhiteSpace(meta.AccessKey)
                            ? $"{meta.OwnerId}_{meta.AudioId}_{meta.AccessKey}"
                            : $"{meta.OwnerId}_{meta.AudioId}";
                        using var byIdDoc = await CallVkApiAsync(
                            "audio.getById",
                            new Dictionary<string, string> { ["audios"] = audioArg },
                            token,
                            "5.131",
                            ct);
                        if (byIdDoc != null && !TryGetVkApiError(byIdDoc.RootElement, out _))
                        {
                            var fetched = ExtractTracksFromVkApiResponse(byIdDoc.RootElement, 1);
                            if (fetched.Count > 0 && _metaCache.TryGetValue(fetched[0].Id, out var freshMeta))
                            {
                                var apiUrl = NormalizePlayableVkStreamUrl(freshMeta.RawUrl);
                                if (IsPlayableUrl(apiUrl))
                                {
                                    meta.RawUrl = freshMeta.RawUrl;
                                    return apiUrl;
                                }
                            }
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch { }
                }

                bool hasExplicitCookie = !string.IsNullOrWhiteSpace(s.VkCookieHeader);
                if (hasExplicitCookie || string.IsNullOrWhiteSpace(token))
                {
                    var cookie = await GetEffectiveCookieHeaderAsync(ct);
                    string fullId = (!string.IsNullOrWhiteSpace(meta.ActionHash) && !string.IsNullOrWhiteSpace(meta.UrlHash))
                        ? $"{meta.OwnerId}_{meta.AudioId}_{meta.ActionHash}_{meta.UrlHash}"
                        : $"{meta.OwnerId}_{meta.AudioId}";

                    // 1а. Протокол vk_audio.py (line 660): POST https://m.vk.ru/audio (act=reload_audio&ids=...)
                    string mobileReloadForm = $"act=reload_audio&ids={Uri.EscapeDataString(fullId)}";
                    string? mobileRaw = await PostMobileAudioDirectAsync(mobileReloadForm, cookie, ct)
                                        ?? (hasExplicitCookie ? await _bridge.PostMobileAudioAsync(mobileReloadForm, uid, ct) : null);

                    if (!string.IsNullOrWhiteSpace(mobileRaw))
                    {
                        using var mDoc = ParseAlJson(mobileRaw);
                        if (mDoc != null)
                        {
                            foreach (var cand in ExtractRawUrlsFromElement(mDoc.RootElement))
                            {
                                var unmasked = NormalizePlayableVkStreamUrl(UnmaskAudioUrl(cand.RawUrl, uid));
                                if (IsPlayableUrl(unmasked))
                                {
                                    meta.RawUrl = cand.RawUrl;
                                    return unmasked;
                                }
                            }
                        }
                    }

                    // 1б. Desktop al_audio.php (act=reload_audios)
                    string body = $"act=reload_audios&al=1&ids={Uri.EscapeDataString(fullId)}";
                    string? raw = await PostAlAudioRawAsync("?act=reload_audios", body, cookie, uid, allowBridge: hasExplicitCookie, ct);
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        using var doc = ParseAlJson(raw);
                        if (doc != null)
                        {
                            foreach (var candidate in ExtractRawUrlsFromElement(doc.RootElement))
                            {
                                var unmasked = NormalizePlayableVkStreamUrl(UnmaskAudioUrl(candidate.RawUrl, uid));
                                if (IsPlayableUrl(unmasked))
                                {
                                    meta.RawUrl = candidate.RawUrl;
                                    return unmasked;
                                }
                            }
                        }
                    }
                }
            }
        }

        // Этап 2: верифицированный подбор как в BuevichRoman/yandex2spotify (TrackMatcher ±3 с + артист)
        return await ResolveViaVerifiedMatchAsync(track, ct);
    }

    public async Task<(bool Ok, string Message)> RemoveTrackFromPlaylistAsync(
        string externalPlaylistId,
        string playlistTitle,
        TrackItem track,
        CancellationToken ct = default)
    {
        if (!IsAuthenticated)
            return (false, "Подключите аккаунт ВКонтакте в Настройках.");

        var meta = ResolveTrackMeta(track);
        if (meta == null)
            return (false, "Не удалось определить ID аудиозаписи ВКонтакте.");

        var s = _settings.Current;
        var uid = s.VkUserId;
        var token = s.VkAccessToken?.Trim();

        try
        {
            if (externalPlaylistId.EndsWith("_-1", StringComparison.Ordinal))
            {
                if (!string.IsNullOrWhiteSpace(token))
                {
                    using var doc = await CallVkApiAsync(
                        "audio.delete",
                        new Dictionary<string, string>
                        {
                            ["audio_id"] = meta.AudioId.ToString(),
                            ["owner_id"] = meta.OwnerId.ToString()
                        },
                        token,
                        "5.131",
                        ct);

                    string? apiErr = null;
                    if (doc != null && !TryGetVkApiError(doc.RootElement, out apiErr))
                        return (true, $"Удалено из «Моей музыки» ВК: {track.Title}");

                    if (!string.IsNullOrWhiteSpace(apiErr))
                        return (false, $"ВК: {apiErr}");
                }

                if (!string.IsNullOrWhiteSpace(meta.ActionHash) && !string.IsNullOrWhiteSpace(s.VkCookieHeader))
                {
                    var cookie = await GetEffectiveCookieHeaderAsync(ct);
                    string form = $"act=delete_audio&aid={meta.AudioId}&al=1&hash={Uri.EscapeDataString(meta.ActionHash)}&oid={meta.OwnerId}&restore=1";
                    string? raw = await PostAlAudioRawAsync("?act=delete_audio", form, cookie, uid, allowBridge: true, ct);
                    if (!string.IsNullOrWhiteSpace(raw) && !LooksUnauthenticated(raw))
                        return (true, $"Удалено из «Моей музыки» ВК: {track.Title}");
                }

                return (false, "Не удалось удалить трек из ВК (переподключите ВК через браузер по OAuth).");
            }

            var parts = (externalPlaylistId ?? "").Split('_', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 ||
                !long.TryParse(parts[0], out long plOwnerId) ||
                !long.TryParse(parts[1], out long plId))
            {
                return (false, "Некорректный идентификатор плейлиста ВК.");
            }

            if (plOwnerId != uid)
                return (false, "Нельзя изменять чужой плейлист ВКонтакте.");

            string accessKey = parts.Length >= 3 ? parts[2] : "";
            if (string.IsNullOrWhiteSpace(token))
                return (false, "Для удаления из плейлистов ВК переподключите аккаунт через браузер (OAuth).");

            var getParams = new Dictionary<string, string>
            {
                ["owner_id"] = plOwnerId.ToString(),
                ["album_id"] = plId.ToString(),
                ["count"] = "1000"
            };
            if (!string.IsNullOrWhiteSpace(accessKey))
                getParams["access_key"] = accessKey;

            using var listDoc = await CallVkApiAsync("audio.get", getParams, token, "5.131", ct);
            string? listErr = null;
            if (listDoc == null || TryGetVkApiError(listDoc.RootElement, out listErr))
                return (false, $"Не удалось загрузить состав плейлиста ВК: {(string.IsNullOrWhiteSpace(listErr) ? "ошибка API" : listErr)}");

            var currentTracks = ExtractTracksFromVkApiResponse(listDoc.RootElement, 1000);
            var remainingIds = new List<string>();
            bool removedFound = false;

            foreach (var cur in currentTracks)
            {
                var curMeta = ResolveTrackMeta(cur);
                if (curMeta != null && curMeta.OwnerId == meta.OwnerId && curMeta.AudioId == meta.AudioId)
                {
                    removedFound = true;
                    continue;
                }
                if (curMeta != null)
                    remainingIds.Add($"{curMeta.OwnerId}_{curMeta.AudioId}");
            }

            if (!removedFound)
                return (true, $"Трек «{track.Title}» уже отсутствует в плейлисте ВК.");

            var editParams = new Dictionary<string, string>
            {
                ["owner_id"] = plOwnerId.ToString(),
                ["playlist_id"] = plId.ToString(),
                ["title"] = string.IsNullOrWhiteSpace(playlistTitle) ? "Плейлист ВК" : playlistTitle,
                ["audio_ids"] = string.Join(",", remainingIds)
            };

            using var editDoc = await CallVkApiAsync("audio.editPlaylist", editParams, token, "5.131", ct);
            string? editErr = null;
            if (editDoc != null && !TryGetVkApiError(editDoc.RootElement, out editErr))
                return (true, $"Удалено из плейлиста ВК: {track.Title}");

            return (false, !string.IsNullOrWhiteSpace(editErr) ? $"ВК: {editErr}" : "Не удалось обновить плейлист ВК.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, $"Ошибка синхронизации удаления с ВК: {ex.Message}");
        }
    }

    private static string? NormalizePlayableVkStreamUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (url.Contains("index.m3u8", StringComparison.OrdinalIgnoreCase))
        {
            var converted = M3u8ToMp3Regex.Replace(url, "$1/$2.mp3");
            if (!string.IsNullOrWhiteSpace(converted))
                return converted;
        }
        return url;
    }

    private async Task<string?> ResolveViaVerifiedMatchAsync(TrackItem track, CancellationToken ct)
    {
        var activeFallbacks = _fallbackProviders
            .Where(p => p is not YandexMusicProvider ym || ym.IsAuthenticated)
            .ToList();

        if (activeFallbacks.Count == 0) return null;

        var query = TrackMatcher.BuildSearchQuery(track.Artist, track.Title);
        var (winner, provider) = await SearchAndMatchAcrossFallbacksAsync(track, query, activeFallbacks, ct);

        if (winner == null && TrackMatcher.HasMultipleArtists(track.Artist))
        {
            var shortQuery = TrackMatcher.BuildSearchQuery(TrackMatcher.GetPrimaryArtist(track.Artist), track.Title);
            if (!string.Equals(shortQuery, query, StringComparison.OrdinalIgnoreCase))
            {
                (winner, provider) = await SearchAndMatchAcrossFallbacksAsync(track, shortQuery, activeFallbacks, ct);
            }
        }

        if (winner != null && provider != null)
        {
            var url = await provider.ResolveStreamUrlAsync(winner, ct);
            if (!string.IsNullOrWhiteSpace(url)) return url;
        }

        return null;
    }

    private async Task<(TrackItem? Winner, IMusicProvider? Provider)>
        SearchAndMatchAcrossFallbacksAsync(
            TrackItem target,
            string query,
            List<IMusicProvider> providers,
            CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query))
            return (null, null);

        var tasks = providers.Select(async p =>
        {
            try
            {
                var items = await p.SearchAsync(query, 8, ct);
                return (Provider: p, Items: items);
            }
            catch
            {
                return (Provider: p, Items: new List<TrackItem>());
            }
        }).ToList();

        var batches = await Task.WhenAll(tasks);

        var candidateToProvider = new Dictionary<TrackItem, IMusicProvider>();
        var allCandidates = new List<TrackItem>();

        foreach (var (prov, items) in batches)
        {
            foreach (var item in items)
            {
                candidateToProvider[item] = prov;
                allCandidates.Add(item);
            }
        }

        var winner = TrackMatcher.PickBestMatch(target, allCandidates);
        if (winner != null && candidateToProvider.TryGetValue(winner, out var winProv))
            return (winner, winProv);

        return (null, null);
    }

    private async Task<JsonDocument?> CallVkApiAsync(
        string method,
        Dictionary<string, string> parameters,
        string token,
        string apiVersion,
        CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                var formParams = new Dictionary<string, string>(parameters)
                {
                    ["access_token"] = token,
                    ["v"] = apiVersion
                };

                using var req = new HttpRequestMessage(HttpMethod.Post, VkApiBase + method)
                {
                    Content = new FormUrlEncodedContent(formParams)
                };

                using var resp = await _apiHttp.SendAsync(req, ct);
                if ((int)resp.StatusCode >= 500 && attempt < MaxRetries)
                {
                    int delaySec = Math.Min(1 << attempt, 16);
                    await Task.Delay(TimeSpan.FromSeconds(delaySec), ct);
                    continue;
                }

                if (!resp.IsSuccessStatusCode) return null;
                var json = await resp.Content.ReadAsStringAsync(ct);
                return JsonDocument.Parse(json);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception) when (attempt < MaxRetries)
            {
                int delaySec = Math.Min(1 << attempt, 16);
                await Task.Delay(TimeSpan.FromSeconds(delaySec), ct);
            }
            catch
            {
                return null;
            }
        }
    }

    private async Task<List<VkPlaylistDescriptor>> LoadUserPlaylistDescriptorsAsync(long uid, string cookie, CancellationToken ct)
    {
        var map = new Dictionary<string, VkPlaylistDescriptor>(StringComparer.OrdinalIgnoreCase);

        // 1. Mobile m.vk.ru/audio?act=audio_playlists{uid} (как в python273/vk_api vk_audio.py lines 169-185)
        foreach (var mDomain in new[] { "https://m.vk.ru", "https://m.vk.com" })
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, $"{mDomain}/audio?act=audio_playlists{uid}&offset=0");
                req.Headers.TryAddWithoutValidation("Cookie", EnsureRequiredAudioCookies(cookie));
                using var resp = await _http.SendAsync(req, ct);
                if (resp.IsSuccessStatusCode)
                {
                    var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                    var html = DecodeResponseBytes(bytes, resp.Content.Headers.ContentType?.CharSet);
                    foreach (Match m in MobileAlbumItemRegex.Matches(html))
                    {
                        if (!long.TryParse(m.Groups[1].Value, out long pOwner)) continue;
                        if (!long.TryParse(m.Groups[2].Value, out long pId) || pId <= 0) continue;
                        string hash = m.Groups[3].Value;
                        string title = WebUtility.HtmlDecode(m.Groups[4].Value).Trim();
                        string key = $"{pOwner}_{pId}";
                        if (!map.ContainsKey(key) && !string.IsNullOrWhiteSpace(title))
                        {
                            map[key] = new VkPlaylistDescriptor
                            {
                                OwnerId = pOwner,
                                PlaylistId = pId,
                                AccessHash = hash,
                                Title = title
                            };
                        }
                    }
                    if (map.Count > 0) break;
                }
            }
            catch { }
        }

        // 2. Desktop vk.ru/al_audio.php (act=section&section=playlists / all)
        foreach (var section in new[] { "playlists", "all" })
        {
            string body = $"act=section&al=1&claim=0&is_layer=0&owner_id={uid}&section={section}";
            string? raw = await PostAlAudioRawAsync("?act=section", body, cookie, uid, allowBridge: false, ct);
            if (string.IsNullOrWhiteSpace(raw)) continue;

            using var doc = ParseAlJson(raw);
            if (doc != null)
            {
                CollectPlaylistDescriptors(doc.RootElement, uid, map);
            }

            foreach (Match m in PlaylistHrefRegex.Matches(raw))
            {
                if (!long.TryParse(m.Groups[1].Value, out long pOwner)) continue;
                if (!long.TryParse(m.Groups[2].Value, out long pId) || pId <= 0) continue;
                string hash = m.Groups[3].Value;
                string title = WebUtility.HtmlDecode(m.Groups[4].Value).Trim();
                string key = $"{pOwner}_{pId}";
                if (!map.ContainsKey(key) && !string.IsNullOrWhiteSpace(title))
                {
                    map[key] = new VkPlaylistDescriptor
                    {
                        OwnerId = pOwner,
                        PlaylistId = pId,
                        AccessHash = hash,
                        Title = title
                    };
                }
            }

            if (map.Count > 0) break;
        }

        return map.Values.ToList();
    }

    private void CollectPlaylistDescriptors(JsonElement el, long currentUid, Dictionary<string, VkPlaylistDescriptor> map)
    {
        if (el.ValueKind == JsonValueKind.Object)
        {
            if (el.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.Number &&
                el.TryGetProperty("title", out var titleEl) && titleEl.ValueKind == JsonValueKind.String)
            {
                long pId = idEl.GetInt64();
                long pOwner = el.TryGetProperty("ownerId", out var oEl) && oEl.ValueKind == JsonValueKind.Number
                    ? oEl.GetInt64()
                    : (el.TryGetProperty("owner_id", out var o2El) && o2El.ValueKind == JsonValueKind.Number
                        ? o2El.GetInt64()
                        : currentUid);
                string title = WebUtility.HtmlDecode(titleEl.GetString() ?? "").Trim();
                string hash = el.TryGetProperty("accessHash", out var hEl) && hEl.ValueKind == JsonValueKind.String
                    ? hEl.GetString() ?? ""
                    : (el.TryGetProperty("access_hash", out var h2El) && h2El.ValueKind == JsonValueKind.String
                        ? h2El.GetString() ?? ""
                        : "");

                if (pId > 0 && !string.IsNullOrWhiteSpace(title))
                {
                    string key = $"{pOwner}_{pId}";
                    if (!map.ContainsKey(key))
                    {
                        var inline = new List<TrackItem>();
                        if (el.TryGetProperty("list", out var listEl))
                            inline = ExtractTracksFromElement(listEl, currentUid, 150);

                        map[key] = new VkPlaylistDescriptor
                        {
                            OwnerId = pOwner,
                            PlaylistId = pId,
                            AccessHash = hash,
                            Title = title,
                            InlineTracks = inline
                        };
                    }
                }
            }

            foreach (var prop in el.EnumerateObject())
                CollectPlaylistDescriptors(prop.Value, currentUid, map);
        }
        else if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in el.EnumerateArray())
                CollectPlaylistDescriptors(item, currentUid, map);
        }
    }

    private async Task<string?> PostMobileAudioDirectAsync(string formBody, string cookieHeader, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(cookieHeader)) return null;
        var fullCookie = EnsureRequiredAudioCookies(cookieHeader);

        foreach (var domain in new[] { "https://m.vk.ru", "https://m.vk.com" })
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, domain + "/audio");
                req.Headers.TryAddWithoutValidation("Cookie", fullCookie);
                req.Headers.TryAddWithoutValidation("Origin", domain);
                req.Headers.TryAddWithoutValidation("Referer", domain + "/audio");
                req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
                req.Content = new StringContent(formBody, Encoding.UTF8, "application/x-www-form-urlencoded");

                using var resp = await _http.SendAsync(req, ct);
                if (!resp.IsSuccessStatusCode) continue;
                var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                var text = DecodeResponseBytes(bytes, resp.Content.Headers.ContentType?.CharSet);

                if (!string.IsNullOrWhiteSpace(text) && !LooksUnauthenticated(text))
                    return text;
            }
            catch (OperationCanceledException) { throw; }
            catch { }
        }

        return null;
    }

    private async Task<string?> PostAlAudioRawAsync(
        string queryString,
        string formBody,
        string cookieHeader,
        long userId,
        bool allowBridge,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(cookieHeader))
        {
            var fullCookie = EnsureRequiredAudioCookies(cookieHeader);
            foreach (var domain in new[] { "https://vk.ru", "https://vk.com" })
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, domain + "/al_audio.php" + queryString);
                    req.Headers.TryAddWithoutValidation("Cookie", fullCookie);
                    req.Headers.TryAddWithoutValidation("Origin", domain);
                    req.Headers.TryAddWithoutValidation("Referer", $"{domain}/audios{(userId > 0 ? userId.ToString() : "")}");
                    req.Headers.TryAddWithoutValidation("X-Requested-With", "XMLHttpRequest");
                    req.Content = new StringContent(formBody, Encoding.UTF8, "application/x-www-form-urlencoded");

                    using var resp = await _http.SendAsync(req, ct);
                    if (!resp.IsSuccessStatusCode) continue;
                    var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                    var charset = resp.Content.Headers.ContentType?.CharSet;
                    var text = DecodeResponseBytes(bytes, charset);

                    if (!string.IsNullOrWhiteSpace(text) && !LooksUnauthenticated(text))
                        return text;
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch { }
            }
        }

        if (!allowBridge)
            return null;

        var bridgeText = await _bridge.PostAlAudioAsync(queryString, formBody, userId, ct);
        if (!string.IsNullOrWhiteSpace(bridgeText) && !LooksUnauthenticated(bridgeText))
        {
            var freshCookies = await _bridge.RefreshCookiesAsync(ct);
            if (!string.IsNullOrWhiteSpace(freshCookies) && IsAuthenticated)
                _settings.Update(s => s.VkCookieHeader = freshCookies, notify: false);
            return bridgeText;
        }

        return null;
    }

    private async Task<(long UserId, string? UserName)> ProbeUserIdentityAsync(string? cookieHeader, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(cookieHeader))
        {
            var fullCookie = EnsureRequiredAudioCookies(cookieHeader);
            var probeUrls = new[]
            {
                "https://vk.ru/audios",
                "https://vk.com/audios",
                "https://m.vk.ru/audio"
            };
            foreach (var url in probeUrls)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.TryAddWithoutValidation("Cookie", fullCookie);
                    using var resp = await _http.SendAsync(req, ct);

                    var loc = resp.Headers.Location?.ToString();
                    if (!string.IsNullOrWhiteSpace(loc))
                    {
                        var mLoc = AudiosRedirectUidRegex.Match(loc);
                        if (mLoc.Success && long.TryParse(mLoc.Groups[1].Value, out long locUid) && locUid > 0)
                            return (locUid, null);
                    }

                    var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
                    var html = DecodeResponseBytes(bytes, resp.Content.Headers.ContentType?.CharSet);

                    if (!string.IsNullOrWhiteSpace(html) && !html.Contains("badbrowser", StringComparison.OrdinalIgnoreCase))
                    {
                        var m = VkUidRegex.Match(html);
                        if (m.Success && long.TryParse(m.Groups[1].Value, out long uid) && uid > 0)
                            return (uid, null);
                    }
                }
                catch { }
            }
        }

        return await _bridge.ProbeIdentityAsync(ct);
    }

    private static bool LooksUnauthenticated(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return true;
        if (raw.Contains("badbrowser.php", StringComparison.OrdinalIgnoreCase)) return true;

        var trimmed = raw.TrimStart();
        if (trimmed.StartsWith("<!--", StringComparison.Ordinal))
            trimmed = trimmed[4..].TrimStart();

        if (trimmed.StartsWith("[", StringComparison.Ordinal))
            return false;

        if (!trimmed.StartsWith("{", StringComparison.Ordinal))
            return !raw.Contains("data-audio=", StringComparison.OrdinalIgnoreCase);

        try
        {
            using var doc = JsonDocument.Parse(trimmed);
            if (doc.RootElement.TryGetProperty("payload", out var pl) &&
                pl.ValueKind == JsonValueKind.Array &&
                pl.GetArrayLength() >= 2 &&
                pl[0].ValueKind == JsonValueKind.Number &&
                pl[0].GetInt32() == 3)
            {
                return true;
            }

            if (doc.RootElement.TryGetProperty("data", out var dataArr) &&
                dataArr.ValueKind == JsonValueKind.Array &&
                dataArr.GetArrayLength() > 0 &&
                (dataArr[0].ValueKind == JsonValueKind.False || dataArr[0].ValueKind == JsonValueKind.Null))
            {
                return true;
            }
        }
        catch
        {
            return !raw.Contains("data-audio=", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static JsonDocument? ParseAlJson(string raw)
    {
        try
        {
            var trimmed = raw.TrimStart();
            if (trimmed.StartsWith("<!--", StringComparison.Ordinal))
                trimmed = trimmed[4..].TrimStart();

            if (trimmed.StartsWith("[", StringComparison.Ordinal) || trimmed.StartsWith("{", StringComparison.Ordinal))
                return JsonDocument.Parse(trimmed);

            int jsonStart = trimmed.IndexOf('{');
            if (jsonStart < 0) return null;
            return JsonDocument.Parse(trimmed[jsonStart..]);
        }
        catch
        {
            return null;
        }
    }

    private List<TrackItem> ExtractAllTracksFromRawText(string raw, long vkId, int maxCount)
    {
        var results = new List<TrackItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var doc = ParseAlJson(raw);
        if (doc != null)
        {
            foreach (var tr in ExtractTracksFromElement(doc.RootElement, vkId, maxCount))
            {
                if (seen.Add(tr.UniqueKey))
                {
                    results.Add(tr);
                    if (results.Count >= maxCount) return results;
                }
            }
        }

        foreach (Match m in DataAudioAttrRegex.Matches(raw))
        {
            var decodedJson = WebUtility.HtmlDecode(m.Groups[1].Value);
            try
            {
                using var attrDoc = JsonDocument.Parse(decodedJson);
                var tr = ParseAudioTuple(attrDoc.RootElement, vkId);
                if (tr != null && seen.Add(tr.UniqueKey))
                {
                    results.Add(tr);
                    if (results.Count >= maxCount) return results;
                }
            }
            catch { }
        }

        return results;
    }

    private List<TrackItem> ExtractTracksFromElement(JsonElement root, long vkId, int maxCount)
    {
        var results = new List<TrackItem>();
        WalkAudioArrays(root, arr =>
        {
            if (results.Count >= maxCount) return false;
            var item = ParseAudioTuple(arr, vkId);
            if (item != null) results.Add(item);
            return results.Count < maxCount;
        });
        return results;
    }

    private List<(long OwnerId, long AudioId, string RawUrl)> ExtractRawUrlsFromElement(JsonElement root)
    {
        var results = new List<(long, long, string)>();
        WalkAudioArrays(root, arr =>
        {
            if (arr.GetArrayLength() >= 6 &&
                arr[0].ValueKind == JsonValueKind.Number &&
                arr[1].ValueKind == JsonValueKind.Number &&
                arr[2].ValueKind == JsonValueKind.String)
            {
                long audioId = arr[0].GetInt64();
                long ownerId = arr[1].GetInt64();
                string rawUrl = arr[2].GetString() ?? "";
                if (!string.IsNullOrWhiteSpace(rawUrl))
                    results.Add((ownerId, audioId, rawUrl));
            }
            return true;
        });
        return results;
    }

    private static void WalkAudioArrays(JsonElement el, Func<JsonElement, bool> onAudioTuple)
    {
        if (el.ValueKind == JsonValueKind.Array)
        {
            int len = el.GetArrayLength();
            if (len >= 6 &&
                el[0].ValueKind == JsonValueKind.Number &&
                el[1].ValueKind == JsonValueKind.Number &&
                el[2].ValueKind == JsonValueKind.String &&
                el[3].ValueKind == JsonValueKind.String &&
                el[4].ValueKind == JsonValueKind.String &&
                el[5].ValueKind == JsonValueKind.Number)
            {
                if (!onAudioTuple(el)) return;
                return;
            }

            foreach (var child in el.EnumerateArray())
                WalkAudioArrays(child, onAudioTuple);
        }
        else if (el.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in el.EnumerateObject())
                WalkAudioArrays(prop.Value, onAudioTuple);
        }
    }

    private TrackItem? ParseAudioTuple(JsonElement arr, long vkId)
    {
        try
        {
            if (arr.ValueKind != JsonValueKind.Array || arr.GetArrayLength() < 6) return null;
            long audioId = arr[0].GetInt64();
            long ownerId = arr[1].GetInt64();
            if (audioId == 0 || ownerId == 0) return null;

            string rawUrl = arr[2].GetString() ?? "";
            string title = StripHtmlTags(WebUtility.HtmlDecode(arr[3].GetString() ?? "")).Trim();
            string artist = StripHtmlTags(WebUtility.HtmlDecode(arr[4].GetString() ?? "")).Trim();
            int durationSec = Math.Max(0, arr[5].GetInt32());

            if (string.IsNullOrWhiteSpace(title))
                title = "Аудиозапись ВК";
            if (string.IsNullOrWhiteSpace(artist))
                artist = "ВКонтакте";

            string subtitle = arr.GetArrayLength() > 16 && arr[16].ValueKind == JsonValueKind.String
                ? StripHtmlTags(WebUtility.HtmlDecode(arr[16].GetString() ?? "")).Trim()
                : "";
            if (!string.IsNullOrWhiteSpace(subtitle))
                title = $"{title} ({subtitle})";

            string actionHash = "";
            string urlHash = "";
            if (arr.GetArrayLength() > 13 && arr[13].ValueKind == JsonValueKind.String)
            {
                var hashParts = (arr[13].GetString() ?? "").Split('/');
                if (hashParts.Length > 2) actionHash = hashParts[2];
                if (hashParts.Length > 5) urlHash = hashParts[5];
            }

            string coverUrl = "";
            if (arr.GetArrayLength() > 14 && arr[14].ValueKind == JsonValueKind.String)
            {
                var thumbParts = (arr[14].GetString() ?? "")
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                coverUrl = thumbParts.LastOrDefault(u => u.StartsWith("http", StringComparison.OrdinalIgnoreCase)) ?? "";
            }

            string pageUrl = $"https://vk.ru/audio{ownerId}_{audioId}";
            if (!string.IsNullOrEmpty(actionHash) && !string.IsNullOrEmpty(urlHash))
                pageUrl += $"?ah={actionHash}&uh={urlHash}";

            string idKey = $"{ownerId}_{audioId}";
            _metaCache[idKey] = new VkAudioMeta
            {
                OwnerId = ownerId,
                AudioId = audioId,
                ActionHash = actionHash,
                UrlHash = urlHash,
                RawUrl = rawUrl
            };

            return new TrackItem
            {
                Id = idKey,
                Source = "ВК",
                BrandKey = "vk",
                BrandColor = BrandColorHex,
                Title = title,
                Artist = artist,
                PageUrl = pageUrl,
                CoverUrl = coverUrl,
                Duration = durationSec > 0 ? TimeSpan.FromSeconds(durationSec) : TimeSpan.Zero
            };
        }
        catch
        {
            return null;
        }
    }

    private static string StripHtmlTags(string input)
    {
        if (string.IsNullOrEmpty(input) || !input.Contains('<')) return input;
        return Regex.Replace(input, "<.*?>", "").Trim();
    }

    private VkAudioMeta? ResolveTrackMeta(TrackItem track)
    {
        if (!string.IsNullOrWhiteSpace(track.Id) && _metaCache.TryGetValue(track.Id, out var cached))
            return cached;

        var m = AudioPageIdRegex.Match(track.PageUrl ?? "");
        if (!m.Success && !string.IsNullOrWhiteSpace(track.Id))
            m = AudioPageIdRegex.Match("audio" + track.Id);

        if (!m.Success) return null;
        if (!long.TryParse(m.Groups[1].Value, out long ownerId) ||
            !long.TryParse(m.Groups[2].Value, out long audioId))
            return null;

        string ah = "", uh = "", ak = "";
        if (Uri.TryCreate(track.PageUrl, UriKind.Absolute, out var uri))
        {
            foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var idx = pair.IndexOf('=');
                if (idx <= 0) continue;
                var k = pair[..idx];
                var v = Uri.UnescapeDataString(pair[(idx + 1)..]);
                if (k == "ah") ah = v;
                else if (k == "uh") uh = v;
                else if (k == "ak") ak = v;
            }
        }

        var meta = new VkAudioMeta
        {
            OwnerId = ownerId,
            AudioId = audioId,
            AccessKey = ak,
            ActionHash = ah,
            UrlHash = uh
        };
        _metaCache[$"{ownerId}_{audioId}"] = meta;
        return meta;
    }

    private static bool TryGetNextOffset(JsonElement root, out int nextOffset, out bool hasMore)
    {
        nextOffset = 0;
        hasMore = false;
        return FindOffsetProps(root, ref nextOffset, ref hasMore);

        static bool FindOffsetProps(JsonElement el, ref int no, ref bool hm)
        {
            if (el.ValueKind == JsonValueKind.Object)
            {
                bool foundHere = false;
                if (el.TryGetProperty("nextOffset", out var nEl) && nEl.ValueKind == JsonValueKind.Number)
                {
                    no = nEl.GetInt32();
                    foundHere = true;
                }
                if (el.TryGetProperty("hasMore", out var hEl))
                {
                    hm = hEl.ValueKind == JsonValueKind.True ||
                         (hEl.ValueKind == JsonValueKind.Number && hEl.GetInt32() != 0);
                    foundHere = true;
                }
                if (foundHere) return true;

                foreach (var prop in el.EnumerateObject())
                {
                    if (FindOffsetProps(prop.Value, ref no, ref hm))
                        return true;
                }
            }
            else if (el.ValueKind == JsonValueKind.Array)
            {
                foreach (var child in el.EnumerateArray())
                {
                    if (FindOffsetProps(child, ref no, ref hm))
                        return true;
                }
            }
            return false;
        }
    }

    private static string? ReadStringOrNumber(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var val)) return null;
        return val.ValueKind switch
        {
            JsonValueKind.String => val.GetString(),
            JsonValueKind.Number => val.GetRawText(),
            _ => null
        };
    }

    private static bool IsPlayableUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url) &&
        (url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
         url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) &&
        !url.Contains("audio_api_unavailable", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeCookieInput(string? raw)
    {
        var trimmed = (raw ?? "").Trim();
        if (string.IsNullOrWhiteSpace(trimmed)) return "";
        if (!trimmed.Contains('=') && !trimmed.Contains(';'))
            return $"remixsid={trimmed}";
        return trimmed;
    }

    #region Декодер Windows-1251 и деобфускатор ссылок ВК

    private static readonly char[] Cp1251HighChars =
    {
        'Ђ','Ѓ','‚','ѓ','„','…','†','‡','€','‰','Љ','‹','Њ','Ќ','Ћ','Џ',
        'ђ','‘','’','“','”','•','–','—',' ','™','љ','›','њ','ќ','ћ','џ',
        '\u00A0','Ў','ў','Ј','¤','Ґ','¦','§','Ё','©','Є','«','¬','\u00AD','®','Ї',
        '°','±','І','і','ґ','µ','¶','·','ё','№','є','»','ј','Ѕ','ѕ','ї',
        'А','Б','В','Г','Д','Е','Ж','З','И','Й','К','Л','М','Н','О','П',
        'Р','С','Т','У','Ф','Х','Ц','Ч','Ш','Щ','Ъ','Ы','Ь','Э','Ю','Я',
        'а','б','в','г','д','е','ж','з','и','й','к','л','м','н','о','п',
        'р','с','т','у','ф','х','ц','ч','ш','щ','ъ','ы','ь','э','ю','я'
    };

    private static string DecodeResponseBytes(byte[] bytes, string? charset)
    {
        if (bytes.Length == 0) return "";
        if (!string.IsNullOrWhiteSpace(charset) && charset.Contains("1251", StringComparison.OrdinalIgnoreCase))
            return DecodeCp1251(bytes);

        try
        {
            var utf8Strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return utf8Strict.GetString(bytes);
        }
        catch
        {
            return DecodeCp1251(bytes);
        }
    }

    private static string DecodeCp1251(byte[] bytes)
    {
        var chars = new char[bytes.Length];
        for (int i = 0; i < bytes.Length; i++)
        {
            byte b = bytes[i];
            chars[i] = b < 0x80 ? (char)b : Cp1251HighChars[b - 0x80];
        }
        return new string(chars);
    }

    private const string VkAlphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMN0PQRSTUVWXYZO123456789+/=";

    public static string? UnmaskAudioUrl(string? url, long vkId)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (!url.Contains("audio_api_unavailable", StringComparison.OrdinalIgnoreCase))
            return url;

        try
        {
            int extraIdx = url.IndexOf("?extra=", StringComparison.Ordinal);
            if (extraIdx < 0) return null;

            var extraParts = url[(extraIdx + 7)..].Split('#');
            if (extraParts.Length < 2) return null;

            string t = DecodeVkBase64(extraParts[0]);
            string opsRaw = DecodeVkBase64(extraParts[1]);
            if (string.IsNullOrEmpty(t) || string.IsNullOrEmpty(opsRaw))
                return null;

            var ops = opsRaw.Split('\x09', StringSplitOptions.RemoveEmptyEntries);
            for (int i = ops.Length - 1; i >= 0; i--)
            {
                var parts = ops[i].Split('\x0b');
                if (parts.Length == 0 || string.IsNullOrEmpty(parts[0])) continue;

                char cmd = parts[0][0];
                string arg = parts.Length > 1 ? parts[1] : "";

                switch (cmd)
                {
                    case 'v':
                        var rev = t.ToCharArray();
                        Array.Reverse(rev);
                        t = new string(rev);
                        break;

                    case 'r':
                        if (int.TryParse(arg, out int rot))
                        {
                            string doubled = VkAlphabet + VkAlphabet;
                            var rotated = t.ToCharArray();
                            for (int c = 0; c < rotated.Length; c++)
                            {
                                int pos = doubled.IndexOf(rotated[c]);
                                if (pos >= 0)
                                {
                                    int offset = (pos - rot) % VkAlphabet.Length;
                                    if (offset < 0) offset += VkAlphabet.Length;
                                    rotated[c] = doubled[offset];
                                }
                            }
                            t = new string(rotated);
                        }
                        break;

                    case 's':
                        if (int.TryParse(arg, out int seedS))
                            t = ShuffleSwap(t, seedS);
                        break;

                    case 'i':
                        if (long.TryParse(arg, out long seedI))
                            t = ShuffleSwap(t, (int)(seedI ^ vkId));
                        break;

                    case 'x':
                        if (arg.Length > 0)
                        {
                            int keyChar = arg[0];
                            var xored = t.ToCharArray();
                            for (int c = 0; c < xored.Length; c++)
                                xored[c] = (char)(xored[c] ^ keyChar);
                            t = new string(xored);
                        }
                        break;
                }
            }

            return t.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? t : null;
        }
        catch
        {
            return null;
        }
    }

    private static string DecodeVkBase64(string input)
    {
        if (string.IsNullOrEmpty(input)) return "";
        var sb = new StringBuilder();
        int e = 0, a = 0, n = 0;

        foreach (char ch in input)
        {
            int o = VkAlphabet.IndexOf(ch);
            if (o < 0) continue;

            e = (n % 4 != 0) ? e * 64 + o : o;
            if (n++ % 4 != 0)
            {
                a = a == 0 ? 1 : a;
                int shift = -2 * n & 6;
                int charCode = 255 & (e >> shift);
                if (charCode != 0)
                    sb.Append((char)charCode);
            }
        }
        return sb.ToString();
    }

    private static string ShuffleSwap(string str, int seed)
    {
        int len = str.Length;
        if (len == 0) return str;

        var indices = new int[len];
        long e = Math.Abs((long)seed);
        for (int n = len - 1; n >= 0; n--)
        {
            e = (len * (n + 1) ^ e + n) % len;
            indices[n] = (int)e;
        }

        var arr = str.ToCharArray();
        for (int n = 1; n < len; n++)
        {
            int swapIdx = indices[len - 1 - n];
            (arr[n], arr[swapIdx]) = (arr[swapIdx], arr[n]);
        }
        return new string(arr);
    }

    #endregion

    public void Dispose()
    {
        _http.Dispose();
        _apiHttp.Dispose();
        _bridge.Dispose();
    }
}
