using System.IO;
using Microsoft.Data.Sqlite;
using ScreenTime.Data;

namespace ScreenTime.Services;

/// <summary>
/// 浏览器历史回填：把 Chrome/Edge 的本地 History 库复制到缓存目录后只读查询，
/// 为「没有地址」的浏览器前台片段补上 site 与原始 url。
/// 只在本机读取、只回填能匹配到的片段、不联网、可在设置中关闭。
/// 与实时 UIA 采集互补：UIA 给当前标签页，历史库覆盖读取失败的时间段。
/// </summary>
public sealed class HistoryImportService : IDisposable
{
    private readonly Database _db;
    private readonly System.Threading.Timer _timer;
    private int _running;   // 防重入：上一次回填未结束时跳过本轮
    private bool _disposed;

    /// <summary>回填窗口：只处理最近 N 天的片段（历史库通常也只保留有限天数）。</summary>
    private const int WindowDays = 30;

    /// <summary>visit 匹配窗口：片段开始前 60 秒内的访问视为同一页面。</summary>
    private static readonly TimeSpan BeforeStart = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 严格窗口外的补充匹配：抖音、B 站这类单页应用长时间停留不会产生新 visit，
    /// 此时回看 60 分钟内最近一次访问，且要求网页标题与片段标题一致才回填，避免张冠李戴。
    /// </summary>
    private static readonly TimeSpan TitleFallbackWindow = TimeSpan.FromMinutes(60);

    /// <summary>敏感域名排除（子串匹配）：邮件、网银、密码管理器等不回填。</summary>
    private const string DefaultExcluded = "mail.google.com,outlook.live.com,outlook.office.com," +
        "alipay.com,tenpay.com,icbc.com.cn,ccb.com.cn,bankofchina.com,abchina.com,cmbchina.com," +
        "1password.com,lastpass.com,bitwarden.com";

    private static readonly (string Name, string UserData)[] Browsers =
    [
        ("chrome", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Google", "Chrome", "User Data")),
        ("edge", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Microsoft", "Edge", "User Data")),
    ];

    private static string CacheRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ScreenTime", "cache", "browser");

    private static string LogPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ScreenTime", "history-backfill.log");

    /// <summary>最近一次回填的摘要（设置页/验收用）。</summary>
    public string LastSummary { get; private set; } = "";

    public HistoryImportService(Database db)
    {
        _db = db;
        _timer = new System.Threading.Timer(_ => RunSafely(), null,
            System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
    }

    /// <summary>启动回填：10 秒后首次执行，之后每 30 分钟一次。</summary>
    public void Start()
    {
        if (_disposed) return;
        _timer.Change(TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(30));
    }

    private void RunSafely()
    {
        if (_db.GetSetting("history_backfill_enabled", "true") == "false") return;
        if (System.Threading.Interlocked.Exchange(ref _running, 1) == 1) return;
        try
        {
            RunOnce();
        }
        catch (Exception ex)
        {
            WriteLog($"回填失败：{ex.Message}");
        }
        finally
        {
            System.Threading.Interlocked.Exchange(ref _running, 0);
        }
    }

    /// <summary>执行一轮回填，返回写入的片段数。</summary>
    public int RunOnce()
    {
        var now = DateTime.UtcNow;
        var from = now.AddDays(-WindowDays);

        // 候选片段：无 site 的前台片段，且所属应用是浏览器
        var candidates = _db.QueryBackfillCandidates(from, now);
        if (candidates.Count == 0) return 0;

        var apps = _db.GetAllApps()
            .ToDictionary(a => a.Id, a => a.ProcessName);
        var browserOnly = candidates
            .Where(x => apps.TryGetValue(x.AppId, out var p) && BrowserTabService.IsBrowser(p))
            .ToList();
        if (browserOnly.Count == 0) return 0;

        // 候选最早时间之前的 1 天作为历史读取下界（visit 可能略早于片段开始）
        var visitFrom = browserOnly.Min(x => x.StartUtc).AddDays(-1);

        var visits = new List<(DateTime Utc, string Url, string Title)>();
        foreach (var (name, userData) in Browsers)
        {
            foreach (var profile in ProfileDirs(userData))
            {
                var cached = CopyProfile(profile, Path.Combine(CacheRoot, name, Path.GetFileName(profile)));
                if (cached == null) continue;
                try
                {
                    visits.AddRange(ReadVisits(cached, visitFrom));
                }
                catch (Exception ex)
                {
                    WriteLog($"{name}/{Path.GetFileName(profile)} 读取失败：{ex.Message}");
                }
                finally
                {
                    TryDelete(cached);
                }
            }
        }
        if (visits.Count == 0) return 0;

        visits = visits.OrderBy(v => v.Utc).ToList();

        var excluded = (_db.GetSetting("history_excluded_domains", DefaultExcluded) ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var filled = 0;
        var samples = new List<string>();

        foreach (var seg in browserOnly)
        {
            var hit = FindVisit(visits, seg.StartUtc, seg.EndUtc, seg.Title);
            if (hit == null) continue;

            var raw = hit.Value.Url;
            if (IsExcluded(raw, excluded)) continue;

            var site = BrowserTabService.ExtractPage(raw);
            if (string.IsNullOrEmpty(site)) continue;

            filled += _db.BackfillPage(seg.Id, site, CleanUrl(raw));
            if (samples.Count < 5)
                samples.Add($"{seg.StartUtc:yyyy-MM-dd HH:mm:ss}Z {site} -> {CleanUrl(raw)}");
        }

        LastSummary = $"候选 {browserOnly.Count} 条，visit {visits.Count} 条，回填 {filled} 条";
        WriteLog(LastSummary + (samples.Count > 0 ? "\n  " + string.Join("\n  ", samples) : ""));
        return filled;
    }

    /// <summary>
    /// 取距片段开始时间最近的一次访问：
    /// 1) 严格窗口 [start-60s, end]：切到前台那一刻打开的页面，最可信；
    /// 2) 无访问时回看 30 分钟，仅当网页标题与片段标题一致才采用（单页应用不产生新 visit）。
    /// </summary>
    private static (DateTime Utc, string Url, string Title)? FindVisit(
        List<(DateTime Utc, string Url, string Title)> visits, DateTime start, DateTime end, string title)
    {
        var best = Nearest(visits, start, start - BeforeStart, end);
        if (best != null) return best;

        var key = NormalizeTitle(title);
        if (key.Length < 4) return null;

        var lo = start - TitleFallbackWindow;
        var hi = start - BeforeStart;
        (DateTime, string, string)? fallback = null;
        var fallbackDelta = TimeSpan.MaxValue;
        for (var i = LowerBound(visits, lo); i < visits.Count && visits[i].Utc < hi; i++)
        {
            var v = visits[i];
            if (!TitleMatches(key, v.Title)) continue;
            var delta = (v.Utc - start).Duration();
            if (delta < fallbackDelta) { fallbackDelta = delta; fallback = v; }
        }
        return fallback;
    }

    private static (DateTime Utc, string Url, string Title)? Nearest(
        List<(DateTime Utc, string Url, string Title)> visits,
        DateTime start, DateTime lo, DateTime hi)
    {
        (DateTime, string, string)? best = null;
        var bestDelta = TimeSpan.MaxValue;
        for (var i = LowerBound(visits, lo); i < visits.Count && visits[i].Utc <= hi; i++)
        {
            var delta = (visits[i].Utc - start).Duration();
            if (delta < bestDelta) { bestDelta = delta; best = visits[i]; }
        }
        return best;
    }

    private static bool TitleMatches(string segmentKey, string urlTitle)
    {
        var urlKey = NormalizeTitle(urlTitle);
        if (urlKey.Length < 4) return false;
        return urlKey == segmentKey || urlKey.Contains(segmentKey) || segmentKey.Contains(urlKey);
    }

    private static string NormalizeTitle(string title)
    {
        var cleaned = BrowserTabService.CleanTitle(title);
        return string.Join(" ", cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();
    }

    private static int LowerBound(List<(DateTime Utc, string Url, string Title)> visits, DateTime t)
    {
        var lo = 0;
        var hi = visits.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (visits[mid].Utc < t) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }

    /// <summary>遍历 User Data 下所有含 History 的配置目录（跳过 Guest/System Profile）。</summary>
    private static IEnumerable<string> ProfileDirs(string userData)
    {
        if (!Directory.Exists(userData)) yield break;
        foreach (var dir in Directory.EnumerateDirectories(userData))
        {
            var name = Path.GetFileName(dir);
            if (name.Equals("Guest Profile", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("System Profile", StringComparison.OrdinalIgnoreCase))
                continue;
            if (File.Exists(Path.Combine(dir, "History"))) yield return dir;
        }
    }

    /// <summary>
    /// 复制 History 主文件及 journal/wal/shm 到缓存目录。
    /// Chrome 用 rollback journal、Edge 也可能存在 -wal，存在即拷、不存在则清掉旧副本，
    /// 否则残留文件会让只读打开读到不一致数据。
    /// </summary>
    private static string? CopyProfile(string srcDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        var target = Path.Combine(destDir, "History");
        foreach (var suffix in new[] { "", "-journal", "-wal", "-shm" })
        {
            var src = Path.Combine(srcDir, "History" + suffix);
            var dst = Path.Combine(destDir, "History" + suffix);
            try
            {
                if (File.Exists(src)) File.Copy(src, dst, overwrite: true);
                else if (File.Exists(dst)) File.Delete(dst);
            }
            catch (IOException)
            {
                // 浏览器正在写入时可能短暂占用，本次跳过该文件
            }
        }
        return File.Exists(target) ? target : null;
    }

    private static List<(DateTime Utc, string Url, string Title)> ReadVisits(string dbPath, DateTime fromUtc)
    {
        var list = new List<(DateTime, string, string)>();
        var cs = new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadOnly,
        }.ToString();

        using var conn = new SqliteConnection(cs);
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT v.visit_time, u.url, COALESCE(u.title, '')
            FROM visits v JOIN urls u ON u.id = v.url
            WHERE v.visit_time >= $f
            ORDER BY v.visit_time
            """;
        cmd.Parameters.AddWithValue("$f", ToChromeMicros(fromUtc));
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var url = r.GetString(1);
            if (string.IsNullOrWhiteSpace(url)) continue;
            list.Add((FromChromeMicros(r.GetInt64(0)), url, r.GetString(2)));
        }
        return list;
    }

    /// <summary>Chrome/Edge 的 visit_time：1601-01-01 UTC 起的微秒数。</summary>
    private static readonly DateTime ChromeEpoch = new(1601, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static long ToChromeMicros(DateTime utc) => (utc - ChromeEpoch).Ticks / 10;

    private static DateTime FromChromeMicros(long micros) => ChromeEpoch.AddTicks(micros * 10);

    private static bool IsExcluded(string url, string[] excluded)
    {
        if (excluded.Length == 0) return false;
        var lower = url.ToLowerInvariant();
        return excluded.Any(e => lower.Contains(e.ToLowerInvariant()));
    }

    /// <summary>去掉 fragment 与追踪参数，截断到 200 字符，仅用于点击跳转。</summary>
    private static string CleanUrl(string url)
    {
        url = url.Trim();
        var hash = url.IndexOf('#');
        if (hash >= 0) url = url[..hash];

        var q = url.IndexOf('?');
        if (q >= 0)
        {
            var head = url[..q];
            var kept = url[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Where(p =>
                {
                    var name = p.Split('=')[0].ToLowerInvariant();
                    return !name.StartsWith("utm_") &&
                           name is not ("gclid" or "fbclid" or "msclkid" or "spm_id_from" or "vd_source"
                               or "share_source" or "share_medium");
                })
                .ToList();
            url = kept.Count == 0 ? head : head + "?" + string.Join("&", kept);
        }
        return url.Length > 200 ? url[..200] : url;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }

    private static void WriteLog(string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            File.AppendAllText(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}\n");
        }
        catch { }
    }

    public void Dispose()
    {
        _disposed = true;
        _timer.Dispose();
    }
}
