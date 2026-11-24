using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DynamicIsland.Services;

public static class WindowBlurHelper
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    private const int DWMWCP_ROUND = 2;
    private const int DWMSBT_NONE = 0;
    private const int DWMSBT_TRANSIENTWINDOW = 3; // Acrylic

    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_DISABLED = 0;
    private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCAPTION = 2;

    public static void ApplyAcrylic(Window window, bool enable)
        => ApplyDarkAcrylic(window, enable, extendFrame: false);

    public static void ApplyDarkAcrylic(Window window, bool enable, bool extendFrame = false)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        try
        {
            int dark = 1;
            _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

            int corner = DWMWCP_ROUND;
            _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

            if (extendFrame)
            {
                var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                _ = DwmExtendFrameIntoClientArea(hwnd, ref margins);
            }

            // Windows 11 Build 22621+ поддерживает DWMWA_SYSTEMBACKDROP_TYPE
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
            {
                int backdrop = enable ? DWMSBT_TRANSIENTWINDOW : DWMSBT_NONE;
                int hr = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
                if (hr == 0)
                    return;
            }

            // Фолбэк для более ранних сборок Windows 10 / 11 через SetWindowCompositionAttribute
            var policy = new AccentPolicy
            {
                AccentState = enable ? ACCENT_ENABLE_ACRYLICBLURBEHIND : ACCENT_DISABLED,
                AccentFlags = enable ? 2 : 0,
                GradientColor = enable ? unchecked((int)0x660A0D16) : 0,
                AnimationId = 0
            };

            int size = Marshal.SizeOf<AccentPolicy>();
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(policy, ptr, false);
                var data = new WindowCompositionAttributeData
                {
                    Attribute = WCA_ACCENT_POLICY,
                    Data = ptr,
                    SizeOfData = size
                };
                _ = SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        catch { }
    }

    public static void StartWindowDrag(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        try
        {
            _ = ReleaseCapture();
            _ = SendMessage(hwnd, WM_NCLBUTTONDOWN, (IntPtr)HTCAPTION, IntPtr.Zero);
        }
        catch { }
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    public static Rect GetMonitorWorkArea(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd != IntPtr.Zero)
            {
                var hMonitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                if (hMonitor != IntPtr.Zero)
                {
                    var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                    if (GetMonitorInfoW(hMonitor, ref info))
                    {
                        var topLeft = new Point(info.rcWork.Left, info.rcWork.Top);
                        var bottomRight = new Point(info.rcWork.Right, info.rcWork.Bottom);
                        var source = PresentationSource.FromVisual(window);
                        if (source?.CompositionTarget != null)
                        {
                            var matrix = source.CompositionTarget.TransformFromDevice;
                            topLeft = matrix.Transform(topLeft);
                            bottomRight = matrix.Transform(bottomRight);
                        }
                        return new Rect(topLeft, bottomRight);
                    }
                }
            }
        }
        catch { }

        return SystemParameters.WorkArea;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS pMarInset);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);
}
