using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using ScreenTime.Services;

namespace ScreenTime.Pages;

public partial class AppsPage : Page
{
    private bool _loading = true;
    private string? _pendingFilter;

    private const string AllFilter = "全部";

    private record AppItem(int Id, string Name, string Sub, int Ignored, int MergedInto, ImageSource? Icon);
    private record SiteItem(string Site, string Sub, string Duration, string Url);

    public AppsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadApps();
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string cat && cat != "")
            _pendingFilter = cat;
    }

    private void LoadApps()
    {
        _loading = true;
        var db = App.Current.Database;
        var apps = db.GetAllApps();
        var search = (SearchBox.Text ?? "").Trim();

        // 初始化分类筛选（全部 + 8 类），应用待处理筛选
        if (FilterBox.Items.Count == 0)
        {
            var opts = new List<string> { AllFilter };
            opts.AddRange(CategoryService.Categories);
            FilterBox.ItemsSource = opts;
        }
        var filter = _pendingFilter ?? (FilterBox.SelectedItem as string) ?? AllFilter;
        if (_pendingFilter != null)
        {
            FilterBox.SelectedItem = filter;
            _pendingFilter = null;
        }
        else if (FilterBox.SelectedItem == null)
        {
            FilterBox.SelectedItem = AllFilter;
        }

        var showHidden = ShowHiddenSwitch.IsOn;
        var usedOnly = UsedOnlyBox.IsChecked == true;
        var items = apps
            // 默认隐藏「已忽略」与「已合并」条目，避免列表被系统外壳和重复项占满
            .Where(a => showHidden || (a.Ignored == 0 && a.MergedInto == 0))
            // 导入已安装应用会补进一批时长为 0 的条目，勾选后只看真正用过的
            .Where(a => !usedOnly || a.SegmentCount > 0)
            .Where(a => filter == AllFilter || a.Category == filter)
            // 同时匹配显示名与进程名，便于用 chrome / msedge 等英文名搜索中文显示名
            .Where(a => string.IsNullOrEmpty(search) ||
                        a.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                        a.ProcessName.Contains(search, StringComparison.OrdinalIgnoreCase))
            .Select(a => new AppItem(a.Id, a.Name,
                a.Ignored != 0 ? $"{a.ProcessName}  ·  已忽略"
                : a.MergedInto != 0 ? $"{a.ProcessName}  ·  已合并"
                : $"{a.ProcessName}  ·  {a.Category}",
                a.Ignored, a.MergedInto, SafeIcon(a.ExePath, a.ProcessName, a.IconSource)))
            .ToList();

        // 先绑定各下拉框数据源（此时应用列表尚未选中，不会误触发手动分类）
        CategoryBox.ItemsSource = CategoryService.Categories;
        PurposeBox.ItemsSource = PurposeService.Purposes;
        MergeBox.ItemsSource = apps.Select(a => new AppItem(a.Id, a.Name, a.ProcessName, a.Ignored, a.MergedInto, null)).ToList();

        // 最后绑定应用列表（会触发一次选中，下拉框已就绪且受 _loading 保护）
        AppListBox.ItemsSource = items;

        _loading = false;
    }

    private void FilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !IsLoaded) return;
        LoadApps();
    }

    private void ShowHiddenSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || !IsLoaded) return;
        LoadApps();
    }

    private void UsedOnlyBox_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || !IsLoaded) return;
        LoadApps();
    }

    private static ImageSource? SafeIcon(string exePath, string processName, string iconSource)
    {
        try { return AppIconService.GetIcon(exePath, processName, iconSource); }
        catch { return null; }
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (IsLoaded) LoadApps();
    }

    /// <summary>扫描开始菜单与已安装的 UWP/商店应用，补充还没用过的应用。</summary>
    private async void ImportBtn_Click(object sender, RoutedEventArgs e)
    {
        ImportBtn.IsEnabled = false;
        int desktop = 0, uwp = 0;
        try
        {
            var db = App.Current.Database;
            desktop = await Task.Run(() => InstalledAppService.Import(db));
            // UWP 导入全程 await（内部有 WinRT 异步调用），放在后台线程上跑，
            // 既不卡 UI 也不会因为同步阻塞 WinRT 而卡死
            uwp = await Task.Run(() => UwpAppService.ImportAsync(db));
        }
        finally
        {
            ImportBtn.IsEnabled = true;
        }

        LoadApps();

        var total = desktop + uwp;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "导入已安装应用",
            Content = total > 0
                ? $"新增 {total} 个应用（桌面程序 {desktop} 个、UWP/商店应用 {uwp} 个）。\n\n时长为 0，不影响统计；分类不出来的会进「收件箱」，可在那里归类。"
                : "没有新的应用可导入：开始菜单与已安装的商店应用都已在列表中。",
            CloseButtonText = "好"
        };
        await dialog.ShowAsync();
    }

    private void AppListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AppListBox.SelectedItem is not AppItem item) return;
        _loading = true;
        var apps = App.Current.Database.GetAllApps();
        var data = apps.First(a => a.Id == item.Id);

        AppNameText.Text = data.Name;
        AppProcText.Text = $"{data.ProcessName}    {data.Category}";
        CategoryBox.SelectedItem = data.Category;
        PurposeBox.SelectedItem = PurposeService.Resolve(data.DefaultPurpose, data.Category);
        IgnoreSwitch.IsOn = data.Ignored != 0;
        MergeBox.SelectedIndex = -1;
        _loading = false;

        LoadSiteUsage(data.Id, data.ProcessName);
    }

    /// <summary>
    /// 浏览器应用：加载近 30 天各网页时长；非浏览器隐藏。
    /// 有地址的按「域名/路径」聚合；没有地址的历史片段按网页标题聚合，避免大量"未知"。
    /// </summary>
    private void LoadSiteUsage(int appId, string processName)
    {
        if (BrowserTabService.IsBrowser(processName))
        {
            var toUtc = DateTime.UtcNow;
            var rows = App.Current.Database.QueryPageUsage(appId, toUtc.AddDays(-30), toUtc);

            var pages = new Dictionary<string, (double Seconds, double TitleSeconds, string Title, string Url, bool HasSite)>();
            // 标题 → 已知地址：把「同站无地址」的片段并回同一行，避免同一页面拆成两条
            var siteOfTitle = new Dictionary<string, string>();
            var titleTime = new Dictionary<(string Title, string Site), double>();

            foreach (var r in rows)
            {
                if (string.IsNullOrEmpty(r.Site)) continue;
                var title = BrowserTabService.CleanTitle(r.Title);
                pages.TryGetValue(r.Site, out var g);
                g.Seconds += r.Seconds;
                // 同一地址下可能有多个标题：取占用时长最长的那个，避免被一闪而过的标题顶替
                if (!string.IsNullOrEmpty(title) && r.Seconds > g.TitleSeconds)
                {
                    g.Title = title;
                    g.TitleSeconds = r.Seconds;
                }
                if (!string.IsNullOrEmpty(r.Url)) g.Url = r.Url;
                g.HasSite = true;
                pages[r.Site] = g;

                var key = NormalizeTitle(title);
                if (key.Length >= 4)
                {
                    var tk = (key, r.Site);
                    titleTime.TryGetValue(tk, out var tt);
                    titleTime[tk] = tt + r.Seconds;
                }
            }
            foreach (var ((key, site), seconds) in titleTime)
            {
                if (!siteOfTitle.TryGetValue(key, out var existing) ||
                    titleTime[(key, existing)] < seconds)
                    siteOfTitle[key] = site;
            }

            foreach (var r in rows)
            {
                if (!string.IsNullOrEmpty(r.Site)) continue;
                var title = BrowserTabService.CleanTitle(r.Title);
                var key = NormalizeTitle(title);
                if (key.Length >= 4 && siteOfTitle.TryGetValue(key, out var known))
                {
                    pages.TryGetValue(known, out var gk);
                    gk.Seconds += r.Seconds;
                    gk.HasSite = true;
                    pages[known] = gk;
                    continue;
                }

                var name = !string.IsNullOrEmpty(title) ? title : "（未知页面）";
                pages.TryGetValue(name, out var g);
                g.Seconds += r.Seconds;
                if (!string.IsNullOrEmpty(title) && r.Seconds > g.TitleSeconds)
                {
                    g.Title = title;
                    g.TitleSeconds = r.Seconds;
                }
                pages[name] = g;
            }

            SiteListBox.ItemsSource = pages
                .OrderByDescending(kv => kv.Value.Seconds)
                .Select(kv => new SiteItem(
                    kv.Key,
                    kv.Value.Title == kv.Key ? "" : kv.Value.Title,
                    FormatDuration(kv.Value.Seconds),
                    kv.Value.HasSite && kv.Key.Contains('.')
                        ? kv.Value.Url != "" ? kv.Value.Url : "https://" + kv.Key
                        : ""))
                .ToList();
            SitePanel.Visibility = Visibility.Visible;
        }
        else
        {
            SitePanel.Visibility = Visibility.Collapsed;
            SiteListBox.ItemsSource = null;
        }
    }

    private static string NormalizeTitle(string title) =>
        string.Join(" ", title.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    /// <summary>点击网页条目：在默认浏览器打开该地址。</summary>
    private void SiteListBox_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not SiteItem item || string.IsNullOrEmpty(item.Url)) return;
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(item.Url) { UseShellExecute = true });
        }
        catch
        {
            // 无法打开时忽略
        }
    }

    private static string FormatDuration(double seconds)
    {
        var span = TimeSpan.FromSeconds(seconds);
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}小时{span.Minutes}分";
        if (span.TotalMinutes >= 1) return $"{span.Minutes}分";
        return $"{span.Seconds}秒";
    }

    private void CategoryBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || AppListBox.SelectedItem is not AppItem item || CategoryBox.SelectedItem is not string cat)
            return;
        App.Current.Database.SetAppCategory(item.Id, cat);
        LoadApps();
    }

    private void PurposeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || AppListBox.SelectedItem is not AppItem item ||
            PurposeBox.SelectedItem is not string purpose)
            return;
        App.Current.Database.SetAppDefaultPurpose(item.Id, purpose);
    }

    private void IgnoreSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading || AppListBox.SelectedItem is not AppItem item) return;
        App.Current.Database.SetAppIgnored(item.Id, IgnoreSwitch.IsOn);
        LoadApps();
    }

    private async void MergeBtn_Click(object sender, RoutedEventArgs e)
    {
        if (AppListBox.SelectedItem is not AppItem item) return;
        if (MergeBox.SelectedItem is not AppItem target || target.Id == item.Id)
        {
            await ShowDialogAsync("请选择要合并到的其他应用。");
            return;
        }
        App.Current.Database.MergeApp(item.Id, target.Id);
        LoadApps();
    }

    private async System.Threading.Tasks.Task ShowDialogAsync(string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "ScreenTime",
            Content = message,
            CloseButtonText = "确定"
        };
        await dialog.ShowAsync();
    }
}
