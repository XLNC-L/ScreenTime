using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ScreenTime.Data;
using ScreenTime.Services;

namespace ScreenTime.Pages;

/// <summary>
/// 网页页：跨 Chrome/Edge 展示网页前台片段的时间线。
/// 顶部可按来源浏览器、时间范围与关键字（标题 / 域名 / 地址）筛选；
/// 筛选全在本地内存完成，不重新查库。
/// </summary>
public partial class WebPage : Page
{
    private sealed record TimelineItem(
        string DateLabel, string TimeLabel, string Title, string Sub,
        string Duration, Brush DotBrush);

    /// <summary>浏览器筛选项：Process 为空表示「全部」。</summary>
    private sealed record BrowserOption(string Process, string Display);

    private const string AllBrowsers = "";

    private bool _loading = true;

    /// <summary>已加载的时间线原始数据，搜索与浏览器筛选都在它上面做本地过滤。</summary>
    private List<WebTimelineRow> _timelineRows = new();

    private Dictionary<string, string> _nameByProcess = new(StringComparer.OrdinalIgnoreCase);
    private int _days = 30;

    /// <summary>搜索关键字：从 TextChanged 的 sender 取，避免 AutoSuggestBox.Text 的更新时序问题。</summary>
    private string _searchKeyword = "";

    /// <summary>搜索防抖：逐字符重建 200 项列表会卡。</summary>
    private readonly DispatcherTimer _searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(200) };

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
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            ApplyTimelineFilter();
        };
        // 页面切走后让在途查询结果作废
        Unloaded += (_, _) =>
        {
            _searchDebounce.Stop();
            _version++;
        };
    }

    private void RangeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !IsLoaded) return;
        // 换时间范围需要重新查库
        LoadPages();
    }

    private void BrowserBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || !IsLoaded) return;
        // 换浏览器只影响过滤，数据已经加载好了
        ApplyTimelineFilter();
    }

    /// <summary>加载入口：失败时在页面内提示并可重试，而不是靠全局 handler 留白。</summary>
    private void LoadPages() => _ = LoadPagesAsync();

    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _version;
    private bool _pending;

    /// <summary>
    /// 两个跨表查询放后台线程；聚合与绑定留在 UI（纯内存操作，毫秒级）。
    /// 网页片段量大时（30 天可能上万条），同步查询会把切页卡住。
    /// </summary>
    private async System.Threading.Tasks.Task LoadPagesAsync()
    {
        if (!await _gate.WaitAsync(0))
        {
            _pending = true;
            return;
        }

        var version = ++_version;
        try
        {
            var days = 30;
            if (RangeBox.SelectedItem is ComboBoxItem item && item.Tag is string tag &&
                int.TryParse(tag, out var n))
                days = n;

            var db = App.Current.Database;
            var to = DateTime.UtcNow;
            var from = to.AddDays(-days);

            var (rows, timelineRows) = await System.Threading.Tasks.Task.Run(
                () => (db.QueryWebPages(from, to), db.QueryWebTimeline(from, to)));

            if (version != _version || !IsLoaded) return;

            LoadPagesCore(rows, timelineRows, days);
            ErrorBar.IsOpen = false;
        }
        catch (Exception ex)
        {
            ErrorBar.Message = ex.Message;
            ErrorBar.IsOpen = true;
        }
        finally
        {
            _gate.Release();
            if (_pending)
            {
                _pending = false;
                _ = LoadPagesAsync();
            }
        }
    }

    private void RetryBtn_Click(object sender, RoutedEventArgs e) => LoadPages();

    private void LoadPagesCore(List<WebPageRow> rows, List<WebTimelineRow> timelineRows, int days)
    {
        _days = days;
        _timelineRows = timelineRows;

        _nameByProcess = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in App.Current.Database.GetAllApps())
            _nameByProcess.TryAdd(app.ProcessName, app.Name);

        RefreshBrowserFilter(rows, _nameByProcess);
        ApplyTimelineFilter();
    }

    /// <summary>浏览器筛选或搜索词变化时重新过滤时间线（本地过滤，不重新查库）。</summary>
    private void ApplyTimelineFilter()
    {
        var filter = (BrowserBox.SelectedItem as BrowserOption)?.Process ?? AllBrowsers;
        var shown = FilterTimeline(filter);
        LoadTimeline(shown);
        UpdateSummary(shown, filter);
    }

    private List<WebTimelineRow> FilterTimeline(string processFilter)
    {
        IEnumerable<WebTimelineRow> rows = _timelineRows;

        if (processFilter.Length > 0)
            rows = rows.Where(r => string.Equals(r.ProcessName, processFilter, StringComparison.OrdinalIgnoreCase));

        var keyword = _searchKeyword.Trim();
        if (keyword.Length > 0)
            rows = rows.Where(r => ContainsText(r.Title, keyword) ||
                                   ContainsText(r.Site, keyword) ||
                                   ContainsText(r.Url, keyword));

        return rows.ToList();
    }

    private static bool ContainsText(string? text, string keyword) =>
        !string.IsNullOrEmpty(text) && text.Contains(keyword, StringComparison.OrdinalIgnoreCase);

    private void UpdateSummary(List<WebTimelineRow> shown, string filter)
    {
        if (shown.Count == 0)
        {
            SummaryText.Text = "";
            return;
        }

        var total = shown.Sum(r => (r.EndUtc - r.StartUtc).TotalSeconds);
        var scope = filter.Length == 0
            ? ""
            : $"{_nameByProcess.GetValueOrDefault(filter) ?? filter} · ";
        SummaryText.Text =
            $"{scope}近 {_days} 天共 {shown.Count} 条网页记录，网页时长 {StatsService.FormatDuration(total)}";
    }

    /// <summary>绘制网页访问时间线，最多展示最近 200 条片段。</summary>
    private void LoadTimeline(IReadOnlyList<WebTimelineRow> rows)
    {
        // 强调色只解析一次：原来每行都去 Resources 里查，且 SystemAccentColor 是 Color 不是 Brush，
        // `as Brush` 恒为 null，导致圆点永远是写死的蓝色。
        var dotBrush = ResolveAccentBrush();

        var items = rows
            .OrderByDescending(r => r.StartUtc)
            .Take(200)
            .Select(r =>
            {
                var start = r.StartUtc.ToLocalTime();
                var title = BrowserTabService.CleanTitle(r.Title);
                if (string.IsNullOrEmpty(title)) title = string.IsNullOrEmpty(r.Site) ? "未识别页面" : r.Site;
                var sub = !string.IsNullOrEmpty(r.Site) ? r.Site : r.AppName;
                return new TimelineItem(
                    start.ToString("M月d日 ddd"),
                    $"{start:HH:mm} · {r.AppName}",
                    title,
                    sub,
                    StatsService.FormatDuration((r.EndUtc - r.StartUtc).TotalSeconds),
                    dotBrush);
            })
            .ToList();

        TimelineList.ItemsSource = items;
        TimelineEmptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TimelineSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        _searchKeyword = sender.Text ?? "";
        // 输入防抖：逐字符重建 200 项列表会卡
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    /// <summary>回车提交：立即过滤，不等防抖。</summary>
    private void TimelineSearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        _searchDebounce.Stop();
        _searchKeyword = sender.Text ?? "";
        ApplyTimelineFilter();
    }

    private static Brush ResolveAccentBrush()
    {
        var res = Application.Current.Resources;
        if (res.TryGetValue("AccentFillColorDefaultBrush", out var b) && b is Brush brush) return brush;
        if (res.TryGetValue("SystemAccentColor", out var c) && c is Windows.UI.Color color)
            return new SolidColorBrush(color);
        return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0, 120, 212));
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

}
