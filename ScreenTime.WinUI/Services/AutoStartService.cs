using System.Diagnostics;

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

    public static bool Enable()
    {
        var exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe)) return false;

        var args = $"/Create /TN \"{TaskName}\" /SC ONLOGON /RL HIGHEST /F /TR \"\\\"{exe}\\\" --silent\"";

        // 先静默尝试（进程已是管理员时直接成功）；权限不足时提升权限再试一次。
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
