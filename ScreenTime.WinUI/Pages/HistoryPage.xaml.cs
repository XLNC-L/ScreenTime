using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using ScreenTime.Data;
using ScreenTime.Services;
using ScreenTime.Themes;
using ScreenTime.UI;

namespace ScreenTime.Pages;

public partial class HistoryPage : Page
{
    private readonly Database _db = App.Current.Database;
    private CancellationTokenSource? _iconLoadCts;
    private List<AppRankItem> _allRows = new();

    /// <summary>应用 id → 分类，供分类下钻筛选（AppRankItem 不带分类字段，避免污染共用模型）。</summary>
    private Dictionary<int, string> _categoryByApp = new();

    /// <summary>当前分类筛选；null 表示不筛选。</summary>
    private string? _categoryFilter;

    /// <summary>
    /// 查询防抖：连点「查询」、快速切主题、右键连续操作都会触发多次全量查询，
    /// 200ms 内合并成一次。首次进入页面直接查，不延迟。
    /// </summary>
    private readonly DispatcherTimer _queryDebounce = new() { Interval = TimeSpan.FromMilliseconds(200) };

    /// <summary>查询门控：同一时间只跑一次查询，多出来的请求合并成「结束后补跑一次」。</summary>
    private readonly SemaphoreSlim _queryGate = new(1, 1);

    /// <summary>查询版本号：用于丢弃过期结果（用户快速切日期时旧查询可能晚到）。</summary>
    private int _queryVersion;

    private bool _queryPending;

    public HistoryPage()
    {
        InitializeComponent();
        // 不做导航缓存（默认 Disabled）：见 TodayPage 注释——缓存会让托盘隐藏时页面释放不掉。
        // 切页重建成本可接受，筛选状态在页面内重建（日期默认近 7 天）。
        Donut.SliceTapped += cat => App.Current.DrillToCategory(cat);
        FromPicker.Date = new DateTimeOffset(DateTime.Today.AddDays(-6));
        ToPicker.Date = new DateTimeOffset(DateTime.Today);
        LeftCol.SizeChanged += (_, e) => SyncRankHeight(e.NewSize.Height);
        _queryDebounce.Tick += (_, _) =>
        {
            _queryDebounce.Stop();
            RunQuery();
        };
        Loaded += (_, _) => RunQuery();
        Unloaded += (_, _) =>
        {
            _queryDebounce.Stop();
            _iconLoadCts?.Cancel();
            // 让在途查询结果作废：回调回来时页面可能已经切走
            _queryVersion++;
        };
    }

    /// <summary>防抖版查询请求：200ms 内的多次请求只执行一次。</summary>
    private void RequestQuery()
    {
        _queryDebounce.Stop();
        _queryDebounce.Start();
    }

    /// <summary>分类下钻：导航参数携带分类名（今日/历史页点环形图或图例时走这里）。</summary>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string category && category.Length > 0)
            _categoryFilter = category;
    }

    /// <summary>页面内切换分类筛选（已在历史页时由 MainWindow 直接调用）。</summary>
    public void FilterByCategory(string category)
    {
        _categoryFilter = string.IsNullOrWhiteSpace(category) ? null : category;
        UpdateCategoryChip();
        ApplyAppSearch();
    }

    private void UpdateCategoryChip()
    {
        if (_categoryFilter is { Length: > 0 } cat)
        {
            CategoryFilterText.Text = $"分类：{cat}";
            CategoryFilterChip.Visibility = Visibility.Visible;
        }
        else
        {
            CategoryFilterChip.Visibility = Visibility.Collapsed;
        }
    }

    private void ClearCategoryBtn_Click(object sender, RoutedEventArgs e) => FilterByCategory("");

    /// <summary>
    /// 导入已安装应用（原「应用」页的功能）：扫描开始菜单与 UWP/商店应用，
    /// 把还没用过的应用补进来，之后就能在收件箱里归类。
    /// </summary>
    private async void ImportAppsBtn_Click(object sender, RoutedEventArgs e)
    {
        ImportAppsBtn.IsEnabled = false;
        int desktop = 0, uwp = 0;
        try
        {
            var db = App.Current.Database;
            desktop = await System.Threading.Tasks.Task.Run(() => InstalledAppService.Import(db));
            // UWP 导入内部是 WinRT 异步调用，整体放到后台跑，避免阻塞 UI 线程
            uwp = await System.Threading.Tasks.Task.Run(() => UwpAppService.ImportAsync(db));
        }
        catch (Exception ex)
        {
            ErrorBar.Message = "导入失败：" + ex.Message;
            ErrorBar.IsOpen = true;
            return;
        }
        finally
        {
            ImportAppsBtn.IsEnabled = true;
        }

        var total = desktop + uwp;
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "导入已安装应用",
            Content = total > 0
                ? $"新增 {total} 个应用（桌面程序 {desktop} 个、UWP/商店应用 {uwp} 个）。\n\n时长为 0，不影响统计；分类不出来的会进「待办」，可在那里归类。"
                : "没有新的应用可导入：开始菜单与已安装的商店应用都已在列表中。",
            CloseButtonText = "好"
        };
        await dialog.ShowAsync();

        if (total > 0) RequestQuery();
        App.Current.RefreshInboxBadge();
    }

    /// <summary>左列决定高度，右列排行卡片跟随；高度没变就不动，避免反复触发布局。</summary>
    private void SyncRankHeight(double height)
    {
        if (height > 0 && (double.IsNaN(AppRankCard.Height) || Math.Abs(AppRankCard.Height - height) > 0.5))
            AppRankCard.Height = height;
    }

    /// <summary>主题切换：重跑查询重建配色（7 天数据量小），不重建页面。</summary>
    public void RefreshTheme() => RequestQuery();

    private void QueryBtn_Click(object sender, RoutedEventArgs e) => RequestQuery();

    /// <summary>查询入口：失败时在页面内提示并可重试，而不是靠全局 handler 留白。</summary>
    private void RunQuery() => _ = RunQueryAsync();

    /// <summary>
    /// 查询与聚合全部在后台线程执行，UI 线程只做绑定。
    /// 之前这段整跑在 UI 线程：7 天数据约 100ms 还能忍，日期范围一拉大就是秒级卡顿。
    /// </summary>
    private async System.Threading.Tasks.Task RunQueryAsync()
    {
        // 门控：上一次还没跑完就记一笔，等它结束后补跑最新的一次
        if (!await _queryGate.WaitAsync(0))
        {
            _queryPending = true;
            return;
        }

        var version = ++_queryVersion;
        try
        {
            // 历史页固定显示“今天 + 前六天”，始终为连续七天，避免跨页后筛选范围漂移。
            var fromDay = FromPicker.Date?.LocalDateTime.Date ?? DateTime.Today.AddDays(-6);
            var toDay = ToPicker.Date?.LocalDateTime.Date ?? DateTime.Today;
            if (toDay < fromDay) (fromDay, toDay) = (toDay, fromDay);
            var days = (toDay - fromDay).Days + 1;

            var from = fromDay.ToUniversalTime();
            var to = toDay.AddDays(1).ToUniversalTime();

            var snapshot = await System.Threading.Tasks.Task.Run(() => BuildSnapshot(from, to, fromDay, days));

            // 用户已经切到别的日期、或页面已经切走：丢弃这次结果
            if (version != _queryVersion || !IsLoaded) return;

            BindSnapshot(snapshot, days);
            ErrorBar.IsOpen = false;
        }
        catch (Exception ex)
        {
            ErrorBar.Message = ex.Message;
            ErrorBar.IsOpen = true;
        }
        finally
        {
            _queryGate.Release();
            if (_queryPending)
            {
                _queryPending = false;
                _ = RunQueryAsync();
            }
        }
    }

    /// <summary>后台查询产物：只有纯数据，不含任何 XAML 对象。</summary>
    private sealed record Snapshot(
        List<AppUsageRow> Apps,
        double TotalSeconds,
        List<(string Category, double Seconds)> Categories,
        string TopCategory,
        Dictionary<int, (string ExePath, string ProcessName, string IconSource)> IconByApp,
        List<(string Purpose, double Seconds)> Purposes,
        List<(DateTime Day, double Seconds)> DayTotals,
        double[] HourSeconds);

    /// <summary>在后台线程执行：查库 + 纯计算，不碰任何 UI 对象。</summary>
    private Snapshot BuildSnapshot(DateTime from, DateTime to, DateTime fromDay, int days)
    {
        var apps = _db.QueryAppUsage(from, to);
        var segments = _db.QuerySegments(from, to);
        var totalSec = StatsService.UnionSeconds(segments.Select(s => (s.Start, s.End)).ToList());

        var categories = apps
            .GroupBy(a => a.Category)
            .Select(g => (Category: g.Key, Seconds: g.Sum(a => a.TotalSeconds)))
            .Where(x => x.Seconds > 0)
            .OrderByDescending(x => x.Seconds)
            .ToList();

        var iconByApp = _db.GetAllApps().ToDictionary(a => a.Id,
            a => (a.ExePath, a.ProcessName, a.IconSource));
        var purposes = _db.QueryPurposeUsage(from, to);
        var topCategory = apps.OrderByDescending(a => a.TotalSeconds).FirstOrDefault()?.Category ?? "未分类";

        return new Snapshot(apps, totalSec, categories, topCategory, iconByApp, purposes,
            BuildDayTotals(segments, fromDay, days), BuildHourSeconds(segments));
    }

    /// <summary>按本地日切分每段时长（跨午夜会拆到两天），再按天做区间并集。</summary>
    private static List<(DateTime Day, double Seconds)> BuildDayTotals(
        List<(DateTime Start, DateTime End, int AppId, string Kind)> segments, DateTime fromDay, int days)
    {
        var result = new List<(DateTime, double)>(days);
        for (var i = 0; i < days; i++)
        {
            var day = fromDay.AddDays(i);
            var df = day.ToUniversalTime();
            var dt = day.AddDays(1).ToUniversalTime();
            var list = new List<(DateTime Start, DateTime End)>();
            foreach (var s in segments)
                if (s.Start < dt && s.End > df) list.Add((s.Start, s.End));
            result.Add((day, StatsService.UnionSeconds(list)));
        }
        return result;
    }

    /// <summary>把片段按本地小时切桶，用于「最活跃时段」。</summary>
    private static double[] BuildHourSeconds(
        List<(DateTime Start, DateTime End, int AppId, string Kind)> segments)
    {
        var hourSec = new double[24];
        foreach (var (s, e, _, _) in segments)
        {
            var cursor = s.ToLocalTime();
            var end = e.ToLocalTime();
            while (cursor < end)
            {
                var hourEnd = cursor.Date.AddHours(cursor.Hour + 1);
                var sliceEnd = end < hourEnd ? end : hourEnd;
                if (sliceEnd <= cursor) break;
                hourSec[cursor.Hour] += (sliceEnd - cursor).TotalSeconds;
                cursor = sliceEnd;
            }
        }
        return hourSec;
    }

    /// <summary>在 UI 线程执行：只做绑定与绘制（Brush / ImageSource 都是 UI 对象）。</summary>
    private void BindSnapshot(Snapshot snapshot, int days)
    {
        TotalText.Text = StatsService.FormatDuration(snapshot.TotalSeconds);
        AvgText.Text = StatsService.FormatDuration(snapshot.TotalSeconds / days);
        DaysText.Text = $"{days} 天";

        var catTotal = snapshot.Categories.Sum(x => x.Seconds);
        Donut.Slices = snapshot.Categories.Select(x => new DonutSlice(x.Category, x.Seconds)).ToList();
        Legend.ItemsSource = snapshot.Categories.Select(x =>
            new LegendItem(x.Category, CategoryPalette.BrushFor(x.Category),
                catTotal > 0 ? $"{x.Seconds / catTotal * 100:0}%" : "0%")).ToList();

        _categoryByApp = snapshot.Apps.ToDictionary(a => a.AppId, a => a.Category);
        var maxSec = snapshot.Apps.Count > 0 ? Math.Max(snapshot.Apps.Max(a => a.TotalSeconds), 1) : 1;
        var rows = snapshot.Apps
            .OrderByDescending(a => a.TotalSeconds)
            .Select(a =>
            {
                snapshot.IconByApp.TryGetValue(a.AppId, out var p);
                return new AppRankItem(a.AppId, a.AppName, StatsService.FormatDuration(a.TotalSeconds),
                    a.TotalSeconds / maxSec * 100, p.ExePath, p.ProcessName, p.IconSource);
            })
            .ToList();
        _allRows = rows;
        UpdateCategoryChip();
        ApplyAppSearch();
        FillIconsAsync(rows);

        BindPurpose(snapshot.Purposes);
        BindReportExtras(snapshot.DayTotals, snapshot.HourSeconds, days, snapshot.TopCategory);
    }

    private void RetryBtn_Click(object sender, RoutedEventArgs e) => RequestQuery();

    private void AppList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var item = FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject);
        if (item?.Content is AppRankItem app)
        {
            // 历史页承载完整的应用管理菜单（原「应用」页的功能都收在这里）
            AppActionMenu.Show(item, app, e.GetPosition(item), full: true);
            e.Handled = true;
        }
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match) return match;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    /// <summary>填充排行图标：缓存命中立即回显；未命中后台并行提取。</summary>
    private void FillIconsAsync(List<AppRankItem> rows)
    {
        _iconLoadCts?.Cancel();
        _iconLoadCts?.Dispose();
        _iconLoadCts = new CancellationTokenSource();
        var token = _iconLoadCts.Token;
        var ui = DispatcherQueue.GetForCurrentThread();
        var pending = new List<AppRankItem>();
        foreach (var row in rows)
        {
            var cached = AppIconService.GetCachedIcon(row.ExePath, row.ProcessName, row.IconSource);
            if (cached is not null) row.Icon = cached;
            else pending.Add(row);
        }
        if (pending.Count == 0) return;

        _ = System.Threading.Tasks.Task.Run(() =>
        {
            try
            {
                Parallel.ForEach(pending, new ParallelOptions
                {
                    MaxDegreeOfParallelism = 2,
                    CancellationToken = token
                }, row =>
                {
                    if (token.IsCancellationRequested) return;
                    var bytes = AppIconService.GetIconBytes(row.ExePath, row.ProcessName, row.IconSource);
                    if (bytes is null) return;
                    var capture = bytes;
                    ui.TryEnqueue(() =>
                    {
                        if (!token.IsCancellationRequested)
                            row.Icon = AppIconService.BytesToImageCached(capture, row.ExePath, row.ProcessName, row.IconSource);
                    });
                });
            }
            catch (OperationCanceledException) { }
        });
    }

    /// <summary>用途分布：数据已在后台查好，这里只做绑定。</summary>
    private void BindPurpose(List<(string Purpose, double TotalSeconds)> purposes)
    {
        var total = purposes.Sum(p => p.TotalSeconds);

        PurposeBar.Children.Clear();
        PurposeBar.ColumnDefinitions.Clear();
        for (var i = 0; i < purposes.Count; i++)
        {
            var (purpose, sec) = purposes[i];
            PurposeBar.ColumnDefinitions.Add(
                new ColumnDefinition { Width = new GridLength(Math.Max(sec, 0.0001), GridUnitType.Star) });

            var cr = new CornerRadius(0);
            if (purposes.Count == 1) cr = new CornerRadius(4);
            else if (i == 0) cr = new CornerRadius(4, 0, 0, 4);
            else if (i == purposes.Count - 1) cr = new CornerRadius(0, 4, 4, 0);

            var seg = new Border
            {
                Background = CategoryPalette.PurposeBrushFor(purpose),
                CornerRadius = cr
            };
            ToolTipService.SetToolTip(seg, $"{PurposeService.Display(purpose)}  {StatsService.FormatDuration(sec)}");
            Grid.SetColumn(seg, i);
            PurposeBar.Children.Add(seg);
        }

        PurposeLegend.ItemsSource = purposes.Select(p =>
            new LegendItem(PurposeService.Display(p.Purpose),
                CategoryPalette.PurposeBrushFor(p.Purpose),
                total > 0 ? $"{p.TotalSeconds / total * 100:0}%" : "0%")).ToList();
        var unknown = purposes.FirstOrDefault(p => p.Purpose == PurposeService.Unknown).TotalSeconds;
        NormalizeBtn.IsEnabled = unknown > 0;
        ToolTipService.SetToolTip(NormalizeBtn, unknown > 0 ? "将未判定记录按应用分类归入推荐用途" : "当前已无未判定记录");
    }

    /// <summary>每日柱状图 + 最活跃时段：分桶计算已在后台完成，这里只做绑定。</summary>
    private void BindReportExtras(List<(DateTime Day, double Seconds)> dayTotals, double[] hourSec,
        int days, string topCategory)
    {
        var maxDay = dayTotals.Count > 0 ? Math.Max(dayTotals.Max(d => d.Seconds), 1) : 1;
        Bars.ItemsSource = dayTotals.Select((d, idx) => new
        {
            Label = days <= 10 || d.Day.Day % 5 == 1 || idx == days - 1 ? d.Day.ToString("dd") : "",
            Hours = d.Seconds > 0 ? StatsService.FormatHours(d.Seconds) + "h" : "",
            BarHeight = d.Seconds / maxDay * 150,
            // 柱子统一用「本区间占用最多的分类」的颜色；原来每根柱子都重排一次应用列表
            Brush = CategoryPalette.BrushFor(topCategory),
            Tip = $"{d.Day:M月d日}  {StatsService.FormatDuration(d.Seconds)}"
        }).ToList();

        var maxHour = hourSec.Length > 0 ? hourSec.Max() : 0;
        var peak = Array.IndexOf(hourSec, maxHour);
        ActiveHoursText.Text = maxHour > 0
            ? $"最活跃 {peak:00}:00 – {peak + 1:00}:00，累计 {StatsService.FormatDuration(maxHour)}"
            : "暂无数据";
    }

    private void NormalizeBtn_Click(object sender, RoutedEventArgs e)
    {
        _db.NormalizePurposes();
        RunQuery();
    }

    /// <summary>点击分类图例：下钻到应用页该分类。</summary>
    private void CategoryLegend_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is LegendItem item)
            App.Current.DrillToCategory(item.Label);
    }

    private void AppSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (IsLoaded) ApplyAppSearch();
    }

    private void ApplyAppSearch()
    {
        IEnumerable<AppRankItem> rows = _allRows;

        // 分类下钻：先按分类收窄，再叠加搜索词
        if (_categoryFilter is { Length: > 0 } category)
            rows = rows.Where(a => _categoryByApp.TryGetValue(a.Id, out var c) && c == category);

        var search = (AppSearchBox.Text ?? "").Trim();
        if (search.Length > 0)
            rows = rows.Where(a => a.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                                   a.ProcessName.Contains(search, StringComparison.OrdinalIgnoreCase));

        AppList.ItemsSource = rows.ToList();
    }

    public void Reload() => RequestQuery();
}
