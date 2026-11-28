using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;

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
    public const string WhisperModelLargeV3TurboUrl = "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/ggml-large-v3-turbo-q5_0.bin";
    public const string OnnxRuntimeNuPkgUrl = "https://api.nuget.org/v3-flatcontainer/microsoft.ml.onnxruntime.directml/1.20.1/microsoft.ml.onnxruntime.directml.1.20.1.nupkg";
    public const string SupertonicBaseUrl = "https://huggingface.co/Supertone/supertonic-3/resolve/main/";
    public const string GigaAmBaseUrl = "https://huggingface.co/istupakov/gigaam-v3-onnx/resolve/main/";
    public const string SileroVadOnnxUrl = "https://github.com/snakers4/silero-vad/raw/v5.1.2/src/silero_vad/data/silero_vad.onnx";
    public const string SileroVadOnnxFallbackUrl = "https://raw.githubusercontent.com/snakers4/silero-vad/master/src/silero_vad/data/silero_vad.onnx";
    public const string WhisperVadGgmlUrl = "https://huggingface.co/ggml-org/whisper-vad/resolve/main/ggml-silero-v5.1.2.bin";

    private const int ParallelChunkBytes = 2 * 1024 * 1024;
    private const int ParallelConcurrency = 4;
    private const int ParallelThresholdBytes = 8 * 1024 * 1024;
    private const int MaxChunkRetries = 10;
    private const int MaxSequentialRetries = 10;
    private static readonly TimeSpan HeaderTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ReadStallTimeout = TimeSpan.FromSeconds(30);

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
    public string OnnxRuntimeDir => Path.Combine(RootDir, "onnxruntime");
    public string VadDir => Path.Combine(RootDir, "vad");
    public string SileroVadOnnxPath => Path.Combine(VadDir, "silero_vad.onnx");
    public string WhisperVadBinPath => Path.Combine(VadDir, "ggml-silero-v5.1.2.bin");
    public string SupertonicDir => Path.Combine(RootDir, "tts-supertonic");
    public string SupertonicOnnxDir => Path.Combine(SupertonicDir, "onnx");
    public string SupertonicVoicesDir => Path.Combine(SupertonicDir, "voice_styles");
    public string GigaAmDir => Path.Combine(RootDir, "gigaam-v3");
    public string GigaAmModelPath => Path.Combine(GigaAmDir, "v3_e2e_ctc.int8.onnx");
    public string GigaAmVocabPath => Path.Combine(GigaAmDir, "v3_e2e_ctc_vocab.txt");

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

    public string GetWhisperModelPath(string? variant = null)
        => Path.Combine(WhisperModelDir, "ggml-large-v3-turbo-q5_0.bin");

    public bool IsWhisperBinaryReady()
        => File.Exists(GetWhisperServerPath()) &&
           File.Exists(Path.Combine(WhisperBinDir, "whisper.dll")) &&
           File.Exists(Path.Combine(WhisperBinDir, "ggml.dll"));

    public bool IsWhisperReady(string? variant = null)
    {
        var modelPath = GetWhisperModelPath();
        return IsWhisperBinaryReady() &&
               File.Exists(modelPath) &&
               new FileInfo(modelPath).Length > 100 * 1024 * 1024 &&
               HasValidGgmlHeader(modelPath);
    }

    public void CleanupObsoleteWhisperModels()
    {
        try
        {
            if (!Directory.Exists(WhisperModelDir))
                return;
            foreach (var name in new[] { "ggml-small-q5_1.bin", "ggml-small-q5_1.bin.part", "ggml-base.bin", "ggml-base.bin.part" })
                TryDeleteFile(Path.Combine(WhisperModelDir, name));
        }
        catch { }
    }

    public bool IsOnnxRuntimeReady()
        => File.Exists(Path.Combine(OnnxRuntimeDir, "onnxruntime.dll"));

    public bool IsSileroVadReady()
        => IsOnnxRuntimeReady() && HasMinFileSize(SileroVadOnnxPath, 500 * 1024);

    public bool IsWhisperVadReady()
        => HasMinFileSize(WhisperVadBinPath, 400 * 1024) && HasValidGgmlHeader(WhisperVadBinPath);

    public bool IsGigaAmReady()
        => IsOnnxRuntimeReady() &&
           HasMinFileSize(GigaAmModelPath, 150L * 1024 * 1024) &&
           HasMinFileSize(GigaAmVocabPath, 500);

    public bool IsNeuralTtsReady()
    {
        if (!IsOnnxRuntimeReady())
            return false;

        return HasMinFileSize(Path.Combine(SupertonicOnnxDir, "tts.json"), 1024) &&
               HasMinFileSize(Path.Combine(SupertonicOnnxDir, "unicode_indexer.json"), 50 * 1024) &&
               HasMinFileSize(Path.Combine(SupertonicOnnxDir, "duration_predictor.onnx"), 1024 * 1024) &&
               HasMinFileSize(Path.Combine(SupertonicOnnxDir, "text_encoder.onnx"), 10 * 1024 * 1024) &&
               HasMinFileSize(Path.Combine(SupertonicOnnxDir, "vector_estimator.onnx"), 100 * 1024 * 1024) &&
               HasMinFileSize(Path.Combine(SupertonicOnnxDir, "vocoder.onnx"), 40 * 1024 * 1024) &&
               HasMinFileSize(Path.Combine(SupertonicVoicesDir, "F1.json"), 100 * 1024);
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
            bool needGigaAm = mode is "hybrid" or "gigaam";
            bool needWhisper = mode is "hybrid" or "whisper";
            bool needTts = mode is "hybrid" or "tts";
            bool needVad = mode is "hybrid" or "whisper" or "vosk" or "vad" or "gigaam";

            if (needVosk)
            {
                if (!IsVoskBinaryReady())
                    await DownloadAndExtractVoskBinAsync(cts.Token);

                if (string.IsNullOrWhiteSpace(GetResolvedVoskModelPath()))
                    await DownloadAndExtractVoskModelAsync(cts.Token);
            }

            if (needGigaAm && !IsGigaAmReady())
            {
                await DownloadGigaAmAsync(cts.Token);
            }

            if (needWhisper)
            {
                if (!IsWhisperBinaryReady())
                    await DownloadAndExtractWhisperBinAsync(cts.Token);

                string modelPath = GetWhisperModelPath();
                if (!File.Exists(modelPath) ||
                    new FileInfo(modelPath).Length < 100 * 1024 * 1024 ||
                    !HasValidGgmlHeader(modelPath))
                {
                    await DownloadWhisperModelAsync(cts.Token);
                }
            }

            CleanupObsoleteWhisperModels();

            if (needVad && (!IsSileroVadReady() || !IsWhisperVadReady()))
            {
                await DownloadVadModelsAsync(cts.Token);
            }

            if (needTts && !IsNeuralTtsReady())
            {
                await DownloadSupertonicTtsAsync(cts.Token);
            }

            PublishProgress(new VoiceDownloadProgress(false, 100, "Готово к работе", 0, 0));
            return (true, "Голосовые модели успешно установлены");
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            PublishProgress(new VoiceDownloadProgress(false, 0, "Установка отменена", 0, 0));
            return (false, "Загрузка моделей отменена");
        }
        catch (Exception ex)
        {
            string friendly = FormatDownloadError(ex);
            PublishProgress(new VoiceDownloadProgress(false, 0, $"Ошибка: {friendly}", 0, 0, friendly));
            return (false, $"Ошибка установки: {friendly}");
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

    private Task DownloadAndExtractVoskBinAsync(CancellationToken ct)
        => DownloadAndExtractZipAsync(
            VoskBinUrl,
            "vosk-bin.zip.part",
            "Скачивание Vosk Runtime (14 МБ)...",
            "Распаковка Vosk Runtime...",
            ct,
            za =>
            {
                Directory.CreateDirectory(VoskBinDir);
                foreach (var entry in za.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    if (!entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                    string dest = Path.Combine(VoskBinDir, entry.Name);
                    entry.ExtractToFile(dest, overwrite: true);
                }
            });

    private Task DownloadAndExtractVoskModelAsync(CancellationToken ct)
        => DownloadAndExtractZipAsync(
            VoskModelRuUrl,
            "vosk-model-ru.zip.part",
            "Скачивание русской модели Vosk (44 МБ)...",
            "Распаковка русской модели Vosk...",
            ct,
            za =>
            {
                if (Directory.Exists(VoskModelDir))
                {
                    try { Directory.Delete(VoskModelDir, recursive: true); } catch { }
                }
                Directory.CreateDirectory(VoskModelDir);
                za.ExtractToDirectory(VoskModelDir, overwriteFiles: true);
            });

    private Task DownloadAndExtractWhisperBinAsync(CancellationToken ct)
        => DownloadAndExtractZipAsync(
            WhisperBinUrl,
            "whisper-bin.zip.part",
            "Скачивание Whisper.cpp (9 МБ)...",
            "Распаковка Whisper.cpp...",
            ct,
            za =>
            {
                Directory.CreateDirectory(WhisperBinDir);
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
            });

    private async Task DownloadAndExtractZipAsync(
        string url,
        string partFileName,
        string downloadStage,
        string extractStage,
        CancellationToken ct,
        Action<ZipArchive> extractAction)
    {
        string tmpZip = Path.Combine(RootDir, partFileName);
        for (int attempt = 1; attempt <= 2; attempt++)
        {
            await DownloadFileWithProgressAsync(url, tmpZip, downloadStage, ct, HasValidZipHeader);
            PublishProgress(new VoiceDownloadProgress(true, 99, extractStage, 0, 0));

            try
            {
                using (var za = ZipFile.OpenRead(tmpZip))
                {
                    extractAction(za);
                }
                TryDeleteFile(tmpZip);
                return;
            }
            catch (InvalidDataException) when (attempt == 1)
            {
                TryDeleteFile(tmpZip);
            }
        }
    }

    private async Task DownloadWhisperModelAsync(CancellationToken ct)
    {
        string dest = GetWhisperModelPath();
        Directory.CreateDirectory(WhisperModelDir);
        string part = dest + ".part";

        await DownloadFileWithProgressAsync(
            WhisperModelLargeV3TurboUrl,
            part,
            "Скачивание Whisper Large v3 Turbo RU+EN (547 МБ)...",
            ct,
            HasValidGgmlHeader);

        if (!File.Exists(part) || new FileInfo(part).Length < 100 * 1024 * 1024 || !HasValidGgmlHeader(part))
        {
            TryDeleteFile(part);
            throw new InvalidDataException("Повреждённый файл модели Whisper Turbo v3 (неверный заголовок или размер)");
        }

        File.Move(part, dest, overwrite: true);
    }

    private async Task DownloadVadModelsAsync(CancellationToken ct)
    {
        if (!IsOnnxRuntimeReady())
            await DownloadAndExtractOnnxRuntimeAsync(ct);

        Directory.CreateDirectory(VadDir);

        if (!IsSileroVadReady())
        {
            string part = SileroVadOnnxPath + ".part";
            try
            {
                await DownloadFileWithProgressAsync(SileroVadOnnxUrl, part, "Скачивание Silero VAD v5 ONNX (2.2 МБ)...", ct, HasNonHtmlHeader);
            }
            catch when (!ct.IsCancellationRequested)
            {
                TryDeleteFile(part);
                await DownloadFileWithProgressAsync(SileroVadOnnxFallbackUrl, part, "Скачивание Silero VAD v5 ONNX (зеркало)...", ct, HasNonHtmlHeader);
            }

            if (!HasMinFileSize(part, 500 * 1024) || !HasNonHtmlHeader(part))
            {
                TryDeleteFile(part);
                throw new InvalidDataException("Повреждённый файл Silero VAD ONNX");
            }
            File.Move(part, SileroVadOnnxPath, overwrite: true);
        }

        if (!IsWhisperVadReady())
        {
            string part = WhisperVadBinPath + ".part";
            await DownloadFileWithProgressAsync(WhisperVadGgmlUrl, part, "Скачивание Whisper Silero VAD (0.9 МБ)...", ct, HasValidGgmlHeader);
            if (!HasMinFileSize(part, 400 * 1024) || !HasValidGgmlHeader(part))
            {
                TryDeleteFile(part);
                throw new InvalidDataException("Повреждённый файл Whisper VAD GGML");
            }
            File.Move(part, WhisperVadBinPath, overwrite: true);
        }
    }

    private Task DownloadAndExtractOnnxRuntimeAsync(CancellationToken ct)
        => DownloadAndExtractZipAsync(
            OnnxRuntimeNuPkgUrl,
            "onnxruntime-win-x64.nupkg.part",
            "Скачивание ONNX Runtime (15 МБ)...",
            "Распаковка ONNX Runtime...",
            ct,
            za =>
            {
                Directory.CreateDirectory(OnnxRuntimeDir);
                foreach (var entry in za.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;
                    string norm = entry.FullName.Replace('\\', '/');
                    if (!norm.StartsWith("runtimes/win-x64/native/", StringComparison.OrdinalIgnoreCase)) continue;
                    if (!entry.Name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase)) continue;
                    string dest = Path.Combine(OnnxRuntimeDir, entry.Name);
                    entry.ExtractToFile(dest, overwrite: true);
                }
            });

    private async Task DownloadSupertonicTtsAsync(CancellationToken ct)
    {
        if (!IsOnnxRuntimeReady())
            await DownloadAndExtractOnnxRuntimeAsync(ct);

        Directory.CreateDirectory(SupertonicOnnxDir);
        Directory.CreateDirectory(SupertonicVoicesDir);

        await DownloadSupertonicFileAsync("onnx/tts.json", Path.Combine(SupertonicOnnxDir, "tts.json"), 1024, "Конфигурация нейро-голоса...", true, ct);
        await DownloadSupertonicFileAsync("onnx/unicode_indexer.json", Path.Combine(SupertonicOnnxDir, "unicode_indexer.json"), 50 * 1024, "Словарь фонем RU+EN...", true, ct);

        foreach (var preset in SupertonicTtsEngine.FemaleVoices)
        {
            await DownloadSupertonicFileAsync(
                $"voice_styles/{preset.StyleFileName}",
                Path.Combine(SupertonicVoicesDir, preset.StyleFileName),
                100 * 1024,
                $"Голосовой профиль {preset.ShortName}...",
                true,
                ct);
        }

        await DownloadSupertonicFileAsync("onnx/duration_predictor.onnx", Path.Combine(SupertonicOnnxDir, "duration_predictor.onnx"), 1024 * 1024, "Модуль ритма речи (3.5 МБ)...", false, ct);
        await DownloadSupertonicFileAsync("onnx/text_encoder.onnx", Path.Combine(SupertonicOnnxDir, "text_encoder.onnx"), 10 * 1024 * 1024, "Текстовый энкодер RU+EN (35 МБ)...", false, ct);
        await DownloadSupertonicFileAsync("onnx/vector_estimator.onnx", Path.Combine(SupertonicOnnxDir, "vector_estimator.onnx"), 100 * 1024 * 1024, "Акустическая модель Supertonic 3 (245 МБ)...", false, ct);
        await DownloadSupertonicFileAsync("onnx/vocoder.onnx", Path.Combine(SupertonicOnnxDir, "vocoder.onnx"), 40 * 1024 * 1024, "Студийный вокодер 44.1 кГц (97 МБ)...", false, ct);
    }

    private async Task DownloadSupertonicFileAsync(
        string relativeUrl,
        string destPath,
        long minBytes,
        string stage,
        bool isJson,
        CancellationToken ct)
    {
        if (HasMinFileSize(destPath, minBytes) && (!isJson || HasValidJsonHeader(destPath)))
            return;

        string part = destPath + ".part";
        Func<string, bool> validator = isJson ? HasValidJsonHeader : HasNonHtmlHeader;
        await DownloadFileWithProgressAsync(SupertonicBaseUrl + relativeUrl, part, stage, ct, validator);

        if (!HasMinFileSize(part, minBytes) || (isJson && !HasValidJsonHeader(part)) || (!isJson && !HasNonHtmlHeader(part)))
        {
            TryDeleteFile(part);
            throw new InvalidDataException($"Повреждён файл нейро-голоса: {Path.GetFileName(destPath)}");
        }

        File.Move(part, destPath, overwrite: true);
    }

    private async Task DownloadGigaAmAsync(CancellationToken ct)
    {
        if (!IsOnnxRuntimeReady())
            await DownloadAndExtractOnnxRuntimeAsync(ct);

        Directory.CreateDirectory(GigaAmDir);

        if (!HasMinFileSize(GigaAmVocabPath, 500) || !HasValidGigaAmVocabHeader(GigaAmVocabPath))
        {
            string vocabPart = GigaAmVocabPath + ".part";
            await DownloadFileWithProgressAsync(
                GigaAmBaseUrl + "v3_e2e_ctc_vocab.txt",
                vocabPart,
                "Словарь Sber GigaAM v3 RU...",
                ct,
                HasValidGigaAmVocabHeader);

            if (!HasMinFileSize(vocabPart, 500) || !HasValidGigaAmVocabHeader(vocabPart))
            {
                TryDeleteFile(vocabPart);
                throw new InvalidDataException("Повреждённый файл словаря Sber GigaAM v3");
            }
            File.Move(vocabPart, GigaAmVocabPath, overwrite: true);
        }

        if (!HasMinFileSize(GigaAmModelPath, 150L * 1024 * 1024) || !HasNonHtmlHeader(GigaAmModelPath))
        {
            string modelPart = GigaAmModelPath + ".part";
            await DownloadFileWithProgressAsync(
                GigaAmBaseUrl + "v3_e2e_ctc.int8.onnx",
                modelPart,
                "Скачивание Sber GigaAM v3 RU (214 МБ)...",
                ct,
                HasNonHtmlHeader);

            if (!HasMinFileSize(modelPart, 150L * 1024 * 1024) || !HasNonHtmlHeader(modelPart))
            {
                TryDeleteFile(modelPart);
                throw new InvalidDataException("Повреждённый файл модели Sber GigaAM v3");
            }
            File.Move(modelPart, GigaAmModelPath, overwrite: true);
        }
    }

    private static HttpClient CreateHttpClient(bool forceFreshConnection)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.None,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            PooledConnectionLifetime = forceFreshConnection ? TimeSpan.Zero : TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 8,
            UseProxy = true
        };

        var http = new HttpClient(handler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("OmniWave/2.0");
        return http;
    }

    private static async Task<(Uri ResolvedUri, long TotalBytes, bool SupportsRange)> ProbeDownloadAsync(
        string url,
        CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                using var http = CreateHttpClient(forceFreshConnection: attempt > 1);
                using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                headerCts.CancelAfter(HeaderTimeout);

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Version = HttpVersion.Version11;
                req.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
                req.Headers.Range = new RangeHeaderValue(0, 0);

                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, headerCts.Token);

                if (resp.StatusCode == HttpStatusCode.PartialContent)
                {
                    long total = resp.Content.Headers.ContentRange?.Length ?? 0;
                    Uri resolved = resp.RequestMessage?.RequestUri ?? new Uri(url);
                    return (resolved, total, total > 0);
                }

                if (resp.StatusCode == HttpStatusCode.OK)
                {
                    long total = resp.Content.Headers.ContentLength ?? 0;
                    bool supportsRange = total > 0 && resp.Headers.AcceptRanges.Any(r =>
                        string.Equals(r, "bytes", StringComparison.OrdinalIgnoreCase));
                    Uri resolved = resp.RequestMessage?.RequestUri ?? new Uri(url);
                    return (resolved, total, supportsRange);
                }

                if ((int)resp.StatusCode == 429 || (int)resp.StatusCode >= 500)
                    throw new HttpRequestException($"HTTP {(int)resp.StatusCode}", null, resp.StatusCode);

                resp.EnsureSuccessStatusCode();
                throw new HttpRequestException($"HTTP {(int)resp.StatusCode}", null, resp.StatusCode);
            }
            catch (OperationCanceledException oce) when (!ct.IsCancellationRequested)
            {
                if (attempt >= 4)
                    throw new TimeoutException("Таймаут опроса сервера загрузки", oce);
                await Task.Delay(TimeSpan.FromSeconds(attempt), ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && attempt < 4 && IsTransientNetworkError(ex, ct))
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt), ct);
            }
        }
    }

    private async Task DownloadFileWithProgressAsync(
        string url,
        string destPartPath,
        string stage,
        CancellationToken ct,
        Func<string, bool>? validateHeader = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPartPath)!);

        var (resolvedUri, totalBytes, supportsRange) = await ProbeDownloadAsync(url, ct);

        long existingBytes = File.Exists(destPartPath) ? new FileInfo(destPartPath).Length : 0;
        if (existingBytes > 0)
        {
            bool invalidExisting = !supportsRange ||
                                   (totalBytes > 0 && existingBytes > totalBytes) ||
                                   (validateHeader != null && existingBytes >= 4 && !validateHeader(destPartPath));
            if (invalidExisting)
            {
                TryDeleteFile(destPartPath);
                existingBytes = 0;
            }
        }

        double totalMb = totalBytes > 0 ? Math.Round(totalBytes / (1024.0 * 1024.0), 1) : 0;
        if (totalBytes > 0 && existingBytes == totalBytes)
        {
            double dlMb = Math.Round(existingBytes / (1024.0 * 1024.0), 1);
            PublishProgress(new VoiceDownloadProgress(true, 99, stage, dlMb, totalMb));
            return;
        }

        int initPct = totalBytes > 0 ? (int)Math.Clamp(existingBytes * 100L / totalBytes, 0, 99) : 0;
        double initMb = Math.Round(existingBytes / (1024.0 * 1024.0), 1);
        PublishProgress(new VoiceDownloadProgress(true, initPct, stage, initMb, totalMb));

        if (supportsRange && totalBytes >= ParallelThresholdBytes)
        {
            try
            {
                await DownloadParallelChunkedAsync(url, resolvedUri, totalBytes, existingBytes, destPartPath, stage, ct);
            }
            catch (RangeNotHonoredException)
            {
                existingBytes = File.Exists(destPartPath) ? new FileInfo(destPartPath).Length : 0;
                await DownloadSequentialWithResumeAsync(url, totalBytes, supportsRange, existingBytes, destPartPath, stage, ct);
            }
        }
        else
        {
            await DownloadSequentialWithResumeAsync(url, totalBytes, supportsRange, existingBytes, destPartPath, stage, ct);
        }

        if (totalBytes > 0)
        {
            long actual = File.Exists(destPartPath) ? new FileInfo(destPartPath).Length : 0;
            if (actual != totalBytes)
                throw new IOException($"Размер загруженного файла не совпадает ({actual}/{totalBytes} байт)");
        }
    }

    private async Task DownloadParallelChunkedAsync(
        string url,
        Uri resolvedUri,
        long totalBytes,
        long initialOffset,
        string destPartPath,
        string stage,
        CancellationToken ct)
    {
        Uri activeUri = resolvedUri;
        object uriLock = new();
        using var refreshLock = new SemaphoreSlim(1, 1);
        using var sharedHttp = CreateHttpClient(forceFreshConnection: false);

        long offset = initialOffset;
        double totalMb = Math.Round(totalBytes / (1024.0 * 1024.0), 1);
        long lastReportTick = Environment.TickCount64;
        object progressLock = new();
        long batchInFlightBytes = 0;

        void ReportProgress(bool force, string? customStage = null)
        {
            long now = Environment.TickCount64;
            if (!force && now - Interlocked.Read(ref lastReportTick) < 150)
                return;

            lock (progressLock)
            {
                now = Environment.TickCount64;
                if (!force && now - lastReportTick < 150)
                    return;
                Interlocked.Exchange(ref lastReportTick, now);

                long dl = Math.Clamp(offset + Volatile.Read(ref batchInFlightBytes), 0, totalBytes);
                int pct = totalBytes > 0 ? (int)Math.Clamp(dl * 100L / totalBytes, 0, 99) : 0;
                double dlMb = Math.Round(dl / (1024.0 * 1024.0), 1);
                PublishProgress(new VoiceDownloadProgress(true, pct, customStage ?? stage, dlMb, totalMb));
            }
        }

        async Task<byte[]> DownloadChunkWithRetryAsync(long start, long end, int expectedLen)
        {
            for (int attempt = 1; ; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                long chunkGot = 0;
                HttpClient? retryHttp = null;

                try
                {
                    var http = attempt == 1 ? sharedHttp : (retryHttp = CreateHttpClient(forceFreshConnection: true));
                    Uri currentUri;
                    lock (uriLock) currentUri = activeUri;

                    using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    headerCts.CancelAfter(HeaderTimeout);

                    using var req = new HttpRequestMessage(HttpMethod.Get, currentUri);
                    req.Version = HttpVersion.Version11;
                    req.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
                    req.Headers.Range = new RangeHeaderValue(start, end);

                    using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, headerCts.Token);

                    if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Gone)
                    {
                        if (attempt >= MaxChunkRetries)
                            resp.EnsureSuccessStatusCode();

                        await refreshLock.WaitAsync(ct);
                        try
                        {
                            Uri latest;
                            lock (uriLock) latest = activeUri;
                            if (latest == currentUri)
                            {
                                var refreshed = await ProbeDownloadAsync(url, ct);
                                lock (uriLock) activeUri = refreshed.ResolvedUri;
                            }
                        }
                        finally
                        {
                            refreshLock.Release();
                        }

                        ReportProgress(force: true, $"{stage} (обновление ссылки {attempt}/{MaxChunkRetries})");
                        await Task.Delay(Math.Min(1000 * attempt, 6000), ct);
                        continue;
                    }

                    if (resp.StatusCode == HttpStatusCode.OK)
                        throw new RangeNotHonoredException();

                    if ((int)resp.StatusCode == 429 || (int)resp.StatusCode >= 500)
                        throw new HttpRequestException($"HTTP {(int)resp.StatusCode}", null, resp.StatusCode);

                    if (resp.StatusCode != HttpStatusCode.PartialContent)
                        resp.EnsureSuccessStatusCode();

                    var cr = resp.Content.Headers.ContentRange;
                    if (cr?.From != null && cr.From.Value != start)
                        throw new IOException($"Неожиданное смещение сегмента: {cr.From.Value} вместо {start}");

                    byte[] data = new byte[expectedLen];
                    await using var stream = await resp.Content.ReadAsStreamAsync(ct);
                    int pos = 0;

                    while (pos < expectedLen)
                    {
                        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                        readCts.CancelAfter(ReadStallTimeout);
                        int n = await stream.ReadAsync(data.AsMemory(pos, expectedLen - pos), readCts.Token);
                        if (n <= 0)
                            break;

                        pos += n;
                        chunkGot += n;
                        Interlocked.Add(ref batchInFlightBytes, n);
                        ReportProgress(force: false);
                    }

                    if (pos != expectedLen)
                        throw new IOException($"Обрыв сегмента {start}-{end}: получено {pos} из {expectedLen} байт");

                    return data;
                }
                catch (OperationCanceledException oce) when (!ct.IsCancellationRequested)
                {
                    if (chunkGot > 0)
                        Interlocked.Add(ref batchInFlightBytes, -chunkGot);

                    if (attempt >= MaxChunkRetries)
                        throw new TimeoutException("Таймаут чтения сегмента модели", oce);

                    ReportProgress(force: true, $"{stage} (повтор сегмента {attempt}/{MaxChunkRetries})");
                    await Task.Delay(Math.Min(1000 * attempt, 6000), ct);
                }
                catch (Exception ex) when (!ct.IsCancellationRequested && attempt < MaxChunkRetries && IsTransientNetworkError(ex, ct))
                {
                    if (chunkGot > 0)
                        Interlocked.Add(ref batchInFlightBytes, -chunkGot);

                    ReportProgress(force: true, $"{stage} (повтор сегмента {attempt}/{MaxChunkRetries})");
                    await Task.Delay(Math.Min(1000 * attempt, 6000), ct);
                }
                finally
                {
                    retryHttp?.Dispose();
                }
            }
        }

        while (offset < totalBytes)
        {
            ct.ThrowIfCancellationRequested();

            var chunks = new List<(long Start, long End, int ExpectedLen)>(ParallelConcurrency);
            long cursor = offset;
            for (int i = 0; i < ParallelConcurrency && cursor < totalBytes; i++)
            {
                long end = Math.Min(cursor + ParallelChunkBytes - 1, totalBytes - 1);
                int len = (int)(end - cursor + 1);
                chunks.Add((cursor, end, len));
                cursor = end + 1;
            }

            Interlocked.Exchange(ref batchInFlightBytes, 0);

            var tasks = chunks.Select(c => DownloadChunkWithRetryAsync(c.Start, c.End, c.ExpectedLen)).ToArray();
            byte[][] buffers = await Task.WhenAll(tasks);

            using (var fs = new FileStream(destPartPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                fs.Seek(offset, SeekOrigin.Begin);
                foreach (var buf in buffers)
                    await fs.WriteAsync(buf, ct);
                await fs.FlushAsync(ct);
            }

            offset = cursor;
            Interlocked.Exchange(ref batchInFlightBytes, 0);
            ReportProgress(force: true);
        }
    }

    private async Task DownloadSequentialWithResumeAsync(
        string url,
        long totalBytes,
        bool supportsRange,
        long initialOffset,
        string destPartPath,
        string stage,
        CancellationToken ct)
    {
        long offset = initialOffset;

        for (int attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                offset = File.Exists(destPartPath) ? new FileInfo(destPartPath).Length : 0;
                if (totalBytes > 0 && offset == totalBytes)
                    return;

                using var http = CreateHttpClient(forceFreshConnection: attempt > 1);
                using var headerCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                headerCts.CancelAfter(HeaderTimeout);

                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Version = HttpVersion.Version11;
                req.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
                if (offset > 0 && supportsRange)
                    req.Headers.Range = new RangeHeaderValue(offset, null);

                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, headerCts.Token);

                if (resp.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
                {
                    if (totalBytes > 0 && offset == totalBytes)
                        return;

                    TryDeleteFile(destPartPath);
                    offset = 0;
                    if (attempt > MaxSequentialRetries)
                        resp.EnsureSuccessStatusCode();
                    await Task.Delay(Math.Min(1000 * attempt, 6000), ct);
                    continue;
                }

                if ((int)resp.StatusCode == 429 || (int)resp.StatusCode >= 500)
                    throw new HttpRequestException($"HTTP {(int)resp.StatusCode}", null, resp.StatusCode);

                resp.EnsureSuccessStatusCode();

                FileMode fileMode;
                if (resp.StatusCode == HttpStatusCode.PartialContent && offset > 0)
                {
                    var cr = resp.Content.Headers.ContentRange;
                    if (cr?.From != null && cr.From.Value != offset)
                    {
                        TryDeleteFile(destPartPath);
                        offset = 0;
                        fileMode = FileMode.Create;
                    }
                    else
                    {
                        if (cr?.Length > 0)
                            totalBytes = cr.Length.Value;
                        fileMode = FileMode.Append;
                    }
                }
                else
                {
                    offset = 0;
                    if (resp.Content.Headers.ContentLength is long len && len > 0)
                        totalBytes = len;
                    fileMode = FileMode.Create;
                }

                double totalMb = totalBytes > 0 ? Math.Round(totalBytes / (1024.0 * 1024.0), 1) : 0;
                long lastReportTick = Environment.TickCount64;

                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using (var fs = new FileStream(destPartPath, fileMode, FileAccess.Write, FileShare.None, 81920, useAsync: true))
                {
                    byte[] buffer = new byte[81920];
                    while (true)
                    {
                        int read;
                        try
                        {
                            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            readCts.CancelAfter(ReadStallTimeout);
                            read = await src.ReadAsync(buffer, readCts.Token);
                        }
                        catch
                        {
                            try { await fs.FlushAsync(CancellationToken.None); } catch { }
                            throw;
                        }

                        if (read <= 0) break;

                        await fs.WriteAsync(buffer.AsMemory(0, read), ct);
                        offset += read;

                        long now = Environment.TickCount64;
                        if (now - lastReportTick >= 150)
                        {
                            lastReportTick = now;
                            int pct = totalBytes > 0 ? (int)Math.Clamp(offset * 100L / totalBytes, 0, 99) : 0;
                            double dlMb = Math.Round(offset / (1024.0 * 1024.0), 1);
                            PublishProgress(new VoiceDownloadProgress(true, pct, stage, dlMb, totalMb));
                        }
                    }
                    await fs.FlushAsync(ct);
                }

                if (totalBytes > 0 && offset < totalBytes)
                    throw new IOException($"Обрыв потока на {offset} из {totalBytes} байт");

                return;
            }
            catch (OperationCanceledException oce) when (!ct.IsCancellationRequested)
            {
                if (attempt > MaxSequentialRetries)
                    throw new TimeoutException("Таймаут ответа сервера при скачивании", oce);

                double totalMb = totalBytes > 0 ? Math.Round(totalBytes / (1024.0 * 1024.0), 1) : 0;
                double dlMb = Math.Round(offset / (1024.0 * 1024.0), 1);
                int pct = totalBytes > 0 ? (int)Math.Clamp(offset * 100L / totalBytes, 0, 99) : 0;
                PublishProgress(new VoiceDownloadProgress(true, pct, $"{stage} (повтор {attempt}/{MaxSequentialRetries})", dlMb, totalMb));
                await Task.Delay(Math.Min(1000 * attempt, 6000), ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested && attempt <= MaxSequentialRetries && IsTransientNetworkError(ex, ct))
            {
                double totalMb = totalBytes > 0 ? Math.Round(totalBytes / (1024.0 * 1024.0), 1) : 0;
                double dlMb = Math.Round(offset / (1024.0 * 1024.0), 1);
                int pct = totalBytes > 0 ? (int)Math.Clamp(offset * 100L / totalBytes, 0, 99) : 0;
                PublishProgress(new VoiceDownloadProgress(true, pct, $"{stage} (повтор {attempt}/{MaxSequentialRetries})", dlMb, totalMb));
                await Task.Delay(Math.Min(1000 * attempt, 6000), ct);
            }
        }
    }

    private static bool IsTransientNetworkError(Exception ex, CancellationToken outerCt)
    {
        if (outerCt.IsCancellationRequested)
            return false;

        switch (ex)
        {
            case RangeNotHonoredException:
                return false;
            case IOException:
            case SocketException:
            case TimeoutException:
                return true;
            case OperationCanceledException:
                return true;
            case HttpRequestException httpEx:
                if (httpEx.StatusCode == null)
                    return true;
                int code = (int)httpEx.StatusCode.Value;
                return code == 408 || code == 429 || code >= 500;
            case AggregateException agg:
                return agg.InnerExceptions.Any(inner => IsTransientNetworkError(inner, outerCt));
            default:
                return ex.InnerException != null && IsTransientNetworkError(ex.InnerException, outerCt);
        }
    }

    private static string FormatDownloadError(Exception ex)
    {
        for (Exception? cur = ex; cur != null; cur = cur.InnerException)
        {
            if (cur is HttpRequestException httpEx && httpEx.StatusCode != null)
                return $"Ошибка HTTP {(int)httpEx.StatusCode.Value} при загрузке модели";
            if (cur is TimeoutException || (cur is OperationCanceledException && cur is not TaskCanceledException { InnerException: IOException or SocketException }))
                return "Таймаут ответа сервера (часть сохранена — повторите)";
            if (cur is IOException or SocketException)
                return "Обрыв сети (часть сохранена — повторите установку)";
            if (cur is InvalidDataException ide)
                return ide.Message.Length > 70 ? ide.Message[..67] + "..." : ide.Message;
        }

        string msg = (ex.Message ?? "неизвестная ошибка").Replace('\r', ' ').Replace('\n', ' ').Trim();
        return msg.Length > 75 ? msg[..72] + "..." : msg;
    }

    private static bool HasValidGgmlHeader(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> buf = stackalloc byte[4];
            if (fs.Read(buf) != 4) return false;
            return buf[0] == (byte)'l' && buf[1] == (byte)'m' && buf[2] == (byte)'g' && buf[3] == (byte)'g';
        }
        catch
        {
            return false;
        }
    }

    private static bool HasValidZipHeader(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> buf = stackalloc byte[2];
            if (fs.Read(buf) != 2) return false;
            return buf[0] == (byte)'P' && buf[1] == (byte)'K';
        }
        catch
        {
            return false;
        }
    }

    private static bool HasValidJsonHeader(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> buf = stackalloc byte[64];
            int n = fs.Read(buf);
            int i = 0;
            if (n >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF) i = 3;
            while (i < n && buf[i] <= 0x20) i++;
            return i < n && (buf[i] == (byte)'{' || buf[i] == (byte)'[');
        }
        catch
        {
            return false;
        }
    }

    private static bool HasValidGigaAmVocabHeader(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> buf = stackalloc byte[64];
            int n = fs.Read(buf);
            if (n <= 0) return false;
            string head = System.Text.Encoding.UTF8.GetString(buf[..n]).TrimStart();
            if (head.StartsWith("<!doctype", StringComparison.OrdinalIgnoreCase) ||
                head.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
                return false;
            return head.Contains("<unk> 0", StringComparison.Ordinal) || head.Contains(" 0", StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }

    private static bool HasNonHtmlHeader(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            Span<byte> buf = stackalloc byte[16];
            int n = fs.Read(buf);
            if (n <= 0) return false;
            int i = 0;
            while (i < n && buf[i] <= 0x20) i++;
            return i < n && buf[i] != (byte)'<';
        }
        catch
        {
            return false;
        }
    }

    private static bool HasMinFileSize(string path, long minBytes)
    {
        try
        {
            return File.Exists(path) && new FileInfo(path).Length >= minBytes;
        }
        catch
        {
            return false;
        }
    }

    private sealed class RangeNotHonoredException : Exception { }

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
