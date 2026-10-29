using System.IO;

namespace ScreenTime.Services;

/// <summary>
/// 应用显示层：① 识别 Windows 系统 UI 外壳（开始菜单/搜索/通知/锁屏等
/// 短暂获得前台、用户未主动打开的进程）；② 把常见进程名映射为中文显示名。
/// 外壳在采集时"穿透"，不入库、不切断当前真实应用。
/// </summary>
public static class AppDisplayService
{
    /// <summary>
    /// 后台/安装器噪声进程名（不含 .exe，大小写不敏感）。
    /// <para>
    /// 收件箱过滤、采集跳过、数据库自动忽略三处共用这一份名单——
    /// 之前数据库那边单独写死了一小串，导致「收件箱里看不见、历史统计里还在计时」。
    /// </para>
    /// </summary>
    public static readonly IReadOnlySet<string> NoiseProcessNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // 任务计划 / 系统宿主
            "schtasks", "taskeng", "taskhost", "taskhostw", "svchost", "dllhost", "credentialuibroker",
            "backgroundtaskhost", "dasHost", "compattelrunner", "werfault", "wermgr",
            // Hyper-V / WSL / 虚拟化
            "vmwp", "vmcompute", "vmmem", "vmmemwsl",
            // WiX / MSI / 安装器与卸载器
            "wixstdb", "wixstdba", "msiexec", "setup", "install", "installer", "unins000",
            // Windows 更新与维护
            "tiworker", "trustedinstaller", "mousocoreworker", "usoclient", "wuauclt",
            "cleanmgr", "dism", "servicingstack",
            // 音频、字体、错误报告等后台组件
            "audiodg", "fontdrvhost", "sppsvc", "spoolsv", "printfilterpipelinesvc",
            // 安全组件与系统扫描器
            "msmpeng", "nisrv", "securityhealthservice", "smartscreen",
            // 自己的采集器进程：不是用户使用的应用，不该出现在排行里
            "screentime.agent"
        };

    /// <summary>系统临时目录：这些路径下的 exe 是安装包解压出来的瞬时进程。</summary>
    public static readonly string[] NoisePathFragments =
    [
        @"\AppData\Local\Temp\",
        @"\Windows\Temp\"
    ];

    /// <summary>判断是否应从“未分类收件箱”中隐藏后台/安装器噪声。</summary>
    public static bool IsInboxNoise(string processName, string exePath)
    {
        var name = StripExe((processName ?? "").Trim());
        if (NoiseProcessNames.Contains(name)) return true;

        var path = (exePath ?? "").Replace('/', '\\');
        foreach (var fragment in NoisePathFragments)
            if (path.Contains(fragment, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
    /// <summary>
    /// 系统 UI 外壳进程名（不含扩展名，小写）。这些进程短暂获得前台是
    /// Windows 正常行为，不应作为"应用"统计。
    /// 注意：explorer（文件资源管理器）、SystemSettings（设置）等用户
    /// 真正使用的不在此列。
    /// </summary>
    private static readonly HashSet<string> ShellProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        // 开始菜单 / 搜索 / 通知 / Shell 宿主
        "searchhost", "startmenuexperiencehost", "shellexperiencehost", "shellhost",
        "textinputhost", "sihost", "peopleexperiencehost",
        // UWP 窗口宿主（真实 UWP 已通过子窗口解析；单独出现时为瞬态）
        "applicationframehost",
        // 桌面 / 运行时 / 文本服务
        "dwm", "runtimebroker", "ctfmon", "taskhostw", "taskhostw",
        // 锁屏
        "lockapp", "assignedaccesslockapp",
        // 文件选择器 / 打开方式 等模态对话框宿主（用户真正在操作的是背后的应用）
        "pickerhost", "openwith",
        // SystemApps 下的各类对话框/宿主（按路径兜底，这里列常见名）
        "filepicker", "cred dialoghost", "accountscontrolhost", "appresolverux",
        "capturepicker", "pinningconfirmationdialog", "printdialog", "printqueueactioncenter",
        "narratorquickstart", "win32webviewhost", "secureassessmentbrowser",
        "addsuggestedfolderstolibrarydialog", "xg puejectdialog", "aicontext",
        "bioenrollmenthost", "chxsmartscreen", "microsoft.aad.brokerplugin",
        "microsoft.asynctextservice", "microsoft.ecapp", "receiver",
        "oobenetworkcaptiveportal", "oobenetworkconnectionflow", "oobehostapp",
        "logonwebhostproduct", "wpc uapapp", "appactions", "camerabarcodescannerpreview",
        "copilotlibrarybroker", "xbox.tcui", "widgets", "widgetservice",
        "useroobebroker", "systemsettingsbroker",
    };

    /// <summary>
    /// 判断进程是否为系统 UI 外壳。进程名命中集合，或可执行文件位于
    /// SystemApps 目录下，均判定为外壳。
    /// </summary>
    public static bool IsShell(string processName, string exePath)
    {
        var name = StripExe((processName ?? "").Trim());
        if (ShellProcesses.Contains(name)) return true;

        var path = (exePath ?? "").Replace('/', '\\');
        if (path.Contains(@"\SystemApps\", StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    /// <summary>
    /// 瞬时窗口标题：桌面（Program Manager）、解锁过渡窗等。
    /// 它们由 explorer 承载、几秒就消失，算成"使用资源管理器"会污染统计。
    /// </summary>
    private static readonly HashSet<string> TransientTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Program Manager",
        "UnlockingWindow",
        "Windows 默认锁屏界面",
        "Windows Default Lock Screen",
    };

    /// <summary>判断当前前台窗口是否只是系统过渡界面（不是用户真正在用的应用）。</summary>
    public static bool IsTransientWindow(string? title)
        => !string.IsNullOrWhiteSpace(title) && TransientTitles.Contains(title.Trim());

    /// <summary>进程名 → 中文显示名（无映射返回 null，由调用方回退）。</summary>
    public static string? ChineseName(string processName)
    {
        var name = StripExe((processName ?? "").Trim());
        if (ChineseNames.TryGetValue(name, out var zh)) return zh;

        // clion64 / idea64 这类带位数后缀的进程名：去掉后缀再查一次
        var trimmed = TrimBitness(name);
        if (!trimmed.Equals(name, StringComparison.OrdinalIgnoreCase) &&
            ChineseNames.TryGetValue(trimmed, out var zh2))
            return zh2;

        return null;
    }

    /// <summary>
    /// 解析最终显示名：优先中文名映射，其次清洗系统读到的 ProductName。
    /// </summary>
    public static string DisplayName(string processName, string fallback)
        => ChineseName(processName) ?? CleanFallback(processName, fallback);

    /// <summary>
    /// 系统通用 ProductName 完全没有区分度（例如 OpenWith.exe 的
    /// "Microsoft® Windows® Operating System"），直接用会让列表出现一堆同名条目。
    /// </summary>
    private static bool IsGenericProductName(string name)
    {
        if (name.Length == 0) return true;
        return name.Contains("Microsoft® Windows®", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Windows® Operating System", StringComparison.OrdinalIgnoreCase)
            || name.Contains("Microsoft Windows Operating System", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Application", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Setup", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Installer", StringComparison.OrdinalIgnoreCase)
            || name.Equals("Update", StringComparison.OrdinalIgnoreCase);
    }

    private static string CleanFallback(string processName, string fallback)
    {
        var process = StripExe((processName ?? "").Trim());
        var product = (fallback ?? "").Trim();

        if (IsGenericProductName(product)) return PrettyProcessName(process);
        if (product.Equals(process, StringComparison.OrdinalIgnoreCase)) return PrettyProcessName(process);
        return product;
    }

    /// <summary>进程名 → 可读名字：去掉 64/32 位后缀。</summary>
    private static string PrettyProcessName(string process)
    {
        var name = TrimBitness(process);
        return name.Length == 0 ? "未知应用" : name;
    }

    private static string TrimBitness(string name)
    {
        foreach (var suffix in new[] { "64", "32" })
            if (name.EndsWith(suffix, StringComparison.Ordinal) && name.Length > suffix.Length + 2)
                return name[..^suffix.Length];
        return name;
    }

    private static readonly Dictionary<string, string> ChineseNames = new(StringComparer.OrdinalIgnoreCase)
    {
        // —— 浏览器 ——
        ["chrome"] = "谷歌浏览器",
        ["firefox"] = "火狐浏览器",
        ["360se"] = "360安全浏览器",
        ["360chrome"] = "360极速浏览器",
        ["qqbrowser"] = "QQ浏览器",
        ["sogouexplorer"] = "搜狗浏览器",
        ["maxthon"] = "傲游浏览器",
        ["iexplore"] = "Internet Explorer",
        ["chromium"] = "Chromium",
        // Edge / Opera / Brave / Vivaldi / Arc 为品牌名，保留原名（不在此表）

        // —— 系统与桌面 ——
        ["explorer"] = "文件资源管理器",
        ["systemsettings"] = "设置",
        ["control"] = "控制面板",
        ["taskmgr"] = "任务管理器",
        ["cmd"] = "命令提示符",
        ["powershell"] = "Windows PowerShell",
        ["pwsh"] = "PowerShell",
        ["windowsterminal"] = "终端",
        ["conhost"] = "控制台窗口宿主",
        ["notepad"] = "记事本",
        ["wordpad"] = "写字板",
        ["mspaint"] = "画图",
        ["calc"] = "计算器",
        ["snippingtool"] = "截图工具",
        ["regedit"] = "注册表编辑器",
        ["mmc"] = "Microsoft 管理控制台",
        ["cleanmgr"] = "磁盘清理",
        ["magnify"] = "放大镜",
        ["narrator"] = "讲述人",
        ["osk"] = "屏幕键盘",
        ["charmap"] = "字符映射表",
        ["winver"] = "关于 Windows",
        ["dxdiag"] = "DirectX 诊断工具",
        ["eventvwr"] = "事件查看器",
        ["services"] = "服务",
        ["resmon"] = "资源监视器",
        ["perfmon"] = "性能监视器",
        ["msconfig"] = "系统配置",
        ["securityhealth"] = "Windows 安全中心",
        ["winstore"] = "微软商店",
        ["winstore.app"] = "微软商店",
        ["securityhealthsystray"] = "Windows 安全中心",
        ["quickassist"] = "快速助手",
        ["mstsc"] = "远程桌面连接",

        // —— 系统 UI 外壳（已忽略，仅用于在应用页友好显示） ——
        ["searchhost"] = "搜索",
        ["startmenuexperiencehost"] = "开始菜单",
        ["shellexperiencehost"] = "Windows 外壳体验",
        ["shellhost"] = "Shell 主机",
        ["applicationframehost"] = "应用程序框架主机",
        ["textinputhost"] = "文本输入主机",
        ["sihost"] = "Shell 基础结构主机",
        ["lockapp"] = "锁屏",
        ["dwm"] = "桌面窗口管理器",
        ["runtimebroker"] = "运行时代理",
        ["ctfmon"] = "CTF 加载器",

        // —— 常用应用 ——
        ["doubao"] = "豆包",
        ["pythonw"] = "Python（无窗口）",
        ["openwith"] = "打开方式",
        ["setup"] = "安装程序",
        ["processlassolauncher"] = "Process Lasso",
        ["codex-plus-plus"] = "Codex++",
        ["weixin"] = "微信",
        ["wechat"] = "微信",
        ["yuanshen"] = "原神",
        ["genshinimpact"] = "原神",
        ["cloudmusic"] = "网易云音乐",
        ["qqmusic"] = "QQ音乐",
        ["kugou"] = "酷狗音乐",
        ["kuwo"] = "酷我音乐",
        ["bilibili"] = "哔哩哔哩",
        ["youku"] = "优酷",
        ["iqiyi"] = "爱奇艺",
        ["qiyi"] = "爱奇艺",
        ["qqlive"] = "腾讯视频",
        ["tencentvideo"] = "腾讯视频",
        ["mgtv"] = "芒果TV",
        ["douyin"] = "抖音",
        ["kuaishou"] = "快手",
        ["code"] = "Visual Studio Code",
        ["devenv"] = "Visual Studio",
        ["cursor"] = "Cursor",
        ["trae"] = "Trae",
        ["trae cn"] = "Trae CN",
        ["winword"] = "Microsoft Word",
        ["excel"] = "Microsoft Excel",
        ["powerpnt"] = "Microsoft PowerPoint",
        ["outlook"] = "Microsoft Outlook",
        ["onenote"] = "Microsoft OneNote",
        ["wps"] = "WPS",
        ["wpsoffice"] = "WPS Office",
        ["wpspdf"] = "WPS PDF",
        ["acrobat"] = "Adobe Acrobat",
        ["acrord32"] = "Adobe Acrobat Reader",
        ["foxitreader"] = "福昕阅读器",
        ["foxit"] = "福昕阅读器",
        ["wxwork"] = "企业微信",
        ["wemeet"] = "腾讯会议",
        ["tencentmeeting"] = "腾讯会议",
        ["dingtalk"] = "钉钉",
        ["dingtalklauncher"] = "钉钉",
        ["feishu"] = "飞书",
        ["lark"] = "Lark",
        ["teams"] = "Microsoft Teams",
        ["ms-teams"] = "Microsoft Teams",
        ["steam++"] = "Watt Toolkit",
        ["7z"] = "7-Zip",
        ["winrar"] = "WinRAR",
        ["bandizip"] = "Bandizip",
        ["baidunetdisk"] = "百度网盘",
        ["baidunetdiskclient"] = "百度网盘",
        ["thunder"] = "迅雷",
        ["xunlei"] = "迅雷",
        ["idm"] = "Internet Download Manager",
        ["taobao"] = "淘宝",
        ["jd"] = "京东",
        ["jdclient"] = "京东",
        ["pdd"] = "拼多多",
        ["pinduoduo"] = "拼多多",
        ["xianyu"] = "闲鱼",
        ["xiaohongshu"] = "小红书",
        ["weibo"] = "微博",
        ["zhihu"] = "知乎",
        ["meituan"] = "美团",
        ["dianping"] = "大众点评",
        ["eleme"] = "饿了么",
        ["elemeclient"] = "饿了么",
        ["ctrip"] = "携程",
        ["qunar"] = "去哪儿",
        ["amap"] = "高德地图",
        ["fufulauncher"] = "芙芙启动器",
        ["bettergi"] = "BetterGI",
        ["gameviewer"] = "UU远程",
        ["processlasso"] = "Process Lasso",
        ["nvidiaoverlay"] = "NVIDIA Overlay",
        ["ccuwinui"] = "CCU",
        ["hyperdown"] = "HyperDown",
        ["deepseek"] = "DeepSeek",
        ["fufu"] = "芙芙启动器",
        ["steam"] = "Steam",
        ["epicgameslauncher"] = "Epic Games 启动器",
        ["battle.net"] = "战网",
        ["qqprotect"] = "QQ",
        ["qqnt"] = "QQ",
        ["tim"] = "TIM",
        ["aimp"] = "AIMP",
        ["groove"] = "Groove 音乐",
        ["wmplayer"] = "Windows Media Player",
        ["foobar2000"] = "foobar2000",
        ["spotify"] = "Spotify",
        ["itunes"] = "iTunes",
        ["potplayer"] = "PotPlayer",
        ["vlc"] = "VLC",
        ["kmplayer"] = "KMPlayer",
        ["obsidian"] = "Obsidian",
        ["figma"] = "Figma",
        ["photoshop"] = "Adobe Photoshop",
        ["illustrator"] = "Adobe Illustrator",
        ["premiere"] = "Adobe Premiere Pro",
        ["afterfx"] = "Adobe After Effects",
        ["lightroom"] = "Adobe Lightroom",
        ["indesign"] = "Adobe InDesign",
        ["jianying"] = "剪映",
        ["capcut"] = "CapCut",
        ["obs64"] = "OBS Studio",
        ["obs32"] = "OBS Studio",
        ["blender"] = "Blender",
        ["gimp"] = "GIMP",
        ["minecraft"] = "Minecraft",
        ["minecraftlauncher"] = "Minecraft Launcher",
        ["leagueclient"] = "英雄联盟",
        ["leagueoflegends"] = "英雄联盟",
        ["valorant"] = "无畏契约",
        ["csgo"] = "Counter-Strike",
        ["dota2"] = "Dota 2",
        ["overwatch"] = "守望先锋",
        ["pubg"] = "绝地求生",
        ["tslgame"] = "绝地求生",
        ["apex"] = "Apex 英雄",
        ["r5apex"] = "Apex 英雄",
        ["wow"] = "魔兽世界",
        ["diablo"] = "暗黑破坏神",
        ["starrail"] = "崩坏：星穹铁道",
        ["honkaistarrail"] = "崩坏：星穹铁道",
        ["zenlesszonezero"] = "绝区零",
        ["hoyoplay"] = "HoYoPlay",
        ["wegame"] = "WeGame",
        ["discord"] = "Discord",
        ["telegram"] = "Telegram",
        ["whatsapp"] = "WhatsApp",
        ["signal"] = "Signal",
        ["skype"] = "Skype",
        ["kakao"] = "KakaoTalk",
        ["yy"] = "YY",
        ["larkshell"] = "Lark",
        ["slack"] = "Slack",
        ["github"] = "GitHub",
        ["githubdesktop"] = "GitHub Desktop",
        ["docker"] = "Docker",
        ["dockerdesktop"] = "Docker Desktop",
        ["wsl"] = "WSL",
        ["postman"] = "PostMan",
        ["wireshark"] = "Wireshark",
        ["fiddler"] = "Fiddler",
        ["dbeaver"] = "DBeaver",
        ["navicat"] = "Navicat",
        ["putty"] = "PuTTY",
        ["mobaxterm"] = "MobaXterm",
        ["joplin"] = "Joplin",
        ["xmind"] = "XMind",
        ["mindmanager"] = "MindManager",
        ["camscanner"] = "扫描全能王",
        ["notepad++"] = "Notepad++",
        ["sublime_text"] = "Sublime Text",
        ["gvim"] = "Vim",
        ["vim"] = "Vim",
        ["nvim"] = "Vim",
        ["pycharm"] = "PyCharm",
        ["idea64"] = "IntelliJ IDEA",
        ["idea"] = "IntelliJ IDEA",
        ["rider"] = "Rider",
        ["clion"] = "CLion",
        ["webstorm"] = "WebStorm",
        ["goland"] = "GoLand",
        ["datagrip"] = "DataGrip",
        ["phpstorm"] = "PhpStorm",
        ["androidstudio"] = "Android Studio",
        ["eclipse"] = "Eclipse",
        ["anaconda"] = "Anaconda",
        ["jupyter"] = "Jupyter",
        ["spyder"] = "Spyder",
        ["python"] = "Python",
        ["aliyundrive"] = "阿里云盘",
        ["quark"] = "夸克",
        ["haozip"] = "2345好压",
        ["amazon"] = "亚马逊",
        ["aliexpress"] = "速卖通",
        ["alibaba"] = "阿里巴巴",
        ["aliwangwang"] = "阿里旺旺",
        ["wangwang"] = "阿里旺旺",
        ["douban"] = "豆瓣",
        ["tieba"] = "百度贴吧",
        ["momo"] = "陌陌",
        ["tantan"] = "探探",
        ["smzdm"] = "什么值得买",
        ["suning"] = "苏宁易购",
        ["dangdang"] = "当当",
        ["vipshop"] = "唯品会",
        ["mogujie"] = "蘑菇街",
        ["tmall"] = "天猫",
        ["netflix"] = "Netflix",
        ["youtube"] = "YouTube",
        ["twitch"] = "Twitch",
        ["huya"] = "虎牙",
        ["douyu"] = "斗鱼",
        ["listen1"] = "Listen 1",
        ["musicbee"] = "MusicBee",
        ["handbrake"] = "HandBrake",
        ["retroarch"] = "RetroArch",
        ["dolphin"] = "Dolphin",
        ["yuzu"] = "Yuzu",
        ["ryujinx"] = "Ryujinx",
        ["cemu"] = "Cemu",
        ["rpcs3"] = "RPCS3",
        ["ppsspp"] = "PPSSPP",
        ["pcsx2"] = "PCSX2",
        ["epicgames"] = "Epic Games",
        ["goggalaxy"] = "GOG Galaxy",
        ["galaxyclient"] = "GOG Galaxy",
        ["origin"] = "Origin",
        ["eaapp"] = "EA App",
        ["upc"] = "Ubisoft Connect",
        ["upclauncher"] = "Ubisoft Connect",
        ["rockstarservice"] = "Rockstar Games",
        ["gta5"] = "Grand Theft Auto V",
        ["cyberpunk"] = "赛博朋克 2077",
        ["eldenring"] = "艾尔登法环",
        ["starfield"] = "星空",
        ["baldur"] = "博德之门",
        ["fallguys"] = "糖豆人",
        ["monsterhunter"] = "怪物猎人",
        ["rocketleague"] = "火箭联盟",
        ["destiny2"] = "命运 2",
        ["lostark"] = "失落的方舟",
        ["ffxiv"] = "最终幻想 XIV",
        ["wowclassic"] = "魔兽世界 怀旧服",
        ["pathofexile"] = "流放之路",
        ["fc24"] = "EA Sports FC 24",
        ["nba2k"] = "NBA 2K",
        ["horizon"] = "极限竞速 地平线",
        ["wzship"] = "使命召唤 战区",
        ["nvidia"] = "NVIDIA",
        ["msmpeng"] = "Windows Defender",
        ["windowsupdate"] = "Windows 更新",
    };

    private static string StripExe(string s)
        => s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? s[..^4] : s;
}
