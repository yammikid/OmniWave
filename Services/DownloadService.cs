using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DynamicIsland.Services;

public class DownloadService : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly Regex YtDlpProgressRegex = new(
        @"\[download\]\s+(\d+(?:\.\d+)?)%",
        RegexOptions.Compiled);

    private readonly Dictionary<string, IMusicProvider> _providers;
    private readonly SettingsService _settings;
    private readonly HttpClient _http;
    private readonly string _indexPath;
    private readonly object _lock = new();
    private readonly ConcurrentDictionary<string, byte> _activeDownloads = new();
    private Dictionary<string, string> _downloadedFiles;

    public event Action<TrackItem>? DownloadCompleted;

    public DownloadService(IEnumerable<IMusicProvider> providers, SettingsService settings)
    {
        _providers = providers.ToDictionary(p => p.BrandKey, StringComparer.OrdinalIgnoreCase);
        _settings = settings;

        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");

        var appDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "OmniWave");
        Directory.CreateDirectory(appDir);
        _indexPath = Path.Combine(appDir, "downloads.json");
        _downloadedFiles = LoadIndex();
    }

    public string GetDownloadFolder()
    {
        var folder = _settings.Current.DownloadFolder;
        if (string.IsNullOrWhiteSpace(folder))
            folder = AppSettings.DefaultDownloadFolder;

        Directory.CreateDirectory(folder);
        return folder;
    }

    public string? TryGetLocalFile(TrackItem? track)
    {
        if (track == null) return null;

        if (!string.IsNullOrWhiteSpace(track.LocalFilePath) && File.Exists(track.LocalFilePath))
        {
            track.IsDownloaded = true;
            return track.LocalFilePath;
        }

        string? storedPath = null;
        bool pruned = false;
        lock (_lock)
        {
            if (_downloadedFiles.TryGetValue(track.UniqueKey, out var path))
            {
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    storedPath = path;
                }
                else
                {
                    _downloadedFiles.Remove(track.UniqueKey);
                    pruned = true;
                }
            }
            if (pruned) SaveIndexUnlocked();
        }

        if (storedPath != null)
        {
            track.LocalFilePath = storedPath;
            track.IsDownloaded = true;
            return storedPath;
        }

        track.IsDownloaded = false;
        track.LocalFilePath = null;
        return null;
    }

    public void SyncDownloadedFlags(IEnumerable<TrackItem> tracks)
    {
        foreach (var track in tracks)
            TryGetLocalFile(track);
    }

    private const long MaxLiveStreamBytes = 40L * 1024 * 1024;
    private static readonly TimeSpan DefaultLiveRecordDuration = TimeSpan.FromSeconds(60);

    public static bool IsLiveStream(TrackItem track, string? streamUrl = null)
        => string.Equals(track.BrandKey, "radio", StringComparison.OrdinalIgnoreCase) ||
           (track.Duration <= TimeSpan.Zero && !string.IsNullOrWhiteSpace(streamUrl) && IsHlsUrl(streamUrl));

    public async Task<string?> DownloadTrackAsync(
        TrackItem track,
        CancellationToken ct = default,
        TimeSpan? maxRecordDuration = null)
    {
        var existing = TryGetLocalFile(track);
        if (existing != null)
            return existing;

        if (!_activeDownloads.TryAdd(track.UniqueKey, 1))
            return null;

        string? tempPartPath = null;
        try
        {
            track.IsDownloading = true;
            track.DownloadProgress = 0;

            if (!_providers.TryGetValue(track.BrandKey, out var provider))
                return null;

            var streamUrl = await provider.ResolveStreamUrlAsync(track, ct);
            if (string.IsNullOrWhiteSpace(streamUrl))
                return null;

            var folder = GetDownloadFolder();
            var baseName = BuildSafeFileName(track);

            string? savedPath;
            bool isHls = IsHlsUrl(streamUrl);
            bool isLive = IsLiveStream(track, streamUrl);
            TimeSpan? liveCap = isLive ? (maxRecordDuration ?? DefaultLiveRecordDuration) : maxRecordDuration;

            if (isHls)
            {
                savedPath = await DownloadViaYtDlpAsync(streamUrl, folder, baseName, track, liveCap, ct);
            }
            else
            {
                (savedPath, tempPartPath) = await DownloadDirectHttpAsync(streamUrl, folder, baseName, track, liveCap, ct);
            }

            if (!string.IsNullOrWhiteSpace(savedPath) && File.Exists(savedPath))
            {
                track.LocalFilePath = savedPath;
                track.IsDownloaded = true;
                track.DownloadProgress = 100;

                lock (_lock)
                {
                    _downloadedFiles[track.UniqueKey] = savedPath;
                    SaveIndexUnlocked();
                }

                DownloadCompleted?.Invoke(track);
                return savedPath;
            }
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            if (!string.IsNullOrWhiteSpace(tempPartPath) && File.Exists(tempPartPath))
            {
                try { File.Delete(tempPartPath); } catch { }
            }

            track.IsDownloading = false;
            if (!track.IsDownloaded)
                track.DownloadProgress = 0;

            _activeDownloads.TryRemove(track.UniqueKey, out _);
        }

        return null;
    }

    private async Task<(string? SavedPath, string TempPartPath)> DownloadDirectHttpAsync(
        string streamUrl,
        string folder,
        string baseName,
        TrackItem track,
        TimeSpan? maxDuration,
        CancellationToken ct)
    {
        var partPath = Path.Combine(folder, $"{baseName}.{Guid.NewGuid():N}.part");
        using var resp = await _http.GetAsync(streamUrl, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        var ext = ResolveExtension(streamUrl, resp.Content.Headers.ContentType?.MediaType);
        var targetPath = EnsureUniqueTargetPath(folder, baseName, ext, track.UniqueKey);

        long? totalBytes = resp.Content.Headers.ContentLength;
        bool useLiveLimit = maxDuration.HasValue || (!totalBytes.HasValue && track.Duration <= TimeSpan.Zero);
        TimeSpan effectiveCap = maxDuration ?? DefaultLiveRecordDuration;
        var sw = useLiveLimit ? Stopwatch.StartNew() : null;

        long totalRead = 0;
        int lastPct = -1;

        await using (var src = await resp.Content.ReadAsStreamAsync(ct))
        await using (var dst = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
        {
            var buffer = new byte[81920];
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                totalRead += read;

                if (totalBytes.HasValue && totalBytes.Value > 0)
                {
                    int pct = (int)Math.Clamp(totalRead * 100L / totalBytes.Value, 0, 99);
                    if (pct != lastPct)
                    {
                        lastPct = pct;
                        track.DownloadProgress = pct;
                    }
                }
                else if (sw != null)
                {
                    int pct = (int)Math.Clamp(sw.Elapsed.TotalMilliseconds * 100.0 / effectiveCap.TotalMilliseconds, 0, 99);
                    if (pct != lastPct)
                    {
                        lastPct = pct;
                        track.DownloadProgress = pct;
                    }

                    if (sw.Elapsed >= effectiveCap || totalRead >= MaxLiveStreamBytes)
                        break;
                }
            }
        }

        if (File.Exists(targetPath))
            File.Delete(targetPath);
        File.Move(partPath, targetPath);
        return (targetPath, partPath);
    }

    private async Task<string?> DownloadViaYtDlpAsync(
        string streamUrl,
        string folder,
        string baseName,
        TrackItem track,
        TimeSpan? maxDuration,
        CancellationToken ct)
    {
        var uniqueBase = $"{baseName}_{Math.Abs(track.UniqueKey.GetHashCode()):X}";
        var outTemplate = Path.Combine(folder, $"{uniqueBase}.%(ext)s");
        var args = $"--newline -f \"bestaudio/best\" --no-playlist --no-warnings -o \"{outTemplate}\" \"{streamUrl}\"";

        var psi = new ProcessStartInfo
        {
            FileName = ResolveYtDlpExecutable(),
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        if (proc == null) return null;

        using var timeoutCts = maxDuration.HasValue
            ? CancellationTokenSource.CreateLinkedTokenSource(ct)
            : null;
        if (timeoutCts != null && maxDuration.HasValue)
            timeoutCts.CancelAfter(maxDuration.Value);

        var effCt = timeoutCts?.Token ?? ct;
        using var reg = effCt.Register(() =>
        {
            try { if (!proc.HasExited) proc.Kill(true); } catch { }
        });

        var stderrTask = proc.StandardError.ReadToEndAsync(effCt);
        try
        {
            while (!proc.StandardOutput.EndOfStream)
            {
                var line = await proc.StandardOutput.ReadLineAsync(effCt);
                if (string.IsNullOrWhiteSpace(line)) continue;

                var m = YtDlpProgressRegex.Match(line);
                if (m.Success &&
                    double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double pct))
                {
                    track.DownloadProgress = (int)Math.Clamp(Math.Round(pct), 0, 99);
                }
            }

            await proc.WaitForExitAsync(effCt);
            _ = await stderrTask;
        }
        catch (OperationCanceledException) when (timeoutCts != null && !ct.IsCancellationRequested)
        {
            // Ограничение времени записи live HLS-потока сработало штатно
        }

        if (proc.ExitCode != 0 && !maxDuration.HasValue) return null;

        var candidate = Directory.GetFiles(folder, $"{uniqueBase}.*")
            .FirstOrDefault(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase) &&
                                 !f.EndsWith(".ytdl", StringComparison.OrdinalIgnoreCase));
        if (candidate == null) return null;

        var ext = Path.GetExtension(candidate);
        var cleanPath = EnsureUniqueTargetPath(folder, baseName, ext, track.UniqueKey);
        if (!string.Equals(candidate, cleanPath, StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(cleanPath)) File.Delete(cleanPath);
            File.Move(candidate, cleanPath);
            return cleanPath;
        }

        return candidate;
    }

    private string EnsureUniqueTargetPath(string folder, string baseName, string ext, string uniqueKey)
    {
        string candidate = Path.Combine(folder, baseName + ext);
        lock (_lock)
        {
            if (!File.Exists(candidate))
                return candidate;

            // Если файл принадлежит этому же треку — перезаписываем чисто
            if (_downloadedFiles.TryGetValue(uniqueKey, out var knownPath) &&
                string.Equals(knownPath, candidate, StringComparison.OrdinalIgnoreCase))
                return candidate;

            for (int i = 1; i < 1000; i++)
            {
                var next = Path.Combine(folder, $"{baseName} ({i}){ext}");
                if (!File.Exists(next))
                    return next;
            }
        }

        return Path.Combine(folder, $"{baseName}_{Guid.NewGuid():N}{ext}");
    }

    private static bool IsHlsUrl(string url)
    {
        if (url.Contains(".m3u8", StringComparison.OrdinalIgnoreCase))
            return true;

        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var path = uri.AbsolutePath.ToLowerInvariant();
            if (path.EndsWith(".m3u8", StringComparison.Ordinal) || path.Contains("/hls/"))
                return true;
        }

        return false;
    }

    private static string ResolveExtension(string url, string? mediaType)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var ext = Path.GetExtension(uri.AbsolutePath);
            if (!string.IsNullOrWhiteSpace(ext) && ext.Length is >= 2 and <= 6)
            {
                var lower = ext.ToLowerInvariant();
                if (lower is ".mp3" or ".m4a" or ".mp4" or ".webm" or ".ogg" or ".opus" or ".flac" or ".wav" or ".aac")
                    return lower == ".mp4" ? ".m4a" : lower;
            }
        }

        return mediaType?.ToLowerInvariant() switch
        {
            "audio/mpeg" or "audio/mp3" => ".mp3",
            "audio/mp4" or "audio/aac" or "audio/x-m4a" => ".m4a",
            "audio/webm" or "video/webm" => ".webm",
            "audio/ogg" or "application/ogg" => ".ogg",
            "audio/flac" => ".flac",
            "audio/wav" or "audio/x-wav" => ".wav",
            "audio/opus" => ".opus",
            _ => ".m4a"
        };
    }

    private static string BuildSafeFileName(TrackItem track)
    {
        var raw = string.IsNullOrWhiteSpace(track.Artist) ||
                  string.Equals(track.Artist, track.Source, StringComparison.OrdinalIgnoreCase)
            ? track.Title
            : $"{track.Artist} - {track.Title}";

        if (string.IsNullOrWhiteSpace(raw))
            raw = "OmniWave Track";

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
            sb.Append(invalid.Contains(ch) ? '_' : ch);

        var cleaned = Regex.Replace(sb.ToString().Trim(), @"\s{2,}", " ").Trim('.');
        if (cleaned.Length > 110)
            cleaned = cleaned[..110].Trim();

        return string.IsNullOrWhiteSpace(cleaned) ? "OmniWave Track" : cleaned;
    }

    private static string ResolveYtDlpExecutable()
    {
        var localExe = Path.Combine(AppContext.BaseDirectory, "yt-dlp.exe");
        return File.Exists(localExe) ? localExe : "yt-dlp";
    }

    private Dictionary<string, string> LoadIndex()
    {
        try
        {
            if (File.Exists(_indexPath))
            {
                var json = File.ReadAllText(_indexPath);
                var data = JsonSerializer.Deserialize<Dictionary<string, string>>(json);
                if (data != null)
                    return new Dictionary<string, string>(data, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch { }

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    private void SaveIndexUnlocked()
    {
        try
        {
            var json = JsonSerializer.Serialize(_downloadedFiles, JsonOptions);
            File.WriteAllText(_indexPath, json);
        }
        catch { }
    }

    public static void RevealInExplorer(string filePath)
    {
        try
        {
            if (File.Exists(filePath))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{filePath}\"",
                    UseShellExecute = true
                });
            }
        }
        catch { }
    }

    public void Dispose() => _http.Dispose();
}
