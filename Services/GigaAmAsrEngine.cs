using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace DynamicIsland.Services;

/// <summary>
/// SOTA офлайн-распознавание русской речи на базе Sber GigaAM v3 E2E CTC INT8 (ONNX Runtime, MIT).
/// Точный STFT 64-mel HTK препроцессор (win=320, hop=160, 0..8 кГц), SIMD Vector&lt;float&gt; и автовыгрузка при простое.
/// </summary>
public sealed class GigaAmAsrEngine : IDisposable
{
    private const int SampleRate = 16000;
    private const int NFft = 320;
    private const int WinLength = 320;
    private const int HopLength = 160;
    private const int NFreqs = NFft / 2 + 1; // 161
    private const int NMels = 64;
    private const int EdgePadSamples = 1280; // 80 мс тишины по краям для защиты согласных при center=false
    private const float ClampMin = 1e-9f;
    private const float ClampMax = 1e9f;
    private const int SubsamplingFactor = 4;
    private const int IdleUnloadMs = 45_000;

    private static readonly Regex MultiSpaceRegex = new(@"\s+", RegexOptions.Compiled);

    private static readonly float[] CosTable;
    private static readonly float[] SinTable;
    private static readonly float[] MelFb;
    private static readonly int[] MelStartBin;
    private static readonly int[] MelEndBin;

    static GigaAmAsrEngine()
    {
        // Периодическое окно Ханна: np.hanning(321)[:-1]
        var hann = new float[WinLength];
        for (int n = 0; n < WinLength; n++)
            hann[n] = 0.5f * (1f - MathF.Cos(2f * MathF.PI * n / WinLength));

        CosTable = new float[NFreqs * WinLength];
        SinTable = new float[NFreqs * WinLength];
        for (int k = 0; k < NFreqs; k++)
        {
            int rowBase = k * WinLength;
            float angleStep = 2f * MathF.PI * k / NFft;
            for (int n = 0; n < WinLength; n++)
            {
                float w = hann[n];
                CosTable[rowBase + n] = w * MathF.Cos(angleStep * n);
                SinTable[rowBase + n] = -w * MathF.Sin(angleStep * n);
            }
        }

        // HTK Mel Filterbank (exact match с istupakov/onnx-asr preprocessors/fbanks.py: f_min=0, f_max=8000, norm=None)
        static double HzToMel(double hz) => 2595.0 * Math.Log10(1.0 + hz / 700.0);
        static double MelToHz(double mel) => 700.0 * (Math.Pow(10.0, mel / 2595.0) - 1.0);

        double mMin = HzToMel(0.0);
        double mMax = HzToMel(8000.0);
        var fPts = new double[NMels + 2];
        for (int i = 0; i < NMels + 2; i++)
        {
            double m = mMin + (mMax - mMin) * i / (NMels + 1);
            fPts[i] = MelToHz(m);
        }

        MelFb = new float[NMels * NFreqs];
        MelStartBin = new int[NMels];
        MelEndBin = new int[NMels];

        for (int m = 0; m < NMels; m++)
        {
            double left = fPts[m];
            double center = fPts[m + 1];
            double right = fPts[m + 2];
            int first = -1;
            int last = -1;

            for (int k = 0; k < NFreqs; k++)
            {
                double freq = 8000.0 * k / (NFreqs - 1);
                double up = (freq - left) / (center - left);
                double down = (right - freq) / (right - center);
                float val = (float)Math.Max(0.0, Math.Min(up, down));
                MelFb[m * NFreqs + k] = val;
                if (val > 0f)
                {
                    if (first < 0) first = k;
                    last = k;
                }
            }

            MelStartBin[m] = first >= 0 ? first : 0;
            MelEndBin[m] = last >= 0 ? last : -1;
        }
    }

    private readonly VoiceModelManager _models;
    private readonly SemaphoreSlim _inferLock = new(1, 1);
    private readonly System.Threading.Timer _idleUnloadTimer;

    private InferenceSession? _session;
    private string[]? _vocab;
    private int _blankId = 256;
    private int _unkId;
    private int _activeOps;
    private long _lastUseTick;
    private bool _loaded;
    private bool _disposed;

    public bool IsLoaded => Volatile.Read(ref _loaded);

    public GigaAmAsrEngine(VoiceModelManager models)
    {
        _models = models;
        _idleUnloadTimer = new System.Threading.Timer(OnIdleUnloadTimer, null, Timeout.Infinite, Timeout.Infinite);
    }

    public async Task<string> TranscribePcm16kAsync(byte[] pcmBytes, CancellationToken ct = default)
    {
        if (_disposed || pcmBytes == null || pcmBytes.Length < 1600 || !_models.IsGigaAmReady())
            return "";

        Interlocked.Increment(ref _activeOps);
        try
        {
            Volatile.Write(ref _lastUseTick, Environment.TickCount64);
            await _inferLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_disposed || ct.IsCancellationRequested)
                    return "";

                if (!EnsureLoadedLocked())
                    return "";

                float[] denoised = VoiceAudioDsp.PreprocessPcm16kToFloats(pcmBytes);
                if (denoised.Length < 160)
                    return "";

                var padded = new float[denoised.Length + EdgePadSamples * 2];
                Array.Copy(denoised, 0, padded, EdgePadSamples, denoised.Length);

                var (features, frameCount) = ExtractFeatures(padded);
                if (frameCount <= 0)
                    return "";

                var lengths = new DenseTensor<long>(new long[] { frameCount }, new[] { 1 });
                var inputs = new List<NamedOnnxValue>(2)
                {
                    NamedOnnxValue.CreateFromTensor("features", features),
                    NamedOnnxValue.CreateFromTensor("feature_lengths", lengths)
                };

                using var outputs = _session!.Run(inputs);
                var outVal = outputs.FirstOrDefault(o => string.Equals(o.Name, "log_probs", StringComparison.Ordinal))
                             ?? outputs.FirstOrDefault();
                if (outVal == null)
                    return "";

                var logProbs = outVal.AsTensor<float>();
                return DecodeCtcGreedy(logProbs, frameCount);
            }
            finally
            {
                _inferLock.Release();
            }
        }
        catch (OperationCanceledException)
        {
            return "";
        }
        catch
        {
            return "";
        }
        finally
        {
            Volatile.Write(ref _lastUseTick, Environment.TickCount64);
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
        try
        {
            GC.Collect(2, GCCollectionMode.Aggressive, true, true);
            WindowBlurHelper.TrimWorkingSet();
        }
        catch { }
    }

    private bool EnsureLoadedLocked()
    {
        if (_loaded && _session != null && _vocab != null)
            return true;

        if (!_models.IsGigaAmReady())
            return false;

        InferenceSession? created = null;
        try
        {
            var vocab = LoadVocab(_models.GigaAmVocabPath, out int blankId, out int unkId);
            if (vocab.Length == 0)
                return false;

            SupertonicTtsEngine.EnsureNativeResolverRegistered(_models.OnnxRuntimeDir);

            using var opts = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 2, 6),
                InterOpNumThreads = 1,
                EnableCpuMemArena = false,
                EnableMemoryPattern = false
            };
            try { opts.AddSessionConfigEntry("session.intra_op.allow_spinning", "0"); } catch { }
            try { opts.AddSessionConfigEntry("session.inter_op.allow_spinning", "0"); } catch { }
            try { opts.AddSessionConfigEntry("session.set_denormal_as_zero", "1"); } catch { }

            created = new InferenceSession(_models.GigaAmModelPath, opts);
            _vocab = vocab;
            _blankId = blankId;
            _unkId = unkId;
            _session = created;
            Volatile.Write(ref _loaded, true);
            return true;
        }
        catch
        {
            try { created?.Dispose(); } catch { }
            UnloadLocked();
            return false;
        }
    }

    private void UnloadLocked()
    {
        Volatile.Write(ref _loaded, false);
        try { _session?.Dispose(); } catch { }
        _session = null;
        _vocab = null;
    }

    private static (DenseTensor<float> Features, int FrameCount) ExtractFeatures(float[] waveform)
    {
        int frameCount = (waveform.Length - WinLength) / HopLength + 1;
        if (frameCount <= 0)
            return (new DenseTensor<float>(new[] { 1, NMels, 0 }), 0);

        var data = new float[NMels * frameCount];
        var powerSpec = new float[NFreqs];
        int vecLen = Vector<float>.Count;

        for (int t = 0; t < frameCount; t++)
        {
            int frameOffset = t * HopLength;
            ReadOnlySpan<float> frame = waveform.AsSpan(frameOffset, WinLength);

            for (int k = 0; k < NFreqs; k++)
            {
                int rowBase = k * WinLength;
                ReadOnlySpan<float> cosRow = CosTable.AsSpan(rowBase, WinLength);
                ReadOnlySpan<float> sinRow = SinTable.AsSpan(rowBase, WinLength);

                float re = 0f;
                float im = 0f;
                int n = 0;

                if (Vector.IsHardwareAccelerated && WinLength >= vecLen)
                {
                    var vRe = Vector<float>.Zero;
                    var vIm = Vector<float>.Zero;
                    for (; n <= WinLength - vecLen; n += vecLen)
                    {
                        var vx = new Vector<float>(frame.Slice(n, vecLen));
                        vRe += vx * new Vector<float>(cosRow.Slice(n, vecLen));
                        vIm += vx * new Vector<float>(sinRow.Slice(n, vecLen));
                    }
                    re = Vector.Sum(vRe);
                    im = Vector.Sum(vIm);
                }

                for (; n < WinLength; n++)
                {
                    float x = frame[n];
                    re += x * cosRow[n];
                    im += x * sinRow[n];
                }

                powerSpec[k] = re * re + im * im;
            }

            for (int m = 0; m < NMels; m++)
            {
                int kStart = MelStartBin[m];
                int kEnd = MelEndBin[m];
                int fbBase = m * NFreqs;
                float melEnergy = 0f;

                for (int k = kStart; k <= kEnd; k++)
                    melEnergy += powerSpec[k] * MelFb[fbBase + k];

                float clamped = Math.Clamp(melEnergy, ClampMin, ClampMax);
                data[m * frameCount + t] = MathF.Log(clamped);
            }
        }

        return (new DenseTensor<float>(data, new[] { 1, NMels, frameCount }), frameCount);
    }

    private string DecodeCtcGreedy(Tensor<float> logProbs, int featureFrames)
    {
        if (_vocab == null || logProbs.Dimensions.Length < 3)
            return "";

        int tOut = logProbs.Dimensions[1];
        int vocabDim = logProbs.Dimensions[2];
        int validSteps = Math.Min(tOut, (featureFrames - 1) / SubsamplingFactor + 1);
        int vocabLen = Math.Min(vocabDim, _vocab.Length);

        var sb = new StringBuilder(128);
        int prevId = -1;

        if (logProbs is DenseTensor<float> dense)
        {
            ReadOnlySpan<float> span = dense.Buffer.Span;
            for (int t = 0; t < validSteps; t++)
            {
                int rowStart = t * vocabDim;
                int bestId = 0;
                float bestVal = span[rowStart];
                for (int c = 1; c < vocabLen; c++)
                {
                    float v = span[rowStart + c];
                    if (v > bestVal)
                    {
                        bestVal = v;
                        bestId = c;
                    }
                }

                if (bestId != prevId && bestId != _blankId && bestId != _unkId)
                {
                    string tok = _vocab[bestId];
                    if (!string.IsNullOrEmpty(tok))
                        sb.Append(tok);
                }
                prevId = bestId;
            }
        }
        else
        {
            for (int t = 0; t < validSteps; t++)
            {
                int bestId = 0;
                float bestVal = logProbs[0, t, 0];
                for (int c = 1; c < vocabLen; c++)
                {
                    float v = logProbs[0, t, c];
                    if (v > bestVal)
                    {
                        bestVal = v;
                        bestId = c;
                    }
                }

                if (bestId != prevId && bestId != _blankId && bestId != _unkId)
                {
                    string tok = _vocab[bestId];
                    if (!string.IsNullOrEmpty(tok))
                        sb.Append(tok);
                }
                prevId = bestId;
            }
        }

        string decoded = sb.ToString().Replace('\u2581', ' ');
        decoded = MultiSpaceRegex.Replace(decoded, " ").Trim();
        return decoded;
    }

    private static string[] LoadVocab(string path, out int blankId, out int unkId)
    {
        blankId = 256;
        unkId = 0;

        var entries = new List<(int Id, string Token)>(300);
        int maxId = -1;

        foreach (var rawLine in File.ReadLines(path, Encoding.UTF8))
        {
            string line = rawLine.TrimEnd('\r', '\n');
            if (string.IsNullOrWhiteSpace(line))
                continue;

            int sep = line.LastIndexOf(' ');
            if (sep <= 0 || sep + 1 >= line.Length)
                continue;

            if (!int.TryParse(line.AsSpan(sep + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int id) || id < 0)
                continue;

            string token = line[..sep];
            if (token == "<blk>") blankId = id;
            else if (token == "<unk>") unkId = id;

            entries.Add((id, token));
            if (id > maxId) maxId = id;
        }

        if (maxId < 0)
            return Array.Empty<string>();

        var vocab = new string[maxId + 1];
        Array.Fill(vocab, "");
        foreach (var (id, token) in entries)
            vocab[id] = token;

        return vocab;
    }

    private void ScheduleIdleUnload()
    {
        if (_disposed) return;
        try { _idleUnloadTimer.Change(IdleUnloadMs, Timeout.Infinite); } catch { }
    }

    private void OnIdleUnloadTimer(object? _)
    {
        if (_disposed || !IsLoaded || Volatile.Read(ref _activeOps) > 0)
            return;

        if (Environment.TickCount64 - Volatile.Read(ref _lastUseTick) < IdleUnloadMs - 500)
            return;

        Unload();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _idleUnloadTimer.Dispose(); } catch { }
        try
        {
            if (_inferLock.Wait(500))
            {
                try { UnloadLocked(); }
                finally { _inferLock.Release(); }
            }
        }
        catch { }
        try { _inferLock.Dispose(); } catch { }
    }
}
