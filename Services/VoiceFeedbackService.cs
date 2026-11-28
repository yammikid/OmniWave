using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace DynamicIsland.Services;

public sealed record TtsVoiceInfo(string Id, string DisplayName, string ShortName);

/// <summary>
/// Локальные голосовые ответы: только нейросетевая женская модель Supertonic 3 (F1–F5, RU+EN) и звуковые отклики.
/// </summary>
public sealed class VoiceFeedbackService : IDisposable
{
    private const uint SND_SYNC = 0x0000;
    private const uint SND_NODEFAULT = 0x0002;
    private const uint SND_MEMORY = 0x0004;

    private static readonly byte[] WakeChimeWav = BuildTwoToneChime(523.25, 783.99, 62, 74, 0.22);
    private static readonly byte[] ConfirmChimeWav = BuildTwoToneChime(659.25, 880.00, 42, 54, 0.16);
    private static readonly byte[] CancelChimeWav = BuildTwoToneChime(493.88, 349.23, 48, 60, 0.16);

    private readonly VoiceModelManager? _models;
    private readonly SupertonicTtsEngine? _neuralEngine;
    private readonly SemaphoreSlim _playLock = new(1, 1);
    private CancellationTokenSource? _speakCts;
    private int _volumePercent = 85;
    private bool _disposed;

    public bool IsNeuralLoaded => _neuralEngine?.IsLoaded == true;

    public int VolumePercent
    {
        get => Volatile.Read(ref _volumePercent);
        set => Volatile.Write(ref _volumePercent, Math.Clamp(value, 0, 100));
    }

    public VoiceFeedbackService(VoiceModelManager? models = null)
    {
        _models = models;
        if (models != null)
            _neuralEngine = new SupertonicTtsEngine(models);
    }

    public static List<TtsVoiceInfo> GetAvailableVoices(VoiceModelManager? models)
    {
        var result = new List<TtsVoiceInfo>(SupertonicTtsEngine.FemaleVoices.Count);
        foreach (var preset in SupertonicTtsEngine.FemaleVoices)
            result.Add(new(preset.Id, preset.DisplayName, preset.ShortName));
        return result;
    }

    public Task WarmUpNeuralAsync(string preferredVoiceName = "", CancellationToken ct = default)
    {
        if (_disposed || _neuralEngine == null || _models?.IsNeuralTtsReady() != true)
            return Task.CompletedTask;

        return _neuralEngine.WarmUpAsync(preferredVoiceName, ct);
    }

    public void UnloadNeuralModels()
    {
        StopSpeaking();
        try { _neuralEngine?.Unload(); } catch { }
    }

    public Task PlayWakeChimeAsync() => PlayWavBytesAsync(WakeChimeWav, CancellationToken.None);
    public Task PlayConfirmChimeAsync() => PlayWavBytesAsync(ConfirmChimeWav, CancellationToken.None);
    public Task PlayCancelChimeAsync() => PlayWavBytesAsync(CancelChimeWav, CancellationToken.None);

    public void StopSpeaking()
    {
        var prev = Interlocked.Exchange(ref _speakCts, null);
        if (prev != null)
        {
            try { prev.Cancel(); } catch { }
            try { prev.Dispose(); } catch { }
        }
        try { _ = PlaySoundW(IntPtr.Zero, IntPtr.Zero, 0); } catch { }
    }

    public async Task SpeakAsync(string text, string preferredVoiceName = "", CancellationToken outerCt = default)
    {
        if (_disposed || string.IsNullOrWhiteSpace(text) || VolumePercent <= 0) return;
        if (_neuralEngine == null || _models?.IsNeuralTtsReady() != true)
            return;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        var prev = Interlocked.Exchange(ref _speakCts, cts);
        if (prev != null)
        {
            try { prev.Cancel(); } catch { }
            try { prev.Dispose(); } catch { }
        }
        try { _ = PlaySoundW(IntPtr.Zero, IntPtr.Zero, 0); } catch { }

        byte[]? wavBytes;
        try
        {
            wavBytes = await _neuralEngine.SynthesizeWavAsync(text, preferredVoiceName, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
            return;
        }

        if (wavBytes == null || wavBytes.Length < 44 || cts.IsCancellationRequested)
            return;

        await PlayWavBytesAsync(wavBytes, cts.Token);
    }

    private async Task PlayWavBytesAsync(byte[] wavBytes, CancellationToken ct)
    {
        if (_disposed || wavBytes.Length < 44) return;
        int vol = VolumePercent;
        if (vol <= 0) return;

        byte[] playbackWav = ApplyVolumeToWav(wavBytes, vol);
        if (playbackWav.Length < 44) return;

        await _playLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (ct.IsCancellationRequested || _disposed) return;

            await Task.Run(() =>
            {
                var pin = GCHandle.Alloc(playbackWav, GCHandleType.Pinned);
                try
                {
                    if (!ct.IsCancellationRequested && !_disposed)
                        _ = PlaySoundW(pin.AddrOfPinnedObject(), IntPtr.Zero, SND_MEMORY | SND_SYNC | SND_NODEFAULT);
                }
                finally
                {
                    if (pin.IsAllocated)
                        pin.Free();
                }
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch { }
        finally
        {
            _playLock.Release();
        }
    }

    /// <summary>
    /// Применяет перцептивную кривую громкости (vol/100)^1.5 к PCM16LE WAV-буферу без изменения исходного кэша.
    /// </summary>
    public static byte[] ApplyVolumeToWav(byte[] wavBytes, int volumePercent)
    {
        int vol = Math.Clamp(volumePercent, 0, 100);
        if (vol <= 0 || wavBytes == null || wavBytes.Length < 44)
            return Array.Empty<byte>();
        if (vol >= 100)
            return wavBytes;

        byte[] scaled = new byte[wavBytes.Length];
        Buffer.BlockCopy(wavBytes, 0, scaled, 0, wavBytes.Length);

        int dataStart = FindWavDataChunkOffset(scaled, out int dataLength);
        int dataEnd = Math.Min(scaled.Length, dataStart + dataLength);
        float gain = MathF.Pow(vol / 100f, 1.5f);

        for (int i = dataStart; i + 1 < dataEnd; i += 2)
        {
            short sample = (short)(scaled[i] | (scaled[i + 1] << 8));
            int amplified = Math.Clamp((int)MathF.Round(sample * gain), -32768, 32767);
            short outSample = (short)amplified;
            scaled[i] = (byte)(outSample & 0xFF);
            scaled[i + 1] = (byte)((outSample >> 8) & 0xFF);
        }

        return scaled;
    }

    private static int FindWavDataChunkOffset(byte[] wav, out int dataLength)
    {
        if (wav.Length >= 44 &&
            wav[0] == (byte)'R' && wav[1] == (byte)'I' && wav[2] == (byte)'F' && wav[3] == (byte)'F' &&
            wav[8] == (byte)'W' && wav[9] == (byte)'A' && wav[10] == (byte)'V' && wav[11] == (byte)'E')
        {
            int pos = 12;
            while (pos + 8 <= wav.Length)
            {
                int chunkSize = wav[pos + 4] | (wav[pos + 5] << 8) | (wav[pos + 6] << 16) | (wav[pos + 7] << 24);
                if (chunkSize < 0)
                    break;

                if (wav[pos] == (byte)'d' && wav[pos + 1] == (byte)'a' &&
                    wav[pos + 2] == (byte)'t' && wav[pos + 3] == (byte)'a')
                {
                    dataLength = Math.Min(chunkSize, wav.Length - (pos + 8));
                    return pos + 8;
                }

                int step = 8 + chunkSize + (chunkSize & 1);
                if (step <= 8 || pos + step > wav.Length)
                    break;
                pos += step;
            }
        }

        dataLength = Math.Max(0, wav.Length - 44);
        return Math.Min(44, wav.Length);
    }

    private static byte[] BuildTwoToneChime(double f1Hz, double f2Hz, int ms1, int ms2, double amp)
    {
        const int sr = 16000;
        int n1 = sr * ms1 / 1000;
        int n2 = sr * ms2 / 1000;
        int totalSamples = n1 + n2;
        int dataBytes = totalSamples * 2;

        using var ms = new MemoryStream(44 + dataBytes);
        using var bw = new BinaryWriter(ms, Encoding.ASCII);

        bw.Write("RIFF"u8);
        bw.Write(36 + dataBytes);
        bw.Write("WAVE"u8);
        bw.Write("fmt "u8);
        bw.Write(16);
        bw.Write((short)1);
        bw.Write((short)1);
        bw.Write(sr);
        bw.Write(sr * 2);
        bw.Write((short)2);
        bw.Write((short)16);
        bw.Write("data"u8);
        bw.Write(dataBytes);

        for (int i = 0; i < totalSamples; i++)
        {
            bool second = i >= n1;
            int segIdx = second ? (i - n1) : i;
            int segLen = second ? n2 : n1;
            double freq = second ? f2Hz : f1Hz;

            double t = segIdx / (double)sr;
            double env = Math.Sin(Math.PI * segIdx / Math.Max(1, segLen - 1));
            double val = Math.Sin(2.0 * Math.PI * freq * t) * amp * env;
            bw.Write((short)Math.Clamp((int)Math.Round(val * 32767.0), -32768, 32767));
        }

        bw.Flush();
        return ms.ToArray();
    }

    public void Dispose()
    {
        _disposed = true;
        StopSpeaking();
        try { _neuralEngine?.Dispose(); } catch { }
        _playLock.Dispose();
    }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySoundW(IntPtr pszSound, IntPtr hmod, uint fdwSound);
}
