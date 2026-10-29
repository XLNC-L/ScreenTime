using System.Runtime.InteropServices;

namespace ScreenTime.Agent.Native;

/// <summary>Win32 消息结构（消息泵与托盘回调共用）。</summary>
[StructLayout(LayoutKind.Sequential)]
public struct Msg
{
    public IntPtr hwnd;
    public uint message;
    public IntPtr wParam;
    public IntPtr lParam;
    public uint time;
    public int pt_x;
    public int pt_y;
}

/// <summary>
/// 纯 Win32 托盘图标（Shell_NotifyIcon + 消息窗口回调 + 原生右键菜单）。
/// 采集器进程没有 WinUI/XAML 环境，不能使用 H.NotifyIcon.WinUI，
/// 这里用经典模式（不设 NOTIFYICON_VERSION），回调消息直接收 WM_LBUTTONUP / WM_RBUTTONUP。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const uint NIM_ADD = 0;
    private const uint NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 0x1;
    private const uint NIF_ICON = 0x2;
    private const uint NIF_TIP = 0x4;
    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x0010;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint MF_STRING = 0x0000;
    private const uint MF_SEPARATOR = 0x0800;
    private const int WM_DESTROY = 0x0002;

    private static readonly string WindowClassName = "ScreenTimeAgentTrayWnd_" + Guid.NewGuid().ToString("N");
    private static WndProcDelegate? _wndProcHolder; // 防 GC 回收

    private readonly IntPtr _hWnd;
    private readonly IntPtr _hMenu;
    private IntPtr _hIcon;
    private readonly uint _callbackMsg;

    public event Action? OpenRequested;
    public event Action? ExitRequested;

    /// <summary>回调消息号（消息泵据此识别托盘事件）。</summary>
    public uint CallbackMessageId => _callbackMsg;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public TrayIcon(string tooltip, string iconPath)
    {
        _callbackMsg = 0x8000 + 0x1; // WM_APP + 1
        _hWnd = CreateMessageWindow();
        _hIcon = LoadIconFromFile(iconPath);
        _hMenu = BuildMenu();

        var data = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hWnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = _callbackMsg,
            hIcon = _hIcon,
            szTip = tooltip
        };
        Shell_NotifyIcon(NIM_ADD, ref data);
    }

    private IntPtr CreateMessageWindow()
    {
        var hInstance = GetModuleHandleW(null);
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcHolder = WndProc),
            hInstance = hInstance,
            lpszClassName = WindowClassName
        };
        RegisterClassExW(ref wc);
        return CreateWindowExW(0, WindowClassName, "ScreenTime Agent", 0,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
    }

    private IntPtr LoadIconFromFile(string iconPath)
    {
        if (string.IsNullOrEmpty(iconPath) || !File.Exists(iconPath)) return IntPtr.Zero;
        var hIcon = LoadImageW(IntPtr.Zero, iconPath, IMAGE_ICON, 16, 16, LR_LOADFROMFILE);
        if (hIcon == IntPtr.Zero)
        {
            // 16px 失败时退回 32px（部分 ico 只含大尺寸）
            hIcon = LoadImageW(IntPtr.Zero, iconPath, IMAGE_ICON, 32, 32, LR_LOADFROMFILE);
        }
        return hIcon;
    }

    private IntPtr BuildMenu()
    {
        var menu = CreatePopupMenu();
        AppendMenuW(menu, MF_STRING, 1, "打开统计");
        AppendMenuW(menu, MF_SEPARATOR, 0, null);
        AppendMenuW(menu, MF_STRING, 2, "退出 ScreenTime");
        return menu;
    }

    /// <summary>由消息泵在收到 <see cref="CallbackMessageId"/> 时调用。</summary>
    public void HandleMessage(Msg msg)
    {
        if (msg.message != _callbackMsg) return;
        switch ((uint)msg.lParam)
        {
            case WM_LBUTTONUP:
                OpenRequested?.Invoke();
                break;
            case WM_RBUTTONUP:
                GetCursorPos(out var pt);
                SetForegroundWindow(_hWnd);
                var cmd = TrackPopupMenu(_hMenu, TPM_RIGHTBUTTON | TPM_RETURNCMD,
                    pt.X, pt.Y, 0, _hWnd, IntPtr.Zero);
                if (cmd == 1) OpenRequested?.Invoke();
                else if (cmd == 2) ExitRequested?.Invoke();
                break;
        }
    }

    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_DESTROY) PostQuitMessage(0);
        return DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    public void Dispose()
    {
        try
        {
            var data = new NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                hWnd = _hWnd,
                uID = 1
            };
            Shell_NotifyIcon(NIM_DELETE, ref data);
            if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
            if (_hMenu != IntPtr.Zero) DestroyMenu(_hMenu);
            if (_hWnd != IntPtr.Zero) DestroyWindow(_hWnd);
        }
        catch { }
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
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpdata);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassExW(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu,
        IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadImageW(IntPtr hinst, string lpszName, uint type,
        int cx, int cy, uint fuLoad);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, uint uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y,
        int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool PostQuitMessage(int nExitCode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);
}
