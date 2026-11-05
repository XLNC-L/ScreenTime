using System.Runtime.InteropServices;
using System.Text;
using ScreenTime.Agent.Native;

// 托盘自绘菜单探针：编译仓库里同一份 TrayMenuWindow.cs。
//   MenuProbe.exe          进程内全生命周期用例
//   MenuProbe.exe live     对运行中的 Agent 做端到端验证
//   MenuProbe.exe count    统计系统内残留的菜单窗口
//
// 菜单的「点外面关闭」从 WH_MOUSE_LL 低级钩子（拖慢全系统鼠标，已废弃）
// 改为 EVENT_SYSTEM_FOREGROUND WinEvent：只有别的窗口真正拿到前台才关闭。
// 因此用例里的「点菜单外」必须制造一次真实的前台切换——
//   * 真点击一个非前台的落点窗口（输入驱动的激活不受前台锁限制）
//   * 或在两个落点窗口之间 SetForegroundWindow 轮换
// 连续点同一个已在前台的窗口不产生事件，菜单按设计就不该关。
//
// 环境坑（都踩过）：
//   * 真实指针是全局状态：窗口显示在静止指针下方时系统会补发 WM_MOUSEMOVE，
//     悬停逻辑会意外展开子菜单——ShowMenu 前必须把指针停到中性点。
//   * 宿主可能有 topmost/UIAccess 窗口盖住固定坐标——点击点必须用
//     WindowFromPoint 自适应确认落在自己的落点窗口上。
internal static class Program
{
    private const string MenuClass = "ScreenTimeAgentTrayMenu_v2";
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_VISIBLE = 0x10000000;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint PM_REMOVE = 0x0001;

    private static readonly TrayMenuWindow.Entry[] Entries =
    {
        new("打开统计", 1),
        default,
        new("暂停统计", 10, new TrayMenuWindow.Entry[]
        {
            new("30 分钟", 4),
            new("1 小时", 5),
            default,
            new("恢复统计", 6),
        }),
        default,
        new("退出 ScreenTime", 2),
    };

    private static int _failures;
    private static int _picked;
    private static IntPtr _dummy;      // (0,0)-(160,120)
    private static IntPtr _dummy2;     // (400,300)-(560,420)
    private static bool _flip = true;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length > 0 && args[0] == "count") return ModeCount();
        if (args.Length > 0 && args[0] == "diag") return ModeDiag();
        if (args.Length > 0 && args[0] == "live") return ModeLive();
        return ModeSuite();
    }

    // ---------------- diag（看谁盖住了落点窗口） ----------------

    private static int ModeDiag()
    {
        MakeDummy();
        Thread.Sleep(200);
        foreach (var (name, h) in new[] { ("dummy1", _dummy), ("dummy2", _dummy2) })
        {
            EnsureTop(h);
            GetWindowRect(h, out var r);
            Console.WriteLine($"{name}=0x{h.ToInt64():X} rect=({r.Left},{r.Top},{r.Right},{r.Bottom})");
            for (var dy = 15; dy < 120; dy += 45)
            {
                for (var dx = 15; dx < 160; dx += 45)
                {
                    var hit = WindowFromPoint(r.Left + dx, r.Top + dy);
                    var cls = "";
                    if (hit != IntPtr.Zero)
                    {
                        var sb = new StringBuilder(256);
                        GetClassNameW(hit, sb, sb.Capacity);
                        cls = sb.ToString();
                    }
                    GetWindowThreadProcessId(hit, out var pid);
                    Console.WriteLine($"  ({r.Left + dx},{r.Top + dy}) -> 0x{hit.ToInt64():X} {cls} pid={pid}{(hit == h ? " [自己]" : "")}");
                }
            }
        }
        DropDummy();
        return 0;
    }

    // ---------------- count ----------------

    private static int ModeCount()
    {
        Console.WriteLine($"系统内 {MenuClass} 窗口数: {CountAll()}");
        EnumWindows((hwnd, _) =>
        {
            var sb = new StringBuilder(256);
            GetClassNameW(hwnd, sb, sb.Capacity);
            if (sb.ToString() == MenuClass)
            {
                GetWindowThreadProcessId(hwnd, out var pid);
                GetWindowRect(hwnd, out var r);
                Console.WriteLine($"  hwnd=0x{hwnd.ToInt64():X} pid={pid} rect=({r.Left},{r.Top},{r.Right},{r.Bottom})");
            }
            return true;
        }, IntPtr.Zero);
        return 0;
    }

    // ---------------- live（对真实 Agent） ----------------

    private static int ModeLive()
    {
        MakeDummy();
        var tray = FindWindowW(null, "ScreenTime Agent");
        Console.WriteLine($"托盘窗口: 0x{tray.ToInt64():X}");
        if (tray == IntPtr.Zero) { Check(false, "找不到运行中的 Agent"); return 1; }

        // 先把前台落到落点窗口 A 上（真实点击优先；被宿主窗口盖住时退化为 SetForegroundWindow）
        ActivateDummy(prefersFirst: true);
        Thread.Sleep(300);

        RightClickTray(tray);
        Thread.Sleep(700);
        var a = CountAll();
        Check(a == 1, $"右键后菜单数应为 1，实际 {a}");

        RightClickTray(tray);
        Thread.Sleep(700);
        var b = CountAll();
        Check(b == 1, $"再右键一次仍应为 1（不套娃），实际 {b}");

        // 切到落点窗口 B（前台切换）→ Agent 的 WinEvent 关菜单
        ActivateDummy(prefersFirst: false);
        Thread.Sleep(700);
        var c = CountAll();
        Check(c == 0, $"切到别处后应为 0，实际 {c}");

        Console.WriteLine("\n== 再来一轮：菜单重开，切回第一个落点窗口，必须清零");
        RightClickTray(tray);
        Thread.Sleep(700);
        var e = CountAll();
        Check(e == 1, $"重开后菜单数应为 1，实际 {e}");

        ActivateDummy(prefersFirst: true);
        Thread.Sleep(800);
        var d = CountAll();
        Check(d == 0, $"切到别处后应为 0，实际 {d}");

        DropDummy();
        return Report("实战");
    }

    private static void RightClickTray(IntPtr tray) =>
        PostMessageW(tray, 0x8001 /*WM_APP+1*/, (IntPtr)1, (IntPtr)0x0205 /*WM_RBUTTONUP*/);

    // ---------------- 进程内全生命周期 ----------------

    private static int ModeSuite()
    {
        MakeDummy();

        Section("1. 单次弹出");
        ShowMenu();
        Pump(250);
        var n1 = Count();
        Check(n1 == 1, $"窗口数应为 1，实际 {n1}");

        Section("2. 连续弹出三次不套娃");
        ShowMenu(); ShowMenu(); ShowMenu();
        Pump(350);
        var n2 = Count();
        Check(n2 == 1, $"窗口数应为 1，实际 {n2}");

        Section("3. 前台切到别的窗口立即关闭（真实点击优先，被盖住则退化 SetForegroundWindow）");
        ShowMenu();
        Pump(150);
        // 宿主聊天窗口常是全屏 Chromium 渲染件，把落点窗口全盖住，真实点击落不进去——
        // 两条路触发的是同一个系统事件（EVENT_SYSTEM_FOREGROUND），机制验证等价。
        var clicked = ClickOwnDummy();
        Console.WriteLine(clicked ? "   · 路径：真实点击" : "   · 路径：SetForegroundWindow（落点窗口被宿主盖住）");
        if (!clicked) ActivateDummy(prefersFirst: true);
        Pump(400);
        var n3 = Count();
        Check(n3 == 0, $"窗口数应为 0，实际 {n3}");

        Section("4. 指针移出菜单后自动关闭（旧版必挂：定时器线程 DestroyWindow 失败）");
        ShowMenu();
        Pump(150);
        HoverFirstRow();
        Pump(200);
        GlideTo(80, 60);          // 瞬移不会产生 WM_MOUSELEAVE，必须逐点移动
        Pump(800);
        var n4 = Count();
        Check(n4 == 0, $"窗口数应为 0，实际 {n4}");

        Section("5. 悬停展开子菜单，激活其他窗口父子一起关");
        ShowMenu();
        Pump(150);
        HoverPauseRow();
        Pump(350);
        var n5 = Count();
        Check(n5 == 2, $"父 + 子应为 2，实际 {n5}");
        ActivateOther();
        Pump(350);
        var n5b = Count();
        Check(n5b == 0, $"窗口数应为 0，实际 {n5b}");

        Section("6. 点菜单项：回调触发且窗口关闭");
        _picked = 0;
        ShowMenu();
        Pump(150);
        ClickFirstRow();
        Pump(350);
        Check(_picked == 1, $"回调应为 1 次，实际 {_picked}");
        var n6 = Count();
        Check(n6 == 0, $"窗口数应为 0，实际 {n6}");

        Section("7. 反复 20 次开合无残留（每轮用前台切换关闭）");
        for (var i = 0; i < 20; i++)
        {
            ShowMenu();
            Pump(40);
            ActivateOther();
            Pump(40);
        }
        Pump(500);
        var n7 = Count();
        Check(n7 == 0, $"窗口数应为 0，实际 {n7}");

        DropDummy();
        return Report("进程内");
    }

    // ---------------- 辅助 ----------------

    private static int Report(string title)
    {
        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? $"{title}全部通过" : $"{title} {_failures} 项失败");
        return _failures == 0 ? 0 : 1;
    }

    private static void MakeDummy()
    {
        // 落点窗口必须 TOPMOST：屏幕中央常被已前台的宿主窗口盖住，
        // 点击落在它身上不产生前台切换（菜单按设计不关），还会误点用户的界面。
        _dummy = CreateWindowExW(WS_EX_TOPMOST, "STATIC", "probe", WS_POPUP | WS_VISIBLE,
            0, 0, 160, 120, IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
        _dummy2 = CreateWindowExW(WS_EX_TOPMOST, "STATIC", "probe2", WS_POPUP | WS_VISIBLE,
            400, 300, 160, 120, IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);
    }

    private static void DropDummy()
    {
        if (_dummy != IntPtr.Zero) DestroyWindow(_dummy);
        if (_dummy2 != IntPtr.Zero) DestroyWindow(_dummy2);
        _dummy = IntPtr.Zero;
        _dummy2 = IntPtr.Zero;
    }

    /// <summary>把落点窗口压回 topmost 带最顶端：宿主窗口可能后来居上。</summary>
    private static void EnsureTop(IntPtr h) =>
        SetWindowPos(h, (IntPtr)(-1) /*HWND_TOPMOST*/, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 /*NOSIZE|NOMOVE|NOACTIVATE*/);

    /// <summary>
    /// 在落点窗口上找一个真正可见、可点击的点（WindowFromPoint 命中的必须恰好是落点窗口本身），
    /// 真实点击它。返回 false 表示这个窗口全被盖住。
    /// </summary>
    private static bool ClickPointOn(IntPtr target)
    {
        EnsureTop(target);
        GetWindowRect(target, out var r);
        for (var dy = 15; dy < 120; dy += 30)
        for (var dx = 15; dx < 160; dx += 30)
        {
            var x = r.Left + dx;
            var y = r.Top + dy;
            if (WindowFromPoint(x, y) != target) continue;
            ClickAt(x, y);
            return true;
        }
        return false;
    }

    private static bool ClickOwnDummy() => ClickPointOn(_dummy) || ClickPointOn(_dummy2);

    /// <summary>把前台切到指定落点窗口（优先 prefersFirst 对应那个），制造一次前台切换事件。</summary>
    private static void ActivateDummy(bool prefersFirst)
    {
        var first = prefersFirst ? _dummy : _dummy2;
        var second = prefersFirst ? _dummy2 : _dummy;
        if (SetForegroundWindow(first)) { Pump(20); return; }
        SetForegroundWindow(second);
        Pump(20);
    }

    /// <summary>在两个落点窗口之间轮换前台：每次调用都制造一次真实的前台切换，
    /// 这正是新关闭机制（EVENT_SYSTEM_FOREGROUND）的触发条件。</summary>
    private static void ActivateOther() => ActivateDummy(prefersFirst: _flip = !_flip);

    private static void ShowMenu()
    {
        // 真实指针是全局状态，上一轮用例可能把它留在菜单区域内——
        // 窗口显示在静止指针下方时系统会补发 WM_MOUSEMOVE，悬停逻辑会意外展开子菜单。
        // 先把指针停到远离菜单的中性点，保证用例确定性。
        SetCursorPos(100, 250);
        Pump(30);
        var ok = TrayMenuWindow.Show(600, 500, Entries,
            id => { _picked++; Console.WriteLine($"   → pick={id}"); },
            s => Console.WriteLine("   · " + s));
        if (!ok) Check(false, "Show 返回 false");
    }

    private static void Check(bool ok, string note)
    {
        Console.WriteLine((ok ? "   PASS " : "   FAIL ") + note);
        if (!ok) _failures++;
    }

    private static void Section(string title) => Console.WriteLine("\n== " + title);

    private static void Pump(int ms)
    {
        var until = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < until)
        {
            while (PeekMessageW(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
            {
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
            Thread.Sleep(5);
        }
    }

    private static int Count() => MenuWindows(samePidOnly: true).Count;
    private static int CountAll() => MenuWindows(samePidOnly: false).Count;

    private static List<IntPtr> MenuWindows(bool samePidOnly)
    {
        var list = new List<IntPtr>();
        var self = (uint)Environment.ProcessId;
        EnumWindows((hwnd, _) =>
        {
            if (samePidOnly)
            {
                GetWindowThreadProcessId(hwnd, out var pid);
                if (pid != self) return true;
            }
            var sb = new StringBuilder(256);
            GetClassNameW(hwnd, sb, sb.Capacity);
            if (sb.ToString() == MenuClass) list.Add(hwnd);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static RECT FirstRect()
    {
        var list = MenuWindows(samePidOnly: true);
        if (list.Count == 0) return default;
        GetWindowRect(list[0], out var rect);
        return rect;
    }

    private static void HoverFirstRow()
    {
        var r = FirstRect();
        if (r.Right == r.Left) return;
        SetCursorPos((r.Left + r.Right) / 2, r.Top + 6 + 17);
    }

    private static void HoverPauseRow()
    {
        var r = FirstRect();
        if (r.Right == r.Left) return;
        SetCursorPos((r.Left + r.Right) / 2, r.Top + 6 + 34 + 9 + 17);   // 内边距 6 + 一行 34 + 分隔 9
    }

    private static void ClickFirstRow()
    {
        var r = FirstRect();
        if (r.Right == r.Left) return;
        ClickAt((r.Left + r.Right) / 2, r.Top + 6 + 17);
    }

    /// <summary>逐点移动指针：瞬移（SetCursorPos 一步到位）不会产生 WM_MOUSELEAVE。</summary>
    private static void GlideTo(int x, int y)
    {
        GetCursorPos(out var from);
        const int steps = 24;
        for (var i = 1; i <= steps; i++)
        {
            SetCursorPos(from.X + (x - from.X) * i / steps, from.Y + (y - from.Y) * i / steps);
            Pump(8);
        }
    }

    private static void ClickAt(int x, int y)
    {
        SetCursorPos(x, y);
        Thread.Sleep(60);
        mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(30);
        mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(60);
    }

    // ---------------- Win32 ----------------

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX, ptY;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? lpClassName, string? lpWindowName);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(int x, int y);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PeekMessageW(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int w, int h, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);
}
