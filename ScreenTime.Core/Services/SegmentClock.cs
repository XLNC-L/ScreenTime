namespace ScreenTime.Services;

/// <summary>
/// 片段结算的时间决策。抽到纯逻辑层是为了能单测。
///
/// 背景：心跳每 5 秒一次，正常情况下片段结束时间每拍都往前推。
/// 一旦出现「距上次结算超过 2 分钟」，说明中间被打断过——
/// 睡眠、现代待机丢了 suspend 事件、进程被挂起、系统时钟跳变。
/// 这段空隙绝不能算进片段：睡 3 小时醒来会凭空多记 3 小时前台时长，
/// 而且用户完全察觉不到，是最难排查的那类偏差。
/// </summary>
public static class SegmentClock
{
    /// <summary>心跳 5 秒一次，24 拍没更新就视为断档。</summary>
    public const int MaxGapSeconds = 120;

    /// <summary>距上次结算是否已经断档。</summary>
    public static bool IsStale(DateTime segmentEndUtc, DateTime nowUtc)
        => (nowUtc - segmentEndUtc).TotalSeconds > MaxGapSeconds;

    /// <summary>
    /// 解析片段的有效结束时间：正常取 <paramref name="at"/>（未指定则取 now）；
    /// 若距上次结算超过上限，则钳回上次结算点，空隙不计入。
    /// 返回值不会小于 <paramref name="segmentEndUtc"/>（片段只会变长，不会变短）。
    /// </summary>
    public static DateTime ResolveEnd(DateTime segmentEndUtc, DateTime? at, DateTime nowUtc)
    {
        var end = at ?? nowUtc;
        if ((end - segmentEndUtc).TotalSeconds > MaxGapSeconds) return segmentEndUtc;
        return end > segmentEndUtc ? end : segmentEndUtc;
    }
}
