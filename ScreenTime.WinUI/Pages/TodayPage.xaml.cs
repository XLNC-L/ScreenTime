using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using ScreenTime.Data;
using ScreenTime.Services;
using ScreenTime.Themes;
using ScreenTime.UI;

namespace ScreenTime.Pages;

public partial class TodayPage : Page
{
    private readonly Database _db = App.Current.Database;
    private readonly DispatcherTimer _timer;
    private List<TimelineRow> _timeline = new();

    public TodayPage()
    {
        InitializeComponent();

        Donut.SliceTapped += cat => App.Current.DrillToCategory(cat);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        _timer.Tick += (_, _) => LoadData();

        Loaded += (_, _) =>
        {
            LoadData();
            _timer.Start();
        };
        Unloaded += (_, _) => _timer.Stop();
        TimelineGrid.SizeChanged += (_, _) => DrawTimeline();
        LeftCol.SizeChanged += (_, e) => AppRankCard.Height = e.NewSize.Height;
    }

    private void LoadData()
    {
        var today = DateTime.Now;
        DateText.Text = today.ToString("yyyy年M月d日 dddd");
        var (from, to) = StatsService.DayRange(today);

        var apps = _db.QueryAppUsage(from, to);
        var segments = _db.QuerySegments(from, to);
        var totalSec = StatsService.UnionSeconds(segments.Select(s => (s.Start, s.End)).ToList());
        var fgSec = apps.Sum(a => a.ForegroundSeconds);
        var audioSec = apps.Sum(a => a.AudioSeconds);

        TotalText.Text = StatsService.FormatDuration(totalSec);
        FgText.Text = StatsService.FormatDuration(fgSec);
        AudioText.Text = StatsService.FormatDuration(audioSec);

        var lastBoot = _db.GetLastBootTime();
        BootText.Text = lastBoot != null
            ? lastBoot.Value.ToLocalTime().ToString("MM-dd HH:mm")
            : "—";

        // 分类占比（过滤无时长的空类别）
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

        // 应用排行（带图标）
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

        _timeline = _db.QueryTimeline(today);
        DrawTimeline();
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

    private void DrawTimeline()
    {
        TimelineGrid.Children.Clear();
        HourLabels.Children.Clear();
        var w = TimelineGrid.ActualWidth;
        if (w <= 0) return;

        // 轨道
        var track = new Border
        {
            Height = 24,
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(4),
            Background = Application.Current.Resources["SubtleFillColorSecondaryBrush"] as Brush
        };
        TimelineGrid.Children.Add(track);

        var localNow = DateTime.Now;

        // 收集前台片段的本地时间区间
        var bars = new List<(DateTime S, DateTime E, TimelineRow Row)>();
        foreach (var row in _timeline.Where(t => t.Kind == "foreground"))
        {
            var s = Database.ParseIso(row.StartUtc).ToLocalTime();
            var e = Database.ParseIso(row.EndUtc).ToLocalTime();
            if (e.Date > s.Date) e = s.Date.AddDays(1);
            if (e <= s) continue;
            bars.Add((s, e, row));
        }

        if (bars.Count == 0) return;

        // 自适应时间范围：只覆盖实际使用时段
        var dataStart = bars.Min(b => b.S);
        var dataEnd = bars.Max(b => b.E);
        if (dataEnd < localNow && localNow.Date == dataStart.Date) dataEnd = localNow;

        // 根据跨度选择刻度间隔
        var rawHours = (dataEnd - dataStart).TotalHours;
        var tickMinutes = rawHours > 8 ? 120 : rawHours > 3 ? 60 : rawHours > 1 ? 30 : 15;

        // 范围对齐到刻度，保证刻度整齐
        var rangeStart = new DateTime(dataStart.Year, dataStart.Month, dataStart.Day,
            dataStart.Hour, dataStart.Minute / tickMinutes * tickMinutes, 0);
        var rangeEnd = rangeStart.AddMinutes(
            Math.Ceiling((dataEnd - rangeStart).TotalMinutes / tickMinutes) * tickMinutes);
        var span = rangeEnd - rangeStart;
        if (span.TotalSeconds <= 0) span = TimeSpan.FromMinutes(tickMinutes);

        double X(DateTime t) =>
            Math.Clamp((t - rangeStart).TotalSeconds / span.TotalSeconds * w, 0, w);

        foreach (var (s, e, row) in bars)
        {
            var x = X(s);
            var width = X(e) - x;
            if (width < 1.5) width = 1.5;

            var bar = new Border
            {
                Width = width,
                Height = 24,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(x, 0, 0, 0),
                Background = CategoryPalette.BrushFor(row.Category),
                CornerRadius = new CornerRadius(4)
            };
            var tip = $"{s:HH:mm:ss}–{e:HH:mm:ss}  {row.AppName}";
            if (!string.IsNullOrEmpty(row.Title)) tip += $"\n{row.Title}";
            ToolTipService.SetToolTip(bar, tip);
            bar.PointerEntered += (_, _) => TimelineDetail.Text = tip.Replace("\n", "    ");
            TimelineGrid.Children.Add(bar);
        }

        // 当前时间游标（仅在范围内）
        if (localNow >= rangeStart && localNow <= rangeEnd)
        {
            var cursor = new Border
            {
                Width = 2,
                Height = 32,
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(Math.Max(X(localNow) - 1, 0), 0, 0, 0),
                Background = Application.Current.Resources["TextFillColorPrimaryBrush"] as Brush,
                Opacity = 0.8
            };
            TimelineGrid.Children.Add(cursor);
        }

        // 动态刻度（标签边界内对齐，避免首尾被截断）
        const double labelW = 30;
        for (var t = rangeStart; t <= rangeEnd; t = t.AddMinutes(tickMinutes))
        {
            var left = Math.Clamp(X(t) - labelW / 2, 0, Math.Max(w - labelW, 0));
            var label = new TextBlock
            {
                Text = $"{t.Hour:00}:{t.Minute:00}",
                Style = Application.Current.Resources["CaptionTextBlockStyle"] as Style,
                Foreground = Application.Current.Resources["TextFillColorSecondaryBrush"] as Brush,
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(left, 0, 0, 0)
            };
            HourLabels.Children.Add(label);
        }
    }
}
