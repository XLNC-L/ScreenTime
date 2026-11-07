using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using ScreenTime.Services;

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

        var category = new MenuFlyoutSubItem { Text = "设置分类" };
        foreach (var value in CategoryService.Categories)
            Add(category, value, () => { App.Current.Database.SetAppCategory(app.Id, value); App.Current.RefreshCurrentPage(); });
        menu.Items.Add(category);

        var purpose = new MenuFlyoutSubItem { Text = "设置默认用途" };
        foreach (var value in PurposeService.Purposes)
            Add(purpose, PurposeService.Display(value), () => { App.Current.Database.SetAppDefaultPurpose(app.Id, value); App.Current.RefreshCurrentPage(); });
        menu.Items.Add(purpose);

        Add(menu, "忽略此应用", () => { App.Current.Database.SetAppIgnored(app.Id, true); App.Current.RefreshCurrentPage(); });
        Add(menu, "恢复统计", () => { App.Current.Database.SetAppIgnored(app.Id, false); App.Current.RefreshCurrentPage(); });

        var merge = new MenuFlyoutSubItem { Text = "合并到" };
        foreach (var targetApp in App.Current.Database.GetAllApps().Where(a => a.Id != app.Id).Take(100))
            Add(merge, targetApp.Name, () => { App.Current.Database.MergeApp(app.Id, targetApp.Id); App.Current.RefreshCurrentPage(); });
        if (merge.Items.Count > 0) menu.Items.Add(merge);
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

    private static void Add(MenuFlyoutSubItem menu, string text, Action action)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) => { try { action(); } catch { } };
        menu.Items.Add(item);
    }
}
