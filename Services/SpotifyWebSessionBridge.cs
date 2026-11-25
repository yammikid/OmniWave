using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using Microsoft.Web.WebView2.Wpf;

namespace DynamicIsland.Services;

/// <summary>
/// Скрытый фоновый WebView2-мост для Spotify Web Player:
/// - в авторизованном режиме (профиль WebView2Spotify) обновляет OAuth Bearer-токен по sp_dc,
/// - в гостевом режиме без аккаунта (профиль WebView2SpotifyGuest) получает анонимный
///   TOTP-токен + client-token и выполняет живой поиск через pathfinder / DOM Spotify.
/// </summary>
public sealed class SpotifyWebSessionBridge : IDisposable
{
    internal const string TokenHookScript = @"(function() {
  if (window.__omniSpotifyHookInstalled) return;
  window.__omniSpotifyHookInstalled = true;

  function postMsg(obj) {
    try {
      window.chrome && window.chrome.webview && window.chrome.webview.postMessage(JSON.stringify(obj));
    } catch (e) {}
  }

  function report(kind, token, expiresMs, isAnon) {
    try {
      if (!token || typeof token !== 'string') return;
      var clean = token.replace(/^Bearer\s+/i, '').trim();
      if (clean.length < 30) return;
      postMsg({ kind: kind, token: clean, expiresMs: expiresMs || 0, isAnonymous: !!isAnon });
    } catch (e) {}
  }

  function reportClientToken(ct) {
    try {
      if (ct && typeof ct === 'string' && ct.length > 10) {
        postMsg({ kind: 'clientToken', clientToken: ct.trim() });
      }
    } catch (e) {}
  }

  function inspectSessionTag() {
    try {
      var el = document.getElementById('session');
      if (!el || !el.textContent) return;
      var data = JSON.parse(el.textContent);
      if (data && data.accessToken) {
        report('sessionTag', data.accessToken, data.accessTokenExpirationTimestampMs || 0, data.isAnonymous);
      }
    } catch (e) {}
  }

  try {
    var origFetch = window.fetch;
    if (typeof origFetch === 'function') {
      window.fetch = function(input, init) {
        var reqUrl = typeof input === 'string' ? input : (input && input.url ? input.url : '');
        try {
          var auth = null;
          var clientTok = null;
          if (init && init.headers) {
            if (typeof init.headers.get === 'function') {
              auth = init.headers.get('authorization') || init.headers.get('Authorization');
              clientTok = init.headers.get('client-token') || init.headers.get('Client-Token');
            } else if (typeof init.headers === 'object') {
              for (var k in init.headers) {
                var kl = k ? k.toLowerCase() : '';
                if (kl === 'authorization') auth = init.headers[k];
                if (kl === 'client-token') clientTok = init.headers[k];
              }
            }
          }
          if (input && input.headers && typeof input.headers.get === 'function') {
            if (!auth) auth = input.headers.get('authorization') || input.headers.get('Authorization');
            if (!clientTok) clientTok = input.headers.get('client-token') || input.headers.get('Client-Token');
          }
          if (auth && /^Bearer\s+/i.test(auth)) report('fetchBearer', auth, 0, false);
          if (clientTok) reportClientToken(clientTok);
        } catch (e) {}

        var p = origFetch.apply(this, arguments);
        try {
          if (reqUrl.indexOf('/api/token') !== -1) {
            p.then(function(resp) {
              try {
                if (resp && resp.ok) {
                  resp.clone().json().then(function(d) {
                    if (d && d.accessToken) {
                      report('apiToken', d.accessToken, d.accessTokenExpirationTimestampMs || 0, !!d.isAnonymous);
                    }
                  }).catch(function(){});
                }
              } catch (e) {}
            }).catch(function(){});
          } else if (reqUrl.indexOf('clienttoken.spotify.com/v1/clienttoken') !== -1) {
            p.then(function(resp) {
              try {
                if (resp && resp.ok) {
                  resp.clone().json().then(function(d) {
                    var gt = d && d.granted_token && d.granted_token.token;
                    if (gt) reportClientToken(gt);
                  }).catch(function(){});
                }
              } catch (e) {}
            }).catch(function(){});
          } else if (reqUrl.indexOf('api-partner.spotify.com/pathfinder/') !== -1) {
            p.then(function(resp) {
              try {
                if (resp && resp.ok) {
                  resp.clone().json().then(function(d) {
                    if (!d || !d.data) return;
                    if (d.data.searchV2) {
                      postMsg({ kind: 'pathfinderSearch', reqUrl: reqUrl || '', payload: d.data.searchV2 });
                    }
                    if (d.data.me && d.data.me.profile) {
                      postMsg({ kind: 'pathfinderProfile', profile: d.data.me.profile });
                    }
                    if (d.data.me && d.data.me.library && d.data.me.library.tracks) {
                      postMsg({ kind: 'pathfinderLikedTracks', payload: d.data.me.library.tracks });
                    }
                    if (d.data.me && d.data.me.libraryV3) {
                      postMsg({ kind: 'pathfinderLibraryV3', payload: d.data.me.libraryV3 });
                    }
                    if (d.data.playlistV2) {
                      postMsg({ kind: 'pathfinderPlaylist', payload: d.data.playlistV2 });
                    }
                  }).catch(function(){});
                }
              } catch (e) {}
            }).catch(function(){});
          }
        } catch (e) {}
        return p;
      };
    }
  } catch (e) {}

  try {
    var origSetHeader = XMLHttpRequest.prototype.setRequestHeader;
    XMLHttpRequest.prototype.setRequestHeader = function(name, val) {
      try {
        var nl = name ? name.toLowerCase() : '';
        if (nl === 'authorization' && typeof val === 'string' && /^Bearer\s+/i.test(val)) {
          report('xhrBearer', val, 0, false);
        }
        if (nl === 'client-token' && typeof val === 'string') {
          reportClientToken(val);
        }
      } catch (e) {}
      return origSetHeader.apply(this, arguments);
    };
  } catch (e) {}

  if (document.readyState === 'loading') {
    document.addEventListener('DOMContentLoaded', inspectSessionTag);
  } else {
    inspectSessionTag();
  }
  setInterval(inspectSessionTag, 600);
})();";

    private const string DomExtractTracksJs = @"(function() {
  try {
    var rows = document.querySelectorAll('[data-testid=""tracklist-row""]');
    if (!rows || rows.length === 0) return '';
    var out = [];
    rows.forEach(function(r) {
      var link = r.querySelector('a[href^=""/track/""]');
      if (!link) return;
      var href = link.getAttribute('href') || '';
      var m = href.match(/\/track\/([a-zA-Z0-9]+)/);
      if (!m) return;
      var title = (link.textContent || '').trim();
      var artistLinks = [];
      r.querySelectorAll('a[href^=""/artist/""]').forEach(function(a) {
        var n = (a.textContent || '').trim();
        if (n && artistLinks.indexOf(n) === -1) artistLinks.push(n);
      });
      var durText = '';
      var els = r.querySelectorAll('div,span');
      for (var i = els.length - 1; i >= 0; i--) {
        if (els[i].childElementCount === 0) {
          var t = (els[i].textContent || '').trim();
          if (/^\d{1,2}:\d{2}$/.test(t)) { durText = t; break; }
        }
      }
      var img = r.querySelector('img[src*=""scdn.co""]') || r.querySelector('img');
      var cover = img ? (img.getAttribute('src') || '') : '';
      out.push({ id: m[1], title: title, artists: artistLinks, durationText: durText, cover: cover });
    });
    if (out.length === 0) return '';
    var decodedHref = '';
    try { decodedHref = decodeURIComponent((location.href || '').replace(/\+/g, ' ')); }
    catch (e2) { decodedHref = location.href || ''; }
    return JSON.stringify({ href: decodedHref, tracks: out });
  } catch (e) { return ''; }
})()";

    private const string DomExtractPlaylistsJs = @"(function() {
  try {
    var out = [];
    var seen = {};
    var links = document.querySelectorAll('a[href*=""/playlist/""]');
    links.forEach(function(a) {
      var href = a.getAttribute('href') || '';
      var m = href.match(/\/playlist\/([a-zA-Z0-9]+)/);
      if (!m || seen[m[1]]) return;
      var card = a.closest('[role=""listitem""], li, [data-testid]') || a.parentElement;
      var name = (a.getAttribute('aria-label') || a.getAttribute('title') || a.textContent || '').trim();
      if (!name && card) {
        var titleEl = card.querySelector('p, span');
        if (titleEl) name = (titleEl.textContent || '').trim();
      }
      if (!name) return;
      seen[m[1]] = true;
      var img = card ? card.querySelector('img') : null;
      var cover = img ? (img.getAttribute('src') || '') : '';
      out.push({ id: m[1], name: name, cover: cover });
    });
    return out.length > 0 ? JSON.stringify(out) : '';
  } catch (e) { return ''; }
})()";

    internal const string DomExtractUserWidgetJs = @"(function() {
  try {
    var w = document.querySelector('[data-testid=""user-widget-link""]');
    if (!w) return '';
    var img = w.querySelector('img[alt]');
    return (w.getAttribute('aria-label') || w.getAttribute('title') || (img ? img.getAttribute('alt') : '') || w.textContent || '').trim();
  } catch (e) { return ''; }
})()";

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly bool _isGuestProfile;

    private Window? _hostWindow;
    private WebView2? _webView;
    private TaskCompletionSource<(string? Token, long ExpiresMs, bool IsAnonymous)>? _tokenWaiter;
    private bool _tokenWaiterAllowAnonymous;
    private TaskCompletionSource<List<TrackItem>>? _searchWaiter;
    private TaskCompletionSource<(string? UserId, string? DisplayName)>? _profileWaiter;
    private TaskCompletionSource<List<TrackItem>>? _likedTracksWaiter;
    private TaskCompletionSource<List<(string Id, string Name, string CoverUrl)>>? _libraryWaiter;
    private TaskCompletionSource<List<TrackItem>>? _playlistTracksWaiter;
    private int _searchLimit = 12;
    private string? _expectedSearchQuery;
    private string? _lastDomSignature;
    private bool _navCompletedFlag;
    private bool _initialized;
    private bool _disposed;

    public string? LastAccessToken { get; private set; }
    public long LastTokenExpiresMs { get; private set; }
    public string? LastClientToken { get; private set; }
    public string? LastUserId { get; private set; }
    public string? LastUserName { get; private set; }

    public SpotifyWebSessionBridge(bool isGuestProfile = false)
    {
        _isGuestProfile = isGuestProfile;
        ProxyService.ProxyChanged += ResetSession;
    }

    public Task<(string? AccessToken, long ExpiresUnixMs, string? SpDcCookie)> RefreshAccessTokenAsync(
        string? spDcCookie,
        CancellationToken ct = default)
        => RefreshInternalAsync(spDcCookie, ct);

    private async Task<(string? AccessToken, long ExpiresUnixMs, string? SpDcCookie)> RefreshInternalAsync(
        string? spDcCookie,
        CancellationToken ct)
    {
        var (tok, exp, _, dc, _) = await AcquireTokenAsync(spDcCookie, allowAnonymous: false, ct).ConfigureAwait(false);
        return (tok, exp, dc);
    }

    public async Task<(string? AccessToken, long ExpiresUnixMs, string? ClientToken, string? SpDcCookie, bool IsAnonymous)> AcquireTokenAsync(
        string? spDcCookie,
        bool allowAnonymous,
        CancellationToken ct = default)
    {
        if (_disposed) return (null, 0, null, null, true);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed) return (null, 0, null, null, true);
            var disp = Application.Current?.Dispatcher;
            if (disp == null || disp.HasShutdownStarted) return (null, 0, null, null, true);

            return await await disp.InvokeAsync(async () =>
            {
                if (!await EnsureInitializedOnUiAsync(ct))
                    return (null, 0L, (string?)null, (string?)null, true);

                if (_webView?.CoreWebView2 == null)
                    return (null, 0L, (string?)null, (string?)null, true);

                if (!string.IsNullOrWhiteSpace(spDcCookie))
                    ApplySpDcCookieCore(spDcCookie.Trim());

                var waiter = new TaskCompletionSource<(string? Token, long ExpiresMs, bool IsAnonymous)>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _tokenWaiterAllowAnonymous = allowAnonymous;
                _tokenWaiter = waiter;

                try
                {
                    _webView.CoreWebView2.Navigate("https://open.spotify.com/");
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));
                    await using var reg = timeoutCts.Token.Register(() => waiter.TrySetResult((null, 0, true)));

                    var (tok, exp, isAnon) = await waiter.Task;
                    string? freshSpDc = allowAnonymous ? null : await ReadSpDcCookieCoreAsync();
                    return (tok, exp, LastClientToken, freshSpDc ?? spDcCookie, isAnon);
                }
                finally
                {
                    if (ReferenceEquals(_tokenWaiter, waiter))
                        _tokenWaiter = null;
                }
            }).Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return (null, 0, null, null, true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<List<TrackItem>> SearchGuestTracksAsync(string query, int limit = 12, CancellationToken ct = default)
    {
        if (_disposed || string.IsNullOrWhiteSpace(query)) return new List<TrackItem>();
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed) return new List<TrackItem>();
            var disp = Application.Current?.Dispatcher;
            if (disp == null || disp.HasShutdownStarted) return new List<TrackItem>();

            return await await disp.InvokeAsync(async () =>
            {
                if (!await EnsureInitializedOnUiAsync(ct) || _webView?.CoreWebView2 == null)
                    return new List<TrackItem>();

                int clampedLimit = Math.Clamp(limit, 5, 30);
                var waiter = new TaskCompletionSource<List<TrackItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
                string trimmedQuery = query.Trim();
                string? prevSig = _lastDomSignature;
                _searchLimit = clampedLimit;
                _expectedSearchQuery = trimmedQuery;
                _searchWaiter = waiter;
                _navCompletedFlag = false;

                try
                {
                    string searchUrl = $"https://open.spotify.com/search/{Uri.EscapeDataString(trimmedQuery)}/tracks";
                    _webView.CoreWebView2.Navigate(searchUrl);

                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(14));
                    await using var reg = timeoutCts.Token.Register(() => waiter.TrySetResult(new List<TrackItem>()));

                    // Опрашиваем DOM в качестве резерва, если pathfinder не пришёл первым
                    _ = PollSearchDomAsync(waiter, clampedLimit, trimmedQuery, prevSig, timeoutCts.Token);
                    var tracks = await waiter.Task;
                    if (tracks.Count > 0)
                        _lastDomSignature = BuildTracksSignature(tracks);
                    return tracks;
                }
                finally
                {
                    if (ReferenceEquals(_searchWaiter, waiter))
                    {
                        _searchWaiter = null;
                        _expectedSearchQuery = null;
                    }
                }
            }).Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return new List<TrackItem>();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task PollSearchDomAsync(
        TaskCompletionSource<List<TrackItem>> waiter,
        int limit,
        string expectedQuery,
        string? prevSig,
        CancellationToken ct)
    {
        try
        {
            DateTime? navCompletedAtUtc = null;
            while (!waiter.Task.IsCompleted && !ct.IsCancellationRequested)
            {
                await Task.Delay(450, ct);
                if (waiter.Task.IsCompleted || _webView?.CoreWebView2 == null) break;
                if (!_navCompletedFlag) continue;

                navCompletedAtUtc ??= DateTime.UtcNow;

                var raw = await _webView.ExecuteScriptAsync(DomExtractTracksJs);
                var unquoted = JsonSerializer.Deserialize<string>(raw);
                if (string.IsNullOrWhiteSpace(unquoted)) continue;

                using var doc = JsonDocument.Parse(unquoted);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;

                string href = doc.RootElement.TryGetProperty("href", out var hrefEl) ? (hrefEl.GetString() ?? "") : "";
                if (!QueryMatchesText(href, expectedQuery))
                    continue;

                if (!doc.RootElement.TryGetProperty("tracks", out var tracksEl))
                    continue;

                var domTracks = ParseDomTracksArray(tracksEl, limit);
                if (domTracks.Count == 0) continue;

                string sig = BuildTracksSignature(domTracks);
                if (!string.IsNullOrEmpty(prevSig) &&
                    string.Equals(sig, prevSig, StringComparison.Ordinal) &&
                    (DateTime.UtcNow - navCompletedAtUtc.Value).TotalSeconds < 2.0)
                {
                    continue;
                }

                _lastDomSignature = sig;
                waiter.TrySetResult(domTracks);
                return;
            }
        }
        catch { }
    }

    private static bool QueryMatchesText(string? candidateText, string? expectedQuery)
    {
        if (string.IsNullOrWhiteSpace(expectedQuery)) return true;
        if (string.IsNullOrWhiteSpace(candidateText)) return false;

        string Norm(string s)
        {
            string decoded;
            try { decoded = Uri.UnescapeDataString(s.Replace('+', ' ')); }
            catch { decoded = s.Replace('+', ' '); }
            return Regex.Replace(decoded, @"\s+", " ").Trim();
        }

        string cleanCand = Norm(candidateText);
        string cleanExpected = Norm(expectedQuery);

        return candidateText.Contains(expectedQuery, StringComparison.OrdinalIgnoreCase) ||
               cleanCand.Contains(cleanExpected, StringComparison.OrdinalIgnoreCase);
    }

    private static string BuildTracksSignature(List<TrackItem> tracks)
        => string.Join("|", tracks.Select(t => t.Id));

    private async Task<bool> EnsureInitializedOnUiAsync(CancellationToken ct)
    {
        if (_initialized && _webView?.CoreWebView2 != null)
            return true;

        try
        {
            var folderName = _isGuestProfile ? "WebView2SpotifyGuest" : "WebView2Spotify";
            var userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "OmniWave",
                folderName);
            Directory.CreateDirectory(userDataFolder);

            _webView = new WebView2
            {
                CreationProperties = new CoreWebView2CreationProperties
                {
                    UserDataFolder = userDataFolder,
                    AdditionalBrowserArguments = ProxyService.GetWebView2ProxyArguments()
                }
            };

            _hostWindow = new Window
            {
                Width = 960,
                Height = 680,
                Left = -32000,
                Top = -32000,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                AllowsTransparency = true,
                Opacity = 0.0,
                Content = _webView
            };
            _hostWindow.Show();
            _hostWindow.Hide();

            await _webView.EnsureCoreWebView2Async();
            if (_webView.CoreWebView2 == null) return false;

            var settings = _webView.CoreWebView2.Settings;
            settings.AreDefaultContextMenusEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.IsGeneralAutofillEnabled = false;
            settings.AreDevToolsEnabled = false;
            _webView.CoreWebView2.IsMuted = true;

            _webView.NavigationCompleted += (_, _) => _navCompletedFlag = true;

            await _webView.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(TokenHookScript);

            _webView.WebMessageReceived += async (_, ev) =>
            {
                try
                {
                    var json = ev.TryGetWebMessageAsString();
                    if (string.IsNullOrWhiteSpace(json)) return;
                    using var doc = JsonDocument.Parse(json);
                    var root = doc.RootElement;
                    string? kind = root.TryGetProperty("kind", out var kEl) ? kEl.GetString() : null;

                    if (kind == "clientToken")
                    {
                        var ctVal = root.TryGetProperty("clientToken", out var cEl) ? cEl.GetString() : null;
                        if (!string.IsNullOrWhiteSpace(ctVal))
                            LastClientToken = ctVal;
                        return;
                    }

                    if (kind == "pathfinderProfile")
                    {
                        if (root.TryGetProperty("profile", out var profEl) && profEl.ValueKind == JsonValueKind.Object)
                        {
                            string? uid = profEl.TryGetProperty("username", out var uEl) ? uEl.GetString() : null;
                            string? uname = profEl.TryGetProperty("name", out var nEl) ? nEl.GetString() : null;
                            if (!string.IsNullOrWhiteSpace(uid)) LastUserId = uid.Trim();
                            if (!string.IsNullOrWhiteSpace(uname)) LastUserName = uname.Trim();
                            if (!string.IsNullOrWhiteSpace(LastUserId) || !string.IsNullOrWhiteSpace(LastUserName))
                                _profileWaiter?.TrySetResult((LastUserId, LastUserName));
                        }
                        return;
                    }

                    if (kind == "pathfinderLikedTracks")
                    {
                        if (root.TryGetProperty("payload", out var payload))
                        {
                            var found = ParsePathfinderTrackCollection(payload, 500);
                            if (found.Count > 0)
                                _likedTracksWaiter?.TrySetResult(found);
                        }
                        return;
                    }

                    if (kind == "pathfinderLibraryV3")
                    {
                        if (root.TryGetProperty("payload", out var payload))
                        {
                            var pls = ParsePathfinderLibraryV3(payload);
                            if (pls.Count > 0)
                                _libraryWaiter?.TrySetResult(pls);
                        }
                        return;
                    }

                    if (kind == "pathfinderPlaylist")
                    {
                        if (root.TryGetProperty("payload", out var payload))
                        {
                            var found = ParsePathfinderPlaylistTracks(payload, 350);
                            if (found.Count > 0)
                                _playlistTracksWaiter?.TrySetResult(found);
                        }
                        return;
                    }

                    if (kind == "pathfinderSearch")
                    {
                        string reqUrl = root.TryGetProperty("reqUrl", out var urlEl) ? (urlEl.GetString() ?? "") : "";
                        if (!string.IsNullOrWhiteSpace(_expectedSearchQuery) &&
                            reqUrl.Contains("variables=", StringComparison.OrdinalIgnoreCase) &&
                            !QueryMatchesText(reqUrl, _expectedSearchQuery))
                        {
                            return;
                        }

                        if (root.TryGetProperty("payload", out var payload))
                        {
                            var found = ParsePathfinderSearch(payload, _searchLimit);
                            if (found.Count > 0 && _searchWaiter != null)
                            {
                                _lastDomSignature = BuildTracksSignature(found);
                                _searchWaiter.TrySetResult(found);
                            }
                        }
                        return;
                    }

                    string? tok = root.TryGetProperty("token", out var tEl) ? tEl.GetString() : null;
                    long exp = root.TryGetProperty("expiresMs", out var eEl) && eEl.ValueKind == JsonValueKind.Number
                        ? eEl.GetInt64()
                        : 0;
                    bool isAnon = root.TryGetProperty("isAnonymous", out var aEl) && aEl.ValueKind == JsonValueKind.True;

                    if (string.IsNullOrWhiteSpace(tok)) return;

                    LastAccessToken = tok;
                    if (exp > 0) LastTokenExpiresMs = exp;

                    var waiter = _tokenWaiter;
                    if (waiter == null) return;

                    if (isAnon && !_tokenWaiterAllowAnonymous)
                        return;

                    if (!_tokenWaiterAllowAnonymous && kind is "fetchBearer" or "xhrBearer")
                    {
                        var spDc = await ReadSpDcCookieCoreAsync();
                        if (string.IsNullOrWhiteSpace(spDc))
                            return;
                    }

                    waiter.TrySetResult((tok, exp, isAnon));
                }
                catch { }
            };

            _initialized = true;
            return true;
        }
        catch
        {
            CleanupUiResources();
            return false;
        }
    }

    private static TrackItem? TryParsePathfinderTrackEntry(JsonElement entry)
    {
        if (entry.ValueKind != JsonValueKind.Object) return null;

        JsonElement data = entry;
        if (entry.TryGetProperty("track", out var trObj) && trObj.ValueKind == JsonValueKind.Object)
        {
            if (trObj.TryGetProperty("data", out var td) && td.ValueKind == JsonValueKind.Object)
                data = td;
            else if (trObj.TryGetProperty("_data", out var td2) && td2.ValueKind == JsonValueKind.Object)
                data = td2;
            else
                data = trObj;
        }
        else if (entry.TryGetProperty("itemV2", out var itemV2) && itemV2.ValueKind == JsonValueKind.Object &&
                 itemV2.TryGetProperty("data", out var d2) && d2.ValueKind == JsonValueKind.Object)
        {
            data = d2;
        }
        else if (entry.TryGetProperty("item", out var itemObj) && itemObj.ValueKind == JsonValueKind.Object)
        {
            if (itemObj.TryGetProperty("track", out var innerTr) && innerTr.ValueKind == JsonValueKind.Object)
            {
                if (innerTr.TryGetProperty("data", out var itd) && itd.ValueKind == JsonValueKind.Object)
                    data = itd;
                else if (innerTr.TryGetProperty("_data", out var itd2) && itd2.ValueKind == JsonValueKind.Object)
                    data = itd2;
                else
                    data = innerTr;
            }
            else if (itemObj.TryGetProperty("data", out var d1) && d1.ValueKind == JsonValueKind.Object)
            {
                data = d1;
            }
        }
        else if (entry.TryGetProperty("data", out var d3) && d3.ValueKind == JsonValueKind.Object)
        {
            data = d3;
        }
        else if (entry.TryGetProperty("_data", out var d4) && d4.ValueKind == JsonValueKind.Object)
        {
            data = d4;
        }

        if (data.ValueKind != JsonValueKind.Object) return null;
        string? uri = data.TryGetProperty("uri", out var uEl) ? uEl.GetString() : null;
        string? id = data.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(uri) && uri.Contains(":track:", StringComparison.OrdinalIgnoreCase))
            id = uri.Split(':').LastOrDefault();

        string? title = data.TryGetProperty("name", out var nEl) ? nEl.GetString() : null;
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title))
            return null;

        var artists = new List<string>();
        if (data.TryGetProperty("artists", out var artObj) &&
            artObj.TryGetProperty("items", out var artArr) &&
            artArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in artArr.EnumerateArray())
            {
                string? an = null;
                if (a.TryGetProperty("profile", out var pr) && pr.TryGetProperty("name", out var pn))
                    an = pn.GetString();
                else if (a.TryGetProperty("name", out var anEl))
                    an = anEl.GetString();
                if (!string.IsNullOrWhiteSpace(an))
                    artists.Add(an.Trim());
            }
        }

        long durMs = 0;
        if (data.TryGetProperty("duration", out var durObj) &&
            durObj.TryGetProperty("totalMilliseconds", out var msEl) &&
            msEl.ValueKind == JsonValueKind.Number)
        {
            durMs = msEl.GetInt64();
        }
        else if (data.TryGetProperty("trackDuration", out var tdurObj) &&
                 tdurObj.TryGetProperty("totalMilliseconds", out var msEl2) &&
                 msEl2.ValueKind == JsonValueKind.Number)
        {
            durMs = msEl2.GetInt64();
        }

        string coverUrl = "";
        static string PickBestCoverSource(JsonElement caEl)
        {
            if (!caEl.TryGetProperty("sources", out var srcs) || srcs.ValueKind != JsonValueKind.Array || srcs.GetArrayLength() == 0)
                return "";
            string best = "";
            foreach (var s in srcs.EnumerateArray())
            {
                var cu = s.TryGetProperty("url", out var cuEl) ? cuEl.GetString() : null;
                if (string.IsNullOrWhiteSpace(cu)) continue;
                best = cu;
                int w = s.TryGetProperty("width", out var wEl) && wEl.ValueKind == JsonValueKind.Number ? wEl.GetInt32() : 0;
                if (w is >= 250 and <= 640) break;
            }
            return best;
        }

        if (data.TryGetProperty("albumOfTrack", out var alb) &&
            alb.TryGetProperty("coverArt", out var ca))
        {
            coverUrl = PickBestCoverSource(ca);
        }
        else if (data.TryGetProperty("coverArt", out var directCa))
        {
            coverUrl = PickBestCoverSource(directCa);
        }

        return new TrackItem
        {
            Id = id,
            Source = "Spotify",
            BrandKey = "spotify",
            BrandColor = SpotifyMusicProvider.BrandColorHex,
            Title = title.Trim(),
            Artist = artists.Count > 0 ? string.Join(", ", artists) : "Spotify",
            CoverUrl = coverUrl,
            PageUrl = $"https://open.spotify.com/track/{id}",
            Duration = TimeSpan.FromMilliseconds(durMs)
        };
    }

    private static List<TrackItem> ParsePathfinderSearch(JsonElement searchV2, int limit)
    {
        var tracks = new List<TrackItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void CollectTracksArray(JsonElement arr)
        {
            if (arr.ValueKind != JsonValueKind.Array) return;
            foreach (var entry in arr.EnumerateArray())
            {
                if (tracks.Count >= limit) return;
                var tr = TryParsePathfinderTrackEntry(entry);
                if (tr != null && seen.Add(tr.Id))
                    tracks.Add(tr);
            }
        }

        if (searchV2.TryGetProperty("tracksV2", out var tV2) && tV2.TryGetProperty("items", out var items1))
            CollectTracksArray(items1);
        if (tracks.Count == 0 && searchV2.TryGetProperty("tracks", out var t1) && t1.TryGetProperty("items", out var items2))
            CollectTracksArray(items2);

        return tracks;
    }

    private static List<TrackItem> ParsePathfinderTrackCollection(JsonElement container, int limit)
    {
        var tracks = new List<TrackItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (container.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in items.EnumerateArray())
            {
                if (tracks.Count >= limit) break;
                var tr = TryParsePathfinderTrackEntry(entry);
                if (tr != null && seen.Add(tr.Id))
                    tracks.Add(tr);
            }
        }
        return tracks;
    }

    private static List<TrackItem> ParsePathfinderPlaylistTracks(JsonElement playlistV2, int limit)
    {
        if (playlistV2.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Object)
            return ParsePathfinderTrackCollection(content, limit);
        return ParsePathfinderTrackCollection(playlistV2, limit);
    }

    private static List<(string Id, string Name, string CoverUrl)> ParsePathfinderLibraryV3(JsonElement libraryV3)
    {
        var playlists = new List<(string Id, string Name, string CoverUrl)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!libraryV3.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return playlists;

        foreach (var entry in items.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;
            JsonElement data = entry;
            if (entry.TryGetProperty("item", out var iObj) && iObj.TryGetProperty("data", out var d1))
                data = d1;
            else if (entry.TryGetProperty("data", out var d2))
                data = d2;

            if (data.ValueKind != JsonValueKind.Object) continue;
            string? typeName = data.TryGetProperty("__typename", out var tnEl) ? tnEl.GetString() : null;
            string? uri = data.TryGetProperty("uri", out var uEl) ? uEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(uri) || !uri.Contains(":playlist:", StringComparison.OrdinalIgnoreCase))
                continue;
            if (!string.IsNullOrWhiteSpace(typeName) && !typeName.Contains("Playlist", StringComparison.OrdinalIgnoreCase))
                continue;

            string? plId = uri.Split(':').LastOrDefault();
            string? name = data.TryGetProperty("name", out var nEl) ? nEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(plId) || string.IsNullOrWhiteSpace(name) || !seen.Add(plId))
                continue;

            string cover = "";
            if (data.TryGetProperty("images", out var imgsObj) &&
                imgsObj.TryGetProperty("items", out var imgItems) &&
                imgItems.ValueKind == JsonValueKind.Array &&
                imgItems.GetArrayLength() > 0 &&
                imgItems[0].TryGetProperty("sources", out var srcs) &&
                srcs.ValueKind == JsonValueKind.Array &&
                srcs.GetArrayLength() > 0)
            {
                cover = srcs[0].TryGetProperty("url", out var urlEl) ? (urlEl.GetString() ?? "") : "";
            }
            else if (data.TryGetProperty("coverArt", out var ca) &&
                     ca.TryGetProperty("sources", out var csrcs) &&
                     csrcs.ValueKind == JsonValueKind.Array &&
                     csrcs.GetArrayLength() > 0)
            {
                cover = csrcs[0].TryGetProperty("url", out var urlEl) ? (urlEl.GetString() ?? "") : "";
            }

            playlists.Add((plId, name.Trim(), cover));
        }
        return playlists;
    }

    private static List<TrackItem> ParseDomTracksArray(JsonElement arr, int limit)
    {
        var tracks = new List<TrackItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (arr.ValueKind != JsonValueKind.Array) return tracks;

        foreach (var el in arr.EnumerateArray())
        {
            if (tracks.Count >= limit) break;
            var id = el.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            var title = el.TryGetProperty("title", out var tEl) ? tEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(title) || !seen.Add(id))
                continue;

            var artists = new List<string>();
            if (el.TryGetProperty("artists", out var aArr) && aArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in aArr.EnumerateArray())
                {
                    var s = a.GetString();
                    if (!string.IsNullOrWhiteSpace(s))
                        artists.Add(s.Trim());
                }
            }

            var durText = el.TryGetProperty("durationText", out var dEl) ? (dEl.GetString() ?? "") : "";
            TimeSpan dur = TimeSpan.Zero;
            var dp = durText.Split(':');
            if (dp.Length == 2 &&
                int.TryParse(dp[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int m) &&
                int.TryParse(dp[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int sSec))
            {
                dur = new TimeSpan(0, m, sSec);
            }

            var cover = el.TryGetProperty("cover", out var cEl) ? (cEl.GetString() ?? "") : "";

            tracks.Add(new TrackItem
            {
                Id = id,
                Source = "Spotify",
                BrandKey = "spotify",
                BrandColor = SpotifyMusicProvider.BrandColorHex,
                Title = title.Trim(),
                Artist = artists.Count > 0 ? string.Join(", ", artists) : "Spotify",
                CoverUrl = cover,
                PageUrl = $"https://open.spotify.com/track/{id}",
                Duration = dur
            });
        }
        return tracks;
    }

    private void ApplySpDcCookieCore(string spDcValue)
    {
        try
        {
            if (_webView?.CoreWebView2 == null) return;
            var cm = _webView.CoreWebView2.CookieManager;
            foreach (var domain in new[] { ".spotify.com", "open.spotify.com" })
            {
                var c = cm.CreateCookie("sp_dc", spDcValue, domain, "/");
                c.IsSecure = true;
                c.IsHttpOnly = true;
                cm.AddOrUpdateCookie(c);
            }
        }
        catch { }
    }

    private async Task<string?> ReadSpDcCookieCoreAsync()
    {
        try
        {
            if (_webView?.CoreWebView2 == null) return null;
            var cookies = await _webView.CoreWebView2.CookieManager.GetCookiesAsync("https://open.spotify.com");
            var spDc = cookies.FirstOrDefault(c => string.Equals(c.Name, "sp_dc", StringComparison.OrdinalIgnoreCase));
            if (spDc != null && !string.IsNullOrWhiteSpace(spDc.Value))
                return spDc.Value;
        }
        catch { }
        return null;
    }

    public async Task<(bool IsLoggedIn, string? AccessToken, long ExpiresMs, string? ClientToken, string? SpDc, string? UserId, string? DisplayName)>
        FetchAuthenticatedProfileAsync(string spDcCookie, CancellationToken ct = default)
    {
        if (_disposed || string.IsNullOrWhiteSpace(spDcCookie))
            return (false, null, 0, null, null, null, null);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed) return (false, null, 0, null, null, null, null);
            var disp = Application.Current?.Dispatcher;
            if (disp == null || disp.HasShutdownStarted)
                return (false, null, 0, null, null, null, null);

            return await await disp.InvokeAsync(async () =>
            {
                if (!await EnsureInitializedOnUiAsync(ct) || _webView?.CoreWebView2 == null)
                    return (false, (string?)null, 0L, (string?)null, (string?)null, (string?)null, (string?)null);

                ApplySpDcCookieCore(spDcCookie.Trim());

                var tokenWaiter = new TaskCompletionSource<(string? Token, long ExpiresMs, bool IsAnonymous)>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                var profileWaiter = new TaskCompletionSource<(string? UserId, string? DisplayName)>(
                    TaskCreationOptions.RunContinuationsAsynchronously);

                _tokenWaiterAllowAnonymous = false;
                _tokenWaiter = tokenWaiter;
                _profileWaiter = profileWaiter;
                _navCompletedFlag = false;

                try
                {
                    _webView.CoreWebView2.Navigate("https://open.spotify.com/");

                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
                    await using var reg = timeoutCts.Token.Register(() =>
                    {
                        tokenWaiter.TrySetResult((null, 0, true));
                        profileWaiter.TrySetResult((LastUserId, LastUserName));
                    });

                    string? domDisplay = null;
                    var pollDeadline = DateTime.UtcNow.AddSeconds(10);
                    while (!timeoutCts.IsCancellationRequested && DateTime.UtcNow < pollDeadline)
                    {
                        if (tokenWaiter.Task.IsCompleted &&
                            (profileWaiter.Task.IsCompleted || !string.IsNullOrWhiteSpace(domDisplay)))
                        {
                            break;
                        }

                        if (_navCompletedFlag && _webView?.CoreWebView2 != null && string.IsNullOrWhiteSpace(domDisplay))
                        {
                            try
                            {
                                var raw = await _webView.ExecuteScriptAsync(DomExtractUserWidgetJs);
                                var parsed = JsonSerializer.Deserialize<string>(raw);
                                if (!string.IsNullOrWhiteSpace(parsed))
                                {
                                    domDisplay = parsed.Trim();
                                    profileWaiter.TrySetResult((LastUserId, domDisplay));
                                }
                            }
                            catch { }
                        }

                        await Task.Delay(250, timeoutCts.Token).ContinueWith(_ => { }, CancellationToken.None);
                    }

                    var tokRes = tokenWaiter.Task.IsCompleted ? await tokenWaiter.Task : (Token: LastAccessToken, ExpiresMs: LastTokenExpiresMs, IsAnonymous: true);
                    var profRes = profileWaiter.Task.IsCompleted ? await profileWaiter.Task : (UserId: LastUserId, DisplayName: LastUserName);
                    string? freshSpDc = await ReadSpDcCookieCoreAsync();

                    bool loggedIn = (!tokRes.IsAnonymous && !string.IsNullOrWhiteSpace(tokRes.Token)) ||
                                    !string.IsNullOrWhiteSpace(domDisplay) ||
                                    !string.IsNullOrWhiteSpace(profRes.DisplayName);

                    string? finalName = !string.IsNullOrWhiteSpace(profRes.DisplayName)
                        ? profRes.DisplayName
                        : (!string.IsNullOrWhiteSpace(domDisplay) ? domDisplay : LastUserName);

                    return (loggedIn,
                        tokRes.Token ?? LastAccessToken,
                        tokRes.ExpiresMs > 0 ? tokRes.ExpiresMs : LastTokenExpiresMs,
                        LastClientToken,
                        freshSpDc ?? spDcCookie,
                        profRes.UserId ?? LastUserId,
                        finalName);
                }
                finally
                {
                    if (ReferenceEquals(_tokenWaiter, tokenWaiter)) _tokenWaiter = null;
                    if (ReferenceEquals(_profileWaiter, profileWaiter)) _profileWaiter = null;
                }
            }).Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            return (false, null, 0, null, null, null, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<(List<TrackItem> LikedTracks, List<(string Id, string Name, string CoverUrl)> Playlists)>
        FetchAuthenticatedLibraryAsync(string spDcCookie, CancellationToken ct = default)
    {
        if (_disposed || string.IsNullOrWhiteSpace(spDcCookie))
            return (new List<TrackItem>(), new List<(string, string, string)>());

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed)
                return (new List<TrackItem>(), new List<(string, string, string)>());
            var disp = Application.Current?.Dispatcher;
            if (disp == null || disp.HasShutdownStarted)
                return (new List<TrackItem>(), new List<(string, string, string)>());

            return await await disp.InvokeAsync(async () =>
            {
                if (!await EnsureInitializedOnUiAsync(ct) || _webView?.CoreWebView2 == null)
                    return (new List<TrackItem>(), new List<(string, string, string)>());

                ApplySpDcCookieCore(spDcCookie.Trim());

                var likedWaiter = new TaskCompletionSource<List<TrackItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
                var libWaiter = new TaskCompletionSource<List<(string Id, string Name, string CoverUrl)>>(TaskCreationOptions.RunContinuationsAsynchronously);
                _likedTracksWaiter = likedWaiter;
                _libraryWaiter = libWaiter;
                _navCompletedFlag = false;

                try
                {
                    _webView.CoreWebView2.Navigate("https://open.spotify.com/collection/tracks");
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
                    await using var reg = timeoutCts.Token.Register(() =>
                    {
                        likedWaiter.TrySetResult(new List<TrackItem>());
                        libWaiter.TrySetResult(new List<(string, string, string)>());
                    });

                    var deadline = DateTime.UtcNow.AddSeconds(10);
                    DateTime? likedReadyAtUtc = null;
                    while (!timeoutCts.IsCancellationRequested && DateTime.UtcNow < deadline)
                    {
                        if (likedWaiter.Task.IsCompleted && libWaiter.Task.IsCompleted)
                            break;

                        if (likedWaiter.Task.IsCompleted)
                        {
                            likedReadyAtUtc ??= DateTime.UtcNow;
                            if ((DateTime.UtcNow - likedReadyAtUtc.Value).TotalSeconds >= 2.5)
                                break;
                        }

                        if (_navCompletedFlag && _webView?.CoreWebView2 != null)
                        {
                            if (!likedWaiter.Task.IsCompleted)
                            {
                                try
                                {
                                    var raw = await _webView.ExecuteScriptAsync(DomExtractTracksJs);
                                    var unquoted = JsonSerializer.Deserialize<string>(raw);
                                    if (!string.IsNullOrWhiteSpace(unquoted))
                                    {
                                        using var doc = JsonDocument.Parse(unquoted);
                                        if (doc.RootElement.TryGetProperty("tracks", out var tEl))
                                        {
                                            var domTracks = ParseDomTracksArray(tEl, 500);
                                            if (domTracks.Count > 0)
                                                likedWaiter.TrySetResult(domTracks);
                                        }
                                    }
                                }
                                catch { }
                            }

                            if (!libWaiter.Task.IsCompleted)
                            {
                                try
                                {
                                    var rawPl = await _webView.ExecuteScriptAsync(DomExtractPlaylistsJs);
                                    var unquotedPl = JsonSerializer.Deserialize<string>(rawPl);
                                    if (!string.IsNullOrWhiteSpace(unquotedPl))
                                    {
                                        var domPls = ParseDomPlaylistsJson(unquotedPl);
                                        if (domPls.Count > 0)
                                            libWaiter.TrySetResult(domPls);
                                    }
                                }
                                catch { }
                            }
                        }

                        await Task.Delay(350, timeoutCts.Token).ContinueWith(_ => { }, CancellationToken.None);
                    }

                    var liked = likedWaiter.Task.IsCompleted ? await likedWaiter.Task : new List<TrackItem>();
                    var pls = libWaiter.Task.IsCompleted ? await libWaiter.Task : new List<(string, string, string)>();
                    return (liked, pls);
                }
                finally
                {
                    if (ReferenceEquals(_likedTracksWaiter, likedWaiter)) _likedTracksWaiter = null;
                    if (ReferenceEquals(_libraryWaiter, libWaiter)) _libraryWaiter = null;
                }
            }).Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            return (new List<TrackItem>(), new List<(string, string, string)>());
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<List<TrackItem>> FetchAuthenticatedPlaylistTracksAsync(
        string spDcCookie,
        string playlistId,
        CancellationToken ct = default)
    {
        if (_disposed || string.IsNullOrWhiteSpace(spDcCookie) || string.IsNullOrWhiteSpace(playlistId))
            return new List<TrackItem>();

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed) return new List<TrackItem>();
            var disp = Application.Current?.Dispatcher;
            if (disp == null || disp.HasShutdownStarted) return new List<TrackItem>();

            return await await disp.InvokeAsync(async () =>
            {
                if (!await EnsureInitializedOnUiAsync(ct) || _webView?.CoreWebView2 == null)
                    return new List<TrackItem>();

                ApplySpDcCookieCore(spDcCookie.Trim());

                var plWaiter = new TaskCompletionSource<List<TrackItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
                _playlistTracksWaiter = plWaiter;
                _navCompletedFlag = false;

                try
                {
                    _webView.CoreWebView2.Navigate($"https://open.spotify.com/playlist/{Uri.EscapeDataString(playlistId.Trim())}");
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(10));
                    await using var reg = timeoutCts.Token.Register(() => plWaiter.TrySetResult(new List<TrackItem>()));

                    var deadline = DateTime.UtcNow.AddSeconds(10);
                    while (!plWaiter.Task.IsCompleted && !timeoutCts.IsCancellationRequested && DateTime.UtcNow < deadline)
                    {
                        if (_navCompletedFlag && _webView?.CoreWebView2 != null)
                        {
                            try
                            {
                                var raw = await _webView.ExecuteScriptAsync(DomExtractTracksJs);
                                var unquoted = JsonSerializer.Deserialize<string>(raw);
                                if (!string.IsNullOrWhiteSpace(unquoted))
                                {
                                    using var doc = JsonDocument.Parse(unquoted);
                                    if (doc.RootElement.TryGetProperty("tracks", out var tEl))
                                    {
                                        var domTracks = ParseDomTracksArray(tEl, 350);
                                        if (domTracks.Count > 0)
                                        {
                                            plWaiter.TrySetResult(domTracks);
                                            break;
                                        }
                                    }
                                }
                            }
                            catch { }
                        }

                        await Task.Delay(350, timeoutCts.Token).ContinueWith(_ => { }, CancellationToken.None);
                    }

                    return plWaiter.Task.IsCompleted ? await plWaiter.Task : new List<TrackItem>();
                }
                finally
                {
                    if (ReferenceEquals(_playlistTracksWaiter, plWaiter)) _playlistTracksWaiter = null;
                }
            }).Task.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch
        {
            return new List<TrackItem>();
        }
        finally
        {
            _gate.Release();
        }
    }

    private static List<(string Id, string Name, string CoverUrl)> ParseDomPlaylistsJson(string json)
    {
        var list = new List<(string Id, string Name, string CoverUrl)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;

        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var id = el.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            var name = el.TryGetProperty("name", out var nEl) ? nEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) || !seen.Add(id))
                continue;
            var cover = el.TryGetProperty("cover", out var cEl) ? (cEl.GetString() ?? "") : "";
            list.Add((id, name.Trim(), cover));
        }
        return list;
    }

    public void ResetSession()
    {
        _tokenWaiter?.TrySetResult((null, 0, true));
        _searchWaiter?.TrySetResult(new List<TrackItem>());
        _profileWaiter?.TrySetResult((null, null));
        _likedTracksWaiter?.TrySetResult(new List<TrackItem>());
        _libraryWaiter?.TrySetResult(new List<(string, string, string)>());
        _playlistTracksWaiter?.TrySetResult(new List<TrackItem>());
        LastAccessToken = null;
        LastTokenExpiresMs = 0;
        LastClientToken = null;
        LastUserId = null;
        LastUserName = null;

        var disp = Application.Current?.Dispatcher;
        if (disp != null && !disp.HasShutdownStarted)
        {
            _ = disp.BeginInvoke(() =>
            {
                try
                {
                    if (!_isGuestProfile)
                        _webView?.CoreWebView2?.CookieManager?.DeleteAllCookies();
                }
                catch { }
                CleanupUiResources();
            });
        }
    }

    private void CleanupUiResources()
    {
        _initialized = false;
        _navCompletedFlag = false;
        try { _webView?.Dispose(); } catch { }
        _webView = null;
        try { _hostWindow?.Close(); } catch { }
        _hostWindow = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ProxyService.ProxyChanged -= ResetSession;
        ResetSession();
        _gate.Dispose();
    }
}
