using System.IO;
using H.NotifyIcon;
using Microsoft.UI.Xaml.Controls;
using ScreenTime.UI;

namespace ScreenTime.Services;

/// <summary>
/// 独立托盘服务：不依赖主窗口，在代码中创建 TaskbarIcon。
/// 这样 --silent 启动时可以完全不创建主窗口，避免隐藏窗口意外占据前台、
/// 导致真实应用无法被统计。
///
/// 右键菜单用库的默认 <see cref="ContextMenuMode.PopupMenu"/> 模式：
/// H.NotifyIcon 会把 MenuFlyout 转成 Win32 原生菜单（不依赖 XamlRoot，
/// 静默启动没有窗口也能弹出），点击时调用菜单项的 Command。
/// 注意：转换模式只转发 Command，不转发 Click 事件——旧版用 Click
/// 所以「菜单弹得出来但点了没反应」。
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

        // 右键菜单：MenuFlyout → 库转成 Win32 原生菜单弹出（默认模式，无需窗口）
        var menu = new MenuFlyout();

        var openItem = new MenuFlyoutItem
        {
            Text = "打开统计",
            Command = new RelayCommand(() => App.Current.ShowMainWindow()),
        };
        menu.Items.Add(openItem);

        menu.Items.Add(new MenuFlyoutSeparator());

        var exitItem = new MenuFlyoutItem
        {
            Text = "退出 ScreenTime",
            Command = new RelayCommand(() => App.Current.ExitApp()),
        };
        menu.Items.Add(exitItem);

        _tray.ContextFlyout = menu;
        _tray.ContextMenuMode = ContextMenuMode.PopupMenu; // 显式声明，语义清晰

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
