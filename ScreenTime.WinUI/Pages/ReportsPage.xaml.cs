using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ScreenTime.Data;
using ScreenTime.Services;
using ScreenTime.Themes;
using ScreenTime.UI;

namespace ScreenTime.Pages;

public partial class ReportsPage : Page
{
    private readonly Database _db = App.Current.Database;
    private bool _monthly = true;
    private DateTime _anchor = DateTime.Now;

    public ReportsPage()
    {
        InitializeComponent();
        Donut.SliceTapped += cat => App.Current.DrillToCategory(cat);
        Loaded += (_, _) => LoadReport();
    }

    private void ModeBtn_Click(object sender, RoutedEventArgs e)
    {
        _monthly = !_monthly;
        ModeBtn.Content = _monthly ? "按月" : "按周";
        LoadReport();
    }

    private void PrevBtn_Click(object sender, RoutedEventArgs e)
    {
        _anchor = _monthly ? _anchor.AddMonths(-1) : _anchor.AddDays(-7);
        LoadReport();
    }

    private void NextBtn_Click(object sender, RoutedEventArgs e)
    {
        _anchor = _monthly ? _anchor.AddMonths(1) : _anchor.AddDays(7);
        LoadReport();
    }

    private (DateTime From, DateTime To, DateTime PrevFrom, DateTime PrevTo, string Label) GetRange()
    {
        if (_monthly)
        {
            var (f, t) = StatsService.MonthRange(_anchor);
            var prevAnchor = _anchor.AddMonths(-1);
            var (pf, pt) = StatsService.MonthRange(prevAnchor);
            return (f, t, pf, pt, $"{_anchor.Year} 年 {_anchor.Month} 月");
        }
        else
        {
            var (f, t) = StatsService.WeekRange(_anchor);
            var prevAnchor = _anchor.AddDays(-7);
            var (pf, pt) = StatsService.WeekRange(prevAnchor);
            var fl = f.ToLocalTime();
            var tl = t.ToLocalTime().AddDays(-1);
            return (f, t, pf, pt, $"{fl:M月d日} – {tl:M月d日}");
        }
    }

    private void LoadReport()
    {
        var (from, to, prevFrom, prevTo, label) = GetRange();
        RangeLabel.Text = label;

        // 当前周期可能还没走完：只统计"已经过去的时间"，
        // 否则当月 28 号的日均会被整月 31 天除低。
        var now = DateTime.UtcNow;
        var effectiveTo = to > now ? now : to;
        if (effectiveTo <= from) effectiveTo = from.AddMinutes(1);
        var elapsed = effectiveTo - from;
        var dayCount = Math.Max(1, (int)Math.Ceiling(elapsed.TotalDays));

        // 环比取上一周期的同等长度区间：半个月不该跟整月比，否则永远是"下降"
        var prevClipTo = prevFrom + elapsed;
        if (prevClipTo > prevTo) prevClipTo = prevTo;

        var apps = _db.QueryAppUsage(from, effectiveTo);
        var segments = _db.QuerySegments(from, effectiveTo);
        var totalSec = StatsService.UnionSeconds(segments.Select(s => (s.Start, s.End)).ToList());

        var prevSegs = _db.QuerySegments(prevFrom, prevClipTo);
        var prevTotal = StatsService.UnionSeconds(prevSegs.Select(s => (s.Start, s.End)).ToList());

        TotalText.Text = StatsService.FormatDuration(totalSec);
        AvgText.Text = StatsService.FormatDuration(totalSec / dayCount);

        if (prevTotal > 0)
        {
            var ratio = (totalSec - prevTotal) / prevTotal * 100;
            DeltaText.Text = $"{ratio:+0.0;-0.0}%";
            // 屏幕时间变多是"值得注意"的信号，用警示色；变少才是正向
            DeltaText.Foreground = (ratio >= 0
                ? Application.Current.Resources["SystemFillColorCautionBrush"]
                : Application.Current.Resources["SystemFillColorSuccessBrush"]) as Brush;
        }
        else
        {
            DeltaText.Text = "无对比";
            DeltaText.Foreground = Application.Current.Resources["TextFillColorSecondaryBrush"] as Brush;
        }

        // 每日柱状（只到已过去的日期，不再画未来空白柱）
        var dayTotals = new List<(DateTime Day, double Seconds)>();
        foreach (var day in StatsService.EachDay(from, effectiveTo))
        {
            var (df, dt) = StatsService.DayRange(day);
            var ds = _db.QuerySegments(df, dt);
            dayTotals.Add((day, StatsService.UnionSeconds(ds.Select(s => (s.Start, s.End)).ToList())));
        }
        var maxDay = dayTotals.Count > 0 ? Math.Max(dayTotals.Max(d => d.Seconds), 1) : 1;
        Bars.ItemsSource = dayTotals.Select((d, idx) =>
        {
            var (df, dt) = StatsService.DayRange(d.Day);
            var leadCat = _db.QueryAppUsage(df, dt)
                .OrderByDescending(a => a.TotalSeconds).FirstOrDefault()?.Category ?? "未分类";
            return new
            {
                Label = dayCount <= 10 || d.Day.Day % 5 == 1 || idx == dayCount - 1
                    ? d.Day.ToString("dd") : "",
                Hours = d.Seconds > 0 ? StatsService.FormatHours(d.Seconds) + "h" : "",
                BarHeight = d.Seconds / maxDay * 150,
                Brush = CategoryPalette.BrushFor(leadCat),
                Tip = $"{d.Day:M月d日}  {StatsService.FormatDuration(d.Seconds)}"
            };
        }).ToList();

        // 分类占比
        var byCategory = apps.GroupBy(a => a.Category)
            .Select(g => new { Category = g.Key, Seconds = g.Sum(a => a.TotalSeconds) })
            .OrderByDescending(x => x.Seconds).ToList();
        var catTotal = byCategory.Sum(x => x.Seconds);
        Donut.Slices = byCategory.Select(x => new DonutSlice(x.Category, x.Seconds)).ToList();
        Legend.ItemsSource = byCategory.Select(x =>
            new LegendItem(x.Category, CategoryPalette.BrushFor(x.Category),
                catTotal > 0 ? $"{x.Seconds / catTotal * 100:0}%" : "0%")).ToList();

        // 最活跃时段：按小时切分累计。
        // 旧逻辑把整条片段算给"中点所在的那个小时"，一条跨 3 小时的片段会
        // 把 3 小时全压进 1 格，热力条严重偏斜。
        var hourSec = new double[24];
        foreach (var (s, e, _, _) in segments)
        {
            var cursor = s.ToLocalTime();
            var endLocal = e.ToLocalTime();
            while (cursor < endLocal)
            {
                var hourEnd = cursor.Date.AddHours(cursor.Hour + 1);
                var sliceEnd = endLocal < hourEnd ? endLocal : hourEnd;
                if (sliceEnd <= cursor) break;
                hourSec[cursor.Hour] += (sliceEnd - cursor).TotalSeconds;
                cursor = sliceEnd;
            }
        }
        // 24 小时热力条：颜色深浅表示该小时的使用强度
        var maxHourSec = hourSec.Max();
        HourHeat.Children.Clear();
        HourTicks.Children.Clear();
        HourHeat.ColumnDefinitions.Clear();
        HourTicks.ColumnDefinitions.Clear();
        for (var h = 0; h < 24; h++)
        {
            HourHeat.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            HourTicks.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var ratio = maxHourSec > 0 ? hourSec[h] / maxHourSec : 0;
            var cell = new Border
            {
                Margin = new Thickness(1),
                CornerRadius = new CornerRadius(3),
                Background = CategoryPalette.BrushFor("浏览与资讯"),
                // 完全没用的小时保留一层极淡底色，与"用了一点"区分开
                Opacity = hourSec[h] <= 0 ? 0.06 : 0.18 + 0.82 * ratio
            };
            ToolTipService.SetToolTip(cell, $"{h:00}:00 – {h + 1:00}:00  {StatsService.FormatDuration(hourSec[h])}");
            Grid.SetColumn(cell, h);
            HourHeat.Children.Add(cell);

            if (h % 3 == 0)
            {
                var tick = new TextBlock { Text = $"{h:00}", FontSize = 10, Opacity = 0.6 };
                Grid.SetColumn(tick, h);
                HourTicks.Children.Add(tick);
            }
        }

        var peak = Array.IndexOf(hourSec, maxHourSec);
        ActiveHoursText.Text = maxHourSec > 0
            ? $"最活跃 {peak:00}:00 – {peak + 1:00}:00，累计 {StatsService.FormatDuration(maxHourSec)}"
            : "暂无数据";

        TopApps.ItemsSource = apps.OrderByDescending(a => a.TotalSeconds).Take(8)
            .Select(a => new { Name = a.AppName, Duration = StatsService.FormatDuration(a.TotalSeconds) })
            .ToList();

        LoadPurpose(from, to);
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

        // 仍有未判定时长时，允许一键按分类补全
        var unknown = purposes.FirstOrDefault(p => p.Purpose == PurposeService.Unknown).TotalSeconds;
        NormalizeBtn.IsEnabled = unknown > 0;
        ToolTipService.SetToolTip(NormalizeBtn, unknown > 0
            ? $"将 {StatsService.FormatDuration(unknown)} 未判定记录按应用分类归入推荐用途"
            : "当前已无未判定记录");
    }

    /// <summary>一键归类：把未判定片段按应用分类写入推荐用途。</summary>
    private async void NormalizeBtn_Click(object sender, RoutedEventArgs e)
    {
        _db.NormalizePurposes();
        LoadReport();

        // 未分类型应用没有推荐用途可推，只能引导去应用页归类
        if (!NormalizeBtn.IsEnabled) return;
        var unclassified = _db.GetAllApps()
            .Count(a => a.Category == CategoryService.Unclassified && a.Ignored == 0);
        if (unclassified == 0) return;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "仍有未判定记录",
            Content = $"还有 {unclassified} 个应用没有分类，无法推断用途。在「应用」页给它们选一次分类即可。",
            CloseButtonText = "知道了"
        };
        await dialog.ShowAsync();
    }

    /// <summary>点击分类图例：下钻到应用页该分类。</summary>
    private void CategoryLegend_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is LegendItem item)
            App.Current.DrillToCategory(item.Label);
    }
}
