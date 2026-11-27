using System.IO;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace DynamicIsland.Services;

/// <summary>
/// Лёгкий нейросетевой детектор речи Silero VAD v5 поверх onnxruntime.dll (~2.2 МБ на диске).
/// Используется для точного определения границ речи в реальном времени и обрезки тишины перед Whisper.
/// </summary>
public sealed class SileroVadEngine : IDisposable
{
    private const int SampleRate = 16000;
    private const int FrameSamples = 512;
    private const int FrameBytes = FrameSamples * 2;
    private const int ContextSamplesV5 = 64;
    private const int StateSize = 2 * 1 * 128;

    private readonly object _lock = new();
    private InferenceSession? _session;
    private string _inputName = "input";
    private string _stateName = "state";
    private string _srName = "sr";
    private bool _hasSrInput = true;
    private bool _srScalar;
    private int _contextSamples = ContextSamplesV5;

    private float[] _state = new float[StateSize];
    private float[] _context = new float[ContextSamplesV5];
    private readonly byte[] _residual = new byte[FrameBytes];
    private int _residualCount;
    private bool _disposed;

    public bool IsReady
    {
        get { lock (_lock) return _session != null && !_disposed; }
    }

    public bool Initialize(string onnxRuntimeDir, string vadModelPath)
    {
        if (_disposed || string.IsNullOrWhiteSpace(vadModelPath) || !File.Exists(vadModelPath))
            return false;

        lock (_lock)
        {
            if (_disposed) return false;
            if (_session != null) return true;

            InferenceSession? created = null;
            try
            {
                SupertonicTtsEngine.EnsureNativeResolverRegistered(onnxRuntimeDir);

                using var opts = new SessionOptions
                {
                    GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                    ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
                    IntraOpNumThreads = 1,
                    InterOpNumThreads = 1,
                    EnableMemoryPattern = false,
                    EnableCpuMemArena = false
                };
                try { opts.AddSessionConfigEntry("session.intra_op.allow_spinning", "0"); } catch { }
                try { opts.AddSessionConfigEntry("session.inter_op.allow_spinning", "0"); } catch { }
                try { opts.AddSessionConfigEntry("session.set_denormal_as_zero", "1"); } catch { }

                created = new InferenceSession(vadModelPath, opts);

                var meta = created.InputMetadata;
                _inputName = meta.ContainsKey("input") ? "input" : meta.Keys.FirstOrDefault() ?? "input";
                _stateName = meta.ContainsKey("state") ? "state" : (meta.Keys.FirstOrDefault(k => k.Contains("state", StringComparison.OrdinalIgnoreCase)) ?? "state");
                _hasSrInput = meta.ContainsKey("sr");
                _srName = "sr";
                _srScalar = _hasSrInput && meta[_srName].Dimensions.Length == 0;

                int ctx = ContextSamplesV5;
                if (meta.TryGetValue(_inputName, out var inMeta) && inMeta.Dimensions.Length == 2 && inMeta.Dimensions[1] > 0)
                {
                    ctx = Math.Max(0, inMeta.Dimensions[1] - FrameSamples);
                }

                // Быстрая самопроверка контракта тензоров (Silero v5: [1,576] с 64-сэмплным контекстом; v4 fallback: [1,512])
                if (!ProbeContract(created, ctx))
                {
                    int altCtx = ctx > 0 ? 0 : ContextSamplesV5;
                    if (!ProbeContract(created, altCtx))
                    {
                        created.Dispose();
                        return false;
                    }
                    ctx = altCtx;
                }

                _contextSamples = ctx;
                _session = created;
                ResetStateLocked();
                return true;
            }
            catch
            {
                try { created?.Dispose(); } catch { }
                return false;
            }
        }
    }

    public void ResetState()
    {
        lock (_lock)
        {
            ResetStateLocked();
        }
    }

    /// <summary>
    /// Принимает очередной чанк 16-бит PCM 16 кГц и возвращает максимальную вероятность речи (0..1)
    /// по всем завершённым 512-сэмпльным фреймам, либо -1 если модель не загружена / не накопился фрейм.
    /// </summary>
    public float ProcessPcm16kChunk(byte[] pcmBytes, int length)
    {
        if (pcmBytes == null || length <= 0)
            return -1f;

        int clampedLen = Math.Min(length, pcmBytes.Length);
        lock (_lock)
        {
            if (_session == null || _disposed)
                return -1f;

            float maxProb = -1f;
            int offset = 0;

            if (_residualCount > 0)
            {
                int need = FrameBytes - _residualCount;
                int take = Math.Min(need, clampedLen);
                Buffer.BlockCopy(pcmBytes, 0, _residual, _residualCount, take);
                _residualCount += take;
                offset += take;

                if (_residualCount == FrameBytes)
                {
                    float p = RunFrameLocked(_residual, 0, _state, _context);
                    if (p > maxProb) maxProb = p;
                    _residualCount = 0;
                }
            }

            while (offset + FrameBytes <= clampedLen)
            {
                float p = RunFrameLocked(pcmBytes, offset, _state, _context);
                if (p > maxProb) maxProb = p;
                offset += FrameBytes;
            }

            int rem = clampedLen - offset;
            if (rem > 0)
            {
                Buffer.BlockCopy(pcmBytes, offset, _residual, 0, rem);
                _residualCount = rem;
            }

            return maxProb;
        }
    }

    /// <summary>
    /// Обрезает ведущую и хвостовую тишину из PCM 16 кГц с помощью отдельного временного состояния Silero VAD.
    /// </summary>
    public byte[] TrimNonSpeechPcm16k(byte[] pcmBytes, float threshold = 0.42f, int leadPadMs = 220, int tailPadMs = 280)
    {
        if (pcmBytes == null || pcmBytes.Length < FrameBytes * 2)
            return pcmBytes ?? Array.Empty<byte>();

        lock (_lock)
        {
            if (_session == null || _disposed)
                return pcmBytes;

            int totalFrames = pcmBytes.Length / FrameBytes;
            if (totalFrames < 3)
                return pcmBytes;

            var tmpState = new float[StateSize];
            var tmpContext = new float[ContextSamplesV5];
            int firstSpeech = -1;
            int lastSpeech = -1;

            for (int f = 0; f < totalFrames; f++)
            {
                float p = RunFrameLocked(pcmBytes, f * FrameBytes, tmpState, tmpContext);
                if (p >= threshold)
                {
                    if (firstSpeech < 0) firstSpeech = f;
                    lastSpeech = f;
                }
            }

            if (firstSpeech < 0 || lastSpeech < firstSpeech)
                return Array.Empty<byte>();

            int leadFrames = Math.Max(1, (leadPadMs * SampleRate / 1000 + FrameSamples - 1) / FrameSamples);
            int tailFrames = Math.Max(1, (tailPadMs * SampleRate / 1000 + FrameSamples - 1) / FrameSamples);

            int startFrame = Math.Max(0, firstSpeech - leadFrames);
            int endFrame = Math.Min(totalFrames - 1, lastSpeech + tailFrames);

            int startByte = startFrame * FrameBytes;
            int endByte = Math.Min(pcmBytes.Length, (endFrame + 1) * FrameBytes);
            int outLen = endByte - startByte;
            if (outLen <= 0)
                return Array.Empty<byte>();
            if (outLen >= pcmBytes.Length)
                return pcmBytes;

            var trimmed = new byte[outLen];
            Buffer.BlockCopy(pcmBytes, startByte, trimmed, 0, outLen);
            return trimmed;
        }
    }

    public void Unload()
    {
        lock (_lock)
        {
            try { _session?.Dispose(); } catch { }
            _session = null;
            ResetStateLocked();
        }
    }

    private void ResetStateLocked()
    {
        Array.Clear(_state, 0, _state.Length);
        Array.Clear(_context, 0, _context.Length);
        _residualCount = 0;
    }

    private bool ProbeContract(InferenceSession session, int ctxLen)
    {
        try
        {
            int totalIn = ctxLen + FrameSamples;
            var inputBuf = new float[totalIn];
            var stateBuf = new float[StateSize];
            var inputs = BuildInputs(inputBuf, totalIn, stateBuf);
            using var res = session.Run(inputs);
            _ = res.First().AsTensor<float>().GetValue(0);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private float RunFrameLocked(byte[] pcmBytes, int byteOffset, float[] stateBuf, float[] contextBuf)
    {
        var session = _session!;
        int ctxLen = _contextSamples;
        int totalIn = ctxLen + FrameSamples;
        var inputBuf = new float[totalIn];

        if (ctxLen > 0)
            Array.Copy(contextBuf, 0, inputBuf, 0, ctxLen);

        for (int i = 0; i < FrameSamples; i++)
        {
            int bi = byteOffset + i * 2;
            short s = (short)(pcmBytes[bi] | (pcmBytes[bi + 1] << 8));
            inputBuf[ctxLen + i] = s / 32768f;
        }

        if (ctxLen > 0)
            Array.Copy(inputBuf, FrameSamples, contextBuf, 0, ctxLen);

        var inputs = BuildInputs(inputBuf, totalIn, stateBuf);
        using var outputs = session.Run(inputs);

        float prob = 0f;
        foreach (var o in outputs)
        {
            if (o.Name == "output" || o.Name == "prob" || outputs.Count == 1)
            {
                prob = Math.Clamp(o.AsTensor<float>().GetValue(0), 0f, 1f);
            }
            else if (o.Name == "stateN" || o.Name == "state_out" || o.Name.Contains("state", StringComparison.OrdinalIgnoreCase))
            {
                var stTensor = o.AsTensor<float>();
                if (stTensor is DenseTensor<float> dense)
                {
                    int copyLen = Math.Min(stateBuf.Length, dense.Buffer.Length);
                    dense.Buffer.Span[..copyLen].CopyTo(stateBuf);
                }
                else
                {
                    var arr = stTensor.ToArray();
                    Array.Copy(arr, stateBuf, Math.Min(arr.Length, stateBuf.Length));
                }
            }
        }
        return prob;
    }

    private List<NamedOnnxValue> BuildInputs(float[] inputBuf, int totalIn, float[] stateBuf)
    {
        var inputs = new List<NamedOnnxValue>(3)
        {
            NamedOnnxValue.CreateFromTensor(_inputName, new DenseTensor<float>(inputBuf, new[] { 1, totalIn })),
            NamedOnnxValue.CreateFromTensor(_stateName, new DenseTensor<float>(stateBuf, new[] { 2, 1, 128 }))
        };
        if (_hasSrInput)
        {
            var srTensor = _srScalar
                ? new DenseTensor<long>(new[] { (long)SampleRate }, Array.Empty<int>())
                : new DenseTensor<long>(new[] { (long)SampleRate }, new[] { 1 });
            inputs.Add(NamedOnnxValue.CreateFromTensor(_srName, srTensor));
        }
        return inputs;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            try { _session?.Dispose(); } catch { }
            _session = null;
            ResetStateLocked();
        }
    }
}
