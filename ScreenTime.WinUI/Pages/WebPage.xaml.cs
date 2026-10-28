using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ScreenTime.Data;
using ScreenTime.Services;

namespace ScreenTime.Pages;

/// <summary>
/// 网页页：跨 Chrome/Edge 汇总各页面时长。
/// 两层结构：分组头为域名（host），行内为具体页面（host/path）；
/// 没有地址的片段按标题归入「未识别页面」，不按域名压平，也不重复成两条。
/// </summary>
public partial class WebPage : Page
{
    private sealed record PageRow(string Title, string Sub, string Duration, string Url);

    private sealed record PageGroup(string Host, double Seconds, string Duration, List<PageRow> Pages);

    private const string UnknownGroup = "未识别页面";

    public WebPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (RangeBox.SelectedIndex < 0) RangeBox.SelectedIndex = 1;
            LoadPages();
        };
    }

    private void RangeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) LoadPages();
    }

    private void LoadPages()
    {
        var days = 30;
        if (RangeBox.SelectedItem is ComboBoxItem item && item.Tag is string tag &&
            int.TryParse(tag, out var n))
            days = n;

        var to = DateTime.UtcNow;
        var rows = App.Current.Database.QueryWebPages(to.AddDays(-days), to);

        // 页面级聚合：有地址用 site，无地址用标题；同一 key 只出现一次
        var pages = new Dictionary<(string Host, string Key),
            (double Seconds, double TitleSeconds, string Title, string Sub, string Url)>();
        // 标题 → 已知页面：用有地址的片段建立映射，把「同站无地址」的片段并回同一行，避免抖音两条并存
        var titleSite = new Dictionary<string, string>();
        var titleSiteTime = new Dictionary<(string Title, string Site), double>();

        foreach (var r in rows)
        {
            if (string.IsNullOrEmpty(r.Site)) continue;

            var title = BrowserTabService.CleanTitle(r.Title);
            var host = HostOf(r.Site);
            var display = !string.IsNullOrEmpty(title) ? title : r.Site;
            var sub = host == UnknownGroup ? r.AppName : $"{r.AppName} · {r.Site}";

            var k = (host, r.Site);
            pages.TryGetValue(k, out var g);
            g.Seconds += r.Seconds;
            // 同一地址下可能有多个标题：取占用时长最长的那个，避免被一闪而过的标题顶替
            if (!string.IsNullOrEmpty(display) && r.Seconds > g.TitleSeconds)
            {
                g.Title = display;
                g.TitleSeconds = r.Seconds;
            }
            if (string.IsNullOrEmpty(g.Sub)) g.Sub = sub;
            if (!string.IsNullOrEmpty(r.Url)) g.Url = r.Url;
            pages[k] = g;

            var key = NormalizeTitle(title);
            if (key.Length >= 4)
            {
                var tk = (key, r.Site);
                titleSiteTime.TryGetValue(tk, out var t);
                titleSiteTime[tk] = t + r.Seconds;
            }
        }

        // 同一标题对应多个页面时，取时长最长的那个
        foreach (var ((key, site), seconds) in titleSiteTime)
        {
            if (!titleSite.TryGetValue(key, out var existing) ||
                titleSiteTime[(key, existing)] < seconds)
                titleSite[key] = site;
        }

        foreach (var r in rows)
        {
            if (!string.IsNullOrEmpty(r.Site)) continue;

            var title = BrowserTabService.CleanTitle(r.Title);
            if (string.IsNullOrEmpty(title)) continue;
            var key = NormalizeTitle(title);

            // 标题能对上已知页面：并回该页面，不另立「未识别」条目
            if (key.Length >= 4 && titleSite.TryGetValue(key, out var knownSite))
            {
                var host = HostOf(knownSite);
                var k2 = (host, knownSite);
                pages.TryGetValue(k2, out var g2);
                g2.Seconds += r.Seconds;
                pages[k2] = g2;
                continue;
            }

            var ku = (UnknownGroup, key);
            pages.TryGetValue(ku, out var gu);
            gu.Seconds += r.Seconds;
            if (string.IsNullOrEmpty(gu.Title)) gu.Title = title;
            if (string.IsNullOrEmpty(gu.Sub)) gu.Sub = r.AppName;
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
                            x.Value.Url))
                        .ToList());
            })
            // 「未识别页面」是兜底分组，固定放在最后
            .OrderBy(g => g.Host == UnknownGroup ? 1 : 0)
            .ThenByDescending(g => g.Seconds)
            .ToList();

        GroupList.ItemsSource = groups;
        EmptyText.Visibility = groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var total = pages.Sum(p => p.Value.Seconds);
        SummaryText.Text = groups.Count == 0
            ? ""
            : $"近 {days} 天共 {pages.Count} 个页面，网页时长 {StatsService.FormatDuration(total)}";
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
