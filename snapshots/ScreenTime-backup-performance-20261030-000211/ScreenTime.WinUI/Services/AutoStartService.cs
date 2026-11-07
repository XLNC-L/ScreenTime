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
        try
        {
            var psi = new ProcessStartInfo("schtasks", $"/Query /TN \"{TaskName}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using var p = Process.Start(psi);
            if (p == null) return false;
            if (!p.WaitForExit(5000)) return false;
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>当前任务是否以最高权限运行（读任务 XML 里的 RunLevel）。</summary>
    public static bool IsRunAsAdmin()
    {
        var xml = QueryTaskXml();
        return xml != null && xml.Contains("HighestAvailable", StringComparison.OrdinalIgnoreCase);
    }

    private static string? QueryTaskXml()
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
        return RunSchtasks(args, elevate: false) || RunSchtasks(args, elevate: true);
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
            if (!p.WaitForExit(elevate ? 60000 : 15000)) return false;
            return p.ExitCode == 0;
        }
        catch
        {
            // 用户取消 UAC 等情形
            return false;
        }
    }
}
