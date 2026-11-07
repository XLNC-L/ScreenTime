using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Xml.Linq;
using ScreenTime.Agent.Native;
using ScreenTime.Data;
using ScreenTime.Services;

namespace ScreenTime.Agent;

/// <summary>
/// 采集器进程（无 UI，常驻托盘）。
///
/// 与 UI 进程分离的架构：
///  - Agent 负责前台/音频/会话事件采集、UWP 图标回填、开机自启注册，独立于窗口存在；
///  - UI 进程（ScreenTime.exe）按需启动，关闭即退出，托盘态不再保留 110MB 的 WinUI 进程。
///  - 两进程共享同一个 SQLite 库（WAL + busy_timeout 支持多进程并发读写）。
///  - 用途标注：Agent 把待标注片段写入 settings.pending_purpose，UI 轮询读取后弹出。
/// </summary>
internal static class Program
{
    private const string MutexName = "ScreenTime_Agent_Mutex";
    private const string TaskName = "ScreenTime";

    private static Database _db = null!;
    private static WatcherService _watcher = null!;
    private static TrayIcon? _tray;
    private static volatile bool _exit;

    [STAThread]
    private static int Main(string[] args)
    {
        using var mutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew) return 0; // 已有采集器在跑，第二实例直接退出

        try
        {
            Run(args);
        }
        catch (Exception ex)
        {
            WriteError(ex);
            return 1;
        }
        return 0;
    }

    private static void Run(string[] args)
    {
        _db = new Database(CommandLineValue(args, "--db="));
        _db.Init();
        // 结构升级/迁移可能较慢，后台执行；采集心跳 5 秒后才首次写库，缓冲充足
        _ = Task.Run(() => { try { _db.Migrate(); } catch { } });

        _watcher = new WatcherService(_db);
        // 用途标注写库，由 UI 进程轮询弹出（采集线程直接写，无 UI 线程概念）
        _watcher.PurposePrompt += (segmentId, processName) =>
        {
            try { _db.SetSetting("pending_purpose", $"{segmentId}|{processName}"); } catch { }
        };
        _watcher.Start();

        // 设置热重载：UI 修改音频/用途/浏览器开关后写 settings_version，这里轮询并刷新
        var settingsTimer = new System.Threading.Timer(_ => CheckSettingsReload(),
            null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));

        // UWP 图标回填 + 自启注册：都不挡采集启动
        _ = Task.Run(() =>
        {
            try { UwpAppService.RequestBackfill(_db); } catch { }
        });
        EnsureSelfAutoStart();

        // 托盘 + 消息泵：ForegroundWatcher 的 WinEvent 回调也依赖消息循环
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        _tray = new TrayIcon("ScreenTime", File.Exists(iconPath) ? iconPath : "", WriteLog);
        _tray.OpenRequested += OpenMainWindow;
        _tray.ExitRequested += ExitAgent;

        WriteLog($"agent started pid={Environment.ProcessId}");

        // 诊断：--tray-selftest 时 5 秒后自投递一次左键回调，用于排查托盘点击链路
        if (args.Any(a => a.Equals("--tray-selftest", StringComparison.OrdinalIgnoreCase)))
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(5000);
                try { _tray?.SimulateClick(); } catch (Exception ex) { WriteError(ex); }
            });
        }

        RunMessageLoop();
        try { settingsTimer.Dispose(); } catch { }
        try { _watcher.Dispose(); } catch { }
        try { _tray.Dispose(); } catch { }
        WriteLog("agent stopped");
    }

    /// <summary>轮询 UI 写入的 settings_version，变化则让采集器热重载设置。</summary>
    private static string _lastSettingsVersion = "";

    private static void CheckSettingsReload()
    {
        try
        {
            var v = _db.GetSetting("settings_version", "") ?? "";
            if (v != _lastSettingsVersion)
            {
                _lastSettingsVersion = v;
                _watcher.ReloadSettings();
            }
        }
        catch { }
    }

    /// <summary>
    /// 消息泵：所有消息统一派发，托盘回调由 WndProc 处理。
    /// 不在泵里拦截托盘消息——shell 用 SendMessage 投递时消息不入队，
    /// 拦截式写法会完全收不到点击。
    /// </summary>
    private static void RunMessageLoop()
    {
        const uint PM_REMOVE = 0x0001;
        const uint WM_QUIT = 0x0012;
        while (!_exit)
        {
            while (PeekMessageW(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
            {
                if (msg.message == WM_QUIT) { _exit = true; break; }
                TranslateMessage(ref msg);
                DispatchMessageW(ref msg);
            }
            Thread.Sleep(50);
        }
    }

    /// <summary>托盘「打开统计」：启动 UI 进程（UI 单实例会自动唤起已有窗口）。</summary>
    private static void OpenMainWindow()
    {
        try
        {
            var uiExe = Path.Combine(AppContext.BaseDirectory, "ScreenTime.exe");
            if (!File.Exists(uiExe))
            {
                WriteLog("UI exe not found next to agent: " + uiExe);
                return;
            }
            Process.Start(new ProcessStartInfo(uiExe) { UseShellExecute = true });
        }
        catch (Exception ex) { WriteError(ex); }
    }

    private static void ExitAgent()
    {
        try { _db.AddSessionEvent("shutdown"); } catch { }
        _exit = true;
    }

    /// <summary>
    /// 保证开机自启任务指向 Agent 自己（ONLOGON，无参数）。
    /// 已存在且指向当前 exe 则跳过；指向旧版 UI（--silent）则 /F 重建，自动迁移。
    /// </summary>
    private static void EnsureSelfAutoStart()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;

        var current = QueryTaskCommand();
        if (current != null && current.Equals(exe, StringComparison.OrdinalIgnoreCase)) return;

        var args = $"/Create /TN \"{TaskName}\" /SC ONLOGON /F /TR \"\\\"{exe}\\\"\"";
        if (RunSchtasks(args, elevate: false) || RunSchtasks(args, elevate: true))
            WriteLog("auto-start task registered: " + exe);
        else
            WriteLog("auto-start task registration failed");
    }

    private static string? QueryTaskCommand()
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks", $"/Query /TN \"{TaskName}\" /XML")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p == null) return null;
            var output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(5000)) return null;
            if (p.ExitCode != 0) return null;
            var doc = XDocument.Parse(output);
            return doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Command")?.Value;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>执行 schtasks；普通权限失败时提权再试（用户取消 UAC 则放弃）。</summary>
    private static bool RunSchtasks(string args, bool elevate)
    {
        try
        {
            var psi = new ProcessStartInfo("schtasks", args)
            {
                CreateNoWindow = true,
                UseShellExecute = elevate,
                RedirectStandardOutput = !elevate,
                RedirectStandardError = !elevate
            };
            if (elevate) psi.Verb = "runas";
            using var p = Process.Start(psi);
            if (p == null) return false;
            if (!p.WaitForExit(elevate ? 60000 : 15000)) return false;
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string? CommandLineValue(string[] args, string prefix)
    {
        var arg = args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return arg?[prefix.Length..];
    }

    private static void WriteLog(string msg)
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenTime");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "agent.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {msg}\n");
        }
        catch { }
    }

    private static void WriteError(object? ex)
    {
        WriteLog("ERROR: " + ex);
    }

    [DllImport("user32.dll")]
    private static extern bool PeekMessageW(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin,
        uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessageW(ref Msg lpMsg);
}
