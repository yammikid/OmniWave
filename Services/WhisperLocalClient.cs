using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DynamicIsland.Services;

/// <summary>
/// Управляет тёплым локальным процессом whisper-server.exe на loopback 127.0.0.1
/// и распознаёт смешанную RU+EN речь (названия треков/артистов) через HTTP POST /inference.
/// </summary>
public sealed class WhisperLocalClient : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

    private static readonly Regex NoiseTagRegex = new(@"\[[^\]]*\]|\([^\)]*\)", RegexOptions.Compiled);
    private static readonly Regex MultiSpaceRegex = new(@"\s+", RegexOptions.Compiled);

    private readonly VoiceModelManager _models;
    private readonly SemaphoreSlim _serverLock = new(1, 1);
    private readonly HttpClient _http;

    private IntPtr _jobHandle;
    private Process? _process;
    private int _port;
    private string _activeVariant = "";
    private bool _disposed;

    public bool IsRunning => _process is { HasExited: false } && _port > 0;

    public WhisperLocalClient(VoiceModelManager models)
    {
        _models = models;
        _http = new HttpClient(new HttpClientHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false
        })
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
    }

    public async Task<bool> EnsureStartedAsync(string modelVariant, CancellationToken ct = default)
    {
        if (_disposed || !_models.IsWhisperReady(modelVariant))
            return false;

        if (IsRunning && string.Equals(_activeVariant, modelVariant, StringComparison.OrdinalIgnoreCase))
            return true;

        await _serverLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_disposed || !_models.IsWhisperReady(modelVariant))
                return false;

            if (IsRunning && string.Equals(_activeVariant, modelVariant, StringComparison.OrdinalIgnoreCase))
                return true;

            StopProcessLocked();

            string exePath = _models.GetWhisperServerPath();
            string modelPath = _models.GetWhisperModelPath(modelVariant);
            if (!File.Exists(exePath) || !File.Exists(modelPath))
                return false;

            int port = PickFreeLoopbackPort();
            int threads = Math.Clamp(Environment.ProcessorCount / 2, 2, 8);
            const string prompt = "Команда для музыкального плеера OmniWave: включи, поставь, пауза, следующий, громче, тише, моя волна, вк микс, плейлист, таймер сна, радио, Linkin Park, The Weeknd, Miyagi, Кино, Скриптонит.";

            var psi = new ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = _models.WhisperBinDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("-m");
            psi.ArgumentList.Add(modelPath);
            psi.ArgumentList.Add("-l");
            psi.ArgumentList.Add("ru");
            psi.ArgumentList.Add("-t");
            psi.ArgumentList.Add(threads.ToString());
            psi.ArgumentList.Add("--host");
            psi.ArgumentList.Add("127.0.0.1");
            psi.ArgumentList.Add("--port");
            psi.ArgumentList.Add(port.ToString());
            psi.ArgumentList.Add("--inference-path");
            psi.ArgumentList.Add("/inference");
            psi.ArgumentList.Add("-sns");
            psi.ArgumentList.Add("-nt");
            psi.ArgumentList.Add("--prompt");
            psi.ArgumentList.Add(prompt);

            var proc = Process.Start(psi);
            if (proc == null)
                return false;

            DrainStreamAsync(proc.StandardOutput);
            DrainStreamAsync(proc.StandardError);
            AttachToKillJob(proc);

            _process = proc;
            _port = port;
            _activeVariant = modelVariant;

            var deadline = DateTime.UtcNow.AddSeconds(25);
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                if (proc.HasExited)
                {
                    StopProcessLocked();
                    return false;
                }

                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/");
                    using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                    if ((int)resp.StatusCode < 500)
                        return true;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    StopProcessLocked();
                    return false;
                }
                catch { }

                await Task.Delay(220, ct).ConfigureAwait(false);
            }

            StopProcessLocked();
            return false;
        }
        catch
        {
            StopProcessLocked();
            return false;
        }
        finally
        {
            _serverLock.Release();
        }
    }

    public async Task<string> TranscribePcm16kAsync(byte[] pcmBytes, string modelVariant, CancellationToken ct = default)
    {
        if (pcmBytes == null || pcmBytes.Length < 1600)
            return "";

        if (!await EnsureStartedAsync(modelVariant, ct).ConfigureAwait(false))
            return "";

        byte[] wav = WrapPcm16kAsWav(pcmBytes);
        int port = _port;
        if (port <= 0) return "";

        try
        {
            using var form = new MultipartFormDataContent();
            var fileContent = new ByteArrayContent(wav);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
            form.Add(fileContent, "file", "command.wav");
            form.Add(new StringContent("json"), "response_format");
            form.Add(new StringContent("0.0"), "temperature");
            form.Add(new StringContent("ru"), "language");

            using var resp = await _http.PostAsync($"http://127.0.0.1:{port}/inference", form, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
                return "";

            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("text", out var txtEl) || txtEl.ValueKind != JsonValueKind.String)
                return "";

            string cleaned = txtEl.GetString() ?? "";
            cleaned = NoiseTagRegex.Replace(cleaned, " ");
            cleaned = MultiSpaceRegex.Replace(cleaned, " ").Trim();
            return cleaned;
        }
        catch
        {
            return "";
        }
    }

    public void Stop()
    {
        _serverLock.Wait();
        try
        {
            StopProcessLocked();
        }
        finally
        {
            _serverLock.Release();
        }
    }

    private void StopProcessLocked()
    {
        _port = 0;
        _activeVariant = "";

        var proc = _process;
        _process = null;
        if (proc != null)
        {
            try
            {
                if (!proc.HasExited)
                    proc.Kill(entireProcessTree: true);
            }
            catch { }
            try { proc.Dispose(); } catch { }
        }
    }

    private void AttachToKillJob(Process proc)
    {
        try
        {
            if (_jobHandle == IntPtr.Zero)
            {
                _jobHandle = CreateJobObjectW(IntPtr.Zero, null);
                if (_jobHandle != IntPtr.Zero)
                {
                    var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                    info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                    _ = SetInformationJobObject(
                        _jobHandle,
                        JobObjectExtendedLimitInformation,
                        ref info,
                        (uint)Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>());
                }
            }

            if (_jobHandle != IntPtr.Zero)
                _ = AssignProcessToJobObject(_jobHandle, proc.Handle);
        }
        catch { }
    }

    private static byte[] WrapPcm16kAsWav(byte[] pcm)
    {
        const int sr = 16000;
        const short ch = 1;
        const short bps = 16;

        byte[] wav = new byte[44 + pcm.Length];
        using var ms = new MemoryStream(wav, writable: true);
        using var bw = new BinaryWriter(ms, Encoding.ASCII);

        bw.Write("RIFF"u8);
        bw.Write(36 + pcm.Length);
        bw.Write("WAVE"u8);
        bw.Write("fmt "u8);
        bw.Write(16);
        bw.Write((short)1);
        bw.Write(ch);
        bw.Write(sr);
        bw.Write(sr * ch * bps / 8);
        bw.Write((short)(ch * bps / 8));
        bw.Write(bps);
        bw.Write("data"u8);
        bw.Write(pcm.Length);
        bw.Write(pcm);
        bw.Flush();
        return wav;
    }

    private static int PickFreeLoopbackPort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    private static void DrainStreamAsync(StreamReader reader)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                while (await reader.ReadLineAsync() != null) { }
            }
            catch { }
        });
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
        if (_jobHandle != IntPtr.Zero)
        {
            try { _ = CloseHandle(_jobHandle); } catch { }
            _jobHandle = IntPtr.Zero;
        }
        try { _http.Dispose(); } catch { }
        try { _serverLock.Dispose(); } catch { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob,
        int JobObjectInfoClass,
        ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInfo,
        uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);
}
