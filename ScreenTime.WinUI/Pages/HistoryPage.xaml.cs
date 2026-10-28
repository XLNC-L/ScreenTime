using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ScreenTime.Data;
using ScreenTime.Services;
using ScreenTime.Themes;
using ScreenTime.UI;

namespace ScreenTime.Pages;

public partial class HistoryPage : Page
{
    private readonly Database _db = App.Current.Database;

    public HistoryPage()
    {
        InitializeComponent();
        Donut.SliceTapped += cat => App.Current.DrillToCategory(cat);
        FromPicker.Date = new DateTimeOffset(DateTime.Today.AddDays(-7));
        ToPicker.Date = new DateTimeOffset(DateTime.Today);
        LeftCol.SizeChanged += (_, e) => AppRankCard.Height = e.NewSize.Height;
        Loaded += (_, _) => RunQuery();
    }

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

    private void RunQueryCore()
    {
        var fromDay = FromPicker.Date?.LocalDateTime.Date ?? DateTime.Today.AddDays(-7);
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

        var iconByApp = _db.GetAllApps().ToDictionary(a => a.Id, a => a.ExePath);
        var maxSec = apps.Count > 0 ? Math.Max(apps.Max(a => a.TotalSeconds), 1) : 1;
        AppList.ItemsSource = apps
            .OrderByDescending(a => a.TotalSeconds)
            .Select(a => new
            {
                Name = a.AppName,
                BarPercent = a.TotalSeconds / maxSec * 100,
                Duration = StatsService.FormatDuration(a.TotalSeconds),
                Icon = SafeIcon(iconByApp.TryGetValue(a.AppId, out var p) ? p : "", a.ProcessName)
            }).ToList();

        LoadPurpose(from, to);
    }

    private static Microsoft.UI.Xaml.Media.ImageSource? SafeIcon(string exePath, string processName)
    {
        try { return AppIconService.GetIcon(exePath, processName); }
        catch { return null; }
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
}
