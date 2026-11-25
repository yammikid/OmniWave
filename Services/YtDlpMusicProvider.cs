using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
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

                string id = "";
                if (root.TryGetProperty("id", out var idEl))
                {
                    id = idEl.ValueKind == JsonValueKind.String
                        ? (idEl.GetString() ?? "")
                        : (idEl.ValueKind == JsonValueKind.Number ? idEl.GetRawText() : "");
                }
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
                    foreach (var th in thArr.EnumerateArray())
                    {
                        var thId = th.TryGetProperty("id", out var tidEl) ? tidEl.GetString() : null;
                        var tuStr = th.TryGetProperty("url", out var tu) ? tu.GetString() : null;
                        if (string.IsNullOrWhiteSpace(tuStr)) continue;
                        if (string.Equals(thId, "original", StringComparison.OrdinalIgnoreCase)) continue;
                        cover = tuStr;
                    }
                    if (string.IsNullOrWhiteSpace(cover))
                    {
                        var last = thArr[thArr.GetArrayLength() - 1];
                        if (last.TryGetProperty("url", out var tu)) cover = tu.GetString() ?? "";
                    }
                }
                if (!string.IsNullOrWhiteSpace(cover))
                    cover = cover.Replace("-original.", "-t500x500.", StringComparison.OrdinalIgnoreCase);

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

    private const string YtDlpLatestReleaseUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe";
    private static readonly SemaphoreSlim YtDlpDownloadGate = new(1, 1);

    private static string AppDataYtDlpPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OmniWave",
        "tools",
        "yt-dlp.exe");

    private static string? FindExistingYtDlpExecutable()
    {
        var baseExe = Path.Combine(AppContext.BaseDirectory, "yt-dlp.exe");
        if (File.Exists(baseExe)) return baseExe;

        var appDataExe = AppDataYtDlpPath;
        if (File.Exists(appDataExe)) return appDataExe;

        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (!string.IsNullOrWhiteSpace(localApp))
        {
            var winGetLink = Path.Combine(localApp, "Microsoft", "WinGet", "Links", "yt-dlp.exe");
            if (File.Exists(winGetLink)) return winGetLink;

            var winGetPkgRoot = Path.Combine(localApp, "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(winGetPkgRoot))
            {
                try
                {
                    foreach (var pkgDir in Directory.EnumerateDirectories(winGetPkgRoot, "yt-dlp*"))
                    {
                        var candidate = Path.Combine(pkgDir, "yt-dlp.exe");
                        if (File.Exists(candidate)) return candidate;
                    }
                }
                catch { }
            }
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrWhiteSpace(pathEnv))
        {
            foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            {
                try
                {
                    var candidate = Path.Combine(dir.Trim().Trim('"'), "yt-dlp.exe");
                    if (File.Exists(candidate)) return candidate;
                }
                catch { }
            }
        }

        return null;
    }

    public static string ResolveYtDlpExecutable()
        => FindExistingYtDlpExecutable() ?? "yt-dlp";

    public static async Task<string> EnsureYtDlpExecutableAsync(CancellationToken ct = default)
    {
        var existing = FindExistingYtDlpExecutable();
        if (existing != null) return existing;

        await YtDlpDownloadGate.WaitAsync(ct);
        try
        {
            existing = FindExistingYtDlpExecutable();
            if (existing != null) return existing;

            var targetPath = AppDataYtDlpPath;
            var dir = Path.GetDirectoryName(targetPath)!;
            Directory.CreateDirectory(dir);
            var partPath = targetPath + ".part";

            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("OmniWave/1.0");
            using var resp = await http.GetAsync(YtDlpLatestReleaseUrl, HttpCompletionOption.ResponseHeadersRead, ct);
            if (resp.IsSuccessStatusCode)
            {
                await using (var fs = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    await resp.Content.CopyToAsync(fs, ct);
                }

                if (new FileInfo(partPath).Length > 1_000_000)
                {
                    File.Move(partPath, targetPath, overwrite: true);
                    return targetPath;
                }
                try { File.Delete(partPath); } catch { }
            }
        }
        catch { }
        finally
        {
            YtDlpDownloadGate.Release();
        }

        return ResolveYtDlpExecutable();
    }

    private static async Task<string?> RunYtDlpAsync(string arguments, CancellationToken ct)
    {
        var exe = await EnsureYtDlpExecutableAsync(ct);
        var psi = new ProcessStartInfo
        {
            FileName = exe,
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
