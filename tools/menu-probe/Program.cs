using System.Runtime.InteropServices;
using System.Text;
using ScreenTime.Agent.Native;

// 托盘自绘菜单探针：编译仓库里同一份 TrayMenuWindow.cs。
//   MenuProbe.exe          进程内全生命周期用例
//   MenuProbe.exe live     对运行中的 Agent 做端到端验证
//   MenuProbe.exe count    统计系统内残留的菜单窗口
internal static class Program
{
    private const string MenuClass = "ScreenTimeAgentTrayMenu_v2";
    private const uint WS_POPUP = 0x80000000;
    private const uint WS_VISIBLE = 0x10000000;
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
    private static IntPtr _dummy;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        if (args.Length > 0 && args[0] == "count") return ModeCount();
        if (args.Length > 0 && args[0] == "live") return ModeLive();
        return ModeSuite();
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

        SetCursorPos(1200, 700);
        Thread.Sleep(200);
        RightClickTray(tray);
        Thread.Sleep(700);
        var a = CountAll();
        Check(a == 1, $"右键后菜单数应为 1，实际 {a}");

        RightClickTray(tray);
        Thread.Sleep(700);
        var b = CountAll();
        Check(b == 1, $"再右键一次仍应为 1（不套娃），实际 {b}");

        ClickAt(80, 60);
        Thread.Sleep(700);
        var c = CountAll();
        Check(c == 0, $"点菜单外后应为 0，实际 {c}");

        Console.WriteLine("\n== 等回调队列排空后点外面，必须清零");
        Thread.Sleep(1500);
        ClickAt(80, 60);
        Thread.Sleep(800);
        var d = CountAll();
        Check(d == 0, $"排空后点外面应为 0，实际 {d}");

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

        Section("3. 点菜单外立即关闭");
        ClickAt(80, 60);
        Pump(350);
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

        Section("5. 悬停展开子菜单，点外面父子一起关");
        ShowMenu();
        Pump(150);
        HoverPauseRow();
        Pump(350);
        var n5 = Count();
        Check(n5 == 2, $"父 + 子应为 2，实际 {n5}");
        ClickAt(80, 60);
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

        Section("7. 反复 20 次开合无残留");
        for (var i = 0; i < 20; i++)
        {
            ShowMenu();
            Pump(40);
            ClickAt(80, 60);
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

    private static void MakeDummy() =>
        _dummy = CreateWindowExW(0, "STATIC", "probe", WS_POPUP | WS_VISIBLE,
            0, 0, 160, 120, IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(null), IntPtr.Zero);

    private static void DropDummy()
    {
        if (_dummy != IntPtr.Zero) DestroyWindow(_dummy);
        _dummy = IntPtr.Zero;
    }

    private static void ShowMenu()
    {
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
