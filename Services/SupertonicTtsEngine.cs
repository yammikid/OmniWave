using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace DynamicIsland.Services;

public sealed record NeuralTtsVoicePreset(
    string Id,
    string DisplayName,
    string ShortName,
    string StyleFileName);

/// <summary>
/// Офлайн-движок нейросетевого синтеза речи Supertonic 3 (ONNX Runtime, женские голоса F1–F5, RU + EN, 44.1 кГц).
/// Включает нормализацию чисел/времени/процентов, авторазделение RU/EN сегментов, дисковый WAV-кэш и автовыгрузку ONNX при простое.
/// </summary>
public sealed class SupertonicTtsEngine : IDisposable
{
    private const int DiffusionSteps = 6;
    private const float SpeechSpeed = 1.06f;
    private const float PauseBetweenSegmentsSec = 0.065f;
    private const float PauseBetweenChunksSec = 0.14f;
    private const int MaxWavCacheEntries = 12;
    private const int MaxDiskCacheFiles = 160;
    private const long MaxDiskCacheBytes = 25L * 1024 * 1024;
    private const int IdleUnloadMs = 30_000;

    public static readonly IReadOnlyList<NeuralTtsVoicePreset> FemaleVoices = new NeuralTtsVoicePreset[]
    {
        new("supertonic:F1", "Омни F1 • Нейро RU+EN", "F1 Мягкий", "F1.json"),
        new("supertonic:F2", "Омни F2 • Нейро RU+EN", "F2 Яркий", "F2.json"),
        new("supertonic:F3", "Омни F3 • Нейро RU+EN", "F3 Спокойный", "F3.json"),
        new("supertonic:F4", "Омни F4 • Нейро RU+EN", "F4 Тёплый", "F4.json"),
        new("supertonic:F5", "Омни F5 • Нейро RU+EN", "F5 Живой", "F5.json"),
    };

    private static readonly Regex EmojiRangeRegex = new(
        @"[\u2600-\u27BF]|\uD83C[\uDF00-\uDFFF]|\uD83D[\uDC00-\uDFFF]|\uD83E[\uDD00-\uDDFF]",
        RegexOptions.Compiled);
    private static readonly Regex MultiSpaceRegex = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex TrailingSourceBadgeRegex = new(
        @"\s*[\(\[]\s*(?:yandex|я\.?\s*музыка|яндекс|vk|вк|spotify|youtube|yt|soundcloud|sc|радио)\s*[\)\]]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TimeRegex = new(@"\b([01]?\d|2[0-3]):([0-5]\d)\b", RegexOptions.Compiled);
    private static readonly Regex PercentRegex = new(@"(\d+)\s*%", RegexOptions.Compiled);
    private static readonly Regex MinutesRegex = new(@"\b(\d+)\s*(?:мин\.?|минут[аыу]?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex RuIntRegex = new(@"\b(\d{1,4})\b", RegexOptions.Compiled);
    private static readonly Regex TokenSplitRegex = new(@"(\s+|[.,!?;:—–\-""'«»()\[\]/&+@#%]+)", RegexOptions.Compiled);

    private static int _resolverInstalled;
    private static string _nativeRuntimeDir = "";

    private readonly VoiceModelManager _models;
    private readonly SemaphoreSlim _inferLock = new(1, 1);
    private readonly Dictionary<string, StyleData> _styleCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly object _cacheLock = new();
    private readonly Dictionary<string, LinkedListNode<(string Key, byte[] Wav)>> _wavCacheMap = new(StringComparer.Ordinal);
    private readonly LinkedList<(string Key, byte[] Wav)> _wavCacheOrder = new();

    private InferenceSession? _dpOrt;
    private InferenceSession? _textEncOrt;
    private InferenceSession? _vectorEstOrt;
    private InferenceSession? _vocoderOrt;
    private long[]? _unicodeIndexer;
    private int _sampleRate = 44100;
    private int _baseChunkSize = 512;
    private int _chunkCompressFactor = 6;
    private int _latentDim = 24;
    private bool _loaded;
    private bool _disposed;
    private int _activeOps;
    private long _lastUseTick;
    private readonly System.Threading.Timer _idleUnloadTimer;

    public bool IsLoaded => Volatile.Read(ref _loaded);

    private sealed record StyleData(float[] Ttl, int[] TtlShape, float[] Dp, int[] DpShape);

    public SupertonicTtsEngine(VoiceModelManager models)
    {
        _models = models;
        _idleUnloadTimer = new System.Threading.Timer(OnIdleUnloadTimer, null, Timeout.Infinite, Timeout.Infinite);
    }

    public static void EnsureNativeResolverRegistered(string onnxRuntimeDir)
    {
        if (string.IsNullOrWhiteSpace(onnxRuntimeDir))
            return;

        Volatile.Write(ref _nativeRuntimeDir, onnxRuntimeDir);
        if (Interlocked.CompareExchange(ref _resolverInstalled, 1, 0) == 0)
        {
            NativeLibrary.SetDllImportResolver(typeof(InferenceSession).Assembly, ResolveOnnxRuntimeDll);
        }
    }

    private static IntPtr ResolveOnnxRuntimeDll(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (libraryName.Equals("onnxruntime", StringComparison.OrdinalIgnoreCase) ||
            libraryName.Equals("onnxruntime.dll", StringComparison.OrdinalIgnoreCase))
        {
            string dir = Volatile.Read(ref _nativeRuntimeDir);
            if (!string.IsNullOrWhiteSpace(dir))
            {
                string sharedProviders = Path.Combine(dir, "onnxruntime_providers_shared.dll");
                if (File.Exists(sharedProviders))
                    NativeLibrary.TryLoad(sharedProviders, out _);

                string candidate = Path.Combine(dir, "onnxruntime.dll");
                if (File.Exists(candidate) && NativeLibrary.TryLoad(candidate, out var handle))
                    return handle;
            }
        }
        return IntPtr.Zero;
    }

    public async Task WarmUpAsync(string? preferredVoiceId = null, CancellationToken ct = default)
    {
        if (_disposed || !_models.IsNeuralTtsReady())
            return;

        await _inferLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed || !_models.IsNeuralTtsReady()) return;
            EnsureLoadedLocked();
            LoadVoiceStyleLocked(ResolveVoicePreset(preferredVoiceId));
        }
        catch { }
        finally
        {
            _inferLock.Release();
        }
    }

    public async Task<byte[]?> SynthesizeWavAsync(string text, string? preferredVoiceId = null, CancellationToken ct = default)
    {
        if (_disposed || string.IsNullOrWhiteSpace(text) || !_models.IsNeuralTtsReady())
            return null;

        var preset = ResolveVoicePreset(preferredVoiceId);
        string cleanedInput = CleanAnnouncementText(text);
        if (string.IsNullOrWhiteSpace(cleanedInput))
            return null;

        string cacheKey = $"{preset.Id}|{cleanedInput}";
        if (TryGetCachedWav(cacheKey, out var cached))
            return cached;

        var diskWav = await TryReadDiskCachedWavAsync(cacheKey, ct).ConfigureAwait(false);
        if (diskWav != null)
        {
            StoreCachedWav(cacheKey, diskWav);
            return diskWav;
        }

        var segments = SplitIntoLanguageSegments(cleanedInput);
        if (segments.Count == 0)
            return null;

        Interlocked.Increment(ref _activeOps);
        await _inferLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (_disposed || !_models.IsNeuralTtsReady())
                return null;

            Volatile.Write(ref _lastUseTick, Environment.TickCount64);
            EnsureLoadedLocked();
            var style = LoadVoiceStyleLocked(preset);
            int sampleRate = _sampleRate;

            var combinedSamples = new List<float>(sampleRate * 4);

            for (int segIdx = 0; segIdx < segments.Count; segIdx++)
            {
                ct.ThrowIfCancellationRequested();
                var (lang, rawSeg) = segments[segIdx];
                string prepared = lang == "ru" ? NormalizeRussianTextForSpeech(rawSeg) : NormalizeEnglishTextForSpeech(rawSeg);
                if (string.IsNullOrWhiteSpace(prepared))
                    continue;

                var chunks = ChunkText(prepared, 220);
                for (int cIdx = 0; cIdx < chunks.Count; cIdx++)
                {
                    ct.ThrowIfCancellationRequested();
                    var pcm = InferSingleSegmentLocked(chunks[cIdx], lang, style, ct);
                    if (pcm.Length == 0)
                        continue;

                    TrimSilenceAndFade(ref pcm, sampleRate);
                    if (pcm.Length == 0)
                        continue;

                    if (combinedSamples.Count > 0)
                    {
                        int gapSamples = (int)(sampleRate * (cIdx == 0 ? PauseBetweenSegmentsSec : PauseBetweenChunksSec));
                        for (int g = 0; g < gapSamples; g++)
                            combinedSamples.Add(0f);
                    }
                    combinedSamples.AddRange(pcm);
                }
            }

            if (combinedSamples.Count < sampleRate / 20)
                return null;

            byte[] wav = EncodeNormalizedPcm16Wav(combinedSamples, sampleRate);
            StoreCachedWav(cacheKey, wav);
            TrySaveDiskCachedWav(cacheKey, wav);
            return wav;
        }
        finally
        {
            Volatile.Write(ref _lastUseTick, Environment.TickCount64);
            _inferLock.Release();
            Interlocked.Decrement(ref _activeOps);
            ScheduleIdleUnload();
        }
    }

    public void Unload()
    {
        if (!_inferLock.Wait(250))
            return;
        try
        {
            UnloadLocked();
        }
        finally
        {
            _inferLock.Release();
        }
    }

    private void EnsureLoadedLocked()
    {
        if (_loaded) return;

        EnsureNativeResolverRegistered(_models.OnnxRuntimeDir);

        string onnxDir = _models.SupertonicOnnxDir;
        string cfgPath = Path.Combine(onnxDir, "tts.json");
        string indexerPath = Path.Combine(onnxDir, "unicode_indexer.json");

        using (var cfgDoc = JsonDocument.Parse(File.ReadAllText(cfgPath)))
        {
            var root = cfgDoc.RootElement;
            _sampleRate = root.GetProperty("ae").GetProperty("sample_rate").GetInt32();
            _baseChunkSize = root.GetProperty("ae").GetProperty("base_chunk_size").GetInt32();
            _chunkCompressFactor = root.GetProperty("ttl").GetProperty("chunk_compress_factor").GetInt32();
            _latentDim = root.GetProperty("ttl").GetProperty("latent_dim").GetInt32();
        }

        _unicodeIndexer = JsonSerializer.Deserialize<long[]>(File.ReadAllText(indexerPath))
            ?? throw new InvalidDataException("Не удалось прочитать unicode_indexer.json");

        using var opts = new SessionOptions
        {
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
            IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 2, 6),
            InterOpNumThreads = 1,
            EnableMemoryPattern = false,
            EnableCpuMemArena = false
        };
        TryAddSessionConfig(opts, "session.intra_op.allow_spinning", "0");
        TryAddSessionConfig(opts, "session.inter_op.allow_spinning", "0");
        TryAddSessionConfig(opts, "session.set_denormal_as_zero", "1");

        try
        {
            _dpOrt = new InferenceSession(Path.Combine(onnxDir, "duration_predictor.onnx"), opts);
            _textEncOrt = new InferenceSession(Path.Combine(onnxDir, "text_encoder.onnx"), opts);
            _vectorEstOrt = new InferenceSession(Path.Combine(onnxDir, "vector_estimator.onnx"), opts);
            _vocoderOrt = new InferenceSession(Path.Combine(onnxDir, "vocoder.onnx"), opts);
            _loaded = true;
        }
        catch
        {
            UnloadLocked();
            throw;
        }
    }

    private StyleData LoadVoiceStyleLocked(NeuralTtsVoicePreset preset)
    {
        if (_styleCache.TryGetValue(preset.Id, out var cached))
            return cached;

        string path = Path.Combine(_models.SupertonicVoicesDir, preset.StyleFileName);
        if (!File.Exists(path))
            path = Path.Combine(_models.SupertonicVoicesDir, FemaleVoices[0].StyleFileName);

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var root = doc.RootElement;

        var (ttlData, ttlDims) = ParseStyleTensor(root.GetProperty("style_ttl"));
        var (dpData, dpDims) = ParseStyleTensor(root.GetProperty("style_dp"));

        var loaded = new StyleData(ttlData, ttlDims, dpData, dpDims);
        _styleCache[preset.Id] = loaded;
        return loaded;
    }

    private static (float[] Data, int[] Dims) ParseStyleTensor(JsonElement node)
    {
        var rawDims = node.GetProperty("dims");
        int d0 = rawDims[0].GetInt32();
        int d1 = rawDims[1].GetInt32();
        int d2 = rawDims[2].GetInt32();
        int total = checked(d0 * d1 * d2);
        var flat = new float[total];
        int idx = 0;

        foreach (var b in node.GetProperty("data").EnumerateArray())
        {
            foreach (var r in b.EnumerateArray())
            {
                foreach (var val in r.EnumerateArray())
                {
                    if (idx < total)
                        flat[idx++] = val.GetSingle();
                }
            }
        }
        return (flat, new[] { d0, d1, d2 });
    }

    private float[] InferSingleSegmentLocked(string text, string lang, StyleData style, CancellationToken ct)
    {
        var indexer = _unicodeIndexer!;
        string formatted = PreprocessSegmentForOnnx(text, lang);
        var mapped = new List<long>(formatted.Length);

        for (int i = 0; i < formatted.Length; i++)
        {
            int code = formatted[i];
            if ((uint)code < (uint)indexer.Length)
            {
                long tok = indexer[code];
                if (tok >= 0)
                    mapped.Add(tok);
            }
        }

        if (mapped.Count == 0)
            return Array.Empty<float>();

        int seqLen = mapped.Count;
        var textIdsFlat = mapped.ToArray();
        var textMaskFlat = new float[seqLen];
        Array.Fill(textMaskFlat, 1.0f);

        var textIdsTensor = new DenseTensor<long>(textIdsFlat, new[] { 1, seqLen });
        var textMaskTensor = new DenseTensor<float>(textMaskFlat, new[] { 1, 1, seqLen });
        var styleDpTensor = new DenseTensor<float>(style.Dp, style.DpShape);
        var styleTtlTensor = new DenseTensor<float>(style.Ttl, style.TtlShape);

        float durSec;
        using (var dpOut = _dpOrt!.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("text_ids", textIdsTensor),
            NamedOnnxValue.CreateFromTensor("style_dp", styleDpTensor),
            NamedOnnxValue.CreateFromTensor("text_mask", textMaskTensor)
        }))
        {
            durSec = dpOut.First(o => o.Name == "duration").AsTensor<float>().GetValue(0);
        }

        durSec = Math.Max(0.10f, durSec / SpeechSpeed);

        float[] textEmbFlat;
        int[] textEmbDims;
        using (var encOut = _textEncOrt!.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("text_ids", textIdsTensor),
            NamedOnnxValue.CreateFromTensor("style_ttl", styleTtlTensor),
            NamedOnnxValue.CreateFromTensor("text_mask", textMaskTensor)
        }))
        {
            var t = encOut.First(o => o.Name == "text_emb").AsTensor<float>();
            textEmbDims = t.Dimensions.ToArray();
            textEmbFlat = t.ToArray();
        }

        var textEmbTensor = new DenseTensor<float>(textEmbFlat, textEmbDims);

        int chunkSize = _baseChunkSize * _chunkCompressFactor;
        long wavSamples = (long)(durSec * _sampleRate);
        int latentLen = Math.Max(1, (int)((wavSamples + chunkSize - 1) / chunkSize));
        int latentDim = _latentDim * _chunkCompressFactor;
        int latentTotal = latentDim * latentLen;

        var xtFlat = new float[latentTotal];
        var rng = Random.Shared;
        for (int i = 0; i < latentTotal; i++)
        {
            double u1 = Math.Clamp(1.0 - rng.NextDouble(), 1e-12, 1.0);
            double u2 = 1.0 - rng.NextDouble();
            xtFlat[i] = (float)(Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2));
        }

        var latentMaskFlat = new float[latentLen];
        Array.Fill(latentMaskFlat, 1.0f);

        int[] latentShape = { 1, latentDim, latentLen };
        int[] latentMaskShape = { 1, 1, latentLen };
        var latentMaskTensor = new DenseTensor<float>(latentMaskFlat, latentMaskShape);
        var totalStepTensor = new DenseTensor<float>(new[] { (float)DiffusionSteps }, new[] { 1 });

        for (int step = 0; step < DiffusionSteps; step++)
        {
            ct.ThrowIfCancellationRequested();

            var noisyTensor = new DenseTensor<float>(xtFlat, latentShape);
            var curStepTensor = new DenseTensor<float>(new[] { (float)step }, new[] { 1 });

            using var estOut = _vectorEstOrt!.Run(new[]
            {
                NamedOnnxValue.CreateFromTensor("noisy_latent", noisyTensor),
                NamedOnnxValue.CreateFromTensor("text_emb", textEmbTensor),
                NamedOnnxValue.CreateFromTensor("style_ttl", styleTtlTensor),
                NamedOnnxValue.CreateFromTensor("text_mask", textMaskTensor),
                NamedOnnxValue.CreateFromTensor("latent_mask", latentMaskTensor),
                NamedOnnxValue.CreateFromTensor("total_step", totalStepTensor),
                NamedOnnxValue.CreateFromTensor("current_step", curStepTensor)
            });

            var denoised = estOut.First(o => o.Name == "denoised_latent").AsTensor<float>();
            if (denoised is DenseTensor<float> dense)
            {
                dense.Buffer.Span[..latentTotal].CopyTo(xtFlat);
            }
            else
            {
                var tmp = denoised.ToArray();
                Array.Copy(tmp, xtFlat, Math.Min(tmp.Length, latentTotal));
            }
        }

        using var vocOut = _vocoderOrt!.Run(new[]
        {
            NamedOnnxValue.CreateFromTensor("latent", new DenseTensor<float>(xtFlat, latentShape))
        });

        var wavTensor = vocOut.First(o => o.Name == "wav_tts").AsTensor<float>();
        var fullWav = wavTensor.ToArray();
        int targetLen = Math.Min(fullWav.Length, Math.Max(0, (int)wavSamples));
        if (targetLen <= 0)
            return Array.Empty<float>();

        var trimmed = new float[targetLen];
        Array.Copy(fullWav, trimmed, targetLen);
        return trimmed;
    }

    private void UnloadLocked()
    {
        _loaded = false;
        _unicodeIndexer = null;
        _styleCache.Clear();
        try { _dpOrt?.Dispose(); } catch { }
        try { _textEncOrt?.Dispose(); } catch { }
        try { _vectorEstOrt?.Dispose(); } catch { }
        try { _vocoderOrt?.Dispose(); } catch { }
        _dpOrt = null;
        _textEncOrt = null;
        _vectorEstOrt = null;
        _vocoderOrt = null;
    }

    private static NeuralTtsVoicePreset ResolveVoicePreset(string? voiceId)
    {
        if (!string.IsNullOrWhiteSpace(voiceId))
        {
            var match = FemaleVoices.FirstOrDefault(v =>
                string.Equals(v.Id, voiceId, StringComparison.OrdinalIgnoreCase) ||
                v.Id.EndsWith(":" + voiceId.Trim(), StringComparison.OrdinalIgnoreCase));
            if (match != null) return match;
        }
        return FemaleVoices[0];
    }

    private static string CleanAnnouncementText(string text)
    {
        string s = text.Trim();
        s = TrailingSourceBadgeRegex.Replace(s, "");
        s = s.Replace("•", ", ", StringComparison.Ordinal)
             .Replace("—", ", ", StringComparison.Ordinal)
             .Replace("–", ", ", StringComparison.Ordinal);
        return MultiSpaceRegex.Replace(s, " ").Trim();
    }

    private static List<(string Lang, string Text)> SplitIntoLanguageSegments(string text)
    {
        bool hasCyr = ContainsCyrillic(text);
        bool hasLat = ContainsLatin(text);
        var result = new List<(string Lang, string Text)>();

        if (hasCyr && !hasLat)
        {
            result.Add(("ru", text));
            return result;
        }
        if (hasLat && !hasCyr)
        {
            result.Add(("en", text));
            return result;
        }
        if (!hasCyr && !hasLat)
        {
            result.Add(("ru", text));
            return result;
        }

        // Mixed RU + EN text: group adjacent runs cleanly while keeping digits/punctuation attached to their context.
        var tokens = TokenSplitRegex.Split(text);
        string? curLang = null;
        var sb = new StringBuilder();

        foreach (var tok in tokens)
        {
            if (string.IsNullOrEmpty(tok)) continue;

            bool tokCyr = ContainsCyrillic(tok);
            bool tokLat = ContainsLatin(tok);

            // Short music acronyms in Cyrillic context stay Russian ("VK" -> "Вэ Ка")
            if (tokLat && !tokCyr && IsRussianContextAcronym(tok) && (curLang == "ru" || curLang == null))
                tokLat = false;

            string? tokLang = tokCyr ? "ru" : (tokLat ? "en" : null);

            if (tokLang == null)
            {
                sb.Append(tok);
                continue;
            }

            if (curLang == null)
            {
                curLang = tokLang;
                sb.Append(tok);
            }
            else if (curLang == tokLang)
            {
                sb.Append(tok);
            }
            else
            {
                string flushed = sb.ToString().Trim();
                if (!string.IsNullOrWhiteSpace(flushed))
                    result.Add((curLang, flushed));
                sb.Clear();
                curLang = tokLang;
                sb.Append(tok);
            }
        }

        string tail = sb.ToString().Trim();
        if (!string.IsNullOrWhiteSpace(tail))
            result.Add((curLang ?? "ru", tail));

        return result;
    }

    private static bool IsRussianContextAcronym(string tok)
        => tok.Equals("VK", StringComparison.OrdinalIgnoreCase) ||
           tok.Equals("DJ", StringComparison.OrdinalIgnoreCase) ||
           tok.Equals("FM", StringComparison.OrdinalIgnoreCase) ||
           tok.Equals("VIP", StringComparison.OrdinalIgnoreCase);

    private static bool ContainsCyrillic(string s)
    {
        foreach (char c in s)
        {
            if ((c >= '\u0400' && c <= '\u04FF') || (c >= '\u0500' && c <= '\u052F'))
                return true;
        }
        return false;
    }

    private static bool ContainsLatin(string s)
    {
        foreach (char c in s)
        {
            if ((c >= 'A' && c <= 'Z') || (c >= 'a' && c <= 'z'))
                return true;
        }
        return false;
    }

    private static string NormalizeRussianTextForSpeech(string text)
    {
        string s = text;
        s = Regex.Replace(s, @"\bVK\b", "Вэ Ка", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\bDJ\b", "диджей", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\bFM\b", "эф эм", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\bVIP\b", "вип", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\bAlt\s*\+\s*V\b", "Альт Вэ", RegexOptions.IgnoreCase);

        s = TimeRegex.Replace(s, m =>
        {
            int hh = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            int mm = int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
            string hWord = PluralizeRu(hh, "час", "часа", "часов");
            string mWord = PluralizeRu(mm, "минута", "минуты", "минут");
            return $"{NumberToRussianWords(hh, feminine: false)} {hWord} {NumberToRussianWords(mm, feminine: true)} {mWord}";
        });

        s = PercentRegex.Replace(s, m =>
        {
            int n = int.TryParse(m.Groups[1].Value, out int v) ? v : 0;
            if (n > 9999) return m.Value;
            return $"{NumberToRussianWords(n, feminine: false)} {PluralizeRu(n, "процент", "процента", "процентов")}";
        });

        s = MinutesRegex.Replace(s, m =>
        {
            int n = int.TryParse(m.Groups[1].Value, out int v) ? v : 0;
            if (n > 9999) return m.Value;
            return $"{NumberToRussianWords(n, feminine: true)} {PluralizeRu(n, "минута", "минуты", "минут")}";
        });

        s = RuIntRegex.Replace(s, m =>
        {
            int n = int.TryParse(m.Groups[1].Value, out int v) ? v : 0;
            return n <= 9999 ? NumberToRussianWords(n, feminine: false) : m.Value;
        });

        return MultiSpaceRegex.Replace(s, " ").Trim();
    }

    private static string NormalizeEnglishTextForSpeech(string text)
    {
        string s = text.Replace("&", " and ", StringComparison.Ordinal)
                       .Replace("+", " plus ", StringComparison.Ordinal);
        return MultiSpaceRegex.Replace(s, " ").Trim();
    }

    private static string PreprocessSegmentForOnnx(string text, string lang)
    {
        string s = text.Normalize(NormalizationForm.FormKD);
        s = EmojiRangeRegex.Replace(s, "");
        s = s.Replace("–", "-")
             .Replace("‑", "-")
             .Replace("—", "-")
             .Replace("_", " ")
             .Replace("\u201C", "\"")
             .Replace("\u201D", "\"")
             .Replace("\u2018", "'")
             .Replace("\u2019", "'")
             .Replace("´", "'")
             .Replace("`", "'")
             .Replace("«", "\"")
             .Replace("»", "\"")
             .Replace("[", " ")
             .Replace("]", " ")
             .Replace("|", " ")
             .Replace("/", " ")
             .Replace("#", " ")
             .Replace("→", " ")
             .Replace("←", " ")
             .Replace("@", " ");

        s = Regex.Replace(s, @"[♥☆♡©\\]", "");
        s = Regex.Replace(s, @"\s+([,.!?;:])", "$1");
        s = MultiSpaceRegex.Replace(s, " ").Trim();
        if (string.IsNullOrWhiteSpace(s))
            return "";

        if (!Regex.IsMatch(s, @"[.!?;:,'\u0022\u201C\u201D\u2018\u2019)\]}…]$"))
            s += ".";

        return $"<{lang}>{s}</{lang}>";
    }

    private static List<string> ChunkText(string text, int maxLen)
    {
        var chunks = new List<string>();
        var sentences = Regex.Split(text.Trim(), @"(?<=[.!?])\s+");
        string cur = "";

        foreach (var s in sentences)
        {
            if (string.IsNullOrWhiteSpace(s)) continue;
            if (cur.Length + s.Length + 1 <= maxLen)
            {
                cur = cur.Length == 0 ? s : cur + " " + s;
            }
            else
            {
                if (cur.Length > 0) chunks.Add(cur.Trim());
                cur = s;
            }
        }
        if (cur.Length > 0) chunks.Add(cur.Trim());
        if (chunks.Count == 0 && !string.IsNullOrWhiteSpace(text)) chunks.Add(text.Trim());
        return chunks;
    }

    private static void TrimSilenceAndFade(ref float[] samples, int sampleRate)
    {
        if (samples.Length == 0) return;
        const float threshold = 0.008f;
        int first = 0;
        while (first < samples.Length && MathF.Abs(samples[first]) < threshold) first++;
        int last = samples.Length - 1;
        while (last > first && MathF.Abs(samples[last]) < threshold) last--;

        if (last <= first)
        {
            samples = Array.Empty<float>();
            return;
        }

        int pad = Math.Max(64, (int)(sampleRate * 0.020f));
        int start = Math.Max(0, first - pad);
        int end = Math.Min(samples.Length - 1, last + pad);
        int len = end - start + 1;

        var result = new float[len];
        Array.Copy(samples, start, result, 0, len);

        int fadeLen = Math.Min(len / 4, Math.Max(32, (int)(sampleRate * 0.015f)));
        for (int i = 0; i < fadeLen; i++)
        {
            float w = 0.5f * (1f - MathF.Cos(MathF.PI * i / fadeLen));
            result[i] *= w;
            result[len - 1 - i] *= w;
        }

        samples = result;
    }

    private static byte[] EncodeNormalizedPcm16Wav(List<float> samples, int sampleRate)
    {
        float peak = 0f;
        for (int i = 0; i < samples.Count; i++)
        {
            float v = samples[i];
            if (float.IsNaN(v) || float.IsInfinity(v))
            {
                samples[i] = 0f;
                continue;
            }
            float a = MathF.Abs(v);
            if (a > peak) peak = a;
        }

        float gain = peak > 0.01f ? Math.Min(1.5f, 0.85f / peak) : 1.0f;
        int dataBytes = samples.Count * 2;
        using var ms = new MemoryStream(44 + dataBytes);
        using var bw = new BinaryWriter(ms, Encoding.ASCII);

        bw.Write("RIFF"u8);
        bw.Write(36 + dataBytes);
        bw.Write("WAVE"u8);
        bw.Write("fmt "u8);
        bw.Write(16);
        bw.Write((short)1);
        bw.Write((short)1);
        bw.Write(sampleRate);
        bw.Write(sampleRate * 2);
        bw.Write((short)2);
        bw.Write((short)16);
        bw.Write("data"u8);
        bw.Write(dataBytes);

        for (int i = 0; i < samples.Count; i++)
        {
            float x = samples[i] * gain;
            if (MathF.Abs(x) > 0.95f)
                x = MathF.Tanh(x);
            bw.Write((short)Math.Clamp((int)MathF.Round(x * 32767f), -32768, 32767));
        }

        bw.Flush();
        return ms.ToArray();
    }

    private bool TryGetCachedWav(string key, out byte[] wav)
    {
        lock (_cacheLock)
        {
            if (_wavCacheMap.TryGetValue(key, out var node))
            {
                _wavCacheOrder.Remove(node);
                _wavCacheOrder.AddFirst(node);
                wav = node.Value.Wav;
                return true;
            }
        }
        wav = Array.Empty<byte>();
        return false;
    }

    private void StoreCachedWav(string key, byte[] wav)
    {
        lock (_cacheLock)
        {
            if (_wavCacheMap.TryGetValue(key, out var existing))
            {
                _wavCacheOrder.Remove(existing);
                _wavCacheMap.Remove(key);
            }

            var node = _wavCacheOrder.AddFirst((key, wav));
            _wavCacheMap[key] = node;

            while (_wavCacheOrder.Count > MaxWavCacheEntries && _wavCacheOrder.Last is { } last)
            {
                _wavCacheMap.Remove(last.Value.Key);
                _wavCacheOrder.RemoveLast();
            }
        }
    }

    private static string PluralizeRu(int n, string one, string few, string many)
    {
        int m100 = Math.Abs(n) % 100;
        int m10 = m100 % 10;
        if (m100 is >= 11 and <= 19) return many;
        if (m10 == 1) return one;
        if (m10 is >= 2 and <= 4) return few;
        return many;
    }

    private static string NumberToRussianWords(int n, bool feminine)
    {
        if (n == 0) return "ноль";

        string[] onesMasc = { "", "один", "два", "три", "четыре", "пять", "шесть", "семь", "восемь", "девять", "десять", "одиннадцать", "двенадцать", "тринадцать", "четырнадцать", "пятнадцать", "шестнадцать", "семнадцать", "восемнадцать", "девятнадцать" };
        string[] onesFem = { "", "одна", "две", "три", "четыре", "пять", "шесть", "семь", "восемь", "девять", "десять", "одиннадцать", "двенадцать", "тринадцать", "четырнадцать", "пятнадцать", "шестнадцать", "семнадцать", "восемнадцать", "девятнадцать" };
        string[] tens = { "", "", "двадцать", "тридцать", "сорок", "пятьдесят", "шестьдесят", "семьдесят", "восемьдесят", "девяносто" };
        string[] hundreds = { "", "сто", "двести", "триста", "четыреста", "пятьсот", "шестьсот", "семьсот", "восемьсот", "девятьсот" };

        var parts = new List<string>(5);
        int rem = n;

        if (rem >= 1000)
        {
            int th = rem / 1000;
            rem %= 1000;
            if (th < 20)
                parts.Add(onesFem[th]);
            else
                parts.Add(NumberToRussianWords(th, feminine: true));
            parts.Add(PluralizeRu(th, "тысяча", "тысячи", "тысяч"));
        }

        if (rem >= 100)
        {
            parts.Add(hundreds[rem / 100]);
            rem %= 100;
        }

        if (rem >= 20)
        {
            parts.Add(tens[rem / 10]);
            rem %= 10;
        }

        if (rem > 0)
        {
            parts.Add((feminine ? onesFem : onesMasc)[rem]);
        }

        return string.Join(" ", parts);
    }

    private string GetDiskCacheDir() => Path.Combine(_models.RootDir, "tts-cache");

    private string GetDiskCachePath(string cacheKey)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes("v2|" + cacheKey));
        string hex = Convert.ToHexString(hash.AsSpan(0, 12)).ToLowerInvariant();
        return Path.Combine(GetDiskCacheDir(), hex + ".wav");
    }

    private async Task<byte[]?> TryReadDiskCachedWavAsync(string cacheKey, CancellationToken ct)
    {
        try
        {
            string path = GetDiskCachePath(cacheKey);
            if (!File.Exists(path))
                return null;

            byte[] bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);
            if (bytes.Length < 44 || bytes[0] != (byte)'R' || bytes[1] != (byte)'I' || bytes[2] != (byte)'F' || bytes[3] != (byte)'F')
                return null;

            try { File.SetLastWriteTimeUtc(path, DateTime.UtcNow); } catch { }
            return bytes;
        }
        catch
        {
            return null;
        }
    }

    private void TrySaveDiskCachedWav(string cacheKey, byte[] wav)
    {
        if (wav.Length < 44 || wav.Length > 2 * 1024 * 1024)
            return;

        _ = Task.Run(() =>
        {
            try
            {
                string dir = GetDiskCacheDir();
                Directory.CreateDirectory(dir);
                string dest = GetDiskCachePath(cacheKey);
                string tmp = dest + ".tmp";
                File.WriteAllBytes(tmp, wav);
                File.Move(tmp, dest, overwrite: true);
                PruneDiskCache(dir);
            }
            catch { }
        });
    }

    private static void PruneDiskCache(string dir)
    {
        try
        {
            var files = new DirectoryInfo(dir)
                .EnumerateFiles("*.wav", SearchOption.TopDirectoryOnly)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();

            long totalBytes = files.Sum(f => f.Length);
            for (int i = files.Count - 1; i >= 0 && (files.Count > MaxDiskCacheFiles || totalBytes > MaxDiskCacheBytes); i--)
            {
                long sz = files[i].Length;
                try
                {
                    files[i].Delete();
                    totalBytes -= sz;
                    files.RemoveAt(i);
                }
                catch { }
            }
        }
        catch { }
    }

    private void ScheduleIdleUnload()
    {
        if (_disposed) return;
        try { _idleUnloadTimer.Change(IdleUnloadMs, Timeout.Infinite); } catch { }
    }

    private void OnIdleUnloadTimer(object? _)
    {
        if (_disposed || !Volatile.Read(ref _loaded))
            return;
        if (Volatile.Read(ref _activeOps) > 0)
            return;
        if (Environment.TickCount64 - Volatile.Read(ref _lastUseTick) < IdleUnloadMs - 500)
            return;

        if (!_inferLock.Wait(0))
            return;

        bool unloaded = false;
        try
        {
            if (!_disposed && _loaded && Volatile.Read(ref _activeOps) == 0 &&
                Environment.TickCount64 - Volatile.Read(ref _lastUseTick) >= IdleUnloadMs - 500)
            {
                UnloadLocked();
                unloaded = true;
            }
        }
        finally
        {
            _inferLock.Release();
        }

        if (unloaded)
        {
            try { GC.Collect(2, GCCollectionMode.Aggressive, blocking: false, compacting: true); } catch { }
            WindowBlurHelper.TrimWorkingSet();
        }
    }

    private static void TryAddSessionConfig(SessionOptions opts, string key, string value)
    {
        try { opts.AddSessionConfigEntry(key, value); } catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _idleUnloadTimer.Dispose(); } catch { }
        try { Unload(); } catch { }
        _inferLock.Dispose();
    }
}
