using System.IO;
using System.IO.Compression;
using System.Net.Http;

namespace DynamicIsland.Services;

public sealed record VoiceDownloadProgress(
    bool Busy,
    int Percent,
    string Stage,
    double DownloadedMb,
    double TotalMb,
    string? Error = null);

/// <summary>
/// Загрузка и хранение локальных бинарников и моделей Vosk + Whisper.cpp в %AppData%\OmniWave\voice\.
/// Не увеличивает размер исполняемого файла DynamicIsland.exe.
/// </summary>
public sealed class VoiceModelManager
{
    public const string VoskBinUrl = "https://github.com/alphacep/vosk-api/releases/download/v0.3.45/vosk-win64-0.3.45.zip";
    public const string VoskModelRuUrl = "https://alphacephei.com/vosk/models/vosk-model-small-ru-0.22.zip";
    public const string WhisperBinUrl = "https://github.com/ggml-org/whisper.cpp/releases/download/b5907/whisper-bin-x64.zip";
    public const string WhisperModelSmallQ5Url = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-small-q5_1.bin";
    public const string WhisperModelBaseUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-base.bin";

    private static readonly string[] RequiredVoskDlls =
    {
        "libwinpthread-1.dll",
        "libgcc_s_seh-1.dll",
        "libstdc++-6.dll",
        "libvosk.dll"
    };

    private readonly SemaphoreSlim _installLock = new(1, 1);
    private CancellationTokenSource? _downloadCts;

    public string RootDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OmniWave",
        "voice");

    public string VoskBinDir => Path.Combine(RootDir, "vosk-bin");
    public string VoskModelDir => Path.Combine(RootDir, "vosk-model-ru");
    public string WhisperBinDir => Path.Combine(RootDir, "whisper-bin");
    public string WhisperModelDir => Path.Combine(RootDir, "whisper-model");

    public VoiceDownloadProgress CurrentProgress { get; private set; } = new(false, 0, "", 0, 0);
    public event Action<VoiceDownloadProgress>? ProgressChanged;

    public bool IsVoskBinaryReady()
        => RequiredVoskDlls.All(dll => File.Exists(Path.Combine(VoskBinDir, dll)));

    public string? GetResolvedVoskModelPath()
    {
        if (!Directory.Exists(VoskModelDir))
            return null;

        if (LooksLikeVoskModelDir(VoskModelDir))
            return VoskModelDir;

        try
        {
            foreach (var sub in Directory.GetDirectories(VoskModelDir))
            {
                if (LooksLikeVoskModelDir(sub))
                    return sub;
            }
        }
        catch { }

        return null;
    }

    public bool IsVoskReady()
        => IsVoskBinaryReady() && !string.IsNullOrWhiteSpace(GetResolvedVoskModelPath());

    public string GetWhisperServerPath() => Path.Combine(WhisperBinDir, "whisper-server.exe");

    public string GetWhisperModelPath(string variant)
    {
        string file = string.Equals(variant, "base", StringComparison.OrdinalIgnoreCase)
            ? "ggml-base.bin"
            : "ggml-small-q5_1.bin";
        return Path.Combine(WhisperModelDir, file);
    }

    public bool IsWhisperBinaryReady()
        => File.Exists(GetWhisperServerPath()) &&
           File.Exists(Path.Combine(WhisperBinDir, "whisper.dll")) &&
           File.Exists(Path.Combine(WhisperBinDir, "ggml.dll"));

    public bool IsWhisperReady(string variant)
    {
        var modelPath = GetWhisperModelPath(variant);
        return IsWhisperBinaryReady() &&
               File.Exists(modelPath) &&
               new FileInfo(modelPath).Length > 10 * 1024 * 1024;
    }

    public double GetTotalDiskUsageMb()
    {
        if (!Directory.Exists(RootDir)) return 0;
        try
        {
            long bytes = Directory.EnumerateFiles(RootDir, "*", SearchOption.AllDirectories)
                .Where(f => !f.EndsWith(".part", StringComparison.OrdinalIgnoreCase))
                .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
            return Math.Round(bytes / (1024.0 * 1024.0), 1);
        }
        catch
        {
            return 0;
        }
    }

    public void CancelDownload()
    {
        try { _downloadCts?.Cancel(); } catch { }
    }

    public async Task<(bool Ok, string Message)> InstallAsync(string mode, string whisperVariant, CancellationToken outerCt = default)
    {
        if (!await _installLock.WaitAsync(0, outerCt))
            return (false, "Установка уже выполняется");

        var cts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        var prev = Interlocked.Exchange(ref _downloadCts, cts);
        try { prev?.Dispose(); } catch { }

        try
        {
            Directory.CreateDirectory(RootDir);
            bool needVosk = mode is "hybrid" or "vosk";
            bool needWhisper = mode is "hybrid" or "whisper";

            if (needVosk)
            {
                if (!IsVoskBinaryReady())
                    await DownloadAndExtractVoskBinAsync(cts.Token);

                if (string.IsNullOrWhiteSpace(GetResolvedVoskModelPath()))
                    await DownloadAndExtractVoskModelAsync(cts.Token);
            }

            if (needWhisper)
            {
                if (!IsWhisperBinaryReady())
                    await DownloadAndExtractWhisperBinAsync(cts.Token);

                if (!File.Exists(GetWhisperModelPath(whisperVariant)) ||
                    new FileInfo(GetWhisperModelPath(whisperVariant)).Length < 10 * 1024 * 1024)
                {
                    await DownloadWhisperModelAsync(whisperVariant, cts.Token);
                }
            }

            PublishProgress(new VoiceDownloadProgress(false, 100, "Готово к работе", 0, 0));
            return (true, "Голосовые модели успешно установлены");
        }
        catch (OperationCanceledException)
        {
            PublishProgress(new VoiceDownloadProgress(false, 0, "Установка отменена", 0, 0));
            return (false, "Загрузка моделей отменена");
        }
        catch (Exception ex)
        {
            PublishProgress(new VoiceDownloadProgress(false, 0, $"Ошибка: {ex.Message}", 0, 0, ex.Message));
            return (false, $"Ошибка установки: {ex.Message}");
        }
        finally
        {
            Interlocked.CompareExchange(ref _downloadCts, null, cts);
            try { cts.Dispose(); } catch { }
            _installLock.Release();
        }
    }

    public bool DeleteAllModels()
    {
        CancelDownload();
        try
        {
            if (Directory.Exists(RootDir))
                Directory.Delete(RootDir, recursive: true);
            PublishProgress(new VoiceDownloadProgress(false, 0, "Модели удалены", 0, 0));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task DownloadAndExtractVoskBinAsync(CancellationToken ct)
    {
        string tmpZip = Path.Combine(RootDir, "vosk-bin.zip.part");
        await DownloadFileWithProgressAsync(VoskBinUrl, tmpZip, "Скачивание Vosk Runtime (14 МБ)...", ct);
        PublishProgress(new VoiceDownloadProgress(true, 99, "Распаковка Vosk Runtime...", 0, 0));

        Directory.CreateDirectory(VoskBinDir);
        using (var za = ZipFile.OpenRead(tmpZip))
        {
            foreach (var entry in za.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                if (!entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                string dest = Path.Combine(VoskBinDir, entry.Name);
                entry.ExtractToFile(dest, overwrite: true);
            }
        }
        TryDeleteFile(tmpZip);
    }

    private async Task DownloadAndExtractVoskModelAsync(CancellationToken ct)
    {
        string tmpZip = Path.Combine(RootDir, "vosk-model-ru.zip.part");
        await DownloadFileWithProgressAsync(VoskModelRuUrl, tmpZip, "Скачивание русской модели Vosk (44 МБ)...", ct);
        PublishProgress(new VoiceDownloadProgress(true, 99, "Распаковка русской модели Vosk...", 0, 0));

        if (Directory.Exists(VoskModelDir))
        {
            try { Directory.Delete(VoskModelDir, recursive: true); } catch { }
        }
        Directory.CreateDirectory(VoskModelDir);
        ZipFile.ExtractToDirectory(tmpZip, VoskModelDir, overwriteFiles: true);
        TryDeleteFile(tmpZip);
    }

    private async Task DownloadAndExtractWhisperBinAsync(CancellationToken ct)
    {
        string tmpZip = Path.Combine(RootDir, "whisper-bin.zip.part");
        await DownloadFileWithProgressAsync(WhisperBinUrl, tmpZip, "Скачивание Whisper.cpp (9 МБ)...", ct);
        PublishProgress(new VoiceDownloadProgress(true, 99, "Распаковка Whisper.cpp...", 0, 0));

        Directory.CreateDirectory(WhisperBinDir);
        using (var za = ZipFile.OpenRead(tmpZip))
        {
            foreach (var entry in za.Entries)
            {
                if (string.IsNullOrEmpty(entry.Name)) continue;
                bool keep = string.Equals(entry.Name, "whisper-server.exe", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(entry.Name, "whisper-cli.exe", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(entry.Name, "whisper.dll", StringComparison.OrdinalIgnoreCase) ||
                            (entry.Name.StartsWith("ggml", StringComparison.OrdinalIgnoreCase) &&
                             entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));
                if (!keep) continue;

                string dest = Path.Combine(WhisperBinDir, entry.Name);
                entry.ExtractToFile(dest, overwrite: true);
            }
        }
        TryDeleteFile(tmpZip);
    }

    private async Task DownloadWhisperModelAsync(string variant, CancellationToken ct)
    {
        bool isBase = string.Equals(variant, "base", StringComparison.OrdinalIgnoreCase);
        string url = isBase ? WhisperModelBaseUrl : WhisperModelSmallQ5Url;
        string label = isBase ? "Скачивание модели Whisper Base (141 МБ)..." : "Скачивание модели Whisper Small RU+EN (181 МБ)...";
        string dest = GetWhisperModelPath(variant);
        Directory.CreateDirectory(WhisperModelDir);
        string part = dest + ".part";

        await DownloadFileWithProgressAsync(url, part, label, ct);
        File.Move(part, dest, overwrite: true);
    }

    private async Task DownloadFileWithProgressAsync(string url, string destPartPath, string stage, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPartPath)!);
        TryDeleteFile(destPartPath);

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OmniWave/2.0");

        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
        resp.EnsureSuccessStatusCode();

        long totalBytes = resp.Content.Headers.ContentLength ?? 0;
        double totalMb = Math.Round(totalBytes / (1024.0 * 1024.0), 1);

        PublishProgress(new VoiceDownloadProgress(true, 0, stage, 0, totalMb));

        await using var src = await resp.Content.ReadAsStreamAsync(ct);
        await using (var fs = new FileStream(destPartPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
        {
            byte[] buffer = new byte[81920];
            long received = 0;
            long lastReportTick = Environment.TickCount64;

            while (true)
            {
                int read = await src.ReadAsync(buffer, ct);
                if (read <= 0) break;

                await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                received += read;

                long now = Environment.TickCount64;
                if (now - lastReportTick >= 150)
                {
                    lastReportTick = now;
                    int pct = totalBytes > 0 ? (int)Math.Clamp(received * 100L / totalBytes, 0, 99) : 0;
                    double dlMb = Math.Round(received / (1024.0 * 1024.0), 1);
                    PublishProgress(new VoiceDownloadProgress(true, pct, stage, dlMb, totalMb));
                }
            }
        }
    }

    private void PublishProgress(VoiceDownloadProgress p)
    {
        CurrentProgress = p;
        ProgressChanged?.Invoke(p);
    }

    private static bool LooksLikeVoskModelDir(string dir)
        => File.Exists(Path.Combine(dir, "conf", "model.conf")) ||
           File.Exists(Path.Combine(dir, "am", "final.mdl"));

    private static void TryDeleteFile(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}
