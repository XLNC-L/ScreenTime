using System.IO;

namespace ScreenTime.Services;

/// <summary>
/// 一级分类（按"内容形态"，不按意图）：稳定、可自动判断。
/// 分类回答"这是什么"，意图（PurposeService）回答"拿来干嘛"。
/// 共 8 类；未命中任何规则的归 <see cref="Unclassified"/>（未分类收件箱）。
/// </summary>
public static class CategoryService
{
    public const string Unclassified = "未分类";

    public static readonly string[] Categories =
    [
        "开发与创作", "办公与文档", "通讯与协作", "浏览与资讯",
        "影音与娱乐", "游戏", "社交与购物", "系统与工具",
    ];

    private static readonly (string Category, string[] Names)[] Rules =
    [
        // 开发与创作：IDE / 终端 / Git / 数据库 / 设计 / 剪辑 / 写作 / AI 编程
        ("开发与创作", [
            // IDE 与编辑器
            "devenv", "code", "cursor", "rider", "pycharm", "idea64", "idea", "clion",
            "webstorm", "goland", "datagrip", "phpstorm", "rubymine", "androidstudio",
            "eclipse", "netbeans", "atom", "sublime_text", "notepad++", "gvim", "vim", "nvim",
            "vscode", "trae", "trae cn", "visualstudio", "visual studio",
            // 终端 / 运行环境
            "windowsterminal", "cmd", "powershell", "pwsh", "conhost",
            "anaconda", "python", "jupyter", "spyder",
            // 版本控制 / 接口 / 容器 / 虚拟化
            "github", "githubdesktop", "gitkraken", "sourcetree", "tortoisegit",
            "postman", "insomnia", "fiddler", "wireshark",
            "docker", "dockerdesktop", "vmware", "virtualbox", "wsl",
            // 数据库
            "dbeaver", "navicat", "heidisql", "mysqlworkbench",
            "putty", "winscp", "filezilla", "mremoteng", "mobaxterm",
            "ssms", "azuredatastudio", "linqpad",
            // 笔记 / 知识库（偏创作）
            "joplin", "obsidian", "deepseek",
            // 设计 / 图像 / 三维 / 视频创作
            "photoshop", "illustrator", "indesign", "premiere", "afterfx",
            "lightroom", "figma", "gimp", "inkscape", "blender",
            "maya", "3dsmax", "autocad", "solidworks", "sketchup",
            "coreldraw", "resolve", "davinci", "obs64", "obs32",
            "jianying", "capcut", "sai", "sai2", "clipstudio",
            "animate", "dreamweaver", "audition", "scratch", "substance",
            "zbrush", "rhino", "catia", "creo", "mastercam",
            "lumion", "twinmotion", "keyshot", "mspaint", "paint.net",
            "krita", "affinity", "shotcut",
            "handbrake", "kdenlive", "filmora", "vegas", "camtasia", "bandicam",
            // 工具链 / AI 编程 / 设备调试
            "node", "npm", "git-bash", "git-cmd", "git-gui", "cc-switch",
            "lm studio", "scrcpy", "vivetoolgui", "appcertui",
        ]),

        // 办公与文档：Office / PDF / 会议 / 通用 AI 助手
        ("办公与文档", [
            "winword", "excel", "powerpnt", "outlook", "onenote", "winproj",
            "wps", "wpscloud", "wpsoffice", "wpspdf",
            "acrobat", "acrord32", "foxitreader", "foxit",
            "notepad", "wordpad", "sumatrapdf", "xodo",
            "wxwork", "wemeet", "tencentmeeting",
            "kingsoft", "camscanner", "xmind", "mindmanager",
            "doubao",
            // 通用 AI 助手 / 文档工具
            "claude", "chatgpt", "copilot", "setlang", "pdfgear", "pdflauncher",
            "appvlp", "spreadsheet compare",
        ]),

        // 通讯与协作：实时通讯 / 团队协作
        ("通讯与协作", [
            "wechat", "weixin", "qq", "tim", "qqprotect", "qqnt", "qqexternal",
            "discord", "telegram", "whatsapp", "line", "signal", "skype", "messenger",
            "kakao", "yy",
            "dingtalk", "dingtalklauncher", "lark", "feishu", "larkshell",
            "teams", "ms-teams", "slack",
        ]),

        // 浏览与资讯：浏览器
        ("浏览与资讯", [
            "msedge", "chrome", "firefox", "opera", "brave", "vivaldi", "arc",
            "360se", "360chrome", "qqbrowser", "sogouexplorer", "maxthon", "iexplore",
            "chromium", "edge", "palemoon", "waterfox",
        ]),

        // 影音与娱乐：视频 / 音乐 / 直播 / 短视频
        ("影音与娱乐", [
            "potplayer", "potplayermini64", "vlc", "kmplayer", "qqplayer",
            "cloudmusic", "qqmusic", "kugou", "kuwo", "spotify", "itunes",
            "qqlive", "qyclient", "youku", "bilibili", "wmplayer",
            "groove", "mpc-hc", "mpc-be", "foobar2000", "aimp",
            "winamp", "iqiyi", "qiyi",
            "tencentvideo", "mgtv", "funshion", "pptv",
            "soundcloud", "applemusic", "musicbee", "audirvana",
            "listen1", "lx-music", "yesplaymusic",
            "netflix", "youtube",
            "douyin", "kuaishou", "twitch", "huya", "douyu",
            "moekoemusic", "folia",
        ]),

        // 游戏：游戏平台与游戏进程
        ("游戏", [
            "steam", "epicgames", "epicgameslauncher", "goggalaxy", "galaxyclient",
            "battle.net", "upc", "upclauncher", "eaapp", "origin", "wegame",
            "genshinimpact", "genshin", "yuanshen", "starrail", "honkaistarrail", "zenlesszonezero",
            "leagueoflegends", "leagueclient", "valorant", "valorant-win64-shipping",
            "csgo", "counter-strike", "minecraft", "minecraftlauncher", "rockstarservice",
            "gta5", "eldenring", "dota2", "overwatch", "pubg", "tslgame", "apex",
            "r5apex", "wzship", "horizon", "fc24", "nba2k",
            "pathofexile", "diablo", "wow", "wowclassic", "ffxiv", "lostark",
            "destiny2", "rocketleague", "fallguys", "monsterhunter", "cyberpunk",
            "baldur", "starfield", "hoyoplay", "xboxgames",
            "cemu", "yuzu", "ryujinx", "dolphin", "rpcs3", "pcsx2", "ppsspp", "retroarch",
            "gameviewer", "fufulauncher", "bettergi",
            "angry birds 2", "ubisoftconnect", "game cheats manager", "xboxpcapp",
        ]),

        // 社交与购物：电商 / 社区 / 生活消费
        ("社交与购物", [
            // 电商
            "taobao", "tmall", "jd", "jdclient", "pdd", "pinduoduo", "xianyu",
            "amazon", "amazonclient", "suning", "dangdang", "vipshop",
            "mogujie", "smzdm", "alibaba", "aliexpress", "wangwang", "aliwangwang",
            // 社区 / 社交网站
            "xiaohongshu", "xhs", "weibo", "momo", "tantan", "tieba", "douban", "zhihu",
            // 生活消费 / 出行
            "meituan", "dianping", "eleme", "elemeclient", "ctrip", "qunar", "amap",
        ]),

        // 系统与工具：系统外壳 / 设置 / 安全 / 下载网盘 / 压缩 / 网络账号工具
        ("系统与工具", [
            "explorer", "taskmgr", "regedit", "mmc", "msconfig", "steam++",
            "systemsettings", "systemsettingsbroker", "searchhost", "startmenuexperiencehost",
            "shellexperiencehost", "lockapp", "windowsupdate", "securityhealth",
            "securityhealthsystray", "msmpeng", "dwm", "snippingtool",
            "calc", "magnify", "narrator", "osk", "charmap", "cleanmgr",
            "resmon", "perfmon", "eventvwr", "diskmgmt", "services", "gpedit",
            "devmgmt", "dxdiag", "winver", "applicationframehost",
            "runtimebroker", "sihost", "ctfmon", "taskhostw", "useroobebroker",
            "widgets", "widgetservice", "quickassist", "rdpclip", "mstsc",
            "mobsync", "screentime", "ccuwinui", "nvidia", "nvidiaoverlay",
            "control", "controlcenter", "processlasso", "winstore", "winstore.app",
            // 下载 / 网盘
            "thunder", "xunlei", "idm", "hyperdown", "baidunetdisk", "baidunetdiskclient",
            "aliyundrive", "quark",
            // 压缩
            "7z", "winrar", "bandizip", "haozip",
            // 压缩 / 系统增强 / 输入法 / 音频驱动 / 网络工具
            "7zfm", "senaryaudioapp", "nahimic3", "deskhider", "eartrumpet",
            "everything", "flyingmouse format", "inphic mouse", "idman",
            "iobitunlocker", "komi-store", "powertoys", "quickclipboard",
            "revoupport", "translucenttb", "uu_launcher", "tubawinui3",
            "wetype_update", "leigod_launcher", "clash-verge", "openspeedy",
            "图吧工具箱2026",
        ]),
    ];

    /// <summary>
    /// 关键字「包含」规则：进程名 / 可执行文件名 / 路径段里出现即命中，
    /// 按顺序先命中先用。放在精确匹配之后，所以精确规则永远优先。
    ///
    /// 这层的意义是**让以后新装的软件也能自动归位**，不必每装一个就往精确名单里加一行。
    /// 顺序有讲究：游戏放在最前（避免 "player" 把游戏启动器吸到影音），
    /// 系统与工具放最后（它是兜底类，关键字最多、最容易误命中）。
    /// 只收长度 >= 4 的关键字，短词（git / id / qq）误命中率太高。
    /// </summary>
    private static readonly (string Category, string[] Keywords)[] KeywordRules =
    [
        ("游戏", [
            "angry birds", "ubisoft", "genshin", "hoyoverse", "mihoyo",
            "steam", "epicgames", "wegame", "battle.net", "cheat", "游戏",
        ]),
        ("开发与创作", [
            "git-", "github", "nodejs", "jetbrains", "android studio",
            "lm studio", "scrcpy", "vivetool", "appcert", "postman", "docker",
        ]),
        ("办公与文档", [
            "pdf", "spreadsheet", "acrobat", "office", "wps",
            "claude", "chatgpt", "copilot", "onenote",
        ]),
        ("通讯与协作", [
            "wechat", "weixin", "dingtalk", "feishu", "lark", "telegram", "discord", "slack",
        ]),
        ("影音与娱乐", [
            "music", "player", "bilibili", "douyin", "video", "kugou", "netease cloud",
        ]),
        ("社交与购物", [
            "taobao", "pinduoduo", "xiaohongshu", "weibo", "zhihu", "jd.com",
        ]),
        ("系统与工具", [
            "audio", "sound", "nahimic", "realtek", "dolby",
            "mouse", "keyboard", "输入法", "wetype", "clipboard", "translucent",
            "powertoys", "toolbox", "工具箱", "unlocker", "cleaner", "optimizer",
            "clash", "v2ray", "proxy", "vpn", "加速器", "accelerator",
            "everything", "autoruns", "7-zip", "winrar", "bandizip",
            "download", "idman", "thunder", "uninstaller", "tweak",
        ]),
    ];

    /// <summary>在给定文本里按关键字表找分类，未命中返回 null。</summary>
    private static string? MatchKeywords(string haystack)
    {
        if (string.IsNullOrEmpty(haystack)) return null;
        foreach (var (category, keywords) in KeywordRules)
            foreach (var k in keywords)
                if (haystack.Contains(k, StringComparison.Ordinal))
                    return category;
        return null;
    }

    public static string Classify(string processName, string exePath, string company, string title)
    {
        var nameNoExt = StripExe((processName ?? "").ToLowerInvariant());
        var path = (exePath ?? "").ToLowerInvariant();
        var fileName = StripExe(Path.GetFileName(path));
        var pathSegments = path.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);

        // 1) 进程名精确匹配
        foreach (var (category, names) in Rules)
            foreach (var n in names)
                if (nameNoExt == n)
                    return category;

        // 2) 可执行文件名精确匹配
        foreach (var (category, names) in Rules)
            foreach (var n in names)
                if (fileName == n)
                    return category;

        // 3) 关键字包含（进程名 / 文件名）：覆盖没进精确名单的新软件
        var byName = MatchKeywords(nameNoExt + " " + fileName);
        if (byName != null) return byName;

        // 4) 路径段匹配：仅对长度 >3 的关键字做段内包含，避免短关键字（wt/et/nx）误命中
        foreach (var (category, names) in Rules)
        {
            foreach (var n in names)
            {
                if (n.Length <= 3) continue;
                foreach (var seg in pathSegments)
                {
                    var segNoExt = StripExe(seg);
                    if (segNoExt.Contains(n))
                        return category;
                }
            }
        }

        // 5) 关键字包含（安装目录）：`D:\游戏\原神\launcher.exe` 这类靠目录名也能认出来
        var byPath = MatchKeywords(string.Join(" ", pathSegments));
        if (byPath != null) return byPath;

        // 6) 厂商兜底（仅对指向明确的厂商）
        var co = (company ?? "").ToLowerInvariant();
        if (co.Contains("adobe")) return "开发与创作";
        if (co.Contains("autodesk")) return "开发与创作";
        if (co.Contains("valve")) return "游戏";
        if (co.Contains("epic games")) return "游戏";
        if (co.Contains("rovio")) return "游戏";
        if (co.Contains("ubisoft")) return "游戏";
        if (co.Contains("nvidia")) return "系统与工具";
        if (co.Contains("a-volute") || co.Contains("senary")) return "系统与工具";
        if (co.Contains("anthropic") || co.Contains("openai")) return "办公与文档";
        if (co.Contains("mihoyo") || co.Contains("hoyoverse")) return "游戏";
        if (co.Contains("microsoft")) return "系统与工具";

        return Unclassified;
    }

    /// <summary>旧分类名 → 新分类名（用于历史数据迁移，全局生效）。</summary>
    public static string MapLegacy(string old) => old switch
    {
        "开发" or "设计创作" => "开发与创作",
        "办公" => "办公与文档",
        "社交" => "通讯与协作",
        "浏览器" => "浏览与资讯",
        "影音" => "影音与娱乐",
        "游戏" => "游戏",
        "系统" => "系统与工具",
        "其他" => Unclassified,
        _ => old,
    };

    private static string StripExe(string s)
        => s.EndsWith(".exe") ? s[..^4] : s;
}
