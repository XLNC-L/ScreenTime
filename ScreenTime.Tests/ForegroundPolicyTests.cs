using ScreenTime.Services;
using Xunit;

namespace ScreenTime.Tests;

/// <summary>前台采集判定规则：暂停条件、换页切分、连续使用的边界。</summary>
public class ForegroundPolicyTests
{
    // ---------- 暂停计时 ----------

    [Fact]
    public void ShouldPause_LockedOrSuspended_True()
    {
        Assert.True(ForegroundPolicy.ShouldPause(0, 180, locked: true, suspended: false));
        Assert.True(ForegroundPolicy.ShouldPause(0, 180, locked: false, suspended: true));
    }

    [Fact]
    public void ShouldPause_IdleBeyondLimit_True()
    {
        Assert.True(ForegroundPolicy.ShouldPause(181, 180, locked: false, suspended: false));
    }

    [Fact]
    public void ShouldPause_IdleAtLimit_StillCounting()
    {
        // 恰好等于阈值不算超限：避免边界抖动导致片段被反复切断
        Assert.False(ForegroundPolicy.ShouldPause(180, 180, locked: false, suspended: false));
        Assert.False(ForegroundPolicy.ShouldPause(0, 180, locked: false, suspended: false));
    }

    // ---------- 浏览器换页切分 ----------

    [Fact]
    public void ShouldSplitForPage_OnlyWhenSiteChangedAndLongEnough()
    {
        Assert.True(ForegroundPolicy.ShouldSplitForPage(true, 30, 5));
        Assert.False(ForegroundPolicy.ShouldSplitForPage(true, 3, 5));    // 片段太短
        Assert.False(ForegroundPolicy.ShouldSplitForPage(false, 30, 5));  // 没换页
    }

    // ---------- 连续使用 ----------

    [Fact]
    public void IsSameContinuingSession_AdjacentSameApp_True()
    {
        var t = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        Assert.True(ForegroundPolicy.IsSameContinuingSession("VS Code", "VS Code", t, t.AddMinutes(2)));
        Assert.True(ForegroundPolicy.IsSameContinuingSession("vs code", "VS Code", t, t.AddMinutes(1)));
    }

    [Fact]
    public void IsSameContinuingSession_GapOrDifferentApp_False()
    {
        var t = new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc);
        Assert.False(ForegroundPolicy.IsSameContinuingSession("VS Code", "VS Code", t, t.AddMinutes(5)));
        Assert.False(ForegroundPolicy.IsSameContinuingSession("VS Code", "Chrome", t, t));
        Assert.False(ForegroundPolicy.IsSameContinuingSession("", "Chrome", t, t));
    }
}
