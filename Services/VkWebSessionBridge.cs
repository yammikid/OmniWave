using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Windows;
using Microsoft.Web.WebView2.Wpf;

namespace DynamicIsland.Services;

/// <summary>
/// Невидимый фоновый WebView2-мост (разделяет профиль WebView2Auth) для выполнения
/// same-origin запросов к vk.ru / m.vk.ru (al_audio.php, /audio?act=load_section,
/// act=load_catalog_section) и прямого сбора треков/плейлистов из DOM страницы.
/// </summary>
public sealed class VkWebSessionBridge : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<string?>> _pending = new();

    private Window? _hostWindow;
    private WebView2? _webView;
    private TaskCompletionSource<bool>? _navWaiter;
    private bool _initialized;
    private string _currentOrigin = "";
    private bool _disposed;

    public async Task<string?> PostAlAudioAsync(string queryString, string formBody, long userId, CancellationToken ct)
    {
        if (_disposed) return null;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed) return null;
            string targetUrl = userId > 0 ? $"https://vk.ru/audios{userId}" : "https://vk.ru/audios";
            bool ready = await EnsureReadyOnUiAsync(targetUrl, ct).ConfigureAwait(false);
            if (!ready || _webView == null) return null;

            string qs = string.IsNullOrWhiteSpace(queryString) ? "" : (queryString.StartsWith('?') ? queryString : "?" + queryString);
            string js = $@"(async function(reqId) {{
  try {{
    document.cookie = 'remixaudio_show_alert_today=0; path=/; domain=.vk.ru';
    document.cookie = 'remixmdevice=1920/1080/2/!!-!!!!; path=/; domain=.vk.ru';
    const resp = await fetch('/al_audio.php' + {JsonSerializer.Serialize(qs)}, {{
      method: 'POST',
      credentials: 'include',
      headers: {{
        'Content-Type': 'application/x-www-form-urlencoded; charset=UTF-8',
        'X-Requested-With': 'XMLHttpRequest'
      }},
      body: {JsonSerializer.Serialize(formBody)}
    }});
    const text = await resp.text();
    window.chrome.webview.postMessage(JSON.stringify({{ id: reqId, ok: resp.ok, status: resp.status, text }}));
  }} catch (e) {{
    window.chrome.webview.postMessage(JSON.stringify({{ id: reqId, ok: false, error: String(e) }}));
  }}
}})";
            return await RunAsyncScriptWithReplyAsync(js, TimeSpan.FromSeconds(16), ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<string?> PostMobileAudioAsync(string formBody, long userId, CancellationToken ct)
    {
        if (_disposed) return null;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed) return null;
            bool ready = await EnsureReadyOnUiAsync("https://m.vk.ru/audio", ct).ConfigureAwait(false);
            if (!ready || _webView == null) return null;

            string js = $@"(async function(reqId) {{
  try {{
    document.cookie = 'remixaudio_show_alert_today=0; path=/; domain=.vk.ru';
    document.cookie = 'remixmdevice=1920/1080/2/!!-!!!!; path=/; domain=.vk.ru';
    const resp = await fetch('/audio', {{
      method: 'POST',
      credentials: 'include',
      headers: {{
        'Content-Type': 'application/x-www-form-urlencoded; charset=UTF-8',
        'X-Requested-With': 'XMLHttpRequest'
      }},
      body: {JsonSerializer.Serialize(formBody)}
    }});
    const text = await resp.text();
    window.chrome.webview.postMessage(JSON.stringify({{ id: reqId, ok: resp.ok, status: resp.status, text }}));
  }} catch (e) {{
    window.chrome.webview.postMessage(JSON.stringify({{ id: reqId, ok: false, error: String(e) }}));
  }}
}})";
            return await RunAsyncScriptWithReplyAsync(js, TimeSpan.FromSeconds(16), ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Выполняет комплексный сбор «Моей музыки» и плейлистов прямо внутри живой сессии браузера WebView2
    /// (desktop vk.ru/audios + al_audio.php + catalog_section + mobile m.vk.ru/audio + DOM data-audio/.audio_row).
    /// </summary>
    public async Task<string?> FetchFullLibraryJsonAsync(long userId, CancellationToken ct)
    {
        if (_disposed) return null;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed) return null;
            string desktopUrl = userId > 0 ? $"https://vk.ru/audios{userId}" : "https://vk.ru/audios";
            bool ready = await EnsureReadyOnUiAsync(desktopUrl, ct).ConfigureAwait(false);
            if (!ready || _webView == null) return null;

            await SyncCookiesAcrossVkDomainsOnUiAsync().ConfigureAwait(false);
            var curUrl = await GetCurrentUrlOnUiAsync().ConfigureAwait(false);
            if (curUrl.Contains("login.vk.", StringComparison.OrdinalIgnoreCase) ||
                curUrl.Contains("id.vk.", StringComparison.OrdinalIgnoreCase))
            {
                string comUrl = userId > 0 ? $"https://vk.com/audios{userId}" : "https://vk.com/audios";
                await EnsureReadyOnUiAsync(comUrl, ct).ConfigureAwait(false);
                await SyncCookiesAcrossVkDomainsOnUiAsync().ConfigureAwait(false);
            }

            string desktopJs = $@"(async function(reqId) {{
  const bundle = {{ uid: {userId}, rawPayloads: [], domTracks: [], playlists: [], playlistPayloads: [] }};
  try {{
    document.cookie = 'remixaudio_show_alert_today=0; path=/; domain=.vk.ru';
    document.cookie = 'remixmdevice=1920/1080/2/!!-!!!!; path=/; domain=.vk.ru';

    let uid = Number((window.vk && window.vk.id) || (window.cur && window.cur.oid) || {userId} || 0);
    if (!uid) {{
      const pm = window.location.pathname.match(/\/audios(-?\d+)/);
      if (pm) uid = Number(pm[1]);
    }}
    bundle.uid = uid;

    const postAl = async (qs, body) => {{
      try {{
        const r = await fetch('/al_audio.php' + qs, {{
          method: 'POST',
          credentials: 'include',
          headers: {{ 'Content-Type': 'application/x-www-form-urlencoded; charset=UTF-8', 'X-Requested-With': 'XMLHttpRequest' }},
          body
        }});
        return r.ok ? await r.text() : '';
      }} catch {{ return ''; }}
    }};

    const collectDom = () => {{
      document.querySelectorAll('[data-audio]').forEach(el => {{
        const raw = el.getAttribute('data-audio');
        if (raw) bundle.rawPayloads.push(raw);
      }});
      document.querySelectorAll('.audio_row, .audio_item, .AudioRow').forEach(row => {{
        const fullId = row.getAttribute('data-full-id') || (row.id || '').replace(/^audio_?/, '');
        const titleEl = row.querySelector('.audio_row__title_inner, .ai_title, [class*=""Title""]');
        const artistEl = row.querySelector('.audio_row__performers, .ai_artist, [class*=""Performer""], [class*=""Artist""]');
        const durEl = row.querySelector('.audio_row__duration, .ai_dur, [class*=""Duration""]');
        const covEl = row.querySelector('.audio_row__cover, .ai_play');
        const title = (titleEl && titleEl.textContent || '').trim();
        const artist = (artistEl && artistEl.textContent || '').trim();
        const durText = (durEl && durEl.textContent || '').trim();
        let cover = '';
        if (covEl) {{
          const st = covEl.getAttribute('style') || '';
          const m = st.match(/url\(['""]?([^'"")]+)['""]?\)/i);
          if (m) cover = m[1];
          const img = covEl.querySelector('img');
          if (!cover && img) cover = img.src || '';
        }}
        if (title || artist) {{
          bundle.domTracks.push({{ fullId, title, artist, durText, cover }});
        }}
      }});

      const html = document.documentElement.innerHTML || '';
      const plRe = /\/music\/(?:playlist|album)\/(-?\d+)_(\d+)_([a-zA-Z0-9]+)/g;
      let pm;
      const seenPl = new Set();
      while ((pm = plRe.exec(html)) !== null) {{
        const key = pm[1] + '_' + pm[2];
        if (seenPl.has(key)) continue;
        seenPl.add(key);
        bundle.playlists.push({{ ownerId: Number(pm[1]), playlistId: Number(pm[2]), accessHash: pm[3], title: '' }});
      }}
      document.querySelectorAll('a[href*=""/music/playlist/""], a[href*=""/music/album/""]').forEach(a => {{
        const href = a.getAttribute('href') || '';
        const m = href.match(/\/music\/(?:playlist|album)\/(-?\d+)_(\d+)_([a-zA-Z0-9]+)/);
        if (!m) return;
        const lbl = (a.textContent || a.getAttribute('title') || '').trim();
        const found = bundle.playlists.find(p => p.ownerId === Number(m[1]) && p.playlistId === Number(m[2]));
        if (found && lbl && !found.title) found.title = lbl;
      }});
    }};

    collectDom();

    if (uid > 0) {{
      // 1. load_section playlist_id=-1
      const s1 = await postAl('?act=load_section', `act=load_section&al=1&claim=0&is_loading_all=1&offset=0&owner_id=${{uid}}&playlist_id=-1&type=playlist`);
      if (s1) bundle.rawPayloads.push(s1);

      // 2. act=section&section=all + пагинация load_catalog_section
      const sAll = await postAl('?act=section', `act=section&al=1&claim=0&is_layer=0&owner_id=${{uid}}&section=all`);
      if (sAll) {{
        bundle.rawPayloads.push(sAll);
        let secMatch = sAll.match(/""sectionId""\s*:\s*""([^""]+)""/) || (document.documentElement.innerHTML || '').match(/""sectionId""\s*:\s*""([^""]+)""/);
        let nextMatch = sAll.match(/""nextFrom""\s*:\s*""([^""]+)""/) || sAll.match(/""next_from""\s*:\s*""([^""]+)""/);
        let sectionId = secMatch ? secMatch[1] : '';
        let nextFrom = nextMatch ? nextMatch[1] : '';

        for (let page = 0; page < 4 && sectionId && nextFrom; page++) {{
          const catRaw = await postAl('?act=load_catalog_section', `al=1&act=load_catalog_section&section_id=${{encodeURIComponent(sectionId)}}&start_from=${{encodeURIComponent(nextFrom)}}`);
          if (!catRaw) break;
          bundle.rawPayloads.push(catRaw);
          const nm = catRaw.match(/""nextFrom""\s*:\s*""([^""]+)""/) || catRaw.match(/""next_from""\s*:\s*""([^""]+)""/);
          const nextVal = nm ? nm[1] : '';
          if (!nextVal || nextVal === nextFrom) break;
          nextFrom = nextVal;
        }}
      }}

      // 3. act=section&section=playlists
      const sPl = await postAl('?act=section', `act=section&al=1&claim=0&is_layer=0&owner_id=${{uid}}&section=playlists`);
      if (sPl) {{
        bundle.rawPayloads.push(sPl);
        const plRe = /\/music\/(?:playlist|album)\/(-?\d+)_(\d+)_([a-zA-Z0-9]+)/g;
        let pm;
        while ((pm = plRe.exec(sPl)) !== null) {{
          if (!bundle.playlists.some(p => p.ownerId === Number(pm[1]) && p.playlistId === Number(pm[2]))) {{
            bundle.playlists.push({{ ownerId: Number(pm[1]), playlistId: Number(pm[2]), accessHash: pm[3], title: '' }});
          }}
        }}
      }}

      // 4. Загрузка треков найденных плейлистов
      for (const pl of bundle.playlists.slice(0, 15)) {{
        if (pl.playlistId <= 0) continue;
        const plRaw = await postAl('?act=load_section', `act=load_section&al=1&claim=0&is_loading_all=1&offset=0&owner_id=${{pl.ownerId}}&playlist_id=${{pl.playlistId}}&access_hash=${{encodeURIComponent(pl.accessHash || '')}}&type=playlist`);
        if (plRaw) {{
          bundle.playlistPayloads.push({{ ownerId: pl.ownerId, playlistId: pl.playlistId, accessHash: pl.accessHash, title: pl.title, raw: plRaw }});
        }}
      }}
    }}
  }} catch (err) {{
    bundle.error = String(err);
  }}
  window.chrome.webview.postMessage(JSON.stringify({{ id: reqId, ok: true, text: JSON.stringify(bundle) }}));
}})";

            var desktopResult = await RunAsyncScriptWithReplyAsync(desktopJs, TimeSpan.FromSeconds(25), ct).ConfigureAwait(false);

            // Дополнительно проверяем мобильный эндпоинт m.vk.ru/audio (как в python273/vk_api vk_audio.py)
            bool mobileReady = await EnsureReadyOnUiAsync("https://m.vk.ru/audio", ct).ConfigureAwait(false);
            if (mobileReady && _webView != null)
            {
                string mobileJs = $@"(async function(reqId) {{
  const mBundle = {{ uid: {userId}, rawPayloads: [], domTracks: [], playlists: [], playlistPayloads: [] }};
  try {{
    document.cookie = 'remixaudio_show_alert_today=0; path=/; domain=.vk.ru';
    document.cookie = 'remixmdevice=1920/1080/2/!!-!!!!; path=/; domain=.vk.ru';
    let uid = Number((window.vk && window.vk.id) || {userId} || 0);
    if (!uid) {{
      const m = (document.documentElement.innerHTML || '').match(/""(?:id|uid)""\s*:\s*(\d{{3,15}})/);
      if (m) uid = Number(m[1]);
    }}
    mBundle.uid = uid;

    document.querySelectorAll('[data-audio]').forEach(el => {{
      const raw = el.getAttribute('data-audio');
      if (raw) mBundle.rawPayloads.push(raw);
    }});

    if (uid > 0) {{
      const r1 = await fetch('/audio', {{
        method: 'POST',
        credentials: 'include',
        headers: {{ 'Content-Type': 'application/x-www-form-urlencoded; charset=UTF-8', 'X-Requested-With': 'XMLHttpRequest' }},
        body: `act=load_section&owner_id=${{uid}}&playlist_id=-1&offset=0&type=playlist&access_hash=&is_loading_all=1`
      }});
      if (r1.ok) mBundle.rawPayloads.push(await r1.text());

      const rPl = await fetch(`/audio?act=audio_playlists${{uid}}&offset=0`, {{ credentials: 'include' }});
      if (rPl.ok) {{
        const html = await rPl.text();
        const doc = new DOMParser().parseFromString(html, 'text/html');
        doc.querySelectorAll('.audioPlaylistsPage__item').forEach(item => {{
          const linkEl = item.querySelector('.audioPlaylistsPage__itemLink');
          const titleEl = item.querySelector('.audioPlaylistsPage__title');
          const href = linkEl ? (linkEl.getAttribute('href') || '') : '';
          const idM = href.match(/act=audio_playlist(-?\d+)_(\d+)/);
          const hashM = href.match(/access_hash=([a-zA-Z0-9]+)/);
          if (idM) {{
            mBundle.playlists.push({{
              ownerId: Number(idM[1]),
              playlistId: Number(idM[2]),
              accessHash: hashM ? hashM[1] : '',
              title: (titleEl && titleEl.textContent || '').trim()
            }});
          }}
        }});

        for (const pl of mBundle.playlists.slice(0, 15)) {{
          const pr = await fetch('/audio', {{
            method: 'POST',
            credentials: 'include',
            headers: {{ 'Content-Type': 'application/x-www-form-urlencoded; charset=UTF-8', 'X-Requested-With': 'XMLHttpRequest' }},
            body: `act=load_section&owner_id=${{pl.ownerId}}&playlist_id=${{pl.playlistId}}&offset=0&type=playlist&access_hash=${{encodeURIComponent(pl.accessHash || '')}}&is_loading_all=1`
          }});
          if (pr.ok) {{
            mBundle.playlistPayloads.push({{
              ownerId: pl.ownerId,
              playlistId: pl.playlistId,
              accessHash: pl.accessHash,
              title: pl.title,
              raw: await pr.text()
            }});
          }}
        }}
      }}
    }}
  }} catch (err) {{
    mBundle.error = String(err);
  }}
  window.chrome.webview.postMessage(JSON.stringify({{ id: reqId, ok: true, text: JSON.stringify(mBundle) }}));
}})";
                var mobileResult = await RunAsyncScriptWithReplyAsync(mobileJs, TimeSpan.FromSeconds(22), ct).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(desktopResult) && !string.IsNullOrWhiteSpace(mobileResult))
                    return JsonSerializer.Serialize(new[] { desktopResult, mobileResult });
                return mobileResult ?? desktopResult;
            }

            return desktopResult;
        }
        catch
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<string?> RunAsyncScriptWithReplyAsync(string jsFunctionLiteral, TimeSpan timeout, CancellationToken ct)
    {
        if (_webView == null) return null;
        string reqId = Guid.NewGuid().ToString("N");
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[reqId] = tcs;

        try
        {
            var disp = Application.Current?.Dispatcher;
            if (disp == null) return null;

            string invokeCall = $"{jsFunctionLiteral}({JsonSerializer.Serialize(reqId)});";
            await disp.InvokeAsync(() =>
            {
                if (_webView?.CoreWebView2 != null)
                    _ = _webView.ExecuteScriptAsync(invokeCall);
                else
                    tcs.TrySetResult(null);
            }).Task.ConfigureAwait(false);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            await using var reg = timeoutCts.Token.Register(() => tcs.TrySetResult(null));
            return await tcs.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(reqId, out _);
        }
    }

    public async Task<string?> RefreshCookiesAsync(CancellationToken ct)
    {
        if (_disposed) return null;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed) return null;
            var disp = Application.Current?.Dispatcher;
            if (disp == null) return null;

            return await await disp.InvokeAsync(async () =>
            {
                try
                {
                    if (_webView?.CoreWebView2 == null) return null;
                    var cm = _webView.CoreWebView2.CookieManager;
                    var lists = await Task.WhenAll(
                        cm.GetCookiesAsync("https://vk.ru"),
                        cm.GetCookiesAsync("https://m.vk.ru"),
                        cm.GetCookiesAsync("https://vk.com"),
                        cm.GetCookiesAsync("https://m.vk.com"),
                        cm.GetCookiesAsync("https://login.vk.ru"),
                        cm.GetCookiesAsync("https://login.vk.com"));

                    var valid = lists.SelectMany(x => x)
                        .Where(c => !string.IsNullOrWhiteSpace(c.Name))
                        .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                        .Select(g => g.First())
                        .ToList();
                    if (valid.Count == 0) return null;
                    return VkMusicProvider.EnsureRequiredAudioCookies(
                        string.Join("; ", valid.Select(c => $"{c.Name}={c.Value}")));
                }
                catch
                {
                    return null;
                }
            }).Task.ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<(long UserId, string? UserName)> ProbeIdentityAsync(CancellationToken ct)
    {
        if (_disposed) return (0, null);
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed) return (0, null);
            bool ready = await EnsureReadyOnUiAsync("https://vk.ru/audios", ct).ConfigureAwait(false);
            if (!ready) return (0, null);

            var disp = Application.Current?.Dispatcher;
            if (disp == null) return (0, null);

            return await await disp.InvokeAsync(async () =>
            {
                try
                {
                    if (_webView?.CoreWebView2 == null) return (0L, (string?)null);
                    string raw = await _webView.ExecuteScriptAsync(@"(function(){
                        try {
                            var id = (window.vk && Number(window.vk.id)) || (window.cur && Number(window.cur.oid)) || 0;
                            if (!id) {
                                var pm = window.location.pathname.match(/\/audios(-?\d+)/);
                                if (pm && Number(pm[1]) > 0) id = Number(pm[1]);
                            }
                            if (!id) {
                                var m = document.documentElement.innerHTML.match(/""(?:id|uid)""\s*:\s*(\d{3,15})/);
                                if (m) id = Number(m[1]);
                            }
                            var img = document.querySelector('img.TopNavBtn__profileImg');
                            var nameEl = document.querySelector('.TopNavBtn__profileName, .top_profile_name');
                            var name = (nameEl && nameEl.textContent) || (img && img.getAttribute('alt')) || '';
                            return JSON.stringify({ uid: id || 0, name: (name || '').trim() });
                        } catch(e) { return JSON.stringify({ uid: 0, name: '' }); }
                    })();");
                    var unquoted = JsonSerializer.Deserialize<string>(raw);
                    if (string.IsNullOrWhiteSpace(unquoted)) return (0L, (string?)null);
                    using var doc = JsonDocument.Parse(unquoted);
                    long uid = doc.RootElement.TryGetProperty("uid", out var u) ? u.GetInt64() : 0;
                    string? uname = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;
                    return (uid, string.IsNullOrWhiteSpace(uname) ? null : uname.Trim());
                }
                catch
                {
                    return (0L, (string?)null);
                }
            }).Task.ConfigureAwait(false);
        }
        catch
        {
            return (0, null);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<bool> EnsureReadyOnUiAsync(string targetUrl, CancellationToken ct)
    {
        var disp = Application.Current?.Dispatcher;
        if (disp == null) return false;

        return await await disp.InvokeAsync(async () =>
        {
            if (_disposed) return false;

            if (_initialized && _webView?.CoreWebView2 != null)
            {
                if (!string.Equals(_currentOrigin, targetUrl, StringComparison.OrdinalIgnoreCase))
                {
                    await NavigateAndSettleAsync(targetUrl, ct);
                    _currentOrigin = targetUrl;
                }
                return true;
            }

            try
            {
                var userDataFolder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "OmniWave",
                    "WebView2Auth");
                Directory.CreateDirectory(userDataFolder);

                _webView = new WebView2
                {
                    CreationProperties = new CoreWebView2CreationProperties
                    {
                        UserDataFolder = userDataFolder
                    }
                };

                _hostWindow = new Window
                {
                    Width = 1024,
                    Height = 768,
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

                _webView.NavigationCompleted += (_, _) => _navWaiter?.TrySetResult(true);

                _webView.WebMessageReceived += (_, ev) =>
                {
                    try
                    {
                        var json = ev.TryGetWebMessageAsString();
                        if (string.IsNullOrWhiteSpace(json)) return;
                        using var doc = JsonDocument.Parse(json);
                        var root = doc.RootElement;
                        if (!root.TryGetProperty("id", out var idEl)) return;
                        var reqId = idEl.GetString();
                        if (string.IsNullOrWhiteSpace(reqId)) return;

                        if (_pending.TryRemove(reqId, out var waiter))
                        {
                            string? text = root.TryGetProperty("text", out var tEl) ? tEl.GetString() : null;
                            waiter.TrySetResult(text);
                        }
                    }
                    catch { }
                };

                await NavigateAndSettleAsync(targetUrl, ct);
                _currentOrigin = targetUrl;
                _initialized = true;
                await SyncCookiesAcrossVkDomainsCoreAsync();
                return true;
            }
            catch
            {
                CleanupUiResources();
                return false;
            }
        }).Task.ConfigureAwait(false);
    }

    private async Task<string> GetCurrentUrlOnUiAsync()
    {
        var disp = Application.Current?.Dispatcher;
        if (disp == null) return "";
        return await disp.InvokeAsync(() => _webView?.Source?.ToString() ?? "").Task.ConfigureAwait(false);
    }

    private async Task SyncCookiesAcrossVkDomainsOnUiAsync()
    {
        var disp = Application.Current?.Dispatcher;
        if (disp == null) return;
        await await disp.InvokeAsync(SyncCookiesAcrossVkDomainsCoreAsync).Task.ConfigureAwait(false);
    }

    private async Task SyncCookiesAcrossVkDomainsCoreAsync()
    {
        try
        {
            if (_webView?.CoreWebView2 == null) return;
            var cm = _webView.CoreWebView2.CookieManager;
            var comCookies = await cm.GetCookiesAsync("https://vk.com");
            var ruCookies = await cm.GetCookiesAsync("https://vk.ru");
            var all = comCookies.Concat(ruCookies)
                .Where(c => !string.IsNullOrWhiteSpace(c.Name) && c.Name.StartsWith("remix", StringComparison.OrdinalIgnoreCase))
                .GroupBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            foreach (var dom in new[] { ".vk.ru", ".vk.com" })
            {
                foreach (var c in all)
                {
                    var copy = cm.CreateCookie(c.Name, c.Value, dom, "/");
                    copy.IsSecure = true;
                    cm.AddOrUpdateCookie(copy);
                }

                var alertCookie = cm.CreateCookie("remixaudio_show_alert_today", "0", dom, "/");
                alertCookie.IsSecure = true;
                cm.AddOrUpdateCookie(alertCookie);

                var devCookie = cm.CreateCookie("remixmdevice", "1920/1080/2/!!-!!!!", dom, "/");
                devCookie.IsSecure = true;
                cm.AddOrUpdateCookie(devCookie);
            }
        }
        catch { }
    }

    private async Task NavigateAndSettleAsync(string url, CancellationToken ct)
    {
        if (_webView?.CoreWebView2 == null) return;
        await NavigateOnceAsync(url, ct);

        // Если ВК выполняет промежуточный SSO-редирект через login.vk.ru / login.vk.com, даём ему завершиться
        for (int i = 0; i < 3; i++)
        {
            var cur = _webView.Source?.ToString() ?? "";
            if (cur.Contains("login.vk.", StringComparison.OrdinalIgnoreCase) ||
                cur.Contains("id.vk.", StringComparison.OrdinalIgnoreCase))
            {
                await WaitNextNavigationOrTimeoutAsync(TimeSpan.FromSeconds(2.5), ct);
            }
            else
            {
                break;
            }
        }
    }

    private async Task NavigateOnceAsync(string url, CancellationToken ct)
    {
        if (_webView?.CoreWebView2 == null) return;
        var navTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _navWaiter = navTcs;

        try
        {
            _webView.CoreWebView2.Navigate(url);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(12));
            await using var reg = timeoutCts.Token.Register(() => navTcs.TrySetResult(false));
            await navTcs.Task;
        }
        finally
        {
            if (ReferenceEquals(_navWaiter, navTcs))
                _navWaiter = null;
        }
    }

    private async Task WaitNextNavigationOrTimeoutAsync(TimeSpan timeout, CancellationToken ct)
    {
        var navTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _navWaiter = navTcs;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);
            await using var reg = timeoutCts.Token.Register(() => navTcs.TrySetResult(false));
            await navTcs.Task;
        }
        finally
        {
            if (ReferenceEquals(_navWaiter, navTcs))
                _navWaiter = null;
        }
    }

    public void ResetSession()
    {
        foreach (var kv in _pending)
        {
            if (_pending.TryRemove(kv.Key, out var waiter))
                waiter.TrySetResult(null);
        }

        var disp = Application.Current?.Dispatcher;
        if (disp != null && !disp.HasShutdownStarted)
        {
            _ = disp.BeginInvoke(CleanupUiResources);
        }
    }

    private void CleanupUiResources()
    {
        _initialized = false;
        _currentOrigin = "";
        try { _webView?.Dispose(); } catch { }
        _webView = null;
        try { _hostWindow?.Close(); } catch { }
        _hostWindow = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        ResetSession();
        _gate.Dispose();
    }
}
