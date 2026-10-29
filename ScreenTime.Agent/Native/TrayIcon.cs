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
///
/// 回调**必须在 WndProc 里处理**：shell 用 PostMessage 时消息会经过队列，
/// 用 SendMessage 时则直接进 WndProc、根本不入队。
/// 早期版本在消息泵里拦截，一旦 shell 走 SendMessage 点击就完全没反应。
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
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_CONTEXTMENU = 0x007B;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONDOWN = 0x0204;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_RBUTTONDBLCLK = 0x0206;
    /// <summary>NOTIFYICON_VERSION_4 下左键回调是 NIN_SELECT（WM_USER + 0）。</summary>
    private const uint NIN_SELECT = 0x0400;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint MF_STRING = 0x0000;
    private const uint MF_SEPARATOR = 0x0800;

    private static readonly string WindowClassName = "ScreenTimeAgentTrayWnd_" + Guid.NewGuid().ToString("N");
    private static WndProcDelegate? _wndProcHolder; // 防 GC 回收
    private static TrayIcon? _current;              // WndProc 是静态的，靠它回调实例

    private readonly IntPtr _hWnd;
    private readonly IntPtr _hMenu;
    private IntPtr _hIcon;
    private readonly uint _callbackMsg;

    public event Action? OpenRequested;
    public event Action? ExitRequested;

    /// <summary>诊断日志（由 Program 接到 agent.log）。</summary>
    public Action<string>? Log { get; set; }

    /// <summary>回调消息号。</summary>
    public uint CallbackMessageId => _callbackMsg;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public TrayIcon(string tooltip, string iconPath, Action<string>? log = null)
    {
        Log = log;
        _callbackMsg = 0x8000 + 0x1; // WM_APP + 1
        _current = this;             // WndProc 是静态的，需要实例才能回调
        _hWnd = CreateMessageWindow();
        _hIcon = LoadIconFromFile(iconPath);
        _hMenu = BuildMenu();

        if (_hWnd == IntPtr.Zero)
        {
            Log?.Invoke("tray: 消息窗口创建失败，托盘不可用");
            return;
        }

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
        var ok = Shell_NotifyIcon(NIM_ADD, ref data);
        Log?.Invoke($"tray: NIM_ADD ok={ok} hwnd=0x{_hWnd.ToInt64():X} icon=0x{_hIcon.ToInt64():X} cbSize={data.cbSize}");
        if (!ok)
        {
            // 常见原因：资源管理器未就绪。重试一次，多数情况能成功。
            ok = Shell_NotifyIcon(NIM_ADD, ref data);
            Log?.Invoke($"tray: NIM_ADD retry ok={ok}");
        }

        // 记录图标在屏幕上的实际位置：便于排查「图标在哪 / 是否进了溢出区」
        var id = new NOTIFYICONIDENTIFIER
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONIDENTIFIER>(),
            hWnd = _hWnd,
            uID = 1
        };
        var hr = Shell_NotifyIconGetRect(ref id, out var rect);
        Log?.Invoke(hr == 0
            ? $"tray: rect=({rect.Left},{rect.Top},{rect.Right},{rect.Bottom})"
            : $"tray: GetRect failed hr=0x{hr:X8}（图标可能被收进溢出区）");
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
        var atom = RegisterClassExW(ref wc);
        if (atom == 0)
        {
            Log?.Invoke($"tray: RegisterClassEx 失败 err={Marshal.GetLastWin32Error()}");
            return IntPtr.Zero;
        }
        var hwnd = CreateWindowExW(0, WindowClassName, "ScreenTime Agent", 0,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
            Log?.Invoke($"tray: CreateWindowEx 失败 err={Marshal.GetLastWin32Error()}");
        return hwnd;
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

    /// <summary>
    /// 消息泵的兜底入口：正常路径是 WndProc 直接处理，
    /// 这里保留是为了兼容个别把回调投递到队列的实现。
    /// </summary>
    public void HandleMessage(Msg msg)
    {
        if (msg.message != _callbackMsg) return;
        HandleMouseMessage((uint)msg.lParam);
    }

    private void HandleMouseMessage(uint mouseMsg)
    {
        Log?.Invoke($"tray: callback 0x{mouseMsg:X}");
        switch (mouseMsg)
        {
            case WM_LBUTTONUP:
            case WM_LBUTTONDBLCLK:
            case NIN_SELECT:
                OpenRequested?.Invoke();
                break;
            case WM_RBUTTONUP:
            case WM_CONTEXTMENU:
                ShowContextMenu();
                break;
        }
    }

    /// <summary>
    /// 诊断用：给自己的消息窗口投递一次「左键点击」回调，
    /// 用来区分「消息没送到」和「收到了但没处理」。仅 --tray-selftest 时调用。
    /// </summary>
    public void SimulateClick()
    {
        Log?.Invoke($"tray: selftest post callback to 0x{_hWnd.ToInt64():X}");
        PostMessageW(_hWnd, _callbackMsg, (IntPtr)1, (IntPtr)WM_LBUTTONUP);
    }

    private void ShowContextMenu()
    {
        GetCursorPos(out var pt);
        // 弹菜单前必须把消息窗口设成前台，否则点击菜单外部不会自动收起
        SetForegroundWindow(_hWnd);
        var cmd = TrackPopupMenu(_hMenu, TPM_RIGHTBUTTON | TPM_RETURNCMD,
            pt.X, pt.Y, 0, _hWnd, IntPtr.Zero);
        // 官方要求：菜单结束后投递 WM_NULL，避免菜单残留
        PostMessageW(_hWnd, 0x0000, IntPtr.Zero, IntPtr.Zero);
        Log?.Invoke($"tray: menu cmd={cmd}");
        if (cmd == 1) OpenRequested?.Invoke();
        else if (cmd == 2) ExitRequested?.Invoke();
    }

    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_DESTROY)
        {
            PostQuitMessage(0);
            return IntPtr.Zero;
        }

        // 托盘回调：不管 shell 是 Post 还是 Send，都会走到这里
        if (_current is { } tray && msg == tray._callbackMsg)
        {
            tray.HandleMouseMessage((uint)lParam);
            return IntPtr.Zero;
        }

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
            if (_hWnd != IntPtr.Zero) Shell_NotifyIcon(NIM_DELETE, ref data);
            if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
            if (_hMenu != IntPtr.Zero) DestroyMenu(_hMenu);
            if (_hWnd != IntPtr.Zero) DestroyWindow(_hWnd);
        }
        catch { }
        if (ReferenceEquals(_current, this)) _current = null;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct NOTIFYICONIDENTIFIER
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public Guid guidItem;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpdata);

    [DllImport("shell32.dll")]
    private static extern int Shell_NotifyIconGetRect(ref NOTIFYICONIDENTIFIER identifier, out RECT iconLocation);

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
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

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
