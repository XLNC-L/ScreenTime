using System.Diagnostics;
using System.IO.Pipes;
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

    /// <summary>UI 进程的唤起通道（与 App.ActivatePipeName 保持一致）。</summary>
    private const string ActivatePipeName = "ScreenTime_Activate";

    private static Database _db = null!;
    private static WatcherService _watcher = null!;
    private static HistoryImportService _historyImport = null!;
    private static TrayIcon? _tray;
    private static volatile bool _exit;

    [STAThread]
    private static int Main(string[] args)
    {
        var mutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            return 0; // 已有采集器在跑，第二实例直接退出
        }

        try
        {
            Run(args);
        }
        catch (Exception ex)
        {
            WriteError(ex);
            // 先放锁再拉起新进程，否则新实例会被自己的单实例互斥挡掉
            try { mutex.ReleaseMutex(); } catch { }
            mutex.Dispose();
            TryRestartSelf();
            return 1;
        }

        mutex.Dispose();
        return 0;
    }

    /// <summary>
    /// 顶层异常后尝试自恢复。UI 关闭期间没人看护采集器，
    /// 一次未捕获异常就会让采集空窗到下次开机——这里补上。
    /// 10 分钟内只重启一次，避免启动期崩溃（比如数据库损坏）造成无限重启。
    /// </summary>
    private static void TryRestartSelf()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenTime");
            Directory.CreateDirectory(dir);
            var stamp = Path.Combine(dir, "agent-restart.stamp");
            if (File.Exists(stamp) &&
                DateTime.TryParse(File.ReadAllText(stamp), out var last) &&
                (DateTime.Now - last).TotalMinutes < 10)
            {
                WriteLog("restart suppressed (last attempt within 10 min)");
                return;
            }
            File.WriteAllText(stamp, DateTime.Now.ToString("O"));

            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return;
            Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
            WriteLog("restarting self after fatal error");
        }
        catch (Exception ex)
        {
            WriteLog("restart failed: " + ex.Message);
        }
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

        // 浏览器历史回填：v1.2.0 起从 UI 进程搬到这里，UI 关掉也能继续补网页地址。
        // 内部自带 30 分钟节拍与「回填开关」判断，关闭时自动跳过。
        _historyImport = new HistoryImportService(_db);
        _historyImport.Start();

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
        try { _historyImport.Dispose(); } catch { }
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
                // 设置变了让回填尽快跑一轮（打开「历史回填」开关后不用等半小时）
                _historyImport?.Start();
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

    /// <summary>
    /// 托盘「打开统计」。
    ///
    /// 有 UI 在跑时**直接走管道唤醒**，不再「启动第二个实例让它自己发现单实例再转发」——
    /// 那条路要多创建一整个进程（约 200MB 虚拟地址空间、一两百毫秒），
    /// 而且实测偶发失败：`Process.Start` 明明起了进程（pid 有返回），
    /// 信号却没到达 UI，表现就是「点托盘毫无反应」。
    /// 只有确实没有 UI 时才去启动新进程。
    /// </summary>
    private static void OpenMainWindow()
    {
        try
        {
            var uiRunning = Process.GetProcessesByName("ScreenTime").Length > 0;

            if (uiRunning)
            {
                // 用户刚点了托盘，此刻前台权限在 Agent 手里，先转交给 UI。
                GrantForegroundToRunningUi();

                if (SignalRunningUi())
                {
                    WriteLog("tray: signalled running ui via pipe");
                    return;
                }
                WriteLog("tray: pipe signal failed, falling back to Process.Start");
            }

            var uiExe = Path.Combine(AppContext.BaseDirectory, "ScreenTime.exe");
            if (!File.Exists(uiExe))
            {
                WriteLog("UI exe not found next to agent: " + uiExe);
                return;
            }

            var started = Process.Start(new ProcessStartInfo(uiExe) { UseShellExecute = true });
            WriteLog(started is null
                ? "tray: Process.Start 返回 null（没有启动任何进程）"
                : $"tray: started ui pid={started.Id}");
        }
        catch (Exception ex) { WriteError(ex); }
    }

    /// <summary>通过命名管道叫醒正在运行的 UI（写一个字节即触发它置顶）。</summary>
    private static bool SignalRunningUi()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", ActivatePipeName, PipeDirection.Out);
            client.Connect(500);
            client.WriteByte(1);
            client.Flush();
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 把本进程（当前前台）的「可设前台」权限转交给已经在跑的 UI 进程，
    /// 并直接替它把窗口顶到最前。
    ///
    /// 为什么要「直接代为置顶」而不是只转交权限：转交是让 UI 进程自己去抢，
    /// 中间隔着管道 + DispatcherQueue 排队，一次 SetForegroundWindow 的时效很容易被消耗掉；
    /// Agent 刚被点击，前台权限确定在自己手里，直接对 UI 的 HWND 调一次最稳。
    /// 没有运行中的 UI 时什么也不做——那种情况是新进程启动，走 UI 侧的 ForceToFront。
    /// </summary>
    private static void GrantForegroundToRunningUi()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("ScreenTime"))
            {
                try
                {
                    if (AllowSetForegroundWindow(p.Id))
                        WriteLog($"tray: granted foreground to ui pid={p.Id}");

                    var hwnd = p.MainWindowHandle;
                    if (hwnd == IntPtr.Zero) continue;

                    if (IsIconic(hwnd)) ShowWindow(hwnd, SW_RESTORE);

                    // 与 UI 侧同一套思路：改 Z 序不需要前台权限，抢焦点才需要。
                    // 用 HWND_TOP（不是 TOPMOST）：只抬到普通窗口层的顶部，不留置顶属性。
                    // 万一管道信号没送到，这一步至少能把窗口顶上来。
                    SetWindowPos(hwnd, HWND_TOP, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

                    if (SetForegroundWindow(hwnd))
                        WriteLog($"tray: foregrounded ui pid={p.Id} hwnd=0x{hwnd.ToInt64():X}");
                    else
                        // 跨进程抢焦点常被前台锁拒绝，这不算失败：Z 序已经翻转过，窗口在上面
                        WriteLog($"tray: direct SetForegroundWindow declined pid={p.Id}（已翻转 Z 序）");
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        catch { }
    }

    private static void ExitAgent()
    {
        try { _db.AddSessionEvent("shutdown"); } catch { }
        _exit = true;
    }

    /// <summary>
    /// 保证开机自启任务指向 Agent 自己（ONLOGON，无参数）。
    /// 已存在且指向当前 exe 则跳过；指向旧版 UI（--silent）则 /F 重建，自动迁移。
    ///
    /// 用户在设置页主动关闭自启后，UI 会写 <c>autostart_disabled=1</c>，
    /// 这里必须尊重该标记，否则用户下次打开 UI（UI 会拉起 Agent）时任务会被重新建回来。
    /// </summary>
    private static void EnsureSelfAutoStart()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return;

        try
        {
            if (_db.GetSetting("autostart_disabled", "0") == "1")
            {
                WriteLog("auto-start disabled by user, skip");
                return;
            }
        }
        catch { }

        var current = QueryTaskCommand();
        if (current != null && current.Equals(exe, StringComparison.OrdinalIgnoreCase)) return;

        var args = $"/Create /TN \"{TaskName}\" /SC ONLOGON /F /TR \"\\\"{exe}\\\"\"";
        if (RunSchtasks(args, elevate: false) || RunSchtasks(args, elevate: true))
            WriteLog("auto-start task registered: " + exe);
        else
            WriteLog("auto-start task registration failed");
    }

    /// <summary>
    /// 读取当前自启任务指向的 exe（无则返回 null）。
    /// <para>
    /// 优先直接读 <c>%SystemRoot%\System32\Tasks\&lt;任务名&gt;</c>：任务文件是 UTF-16 的 XML，
    /// 而 <c>schtasks /Query /XML</c> 通过管道重定向读出来时编码并不可靠，解析失败就会
    /// 让调用方误判成「任务不存在」，于是每次启动都 /F 重建一次。
    /// schtasks 只作为读不到文件时的兜底。
    /// </para>
    /// </summary>
    private static string? QueryTaskCommand()
    {
        var fromFile = QueryTaskCommandFromFile();
        if (fromFile != null) return fromFile;
        return QueryTaskCommandFromSchtasks();
    }

    private static string? QueryTaskCommandFromFile()
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.System), "Tasks");
            var path = Path.Combine(dir, TaskName);
            if (!File.Exists(path)) return null;

            var text = File.ReadAllText(path, System.Text.Encoding.Unicode);
            var m = System.Text.RegularExpressions.Regex.Match(
                text, "<Command>(.*?)</Command>",
                System.Text.RegularExpressions.RegexOptions.Singleline);
            if (!m.Success) return null;

            // 任务文件里的 Command 带 /TR 的引号，剥掉再比
            var value = m.Groups[1].Value.Trim().Trim('"');
            return value.Length == 0 ? null : value;
        }
        catch
        {
            return null;
        }
    }

    private static string? QueryTaskCommandFromSchtasks()
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
            var command = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Command")?.Value;
            return command?.Trim().Trim('"');
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

    /// <summary>把「可设前台」权限交给指定进程（调用方必须是当前前台进程）。</summary>
    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int dwProcessId);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    private const int SW_RESTORE = 9;
    private static readonly IntPtr HWND_TOP = 0;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOACTIVATE = 0x0010;
}
