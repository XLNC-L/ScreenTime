using Microsoft.UI.Xaml.Media;

namespace ScreenTime.UI;

/// <summary>图例项（分类或用途），可点击下钻。</summary>
public sealed record LegendItem(string Label, Brush Brush, string Percent);
