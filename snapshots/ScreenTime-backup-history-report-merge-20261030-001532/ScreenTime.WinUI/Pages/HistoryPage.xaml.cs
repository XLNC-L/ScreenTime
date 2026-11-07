using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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

    public HistoryPage()
    {
        InitializeComponent();
        // 不做导航缓存（默认 Disabled）：见 TodayPage 注释——缓存会让托盘隐藏时页面释放不掉。
        // 切页重建成本可接受，筛选状态在页面内重建（日期默认近 7 天）。
        Donut.SliceTapped += cat => App.Current.DrillToCategory(cat);
        FromPicker.Date = new DateTimeOffset(DateTime.Today.AddDays(-6));
        ToPicker.Date = new DateTimeOffset(DateTime.Today);
        ReportFrame.Navigate(typeof(ReportsPage));
        LeftCol.SizeChanged += (_, e) => AppRankCard.Height = e.NewSize.Height;
        Loaded += (_, _) => RunQuery();
        Unloaded += (_, _) => _iconLoadCts?.Cancel();
    }

    /// <summary>主题切换：重跑查询重建配色（7 天数据量小），不重建页面。</summary>
    public void RefreshTheme() => RunQuery();

    private void QueryBtn_Click(object sender, RoutedEventArgs e) => RunQuery();

    /// <summary>查询入口：失败时在页面内提示并可重试，而不是靠全局 handler 留白。</summary>
    private void RunQuery()
    {
        try
        {
            RunQueryCore();
            ErrorBar.IsOpen = false;
        }
        catch (Exception ex)
        {
            ErrorBar.Message = ex.Message;
            ErrorBar.IsOpen = true;
        }
    }

    private void RetryBtn_Click(object sender, RoutedEventArgs e) => RunQuery();

    private void AppList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var item = FindAncestor<ListViewItem>(e.OriginalSource as DependencyObject);
        if (item?.Content is AppRankItem app)
        {
            AppActionMenu.Show(item, app, e.GetPosition(item));
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

    private void RunQueryCore()
    {
        // 历史页固定显示“今天 + 前六天”，始终为连续七天，避免跨页后筛选范围漂移。
        var fromDay = FromPicker.Date?.LocalDateTime.Date ?? DateTime.Today.AddDays(-6);
        var toDay = ToPicker.Date?.LocalDateTime.Date ?? DateTime.Today;
        if (toDay < fromDay) (fromDay, toDay) = (toDay, fromDay);

        var from = fromDay.ToUniversalTime();
        var to = toDay.AddDays(1).ToUniversalTime();

        var apps = _db.QueryAppUsage(from, to);
        var segments = _db.QuerySegments(from, to);
        var totalSec = StatsService.UnionSeconds(segments.Select(s => (s.Start, s.End)).ToList());
        var days = (toDay - fromDay).Days + 1;

        TotalText.Text = StatsService.FormatDuration(totalSec);
        AvgText.Text = StatsService.FormatDuration(totalSec / days);
        DaysText.Text = $"{days} 天";

        var byCategory = apps
            .GroupBy(a => a.Category)
            .Select(g => new { Category = g.Key, Seconds = g.Sum(a => a.TotalSeconds) })
            .Where(x => x.Seconds > 0)
            .OrderByDescending(x => x.Seconds)
            .ToList();
        var catTotal = byCategory.Sum(x => x.Seconds);
        Donut.Slices = byCategory.Select(x => new DonutSlice(x.Category, x.Seconds)).ToList();
        Legend.ItemsSource = byCategory.Select(x =>
            new LegendItem(x.Category, CategoryPalette.BrushFor(x.Category),
                catTotal > 0 ? $"{x.Seconds / catTotal * 100:0}%" : "0%")).ToList();

        var iconByApp = _db.GetAllApps().ToDictionary(a => a.Id,
            a => (a.ExePath, a.ProcessName, a.IconSource));
        var maxSec = apps.Count > 0 ? Math.Max(apps.Max(a => a.TotalSeconds), 1) : 1;
        var rows = apps
            .OrderByDescending(a => a.TotalSeconds)
            .Select(a =>
            {
                iconByApp.TryGetValue(a.AppId, out var p);
                return new AppRankItem(a.AppId, a.AppName, StatsService.FormatDuration(a.TotalSeconds),
                    a.TotalSeconds / maxSec * 100, p.ExePath, p.ProcessName, p.IconSource);
            })
            .ToList();
        _allRows = rows;
        ApplyAppSearch();
        FillIconsAsync(rows);

        LoadPurpose(from, to);
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

    private void LoadPurpose(DateTime fromUtc, DateTime toUtc)
    {
        var purposes = _db.QueryPurposeUsage(fromUtc, toUtc);
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
        var search = (AppSearchBox.Text ?? "").Trim();
        var rows = search.Length == 0
            ? _allRows
            : _allRows.Where(a => a.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
                                  a.ProcessName.Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
        AppList.ItemsSource = rows;
    }

    public void Reload() => RunQuery();
}
