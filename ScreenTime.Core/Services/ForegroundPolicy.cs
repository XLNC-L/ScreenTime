namespace ScreenTime.Services;

/// <summary>
/// 前台采集的判定规则（纯逻辑，便于单测）。
///
/// 这些规则原来散在 WatcherService 与 Database 里，同一个「连续使用」概念
/// 在两个地方各写了一遍。集中到这里后，改动只需改一处，测试也能直接覆盖。
/// </summary>
public static class ForegroundPolicy
{
    /// <summary>是否应该暂停计时：锁屏、休眠、或无输入超过阈值。</summary>
    public static bool ShouldPause(double idleSeconds, int idleLimitSeconds, bool locked, bool suspended)
        => locked || suspended || idleSeconds > idleLimitSeconds;

    /// <summary>
    /// 同一应用换了页面（浏览器换标签）时是否要切分片段。
    /// 片段太短就不切，避免标题/页面抖动把时间剁成碎片。
    /// </summary>
    public static bool ShouldSplitForPage(bool siteChanged, double segmentSeconds, double minSegmentSeconds)
        => siteChanged && segmentSeconds >= minSegmentSeconds;

    /// <summary>
    /// 两个片段是否属于「同一段连续使用」：同一个应用，且间隔不超过容差（默认 2 分钟）。
    /// 碎片治理之后正常片段本来就相连，这里的容差只是兜住零星断档。
    /// </summary>
    public static bool IsSameContinuingSession(string? currentApp, string? newApp,
        DateTime lastEndUtc, DateTime nowUtc, double maxGapMinutes = 2)
        => !string.IsNullOrEmpty(currentApp) &&
           string.Equals(currentApp, newApp, StringComparison.OrdinalIgnoreCase) &&
           (nowUtc - lastEndUtc).TotalMinutes <= maxGapMinutes;
}
