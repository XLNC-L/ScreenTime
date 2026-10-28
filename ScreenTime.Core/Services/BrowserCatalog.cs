namespace ScreenTime.Services;

/// <summary>
/// 浏览器的纯字符串知识：进程名判断、窗口标题清洗。
/// 单独放这里是为了让数据层（Database）不必依赖 BrowserTabService ——
/// 后者要拉起 UIAutomation COM，属于 UI 侧的东西，不该被纯逻辑层引用。
/// </summary>
public static class BrowserCatalog
{
    private static readonly string[] BrowserProcesses = ["chrome", "msedge"];

    /// <summary>该进程是否属于当前支持页面级统计的浏览器。</summary>
    public static bool IsBrowser(string? processName)
    {
        var n = (processName ?? "").ToLowerInvariant();
        if (n.EndsWith(".exe")) n = n[..^4];
        return Array.IndexOf(BrowserProcesses, n) >= 0;
    }

    /// <summary>
    /// 窗口标题 → 网页标题（去掉 " - Google Chrome" / " - 个人 - Microsoft Edge"
    /// / " 和另外 N 个页面" 等浏览器后缀）。
    /// </summary>
    public static string CleanTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";

        var parts = title.Split(" - ").ToList();
        while (parts.Count > 1)
        {
            var last = parts[^1].Trim().ToLowerInvariant();
            if (TitleSuffixes.Contains(last)) parts.RemoveAt(parts.Count - 1);
            else break;
        }

        var head = parts[0];
        var extra = head.IndexOf(" 和另外", StringComparison.Ordinal);
        if (extra > 0) head = head[..extra];
        parts[0] = head;

        return string.Join(" - ", parts).Trim();
    }

    private static readonly HashSet<string> TitleSuffixes = new(StringComparer.OrdinalIgnoreCase)
    {
        "google chrome", "microsoft edge", "chromium", "brave", "opera",
        "mozilla firefox", "个人", "工作", "其他用户", "个人资料",
    };
}
