using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace ScreenTime.Native;

/// <summary>前台窗口的采集结果。</summary>
public sealed class ForegroundInfo
{
    public nint Hwnd;
    public uint Pid;
    public string ExePath = "";
    public string ProcessName = "";
    public string AppName = "";
    public string Company = "";
    public string Title = "";
}

internal static class Win32
{
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmRead = 0x0010;

    [DllImport("user32.dll")]
    internal static extern nint GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(nint hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(nint hWnd);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern int GetWindowText(nint hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint hmodWinEventProc,
        WinEventProc pfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWinEvent(nint hWinEventHook);

    internal delegate void WinEventProc(nint hWinEventHook, uint eventType, nint hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool CloseHandle(nint hObject);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool QueryFullProcessImageName(nint hProcess, uint dwFlags,
        StringBuilder lpExeName, ref uint lpdwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern nint OpenProcess(uint dwDesiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, uint dwProcessId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumChildWindows(nint hWndParent, EnumWindowsProc lpEnumFunc, nint lParam);

    internal delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint CbSize;
        public uint DwTime;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetLastInputInfo(ref LastInputInfo plii);

    [DllImport("kernel32.dll")]
    private static extern uint GetTickCount64();

    /// <summary>返回用户空闲秒数（距上次键鼠输入）。</summary>
    public static double GetIdleSeconds()
    {
        var lii = new LastInputInfo { CbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
        if (!GetLastInputInfo(ref lii)) return 0;
        return (GetTickCount64() - lii.DwTime) / 1000.0;
    }

    /// <summary>采集当前前台窗口的完整信息；锁屏或无前台时返回 null。</summary>
    public static ForegroundInfo? GetForegroundInfo()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == 0) return null;

        GetWindowThreadProcessId(hwnd, out var pid);

        var title = new StringBuilder(512);
        GetWindowText(hwnd, title, title.Capacity);

        // UWP / 商店应用：窗口宿主为 ApplicationFrameHost，真实进程在其可见子窗口上。
        if (TryGetProcessName(pid) is { } hostName && hostName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
        {
            var realPid = pid;
            EnumChildWindows(hwnd, (child, _) =>
            {
                GetWindowThreadProcessId(child, out var childPid);
                if (childPid != 0 && childPid != pid && IsWindowVisible(child))
                {
                    realPid = childPid;
                    return false;
                }
                return true;
            }, 0);
            pid = realPid;
        }

        return BuildInfo(pid, title.ToString(), hwnd);
    }

    /// <summary>按 PID 采集进程信息（用于后台音频进程）。</summary>
    public static ForegroundInfo? GetInfoForPid(uint pid)
    {
        if (pid == 0) return null;
        return BuildInfo(pid, "", 0);
    }

    private static ForegroundInfo BuildInfo(uint pid, string title, nint hwnd)
    {
        var info = new ForegroundInfo { Hwnd = hwnd, Pid = pid, Title = title };
        var path = GetProcessPath(pid);
        info.ExePath = path ?? "";
        info.ProcessName = TryGetProcessName(pid) ?? ("PID:" + pid);

        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            try
            {
                var vi = FileVersionInfo.GetVersionInfo(path);
                info.AppName = FirstNonEmpty(vi.ProductName, vi.FileDescription, info.ProcessName);
                info.Company = vi.CompanyName ?? "";
            }
            catch
            {
                info.AppName = info.ProcessName;
            }
        }
        else
        {
            info.AppName = info.ProcessName;
        }
        return info;
    }

    private static string? TryGetProcessName(uint pid)
    {
        try
        {
            return Process.GetProcessById((int)pid).ProcessName;
        }
        catch
        {
            return null;
        }
    }

    private static string? GetProcessPath(uint pid)
    {
        var h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (h == 0)
            h = OpenProcess(ProcessQueryInformation | ProcessVmRead, false, pid);
        if (h == 0) return null;
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            if (QueryFullProcessImageName(h, 0, sb, ref size))
                return sb.ToString();
        }
        finally
        {
            CloseHandle(h);
        }
        return null;
    }

    private static string FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
            if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
        return "";
    }
}

/// <summary>前台窗口切换事件钩子（在拥有 Dispatcher 消息循环的线程上使用）。</summary>
public sealed class ForegroundWatcher : IDisposable
{
    private nint _hook;
    private Win32.WinEventProc? _proc;

    public event Action? ForegroundChanged;

    public void Start()
    {
        _proc = (_, _, _, idObject, idChild, _, _) =>
        {
            if (idObject == 0 && idChild == 0)
                ForegroundChanged?.Invoke();
        };
        _hook = Win32.SetWinEventHook(0x0003, 0x0003, 0, _proc, 0, 0, 0); // EVENT_SYSTEM_FOREGROUND
    }

    public void Dispose()
    {
        if (_hook != 0)
        {
            Win32.UnhookWinEvent(_hook);
            _hook = 0;
        }
    }
}
