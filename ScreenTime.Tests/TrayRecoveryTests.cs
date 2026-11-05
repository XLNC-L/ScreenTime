using ScreenTime.Services;
using Xunit;

namespace ScreenTime.Tests;

/// <summary>
/// 托盘恢复策略。真机复现要反复重启 Explorer，只能靠这里守住「什么时候该重新登记」，
/// 尤其是「图标还在时绝不动 shell」这条——多一次调用就多一次把进程带走的窗口。
/// </summary>
public class TrayRecoveryTests
{
    [Theory]
    // 没释放 + 没图标 + 外壳就绪 → 必须补登记（Explorer 重启后就是这个组合）
    [InlineData(false, true, false, true)]
    // 图标还在 → 什么都不做（这是最关键的一条）
    [InlineData(false, true, true, false)]
    // 外壳还没起来（开机自启时 Explorer 晚于 Agent）→ 等下一拍，不要硬试
    [InlineData(false, false, false, false)]
    // 已释放 → 不再碰 shell
    [InlineData(false, false, true, false)]
    [InlineData(true, true, true, false)]
    public void ShouldRegister_matches_policy(bool disposed, bool shellReady, bool iconPresent, bool expected)
    {
        Assert.Equal(expected, TrayRecoveryPolicy.ShouldRegister(iconPresent, shellReady, disposed));
    }

    [Fact]
    public void ShouldRegister_only_true_when_every_precondition_holds()
    {
        // 三个条件缺一不可：未释放、没图标、外壳就绪
        Assert.True(TrayRecoveryPolicy.ShouldRegister(iconPresent: false, shellReady: true, disposed: false));
        Assert.False(TrayRecoveryPolicy.ShouldRegister(iconPresent: true, shellReady: true, disposed: false));
        Assert.False(TrayRecoveryPolicy.ShouldRegister(iconPresent: false, shellReady: false, disposed: false));
        Assert.False(TrayRecoveryPolicy.ShouldRegister(iconPresent: false, shellReady: true, disposed: true));
    }
}
