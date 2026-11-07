namespace ScreenTime.Services;

/// <summary>
/// 用途维度（意图）：回答"我当时想干什么"。
/// 四类用途：工作学习 / 生活事务 / 消遣娱乐 / 社交通讯。
/// 原则"不硬猜"：只有高置信分类（开发/办公、游戏/影音）才自动判，
/// 灰区（浏览/通讯/购物/系统）默认留空（未判定），由明确规则或手动标注覆盖。
/// </summary>
public static class PurposeService
{
    public const string Work = "工作学习";
    public const string Life = "生活事务";
    public const string Consumption = "消遣娱乐";
    public const string Social = "社交通讯";

    /// <summary>未判定（空用途的展示名）。</summary>
    public const string Unknown = "未判定";

    public static readonly string[] Purposes = [Work, Life, Consumption, Social];

    /// <summary>灰区进程：同一应用可用于多种用途，需按会话/标签判断。</summary>
    private static readonly string[] GrayAreaProcesses =
        ["chrome", "msedge", "firefox", "arc", "weixin", "wechat", "qq",
         "bilibili", "bili", "youtube", "doubao", "deepseek"];

    /// <summary>灰区分类：内容形态无法稳定推出意图。</summary>
    public static bool IsGrayCategory(string category)
        => category is "浏览与资讯" or "通讯与协作" or "社交与购物";

    /// <summary>
    /// 分类 → 用途默认映射：高置信分类直接判，其余按最常见的用途兜底，
    /// 避免大量"未判定"；手动标注仍然优先并可随时改。
    /// </summary>
    public static string AutoFromCategory(string category) => category switch
    {
        "开发与创作" or "办公与文档" => Work,
        "游戏" or "影音与娱乐" => Consumption,
        // 通讯与协作以聊天/会议为主，默认社交通讯
        "通讯与协作" => Social,
        // 电商、生活服务、系统维护类默认生活事务
        "社交与购物" or "系统与工具" => Life,
        // 浏览与资讯先按站点判断（见 FromSite），未命中时按浏览行为归生活事务
        "浏览与资讯" => Life,
        _ => "",
    };

    /// <summary>判断是否为灰区应用（进程名或功能分类）。</summary>
    public static bool IsGrayArea(string processName, string category)
    {
        var name = (processName ?? "").ToLowerInvariant();
        if (name.EndsWith(".exe")) name = name[..^4];
        foreach (var g in GrayAreaProcesses)
            if (name == g) return true;
        return IsGrayCategory(category);
    }

    /// <summary>
    /// 解析有效用途：显式值优先，否则仅对高置信分类自动判；灰区返回空串。
    /// </summary>
    public static string Resolve(string? explicitPurpose, string category)
        => !string.IsNullOrEmpty(explicitPurpose)
            ? explicitPurpose!
            : AutoFromCategory(category);

    /// <summary>用途展示名：空串归"未判定"。</summary>
    public static string Display(string? purpose)
        => string.IsNullOrEmpty(purpose) ? Unknown : purpose!;

    /// <summary>
    /// 网站域名 → 用途（按关键字，顺序匹配，先命中先用）。
    /// 未命中的网站返回 fallback（可能为空，即未判定）。
    /// </summary>
    private static readonly (string Key, string Purpose)[] SiteRules =
    [
        // 工作学习
        ("github", Work), ("gitlab", Work), ("gitee", Work), ("stackoverflow", Work),
        ("stackexchange", Work), ("leetcode", Work), ("coursera", Work), ("deepseek", Work),
        ("openai", Work), ("w3school", Work), ("geeksforgeeks", Work), ("juejin", Work),
        ("csdn", Work), ("zhihu", Work), ("kaggle", Work), ("codeforces", Work),
        ("colab", Work), ("notion", Work), ("overleaf", Work), ("docs.", Work),
        ("learn.", Work), ("developer.", Work), ("mozilla", Work), ("edx", Work),
        // 社交通讯
        ("weibo", Social), ("twitter", Social), ("x.com", Social), ("weixin", Social),
        ("telegram", Social), ("discord", Social), ("qq.com", Social),
        ("facebook", Social), ("instagram", Social), ("mastodon", Social),
        // 消遣娱乐
        ("bilibili", Consumption), ("youtube", Consumption), ("netflix", Consumption),
        ("douyin", Consumption), ("twitch", Consumption), ("youku", Consumption),
        ("iqiyi", Consumption), ("spotify", Consumption), ("music.163", Consumption),
        ("huya", Consumption), ("douyu", Consumption),
        // 生活事务
        ("taobao", Life), ("tmall", Life), ("jd.com", Life), ("pinduoduo", Life),
        ("amap", Life), ("12306", Life), ("meituan", Life), ("ele.me", Life),
        ("alipay", Life), ("baidu", Life),
    ];

    /// <summary>按网站域名推断用途；未命中返回 fallback。</summary>
    public static string FromSite(string site, string fallback)
    {
        if (string.IsNullOrEmpty(site)) return fallback;
        var s = site.ToLowerInvariant();
        foreach (var (key, purpose) in SiteRules)
            if (s.Contains(key, StringComparison.Ordinal)) return purpose;
        return fallback;
    }
}
