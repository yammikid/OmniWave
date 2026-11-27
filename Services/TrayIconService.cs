using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;

namespace DynamicIsland.Services;

public class TrayIconService : IDisposable
{
    private const int WM_TRAYICON = 0x8001;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_CONTEXTMENU = 0x007B;

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_DELETE = 0x00000002;

    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;

    private const uint TrayIconId = 1;

    private readonly Window _owner;
    private readonly ContextMenu _menu;
    private readonly Action _onOpenRequested;
    private readonly uint _taskbarCreatedMsg;

    private HwndSource? _hwndSource;
    private IntPtr _hwnd;
    private IntPtr _hIcon;
    private bool _ownsIcon;
    private bool _iconAdded;
    private bool _disposed;

    public TrayIconService(Window owner, ContextMenu menu, Action onOpenRequested)
    {
        _owner = owner;
        _menu = menu;
        _onOpenRequested = onOpenRequested;
        _taskbarCreatedMsg = RegisterWindowMessageW("TaskbarCreated");

        CreateTrayIconHandle();

        var hwnd = new WindowInteropHelper(_owner).Handle;
        if (hwnd != IntPtr.Zero)
            AttachAndAdd(hwnd);
        else
            _owner.SourceInitialized += OnOwnerSourceInitialized;
    }

    private void OnOwnerSourceInitialized(object? sender, EventArgs e)
    {
        _owner.SourceInitialized -= OnOwnerSourceInitialized;
        if (_disposed) return;

        var hwnd = new WindowInteropHelper(_owner).Handle;
        if (hwnd != IntPtr.Zero)
            AttachAndAdd(hwnd);
    }

    private void AttachAndAdd(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource?.AddHook(WndProc);
        AddIcon();
    }

    private void AddIcon()
    {
        if (_hwnd == IntPtr.Zero || _disposed) return;

        var nid = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = TrayIconId,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _hIcon,
            szTip = "OmniWave"
        };

        _iconAdded = Shell_NotifyIconW(NIM_ADD, ref nid);
    }

    private void RemoveIcon()
    {
        if (!_iconAdded || _hwnd == IntPtr.Zero) return;

        var nid = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = TrayIconId
        };

        Shell_NotifyIconW(NIM_DELETE, ref nid);
        _iconAdded = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (_taskbarCreatedMsg != 0 && msg == (int)_taskbarCreatedMsg)
        {
            _iconAdded = false;
            AddIcon();
            return IntPtr.Zero;
        }

        if (msg == WM_TRAYICON && (uint)wParam.ToInt64() == TrayIconId)
        {
            int mouseMsg = lParam.ToInt32() & 0xFFFF;
            if (mouseMsg == WM_LBUTTONUP || mouseMsg == WM_LBUTTONDBLCLK)
            {
                _onOpenRequested();
                handled = true;
            }
            else if (mouseMsg == WM_RBUTTONUP || mouseMsg == WM_CONTEXTMENU)
            {
                ShowTrayContextMenu();
                handled = true;
            }
        }

        return IntPtr.Zero;
    }

    private void ShowTrayContextMenu()
    {
        if (_hwnd != IntPtr.Zero)
            SetForegroundWindow(_hwnd);

        _menu.Placement = PlacementMode.MousePoint;
        _menu.IsOpen = true;
    }

    private void CreateTrayIconHandle()
    {
        if (TryLoadEmbeddedTrayIconHandle())
            return;

        const int size = 16;
        // Создаём 16x16 ARGB-иконку (тёмный скруглённый фон с золотой нотой/искрой OmniWave)
        var pixels = new int[size * size];

        int bg = unchecked((int)0xFF14151E);
        int border = unchecked((int)0xFF3A3C4A);
        int gold = unchecked((int)0xFFFFCC00);
        int goldBright = unchecked((int)0xFFFFE57F);

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                bool corner = (x <= 1 || x >= size - 2) && (y <= 1 || y >= size - 2);
                if (corner)
                {
                    pixels[y * size + x] = 0;
                    continue;
                }

                bool isEdge = x == 0 || x == size - 1 || y == 0 || y == size - 1 ||
                              ((x == 2 || x == size - 3) && (y == 1 || y == size - 2));
                pixels[y * size + x] = isEdge ? border : bg;
            }
        }

        // Рисуем стилизованную звуковую волну / ноту в центре
        void Plot(int x, int y, int col)
        {
            if (x is >= 0 and < size && y is >= 0 and < size)
                pixels[y * size + x] = col;
        }

        // Левая полоса волны
        for (int y = 6; y <= 10; y++) Plot(4, y, gold);
        // Центральный пик
        for (int y = 3; y <= 12; y++) Plot(7, y, goldBright);
        for (int y = 4; y <= 11; y++) Plot(8, y, gold);
        // Правая полоса волны
        for (int y = 5; y <= 11; y++) Plot(11, y, gold);

        var maskBits = new byte[size * 2]; // 16x16 1-bpp маска (0 = непрозрачные пиксели по альфе)
        IntPtr hColor = CreateBitmap(size, size, 1, 32, pixels);
        IntPtr hMask = CreateBitmap(size, size, 1, 1, maskBits);

        if (hColor != IntPtr.Zero && hMask != IntPtr.Zero)
        {
            var iconInfo = new ICONINFO
            {
                fIcon = true,
                hbmColor = hColor,
                hbmMask = hMask
            };
            _hIcon = CreateIconIndirect(ref iconInfo);
            _ownsIcon = _hIcon != IntPtr.Zero;
        }

        if (hColor != IntPtr.Zero) DeleteObject(hColor);
        if (hMask != IntPtr.Zero) DeleteObject(hMask);

        if (_hIcon == IntPtr.Zero)
        {
            _hIcon = LoadIconW(IntPtr.Zero, (IntPtr)32512); // IDI_APPLICATION fallback
            _ownsIcon = false;
        }
    }

    private const int SM_CXSMICON = 49;

    private bool TryLoadEmbeddedTrayIconHandle()
    {
        try
        {
            var res = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/omniwave.ico", UriKind.Absolute));
            if (res?.Stream == null) return false;

            using var ms = new MemoryStream();
            res.Stream.CopyTo(ms);
            res.Stream.Dispose();
            byte[] icoBytes = ms.ToArray();
            if (icoBytes.Length < 6) return false;

            int count = BitConverter.ToUInt16(icoBytes, 4);
            if (count <= 0 || icoBytes.Length < 6 + count * 16) return false;

            int targetSize = Math.Clamp(GetSystemMetrics(SM_CXSMICON), 16, 64);
            int bestIndex = -1;
            int bestScore = int.MaxValue;

            for (int i = 0; i < count; i++)
            {
                int off = 6 + i * 16;
                int w = icoBytes[off] == 0 ? 256 : icoBytes[off];
                int h = icoBytes[off + 1] == 0 ? 256 : icoBytes[off + 1];
                int size = Math.Max(w, h);
                int bytesInRes = BitConverter.ToInt32(icoBytes, off + 8);
                int imgOffset = BitConverter.ToInt32(icoBytes, off + 12);
                if (bytesInRes <= 0 || imgOffset < 0 || imgOffset + bytesInRes > icoBytes.Length)
                    continue;

                int score = size >= targetSize ? (size - targetSize) : (1000 + (targetSize - size));
                if (score < bestScore)
                {
                    bestScore = score;
                    bestIndex = i;
                }
            }

            if (bestIndex < 0) return false;

            int bestOff = 6 + bestIndex * 16;
            int resSize = BitConverter.ToInt32(icoBytes, bestOff + 8);
            int resOffset = BitConverter.ToInt32(icoBytes, bestOff + 12);
            var entryBytes = new byte[resSize];
            Buffer.BlockCopy(icoBytes, resOffset, entryBytes, 0, resSize);

            IntPtr hIcon = CreateIconFromResourceEx(entryBytes, (uint)entryBytes.Length, true, 0x00030000, targetSize, targetSize, 0);
            if (hIcon == IntPtr.Zero) return false;

            _hIcon = hIcon;
            _ownsIcon = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        RemoveIcon();
        _hwndSource?.RemoveHook(WndProc);
        _hwndSource = null;

        if (_ownsIcon && _hIcon != IntPtr.Zero)
        {
            DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string lpString);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref ICONINFO piconinfo);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int nWidth, int nHeight, uint cPlanes, uint cBitsPerPel, [In] int[] lpvBits);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int nWidth, int nHeight, uint cPlanes, uint cBitsPerPel, [In] byte[] lpvBits);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateIconFromResourceEx(
        byte[] presbits,
        uint dwResSize,
        [MarshalAs(UnmanagedType.Bool)] bool fIcon,
        uint dwVer,
        int cxDesired,
        int cyDesired,
        uint flags);
}
