using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DynamicIsland.Services;

public sealed record TelemetrySample(long RamMb, double CpuPercent, double GpuPercent, int ProcessCount);

/// <summary>
/// Точный замер ресурсов дерева процессов OmniWave (основной процесс + LibVLC + WebView2 + yt-dlp):
/// Private Working Set (RAM), многопроцессный delta CPU% и загрузка GPU Engine через PDH.
/// </summary>
public sealed class AppTelemetryService : IDisposable
{
    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint PDH_FMT_DOUBLE = 0x00000200;
    private const uint PDH_FMT_LARGE = 0x00000400;
    private const int PDH_MORE_DATA = unchecked((int)0x800007D2);
    private const uint PDH_CSTATUS_VALID_DATA = 0x00000000;
    private const uint PDH_CSTATUS_NEW_DATA = 0x00000001;

    private readonly object _lock = new();
    private readonly int _rootPid = Environment.ProcessId;
    private volatile int _webViewBrowserPid;

    private Dictionary<int, long> _prevCpuTicksByPid = new();
    private DateTime _prevSampleUtc = DateTime.UtcNow;
    private bool _hasCpuBaseline;

    private IntPtr _pdhQuery;
    private IntPtr _pdhProcIdCounter;
    private IntPtr _pdhProcPrivWsCounter;
    private IntPtr _pdhGpuCounter;
    private bool _pdhInitAttempted;
    private bool _pdhReady;
    private bool _pdhHasRateBaseline;

    private TelemetrySample _lastSample = new(0, 0, 0, 1);
    private volatile bool _disposed;

    public TelemetrySample LastSample
    {
        get { lock (_lock) return _lastSample; }
    }

    public void SetWebViewBrowserProcessId(uint pid)
    {
        if (pid > 0)
            _webViewBrowserPid = unchecked((int)pid);
    }

    public TelemetrySample Sample()
    {
        if (_disposed)
            return _lastSample;

        lock (_lock)
        {
            if (_disposed)
                return _lastSample;
            var pids = CollectAppProcessIds();
            if (pids.Count == 0)
                pids.Add(_rootPid);

            EnsurePdhInitialized();

            bool pdhCollected = false;
            if (_pdhReady && _pdhQuery != IntPtr.Zero)
            {
                try
                {
                    if (PdhCollectQueryData(_pdhQuery) == 0)
                        pdhCollected = true;
                }
                catch { }
            }

            long totalRamBytes = MeasurePrivateRamBytes(pids, pdhCollected);
            long ramMb = Math.Max(1, totalRamBytes / (1024L * 1024L));
            double cpuPct = MeasureCpuPercent(pids);
            double gpuPct = MeasureGpuPercent(pids, pdhCollected);

            _lastSample = new TelemetrySample(
                ramMb,
                Math.Round(cpuPct, 1),
                Math.Round(gpuPct, 1),
                pids.Count);
            return _lastSample;
        }
    }

    private HashSet<int> CollectAppProcessIds()
    {
        var result = new HashSet<int> { _rootPid };
        int wvPid = _webViewBrowserPid;
        if (wvPid > 0)
            result.Add(wvPid);

        IntPtr snap = IntPtr.Zero;
        try
        {
            snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1))
                return result;

            var childrenByParent = new Dictionary<int, List<int>>();
            var entry = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
            if (Process32FirstW(snap, ref entry))
            {
                do
                {
                    int pid = unchecked((int)entry.th32ProcessID);
                    int ppid = unchecked((int)entry.th32ParentProcessID);
                    if (pid <= 0 || pid == ppid)
                        continue;

                    if (!childrenByParent.TryGetValue(ppid, out var list))
                    {
                        list = new List<int>(4);
                        childrenByParent[ppid] = list;
                    }
                    list.Add(pid);
                }
                while (Process32NextW(snap, ref entry));
            }

            var queue = new Queue<int>(result);
            while (queue.Count > 0)
            {
                int parent = queue.Dequeue();
                if (!childrenByParent.TryGetValue(parent, out var children))
                    continue;

                foreach (int child in children)
                {
                    if (result.Add(child))
                        queue.Enqueue(child);
                }
            }
        }
        catch { }
        finally
        {
            if (snap != IntPtr.Zero && snap != new IntPtr(-1))
                _ = CloseHandle(snap);
        }

        return result;
    }

    private void EnsurePdhInitialized()
    {
        if (_pdhInitAttempted) return;
        _pdhInitAttempted = true;

        try
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out _pdhQuery) != 0 || _pdhQuery == IntPtr.Zero)
                return;

            _ = PdhAddEnglishCounterW(_pdhQuery, @"\Process(*)\ID Process", IntPtr.Zero, out _pdhProcIdCounter);
            _ = PdhAddEnglishCounterW(_pdhQuery, @"\Process(*)\Working Set - Private", IntPtr.Zero, out _pdhProcPrivWsCounter);
            _ = PdhAddEnglishCounterW(_pdhQuery, @"\GPU Engine(*)\Utilization Percentage", IntPtr.Zero, out _pdhGpuCounter);

            if (_pdhProcIdCounter != IntPtr.Zero && _pdhProcPrivWsCounter != IntPtr.Zero)
            {
                _pdhReady = true;
                // Первичная выборка формирует базовую точку для rate-счётчиков (GPU Engine).
                if (PdhCollectQueryData(_pdhQuery) == 0)
                    _pdhHasRateBaseline = true;
            }
        }
        catch
        {
            _pdhReady = false;
        }
    }

    private long MeasurePrivateRamBytes(HashSet<int> pids, bool pdhCollected)
    {
        long totalBytes = 0;
        var coveredPids = new HashSet<int>();

        if (pdhCollected && _pdhProcIdCounter != IntPtr.Zero && _pdhProcPrivWsCounter != IntPtr.Zero)
        {
            try
            {
                var pidByInstance = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                ReadPdhLargeArray(_pdhProcIdCounter, (name, val) =>
                {
                    int pid = unchecked((int)val);
                    if (pid > 0 && pids.Contains(pid))
                        pidByInstance[name] = pid;
                });

                if (pidByInstance.Count > 0)
                {
                    ReadPdhLargeArray(_pdhProcPrivWsCounter, (name, val) =>
                    {
                        if (val > 0 && pidByInstance.TryGetValue(name, out int pid) && coveredPids.Add(pid))
                            totalBytes += val;
                    });
                }
            }
            catch { }
        }

        foreach (int pid in pids)
        {
            if (coveredPids.Contains(pid)) continue;
            totalBytes += ReadWin32ProcessMemoryFallback(pid);
        }

        if (totalBytes <= 0)
        {
            try
            {
                using var self = Process.GetCurrentProcess();
                self.Refresh();
                totalBytes = Math.Max(self.PrivateMemorySize64, self.WorkingSet64);
            }
            catch { }
        }

        return totalBytes;
    }

    private static long ReadWin32ProcessMemoryFallback(int pid)
    {
        IntPtr hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProc == IntPtr.Zero) return 0;

        try
        {
            var pmc = new PROCESS_MEMORY_COUNTERS_EX { cb = (uint)Marshal.SizeOf<PROCESS_MEMORY_COUNTERS_EX>() };
            if (K32GetProcessMemoryInfo(hProc, out pmc, pmc.cb))
            {
                long priv = (long)pmc.PrivateUsage;
                long ws = (long)pmc.WorkingSetSize;
                return priv > 0 ? Math.Min(priv, ws > 0 ? ws : priv) : ws;
            }
        }
        catch { }
        finally
        {
            _ = CloseHandle(hProc);
        }

        return 0;
    }

    private double MeasureCpuPercent(HashSet<int> pids)
    {
        var nowUtc = DateTime.UtcNow;
        var nextTicks = new Dictionary<int, long>(pids.Count);
        long totalDeltaTicks = 0;

        foreach (int pid in pids)
        {
            long ticks = ReadProcessCpuTicks(pid);
            if (ticks < 0) continue;

            nextTicks[pid] = ticks;
            if (_hasCpuBaseline && _prevCpuTicksByPid.TryGetValue(pid, out long prevTicks) && ticks >= prevTicks)
                totalDeltaTicks += ticks - prevTicks;
        }

        double elapsedMs = Math.Max(1.0, (nowUtc - _prevSampleUtc).TotalMilliseconds);
        double cpuPct = 0.0;
        if (_hasCpuBaseline && elapsedMs > 5.0)
        {
            double cpuDeltaMs = totalDeltaTicks / 10_000.0;
            cpuPct = Math.Clamp(
                cpuDeltaMs / (elapsedMs * Math.Max(1, Environment.ProcessorCount)) * 100.0,
                0.0,
                100.0);
        }

        _prevCpuTicksByPid = nextTicks;
        _prevSampleUtc = nowUtc;
        _hasCpuBaseline = true;
        return cpuPct;
    }

    private static long ReadProcessCpuTicks(int pid)
    {
        IntPtr hProc = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (hProc == IntPtr.Zero) return -1;

        try
        {
            if (GetProcessTimes(hProc, out _, out _, out long kernel, out long user))
                return Math.Max(0, kernel) + Math.Max(0, user);
        }
        catch { }
        finally
        {
            _ = CloseHandle(hProc);
        }

        return -1;
    }

    private double MeasureGpuPercent(HashSet<int> pids, bool pdhCollected)
    {
        if (!pdhCollected || _pdhGpuCounter == IntPtr.Zero)
            return 0.0;

        if (!_pdhHasRateBaseline)
        {
            _pdhHasRateBaseline = true;
            return 0.0;
        }

        try
        {
            var engineUtilization = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            ReadPdhDoubleArray(_pdhGpuCounter, (instanceName, val) =>
            {
                if (val <= 0 || !TryParseGpuInstance(instanceName, out int pid, out string engineKey))
                    return;
                if (!pids.Contains(pid))
                    return;

                engineUtilization.TryGetValue(engineKey, out double existing);
                engineUtilization[engineKey] = existing + val;
            });

            if (engineUtilization.Count == 0)
                return 0.0;

            return Math.Clamp(engineUtilization.Values.Max(), 0.0, 100.0);
        }
        catch
        {
            return 0.0;
        }
    }

    private static bool TryParseGpuInstance(string instanceName, out int pid, out string engineKey)
    {
        pid = 0;
        engineKey = "";
        if (string.IsNullOrEmpty(instanceName))
            return false;

        if (!instanceName.StartsWith("pid_", StringComparison.OrdinalIgnoreCase))
            return false;

        int sep = instanceName.IndexOf('_', 4);
        if (sep <= 4)
            return false;

        if (!int.TryParse(instanceName.AsSpan(4, sep - 4), out pid) || pid <= 0)
            return false;

        engineKey = sep + 1 < instanceName.Length ? instanceName[(sep + 1)..] : "default";
        return true;
    }

    private static void ReadPdhLargeArray(IntPtr counter, Action<string, long> visitor)
    {
        uint bufSize = 0;
        int status = PdhGetFormattedCounterArrayW(counter, PDH_FMT_LARGE, ref bufSize, out uint count, IntPtr.Zero);
        if (status != PDH_MORE_DATA || bufSize == 0)
            return;

        IntPtr buf = Marshal.AllocHGlobal((int)bufSize);
        try
        {
            status = PdhGetFormattedCounterArrayW(counter, PDH_FMT_LARGE, ref bufSize, out count, buf);
            if (status != 0 || count == 0)
                return;

            int stride = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM_LARGE>();
            for (uint i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM_LARGE>(IntPtr.Add(buf, checked((int)i * stride)));
                if (item.CStatus != PDH_CSTATUS_VALID_DATA && item.CStatus != PDH_CSTATUS_NEW_DATA)
                    continue;

                string? name = Marshal.PtrToStringUni(item.szName);
                if (!string.IsNullOrEmpty(name))
                    visitor(name, item.largeValue);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static void ReadPdhDoubleArray(IntPtr counter, Action<string, double> visitor)
    {
        uint bufSize = 0;
        int status = PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE, ref bufSize, out uint count, IntPtr.Zero);
        if (status != PDH_MORE_DATA || bufSize == 0)
            return;

        IntPtr buf = Marshal.AllocHGlobal((int)bufSize);
        try
        {
            status = PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE, ref bufSize, out count, buf);
            if (status != 0 || count == 0)
                return;

            int stride = Marshal.SizeOf<PDH_FMT_COUNTERVALUE_ITEM_DOUBLE>();
            for (uint i = 0; i < count; i++)
            {
                var item = Marshal.PtrToStructure<PDH_FMT_COUNTERVALUE_ITEM_DOUBLE>(IntPtr.Add(buf, checked((int)i * stride)));
                if (item.CStatus != PDH_CSTATUS_VALID_DATA && item.CStatus != PDH_CSTATUS_NEW_DATA)
                    continue;
                if (double.IsNaN(item.doubleValue) || double.IsInfinity(item.doubleValue))
                    continue;

                string? name = Marshal.PtrToStringUni(item.szName);
                if (!string.IsNullOrEmpty(name))
                    visitor(name, item.doubleValue);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        bool entered = false;
        try
        {
            entered = Monitor.TryEnter(_lock, 100);
            if (!entered)
                return;

            if (_pdhQuery != IntPtr.Zero)
            {
                try { _ = PdhCloseQuery(_pdhQuery); } catch { }
                _pdhQuery = IntPtr.Zero;
            }
            _pdhProcIdCounter = IntPtr.Zero;
            _pdhProcPrivWsCounter = IntPtr.Zero;
            _pdhGpuCounter = IntPtr.Zero;
            _pdhReady = false;
        }
        finally
        {
            if (entered)
                Monitor.Exit(_lock);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_MEMORY_COUNTERS_EX
    {
        public uint cb;
        public uint PageFaultCount;
        public nuint PeakWorkingSetSize;
        public nuint WorkingSetSize;
        public nuint QuotaPeakPagedPoolUsage;
        public nuint QuotaPagedPoolUsage;
        public nuint QuotaPeakNonPagedPoolUsage;
        public nuint QuotaNonPagedPoolUsage;
        public nuint PagefileUsage;
        public nuint PeakPagefileUsage;
        public nuint PrivateUsage;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE_ITEM_LARGE
    {
        public IntPtr szName;
        public uint CStatus;
        public long largeValue;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PDH_FMT_COUNTERVALUE_ITEM_DOUBLE
    {
        public IntPtr szName;
        public uint CStatus;
        public double doubleValue;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(IntPtr hProcess, out long lpCreationTime, out long lpExitTime, out long lpKernelTime, out long lpUserTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool K32GetProcessMemoryInfo(IntPtr Process, out PROCESS_MEMORY_COUNTERS_EX ppsmemCounters, uint cb);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhOpenQueryW(string? szDataSource, IntPtr dwUserData, out IntPtr phQuery);

    [DllImport("pdh.dll")]
    private static extern int PdhCloseQuery(IntPtr hQuery);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhAddEnglishCounterW(IntPtr hQuery, string szFullCounterPath, IntPtr dwUserData, out IntPtr phCounter);

    [DllImport("pdh.dll")]
    private static extern int PdhCollectQueryData(IntPtr hQuery);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhGetFormattedCounterArrayW(IntPtr hCounter, uint dwFormat, ref uint lpdwBufferSize, out uint lpdwItemCount, IntPtr ItemBuffer);
}
