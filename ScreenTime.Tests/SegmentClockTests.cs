using ScreenTime.Services;
using Xunit;

namespace ScreenTime.Tests;

/// <summary>
/// 断档守卫。核心场景：片段 EndUtc 距今超过 120 秒后 Tick 一次，
/// 断言那段间隔不会被记进片段（睡眠/挂起丢事件时的核心保护）。
/// </summary>
public class SegmentClockTests
{
    private static readonly DateTime T = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    // ---------- IsStale ----------

    [Theory]
    [InlineData(0, false)]
    [InlineData(5, false)]      // 一个正常心跳
    [InlineData(119, false)]
    [InlineData(120, false)]    // 边界：不超过 120 不算断档
    [InlineData(121, true)]
    [InlineData(10800, true)]   // 睡 3 小时
    public void IsStale_Thresholds(int gapSeconds, bool expected)
    {
        Assert.Equal(expected, SegmentClock.IsStale(T, T.AddSeconds(gapSeconds)));
    }

    [Fact]
    public void IsStale_ClockGoingBackwards_NotStale()
    {
        // 系统时钟回拨（NTP 校正）不该被判成断档
        Assert.False(SegmentClock.IsStale(T, T.AddSeconds(-600)));
    }

    // ---------- ResolveEnd ----------

    [Fact]
    public void ResolveEnd_NormalTick_ExtendsToNow()
    {
        Assert.Equal(T.AddSeconds(5), SegmentClock.ResolveEnd(T, null, T.AddSeconds(5)));
    }

    [Fact]
    public void ResolveEnd_ExplicitAt_Wins()
    {
        Assert.Equal(T.AddSeconds(3),
            SegmentClock.ResolveEnd(T, T.AddSeconds(3), T.AddSeconds(30)));
    }

    [Fact]
    public void ResolveEnd_SleptThreeHours_DoesNotAbsorbTheGap()
    {
        // 这是核心用例：片段上次结算在 T，三小时后才 Tick 一次。
        // 期望结束时间仍停在 T——绝不能变成 T+3h，否则当天凭空多记 3 小时。
        var segEnd = T;
        var now = T.AddHours(3);

        var resolved = SegmentClock.ResolveEnd(segEnd, null, now);

        Assert.Equal(segEnd, resolved);
        Assert.Equal(0, (resolved - segEnd).TotalSeconds, 3);
    }

    [Fact]
    public void ResolveEnd_ExplicitAtButStale_StillClamped()
    {
        // 音频路径传的是显式 now，同样不能把整段睡眠算进去
        var segEnd = T;
        var now = T.AddHours(2);
        Assert.Equal(segEnd, SegmentClock.ResolveEnd(segEnd, now, now));
    }

    [Fact]
    public void ResolveEnd_Boundary120_Extends()
    {
        Assert.Equal(T.AddSeconds(120),
            SegmentClock.ResolveEnd(T, null, T.AddSeconds(120)));
    }

    [Fact]
    public void ResolveEnd_Boundary121_Clamped()
    {
        Assert.Equal(T, SegmentClock.ResolveEnd(T, null, T.AddSeconds(121)));
    }

    [Fact]
    public void ResolveEnd_AtBeforeSegmentEnd_NeverShrinks()
    {
        // 片段只会变长不会变短：传入更早的时间不能把已记录的时长改小
        Assert.Equal(T, SegmentClock.ResolveEnd(T, T.AddSeconds(-30), T.AddSeconds(-30)));
    }

    [Fact]
    public void ResolveEnd_SegmentDuration_UnchangedByStaleTick()
    {
        // 端到端语义：片段 T-60s 开始、T 结束（60 秒），三小时后才 Tick。
        // 结算后时长必须仍是 60 秒，而不是 60 秒 + 3 小时。
        var start = T.AddSeconds(-60);
        var segEnd = T;
        var resolved = SegmentClock.ResolveEnd(segEnd, null, T.AddHours(3));

        Assert.Equal(60, (resolved - start).TotalSeconds, 3);
    }
}
