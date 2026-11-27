using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace DynamicIsland.Services;

/// <summary>
/// Потоковый офлайн-движок распознавания русской речи поверх нативного C API libvosk.dll.
/// Загружается динамически из %AppData%\OmniWave\voice\vosk-bin\ (без NuGet-зависимостей).
/// </summary>
public sealed class VoskNativeEngine : IDisposable
{
    private static readonly object DllLoadLock = new();
    private static bool _dllsLoaded;
    private static IntPtr _libVoskHandle;

    private static VoskSetLogLevelFn? _setLogLevel;
    private static VoskModelNewFn? _modelNew;
    private static VoskModelFreeFn? _modelFree;
    private static VoskRecognizerNewFn? _recNew;
    private static VoskRecognizerSetMaxAltFn? _recSetMaxAlt;
    private static VoskRecognizerSetWordsFn? _recSetWords;
    private static VoskRecognizerAcceptFn? _recAccept;
    private static VoskRecognizerStrFn? _recResult;
    private static VoskRecognizerStrFn? _recPartial;
    private static VoskRecognizerStrFn? _recFinal;
    private static VoskRecognizerVoidFn? _recReset;
    private static VoskRecognizerVoidFn? _recFree;

    private readonly object _engineLock = new();
    private IntPtr _modelPtr;
    private IntPtr _recognizerPtr;
    private bool _disposed;

    public bool IsReady
    {
        get { lock (_engineLock) return _modelPtr != IntPtr.Zero && _recognizerPtr != IntPtr.Zero; }
    }

    public bool Initialize(string binDir, string modelDir)
    {
        lock (_engineLock)
        {
            if (_modelPtr != IntPtr.Zero && _recognizerPtr != IntPtr.Zero)
                return true;

            if (!EnsureNativeLibrariesLoaded(binDir))
                return false;

            string safePath = ToAsciiSafePath(modelDir);
            byte[] pathUtf8 = Encoding.UTF8.GetBytes(safePath + "\0");
            _modelPtr = _modelNew!(pathUtf8);
            if (_modelPtr == IntPtr.Zero)
                return false;

            _recognizerPtr = _recNew!(_modelPtr, 16000.0f);
            if (_recognizerPtr == IntPtr.Zero)
            {
                _modelFree!(_modelPtr);
                _modelPtr = IntPtr.Zero;
                return false;
            }

            _recSetMaxAlt?.Invoke(_recognizerPtr, 0);
            _recSetWords?.Invoke(_recognizerPtr, 0);
            return true;
        }
    }

    public (bool IsFinal, string Text, string Partial) FeedAudio(byte[] pcmBytes, int length)
    {
        if (_disposed || length <= 0)
            return (false, "", "");

        lock (_engineLock)
        {
            if (_recognizerPtr == IntPtr.Zero)
                return (false, "", "");

            int state = _recAccept!(_recognizerPtr, pcmBytes, length);
            if (state > 0)
            {
                string json = PtrToUtf8(_recResult!(_recognizerPtr));
                return (true, ExtractJsonField(json, "text"), "");
            }
            if (state == 0)
            {
                string json = PtrToUtf8(_recPartial!(_recognizerPtr));
                return (false, "", ExtractJsonField(json, "partial"));
            }
            return (false, "", "");
        }
    }

    public string FinalizeUtterance()
    {
        lock (_engineLock)
        {
            if (_recognizerPtr == IntPtr.Zero)
                return "";

            string json = PtrToUtf8(_recFinal!(_recognizerPtr));
            string text = ExtractJsonField(json, "text");
            _recReset!(_recognizerPtr);
            return text;
        }
    }

    public void Reset()
    {
        lock (_engineLock)
        {
            if (_recognizerPtr != IntPtr.Zero)
                _recReset!(_recognizerPtr);
        }
    }

    private static bool EnsureNativeLibrariesLoaded(string binDir)
    {
        lock (DllLoadLock)
        {
            if (_dllsLoaded && _libVoskHandle != IntPtr.Zero)
                return true;

            try
            {
                string[] order =
                {
                    "libwinpthread-1.dll",
                    "libgcc_s_seh-1.dll",
                    "libstdc++-6.dll",
                    "libvosk.dll"
                };

                IntPtr lastHandle = IntPtr.Zero;
                foreach (var dll in order)
                {
                    string full = Path.Combine(binDir, dll);
                    if (!File.Exists(full))
                        return false;
                    lastHandle = NativeLibrary.Load(full);
                }

                _libVoskHandle = lastHandle;
                _setLogLevel = Resolve<VoskSetLogLevelFn>(_libVoskHandle, "vosk_set_log_level");
                _modelNew = Resolve<VoskModelNewFn>(_libVoskHandle, "vosk_model_new");
                _modelFree = Resolve<VoskModelFreeFn>(_libVoskHandle, "vosk_model_free");
                _recNew = Resolve<VoskRecognizerNewFn>(_libVoskHandle, "vosk_recognizer_new");
                _recSetMaxAlt = Resolve<VoskRecognizerSetMaxAltFn>(_libVoskHandle, "vosk_recognizer_set_max_alternatives");
                _recSetWords = Resolve<VoskRecognizerSetWordsFn>(_libVoskHandle, "vosk_recognizer_set_words");
                _recAccept = Resolve<VoskRecognizerAcceptFn>(_libVoskHandle, "vosk_recognizer_accept_waveform");
                _recResult = Resolve<VoskRecognizerStrFn>(_libVoskHandle, "vosk_recognizer_result");
                _recPartial = Resolve<VoskRecognizerStrFn>(_libVoskHandle, "vosk_recognizer_partial_result");
                _recFinal = Resolve<VoskRecognizerStrFn>(_libVoskHandle, "vosk_recognizer_final_result");
                _recReset = Resolve<VoskRecognizerVoidFn>(_libVoskHandle, "vosk_recognizer_reset");
                _recFree = Resolve<VoskRecognizerVoidFn>(_libVoskHandle, "vosk_recognizer_free");

                _setLogLevel?.Invoke(-1);
                _dllsLoaded = true;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    private static T Resolve<T>(IntPtr lib, string name) where T : Delegate
    {
        IntPtr export = NativeLibrary.GetExport(lib, name);
        return Marshal.GetDelegateForFunctionPointer<T>(export);
    }

    private static string ToAsciiSafePath(string path)
    {
        if (path.All(c => c < 128))
            return path;

        try
        {
            var sb = new StringBuilder(512);
            uint len = GetShortPathNameW(path, sb, (uint)sb.Capacity);
            if (len > 0 && len < sb.Capacity)
                return sb.ToString();
        }
        catch { }

        return path;
    }

    private static string PtrToUtf8(IntPtr ptr)
        => ptr == IntPtr.Zero ? "" : (Marshal.PtrToStringUTF8(ptr) ?? "");

    private static string ExtractJsonField(string json, string field)
    {
        if (string.IsNullOrWhiteSpace(json)) return "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(field, out var el) && el.ValueKind == JsonValueKind.String)
                return (el.GetString() ?? "").Trim();
        }
        catch { }
        return "";
    }

    public void Dispose()
    {
        lock (_engineLock)
        {
            _disposed = true;
            if (_recognizerPtr != IntPtr.Zero)
            {
                try { _recFree?.Invoke(_recognizerPtr); } catch { }
                _recognizerPtr = IntPtr.Zero;
            }
            if (_modelPtr != IntPtr.Zero)
            {
                try { _modelFree?.Invoke(_modelPtr); } catch { }
                _modelPtr = IntPtr.Zero;
            }
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void VoskSetLogLevelFn(int logLevel);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr VoskModelNewFn(byte[] modelPathUtf8);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void VoskModelFreeFn(IntPtr model);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr VoskRecognizerNewFn(IntPtr model, float sampleRate);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void VoskRecognizerSetMaxAltFn(IntPtr recognizer, int maxAlternatives);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void VoskRecognizerSetWordsFn(IntPtr recognizer, int words);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int VoskRecognizerAcceptFn(IntPtr recognizer, byte[] data, int length);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr VoskRecognizerStrFn(IntPtr recognizer);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void VoskRecognizerVoidFn(IntPtr recognizer);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string lpszLongPath, StringBuilder lpszShortPath, uint cchBuffer);
}
