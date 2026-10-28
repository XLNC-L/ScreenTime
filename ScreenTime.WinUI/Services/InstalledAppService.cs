using System.IO;
using System.Reflection;
using ScreenTime.Data;

namespace ScreenTime.Services;

/// <summary>
/// 扫描开始菜单快捷方式，把「已安装但还没用过」的应用补进应用列表，
/// 便于提前分类（时长为 0，不影响统计）。只导入能自动分类的应用，
/// 避免把一堆未知程序塞进收件箱。
/// </summary>
public static class InstalledAppService
{
    private static readonly string[] SkipKeywords =
    [
        "uninstall", "卸载", "readme", "帮助", "help", "changelog", "更新日志",
        "repair", "修复", "license", "许可", "文档", "website", "官网", "support",
    ];

    /// <summary>
    /// 浏览器 PWA 的启动器。PWA 窗口实际由 chrome.exe / msedge.exe 承载，
    /// 启动器只在点开瞬间闪一下，永远不会有自己的时长。
    /// 把它们留在应用列表里只是一条 0 分钟的干扰项，网页归属在「网页」页照样看得到。
    /// </summary>
    private static readonly HashSet<string> PwaLaunchers = new(StringComparer.OrdinalIgnoreCase)
    {
        "chrome_proxy", "msedge_proxy",
    };

    /// <summary>
    /// 重扫一遍应用的 exe 位置。程序升级、换目录、UWP 版本号变化后，
    /// 库里记录的路径会失效（图标、「打开所在文件夹」都会跟着坏）。
    /// 依次用「运行中的进程 → 开始菜单快捷方式 → UWP 包」三类来源补齐，
    /// 只处理当前路径已失效的条目，路径还有效的不动。
    /// </summary>
    /// <returns>(更新数, 检查总数)</returns>
    public static async Task<(int Updated, int Total)> RefreshPathsAsync(Database db)
    {
        var apps = db.GetAllApps();
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // 1) 运行中的进程最准
        foreach (var p in System.Diagnostics.Process.GetProcesses())
        {
            try
            {
                var file = p.MainModule?.FileName;
                if (!string.IsNullOrEmpty(file)) map.TryAdd(p.ProcessName, file);
            }
            catch
            {
                // 受保护进程读不到模块路径，跳过
            }
            finally
            {
                p.Dispose();
            }
        }

        // 2) 开始菜单快捷方式：程序没在跑时也能拿到
        foreach (var lnk in EnumerateShortcuts())
        {
            var resolved = ResolveShortcut(lnk);
            if (resolved is not { } sc) continue;
            if (!sc.Target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            var process = Path.GetFileNameWithoutExtension(sc.Target);
            if (!string.IsNullOrEmpty(process)) map.TryAdd(process, sc.Target);
        }

        // 3) UWP 包：升级后版本目录会变
        foreach (var kv in await UwpAppService.GetExePathMapAsync())
            map.TryAdd(kv.Key, kv.Value);

        var updated = 0;
        foreach (var a in apps)
        {
            if (!string.IsNullOrEmpty(a.ExePath) && File.Exists(a.ExePath)) continue;
            if (!map.TryGetValue(a.ProcessName, out var path)) continue;
            if (!File.Exists(path)) continue;
            if (db.UpdateAppExePath(a.Id, path)) updated++;
        }

        return (updated, apps.Count);
    }

    /// <summary>执行导入，返回新增的应用数量。</summary>
    public static int Import(Database db)
    {
        var existing = db.GetAllApps()
            .Select(a => a.ExePath)
            .Where(p => !string.IsNullOrEmpty(p))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var added = 0;

        foreach (var lnk in EnumerateShortcuts())
        {
            var lnkName = Path.GetFileNameWithoutExtension(lnk);
            if (lnkName.Length == 0 || ContainsAny(lnkName, SkipKeywords)) continue;

            var resolved = ResolveShortcut(lnk);
            if (resolved is not { } sc) continue;
            var (target, args) = sc;
            if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            if (!File.Exists(target)) continue;

            // 浏览器的「网页应用」(PWA) 不算独立应用，时间归浏览器
            if (IsPwaShortcut(target, args)) continue;

            // 控制面板、注册表编辑器这类 Windows 自带小工具不算「已安装应用」，
            // 既不该出现在列表里，也不该被提前分类。
            if (IsWindowsTool(target))
            {
                // 之前导入过且从未使用过的，顺手清掉
                db.DeleteAppIfUnused(target);
                continue;
            }

            if (!seen.Add(target) || existing.Contains(target)) continue;

            var processName = Path.GetFileNameWithoutExtension(target);
            // 分类不出来也导入：进「收件箱」由用户手动归类。
            // 之前这里直接丢弃，导致一大批小众软件永远进不了列表。
            var category = CategoryService.Classify(processName, target, "", lnkName);

            var display = AppDisplayService.DisplayName(processName, lnkName);
            db.GetOrCreateApp(target, processName, display, "", category);
            added++;
        }

        return added;
    }

    private static IEnumerable<string> EnumerateShortcuts()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
        };

        foreach (var root in roots)
        {
            if (string.IsNullOrEmpty(root)) continue;
            var dir = Path.Combine(root, "Programs");
            if (!Directory.Exists(dir)) continue;

            string[] files;
            try { files = Directory.GetFiles(dir, "*.lnk", SearchOption.AllDirectories); }
            catch { continue; }

            foreach (var f in files) yield return f;
        }
    }

    /// <summary>
    /// 判断快捷方式是否指向浏览器的「网页应用」(PWA)。三个判据任一命中即算：
    ///   1) 目标是 Chromium 系的 <c>&lt;brand&gt;_proxy.exe</c>——Chrome / Edge / Brave /
    ///      Vivaldi / Opera 的 PWA 启动器都是这个命名规律；
    ///   2) 参数带 <c>--app-id=</c>——Chromium 安装 PWA 时的固定写法；
    ///   3) 参数带 <c>--app=</c> 或 <c>--ssb</c>——Edge / Firefox 的站点独立窗口。
    /// 这类条目不该进应用列表：窗口实际由浏览器进程承载，永远不会有自己的时长。
    /// </summary>
    private static bool IsPwaShortcut(string target, string args)
    {
        var exe = Path.GetFileNameWithoutExtension(target);
        if (exe.EndsWith("_proxy", StringComparison.OrdinalIgnoreCase)) return true;
        if (PwaLaunchers.Contains(exe)) return true;

        if (string.IsNullOrEmpty(args)) return false;
        return args.Contains("--app-id=", StringComparison.OrdinalIgnoreCase)
            || args.Contains("--app=", StringComparison.OrdinalIgnoreCase)
            || args.Contains("--ssb", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>用 WScript.Shell 解析 .lnk 的目标路径与参数（不引入额外依赖）。</summary>
    private static (string Target, string Args)? ResolveShortcut(string lnkPath)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return null;
            var shell = Activator.CreateInstance(type);
            if (shell == null) return null;

            var shortcut = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null,
                shell, new object[] { lnkPath });
            if (shortcut == null) return null;

            var target = shortcut.GetType().InvokeMember("TargetPath",
                BindingFlags.GetProperty, null, shortcut, null) as string;
            var args = shortcut.GetType().InvokeMember("Arguments",
                BindingFlags.GetProperty, null, shortcut, null) as string;

            if (string.IsNullOrEmpty(target)) return null;
            return (Environment.ExpandEnvironmentVariables(target.Trim().Trim('"')), args ?? "");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>目标程序是否位于 Windows 目录（系统自带工具）。</summary>
    private static bool IsWindowsTool(string target)
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (string.IsNullOrEmpty(root)) return false;
        return target.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsAny(string text, string[] keywords)
    {
        var lower = text.ToLowerInvariant();
        foreach (var k in keywords)
            if (lower.Contains(k, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
