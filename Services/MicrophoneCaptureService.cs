using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace DynamicIsland.Services;

public sealed record AudioInputDeviceInfo(int DeviceIndex, string Name);

/// <summary>
/// Потоковый захват аудио с микрофона через Win32 winmm.dll в формате 16 kHz / 16-bit PCM / Mono.
/// Безопасен от deadlock waveInProc за счёт выноса обработки в выделенный рабочий поток.
/// </summary>
public sealed class MicrophoneCaptureService : IDisposable
{
    private const int SampleRate = 16000;
    private const int BitsPerSample = 16;
    private const int Channels = 1;
    private const int BufferCount = 4;
    private const int BufferSizeBytes = 2560; // 80 ms @ 16 kHz 16-bit mono

    private const uint WAVE_FORMAT_PCM = 1;
    private const uint WAVE_MAPPER = unchecked((uint)-1);
    private const uint CALLBACK_FUNCTION = 0x00030000;
    private const uint WIM_DATA = 0x3C0;

    private delegate void WaveInProc(IntPtr hwi, uint uMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2);

    private readonly object _stateLock = new();
    private readonly WaveInProc _waveInCallback;

    private IntPtr _hWaveIn;
    private byte[][]? _audioBuffers;
    private GCHandle[]? _audioPins;
    private IntPtr[]? _hdrPtrs;
    private BlockingCollection<int>? _readyQueue;
    private Thread? _workerThread;
    private volatile bool _capturing;
    private volatile bool _stopping;

    public bool IsCapturing => _capturing;
    public int ActiveDeviceIndex { get; private set; } = -1;

    /// <summary>
    /// (pcmBytes, lengthBytes, normalizedRms 0..1)
    /// </summary>
    public event Action<byte[], int, float>? AudioChunkCaptured;

    public MicrophoneCaptureService()
    {
        _waveInCallback = OnWaveInMessage;
    }

    public static List<AudioInputDeviceInfo> GetInputDevices()
    {
        var list = new List<AudioInputDeviceInfo>
        {
            new(-1, "Микрофон по умолчанию")
        };

        try
        {
            uint count = waveInGetNumDevs();
            int cbCaps = Marshal.SizeOf<WAVEINCAPSW>();
            for (uint i = 0; i < count; i++)
            {
                if (waveInGetDevCapsW(i, out var caps, (uint)cbCaps) == 0 &&
                    !string.IsNullOrWhiteSpace(caps.szPname))
                {
                    list.Add(new((int)i, caps.szPname.Trim()));
                }
            }
        }
        catch { }

        return list;
    }

    public bool Start(int deviceIndex = -1, string? errorHint = null)
    {
        lock (_stateLock)
        {
            if (_capturing && ActiveDeviceIndex == deviceIndex)
                return true;

            StopInternal();

            uint devCount = 0;
            try { devCount = waveInGetNumDevs(); } catch { }
            if (devCount == 0)
                return false;

            uint devId = (deviceIndex >= 0 && deviceIndex < (int)devCount)
                ? (uint)deviceIndex
                : WAVE_MAPPER;

            var format = new WAVEFORMATEX
            {
                wFormatTag = (ushort)WAVE_FORMAT_PCM,
                nChannels = Channels,
                nSamplesPerSec = SampleRate,
                wBitsPerSample = BitsPerSample,
                nBlockAlign = Channels * BitsPerSample / 8,
                nAvgBytesPerSec = SampleRate * Channels * BitsPerSample / 8,
                cbSize = 0
            };

            if (waveInOpen(out IntPtr hWaveIn, devId, ref format, _waveInCallback, IntPtr.Zero, CALLBACK_FUNCTION) != 0)
            {
                if (devId != WAVE_MAPPER &&
                    waveInOpen(out hWaveIn, WAVE_MAPPER, ref format, _waveInCallback, IntPtr.Zero, CALLBACK_FUNCTION) != 0)
                {
                    return false;
                }
            }

            _hWaveIn = hWaveIn;
            ActiveDeviceIndex = deviceIndex;
            _stopping = false;
            _readyQueue = new BlockingCollection<int>(BufferCount * 2);

            int hdrSize = Marshal.SizeOf<WAVEHDR>();
            _audioBuffers = new byte[BufferCount][];
            _audioPins = new GCHandle[BufferCount];
            _hdrPtrs = new IntPtr[BufferCount];

            bool ok = true;
            for (int i = 0; i < BufferCount; i++)
            {
                _audioBuffers[i] = new byte[BufferSizeBytes];
                _audioPins[i] = GCHandle.Alloc(_audioBuffers[i], GCHandleType.Pinned);
                _hdrPtrs[i] = Marshal.AllocHGlobal(hdrSize);

                var hdr = new WAVEHDR
                {
                    lpData = _audioPins[i].AddrOfPinnedObject(),
                    dwBufferLength = BufferSizeBytes,
                    dwBytesRecorded = 0,
                    dwUser = new IntPtr(i),
                    dwFlags = 0,
                    dwLoops = 0,
                    lpNext = IntPtr.Zero,
                    reserved = IntPtr.Zero
                };
                Marshal.StructureToPtr(hdr, _hdrPtrs[i], false);

                if (waveInPrepareHeader(_hWaveIn, _hdrPtrs[i], (uint)hdrSize) != 0 ||
                    waveInAddBuffer(_hWaveIn, _hdrPtrs[i], (uint)hdrSize) != 0)
                {
                    ok = false;
                    break;
                }
            }

            if (!ok || waveInStart(_hWaveIn) != 0)
            {
                StopInternal();
                return false;
            }

            _capturing = true;
            _workerThread = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "OmniWave_MicCapture"
            };
            _workerThread.Start();
            return true;
        }
    }

    private void OnWaveInMessage(IntPtr hwi, uint uMsg, IntPtr dwInstance, IntPtr dwParam1, IntPtr dwParam2)
    {
        if (uMsg != WIM_DATA || _stopping)
            return;

        var ptrs = _hdrPtrs;
        var q = _readyQueue;
        if (ptrs == null || q == null)
            return;

        for (int i = 0; i < ptrs.Length; i++)
        {
            if (ptrs[i] == dwParam1)
            {
                try { q.Add(i); } catch { }
                break;
            }
        }
    }

    private void WorkerLoop()
    {
        var q = _readyQueue;
        if (q == null) return;

        int hdrSize = Marshal.SizeOf<WAVEHDR>();

        while (!_stopping)
        {
            int idx;
            try
            {
                idx = q.Take();
            }
            catch
            {
                break;
            }

            if (_stopping) break;

            var hdrPtrs = _hdrPtrs;
            var bufs = _audioBuffers;
            var hWave = _hWaveIn;
            if (hdrPtrs == null || bufs == null || hWave == IntPtr.Zero || idx < 0 || idx >= bufs.Length)
                continue;

            var hdr = Marshal.PtrToStructure<WAVEHDR>(hdrPtrs[idx]);
            int recorded = (int)Math.Min(hdr.dwBytesRecorded, (uint)bufs[idx].Length);
            if (recorded > 1)
            {
                byte[] copy = new byte[recorded];
                Buffer.BlockCopy(bufs[idx], 0, copy, 0, recorded);
                float rms = ComputeNormalizedRms(copy, recorded);
                try
                {
                    AudioChunkCaptured?.Invoke(copy, recorded, rms);
                }
                catch { }
            }

            if (!_stopping && _hWaveIn != IntPtr.Zero)
            {
                hdr.dwBytesRecorded = 0;
                Marshal.StructureToPtr(hdr, hdrPtrs[idx], false);
                _ = waveInAddBuffer(_hWaveIn, hdrPtrs[idx], (uint)hdrSize);
            }
        }
    }

    private static float ComputeNormalizedRms(byte[] pcm16, int length)
    {
        int samples = length / 2;
        if (samples <= 0) return 0f;

        double sumSq = 0;
        for (int i = 0; i < samples * 2; i += 2)
        {
            short s = (short)(pcm16[i] | (pcm16[i + 1] << 8));
            double norm = s / 32768.0;
            sumSq += norm * norm;
        }

        float rms = (float)Math.Sqrt(sumSq / samples);
        return Math.Clamp(rms * 5.0f, 0f, 1f);
    }

    public void Stop()
    {
        lock (_stateLock)
            StopInternal();
    }

    private void StopInternal()
    {
        _stopping = true;
        _capturing = false;

        try { _readyQueue?.CompleteAdding(); } catch { }

        if (_hWaveIn != IntPtr.Zero)
        {
            try { _ = waveInStop(_hWaveIn); } catch { }
            try { _ = waveInReset(_hWaveIn); } catch { }
        }

        if (_workerThread != null && _workerThread != Thread.CurrentThread)
        {
            try { _workerThread.Join(600); } catch { }
            _workerThread = null;
        }

        if (_hWaveIn != IntPtr.Zero && _hdrPtrs != null)
        {
            int hdrSize = Marshal.SizeOf<WAVEHDR>();
            for (int i = 0; i < _hdrPtrs.Length; i++)
            {
                if (_hdrPtrs[i] != IntPtr.Zero)
                {
                    try { _ = waveInUnprepareHeader(_hWaveIn, _hdrPtrs[i], (uint)hdrSize); } catch { }
                    Marshal.FreeHGlobal(_hdrPtrs[i]);
                    _hdrPtrs[i] = IntPtr.Zero;
                }
            }
        }
        _hdrPtrs = null;

        if (_audioPins != null)
        {
            for (int i = 0; i < _audioPins.Length; i++)
            {
                if (_audioPins[i].IsAllocated)
                    _audioPins[i].Free();
            }
            _audioPins = null;
        }
        _audioBuffers = null;

        if (_hWaveIn != IntPtr.Zero)
        {
            try { _ = waveInClose(_hWaveIn); } catch { }
            _hWaveIn = IntPtr.Zero;
        }

        try { _readyQueue?.Dispose(); } catch { }
        _readyQueue = null;
    }

    public void Dispose()
    {
        Stop();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct WAVEFORMATEX
    {
        public ushort wFormatTag;
        public ushort nChannels;
        public uint nSamplesPerSec;
        public uint nAvgBytesPerSec;
        public ushort nBlockAlign;
        public ushort wBitsPerSample;
        public ushort cbSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WAVEHDR
    {
        public IntPtr lpData;
        public uint dwBufferLength;
        public uint dwBytesRecorded;
        public IntPtr dwUser;
        public uint dwFlags;
        public uint dwLoops;
        public IntPtr lpNext;
        public IntPtr reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WAVEINCAPSW
    {
        public ushort wMid;
        public ushort wPid;
        public uint vDriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szPname;
        public uint dwFormats;
        public ushort wChannels;
        public ushort wReserved1;
    }

    [DllImport("winmm.dll")]
    private static extern uint waveInGetNumDevs();

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)]
    private static extern uint waveInGetDevCapsW(uint uDeviceID, out WAVEINCAPSW pwic, uint cbwic);

    [DllImport("winmm.dll")]
    private static extern uint waveInOpen(out IntPtr phwi, uint uDeviceID, ref WAVEFORMATEX pwfx, WaveInProc dwCallback, IntPtr dwInstance, uint fdwOpen);

    [DllImport("winmm.dll")]
    private static extern uint waveInPrepareHeader(IntPtr hwi, IntPtr pwh, uint cbwh);

    [DllImport("winmm.dll")]
    private static extern uint waveInUnprepareHeader(IntPtr hwi, IntPtr pwh, uint cbwh);

    [DllImport("winmm.dll")]
    private static extern uint waveInAddBuffer(IntPtr hwi, IntPtr pwh, uint cbwh);

    [DllImport("winmm.dll")]
    private static extern uint waveInStart(IntPtr hwi);

    [DllImport("winmm.dll")]
    private static extern uint waveInStop(IntPtr hwi);

    [DllImport("winmm.dll")]
    private static extern uint waveInReset(IntPtr hwi);

    [DllImport("winmm.dll")]
    private static extern uint waveInClose(IntPtr hwi);
}
