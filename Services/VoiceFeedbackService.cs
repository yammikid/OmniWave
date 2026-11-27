using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Media.SpeechSynthesis;

namespace DynamicIsland.Services;

public sealed record TtsVoiceInfo(string Id, string DisplayName, string ShortName);

/// <summary>
/// Локальные голосовые ответы: нейросетевая женская модель Supertonic 3 (RU+EN) с фоллбеком на WinRT SpeechSynthesizer OneCore + звуковые отклики.
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
    private bool _disposed;

    public VoiceFeedbackService(VoiceModelManager? models = null)
    {
        _models = models;
        if (models != null)
            _neuralEngine = new SupertonicTtsEngine(models);
    }

    public static List<TtsVoiceInfo> GetAvailableVoices(VoiceModelManager? models)
    {
        var result = new List<TtsVoiceInfo>();
        if (models?.IsNeuralTtsReady() == true)
        {
            foreach (var preset in SupertonicTtsEngine.FemaleVoices)
                result.Add(new(preset.Id, preset.DisplayName, preset.ShortName));
        }
        result.AddRange(GetRussianVoices());
        return result;
    }

    public static List<TtsVoiceInfo> GetRussianVoices()
    {
        var result = new List<TtsVoiceInfo>();
        try
        {
            foreach (var v in SpeechSynthesizer.AllVoices)
            {
                if (!v.Language.StartsWith("ru", StringComparison.OrdinalIgnoreCase))
                    continue;

                string shortName = v.DisplayName
                    .Replace("Microsoft ", "", StringComparison.OrdinalIgnoreCase)
                    .Replace(" - Russian (Russia)", "", StringComparison.OrdinalIgnoreCase)
                    .Replace(" Desktop", "", StringComparison.OrdinalIgnoreCase)
                    .Trim();
                result.Add(new(v.Id, v.DisplayName, string.IsNullOrWhiteSpace(shortName) ? v.DisplayName : shortName));
            }
        }
        catch { }
        return result;
    }

    public Task WarmUpNeuralAsync(string preferredVoiceName = "", CancellationToken ct = default)
    {
        if (_disposed || _neuralEngine == null || _models?.IsNeuralTtsReady() != true)
            return Task.CompletedTask;

        bool wantsNeural = string.IsNullOrWhiteSpace(preferredVoiceName) ||
                           preferredVoiceName.StartsWith("supertonic:", StringComparison.OrdinalIgnoreCase);
        return wantsNeural ? _neuralEngine.WarmUpAsync(preferredVoiceName, ct) : Task.CompletedTask;
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
        if (_disposed || string.IsNullOrWhiteSpace(text)) return;

        var cts = CancellationTokenSource.CreateLinkedTokenSource(outerCt);
        var prev = Interlocked.Exchange(ref _speakCts, cts);
        if (prev != null)
        {
            try { prev.Cancel(); } catch { }
            try { prev.Dispose(); } catch { }
        }
        try { _ = PlaySoundW(IntPtr.Zero, IntPtr.Zero, 0); } catch { }

        byte[]? wavBytes = null;

        bool useNeural = _neuralEngine != null &&
                         _models?.IsNeuralTtsReady() == true &&
                         (string.IsNullOrWhiteSpace(preferredVoiceName) ||
                          preferredVoiceName.StartsWith("supertonic:", StringComparison.OrdinalIgnoreCase));

        if (useNeural)
        {
            try
            {
                wavBytes = await _neuralEngine!.SynthesizeWavAsync(text, preferredVoiceName, cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                wavBytes = null;
            }
        }

        if (wavBytes == null || wavBytes.Length < 44)
        {
            try
            {
                using var synth = new SpeechSynthesizer();
                var ruVoices = SpeechSynthesizer.AllVoices
                    .Where(v => v.Language.StartsWith("ru", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (ruVoices.Count > 0)
                {
                    VoiceInformation? chosen = null;
                    if (!string.IsNullOrWhiteSpace(preferredVoiceName) &&
                        !preferredVoiceName.StartsWith("supertonic:", StringComparison.OrdinalIgnoreCase))
                    {
                        chosen = ruVoices.FirstOrDefault(v =>
                            v.DisplayName.Contains(preferredVoiceName, StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(v.Id, preferredVoiceName, StringComparison.OrdinalIgnoreCase));
                    }
                    synth.Voice = chosen ?? ruVoices.FirstOrDefault(v => v.DisplayName.Contains("Irina", StringComparison.OrdinalIgnoreCase)) ?? ruVoices[0];
                }

                using var stream = await synth.SynthesizeTextToStreamAsync(text).AsTask(cts.Token);
                using var readStream = stream.AsStreamForRead();
                using var ms = new MemoryStream((int)Math.Min(stream.Size, 4 * 1024 * 1024));
                await readStream.CopyToAsync(ms, cts.Token);
                wavBytes = ms.ToArray();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                return;
            }
        }

        if (wavBytes == null || wavBytes.Length < 44 || cts.IsCancellationRequested)
            return;

        await PlayWavBytesAsync(wavBytes, cts.Token);
    }

    private async Task PlayWavBytesAsync(byte[] wavBytes, CancellationToken ct)
    {
        if (_disposed || wavBytes.Length < 44) return;

        await _playLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (ct.IsCancellationRequested || _disposed) return;

            await Task.Run(() =>
            {
                var pin = GCHandle.Alloc(wavBytes, GCHandleType.Pinned);
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
