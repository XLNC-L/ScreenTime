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

            var target = ResolveTarget(lnk);
            if (string.IsNullOrEmpty(target)) continue;
            if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            if (!File.Exists(target)) continue;

            // PWA（Chrome/Edge 应用）不算独立应用，时间归浏览器
            if (PwaLaunchers.Contains(Path.GetFileNameWithoutExtension(target))) continue;

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
            var category = CategoryService.Classify(processName, target, "", lnkName);
            if (category == CategoryService.Unclassified) continue;

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

    /// <summary>用 WScript.Shell 解析 .lnk 的目标路径（不引入额外依赖）。</summary>
    private static string? ResolveTarget(string lnkPath)
    {
        try
        {
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type == null) return null;
            var shell = Activator.CreateInstance(type);
            if (shell == null) return null;

            var shortcut = type.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnkPath });
            if (shortcut == null) return null;

            var target = shortcut.GetType().InvokeMember("TargetPath",
                BindingFlags.GetProperty, null, shortcut, null) as string;

            if (string.IsNullOrEmpty(target)) return null;
            return Environment.ExpandEnvironmentVariables(target.Trim().Trim('"'));
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
