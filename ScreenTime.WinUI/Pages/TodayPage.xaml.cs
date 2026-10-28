using Microsoft.UI.Dispatching;
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

    /// <summary>秒级节拍：每秒移游标 / 查跨天；累计到 10 秒刷数字、30 秒刷图表。</summary>
    private readonly DispatcherTimer _timer;
    private int _ticks;
    private string _dayKey = "";

    private List<TimelineRow> _timeline = new();

    // 时间线游标状态：全量重建后缓存范围参数，秒级 tick 只平移游标、不重建时间线
    private Border? _cursor;
    private DateTime _tlRangeStart;
    private TimeSpan _tlSpan;
    private double _tlWidth;

    public TodayPage()
    {
        InitializeComponent();

        Donut.SliceTapped += cat => App.Current.DrillToCategory(cat);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => OnTick();

        Loaded += (_, _) =>
        {
            _dayKey = DateTime.Now.ToString("yyyy-MM-dd");
            RefreshNumbers();
            RefreshCharts();
            _timer.Start();
        };
        Unloaded += (_, _) => _timer.Stop();
        TimelineGrid.SizeChanged += (_, _) => DrawTimeline();
    }

    /// <summary>
    /// 刷新节奏拆分：原来每 10 秒全页重建（6 次查询 + 排行 / 环形图 / 时间线全部重画，
    /// 一天几百个时间线 bar 全换一遍），这是「一卡一卡」的主因。现在拆成三级：
    ///   每秒  —— 时间线游标平滑平移 + 跨天检查；
    ///   10 秒 —— 只刷四个时长数字和日期（2 次轻量查询）；
    ///   30 秒 —— 分类占比 / 用途分布 / 应用排行 / 时间线全量重建。
    /// </summary>
    private void OnTick()
    {
        _ticks++;
        UpdateCursor();

        var dayKey = DateTime.Now.ToString("yyyy-MM-dd");
        if (dayKey != _dayKey)
        {
            // 跨天：日期、时间线范围全变，直接全量刷新
            _dayKey = dayKey;
            RefreshNumbers();
            RefreshCharts();
            return;
        }

        if (_ticks % 10 == 0) RefreshNumbers();
        if (_ticks % 30 == 0) RefreshCharts();
    }

    /// <summary>轻量数字刷新：日期 + 四张卡片时长（分钟粒度，秒级变化不展示）。</summary>
    private void RefreshNumbers()
    {
        try
        {
            var today = DateTime.Now;
            DateText.Text = today.ToString("yyyy年M月d日 dddd");
            var (from, to) = StatsService.DayRange(today);

            var segments = _db.QuerySegments(from, to);
            var totalSec = StatsService.UnionSeconds(segments.Select(s => (s.Start, s.End)).ToList());
            var fgSec = StatsService.UnionSeconds(
                segments.Where(s => s.Kind == "foreground").Select(s => (s.Start, s.End)).ToList());

            // 分钟粒度：先按分钟取整再做差，保证「总 = 前台 + 后台」显示上严格相加
            var totalMin = (long)Math.Floor(totalSec / 60.0);
            var fgMin = (long)Math.Floor(fgSec / 60.0);
            var bgMin = Math.Max(0, totalMin - fgMin);

            TotalText.Text = StatsService.FormatDurationMinute(totalMin * 60.0);
            FgText.Text = StatsService.FormatDurationMinute(fgMin * 60.0);
            AudioText.Text = StatsService.FormatDurationMinute(bgMin * 60.0);
            ScreenOnText.Text = StatsService.FormatDurationMinute(_db.GetDisplayOnSeconds(from, to));
        }
        catch
        {
            // 单次刷新失败不打断节拍；30 秒内的全量刷新会再次尝试并显示错误
        }
    }

    /// <summary>全量刷新入口：图表 / 排行 / 时间线。失败时在页面内提示并可重试。</summary>
    private void RefreshCharts()
    {
        try
        {
            RefreshChartsCore();
            ErrorBar.IsOpen = false;
        }
        catch (Exception ex)
        {
            ErrorBar.Message = ex.Message;
            ErrorBar.IsOpen = true;
        }
    }

    private void RetryBtn_Click(object sender, RoutedEventArgs e) => RefreshCharts();

    private void RefreshChartsCore()
    {
        var today = DateTime.Now;
        var (from, to) = StatsService.DayRange(today);
        var apps = _db.QueryAppUsage(from, to);

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

        // 应用排行（带图标）：先显示无图标行，后台提取图标字节后逐个填充
        var iconByApp = _db.GetAllApps().ToDictionary(a => a.Id,
            a => (a.ExePath, a.ProcessName, a.IconSource));
        var maxSec = apps.Count > 0 ? Math.Max(apps.Max(a => a.TotalSeconds), 1) : 1;
        var rows = apps
            .OrderByDescending(a => a.TotalSeconds)
            .Select(a =>
            {
                iconByApp.TryGetValue(a.AppId, out var p);
                return new AppRankItem(a.AppName, StatsService.FormatDuration(a.TotalSeconds),
                    a.TotalSeconds / maxSec * 100, p.ExePath, p.ProcessName, p.IconSource);
            })
            .ToList();
        AppList.ItemsSource = rows;
        FillIconsAsync(rows);

        LoadPurpose(from, to);

        _timeline = _db.QueryTimeline(today);
        DrawTimeline();
    }

    /// <summary>
    /// 填充排行图标：缓存命中的立即回显（排行榜 30 秒重建后图标秒出）；
    /// 未命中的后台并行提取（提取 + PNG 编码是重活，串行几十个会拖好几秒）。
    /// </summary>
    private void FillIconsAsync(List<AppRankItem> rows)
    {
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
            Parallel.ForEach(pending, row =>
            {
                var bytes = AppIconService.GetIconBytes(row.ExePath, row.ProcessName, row.IconSource);
                if (bytes is null) return;
                var capture = bytes;
                ui.TryEnqueue(() => row.Icon =
                    AppIconService.BytesToImageCached(capture, row.ExePath, row.ProcessName, row.IconSource));
            });
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

    private void DrawTimeline()
    {
        TimelineGrid.Children.Clear();
        HourLabels.Children.Clear();
        _cursor = null;
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

        // 当前时间游标（仅在范围内）；缓存范围参数，之后由秒级 tick 只平移位置
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
            _cursor = cursor;
            _tlRangeStart = rangeStart;
            _tlSpan = span;
            _tlWidth = w;
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

    /// <summary>秒级节拍：时间线游标随时间平滑平移，不再等 30 秒全量重建才跳一下。</summary>
    private void UpdateCursor()
    {
        if (_cursor is null) return;
        var now = DateTime.Now;
        if (now < _tlRangeStart || now > _tlRangeStart + _tlSpan)
        {
            _cursor.Visibility = Visibility.Collapsed;
            return;
        }
        _cursor.Visibility = Visibility.Visible;
        var x = Math.Clamp((now - _tlRangeStart).TotalSeconds / _tlSpan.TotalSeconds * _tlWidth, 0, _tlWidth);
        _cursor.Margin = new Thickness(Math.Max(x - 1, 0), 0, 0, 0);
    }
}
