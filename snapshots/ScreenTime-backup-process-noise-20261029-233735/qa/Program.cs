// 独立验收注入测试（QA）：不信任前提，用可注入数据验证边界与聚合。
// 运行：dotnet run --project qa  （退出码 0 = 全部通过）
//       dotnet run --project qa -- check <db路径>   （库完整性：integrity_check / 片段重叠检测）
// 覆盖：B1 跨午夜切分 / A3 视图自洽 / A4 分类修改 / C3 写入幂等 / B4 脏数据(倒序/零长) / C2 UTC口径
using ScreenTime.Data;
using ScreenTime.Services;

if (args.Length == 3 && args[0] == "set")
{
    var path = args[1];
    var setDb = new Database(path);
    setDb.Init();
    var kv = args[2].Split('=', 2);
    setDb.SetSetting(kv[0], kv[1]);
    Console.WriteLine($"set {kv[0]}={kv[1]}");
    Environment.Exit(0);
}

if (args.Length == 2 && args[0] == "apps")
{
    var path = args[1];
    var appDb = new Database(path);
    appDb.Init();
    var rows = appDb.QueryAppUsage(DateTime.UtcNow.AddHours(-2), DateTime.UtcNow);
    foreach (var r in rows.OrderByDescending(x => x.TotalSeconds))
        Console.WriteLine($"{r.AppName,-16} fg={r.ForegroundSeconds,7:0.0}s  total={r.TotalSeconds,7:0.0}s  ({r.ProcessName})");
    Environment.Exit(0);
}

if (args.Length >= 2 && args[0] == "check")
{
    var path = args[1];
    var checkDb = new Database(path);
    checkDb.Init();
    using var c = new Microsoft.Data.Sqlite.SqliteConnection(
        $"Data Source={Path.GetFullPath(path)}");
    c.Open();
    var integ = new Microsoft.Data.Sqlite.SqliteCommand("PRAGMA integrity_check", c).ExecuteScalar()!.ToString();
    var segCount = (long)new Microsoft.Data.Sqlite.SqliteCommand("SELECT COUNT(*) FROM usage_segments", c).ExecuteScalar()!;
    var overlap = new Microsoft.Data.Sqlite.SqliteCommand(
        """
        SELECT COUNT(*) FROM (
          SELECT s1.id FROM usage_segments s1
          JOIN usage_segments s2 ON s1.id < s2.id AND s1.app_id = s2.app_id AND s1.kind = s2.kind
          WHERE s1.start_ts < s2.end_ts AND s2.start_ts < s1.end_ts
        )
        """, c).ExecuteScalar()!.ToString();
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var week = checkDb.QueryAppUsage(DateTime.UtcNow.AddDays(-7), DateTime.UtcNow);
    sw.Stop();
    var timeline = checkDb.QueryTimeline(DateTime.Now);
    var recent = checkDb.QueryAppUsage(DateTime.UtcNow.AddSeconds(-90), DateTime.UtcNow).Sum(r => r.TotalSeconds);
    Console.WriteLine($"integrity_check={integ}  segments={segCount}  same-app-overlap-pairs={overlap}  " +
        $"7d_query_ms={sw.ElapsedMilliseconds}  apps={week.Count}  timeline_rows={timeline.Count}  recent90s={recent:0.0}");
    Environment.Exit(0);
}

var failures = new List<string>();
var dbPath = Path.Combine(Path.GetTempPath(), "st-qa", "qa.db");
if (Directory.Exists(Path.GetDirectoryName(dbPath)))
    Directory.Delete(Path.GetDirectoryName(dbPath)!, true);
Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
var db = new Database(dbPath);
db.Init();

void Check(string name, bool ok, string detail)
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}  {detail}");
    if (!ok) failures.Add(name);
}

// ---------- B1 跨午夜切分 ----------
// 本地时间 2026-10-28 23:58 → 2026-10-29 00:02（4 分钟横跨两天）
var localDay1 = new DateTime(2026, 10, 28);
var localDay2 = new DateTime(2026, 10, 29);
var appId = db.GetOrCreateApp("D:\\qa\\notepad.exe", "notepad", "记事本", "", "办公与文档");
var startLocal = new DateTime(2026, 10, 28, 23, 58, 0);
var endLocal = new DateTime(2026, 10, 29, 0, 2, 0);
db.InsertSegment(appId, "foreground", startLocal.ToUniversalTime(), endLocal.ToUniversalTime(),
    "跨午夜", startLocal.ToString("yyyy-MM-dd"), "");

var d1 = db.QueryAppUsage(StatsService.DayRange(localDay1).From, StatsService.DayRange(localDay1).To);
var d2 = db.QueryAppUsage(StatsService.DayRange(localDay2).From, StatsService.DayRange(localDay2).To);
var d1sec = d1.Sum(r => r.TotalSeconds);
var d2sec = d2.Sum(r => r.TotalSeconds);
Check("B1 跨午夜两天切分", Math.Abs(d1sec - 120) < 1 && Math.Abs(d2sec - 120) < 1,
    $"day1={d1sec:0.0}s(期望120) day2={d2sec:0.0}s(期望120) 总和={d1sec + d2sec:0.0}s(期望240)");

// ---------- A3 视图自洽：本月 ≥ 本周 ≥ 今日 ----------
// 注入：今日 30 分钟、本周(非今日) 40 分钟、本月(非本周) 100 分钟
var now = DateTime.Now;
var todayLocal = now.Date;
var appId2 = db.GetOrCreateApp("D:\\qa\\browser.exe", "msedge", "Edge", "", "浏览与资讯");
void Inject(LocalDateTimeRange r, int minutes, int app) { }
// 用具体时间点注入
var ranges = new List<(DateTime Start, DateTime End)>
{
    // 今日：09:00-09:30
    (todayLocal.AddHours(9), todayLocal.AddHours(9.5)),
    // 本周非今日：昨天 10:00-10:40（若昨天是上周则取本周第一天）
};
var monday = todayLocal.AddDays(-((int)todayLocal.DayOfWeek + 6) % 7);
var tuesday = monday.AddDays(1);
var lastMonthDay = new DateTime(todayLocal.Year, todayLocal.Month, 1).AddDays(-1);
if (tuesday < todayLocal)
    ranges.Add((tuesday.AddHours(10), tuesday.AddHours(10).AddMinutes(40)));
ranges.Add((lastMonthDay.AddHours(15), lastMonthDay.AddHours(15).AddMinutes(100)));
foreach (var (s, e) in ranges)
    db.InsertSegment(appId2, "foreground", s.ToUniversalTime(), e.ToUniversalTime(), "注入", s.ToString("yyyy-MM-dd"), "");

var daySec = db.QueryAppUsage(StatsService.DayRange(todayLocal).From, StatsService.DayRange(todayLocal).To).Sum(r => r.TotalSeconds);
var weekSec = db.QueryAppUsage(StatsService.WeekRange(todayLocal).From, StatsService.WeekRange(todayLocal).To).Sum(r => r.TotalSeconds);
var monthSec = db.QueryAppUsage(StatsService.MonthRange(todayLocal).From, StatsService.MonthRange(todayLocal).To).Sum(r => r.TotalSeconds);
Check("A3 本月≥本周≥今日", monthSec >= weekSec - 1 && weekSec >= daySec - 1,
    $"今日={daySec:0}s 本周={weekSec:0}s 本月={monthSec:0}s");

// ---------- A4 分类修改后历史数据随新分类 ----------
var appId3 = db.GetOrCreateApp("D:\\qa\\game.exe", "somegame", "某游戏", "", "游戏");
db.InsertSegment(appId3, "foreground", todayLocal.AddHours(13).ToUniversalTime(),
    todayLocal.AddHours(13).AddMinutes(10).ToUniversalTime(), "", todayLocal.ToString("yyyy-MM-dd"), "");
var before = db.QueryAppUsage(StatsService.DayRange(todayLocal).From, StatsService.DayRange(todayLocal).To)
    .First(r => r.AppId == appId3);
db.SetAppCategory(appId3, "办公与文档");
var after = db.QueryAppUsage(StatsService.DayRange(todayLocal).From, StatsService.DayRange(todayLocal).To)
    .First(r => r.AppId == appId3);
Check("A4 分类修改历史随之归类", before.Category == "游戏" && after.Category == "办公与文档"
    && Math.Abs(before.TotalSeconds - 600) < 1 && Math.Abs(after.TotalSeconds - 600) < 1,
    $"改前={before.Category}(600s) 改后={after.Category}({after.TotalSeconds:0}s)");

// ---------- C3 写入幂等：同一片段反复延长不重复累加 ----------
var appId4 = db.GetOrCreateApp("D:\\qa\\idle.exe", "idleapp", "幂等", "", "系统与工具");
var segId = db.InsertSegment(appId4, "foreground", todayLocal.AddHours(15).ToUniversalTime(),
    todayLocal.AddHours(15).AddMinutes(5).ToUniversalTime(), "", todayLocal.ToString("yyyy-MM-dd"), "");
// 模拟心跳 10 次反复 UpdateSegmentEnd（就地延长到 5:05）
for (var i = 0; i < 10; i++)
    db.UpdateSegmentEnd(segId, todayLocal.AddHours(15).AddMinutes(5).AddSeconds(i + 1).ToUniversalTime());
var idemp = db.QueryAppUsage(StatsService.DayRange(todayLocal).From, StatsService.DayRange(todayLocal).To)
    .First(r => r.AppId == appId4).TotalSeconds;
Check("C3 反复延长不重复累加", Math.Abs(idemp - 310) < 1,
    $"总时长={idemp:0}s(期望310s=15:00→15:05:10；若重复累加会 >355s)");

// ---------- B4 脏数据：倒序片段/零长片段不影响统计 ----------
var appId5 = db.GetOrCreateApp("D:\\qa\\dirty.exe", "dirty", "脏数据", "", "系统与工具");
db.InsertSegment(appId5, "foreground", todayLocal.AddHours(17).ToUniversalTime(),
    todayLocal.AddHours(17).AddMinutes(30).ToUniversalTime(), "", todayLocal.ToString("yyyy-MM-dd"), ""); // 正常
db.InsertSegment(appId5, "foreground", todayLocal.AddHours(17).AddMinutes(40).ToUniversalTime(),
    todayLocal.AddHours(17).AddMinutes(30).ToUniversalTime(), "", todayLocal.ToString("yyyy-MM-dd"), ""); // 倒序(end<start)
db.InsertSegment(appId5, "foreground", todayLocal.AddHours(18).ToUniversalTime(),
    todayLocal.AddHours(18).ToUniversalTime(), "", todayLocal.ToString("yyyy-MM-dd"), ""); // 零长
var dirty = db.QueryAppUsage(StatsService.DayRange(todayLocal).From, StatsService.DayRange(todayLocal).To)
    .First(r => r.AppId == appId5).TotalSeconds;
Check("B4 倒序/零长片段不产生负时长", Math.Abs(dirty - 1800) < 1, $"总时长={dirty:0}s(期望1800s，倒序/零长不计)");

// ---------- C2 存储口径：start_ts/end_ts 是 UTC ISO（带 Z 后缀、RoundtripKind 可逆） ----------
var raw = db.QuerySegments(DateTime.UtcNow.AddDays(-2), DateTime.UtcNow.AddDays(1)).FirstOrDefault();
Check("C2 时间存储为 UTC ISO", raw.Start.Kind == DateTimeKind.Utc, $"start.Kind={raw.Start.Kind}");

Console.WriteLine();
Console.WriteLine(failures.Count == 0 ? "=== QA 注入测试：全部通过 ===" : $"=== QA 注入测试：{failures.Count} 项失败 ===");
Environment.Exit(failures.Count == 0 ? 0 : 1);

// 辅助（PowerShell 无法方便传元组的替代）
public record LocalDateTimeRange(DateTime Start, DateTime End);
