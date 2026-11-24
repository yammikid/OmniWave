using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace DynamicIsland.Services;

public class YtDlpMusicProvider : IMusicProvider
{
    private readonly string _prefix;
    private readonly string _brandColor;

    public string Name { get; }
    public string BrandKey { get; }

    public YtDlpMusicProvider(string name, string brandKey, string searchPrefix, string brandColor)
    {
        Name = name;
        BrandKey = brandKey;
        _prefix = searchPrefix; // "scsearch" для SoundCloud, "ytsearch" для YouTube
        _brandColor = brandColor;
    }

    public async Task<List<TrackItem>> SearchAsync(string query, int limit = 8, CancellationToken ct = default)
    {
        var results = new List<TrackItem>();
        if (string.IsNullOrWhiteSpace(query)) return results;

        var cleanQuery = query.Replace("\"", "").Trim();
        var args = $"--flat-playlist --dump-json --no-warnings --ignore-errors \"{_prefix}{limit}:{cleanQuery}\"";
        var output = await RunYtDlpAsync(args, ct);
        if (string.IsNullOrWhiteSpace(output)) return results;

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                string id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
                string title = root.TryGetProperty("title", out var tEl) ? tEl.GetString() ?? "Без названия" : "Без названия";
                string artist = root.TryGetProperty("uploader", out var uEl) && !string.IsNullOrWhiteSpace(uEl.GetString())
                    ? uEl.GetString()!
                    : (root.TryGetProperty("channel", out var chEl) && !string.IsNullOrWhiteSpace(chEl.GetString())
                        ? chEl.GetString()!
                        : Name);

                string url = "";
                if (root.TryGetProperty("webpage_url", out var wpEl) && !string.IsNullOrWhiteSpace(wpEl.GetString()))
                    url = wpEl.GetString()!;
                else if (root.TryGetProperty("url", out var urlEl))
                    url = urlEl.GetString() ?? "";

                if (string.IsNullOrWhiteSpace(url)) continue;
                if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase) && BrandKey == "youtube" && !string.IsNullOrEmpty(id))
                    url = $"https://www.youtube.com/watch?v={id}";

                string cover = "";
                if (root.TryGetProperty("thumbnail", out var thEl) && thEl.ValueKind == JsonValueKind.String)
                    cover = thEl.GetString() ?? "";
                else if (root.TryGetProperty("thumbnails", out var thArr) &&
                         thArr.ValueKind == JsonValueKind.Array && thArr.GetArrayLength() > 0)
                {
                    var last = thArr[thArr.GetArrayLength() - 1];
                    if (last.TryGetProperty("url", out var tu)) cover = tu.GetString() ?? "";
                }

                double sec = root.TryGetProperty("duration", out var durEl) && durEl.ValueKind == JsonValueKind.Number
                    ? durEl.GetDouble()
                    : 0;

                results.Add(new TrackItem
                {
                    Id = id,
                    Source = Name,
                    BrandKey = BrandKey,
                    BrandColor = _brandColor,
                    Title = title,
                    Artist = artist,
                    PageUrl = url,
                    CoverUrl = cover,
                    Duration = TimeSpan.FromSeconds(sec)
                });
            }
            catch { }
        }

        return results;
    }

    public async Task<string?> ResolveStreamUrlAsync(TrackItem track, CancellationToken ct = default)
    {
        var args = $"-f \"bestaudio/best\" --no-playlist --no-warnings -g \"{track.PageUrl}\"";
        var output = await RunYtDlpAsync(args, ct);
        return output?.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .FirstOrDefault(l => l.StartsWith("http", StringComparison.OrdinalIgnoreCase));
    }

    public static async Task<(bool Available, string Info)> CheckYtDlpAsync(CancellationToken ct = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(5));

        try
        {
            var output = await RunYtDlpAsync("--version", timeoutCts.Token);
            var version = output?.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(l => l.Trim())
                .FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));

            if (!string.IsNullOrWhiteSpace(version))
                return (true, $"yt-dlp найден (версия {version})");

            return (false, "yt-dlp ответил пустым выводом.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (false, "Превышен таймаут проверки yt-dlp.");
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static string ResolveYtDlpExecutable()
    {
        var localExe = Path.Combine(AppContext.BaseDirectory, "yt-dlp.exe");
        return File.Exists(localExe) ? localExe : "yt-dlp";
    }

    private static async Task<string?> RunYtDlpAsync(string arguments, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ResolveYtDlpExecutable(),
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        Process? proc;
        try
        {
            proc = Process.Start(psi);
        }
        catch (Win32Exception)
        {
            throw new InvalidOperationException(
                "Не найден yt-dlp. Установите его командой 'winget install yt-dlp.yt-dlp' или положите yt-dlp.exe рядом с программой.");
        }

        if (proc == null) return null;

        using (proc)
        {
            using var reg = ct.Register(() =>
            {
                try { if (!proc.HasExited) proc.Kill(true); } catch { }
            });

            var stdoutTask = proc.StandardOutput.ReadToEndAsync(ct);
            var stderrTask = proc.StandardError.ReadToEndAsync(ct);
            await proc.WaitForExitAsync(ct);
            _ = await stderrTask;
            return await stdoutTask;
        }
    }
}
