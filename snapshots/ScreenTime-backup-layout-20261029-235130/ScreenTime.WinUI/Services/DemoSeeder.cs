using System.IO;
using ScreenTime.Data;

namespace ScreenTime.Services;

/// <summary>
/// 演示数据生成器：往指定数据库写入若干天的合成使用记录，
/// 用于在没有真实历史的情况下验证「周报 / 月报 / 环比 / 热力条」这些长周期逻辑。
///
/// 安全边界：只由 <c>--seed-demo</c> 触发，且必须同时用 <c>--db=</c> 指定库文件
/// （见 <c>App.OnLaunched</c>），永远不会写进 %AppData% 里的真实数据库。
/// </summary>
public static class DemoSeeder
{
    private sealed record DemoApp(string Process, string Name, string Category, double Weight);

    private static readonly DemoApp[] Apps =
    [
        new("chrome", "谷歌浏览器", "浏览与资讯", 0.28),
        new("Code", "Visual Studio Code", "开发与创作", 0.18),
        new("Weixin", "微信", "通讯与协作", 0.14),
        new("Doubao", "豆包", "办公与文档", 0.12),
        new("YuanShen", "原神", "游戏", 0.10),
        new("KuGou", "酷狗音乐", "影音与娱乐", 0.08),
        new("explorer", "文件资源管理器", "系统与工具", 0.06),
        new("SystemSettings", "设置", "系统与工具", 0.04),
    ];

    /// <summary>浏览器演示页面：(site, 标题, 可跳转地址)。</summary>
    private static readonly (string Site, string Title, string Url)[] Pages =
    [
        ("github.com/Trae-AI/Trae", "Trae-AI/Trae: 探索未至之境", "https://github.com/Trae-AI/Trae"),
        ("chat.deepseek.com/a/chat/s/demo-1", "DeepSeek - 探索未至之境", "https://chat.deepseek.com"),
        ("bilibili.com/video/BV1demo", "【4K】某个视频 - 哔哩哔哩", "https://www.bilibili.com"),
        ("douyin.com", "抖音 - 记录美好生活", "https://www.douyin.com"),
        ("zhihu.com/question/123456", "如何评价屏幕时间统计工具 - 知乎", "https://www.zhihu.com"),
        ("learn.microsoft.com/windows/apps/winui", "WinUI 3 文档 - Microsoft Learn", "https://learn.microsoft.com/windows/apps/winui"),
    ];

    private static readonly string[] CodeTitles =
    [
        "ScreenTime.WinUI - Visual Studio Code",
        "Database.cs - ScreenTime.WinUI - Visual Studio Code",
        "ReportsPage.xaml - ScreenTime.WinUI - Visual Studio Code",
    ];

    private static readonly string[] ChatTitles = ["微信", "文件传输助手", "项目讨论组"];

    /// <summary>
    /// 生成 <paramref name="days"/> 天的数据（含今天）。返回写入的片段数。
    /// 用固定随机种子，保证每次生成结果一致，便于对比前后差异。
    /// </summary>
    public static int Seed(Database db, int days)
    {
        var rng = new Random(20261028);
        var appIds = Apps
            .Select(a => (App: a, Id: db.GetOrCreateApp("", a.Process, a.Name, "Demo", a.Category)))
            .ToArray();

        var today = DateTime.Today;
        var total = 0;

        for (var offset = days - 1; offset >= 0; offset--)
        {
            var day = today.AddDays(-offset);
            var isWeekend = day.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday;

            // 工作日约 7 小时、周末约 5 小时；再叠加 ±15% 的日间波动
            var activeHours = (isWeekend ? 5.0 : 7.0) * (0.85 + rng.NextDouble() * 0.3);

            // 今天只生成到当前时刻，避免出现"未来已使用"的假数据
            var cutoff = day == today ? DateTime.Now : day.AddHours(23);
            if (cutoff <= day.AddHours(9)) continue;

            var sessions = BuildSessions(day, isWeekend, activeHours, rng);
            foreach (var (sessionStart, sessionEnd) in sessions)
            {
                var cursor = sessionStart;
                if (cursor < day.AddHours(8)) cursor = day.AddHours(8);

                // 今天只到当前时刻为止，避免出现"未来已使用"的假数据
                var end = sessionEnd > cutoff ? cutoff : sessionEnd;
                if (end <= cursor) continue;

                // 会话内按权重切分成连续片段
                while (cursor < end)
                {
                    var (app, appId) = PickApp(appIds, isWeekend, rng);
                    var sliceMinutes = 6 + rng.Next(25);
                    var sliceEnd = cursor.AddMinutes(sliceMinutes);
                    if (sliceEnd > end) sliceEnd = end;

                    var (title, site, url) = Describe(app, rng);
                    Insert(db, appId, "foreground", cursor, sliceEnd, title, site, url);
                    total++;

                    cursor = sliceEnd;
                }
            }
        }

        return total;
    }

    private static long Insert(Database db, int appId, string kind, DateTime startLocal, DateTime endLocal,
        string title, string site, string url)
    {
        return db.InsertSegment(appId, kind, startLocal.ToUniversalTime(), endLocal.ToUniversalTime(),
            title, startLocal.ToString("yyyy-MM-dd"), "", site, url);
    }

    private static (DemoApp App, int Id) PickApp((DemoApp App, int Id)[] apps, bool isWeekend, Random rng)
    {
        // 周末把「开发」权重挪给「游戏/影音」，让周报和月报有明显形态差异
        var weights = apps.Select(x =>
        {
            var w = x.App.Weight;
            if (isWeekend && x.App.Category is "开发与创作" or "办公与文档") w *= 0.35;
            if (isWeekend && x.App.Category is "游戏" or "影音与娱乐") w *= 2.2;
            return w;
        }).ToArray();

        var roll = rng.NextDouble() * weights.Sum();
        for (var i = 0; i < weights.Length; i++)
        {
            roll -= weights[i];
            if (roll <= 0) return apps[i];
        }
        return apps[^1];
    }

    private static (string Title, string Site, string Url) Describe(DemoApp app, Random rng)
    {
        if (app.Process == "chrome")
        {
            var page = Pages[rng.Next(Pages.Length)];
            return (page.Title, page.Site, page.Url);
        }
        if (app.Process == "Code") return (CodeTitles[rng.Next(CodeTitles.Length)], "", "");
        if (app.Process == "Weixin") return (ChatTitles[rng.Next(ChatTitles.Length)], "", "");
        return (app.Name, "", "");
    }

    /// <summary>
    /// 每天 2-3 段使用时段：上午、下午、晚上，周末偏向下午和深夜。
    /// </summary>
    private static List<(DateTime Start, DateTime End)> BuildSessions(
        DateTime day, bool isWeekend, double activeHours, Random rng)
    {
        var list = new List<(DateTime, DateTime)>();

        if (isWeekend)
        {
            var lateStart = day.AddHours(10 + rng.Next(2));
            list.Add((lateStart, lateStart.AddMinutes(activeHours * 60 * 0.55)));
            var evening = day.AddHours(20 + rng.Next(2));
            list.Add((evening, evening.AddMinutes(activeHours * 60 * 0.45)));
        }
        else
        {
            var morning = day.AddHours(9);
            list.Add((morning, morning.AddMinutes(activeHours * 60 * 0.42)));
            var afternoon = day.AddHours(14);
            list.Add((afternoon, afternoon.AddMinutes(activeHours * 60 * 0.38)));
            var evening = day.AddHours(20 + rng.Next(2));
            list.Add((evening, evening.AddMinutes(activeHours * 60 * 0.20)));
        }

        return list;
    }

    /// <summary>把生成结果写到库文件旁边，便于确认本次运行确实用了演示库。</summary>
    public static void WriteLog(Database db, string line)
    {
        try
        {
            var dir = Path.GetDirectoryName(db.DbPath);
            if (string.IsNullOrEmpty(dir)) return;
            File.AppendAllText(Path.Combine(dir, "demo-seed.log"), line + Environment.NewLine);
        }
        catch { }
    }
}
