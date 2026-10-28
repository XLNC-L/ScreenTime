using System.IO;
using Microsoft.UI.Xaml.Controls;
using H.NotifyIcon;
using ScreenTime.UI;

namespace ScreenTime.Services;

/// <summary>
/// 独立托盘服务：不依赖主窗口，在代码中创建 TaskbarIcon。
/// 这样 --silent 启动时可以完全不创建主窗口，避免隐藏窗口意外占据前台、
/// 导致真实应用无法被统计。
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly TaskbarIcon _tray;

    public TrayService()
    {
        _tray = new TaskbarIcon
        {
            ToolTipText = "ScreenTime",
        };

        // 单击 / 双击：打开主窗口
        _tray.LeftClickCommand = new RelayCommand(() => App.Current.ShowMainWindow());
        _tray.DoubleClickCommand = new RelayCommand(() => App.Current.ShowMainWindow());

        // 右键菜单
        var menu = new MenuFlyout();

        var openItem = new MenuFlyoutItem { Text = "打开统计" };
        openItem.Click += (_, _) => App.Current.ShowMainWindow();
        menu.Items.Add(openItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        var exitItem = new MenuFlyoutItem { Text = "退出 ScreenTime" };
        exitItem.Click += (_, _) => App.Current.ExitApp();
        menu.Items.Add(exitItem);

        _tray.ContextFlyout = menu;

        // 图标
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(iconPath))
        {
            try { _tray.Icon = new System.Drawing.Icon(iconPath); }
            catch { _tray.Icon = System.Drawing.Icon.ExtractAssociatedIcon(iconPath); }
        }

        // 代码创建（非 XAML 加载），需强制创建托盘图标
        _tray.ForceCreate();
    }

    public void Dispose()
    {
        try { _tray.Dispose(); } catch { }
    }
}
