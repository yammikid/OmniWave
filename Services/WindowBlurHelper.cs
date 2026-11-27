using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace DynamicIsland.Services;

public static class WindowBlurHelper
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_BORDER_COLOR = 34;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    private const int DWMWCP_ROUND = 2;
    private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);
    private const int DWMSBT_NONE = 0;

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_LAYERED = 0x00080000;
    private const uint LWA_ALPHA = 0x00000002;

    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int HTCAPTION = 2;

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;

    private static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);
    private static readonly IntPtr HWND_BROADCAST = new(0xFFFF);

    /// <summary>
    /// Включает Per-Monitor DPI v2 для чёткого масштабирования на Windows 10 (1703+) и Windows 11.
    /// </summary>
    public static void EnablePerMonitorDpiAwareness()
    {
        try
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 15063))
                _ = SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        }
        catch { }
    }

    public static uint RegisterRestoreMessage()
    {
        try
        {
            return RegisterWindowMessageW("OmniWave_RestoreWindow_Msg");
        }
        catch
        {
            return 0;
        }
    }

    public static void BroadcastRestoreMessage(uint msgId)
    {
        if (msgId == 0) return;
        try
        {
            _ = PostMessageW(HWND_BROADCAST, msgId, IntPtr.Zero, IntPtr.Zero);
        }
        catch { }
    }

    /// <summary>
    /// Сбрасывает неиспользуемые физические страницы памяти процесса при уходе в трей.
    /// </summary>
    public static void TrimWorkingSet()
    {
        try
        {
            _ = SetProcessWorkingSetSize(GetCurrentProcess(), new IntPtr(-1), new IntPtr(-1));
        }
        catch { }
    }

    /// <summary>
    /// Возвращает true на Windows 11 (Build 22000+), где DWM нативно скругляет углы (8px DIP).
    /// </summary>
    public static bool IsWindows11OrLater => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    /// <summary>
    /// Применяет тёмный стиль, отключённую системную рамку Win11 и нативные скруглённые углы без DWM/Acrylic-блюра (Zero-Blur).
    /// </summary>
    public static void ApplyChromaWindowStyle(Window window, int cornerRadiusPx = 16)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        try
        {
            int dark = 1;
            _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

            if (IsWindows11OrLater)
            {
                int corner = DWMWCP_ROUND;
                _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

                int borderNone = DWMWA_COLOR_NONE;
                _ = DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref borderNone, sizeof(int));

                if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
                {
                    int backdropNone = DWMSBT_NONE;
                    _ = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdropNone, sizeof(int));
                }

                // На Windows 11 нативный DWM отвечает за скругление, отдельный регион не нужен
                _ = SetWindowRgn(hwnd, IntPtr.Zero, false);
            }
            else
            {
                UpdateWin10RoundedRegion(window, cornerRadiusPx);
            }
        }
        catch { }
    }

    /// <summary>
    /// Обновляет CreateRoundRectRgn для скругления краёв окна на Windows 10 (&lt; Build 22000).
    /// </summary>
    public static void UpdateWin10RoundedRegion(Window window, int cornerRadiusPx)
    {
        if (IsWindows11OrLater)
            return;

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        try
        {
            if (!GetWindowRect(hwnd, out var rc)) return;
            int w = rc.Right - rc.Left;
            int h = rc.Bottom - rc.Top;
            if (w <= 0 || h <= 0) return;

            var (scaleX, scaleY) = GetDpiScale(window);
            int diamX = Math.Max(4, (int)Math.Round(cornerRadiusPx * 2.0 * scaleX));
            int diamY = Math.Max(4, (int)Math.Round(cornerRadiusPx * 2.0 * scaleY));
            IntPtr hrgn = CreateRoundRectRgn(0, 0, w + 1, h + 1, diamX, diamY);
            if (hrgn == IntPtr.Zero) return;

            if (SetWindowRgn(hwnd, hrgn, true) == 0)
                _ = DeleteObject(hrgn);
        }
        catch { }
    }

    /// <summary>
    /// Снимает кастомный регион скругления (например, при разворачивании окна на весь экран на Windows 10).
    /// </summary>
    public static void ClearRoundedRegion(Window window)
    {
        if (IsWindows11OrLater) return;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        try { _ = SetWindowRgn(hwnd, IntPtr.Zero, true); } catch { }
    }

    /// <summary>
    /// Настраивает общую прозрачность окна (0.75–1.0) через Win32 WS_EX_LAYERED / LWA_ALPHA
    /// для окон с AllowsTransparency="False" без использования DWM blur.
    /// </summary>
    public static void SetWindowAlpha(Window window, double opacity01)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        try
        {
            double clamped = Math.Clamp(double.IsNaN(opacity01) ? 1.0 : opacity01, 0.75, 1.0);
            nint exStyle = GetWindowLongPtrCompat(hwnd, GWL_EXSTYLE);

            if (clamped >= 0.999)
            {
                if ((exStyle & WS_EX_LAYERED) != 0)
                {
                    _ = SetWindowLongPtrCompat(hwnd, GWL_EXSTYLE, exStyle & ~WS_EX_LAYERED);
                    _ = SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                        SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
                }
                return;
            }

            if ((exStyle & WS_EX_LAYERED) == 0)
            {
                _ = SetWindowLongPtrCompat(hwnd, GWL_EXSTYLE, exStyle | WS_EX_LAYERED);
            }

            byte alpha = (byte)Math.Clamp((int)Math.Round(clamped * 255.0), 190, 255);
            _ = SetLayeredWindowAttributes(hwnd, 0, alpha, LWA_ALPHA);
        }
        catch { }
    }

    /// <summary>
    /// Возвращает рабочую область ближайшего монитора в DIP-координатах WPF.
    /// </summary>
    public static Rect GetMonitorWorkAreaForWindow(Window window)
    {
        try
        {
            var (scaleX, scaleY) = GetDpiScale(window);
            var hwnd = new WindowInteropHelper(window).Handle;
            RECT rc;
            if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out rc))
            {
                double w = window.Width > 0 ? window.Width : 348.0;
                double h = window.Height > 0 ? window.Height : 84.0;
                rc = new RECT
                {
                    Left = (int)Math.Round(window.Left * scaleX),
                    Top = (int)Math.Round(window.Top * scaleY),
                    Right = (int)Math.Round((window.Left + w) * scaleX),
                    Bottom = (int)Math.Round((window.Top + h) * scaleY)
                };
            }

            IntPtr hMonitor = MonitorFromRect(ref rc, MONITOR_DEFAULTTONEAREST);
            if (hMonitor != IntPtr.Zero)
            {
                var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfoW(hMonitor, ref info))
                {
                    var source = PresentationSource.FromVisual(window);
                    Point tl = new(info.rcWork.Left, info.rcWork.Top);
                    Point br = new(info.rcWork.Right, info.rcWork.Bottom);
                    if (source?.CompositionTarget != null)
                    {
                        var inv = source.CompositionTarget.TransformFromDevice;
                        tl = inv.Transform(tl);
                        br = inv.Transform(br);
                    }
                    else
                    {
                        tl = new Point(tl.X / scaleX, tl.Y / scaleY);
                        br = new Point(br.X / scaleX, br.Y / scaleY);
                    }
                    return new Rect(tl, br);
                }
            }
        }
        catch { }

        return SystemParameters.WorkArea;
    }

    public static bool TryGetCursorScreenDipPosition(Window window, out Point dipPoint)
    {
        dipPoint = default;
        try
        {
            if (!GetCursorPos(out var pt))
                return false;

            var source = PresentationSource.FromVisual(window);
            if (source?.CompositionTarget != null)
            {
                dipPoint = source.CompositionTarget.TransformFromDevice.Transform(new Point(pt.X, pt.Y));
                return true;
            }

            var (scaleX, scaleY) = GetDpiScale(window);
            dipPoint = new Point(pt.X / scaleX, pt.Y / scaleY);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Удерживает координаты окна в пределах рабочей области ближайшего видимого монитора
    /// (включая мультимониторные конфигурации и отключение внешнего дисплея).
    /// </summary>
    public static (double Left, double Top) ClampToVisibleMonitors(Window window, double left, double top, double width, double height)
    {
        try
        {
            var (scaleX, scaleY) = GetDpiScale(window);
            var rc = new RECT
            {
                Left = (int)Math.Round(left * scaleX),
                Top = (int)Math.Round(top * scaleY),
                Right = (int)Math.Round((left + width) * scaleX),
                Bottom = (int)Math.Round((top + height) * scaleY)
            };

            IntPtr hMonitor = MonitorFromRect(ref rc, MONITOR_DEFAULTTONEAREST);
            if (hMonitor != IntPtr.Zero)
            {
                var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfoW(hMonitor, ref info))
                {
                    var source = PresentationSource.FromVisual(window);
                    Point tl = new(info.rcWork.Left, info.rcWork.Top);
                    Point br = new(info.rcWork.Right, info.rcWork.Bottom);
                    if (source?.CompositionTarget != null)
                    {
                        var inv = source.CompositionTarget.TransformFromDevice;
                        tl = inv.Transform(tl);
                        br = inv.Transform(br);
                    }
                    else
                    {
                        tl = new Point(tl.X / scaleX, tl.Y / scaleY);
                        br = new Point(br.X / scaleX, br.Y / scaleY);
                    }

                    var work = new Rect(tl, br);
                    double clampedLeft = width >= work.Width
                        ? work.Left
                        : Math.Clamp(left, work.Left, work.Right - width);
                    double clampedTop = height >= work.Height
                        ? work.Top
                        : Math.Clamp(top, work.Top, work.Bottom - height);
                    return (clampedLeft, clampedTop);
                }
            }
        }
        catch { }

        var fallback = SystemParameters.WorkArea;
        double fLeft = width >= fallback.Width ? fallback.Left : Math.Clamp(left, fallback.Left, fallback.Right - width);
        double fTop = height >= fallback.Height ? fallback.Top : Math.Clamp(top, fallback.Top, fallback.Bottom - height);
        return (fLeft, fTop);
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

    private static (double ScaleX, double ScaleY) GetDpiScale(Window window)
    {
        try
        {
            var source = PresentationSource.FromVisual(window);
            if (source?.CompositionTarget != null)
            {
                var m = source.CompositionTarget.TransformToDevice;
                return (Math.Max(0.5, m.M11), Math.Max(0.5, m.M22));
            }
            var dpi = VisualTreeHelper.GetDpi(window);
            return (Math.Max(0.5, dpi.DpiScaleX), Math.Max(0.5, dpi.DpiScaleY));
        }
        catch
        {
            return (1.0, 1.0);
        }
    }

    private static nint GetWindowLongPtrCompat(IntPtr hwnd, int nIndex)
        => IntPtr.Size == 8 ? GetWindowLongPtr64(hwnd, nIndex) : GetWindowLong32(hwnd, nIndex);

    private static nint SetWindowLongPtrCompat(IntPtr hwnd, int nIndex, nint newLong)
        => IntPtr.Size == 8 ? SetWindowLongPtr64(hwnd, nIndex, newLong) : SetWindowLong32(hwnd, nIndex, (int)newLong);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
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

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int dwAttribute, ref int pvAttribute, int cbAttribute);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int nLeftRect, int nTopRect, int nRightRect, int nBottomRect, int nWidthEllipse, int nHeightEllipse);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, [MarshalAs(UnmanagedType.Bool)] bool bRedraw);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
    private static extern nint GetWindowLongPtr64(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
    private static extern nint SetWindowLongPtr64(IntPtr hWnd, int nIndex, nint dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect([In] ref RECT lprc, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string lpString);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);
}
