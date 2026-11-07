using System.Diagnostics;
using System.IO;

namespace ScreenTime.Services;

/// <summary>
/// 通过任务计划程序实现开机自启（ONLOGON + 最高权限），
/// 这样可记录管理员权限窗口且不弹 UAC。
/// 任务以 --silent 启动：只创建托盘，不打开主窗口。
/// </summary>
public static class AutoStartService
{
    private const string TaskName = "ScreenTime";

    public static bool IsEnabled()
    {
        // 优先看任务文件是否存在；读不到内容也不影响「有没有开」这个判断
        if (IsTaskFilePresent()) return true;
        return QueryTaskXmlViaSchtasks() != null;
    }

    /// <summary>
    /// 一次查询同时拿到「任务是否存在」和「是否最高权限」。
    /// 设置页每次加载都要读状态，两次 schtasks 调用会白等几百毫秒。
    /// </summary>
    public static (bool Enabled, bool RunAsAdmin) GetState()
    {
        var xml = ReadTaskFile() ?? QueryTaskXmlViaSchtasks();

        // 任务文件内容读不到（权限/编码）时，至少用存在性判断：
        // 否则会把「已经开着」显示成「关着」，用户以为开关坏了。
        var exists = xml != null || IsTaskFilePresent();
        if (!exists) return (false, false);

        var admin = xml?.Contains("HighestAvailable", StringComparison.OrdinalIgnoreCase) ?? false;
        return (true, admin);
    }

    /// <summary>当前任务是否以最高权限运行（读任务 XML 里的 RunLevel）。</summary>
    public static bool IsRunAsAdmin()
    {
        var xml = ReadTaskFile() ?? QueryTaskXmlViaSchtasks();
        return xml != null && xml.Contains("HighestAvailable", StringComparison.OrdinalIgnoreCase);
    }

    private static string TaskFilePath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "Tasks", TaskName);

    private static bool IsTaskFilePresent()
    {
        try
        {
            return File.Exists(TaskFilePath());
        }
        catch
        {
            return false;
        }
    }

    /// <summary>直接读计划任务定义文件（UTF-16 XML），不依赖 schtasks 输出。</summary>
    private static string? ReadTaskFile()
    {
        try
        {
            var path = TaskFilePath();
            if (!File.Exists(path)) return null;
            return File.ReadAllText(path, System.Text.Encoding.Unicode);
        }
        catch
        {
            return null;
        }
    }

    private static string? QueryTaskXml() => ReadTaskFile() ?? QueryTaskXmlViaSchtasks();

    private static string? QueryTaskXmlViaSchtasks()
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
            return p.ExitCode == 0 ? output : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 创建/重建开机自启任务。
    /// <paramref name="runAsAdmin"/> 默认 false：实测普通权限下同样能读到提升权限应用的
    /// 进程名、exe 路径和窗口标题（GetWindowText 跨完整性可读），
    /// 而 /RL HIGHEST 会让创建任务必须提权、每次登录都以管理员身份运行，代价大于收益。
    ///
    /// 注意：自启目标从 UI 进程改为采集器进程（ScreenTime.Agent.exe，无参数）。
    /// 采集/托盘由 Agent 常驻承担；Agent 启动时也会自查任务指向自己，双向兜底。
    /// </summary>
    public static bool Enable(bool runAsAdmin = false)
    {
        var dir = Path.GetDirectoryName(Environment.ProcessPath);
        var exe = dir is null ? null : Path.Combine(dir, "ScreenTime.Agent.exe");
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe)) return false;

        var level = runAsAdmin ? " /RL HIGHEST" : "";
        var args = $"/Create /TN \"{TaskName}\" /SC ONLOGON{level} /F /TR \"\\\"{exe}\\\"\"";

        // 先静默尝试（普通权限任务通常无需提权）；权限不足时提升权限再试一次。
        var ok = RunSchtasks(args, elevate: false) || RunSchtasks(args, elevate: true);
        if (ok) TryEnableRestartOnFailure();
        return ok;
    }

    /// <summary>
    /// 给任务加「失败后自动重启」：进程以非 0 退出码结束（未捕获异常等）时，
    /// 计划任务在 1 分钟后最多再拉起 3 次，避免掉线后干等到下次登录。
    /// schtasks 命令行没有这个参数，Set-ScheduledTask 又会把 Interval 序列化成
    /// 非 ISO8601 的 "00:01:00" 被 schema 拒绝（实测 HRESULT 0x80041318），
    /// 所以直接改任务 XML 再导入。失败只写日志，不影响自启本身。
    /// </summary>
    private static void TryEnableRestartOnFailure()
    {
        try
        {
            var xml = QueryTaskXml();
            if (string.IsNullOrWhiteSpace(xml)) return;

            var doc = new System.Xml.XmlDocument();
            doc.LoadXml(xml);
            var root = doc.DocumentElement;
            var settings = root?["Settings"];
            if (root is null || settings is null) return;
            if (settings["RestartOnFailure"] is not null) return;   // 已经有了

            var ns = root.NamespaceURI;
            var restart = doc.CreateElement("RestartOnFailure", ns);
            var interval = doc.CreateElement("Interval", ns);
            interval.InnerText = "PT1M";
            var count = doc.CreateElement("Count", ns);
            count.InnerText = "3";
            restart.AppendChild(interval);
            restart.AppendChild(count);
            settings.AppendChild(restart);

            // 任务 XML 必须是 UTF-16，和系统里的 Tasks 文件一致
            var path = Path.Combine(Path.GetTempPath(), $"st-task-{Environment.ProcessId}.xml");
            var ws = new System.Xml.XmlWriterSettings
            {
                Encoding = System.Text.Encoding.Unicode,
                Indent = true
            };
            using (var writer = System.Xml.XmlWriter.Create(path, ws))
                doc.Save(writer);

            var import = $"/Create /TN \"{TaskName}\" /XML \"{path}\" /F";
            if (!RunSchtasks(import, elevate: false))
                RunSchtasks(import, elevate: true);

            try { File.Delete(path); } catch { }
        }
        catch (Exception ex)
        {
            Log($"restart-on-failure threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public static bool Disable()
    {
        const string args = $"/Delete /TN \"{TaskName}\" /F";
        return RunSchtasks(args, elevate: false) || RunSchtasks(args, elevate: true);
    }

    /// <summary>
    /// 执行 schtasks。Verb=runas 必须与 UseShellExecute=true 搭配，
    /// 否则 .NET 会抛 InvalidOperationException（旧实现的失败原因）。
    /// </summary>
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

            // 先读完输出再等退出，避免管道缓冲区满导致死锁
            var stdout = elevate ? "" : p.StandardOutput.ReadToEnd();
            var stderr = elevate ? "" : p.StandardError.ReadToEnd();
            if (!p.WaitForExit(elevate ? 60000 : 15000))
            {
                Log($"schtasks timeout: {args}");
                return false;
            }
            if (p.ExitCode != 0)
            {
                Log($"schtasks exit={p.ExitCode} args={args} stderr={stderr.Trim()} stdout={stdout.Trim()}");
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            // 用户取消 UAC 等情形
            Log($"schtasks threw: {args} | {ex.GetType().Name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>自启相关失败写日志，方便排查「开关点了没反应」。</summary>
    private static void Log(string message)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenTime");
            Directory.CreateDirectory(dir);
            LogFile.Append(Path.Combine(dir, "autostart.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}\n");
        }
        catch
        {
        }
    }
}
