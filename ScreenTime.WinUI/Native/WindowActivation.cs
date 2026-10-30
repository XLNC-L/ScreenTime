using System.IO;
using System.Runtime.InteropServices;

namespace ScreenTime.Native;

/// <summary>
/// 强制把窗口提到最前。
///
/// 为什么不能只用 <c>Window.Activate()</c>：它内部只做一次 <c>SetForegroundWindow</c>，
/// 而 Windows 有前台锁（Foreground Lock）——不是当前前台进程的调用会被直接拒绝，
/// 且**不抛异常、只返回 false**，于是表现成「点了托盘没反应」。
///
/// 从托盘图标唤起正好踩在这个限制上：真正收到鼠标点击的是 Agent 进程，
/// UI 进程属于「被第三方启动的程序」，系统不给它抢焦点的权限。
/// 双保险：Agent 侧先调 <c>AllowSetForegroundWindow</c> 把权限转交过来（见 Program.OpenMainWindow），
/// 这里再用 AttachThreadInput 兜底，最后还不成就闪任务栏，至少让用户知道窗口在哪。
/// </summary>
internal static class WindowActivation
{
    private const int SW_SHOW = 5;
    private const int SW_RESTORE = 9;

    private const uint FLASHW_ALL = 0x00000003;
    private const uint FLASHW_TIMERNOFG = 0x0000000C;

    /// <summary>Z 序顶端（0 = HWND_TOP，普通窗口层的最上层）。</summary>
    private static readonly nint HWND_TOP = 0;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;

    /// <summary>
    /// 把窗口恢复（若最小化）并提到最前。
    ///
    /// 关键认知：**改变 Z 序（视觉层级）不需要前台权限，只有抢键盘焦点才需要**。
    /// 所以先做一次 topmost→非 topmost 的翻转把窗口顶上来（这一步几乎必然成功），
    /// 再去争取焦点；抢不到焦点用户也看得到窗口，不会再"点了没反应"。
    ///
    /// 之前只做 SetForegroundWindow + AttachThreadInput，实测在当前这台机器上会失败：
    /// 前台窗口可能属于**提权进程**（PowerToys / Process Lasso 都以管理员运行），
    /// 非提权进程受 UIPI 限制，既不能 AttachThreadInput 也不能抢焦点。
    /// </summary>
    public static void ForceForeground(nint hwnd)
    {
        if (hwnd == nint.Zero) return;

        var steps = new List<string>();

        // 最小化状态必须先还原：SetForegroundWindow 对最小化窗口只改任务栏状态，
        // 不会把窗口画出来。
        var wasMinimized = IsIconic(hwnd);
        ShowWindow(hwnd, wasMinimized ? SW_RESTORE : SW_SHOW);
        steps.Add($"restore={wasMinimized}");

        // ① 不依赖任何权限地把窗口抬到 Z 序顶端。
        //    用 HWND_TOP 而不是 HWND_TOPMOST：前者只把窗口放到「普通窗口」这一层的顶部，
        //    不需要前台权限，也不会把 WS_EX_TOPMOST 留在窗口上
        //    （TOPMOST→NOTOPMOST 的翻转一旦后一步失败，窗口会永久钉在所有窗口之上）。
        var raised = SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
        steps.Add($"raise={raised}");

        // ② 争取键盘焦点（可能被前台锁拒绝，失败不影响「已经置顶」）
        if (SetForegroundWindow(hwnd))
        {
            Log(string.Join(" ", steps) + " foreground=direct");
            return;
        }
        steps.Add("fg=denied");

        // ③ 经典绕法：临时挂到当前前台线程的输入队列上。
        //    对提权进程的前台窗口无效（UIPI 拦截），所以只是备选。
        var foreground = GetForegroundWindow();
        var foregroundThread = foreground == nint.Zero ? 0 : GetWindowThreadProcessId(foreground, out _);
        var currentThread = GetCurrentThreadId();
        var attached = false;

        if (foregroundThread != 0 && foregroundThread != currentThread)
        {
            attached = AttachThreadInput(currentThread, foregroundThread, true);
            if (attached)
            {
                try
                {
                    BringWindowToTop(hwnd);
                    SetForegroundWindow(hwnd);
                }
                finally
                {
                    AttachThreadInput(currentThread, foregroundThread, false);
                }
            }
        }
        steps.Add($"attach={attached}");

        if (GetForegroundWindow() == hwnd)
        {
            Log(string.Join(" ", steps) + " foreground=attach");
            return;
        }

        // ④ 焦点确实拿不到：闪任务栏告诉用户「窗口已经上来了，点一下就行」。
        //    只有在连 Z 序都没抬起来时才闪，避免明明已经置顶还闪得烦人。
        if (!raised)
        {
            var info = new FLASHWINFO
            {
                cbSize = (uint)Marshal.SizeOf<FLASHWINFO>(),
                hwnd = hwnd,
                dwFlags = FLASHW_ALL | FLASHW_TIMERNOFG,
                uCount = 3,
                dwTimeout = 0
            };
            FlashWindowEx(ref info);
            steps.Add("flash=1");
        }

        Log(string.Join(" ", steps) + " foreground=none");
    }

    /// <summary>激活过程写日志，出问题时能直接看出卡在哪一步。</summary>
    private static void Log(string message)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenTime");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "activate.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {message}\n");
        }
        catch { }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FLASHWINFO
    {
        public uint cbSize;
        public nint hwnd;
        public uint dwFlags;
        public uint uCount;
        public uint dwTimeout;
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(nint hWnd);

    [DllImport("user32.dll")]
    private static extern bool FlashWindowEx(ref FLASHWINFO pwfi);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);
}
