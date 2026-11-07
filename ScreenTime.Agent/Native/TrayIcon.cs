using System.Runtime.InteropServices;
using ScreenTime.Services;

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
///
/// 图标丢失的三条防线（v1.3.6 补）：
///  1. 登记时失败会立刻重试，并把失败写日志；
///  2. 监听外壳的 <c>TaskbarCreated</c> 广播——Explorer 重启后所有托盘图标都会被清除，
///     只有收到这条广播重新 NIM_ADD 才能恢复；
///  3. 看门狗每 5 秒比对一次 <c>Shell_TrayWnd</c> 的窗口句柄，句柄变了（外壳重启）就重新登记。
/// 早期版本只有构造时的一次 NIM_ADD，一旦失败（开机时 Explorer 还没起好）或
/// Explorer 中途重启，图标就永久消失，而进程还在后台跑。
///
/// **所有 shell 调用都必须在消息线程上做**（WndProc 里）。看门狗只负责往自己的窗口
/// PostMessage，绝不在线程池线程直接调 Shell_NotifyIcon：Explorer 被拆掉的那一瞬间
/// 从别的线程调进去会让整个进程静默自杀（实测 3 秒内进程消失、无任何异常日志）。
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const uint NIM_ADD = 0;
    private const uint NIM_MODIFY = 1;
    private const uint NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 0x1;
    private const uint NIF_ICON = 0x2;
    private const uint NIF_TIP = 0x4;
    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x0010;
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_NCMOUSEMOVE = 0x00A0;
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
    /// <summary>看门狗节拍消息（WM_APP + 2），由线程池投递到消息线程。</summary>
    private const uint WM_WATCHDOG = 0x8000 + 0x2;
    /// <summary>更新提示文字（WM_APP + 3），同样由其他线程投递到消息线程。</summary>
    private const uint WM_UPDATE_TIP = 0x8000 + 0x3;

    private static readonly string WindowClassName = "ScreenTimeAgentTrayWnd_" + Guid.NewGuid().ToString("N");
    private static WndProcDelegate? _wndProcHolder; // 防 GC 回收
    private static TrayIcon? _current;              // WndProc 是静态的，靠它回调实例

    /// <summary>外壳重启后广播的自定义消息号（各进程用同一个字符串登记，值一致）。</summary>
    private static readonly uint TaskbarCreatedMsg = RegisterWindowMessageW("TaskbarCreated");

    private readonly IntPtr _hWnd;
    private readonly IntPtr _hMenu;
    private IntPtr _hIcon;
    private readonly uint _callbackMsg;
    private string _tooltip;
    private readonly object _iconGate = new();
    private Timer? _watchdog;
    private bool _iconPresent;
    private bool _disposed;
    /// <summary>当前暂停截止时间；只用于菜单文案，不参与采集。</summary>
    private DateTime? _pausedUntil;

    public event Action? OpenRequested;
    public event Action? ExitRequested;

    /// <summary>右键菜单请求暂停统计：参数为分钟数，0 表示恢复。</summary>
    public event Action<int>? PauseRequested;

    /// <summary>诊断日志（由 Program 接到 agent.log）。</summary>
    public Action<string>? Log { get; set; }

    /// <summary>回调消息号。</summary>
    public uint CallbackMessageId => _callbackMsg;

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public TrayIcon(string tooltip, string iconPath, Action<string>? log = null)
    {
        Log = log;
        _tooltip = tooltip ?? "";
        _callbackMsg = 0x8000 + 0x1; // WM_APP + 1
        _current = this;             // WndProc 是静态的，需要实例才能回调
        EnableDarkMenus();
        _hWnd = CreateMessageWindow();
        // 弹菜单的窗口本身要先有深色主题，弹出菜单才会继承；
        // 只调进程级的 SetPreferredAppMode 不够（实测菜单仍是浅色经典样式）。
        if (_hWnd != IntPtr.Zero) ApplyDarkWindowTheme(_hWnd);
        _hIcon = LoadIconFromFile(iconPath);
        _hMenu = BuildMenu();

        if (_hWnd == IntPtr.Zero)
        {
            Log?.Invoke("tray: 消息窗口创建失败，托盘不可用");
            return;
        }
        if (_hIcon == IntPtr.Zero)
            Log?.Invoke($"tray: 图标加载失败 path={iconPath}（会显示成空白图标）");

        // 首次登记 + 一次即时重试；失败的兜底交给看门狗（开机瞬间 Explorer 常还没起好）
        AddIcon("startup");
        if (!_iconPresent)
        {
            Thread.Sleep(200);
            AddIcon("startup-retry");
        }

        LogRect();

        // 兜底重试：只在「确认当前没有图标」时才去登记（AddIcon 的前提由 EnsureIcon 把关）。
        // 周期 2 秒只是给自己的窗口 PostMessage，shell 调用发生在消息线程；
        // 关键是**图标还在时绝不去碰 shell**——Explorer 正在被拆的那一瞬间调用
        // Shell_NotifyIcon 会让整个进程静默退出（实测约 1/5 概率）。
        _watchdog = new Timer(_ => PostWatchdogTick(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    /// <summary>按当前状态组装 NOTIFYICONDATA（必须用完整结构，cbSize 少了某些系统会拒绝登记）。</summary>
    private NOTIFYICONDATA BuildData(uint flags) => new()
    {
        cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hWnd,
        uID = 1,
        uFlags = flags,
        uCallbackMessage = _callbackMsg,
        hIcon = _hIcon,
        szTip = _tooltip,
        szInfo = "",
        szInfoTitle = ""
    };

    /// <summary>登记托盘图标；返回是否成功。状态没变化时不重复写日志，避免看门狗刷屏。</summary>
    private bool AddIcon(string reason)
    {
        if (_hWnd == IntPtr.Zero || _disposed) return false;

        bool ok;
        lock (_iconGate)
        {
            var data = BuildData(NIF_MESSAGE | NIF_ICON | NIF_TIP);
            ok = Shell_NotifyIcon(NIM_ADD, ref data);

            // 外壳里已经有同 ID 的条目时，NIM_ADD 会返回 FALSE，但图标其实还在。
            // 用 NIM_MODIFY 探一下：能改说明它存在，否则会陷入「一直以为丢了、一直重加」的死循环。
            if (!ok)
            {
                var probe = BuildData(NIF_TIP);
                ok = Shell_NotifyIcon(NIM_MODIFY, ref probe);
            }

            if (ok) _iconPresent = true;
        }

        if (ok)
        {
            if (!_iconPresentLogged)
            {
                Log?.Invoke($"tray: 图标已登记 ({reason}) hwnd=0x{_hWnd.ToInt64():X} icon=0x{_hIcon.ToInt64():X}");
                _iconPresentLogged = true;
            }
        }
        else
        {
            if (_iconPresentLogged || reason != "watchdog-silent")
                Log?.Invoke($"tray: 图标登记失败 ({reason}) err={Marshal.GetLastWin32Error()}，等看门狗重试");
            _iconPresentLogged = false;
        }
        return ok;
    }

    private bool _iconPresentLogged;

    /// <summary>
    /// 看门狗（线程池侧）：只往自己的消息窗口投递一条消息，不碰任何 shell API。
    /// </summary>
    private void PostWatchdogTick()
    {
        try
        {
            if (_disposed || _hWnd == IntPtr.Zero) return;
            PostMessageW(_hWnd, WM_WATCHDOG, IntPtr.Zero, IntPtr.Zero);
        }
        catch (Exception ex) { Log?.Invoke("tray: watchdog post ERROR " + ex.Message); }
    }

    /// <summary>
    /// 兜底：只在确认「当前没有图标」时才补登记。图标还在就什么都不做——
    /// 这样 Explorer 被拆的那一瞬间不会有任何 shell 调用，进程不会被带走。
    /// 外壳还没起来时（Shell_TrayWnd 不存在）先跳过，交给下一个节拍。
    /// </summary>
    private void EnsureIcon(string reason)
    {
        if (_disposed || _hWnd == IntPtr.Zero) return;

        var shellReady = FindWindowW("Shell_TrayWnd", null) != IntPtr.Zero;
        if (!TrayRecoveryPolicy.ShouldRegister(_iconPresent, shellReady, _disposed)) return;

        Log?.Invoke($"tray: 图标缺失，重新登记 ({reason})");
        _iconPresentLogged = false;
        AddIcon(reason);
    }

    /// <summary>Explorer 重启后外壳会向所有顶层窗口广播 TaskbarCreated。</summary>
    private void OnTaskbarCreated()
    {
        Log?.Invoke("tray: 收到 TaskbarCreated（外壳重启），重新登记图标");
        _iconPresent = false;              // 外壳已清空所有托盘图标，必须重登
        EnsureIcon("taskbar-created");
    }

    private void LogRect()
    {
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
            lpszClassName = WindowClassName,
            lpszMenuName = ""
        };
        var atom = RegisterClassExW(ref wc);
        if (atom == 0)
        {
            Log?.Invoke($"tray: RegisterClassEx 失败 err={Marshal.GetLastWin32Error()}");
            return IntPtr.Zero;
        }
        // WS_EX_TOOLWINDOW(0x80)：不带的话这个隐藏的消息窗口可能被当成普通窗口，
        // 任务栏会多出一个 ScreenTime 条目
        var hwnd = CreateWindowExW(0x00000080, WindowClassName, "ScreenTime Agent", 0,
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

    /// <summary>
    /// 把进程内的原生菜单切到 Windows 11 深色 Fluent 外观（圆角、深色底、系统字体），
    /// 和 WinUI3 的 MenuFlyout 是同一套视觉语言。
    ///
    /// 走 uxtheme 的 SetPreferredAppMode（导出序号 135）/ FlushMenuThemes（136），
    /// 这两个是未公开导出：取不到就静默跳过，菜单退回系统默认外观，功能不受影响。
    /// 真要用 MenuFlyout 得有一个 WinUI 窗口，而常驻托盘的是纯 Win32 的采集进程，
    /// 为了一顿右键菜单把 WinUI 拉起来不划算（v1.2.0 瘦身就是把这个进程摘出去的）。
    /// </summary>
    private static void EnableDarkMenus()
    {
        if (_darkMenuApplied) return;
        _darkMenuApplied = true;
        try
        {
            var ux = LoadLibraryW("uxtheme.dll");
            if (ux == IntPtr.Zero) return;

            var setMode = GetProcAddress(ux, "#135");
            if (setMode != IntPtr.Zero)
            {
                var forceDark = Marshal.GetDelegateForFunctionPointer<SetPreferredAppModeProc>(setMode);
                forceDark(2);   // 0 默认 / 1 允许深色 / 2 强制深色
            }

            var flush = GetProcAddress(ux, "#136");
            if (flush != IntPtr.Zero)
                Marshal.GetDelegateForFunctionPointer<FlushMenuThemesProc>(flush)();
        }
        catch
        {
            // 老系统没有这些导出，保持默认外观
        }
    }

    private static bool _darkMenuApplied;
    private delegate int SetPreferredAppModeProc(int mode);
    private delegate void FlushMenuThemesProc();

    /// <summary>给窗口挂深色主题（uxtheme 的 DarkMode_Explorer 类），弹出菜单会沿用。</summary>
    private static void ApplyDarkWindowTheme(IntPtr hWnd)
    {
        try
        {
            SetWindowTheme(hWnd, "DarkMode_Explorer", null);
        }
        catch
        {
            // 老系统没有该主题类：保持默认
        }
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hWnd, string? pszSubAppName, string? pszSubIdList);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryW(string lpLibFileName);

    [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
    private static extern IntPtr GetProcAddress(IntPtr hModule, string lpProcName);

    private IntPtr BuildMenu()
    {
        // 兜底用的原生菜单，条目与自绘菜单保持一致（自绘失败时才会用到）
        var menu = CreatePopupMenu();
        AppendMenuW(menu, MF_STRING, 1, "打开统计");
        AppendMenuW(menu, MF_SEPARATOR, 0, null);
        AppendMenuW(menu, MF_STRING, 4, "暂停统计 30 分钟");
        AppendMenuW(menu, MF_STRING, 5, "暂停统计 1 小时");
        AppendMenuW(menu, MF_STRING, 6, "恢复统计");
        AppendMenuW(menu, MF_SEPARATOR, 0, null);
        AppendMenuW(menu, MF_STRING, 2, "退出 ScreenTime");
        return menu;
    }

    /// <summary>更新悬停提示（可跨线程调用：内部只 PostMessage，shell 调用仍在消息线程）。</summary>
    public void UpdateTooltip(string text)
    {
        var next = (text ?? "").Trim();
        if (next.Length > 120) next = next[..120];   // szTip 上限 128，留余量
        if (next == _tooltip) return;

        _tooltip = next;
        Log?.Invoke($"tray: tooltip = {next}");
        if (_hWnd != IntPtr.Zero && !_disposed)
            PostMessageW(_hWnd, WM_UPDATE_TIP, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>把当前提示文字推给 shell；失败说明图标已不在，转由兜底重试补登记。</summary>
    private void ApplyTip()
    {
        if (_disposed || _hWnd == IntPtr.Zero) return;

        var data = BuildData(NIF_TIP);
        var ok = Shell_NotifyIcon(NIM_MODIFY, ref data);
        if (!ok && _iconPresent)
        {
            _iconPresent = false;
            Log?.Invoke("tray: 更新提示失败，图标可能已丢失，等兜底重试补登记");
        }
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
        // 经典模式下鼠标划过图标会持续发 WM_MOUSEMOVE，
        // 早期版本把每条都写进日志，一天能刷出上千行、白白 IO。
        if (mouseMsg == WM_MOUSEMOVE || mouseMsg == WM_NCMOUSEMOVE) return;

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

        // 先走自绘菜单（WinUI 观感）；极端情况下失败再退回原生菜单
        if (TrayMenuWindow.Show(pt.X, pt.Y, CurrentMenuEntries(), OnMenuPick, Log)) return;

        // 弹菜单前必须把消息窗口设成前台，否则点击菜单外部不会自动收起
        SetForegroundWindow(_hWnd);
        var cmd = TrackPopupMenu(_hMenu, TPM_RIGHTBUTTON | TPM_RETURNCMD,
            pt.X, pt.Y, 0, _hWnd, IntPtr.Zero);
        // 官方要求：菜单结束后投递 WM_NULL，避免菜单残留
        PostMessageW(_hWnd, 0x0000, IntPtr.Zero, IntPtr.Zero);
        OnMenuPick((int)cmd);
    }

    /// <summary>同步暂停状态，供菜单文案使用（由 UpdateTrayTooltip 每次刷新时调用）。</summary>
    public void SetPaused(DateTime? until) => _pausedUntil = until;

    /// <summary>
    /// 菜单项定义：Id 为 0 的行是分隔线，Children 非空表示二级菜单。
    /// 暂停中把文案换成「已暂停至 HH:mm」，子菜单把「恢复统计」提到第一项，
    /// 这样不用悬停 tooltip 也能看出当前处于暂停。
    /// </summary>
    private TrayMenuWindow.Entry[] CurrentMenuEntries()
    {
        var pauseText = _pausedUntil is { } u
            ? $"暂停统计（已暂停至 {u.ToLocalTime():HH:mm}）"
            : "暂停统计";

        var pauseChildren = _pausedUntil is not null
            ? new TrayMenuWindow.Entry[]
            {
                new("恢复统计", 6),
                default,
                new("30 分钟", 4),
                new("1 小时", 5),
            }
            : new TrayMenuWindow.Entry[]
            {
                new("30 分钟", 4),
                new("1 小时", 5),
                default,
                new("恢复统计", 6),
            };

        return new TrayMenuWindow.Entry[]
        {
            new("打开统计", 1),
            default,
            new(pauseText, 10, pauseChildren),
            default,
            new("退出 ScreenTime", 2),
        };
    }

    private void OnMenuPick(int id)
    {
        if (id <= 0) return;                     // 0 = 原生命令为空，int.MinValue = 只是关掉菜单
        Log?.Invoke($"tray: menu cmd={id}");
        switch (id)
        {
            case 1: OpenRequested?.Invoke(); break;
            case 2: ExitRequested?.Invoke(); break;
            case 4: PauseRequested?.Invoke(30); break;
            case 5: PauseRequested?.Invoke(60); break;
            case 6: PauseRequested?.Invoke(0); break;
        }
    }

    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WM_DESTROY)
        {
            PostQuitMessage(0);
            return IntPtr.Zero;
        }

        // 外壳重启广播：必须在 WndProc 里接，进程才可能把图标补回去
        if (TaskbarCreatedMsg != 0 && msg == TaskbarCreatedMsg)
        {
            _current?.OnTaskbarCreated();
            return IntPtr.Zero;
        }

        // 看门狗节拍：由线程池投递过来，真正的 shell 调用在这里执行
        if (msg == WM_WATCHDOG)
        {
            _current?.EnsureIcon("watchdog");
            return IntPtr.Zero;
        }

        // 提示文字更新：同样在消息线程里推给 shell
        if (msg == WM_UPDATE_TIP)
        {
            _current?.ApplyTip();
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
        _disposed = true;
        try { _watchdog?.Dispose(); } catch { }
        try
        {
            if (_hWnd != IntPtr.Zero)
            {
                var data = new NOTIFYICONDATA
                {
                    cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
                    hWnd = _hWnd,
                    uID = 1,
                    szTip = "",
                    szInfo = "",
                    szInfoTitle = ""
                };
                Shell_NotifyIcon(NIM_DELETE, ref data);
            }
            if (_hIcon != IntPtr.Zero) DestroyIcon(_hIcon);
            if (_hMenu != IntPtr.Zero) DestroyMenu(_hMenu);
            if (_hWnd != IntPtr.Zero) DestroyWindow(_hWnd);
        }
        catch { }
        if (ReferenceEquals(_current, this)) _current = null;
    }

    /// <summary>
    /// 完整版 NOTIFYICONDATAW（x64 下 976 字节）。
    /// 早期版本只声明到 szTip 就结束（cbSize=296），虽然多数系统容忍，
    /// 但 shell 对 cbSize 的校验按已知尺寸进行，非标准尺寸在部分环境会被拒。
    /// </summary>
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string lpString);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? lpClassName, string? lpWindowName);

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
