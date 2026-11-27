using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DynamicIsland.Services;

public enum HotkeyAction
{
    PlayPause,
    Stop,
    NextTrack,
    PrevTrack,
    VolumeUp,
    VolumeDown,
    VoiceAssistant
}

/// <summary>
/// Глобальный перехват аппаратных мультимедийных Fn-клавиш клавиатуры и гарнитуры:
/// 1. RegisterHotKey для VK_MEDIA_* (без модификаторов)
/// 2. Низкоуровневый хук WH_KEYBOARD_LL (резерв, если VK_MEDIA_* занята другим приложением)
/// 3. Raw Input HID Consumer Control (UsagePage 0x0C, Usage 0x01 через HidP_GetUsages) + WM_APPCOMMAND
/// </summary>
public sealed class GlobalHotkeyService : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_INPUT = 0x00FF;
    private const int WM_HOTKEY = 0x0312;
    private const int WM_APPCOMMAND = 0x0319;

    private const int VK_MEDIA_NEXT_TRACK = 0xB0;
    private const int VK_MEDIA_PREV_TRACK = 0xB1;
    private const int VK_MEDIA_STOP = 0xB2;
    private const int VK_MEDIA_PLAY_PAUSE = 0xB3;

    private const uint MOD_ALT = 0x0001;
    private const uint MOD_NOREPEAT = 0x4000;
    private const int VK_V = 0x56;

    private const int HOTKEY_ID_MEDIA_PLAY_PAUSE = 9101;
    private const int HOTKEY_ID_MEDIA_PREV = 9102;
    private const int HOTKEY_ID_MEDIA_NEXT = 9103;
    private const int HOTKEY_ID_MEDIA_STOP = 9104;
    private const int HOTKEY_ID_VOICE_ASSISTANT = 9105;

    private const int APPCOMMAND_MEDIA_NEXTTRACK = 11;
    private const int APPCOMMAND_MEDIA_PREVIOUSTRACK = 12;
    private const int APPCOMMAND_MEDIA_STOP = 13;
    private const int APPCOMMAND_MEDIA_PLAY_PAUSE = 14;

    private const uint RID_INPUT = 0x10000003;
    private const uint RIM_TYPEHID = 2;
    private const uint RIDI_PREPARSEDDATA = 0x20000005;
    private const ushort HID_USAGE_PAGE_CONSUMER = 0x0C;
    private const ushort HID_USAGE_CONSUMER_CONTROL = 0x01;
    private const uint RIDEV_REMOVE = 0x00000001;
    private const uint RIDEV_INPUTSINK = 0x00000100;

    private const ushort USAGE_SCAN_NEXT_TRACK = 0x00B5;
    private const ushort USAGE_SCAN_PREV_TRACK = 0x00B6;
    private const ushort USAGE_STOP = 0x00B7;
    private const ushort USAGE_PLAY_PAUSE = 0x00CD;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    private readonly LowLevelKeyboardProc _hookProc;
    private readonly Func<bool> _shouldConsumeHardwareMediaKeys;
    private readonly HashSet<int> _registeredViaHotKey = new();
    private readonly Dictionary<IntPtr, HashSet<ushort>> _pressedHidUsages = new();

    private IntPtr _hookId = IntPtr.Zero;
    private IntPtr _hwnd = IntPtr.Zero;
    private HwndSource? _hwndSource;
    private bool _rawInputRegistered;
    private bool _voiceHotkeyEnabled = true;
    private bool _voiceHotkeyRegistered;
    private long _lastActionTick;
    private HotkeyAction? _lastAction;
    private bool _disposed;

    public event Action<HotkeyAction>? HotkeyTriggered;

    public GlobalHotkeyService(Func<bool> shouldConsumeHardwareMediaKeys)
    {
        _shouldConsumeHardwareMediaKeys = shouldConsumeHardwareMediaKeys;
        _hookProc = HookCallback;
    }

    public void Attach(Window window)
    {
        if (_disposed) return;

        try
        {
            _hwnd = new WindowInteropHelper(window).Handle;
            if (_hwnd != IntPtr.Zero)
            {
                _hwndSource = HwndSource.FromHwnd(_hwnd);
                _hwndSource?.AddHook(WndProc);

                TryRegisterMediaHotKey(HOTKEY_ID_MEDIA_PLAY_PAUSE, VK_MEDIA_PLAY_PAUSE);
                TryRegisterMediaHotKey(HOTKEY_ID_MEDIA_PREV, VK_MEDIA_PREV_TRACK);
                TryRegisterMediaHotKey(HOTKEY_ID_MEDIA_NEXT, VK_MEDIA_NEXT_TRACK);
                TryRegisterMediaHotKey(HOTKEY_ID_MEDIA_STOP, VK_MEDIA_STOP);
                ApplyVoiceHotkeyRegistration();

                var rid = new RAWINPUTDEVICE[]
                {
                    new()
                    {
                        usUsagePage = HID_USAGE_PAGE_CONSUMER,
                        usUsage = HID_USAGE_CONSUMER_CONTROL,
                        dwFlags = RIDEV_INPUTSINK,
                        hwndTarget = _hwnd
                    }
                };
                _rawInputRegistered = RegisterRawInputDevices(rid, (uint)rid.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
            }
        }
        catch { }

        if (_registeredViaHotKey.Count < 4)
        {
            try
            {
                using var curProcess = Process.GetCurrentProcess();
                using var curModule = curProcess.MainModule;
                var hMod = curModule != null ? GetModuleHandle(curModule.ModuleName) : IntPtr.Zero;
                _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, hMod, 0);
            }
            catch { }
        }
    }

    public void SetVoiceHotkeyEnabled(bool enabled)
    {
        _voiceHotkeyEnabled = enabled;
        ApplyVoiceHotkeyRegistration();
    }

    private void ApplyVoiceHotkeyRegistration()
    {
        if (_disposed || _hwnd == IntPtr.Zero) return;
        try
        {
            if (_voiceHotkeyEnabled)
            {
                if (!_voiceHotkeyRegistered)
                    _voiceHotkeyRegistered = RegisterHotKey(_hwnd, HOTKEY_ID_VOICE_ASSISTANT, MOD_ALT | MOD_NOREPEAT, VK_V);
            }
            else if (_voiceHotkeyRegistered)
            {
                _ = UnregisterHotKey(_hwnd, HOTKEY_ID_VOICE_ASSISTANT);
                _voiceHotkeyRegistered = false;
            }
        }
        catch { }
    }

    private void TryRegisterMediaHotKey(int id, int vk)
    {
        if (RegisterHotKey(_hwnd, id, MOD_NOREPEAT, (uint)vk))
            _registeredViaHotKey.Add(vk);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (id == HOTKEY_ID_VOICE_ASSISTANT)
            {
                handled = true;
                TryTriggerAction(HotkeyAction.VoiceAssistant);
                return IntPtr.Zero;
            }

            HotkeyAction? action = id switch
            {
                HOTKEY_ID_MEDIA_PLAY_PAUSE => HotkeyAction.PlayPause,
                HOTKEY_ID_MEDIA_PREV => HotkeyAction.PrevTrack,
                HOTKEY_ID_MEDIA_NEXT => HotkeyAction.NextTrack,
                HOTKEY_ID_MEDIA_STOP => HotkeyAction.Stop,
                _ => null
            };

            if (action.HasValue)
            {
                handled = true;
                if (_shouldConsumeHardwareMediaKeys())
                    TryTriggerAction(action.Value);
            }
        }
        else if (msg == WM_APPCOMMAND)
        {
            int cmd = (int)((lParam.ToInt64() >> 16) & 0xFFF);
            HotkeyAction? action = cmd switch
            {
                APPCOMMAND_MEDIA_NEXTTRACK => HotkeyAction.NextTrack,
                APPCOMMAND_MEDIA_PREVIOUSTRACK => HotkeyAction.PrevTrack,
                APPCOMMAND_MEDIA_STOP => HotkeyAction.Stop,
                APPCOMMAND_MEDIA_PLAY_PAUSE => HotkeyAction.PlayPause,
                _ => null
            };

            if (action.HasValue && _shouldConsumeHardwareMediaKeys())
            {
                handled = true;
                TryTriggerAction(action.Value);
            }
        }
        else if (msg == WM_INPUT)
        {
            try
            {
                ProcessRawInput(lParam);
            }
            catch { }
        }

        return IntPtr.Zero;
    }

    private void ProcessRawInput(IntPtr hRawInput)
    {
        uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        uint dwSize = 0;
        if (GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref dwSize, headerSize) == unchecked((uint)-1))
            return;
        if (dwSize <= headerSize || dwSize > 4096)
            return;

        IntPtr pData = Marshal.AllocHGlobal((int)dwSize);
        try
        {
            if (GetRawInputData(hRawInput, RID_INPUT, pData, ref dwSize, headerSize) == unchecked((uint)-1))
                return;

            var header = Marshal.PtrToStructure<RAWINPUTHEADER>(pData);
            if (header.dwType != RIM_TYPEHID || header.hDevice == IntPtr.Zero)
                return;

            int rawHidOffset = (int)headerSize;
            int rawHidStructSize = Marshal.SizeOf<RAWHID>();
            if (dwSize < (uint)(rawHidOffset + rawHidStructSize))
                return;

            IntPtr pHid = IntPtr.Add(pData, rawHidOffset);
            var rawHid = Marshal.PtrToStructure<RAWHID>(pHid);
            if (rawHid.dwSizeHid == 0 || rawHid.dwCount == 0)
                return;

            long payloadBytes = (long)rawHid.dwSizeHid * rawHid.dwCount;
            if (rawHidOffset + rawHidStructSize + payloadBytes > dwSize)
                return;

            uint preparsedSize = 0;
            GetRawInputDeviceInfo(header.hDevice, RIDI_PREPARSEDDATA, IntPtr.Zero, ref preparsedSize);
            if (preparsedSize == 0 || preparsedSize > 65536)
                return;

            IntPtr pPreparsed = Marshal.AllocHGlobal((int)preparsedSize);
            try
            {
                uint readSize = GetRawInputDeviceInfo(header.hDevice, RIDI_PREPARSEDDATA, pPreparsed, ref preparsedSize);
                if (readSize == unchecked((uint)-1) || readSize == 0)
                    return;

                var activeNow = new HashSet<ushort>();
                byte[] report = new byte[rawHid.dwSizeHid];
                ushort[] usageList = new ushort[32];
                IntPtr pReportBase = IntPtr.Add(pHid, rawHidStructSize);

                for (uint r = 0; r < rawHid.dwCount; r++)
                {
                    Marshal.Copy(IntPtr.Add(pReportBase, (int)(r * rawHid.dwSizeHid)), report, 0, (int)rawHid.dwSizeHid);
                    uint usageLen = (uint)usageList.Length;
                    int status = HidP_GetUsages(
                        HIDP_REPORT_TYPE.HidP_Input,
                        HID_USAGE_PAGE_CONSUMER,
                        0,
                        usageList,
                        ref usageLen,
                        pPreparsed,
                        report,
                        rawHid.dwSizeHid);

                    if (status >= 0)
                    {
                        for (uint u = 0; u < usageLen && u < usageList.Length; u++)
                        {
                            if (usageList[u] != 0)
                                activeNow.Add(usageList[u]);
                        }
                    }
                }

                if (!_pressedHidUsages.TryGetValue(header.hDevice, out var prevPressed))
                {
                    prevPressed = new HashSet<ushort>();
                    _pressedHidUsages[header.hDevice] = prevPressed;
                }

                foreach (ushort usage in activeNow)
                {
                    if (prevPressed.Contains(usage)) continue;

                    HotkeyAction? action = usage switch
                    {
                        USAGE_SCAN_NEXT_TRACK => HotkeyAction.NextTrack,
                        USAGE_SCAN_PREV_TRACK => HotkeyAction.PrevTrack,
                        USAGE_STOP => HotkeyAction.Stop,
                        USAGE_PLAY_PAUSE => HotkeyAction.PlayPause,
                        _ => null
                    };

                    if (action.HasValue && _shouldConsumeHardwareMediaKeys())
                        TryTriggerAction(action.Value);
                }

                prevPressed.Clear();
                foreach (ushort usage in activeNow)
                    prevPressed.Add(usage);
            }
            finally
            {
                Marshal.FreeHGlobal(pPreparsed);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(pData);
        }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
        {
            int flags = Marshal.ReadInt32(lParam, 8);
            if ((flags & 0x10) != 0) // LLKHF_INJECTED
                return CallNextHookEx(_hookId, nCode, wParam, lParam);

            int vkCode = Marshal.ReadInt32(lParam);
            if (_registeredViaHotKey.Contains(vkCode))
                return CallNextHookEx(_hookId, nCode, wParam, lParam);

            HotkeyAction? action = vkCode switch
            {
                VK_MEDIA_PLAY_PAUSE => HotkeyAction.PlayPause,
                VK_MEDIA_STOP => HotkeyAction.Stop,
                VK_MEDIA_NEXT_TRACK => HotkeyAction.NextTrack,
                VK_MEDIA_PREV_TRACK => HotkeyAction.PrevTrack,
                _ => null
            };

            if (action.HasValue && _shouldConsumeHardwareMediaKeys())
            {
                TryTriggerAction(action.Value);
                return (IntPtr)1;
            }
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private void TryTriggerAction(HotkeyAction action)
    {
        long now = Environment.TickCount64;
        if (_lastAction == action && now - _lastActionTick < 220)
            return;

        _lastAction = action;
        _lastActionTick = now;

        var disp = Application.Current?.Dispatcher;
        if (disp != null && !disp.HasShutdownStarted)
        {
            _ = disp.BeginInvoke(() => HotkeyTriggered?.Invoke(action));
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_hookId != IntPtr.Zero)
            {
                _ = UnhookWindowsHookEx(_hookId);
                _hookId = IntPtr.Zero;
            }
        }
        catch { }

        try
        {
            if (_rawInputRegistered)
            {
                var rid = new RAWINPUTDEVICE[]
                {
                    new()
                    {
                        usUsagePage = HID_USAGE_PAGE_CONSUMER,
                        usUsage = HID_USAGE_CONSUMER_CONTROL,
                        dwFlags = RIDEV_REMOVE,
                        hwndTarget = IntPtr.Zero
                    }
                };
                _ = RegisterRawInputDevices(rid, (uint)rid.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
                _rawInputRegistered = false;
            }
        }
        catch { }

        try
        {
            if (_hwnd != IntPtr.Zero)
            {
                _ = UnregisterHotKey(_hwnd, HOTKEY_ID_MEDIA_PLAY_PAUSE);
                _ = UnregisterHotKey(_hwnd, HOTKEY_ID_MEDIA_PREV);
                _ = UnregisterHotKey(_hwnd, HOTKEY_ID_MEDIA_NEXT);
                _ = UnregisterHotKey(_hwnd, HOTKEY_ID_MEDIA_STOP);
                if (_voiceHotkeyRegistered)
                {
                    _ = UnregisterHotKey(_hwnd, HOTKEY_ID_VOICE_ASSISTANT);
                    _voiceHotkeyRegistered = false;
                }
            }
            _hwndSource?.RemoveHook(WndProc);
        }
        catch { }
    }

    private enum HIDP_REPORT_TYPE
    {
        HidP_Input = 0,
        HidP_Output = 1,
        HidP_Feature = 2
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWHID
    {
        public uint dwSizeHid;
        public uint dwCount;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterRawInputDevices(
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] RAWINPUTDEVICE[] pRawInputDevices,
        uint uiNumDevices,
        uint cbSize);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(
        IntPtr hRawInput,
        uint uiCommand,
        IntPtr pData,
        ref uint pcbSize,
        uint cbSizeHeader);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputDeviceInfo(
        IntPtr hDevice,
        uint uiCommand,
        IntPtr pData,
        ref uint pcbSize);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern int HidP_GetUsages(
        HIDP_REPORT_TYPE ReportType,
        ushort UsagePage,
        ushort LinkCollection,
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 4)] ushort[] UsageList,
        ref uint UsageLength,
        IntPtr PreparsedData,
        byte[] Report,
        uint ReportLength);
}
