using ScreenTime.Services;
using Xunit;

namespace ScreenTime.Tests;

/// <summary>
/// 统计口径。这里的目标是打挂它：空值、倒序区间、重复区间、非有限数。
/// </summary>
public class StatsServiceTests
{
    private static readonly DateTime Base = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static (DateTime, DateTime) I(double startSec, double endSec)
        => (Base.AddSeconds(startSec), Base.AddSeconds(endSec));

    // ---------- UnionSeconds ----------

    [Fact]
    public void UnionSeconds_Empty_ReturnsZero()
    {
        Assert.Equal(0, StatsService.UnionSeconds(new List<(DateTime, DateTime)>()));
    }

    [Fact]
    public void UnionSeconds_Null_ReturnsZero()
    {
        Assert.Equal(0, StatsService.UnionSeconds(null!));
    }

    [Fact]
    public void UnionSeconds_Single_ReturnsDuration()
    {
        Assert.Equal(10, StatsService.UnionSeconds(new List<(DateTime, DateTime)> { I(0, 10) }));
    }

    [Fact]
    public void UnionSeconds_Overlapping_CountedOnce()
    {
        var v = new List<(DateTime, DateTime)> { I(0, 10), I(5, 15) };
        Assert.Equal(15, StatsService.UnionSeconds(v));
    }

    [Fact]
    public void UnionSeconds_Nested_CountedOnce()
    {
        var v = new List<(DateTime, DateTime)> { I(0, 100), I(10, 20) };
        Assert.Equal(100, StatsService.UnionSeconds(v));
    }

    [Fact]
    public void UnionSeconds_Adjacent_Summed()
    {
        var v = new List<(DateTime, DateTime)> { I(0, 10), I(10, 20) };
        Assert.Equal(20, StatsService.UnionSeconds(v));
    }

    [Fact]
    public void UnionSeconds_Unsorted_SameResult()
    {
        var v = new List<(DateTime, DateTime)> { I(20, 30), I(0, 10), I(5, 25) };
        Assert.Equal(30, StatsService.UnionSeconds(v));
    }

    [Fact]
    public void UnionSeconds_ZeroLength_ContributesNothing()
    {
        var v = new List<(DateTime, DateTime)> { I(0, 10), I(10, 10) };
        Assert.Equal(10, StatsService.UnionSeconds(v));
    }

    [Fact]
    public void UnionSeconds_ReversedInterval_MustNotProduceNegative()
    {
        // 倒序区间（End < Start）属于脏数据，不能让它把总时长扣成负数
        var v = new List<(DateTime, DateTime)> { I(10, 0) };
        Assert.Equal(0, StatsService.UnionSeconds(v));
    }

    [Fact]
    public void UnionSeconds_ReversedAmongValid_Ignored()
    {
        var v = new List<(DateTime, DateTime)> { I(0, 10), I(100, 50) };
        Assert.Equal(10, StatsService.UnionSeconds(v));
    }

    // ---------- FormatDuration ----------

    [Theory]
    [InlineData(0, "0 秒")]
    [InlineData(0.9, "0 秒")]
    [InlineData(1, "1 秒")]
    [InlineData(59, "59 秒")]
    [InlineData(60, "1 分 0 秒")]
    [InlineData(3599, "59 分 59 秒")]
    [InlineData(3600, "1 小时 0 分")]
    [InlineData(3661, "1 小时 1 分")]
    public void FormatDuration_Normal(double seconds, string expected)
    {
        Assert.Equal(expected, StatsService.FormatDuration(seconds));
    }

    [Fact]
    public void FormatDuration_Negative_ReturnsZeroText()
    {
        Assert.Equal("0 秒", StatsService.FormatDuration(-5));
    }

    [Fact]
    public void FormatDuration_NaN_DoesNotThrow()
    {
        var text = StatsService.FormatDuration(double.NaN);
        Assert.False(string.IsNullOrWhiteSpace(text));
    }

    [Fact]
    public void FormatDuration_Infinity_DoesNotThrow()
    {
        var text = StatsService.FormatDuration(double.PositiveInfinity);
        Assert.False(string.IsNullOrWhiteSpace(text));
    }

    [Fact]
    public void FormatDuration_HugeValue_DoesNotThrow()
    {
        var text = StatsService.FormatDuration(double.MaxValue);
        Assert.False(string.IsNullOrWhiteSpace(text));
    }

    // ---------- FormatDurationExact ----------

    [Theory]
    [InlineData(0, "0 秒")]
    [InlineData(1, "1 秒")]
    [InlineData(60, "1 分 0 秒")]
    [InlineData(3661, "1 小时 1 分 1 秒")]
    [InlineData(3661.9, "1 小时 1 分 1 秒")]
    public void FormatDurationExact_Normal(double seconds, string expected)
    {
        Assert.Equal(expected, StatsService.FormatDurationExact(seconds));
    }

    [Fact]
    public void FormatDurationExact_NaN_DoesNotThrow()
    {
        Assert.False(string.IsNullOrWhiteSpace(StatsService.FormatDurationExact(double.NaN)));
    }

    [Fact]
    public void FormatDurationExact_Infinity_DoesNotThrow()
    {
        Assert.False(string.IsNullOrWhiteSpace(StatsService.FormatDurationExact(double.PositiveInfinity)));
    }

    // ---------- FormatHours ----------

    [Fact]
    public void FormatHours_NaN_DoesNotThrow()
    {
        Assert.False(string.IsNullOrWhiteSpace(StatsService.FormatHours(double.NaN)));
    }

    [Fact]
    public void FormatHours_Negative_DoesNotThrow()
    {
        Assert.False(string.IsNullOrWhiteSpace(StatsService.FormatHours(-3600)));
    }
}
