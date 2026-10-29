using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;
using ScreenTime.Data;
using ScreenTime.Services;

namespace ScreenTime.UI;

/// <summary>
/// 应用排行右键菜单。
/// <para>
/// 今日页用精简版（<paramref name="full"/>=false）：只留「打开 / 定位 / 复制路径」，
/// 避免把管理操作塞进日常查看的页面；历史页用完整版，承担原「应用」页的全部管理功能。
/// </para>
/// <para>
/// 数据库写操作一律走后台线程：UI 与 Agent 共享同一个 SQLite 库（busy_timeout=3s），
/// 在 UI 线程同步写时一旦撞上 Agent 的心跳写就会原地等锁，表现为「点了没反应」。
/// 剪贴板与 ShellExecute 必须留在 UI 线程（前者要求 STA，后者会拉起 UAC/前台窗口）。
/// </para>
/// </summary>
public static class AppActionMenu
{
    public static void Show(FrameworkElement target, AppRankItem app, Windows.Foundation.Point position,
        bool full = true)
    {
        var menu = new MenuFlyout();

        AddUi(menu, "打开应用", () =>
        {
            if (File.Exists(app.ExePath))
                Process.Start(new ProcessStartInfo(app.ExePath) { UseShellExecute = true });
        });
        AddUi(menu, "定位文件", () =>
        {
            if (File.Exists(app.ExePath))
                Process.Start("explorer.exe", $"/select,\"{app.ExePath}\"");
        });
        AddUi(menu, "复制 exe 路径", () =>
        {
            if (string.IsNullOrWhiteSpace(app.ExePath)) return;
            var data = new DataPackage();
            data.SetText(app.ExePath);
            Clipboard.SetContent(data);
        });

        if (full)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
            AddUi(menu, "使用历史…", () => ShowUsageHistory(target, app));

            var category = new MenuFlyoutSubItem { Text = "设置分类" };
            foreach (var value in CategoryService.Categories)
                AddDb(category, value, db => db.SetAppCategory(app.Id, value));
            menu.Items.Add(category);

            var purpose = new MenuFlyoutSubItem { Text = "设置默认用途" };
            foreach (var value in PurposeService.Purposes)
                AddDb(purpose, PurposeService.Display(value), db => db.SetAppDefaultPurpose(app.Id, value));
            menu.Items.Add(purpose);

            menu.Items.Add(new MenuFlyoutSeparator());
            AddDb(menu, "忽略此应用", db => db.SetAppIgnored(app.Id, true));
            AddDb(menu, "恢复统计", db => db.SetAppIgnored(app.Id, false));

            var merge = new MenuFlyoutSubItem { Text = "合并到" };
            foreach (var targetApp in App.Current.Database.GetAllApps().Where(a => a.Id != app.Id).Take(100))
                AddDb(merge, targetApp.Name, db => db.MergeApp(app.Id, targetApp.Id));
            if (merge.Items.Count > 0) menu.Items.Add(merge);
        }

        menu.ShowAt(target, position);
    }

    /// <summary>
    /// 单应用使用历史（原「应用」页的核心功能）。近 30 天按天拆分，
    /// 查询在后台线程跑，弹窗只负责展示。
    /// </summary>
    private static async void ShowUsageHistory(FrameworkElement target, AppRankItem app)
    {
        try
        {
            var db = App.Current.Database;
            var rows = await System.Threading.Tasks.Task.Run(() =>
                db.QueryAppDailyUsage(app.Id, DateTime.UtcNow.AddDays(-30), DateTime.UtcNow));

            var total = rows.Sum(r => r.Seconds);
            var panel = new StackPanel { Spacing = 4 };
            if (rows.Count == 0)
            {
                panel.Children.Add(new TextBlock
                {
                    Text = "近 30 天没有使用记录。",
                    Opacity = 0.7,
                    FontSize = 13
                });
            }
            else
            {
                foreach (var row in rows)
                {
                    var line = new Grid();
                    line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    var dayText = DateTime.TryParseExact(row.Day, "yyyy-MM-dd", null,
                        System.Globalization.DateTimeStyles.None, out var day)
                        ? day.ToString("M月d日 dddd")
                        : row.Day;

                    var left = new TextBlock { Text = dayText, FontSize = 13 };
                    var right = new TextBlock
                    {
                        Text = StatsService.FormatDuration(row.Seconds),
                        FontSize = 13,
                        Opacity = 0.7
                    };
                    Grid.SetColumn(right, 1);
                    line.Children.Add(left);
                    line.Children.Add(right);
                    panel.Children.Add(line);
                }
            }

            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(new TextBlock
            {
                Text = rows.Count > 0
                    ? $"近 30 天共 {StatsService.FormatDuration(total)}，{rows.Count} 天有记录"
                    : "近 30 天没有使用记录",
                FontSize = 12,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap
            });
            content.Children.Add(new ScrollViewer
            {
                Content = panel,
                MaxHeight = 360,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            });

            var dialog = new ContentDialog
            {
                XamlRoot = target.XamlRoot,
                Title = app.Name,
                Content = content,
                CloseButtonText = "关闭"
            };
            await dialog.ShowAsync();
        }
        catch
        {
            // 弹窗失败（例如已有对话框打开）不影响其他操作
        }
    }

    /// <summary>纯界面操作：留在 UI 线程执行。</summary>
    private static void AddUi(MenuFlyout menu, string text, Action action)
        => menu.Items.Add(CreateItem(text, action));

    private static void AddUi(MenuFlyoutSubItem menu, string text, Action action)
        => menu.Items.Add(CreateItem(text, action));

    /// <summary>数据库写操作：丢到线程池，完成后回 UI 线程刷新。</summary>
    private static void AddDb(MenuFlyout menu, string text, Action<Database> action)
        => menu.Items.Add(CreateDbItem(text, action));

    private static void AddDb(MenuFlyoutSubItem menu, string text, Action<Database> action)
        => menu.Items.Add(CreateDbItem(text, action));

    private static MenuFlyoutItem CreateItem(string text, Action action)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += (_, _) =>
        {
            try { action(); } catch { }
        };
        return item;
    }

    private static MenuFlyoutItem CreateDbItem(string text, Action<Database> action)
    {
        var item = new MenuFlyoutItem { Text = text };
        item.Click += async (_, _) =>
        {
            try
            {
                var db = App.Current.Database;
                await System.Threading.Tasks.Task.Run(() => action(db));
            }
            catch
            {
                // 写失败（例如锁超时）不弹窗，刷新时界面会显示真实状态
            }
            // await 之后已回到 UI 线程
            App.Current.RefreshCurrentPage();
        };
        return item;
    }
}
