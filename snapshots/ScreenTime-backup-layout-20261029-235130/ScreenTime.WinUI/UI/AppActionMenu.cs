using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace ScreenTime.UI;

/// <summary>今日/历史应用排行共用的右键操作菜单。</summary>
public static class AppActionMenu
{
    public static void Show(FrameworkElement target, AppRankItem app, Windows.Foundation.Point position)
    {
        var menu = new MenuFlyout();
        Add(menu, "打开应用", () =>
        {
            if (File.Exists(app.ExePath))
                Process.Start(new ProcessStartInfo(app.ExePath) { UseShellExecute = true });
        });
        Add(menu, "定位文件", () =>
        {
            if (File.Exists(app.ExePath))
                Process.Start("explorer.exe", $"/select,\"{app.ExePath}\"");
        });
        Add(menu, "复制 exe 路径", () =>
        {
            if (string.IsNullOrWhiteSpace(app.ExePath)) return;
            var data = new DataPackage();
            data.SetText(app.ExePath);
            Clipboard.SetContent(data);
        });
        menu.ShowAt(target, position);
    }

    private static void Add(MenuFlyout menu, string text, Action action)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) =>
        {
            try { action(); } catch { }
        };
        menu.Items.Add(item);
    }
}
