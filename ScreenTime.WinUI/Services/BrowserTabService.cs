using System.Runtime.InteropServices;
using ScreenTime.Native;

namespace ScreenTime.Services;

/// <summary>
/// 浏览器页面读取：前台为 Chrome/Edge 时，通过 UI Automation 读取当前页面的地址，
/// 归一化为「域名/路径」形式的页面标识（例如 github.com/trae-ai）；
/// 地址栏失焦时 Chromium 只显示域名，因此优先读 Document 元素暴露的完整 URL。
/// 只统计当前活动标签页；读取在后台线程完成并缓存，避免阻塞 UI 线程。
/// </summary>
public sealed class BrowserTabService
{
    private IUiautomation? _automation;
    private nint _editCondition;
    private nint _docCondition;
    private readonly object _gate = new();

    // hwnd -> 最近读到的页面标识
    private readonly Dictionary<nint, string> _cache = new();
    // 正在读取的 hwnd（带开始时间与令牌，避免卡死任务永久占位）
    private readonly Dictionary<nint, (DateTime Start, int Token)> _inflight = new();
    private int _token;

    /// <summary>单次 UIA 读取的占位超时：超时后下一次心跳重新尝试，不做次数上限。</summary>
    private static readonly TimeSpan InflightTimeout = TimeSpan.FromSeconds(5);

    private static readonly string[] BrowserProcesses = ["chrome", "msedge"];

    public static bool IsBrowser(string processName)
    {
        var n = (processName ?? "").ToLowerInvariant();
        if (n.EndsWith(".exe")) n = n[..^4];
        return Array.IndexOf(BrowserProcesses, n) >= 0;
    }

    private IUiautomation Automation
    {
        get
        {
            if (_automation == null)
                _automation = (IUiautomation)new CUiautomationClass();
            return _automation;
        }
    }

    /// <summary>
    /// 返回指定浏览器窗口当前活动标签页的页面标识（缓存值），并在后台刷新。
    /// 首次读取或切换标签页时，结果可能延迟数秒更新。
    /// </summary>
    public string GetSite(nint hwnd, string processName)
    {
        if (hwnd == 0 || !IsBrowser(processName)) return "";

        lock (_gate)
        {
            // 上一次读取仍在进行且未超时：先返回缓存，避免重叠读取
            if (_inflight.TryGetValue(hwnd, out var running) &&
                DateTime.UtcNow - running.Start < InflightTimeout)
                return _cache.TryGetValue(hwnd, out var cached) ? cached : "";

            var token = ++_token;
            _inflight[hwnd] = (DateTime.UtcNow, token);

            var h = hwnd;
            var p = processName;
            System.Threading.Tasks.Task.Run(() =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    var site = ReadSite(h, p);
                    sw.Stop();
                    if (sw.ElapsedMilliseconds > 2000)
                        WriteTimingLog($"{p} hwnd={h} 读取耗时 {sw.ElapsedMilliseconds}ms 结果={(string.IsNullOrEmpty(site) ? "空" : site)}");
                    lock (_gate)
                    {
                        // 读到空值（页面加载中/切换瞬间）时保留旧缓存
                        if (!string.IsNullOrEmpty(site)) _cache[h] = site;
                    }
                }
                catch (Exception ex)
                {
                    // 读取失败：保留旧缓存，记下原因便于复测
                    WriteTimingLog($"{p} hwnd={h} 读取异常 {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    lock (_gate)
                    {
                        // 只清理自己的占位：超时后可能已有新任务接手
                        if (_inflight.TryGetValue(h, out var cur) && cur.Token == token)
                            _inflight.Remove(h);
                    }
                }
            });
            return _cache.TryGetValue(hwnd, out var s) ? s : "";
        }
    }

    private static void WriteTimingLog(string line)
    {
        try
        {
            var path = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ScreenTime", "browser-uia.log");
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            System.IO.File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss} {line}\n");
        }
        catch { }
    }

    private string ReadSite(nint hwnd, string processName)
    {
        var auto = Automation;
        if (auto.ElementFromHandle(hwnd, out var element) != 0 || element == null) return "";
        try
        {
            // 1) 页面级地址：Document 元素暴露当前 URL（地址栏失焦时仍准确）
            var page = ExtractPage(TryReadDocumentUrl(auto, element));
            if (page != "") return page;

            // 2) 回退：读地址栏（失焦时通常只有域名）
            return ExtractPage(ReadAddressBarUrl(auto, element, hwnd));
        }
        finally
        {
            Marshal.ReleaseComObject(element);
        }
    }

    /// <summary>读 Document 元素（ControlType=50030）的 ValuePattern，即当前页面 URL。</summary>
    private string TryReadDocumentUrl(IUiautomation auto, IUiautomationElement window)
    {
        var cond = GetCondition(auto, UiaIds.DocumentControlType, ref _docCondition);
        if (cond == 0) return "";
        // 地址栏未暴露时可能还要触发 Chromium 的按需辅助功能，这里失败即回退，不重复触发
        if (window.FindFirst(UiaIds.TreeScopeDescendants, cond, out var doc) != 0 || doc == null)
            return "";
        try
        {
            return ReadValuePattern(doc);
        }
        finally
        {
            Marshal.ReleaseComObject(doc);
        }
    }

    private string ReadAddressBarUrl(IUiautomation auto, IUiautomationElement window, nint hwnd)
    {
        var cond = GetCondition(auto, UiaIds.EditControlType, ref _editCondition);
        if (cond == 0) return "";

        if (window.FindFirst(UiaIds.TreeScopeDescendants, cond, out var edit) != 0 || edit == null)
        {
            // 地址栏未暴露时不再抢焦点触发按需辅助功能：
            // SetFocus 会打断用户输入，且对后台窗口无效。宁可本次读不到，
            // 由 HistoryImportService 从历史库回填（预期内的降级）。
            return "";
        }

        try
        {
            return ReadValuePattern(edit);
        }
        finally
        {
            Marshal.ReleaseComObject(edit);
        }
    }

    private static string ReadValuePattern(IUiautomationElement element)
    {
        if (element.GetCurrentPattern(UiaIds.ValuePatternId, out var patternRaw) != 0 || patternRaw == 0)
            return "";
        try
        {
            var valuePattern = (IUiautomationValuePattern?)Marshal.GetObjectForIUnknown(patternRaw);
            if (valuePattern == null) return "";
            if (valuePattern.get_CurrentValue(out var value) != 0) return "";
            return value ?? "";
        }
        finally
        {
            Marshal.Release(patternRaw);
        }
    }

    private static nint GetCondition(IUiautomation auto, int controlType, ref nint cache)
    {
        if (cache != 0) return cache;
        object value = controlType;
        if (auto.CreatePropertyCondition(UiaIds.ControlTypeProperty, value, out var cond) != 0)
            return 0;
        cache = cond;
        return cond;
    }

    /// <summary>
    /// 把地址归一化为页面标识：
    ///   https://chat.deepseek.com/a/chat/s/xxx?q=1#top → chat.deepseek.com/a/chat/s/xxx
    ///   https://github.com/                          → github.com
    ///   https://www.bilibili.com/video/BV1xx          → bilibili.com/video/BV1xx
    ///   chrome://settings                             → chrome
    ///   about:blank                                   → about
    /// 去 query/fragment、去 www.、限制长度，保证同一页面聚合稳定。
    /// </summary>
    public static string ExtractPage(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "";
        url = url.Trim();

        var schemeIndex = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeIndex < 0)
        {
            var colon = url.IndexOf(':');
            if (colon > 0)
            {
                var scheme = url[..colon].ToLowerInvariant();
                if (scheme is "chrome" or "edge" or "about" or "devtools")
                    return scheme;
            }
            // 地址栏偶尔只显示纯域名/路径（无 scheme）
            return NormalizePage(url);
        }

        var schemeName = url[..schemeIndex].ToLowerInvariant();
        if (schemeName == "view-source" && url.Length > schemeIndex + 3)
            return ExtractPage(url[(schemeIndex + 3)..]);
        if (schemeName is "chrome" or "edge" or "about" or "chrome-extension" or
            "ms-browser-extension" or "devtools" or "moz-extension")
            return schemeName;

        return NormalizePage(url[(schemeIndex + 3)..]);
    }

    private static string NormalizePage(string afterScheme)
    {
        // 去掉 fragment 与 query，避免同一页面的不同锚点/参数被拆成多条
        var hash = afterScheme.IndexOf('#');
        if (hash >= 0) afterScheme = afterScheme[..hash];
        var query = afterScheme.IndexOf('?');
        if (query >= 0) afterScheme = afterScheme[..query];

        var slash = afterScheme.IndexOf('/');
        var host = slash >= 0 ? afterScheme[..slash] : afterScheme;
        var path = slash >= 0 ? afterScheme[slash..].TrimEnd('/') : "";

        if (host.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
            host = host[4..];
        if (host == "") return path.TrimStart('/');

        var page = path == "" || path == "/" ? host : host + path;
        // 超长路径截断，避免个别 URL 撑爆数据库与列表
        return page.Length > 120 ? page[..120] : page;
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
