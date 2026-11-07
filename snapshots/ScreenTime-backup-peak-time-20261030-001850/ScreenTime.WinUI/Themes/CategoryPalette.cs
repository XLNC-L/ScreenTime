using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace ScreenTime.Themes;

/// <summary>数据可视化分类色板（浅/深两套），用于环形图、时间线、柱状图。</summary>
public static class CategoryPalette
{
    public static ElementTheme CurrentTheme { get; set; } = ElementTheme.Dark;

    private static SolidColorBrush Make(string hex)
    {
        var c = Color.FromArgb(
            0xFF,
            Convert.ToByte(hex.Substring(1, 2), 16),
            Convert.ToByte(hex.Substring(3, 2), 16),
            Convert.ToByte(hex.Substring(5, 2), 16));
        return new SolidColorBrush(c);
    }

    // ---- 一级分类（8 类）· Light ----
    private static readonly SolidColorBrush LDevCreate = Make("#8764B8");
    private static readonly SolidColorBrush LOfficeDoc = Make("#CA5010");
    private static readonly SolidColorBrush LCommCollab = Make("#00B7C3");
    private static readonly SolidColorBrush LBrowseNews = Make("#0078D4");
    private static readonly SolidColorBrush LMediaEnter = Make("#C42B1C");
    private static readonly SolidColorBrush LGame = Make("#107C10");
    private static readonly SolidColorBrush LSocialShop = Make("#B146C2");
    private static readonly SolidColorBrush LSystemTool = Make("#6E6E6E");
    private static readonly SolidColorBrush LUnclassified = Make("#848691");

    // ---- 一级分类（8 类）· Dark ----
    private static readonly SolidColorBrush DDevCreate = Make("#C4B5FD");
    private static readonly SolidColorBrush DOfficeDoc = Make("#FFB900");
    private static readonly SolidColorBrush DCommCollab = Make("#4DD9E6");
    private static readonly SolidColorBrush DBrowseNews = Make("#4CC2FF");
    private static readonly SolidColorBrush DMediaEnter = Make("#FF99A4");
    private static readonly SolidColorBrush DGame = Make("#6CCB5F");
    private static readonly SolidColorBrush DSocialShop = Make("#E689EE");
    private static readonly SolidColorBrush DSystemTool = Make("#A1A1A1");
    private static readonly SolidColorBrush DUnclassified = Make("#989BA5");

    /// <summary>按分类名与当前主题返回画刷。</summary>
    public static SolidColorBrush BrushFor(string category)
    {
        var dark = CurrentTheme == ElementTheme.Dark;
        return category switch
        {
            "开发与创作" => dark ? DDevCreate : LDevCreate,
            "办公与文档" => dark ? DOfficeDoc : LOfficeDoc,
            "通讯与协作" => dark ? DCommCollab : LCommCollab,
            "浏览与资讯" => dark ? DBrowseNews : LBrowseNews,
            "影音与娱乐" => dark ? DMediaEnter : LMediaEnter,
            "游戏" => dark ? DGame : LGame,
            "社交与购物" => dark ? DSocialShop : LSocialShop,
            "系统与工具" => dark ? DSystemTool : LSystemTool,
            _ => dark ? DUnclassified : LUnclassified,
        };
    }

    // ---- 用途色板（4 类 + 未判定）----
    private static readonly SolidColorBrush LPWork = Make("#0078D4");
    private static readonly SolidColorBrush LPLife = Make("#038387");
    private static readonly SolidColorBrush LPConsume = Make("#CA5010");
    private static readonly SolidColorBrush LPSocial = Make("#8764B8");
    private static readonly SolidColorBrush DPWork = Make("#4CC2FF");
    private static readonly SolidColorBrush DPLife = Make("#4DD9E6");
    private static readonly SolidColorBrush DPConsume = Make("#FFB900");
    private static readonly SolidColorBrush DPSocial = Make("#C4B5FD");

    /// <summary>按用途名与当前主题返回画刷；未判定用中性灰。</summary>
    public static SolidColorBrush PurposeBrushFor(string purpose)
    {
        var dark = CurrentTheme == ElementTheme.Dark;
        return purpose switch
        {
            "工作学习" => dark ? DPWork : LPWork,
            "生活事务" => dark ? DPLife : LPLife,
            "消遣娱乐" => dark ? DPConsume : LPConsume,
            "社交通讯" => dark ? DPSocial : LPSocial,
            _ => dark ? DUnclassified : LUnclassified,
        };
    }
}
