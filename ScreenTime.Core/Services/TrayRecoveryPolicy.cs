namespace ScreenTime.Services;

/// <summary>
/// 托盘图标的「要不要补登记」判定。抽成纯函数是为了能写单元测试——
/// 托盘恢复本身是状态机，但真机复现要反复重启 Explorer，只能靠这条路径保护。
///
/// 关键约束：**图标还在时绝不去调 shell**。Explorer 重启的窄窗口内多余的
/// Shell_NotifyIcon 调用不值得冒，宁可晚 2 秒恢复。
/// </summary>
public static class TrayRecoveryPolicy
{
    /// <summary>
    /// 是否需要（重新）登记托盘图标：没释放、当前确实没有图标、外壳托盘窗口已就绪。
    /// </summary>
    /// <param name="iconPresent">当前是否确认图标已在托盘里。</param>
    /// <param name="shellReady">外壳托盘窗口（Shell_TrayWnd）是否存在。</param>
    /// <param name="disposed">托盘对象是否已释放。</param>
    public static bool ShouldRegister(bool iconPresent, bool shellReady, bool disposed)
        => !disposed && !iconPresent && shellReady;
}
