using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace ScreenTime;

/// <summary>
/// 自定义入口点（csproj 里用 DISABLE_XAML_GENERATED_MAIN 关掉了自动生成的 Main）。
///
/// 单实例检查必须放在 WinUI 初始化之前：之前在 App 构造函数里调 Environment.Exit(0)，
/// 会把 XAML 运行时初始化打断，Windows 随即弹出
/// 「Exception Processing Message 0xc0000005 - Unexpected parameters」错误框——
/// 用户双击第二次图标就会看到，非常像程序崩了。
/// </summary>
public static class Program
{
    private const string MutexName = "ScreenTime_SingleInstance_Mutex";

    [STAThread]
    private static int Main(string[] args)
    {
        // 用局部变量持有互斥体直到进程退出（Main 会阻塞在 Application.Start 里）
        using var mutex = new Mutex(true, MutexName, out var createdNew);
        if (!createdNew)
        {
            // 已有实例在跑：让它把窗口亮出来，然后安静退出
            App.SignalExistingInstance();
            return 0;
        }

        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            var context = new DispatcherQueueSynchronizationContext(
                DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            new App();
        });
        return 0;
    }
}
