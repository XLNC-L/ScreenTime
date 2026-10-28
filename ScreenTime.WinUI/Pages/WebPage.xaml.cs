using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ScreenTime.Data;
using ScreenTime.Services;

namespace ScreenTime.Pages;

/// <summary>
/// 网页页：跨 Chrome/Edge 汇总各页面时长。
/// 三层结构：分组头为域名（host），行内为具体页面（host/path），
/// 行首用小号浏览器图标标明来源，不再用文字写「谷歌浏览器 · …」。
/// 没有地址的片段按标题归入「未识别页面」，不按域名压平，也不重复成两条。
/// </summary>
public partial class WebPage : Page
{
    private sealed record PageRow(string Title, string Sub, string Duration, string Url, ImageSource? Icon);

    private sealed record PageGroup(string Host, double Seconds, string Duration, List<PageRow> Pages);

    /// <summary>浏览器筛选项：Process 为空表示「全部」。</summary>
    private sealed record BrowserOption(string Process, string Display);

    private const string UnknownGroup = "未识别页面";
    private const string AllBrowsers = "";

    private bool _loading = true;

    public WebPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _loading = true;
            if (RangeBox.SelectedIndex < 0) RangeBox.SelectedIndex = 1;
            _loading = false;
            LoadPages();
        };
    }

    private void RangeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !IsLoaded) return;
        LoadPages();
    }

    private void BrowserBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !IsLoaded) return;
        LoadPages();
    }

    /// <summary>加载入口：失败时在页面内提示并可重试，而不是靠全局 handler 留白。</summary>
    private void LoadPages()
    {
        try
        {
            LoadPagesCore();
            ErrorBar.IsOpen = false;
        }
        catch (Exception ex)
        {
            ErrorBar.Message = ex.Message;
            ErrorBar.IsOpen = true;
        }
    }

    private void RetryBtn_Click(object sender, RoutedEventArgs e) => LoadPages();

    private void LoadPagesCore()
    {
        var days = 30;
        if (RangeBox.SelectedItem is ComboBoxItem item && item.Tag is string tag &&
            int.TryParse(tag, out var n))
            days = n;

        var db = App.Current.Database;
        var to = DateTime.UtcNow;
        var rows = db.QueryWebPages(to.AddDays(-days), to);

        // exe 路径取自 apps 表：浏览器没在运行时也能拿到图标
        var exeByProcess = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var nameByProcess = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in db.GetAllApps())
        {
            if (!string.IsNullOrEmpty(app.ExePath)) exeByProcess.TryAdd(app.ProcessName, app.ExePath);
            nameByProcess.TryAdd(app.ProcessName, app.Name);
        }

        RefreshBrowserFilter(rows, nameByProcess);

        var filter = (BrowserBox.SelectedItem as BrowserOption)?.Process ?? AllBrowsers;
        var filtered = filter.Length == 0
            ? rows
            : rows.Where(r => string.Equals(r.ProcessName, filter, StringComparison.OrdinalIgnoreCase)).ToList();

        // 页面级聚合：key 带上浏览器，同一网址在 Chrome 和 Edge 各算一行，图标才不会含糊
        var pages = new Dictionary<(string Host, string Process, string Key),
            (double Seconds, double TitleSeconds, string Title, string Sub, string Url)>();

        // 标题 → 已知页面：把「同站无地址」的片段并回同一行，避免同一页面拆成两条。
        // 按浏览器分别建映射，否则 Chrome 的标题会把 Edge 的片段也吸过去。
        var titleSite = new Dictionary<(string Process, string Key), string>();
        var titleSiteTime = new Dictionary<(string Process, string Key, string Site), double>();

        foreach (var r in filtered)
        {
            if (string.IsNullOrEmpty(r.Site)) continue;

            var title = BrowserTabService.CleanTitle(r.Title);
            var host = HostOf(r.Site);
            var display = !string.IsNullOrEmpty(title) ? title : r.Site;

            var k = (host, r.ProcessName, r.Site);
            pages.TryGetValue(k, out var g);
            g.Seconds += r.Seconds;
            // 同一地址下可能有多个标题：取占用时长最长的那个，避免被一闪而过的标题顶替
            if (!string.IsNullOrEmpty(display) && r.Seconds > g.TitleSeconds)
            {
                g.Title = display;
                g.TitleSeconds = r.Seconds;
            }
            // 浏览器已由行首图标表示，副标题只留页面地址
            if (string.IsNullOrEmpty(g.Sub)) g.Sub = r.Site;
            if (!string.IsNullOrEmpty(r.Url)) g.Url = r.Url;
            pages[k] = g;

            var key = NormalizeTitle(title);
            if (key.Length >= 4)
            {
                var tk = (r.ProcessName, key, r.Site);
                titleSiteTime.TryGetValue(tk, out var t);
                titleSiteTime[tk] = t + r.Seconds;
            }
        }

        // 同一标题对应多个页面时，取时长最长的那个
        foreach (var ((process, key, site), seconds) in titleSiteTime)
        {
            if (!titleSite.TryGetValue((process, key), out var existing) ||
                titleSiteTime[(process, key, existing)] < seconds)
                titleSite[(process, key)] = site;
        }

        foreach (var r in filtered)
        {
            if (!string.IsNullOrEmpty(r.Site)) continue;

            var title = BrowserTabService.CleanTitle(r.Title);
            if (string.IsNullOrEmpty(title)) continue;
            var key = NormalizeTitle(title);

            // 标题能对上已知页面：并回该页面，不另立「未识别」条目
            if (key.Length >= 4 && titleSite.TryGetValue((r.ProcessName, key), out var knownSite))
            {
                var k2 = (HostOf(knownSite), r.ProcessName, knownSite);
                pages.TryGetValue(k2, out var g2);
                g2.Seconds += r.Seconds;
                if (string.IsNullOrEmpty(g2.Title)) g2.Title = title;
                pages[k2] = g2;
                continue;
            }

            var ku = (UnknownGroup, r.ProcessName, key);
            pages.TryGetValue(ku, out var gu);
            gu.Seconds += r.Seconds;
            if (string.IsNullOrEmpty(gu.Title)) gu.Title = title;
            pages[ku] = gu;
        }

        var groups = pages
            .GroupBy(kv => kv.Key.Host)
            .Select(g =>
            {
                var seconds = g.Sum(x => x.Value.Seconds);
                return new PageGroup(
                    g.Key,
                    seconds,
                    StatsService.FormatDuration(seconds),
                    g.OrderByDescending(x => x.Value.Seconds)
                        .Select(x => new PageRow(
                            x.Value.Title,
                            x.Value.Sub,
                            StatsService.FormatDuration(x.Value.Seconds),
                            x.Value.Url,
                            AppIconService.GetIcon(
                                exeByProcess.GetValueOrDefault(x.Key.Process) ?? "",
                                x.Key.Process)))
                        .ToList());
            })
            // 「未识别页面」是兜底分组，固定放在最后
            .OrderBy(g => g.Host == UnknownGroup ? 1 : 0)
            .ThenByDescending(g => g.Seconds)
            .ToList();

        GroupList.ItemsSource = groups;
        EmptyText.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var total = pages.Sum(p => p.Value.Seconds);
        var scope = filter.Length == 0
            ? ""
            : $"{nameByProcess.GetValueOrDefault(filter) ?? filter} · ";
        SummaryText.Text = groups.Count == 0
            ? ""
            : $"{scope}近 {days} 天共 {pages.Count} 个页面，网页时长 {StatsService.FormatDuration(total)}";
    }

    /// <summary>按数据里真实出现过的浏览器重建筛选项，尽量保留用户已选的那个。</summary>
    private void RefreshBrowserFilter(
        IReadOnlyList<WebPageRow> rows, Dictionary<string, string> nameByProcess)
    {
        var processes = rows.Select(r => r.ProcessName)
            .Where(p => !string.IsNullOrEmpty(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (processes.Count == 0) return;

        var wanted = (BrowserBox.SelectedItem as BrowserOption)?.Process ?? AllBrowsers;

        var options = new List<BrowserOption> { new(AllBrowsers, "全部浏览器") };
        options.AddRange(processes
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Select(p => new BrowserOption(p, nameByProcess.GetValueOrDefault(p) ?? p)));

        // 选项没变就不重建，避免每次刷新都把下拉框重置
        if (BrowserBox.Items.Count == options.Count &&
            BrowserBox.Items.OfType<BrowserOption>().Select(o => o.Process)
                .SequenceEqual(options.Select(o => o.Process), StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        _loading = true;
        BrowserBox.ItemsSource = options;
        BrowserBox.SelectedItem = options.FirstOrDefault(o =>
            string.Equals(o.Process, wanted, StringComparison.OrdinalIgnoreCase)) ?? options[0];
        _loading = false;
    }

    /// <summary>site（host/path）→ host；不含点的内部页面归入「未识别页面」。</summary>
    private static string HostOf(string site)
    {
        var slash = site.IndexOf('/');
        var host = slash >= 0 ? site[..slash] : site;
        return host.Contains('.') ? host : UnknownGroup;
    }

    private static string NormalizeTitle(string title) =>
        string.Join(" ", title.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    /// <summary>点击页面行：在默认浏览器打开原始地址。</summary>
    private void PageRow_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not PageRow row) return;
        if (string.IsNullOrEmpty(row.Url)) return;
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(row.Url) { UseShellExecute = true });
        }
        catch
        {
            // 地址无法打开时忽略
        }
    }
}
