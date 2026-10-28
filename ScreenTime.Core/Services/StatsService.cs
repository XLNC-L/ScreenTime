namespace ScreenTime.Services;

/// <summary>统计辅助：时长格式化、区间并集、日期范围。</summary>
public static class StatsService
{
    /// <summary>TimeSpan 能表示的上限约 9.22e11 秒，留点余量做钳制，避免超大值抛 OverflowException。</summary>
    private const double MaxSeconds = 900_000_000_000;

    /// <summary>把异常输入收敛成安全值：NaN / Infinity / 负数一律当 0，超大值钳到上限。</summary>
    private static double Sanitize(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds < 0) return 0;
        return seconds > MaxSeconds ? MaxSeconds : seconds;
    }

    public static string FormatDuration(double seconds)
    {
        seconds = Sanitize(seconds);
        if (seconds < 1) return "0 秒";
        var t = TimeSpan.FromSeconds(seconds);
        if (t.TotalHours >= 1)
            return $"{(int)t.TotalHours} 小时 {t.Minutes} 分";
        if (t.TotalMinutes >= 1)
            return $"{t.Minutes} 分 {t.Seconds} 秒";
        return $"{t.Seconds} 秒";
    }

    /// <summary>
    /// 精确到秒的时长，用于「总时长 / 前台 / 后台」这类需要相加对得上的地方。
    /// <see cref="FormatDuration"/> 超过 1 小时会丢掉秒，三个数字相加就会出现几十秒的差。
    /// 向下取整（不是四舍五入）：调用方按整数秒算好差值，才能保证 a + b 恰好等于总数。
    /// </summary>
    public static string FormatDurationExact(double seconds)
    {
        seconds = Sanitize(seconds);
        if (seconds < 1) return "0 秒";
        var t = TimeSpan.FromSeconds(Math.Floor(seconds));
        if (t.TotalHours >= 1)
            return $"{(int)t.TotalHours} 小时 {t.Minutes} 分 {t.Seconds} 秒";
        if (t.TotalMinutes >= 1)
            return $"{t.Minutes} 分 {t.Seconds} 秒";
        return $"{t.Seconds} 秒";
    }

    public static string FormatHours(double seconds)
    {
        var h = Sanitize(seconds) / 3600.0;
        return h >= 10 ? $"{h:0.0}" : $"{h:0.00}";
    }

    /// <summary>多个时间区间的并集秒数（重叠不重复计算）。</summary>
    public static double UnionSeconds(IList<(DateTime Start, DateTime End)> intervals)
    {
        if (intervals is null || intervals.Count == 0) return 0;

        // 倒序 / 零长度区间是脏数据：不能参与计算，否则会把总时长扣成负数
        var valid = intervals.Where(i => i.End > i.Start).OrderBy(i => i.Start).ToList();
        if (valid.Count == 0) return 0;

        double total = 0;
        var curStart = valid[0].Start;
        var curEnd = valid[0].End;
        foreach (var (s, e) in valid.Skip(1))
        {
            if (s <= curEnd)
            {
                if (e > curEnd) curEnd = e;
            }
            else
            {
                total += (curEnd - curStart).TotalSeconds;
                curStart = s;
                curEnd = e;
            }
        }
        total += (curEnd - curStart).TotalSeconds;
        return total;
    }

    /// <summary>本地日期对应的 UTC 范围。</summary>
    public static (DateTime From, DateTime To) DayRange(DateTime localDay)
    {
        var start = localDay.Date;
        return (start.ToUniversalTime(), start.AddDays(1).ToUniversalTime());
    }

    /// <summary>自然周（周一为起点）。</summary>
    public static (DateTime From, DateTime To) WeekRange(DateTime localDate)
    {
        var d = localDate.Date;
        var delta = (d.DayOfWeek == DayOfWeek.Sunday ? 6 : (int)d.DayOfWeek - 1);
        var monday = d.AddDays(-delta);
        return (monday.ToUniversalTime(), monday.AddDays(7).ToUniversalTime());
    }

    /// <summary>自然月。</summary>
    public static (DateTime From, DateTime To) MonthRange(DateTime localDate)
    {
        var first = new DateTime(localDate.Year, localDate.Month, 1);
        return (first.ToUniversalTime(), first.AddMonths(1).ToUniversalTime());
    }

    /// <summary>范围内每一天的本地日期。</summary>
    public static IEnumerable<DateTime> EachDay(DateTime fromUtc, DateTime toUtc)
    {
        var f = fromUtc.ToLocalTime().Date;
        var t = toUtc.ToLocalTime().Date;
        for (var d = f; d < t; d = d.AddDays(1))
            yield return d;
    }
}
