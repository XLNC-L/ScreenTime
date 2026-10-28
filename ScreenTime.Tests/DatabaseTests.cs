using ScreenTime.Data;
using Xunit;

namespace ScreenTime.Tests;

/// <summary>
/// 数据层：去重、区间裁剪、碎片合并、亮屏配对、CSV 转义。
/// 每个用例用独立的临时库，互不干扰。
/// </summary>
public class DatabaseTests : IDisposable
{
    private readonly string _dir;
    private readonly Database _db;

    private static readonly DateTime Base = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public DatabaseTests()
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

    private int NewApp(string process = "testapp", string name = "测试应用", string exe = @"C:\T\testapp.exe")
        => _db.GetOrCreateApp(exe, process, name, "", "系统与工具");

    private long AddSeg(int appId, double startSec, double endSec, string kind = "foreground",
        string title = "", string site = "", string url = "")
        => _db.InsertSegment(appId, kind, Base.AddSeconds(startSec), Base.AddSeconds(endSec),
            title, Base.ToLocalTime().ToString("yyyy-MM-dd"), "", site, url);

    // ---------- 应用去重 ----------

    [Fact]
    public void GetOrCreateApp_SameProcessAndName_DedupesEvenIfPathChanges()
    {
        var a = _db.GetOrCreateApp(@"C:\Old\app.exe", "app", "同一应用", "", "系统与工具");
        var b = _db.GetOrCreateApp(@"C:\New\app.exe", "app", "同一应用", "", "系统与工具");
        Assert.Equal(a, b);
        Assert.Single(_db.GetAllApps(), x => x.ProcessName == "app");
    }

    [Fact]
    public void GetOrCreateApp_DifferentName_CreatesTwo()
    {
        var a = _db.GetOrCreateApp(@"C:\A\app.exe", "app", "应用甲", "", "系统与工具");
        var b = _db.GetOrCreateApp(@"C:\B\app.exe", "app", "应用乙", "", "系统与工具");
        Assert.NotEqual(a, b);
    }

    // ---------- 区间裁剪 ----------

    [Fact]
    public void QuerySegments_ClipsToRange()
    {
        var id = NewApp();
        AddSeg(id, 0, 100);                       // 00:00:00 - 00:01:40
        var rows = _db.QuerySegments(Base.AddSeconds(50), Base.AddSeconds(60));
        var row = Assert.Single(rows);
        Assert.Equal(10, (row.End - row.Start).TotalSeconds, 3);
    }

    [Fact]
    public void QuerySegments_OutOfRange_Empty()
    {
        var id = NewApp();
        AddSeg(id, 0, 10);
        Assert.Empty(_db.QuerySegments(Base.AddSeconds(100), Base.AddSeconds(200)));
    }

    // ---------- 碎片合并 ----------

    [Fact]
    public void MergeShortSegments_MergesAdjacentShortPiece()
    {
        var id = NewApp();
        AddSeg(id, 0, 10);
        AddSeg(id, 10, 11);      // 1 秒，紧邻前一段，应并入
        _db.MergeShortSegments(3, Base.AddSeconds(-10));

        var rows = _db.QuerySegments(Base.AddSeconds(-10), Base.AddSeconds(100));
        var row = Assert.Single(rows);
        Assert.Equal(11, (row.End - row.Start).TotalSeconds, 3);
    }

    [Fact]
    public void MergeShortSegments_DeletesZeroLength()
    {
        var id = NewApp();
        AddSeg(id, 0, 10);
        AddSeg(id, 100, 100);    // 零长度
        _db.MergeShortSegments(3, Base.AddSeconds(-10));

        var rows = _db.QuerySegments(Base.AddSeconds(-10), Base.AddSeconds(200));
        Assert.Single(rows);
    }

    [Fact]
    public void MergeShortSegments_KeepsLongEnoughPiece()
    {
        var id = NewApp();
        AddSeg(id, 0, 10);
        AddSeg(id, 10, 20);      // 10 秒，不该被合并
        _db.MergeShortSegments(3, Base.AddSeconds(-10));

        Assert.Equal(2, _db.QuerySegments(Base.AddSeconds(-10), Base.AddSeconds(100)).Count);
    }

    [Fact]
    public void MergeShortSegments_DifferentApps_NotMerged()
    {
        var a = NewApp("appa", "应用甲", @"C:\A\appa.exe");
        var b = NewApp("appb", "应用乙", @"C:\B\appb.exe");
        AddSeg(a, 0, 10);
        AddSeg(b, 10, 11);
        _db.MergeShortSegments(3, Base.AddSeconds(-10));

        Assert.Equal(2, _db.QuerySegments(Base.AddSeconds(-10), Base.AddSeconds(100)).Count);
    }

    // ---------- 亮屏配对 ----------

    [Fact]
    public void GetDisplayOnSeconds_NoEvents_Zero()
    {
        Assert.Equal(0, _db.GetDisplayOnSeconds(Base, Base.AddSeconds(600)));
    }

    [Fact]
    public void GetDisplayOnSeconds_SingleOnEvent_CountsToEnd()
    {
        _db.AddSessionEventAt("display_on", Base);
        Assert.Equal(600, _db.GetDisplayOnSeconds(Base, Base.AddSeconds(600)), 3);
    }

    [Fact]
    public void GetDisplayOnSeconds_OnThenOff_CountsOnlyOnSpan()
    {
        _db.AddSessionEventAt("display_on", Base);
        _db.AddSessionEventAt("display_off", Base.AddSeconds(300));
        Assert.Equal(300, _db.GetDisplayOnSeconds(Base, Base.AddSeconds(600)), 3);
    }

    [Fact]
    public void GetDisplayOnSeconds_TwoCycles_Summed()
    {
        _db.AddSessionEventAt("display_on", Base);
        _db.AddSessionEventAt("display_off", Base.AddSeconds(300));
        _db.AddSessionEventAt("display_on", Base.AddSeconds(420));
        _db.AddSessionEventAt("display_off", Base.AddSeconds(600));
        Assert.Equal(480, _db.GetDisplayOnSeconds(Base, Base.AddSeconds(1000)), 3);
    }

    [Fact]
    public void GetDisplayOnSeconds_ConsecutiveOnEvents_Continuous()
    {
        _db.AddSessionEventAt("display_on", Base);
        _db.AddSessionEventAt("display_on", Base.AddSeconds(300));
        Assert.Equal(600, _db.GetDisplayOnSeconds(Base, Base.AddSeconds(600)), 3);
    }

    [Fact]
    public void GetDisplayOnSeconds_OnBeforeRange_ClipsToRange()
    {
        _db.AddSessionEventAt("display_on", Base.AddSeconds(-600));
        Assert.Equal(600, _db.GetDisplayOnSeconds(Base, Base.AddSeconds(600)), 3);
    }

    [Fact]
    public void GetDisplayOnSeconds_OffBeforeRangeThenOn_CountsFromOn()
    {
        _db.AddSessionEventAt("display_off", Base.AddSeconds(-600));
        _db.AddSessionEventAt("display_on", Base.AddSeconds(120));
        Assert.Equal(480, _db.GetDisplayOnSeconds(Base, Base.AddSeconds(600)), 3);
    }

    [Fact]
    public void GetDisplayOnSeconds_ReversedRange_Zero()
    {
        _db.AddSessionEventAt("display_on", Base);
        Assert.Equal(0, _db.GetDisplayOnSeconds(Base.AddSeconds(600), Base));
    }

    [Fact]
    public void GetDisplayOnSeconds_FutureRange_ClipsToNow()
    {
        // 区间末端是未来（DayRange 会给到次日 0 点），不能把未来也算进去
        _db.AddSessionEventAt("display_on", Base);
        var huge = _db.GetDisplayOnSeconds(Base, DateTime.UtcNow.AddDays(30));
        Assert.True(huge < (DateTime.UtcNow - Base).TotalSeconds + 5);
    }

    // ---------- CSV 转义 ----------

    [Fact]
    public void BuildSegmentsCsv_EscapesCommaAndQuote()
    {
        var id = NewApp("app,csv", "应\"用,带逗号", @"C:\C\csv.exe");
        AddSeg(id, 0, 10, title: "标题,含逗号和\"引号\"");

        var (rows, csv) = _db.BuildSegmentsCsv();
        Assert.Equal(1, rows);
        Assert.Contains("\"应\"\"用,带逗号\"", csv);
        Assert.Contains("\"标题,含逗号和\"\"引号\"\"\"", csv);
    }

    [Fact]
    public void BuildSegmentsCsv_NewlineReplaced()
    {
        var id = NewApp();
        AddSeg(id, 0, 10, title: "第一行\n第二行");
        var (_, csv) = _db.BuildSegmentsCsv();
        Assert.DoesNotContain("第一行\n第二行", csv);
    }

    [Fact]
    public void BuildSegmentsJson_EmptyDb_ValidJson()
    {
        var (rows, json) = _db.BuildSegmentsJson();
        Assert.Equal(0, rows);
        Assert.Contains("\"count\": 0", json);
    }

    // ---------- 设置与事件 ----------

    [Fact]
    public void Setting_RoundTripAndDefault()
    {
        Assert.Equal("180", _db.GetSetting("idle_limit", "180"));
        _db.SetSetting("idle_limit", "60");
        Assert.Equal("60", _db.GetSetting("idle_limit", "180"));
    }

    [Fact]
    public void GetLastBootTime_None_ReturnsNull()
    {
        Assert.Null(_db.GetLastBootTime());
    }

    [Fact]
    public void GetLastBootTime_AfterEvent_ReturnsValue()
    {
        _db.AddSessionEventAt("boot", Base);
        Assert.Equal(Base, _db.GetLastBootTime());
    }

    // ---------- 排行统计 ----------

    [Fact]
    public void QueryAppUsage_ExcludesIgnoredApps()
    {
        var visible = NewApp("vis", "可见应用", @"C:\V\vis.exe");
        var hidden = NewApp("hid", "被忽略", @"C:\H\hid.exe");
        AddSeg(visible, 0, 10);
        AddSeg(hidden, 0, 10);
        _db.SetAppIgnored(hidden, true);

        var rows = _db.QueryAppUsage(Base.AddSeconds(-10), Base.AddSeconds(100));
        Assert.Single(rows);
        Assert.Equal("可见应用", rows[0].AppName);
        Assert.Equal(10, rows[0].TotalSeconds, 3);
    }
}
