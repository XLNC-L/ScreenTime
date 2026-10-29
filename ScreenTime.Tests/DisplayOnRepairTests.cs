using ScreenTime.Data;
using Xunit;

namespace ScreenTime.Tests;

/// <summary>
/// 亮屏时间线修补。
/// 关机/断电/强杀时进程被直接杀掉，熄灭事件来不及写，
/// 连续两条 display_on 之间的大空档就会被当成"一直亮着"——
/// 实际踩到过：一天报 14 小时亮屏，中间 11.8 小时机器是关着的。
/// </summary>
public class DisplayOnRepairTests : IDisposable
{
    private readonly string _dir;
    private readonly Database _db;
    private static readonly DateTime Base = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    public DisplayOnRepairTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "screentime-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _db = new Database(Path.Combine(_dir, "t.db"));
        _db.Init();
        _db.Migrate();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private int NewApp() => _db.GetOrCreateApp(@"C:\T\a.exe", "a", "测试", "", "系统与工具");

    private void AddSeg(int appId, DateTime startUtc, DateTime endUtc)
        => _db.InsertSegment(appId, "foreground", startUtc, endUtc, "",
            startUtc.ToLocalTime().ToString("yyyy-MM-dd"), "", "", "");

    [Fact]
    public void MidHistoryGap_ClosedAtLastActivity()
    {
        // 12:00 亮屏 → 12:30 最后活动 → 机器关机 → 15:00 重启又亮屏
        var id = NewApp();
        _db.AddSessionEventAt("display_on", Base);
        AddSeg(id, Base, Base.AddMinutes(30));
        _db.AddSessionEventAt("display_on", Base.AddHours(3));
        AddSeg(id, Base.AddHours(3), Base.AddHours(3).AddMinutes(10));
        _db.AddSessionEventAt("display_off", Base.AddHours(3).AddMinutes(10));

        var repaired = _db.RepairDisplaySpans();

        Assert.Equal(1, repaired);
        // 12:00–15:00 的空档只算到 12:30，加第二段的 10 分钟 = 40 分钟，不是 3 小时
        Assert.Equal(40 * 60, _db.GetDisplayOnSeconds(Base, Base.AddHours(4)), 3);
    }

    [Fact]
    public void TrailingOpenSpan_ClosedAtLastActivity()
    {
        var id = NewApp();
        _db.AddSessionEventAt("display_on", Base);
        AddSeg(id, Base, Base.AddMinutes(20));

        Assert.Equal(1, _db.RepairDisplaySpans());
        Assert.Equal(20 * 60, _db.GetDisplayOnSeconds(Base, Base.AddHours(5)), 3);
    }

    [Fact]
    public void RapidRestart_NotSplit()
    {
        // 秒级重启：连续两条 on 之间只差 0 秒，不该被切成碎片
        var id = NewApp();
        _db.AddSessionEventAt("display_on", Base);
        AddSeg(id, Base, Base.AddMinutes(2));
        _db.AddSessionEventAt("display_on", Base.AddMinutes(2));
        AddSeg(id, Base.AddMinutes(2), Base.AddMinutes(5));
        _db.AddSessionEventAt("display_off", Base.AddMinutes(5));

        Assert.Equal(0, _db.RepairDisplaySpans());
    }

    [Fact]
    public void ProperOnOffPair_Untouched()
    {
        var id = NewApp();
        _db.AddSessionEventAt("display_on", Base);
        AddSeg(id, Base, Base.AddHours(1));
        _db.AddSessionEventAt("display_off", Base.AddHours(1));

        Assert.Equal(0, _db.RepairDisplaySpans());
        Assert.Equal(3600, _db.GetDisplayOnSeconds(Base, Base.AddHours(3)), 3);
    }

    [Fact]
    public void EmptyDb_NoChange()
    {
        Assert.Equal(0, _db.RepairDisplaySpans());
    }

    [Fact]
    public void CustomGap_Respected()
    {
        // 空档 20 分钟：默认 30 分钟阈值不动，显式给 10 分钟才收口
        var id = NewApp();
        _db.AddSessionEventAt("display_on", Base);
        AddSeg(id, Base, Base.AddMinutes(5));
        _db.AddSessionEventAt("display_on", Base.AddMinutes(25));
        AddSeg(id, Base.AddMinutes(25), Base.AddMinutes(30));
        _db.AddSessionEventAt("display_off", Base.AddMinutes(30));

        Assert.Equal(0, _db.RepairDisplaySpans());
        Assert.Equal(1, _db.RepairDisplaySpans(TimeSpan.FromMinutes(10)));
    }
}
