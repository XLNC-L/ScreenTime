namespace ScreenTime.Services;

/// <summary>
/// 网页记录的隐私处理：敏感域名排除 + 地址脱敏。
///
/// 采集（UIA 实时读 URL）、回填（浏览器历史库）、查询（网页页展示）三处共用这一份规则，
/// 避免出现「采集时没排除、历史里还能看到」的半吊子情况。
/// 纯字符串逻辑，不碰库也不碰网络，可直接单测。
/// </summary>
public static class WebPrivacy
{
    /// <summary>用户自定义排除名单（逗号分隔）。留空时只用内置名单。</summary>
    public const string SettingKey = "web_excluded_domains";

    /// <summary>内置排除名单：邮件、网银、密码管理器。子串匹配，命中即不记录。</summary>
    public const string BuiltinExcluded =
        "mail.google.com,outlook.live.com,outlook.office.com," +
        "alipay.com,tenpay.com,icbc.com.cn,ccb.com.cn,bankofchina.com,abchina.com,cmbchina.com," +
        "1password.com,lastpass.com,bitwarden.com";

    /// <summary>合并内置名单与用户名单，统一小写、去重。</summary>
    public static string[] ResolveList(string? userValue)
    {
        var list = new List<string>();
        list.AddRange(Split(BuiltinExcluded));
        list.AddRange(Split(userValue));
        return list.Select(x => x.ToLowerInvariant()).Distinct().ToArray();
    }

    /// <summary>名单项按逗号/分号/换行切分，兼容用户从各种地方粘进来的格式。</summary>
    private static IEnumerable<string> Split(string? raw) =>
        (raw ?? "").Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(x => x.Length >= 3);

    /// <summary>地址或域名是否命中排除名单（子串匹配，忽略大小写）。</summary>
    public static bool IsExcluded(string? siteOrUrl, string[] excluded)
    {
        if (string.IsNullOrWhiteSpace(siteOrUrl) || excluded.Length == 0) return false;
        var lower = siteOrUrl.ToLowerInvariant();
        return excluded.Any(e => lower.Contains(e));
    }

    /// <summary>命中名单的站点直接丢弃：返回空串，统计里就不会出现这个域名。</summary>
    public static string FilterSite(string site, string[] excluded) =>
        IsExcluded(site, excluded) ? "" : site;

    /// <summary>去掉 fragment 与常见追踪参数，截断到 200 字符。只用于点击跳转。</summary>
    public static string CleanUrl(string url)
    {
        url = url.Trim();
        var hash = url.IndexOf('#');
        if (hash >= 0) url = url[..hash];

        var q = url.IndexOf('?');
        if (q >= 0)
        {
            var head = url[..q];
            var kept = url[(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Where(p =>
                {
                    var name = p.Split('=')[0].ToLowerInvariant();
                    return !name.StartsWith("utm_") &&
                           name is not ("gclid" or "fbclid" or "msclkid" or "spm_id_from" or "vd_source"
                               or "share_source" or "share_medium");
                })
                .ToList();
            url = kept.Count == 0 ? head : head + "?" + string.Join("&", kept);
        }
        return url.Length > 200 ? url[..200] : url;
    }
}
