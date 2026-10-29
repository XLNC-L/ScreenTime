namespace ScreenTime.Pages;

/// <summary>
/// 主题切换时由 MainWindow 调用，页面只刷新图表配色、不重建实例。
/// 分类色板（CategoryPalette）是静态的，切换后必须由页面重新生成 Brush 才能生效。
/// </summary>
public interface IThemeRefreshable
{
    /// <summary>用当前主题重绘本页的图表与列表配色。</summary>
    void RefreshTheme();
}
