using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using ScreenTime.Services;
using ScreenTime.Themes;
using Windows.Foundation;
using Windows.UI;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace ScreenTime.UI;

public sealed class DonutSlice
{
    public string Name { get; set; } = "";
    public double Value { get; set; }
    public DonutSlice() { }
    public DonutSlice(string name, double value) { Name = name; Value = value; }
}

/// <summary>环形图（WinUI 无内置图表，使用 Path/ArcSegment 自绘，属数据可视化）。</summary>
public sealed class DonutChart : UserControl
{
    public static readonly DependencyProperty SlicesProperty =
        DependencyProperty.Register(nameof(Slices), typeof(List<DonutSlice>), typeof(DonutChart),
            new PropertyMetadata(null, OnSlicesChanged));

    private readonly Canvas _canvas;
    private readonly DispatcherTimer _renderTimer;

    /// <summary>点击扇区（参数：分类名），用于下钻。</summary>
    public event Action<string>? SliceTapped;

    public List<DonutSlice> Slices
    {
        get => (List<DonutSlice>)GetValue(SlicesProperty);
        set => SetValue(SlicesProperty, value);
    }

    public DonutChart()
    {
        _canvas = new Canvas();
        Content = _canvas;
        // 拖动窗口边缘时 SizeChanged 连续触发，直接 Render 会每帧重绘环形图。
        // 防抖 100ms：只在尺寸稳定后重绘一次。
        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _renderTimer.Tick += (_, _) => { _renderTimer.Stop(); Render(); };
        SizeChanged += (_, _) => ScheduleRender();
    }

    private static void OnSlicesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        ((DonutChart)d).ScheduleRender();
    }

    private void ScheduleRender()
    {
        _renderTimer.Stop();
        _renderTimer.Start();
    }

    private void Render()
    {
        _canvas.Children.Clear();
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0 || Slices == null || Slices.Count == 0)
            return;

        var cx = size / 2.0;
        var cy = size / 2.0;
        var outer = size / 2.0 - 2;
        var inner = outer * 0.62;
        var total = Slices.Sum(s => s.Value);
        if (total <= 0) return;

        var angle = -90.0; // 从顶部开始
        foreach (var slice in Slices)
        {
            var sweep = slice.Value / total * 360.0;
            var start = angle;
            var end = angle + sweep;
            var brush = CategoryPalette.BrushFor(slice.Name);
            var tip = $"{slice.Name}  {StatsService.FormatDuration(slice.Value)}";

            if (sweep >= 359.999)
            {
                // 完整圆环：起点终点重合会导致弧退化，拆成两个半环绘制
                AddRing(cx, cy, outer, inner, start, start + 180, brush, tip, slice.Name);
                AddRing(cx, cy, outer, inner, start + 180, start + 360, brush, tip, slice.Name);
            }
            else
            {
                AddRing(cx, cy, outer, inner, start, end, brush, tip, slice.Name);
            }

            angle = end;
        }
    }

    private void AddRing(double cx, double cy, double outer, double inner,
        double startAngle, double endAngle, Brush brush, string tip, string sliceName)
    {
        var path = new Path
        {
            Fill = brush,
            Data = BuildRing(cx, cy, outer, inner, startAngle, endAngle)
        };
        ToolTipService.SetToolTip(path, tip);
        path.Tapped += (_, _) => SliceTapped?.Invoke(sliceName);
        _canvas.Children.Add(path);
    }

    private static Geometry BuildRing(double cx, double cy, double outer, double inner, double startAngle, double endAngle)
    {
        var startRad = startAngle * Math.PI / 180.0;
        var endRad = endAngle * Math.PI / 180.0;

        var outerStart = PointAt(cx, cy, outer, startRad);
        var outerEnd = PointAt(cx, cy, outer, endRad);
        var innerEnd = PointAt(cx, cy, inner, endRad);
        var innerStart = PointAt(cx, cy, inner, startRad);

        var isLargeArc = (endAngle - startAngle) > 180.0;

        var figure = new PathFigure { StartPoint = outerStart, IsClosed = true };
        figure.Segments.Add(new ArcSegment
        {
            Point = outerEnd,
            Size = new Size(outer, outer),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc = isLargeArc
        });
        figure.Segments.Add(new LineSegment { Point = innerEnd });
        figure.Segments.Add(new ArcSegment
        {
            Point = innerStart,
            Size = new Size(inner, inner),
            SweepDirection = SweepDirection.Counterclockwise,
            IsLargeArc = isLargeArc
        });

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    private static Point PointAt(double cx, double cy, double radius, double rad)
    {
        return new Point(cx + radius * Math.Cos(rad), cy + radius * Math.Sin(rad));
    }
}
