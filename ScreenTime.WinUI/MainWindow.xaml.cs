using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ScreenTime.Pages;
using ScreenTime.Services;
using ScreenTime.Themes;
using WinRT.Interop;

namespace ScreenTime;

public partial class MainWindow : Window
{
    private AppWindow _appWindow;
    private long _purposeSegmentId;
    private string _purposeProcess = "";
    private object? _navParam;
    private string? _currentTag;
    private readonly DispatcherTimer _purposePollTimer;
    private readonly DispatcherTimer _agentCheckTimer;
    /// <summary>用户手动关掉掉线告警后，本次会话不再自动弹；心跳恢复后重置。</summary>
    private bool _agentBarDismissed;

    public MainWindow()
    {
        InitializeComponent();

        // Mica 主背景
        SystemBackdrop = new MicaBackdrop();

        // 自定义标题栏
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        // 获取 AppWindow
        var hWnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);
        _appWindow.Closing += OnClosing;

        // 任务栏/ALT+TAB 窗口图标（unpackaged 应用需显式指定路径）
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
        if (File.Exists(iconPath)) _appWindow.SetIcon(iconPath);

        // 系统深浅色切换时重建当前页面，让分类色板即时生效（无需重启）
        ContentFrame.ActualThemeChanged += OnThemeChanged;

        // 用途标注轮询：Agent 采集器把灰区应用的待标注片段写入 settings.pending_purpose，
        // 本页每 30 秒检查一次并弹出 TeachingTip（Agent 无 UI 线程，无法直接弹窗）。
        _purposePollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        _purposePollTimer.Tick += (_, _) => PollPendingPurpose();
        _purposePollTimer.Start();

        // 采集器健康检查：Agent 退出后托盘会一起消失，界面必须主动说出来，
        // 否则像 11-07 那样静默掉 8 小时也没人发现（心跳由 Agent 每 30 秒写一次）。
        _agentCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
        _agentCheckTimer.Tick += async (_, _) => await CheckAgentHealthAsync();
        _agentCheckTimer.Start();
        _ = CheckAgentHealthAfterDelayAsync();

        // 初始导航（支持 --page=xxx 直达指定页面）
        // 先应用「网页」入口可见性：开关关着时 --page=web 也要落到历史页
        RefreshBrowserNav();
        NavigationViewItem initial = (NavigationViewItem)Nav.MenuItems[0];
        var pageArg = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--page="));
        if (pageArg != null)
        {
            var wanted = pageArg.Substring("--page=".Length);
            // 报告页已并入历史页、应用页已并入历史页右键菜单（v1.2.1 的页面收编），
            // 旧参数继续可用，统一落到历史页
            if (wanted.Equals("reports", StringComparison.OrdinalIgnoreCase) ||
                wanted.Equals("apps", StringComparison.OrdinalIgnoreCase))
                wanted = "history";
            if (wanted.Equals("web", StringComparison.OrdinalIgnoreCase) && !_browserNavEnabled)
                wanted = "history";
            foreach (var mi in Nav.MenuItems.OfType<NavigationViewItem>())
            {
                if (mi.Tag is string t && t == wanted) { initial = mi; break; }
            }
        }
        Nav.SelectedItem = initial;
        RefreshInboxBadge();
    }

    /// <summary>
    /// 关闭窗口 = 退出 UI 进程（采集/托盘由 Agent 常驻，UI 进程不留后台）。
    /// WinUI 3 最后一个窗口销毁后进程直接退出——这正是本架构想要的：
    /// 显示态内存随进程一起释放，采集不中断。
    /// </summary>
    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        // 不取消：让进程正常退出。Agent 托盘仍在，可随时重新唤起 UI。
    }

    private void OnThemeChanged(FrameworkElement sender, object args)
    {
        // 主题切换不再整页重建：只更新色板，让当前页重绘图表配色（快得多，且保留页面状态）
        CategoryPalette.CurrentTheme = ContentFrame.ActualTheme;
        if (ContentFrame.Content is IThemeRefreshable refreshable)
            refreshable.RefreshTheme();
    }

    /// <summary>
    /// 「浏览器标签页统计」开关同时管着左侧「网页」入口：关掉就隐藏入口，
    /// 打开再显示。历史数据一条不删——重新打开后网页页还能看到之前记录。
    /// </summary>
    private bool _browserNavEnabled = true;

    public void RefreshBrowserNav()
    {
        bool enabled;
        try { enabled = App.Current.Database.GetSetting("browser_tab_enabled", "true") != "false"; }
        catch { enabled = _browserNavEnabled; }   // 读库撞锁就沿用上一次状态
        _browserNavEnabled = enabled;

        if (FindNavItem("web") is { } web)
            web.Visibility = enabled ? Visibility.Visible : Visibility.Collapsed;

        // 正停在网页页时把开关关了：入口消失后页面不能继续留着，切回历史页
        if (!enabled && _currentTag == "web" && FindNavItem("history") is { } history)
            Nav.SelectedItem = history;
    }

    private NavigationViewItem? FindNavItem(string tag) =>
        Nav.MenuItems.OfType<NavigationViewItem>()
            .FirstOrDefault(mi => mi.Tag is string t && t == tag);

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string tag)
            return;
        _currentTag = tag;
        NavigateTo(tag);
    }

    private void NavigateTo(string tag)
    {
        var pageType = tag switch
        {
            "today" => typeof(TodayPage),
            "history" => typeof(HistoryPage),
            "web" => typeof(WebPage),
            "inbox" => typeof(InboxPage),
            "settings" => typeof(SettingsPage),
            _ => null
        };

        if (pageType != null && ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType, _navParam);
            _navParam = null;
        }
    }

    /// <summary>刷新收件箱导航徽标数量。查询放到后台线程，避免构造窗口时卡住 XAML 线程。</summary>
    public void RefreshInboxBadge() => _ = RefreshInboxBadgeAsync();

    private bool _badgeRefreshing;

    private async System.Threading.Tasks.Task RefreshInboxBadgeAsync()
    {
        if (_badgeRefreshing) return;
        _badgeRefreshing = true;
        try
        {
            var count = await System.Threading.Tasks.Task.Run(InboxPage.CountUnclassified);
            if (count > 0)
            {
                InboxBadge.Value = count;
                InboxBadge.Visibility = Visibility.Visible;
            }
            else
            {
                InboxBadge.Visibility = Visibility.Collapsed;
            }
        }
        catch
        {
            // 徽标失败不影响主流程
        }
        finally
        {
            _badgeRefreshing = false;
        }
    }

    public void RefreshCurrentPage()
    {
        if (ContentFrame.Content is HistoryPage history) history.Reload();
        else if (ContentFrame.Content is IThemeRefreshable refreshable) refreshable.RefreshTheme();
        RefreshInboxBadge();
    }

    public void ShowAndActivate()
    {
        // Show() 只负责「从隐藏变成可见」；Activate() 内部是一次 SetForegroundWindow，
        // 窗口已经可见但被别的窗口盖住时会被前台锁拒绝（返回 false、不报错），
        // 表现就是「桌面已经有面板了，点托盘还是压在最下面」。
        _appWindow.Show();
        Activate();
        ScreenTime.Native.WindowActivation.ForceForeground(WindowNative.GetWindowHandle(this));
    }

    /// <summary>
    /// 把窗口顶到 Z 序最前。与 <see cref="ShowAndActivate"/> 的区别是不碰显示状态，
    /// 供「新建窗口」路径使用——新进程的窗口是 Agent 拉起来的，不属于前台进程，
    /// 系统同样可能让它开在别的窗口后面。
    /// </summary>
    public void ForceToFront() =>
        ScreenTime.Native.WindowActivation.ForceForeground(WindowNative.GetWindowHandle(this));

    /// <summary>下钻：切到历史页并按其分类筛选应用排行。</summary>
    public void DrillToCategory(string category)
    {
        ShowAndActivate();

        // 已经在历史页：直接换筛选条件，不重建页面
        if (ContentFrame.Content is HistoryPage current)
        {
            current.FilterByCategory(category);
            return;
        }

        // 还没到历史页：把分类作为导航参数带过去，页面 OnNavigatedTo 里消费
        _navParam = category;
        foreach (var mi in Nav.MenuItems.OfType<NavigationViewItem>())
        {
            if (mi.Tag is string t && t == "history")
            {
                if ((Nav.SelectedItem as NavigationViewItem)?.Tag as string != "history")
                    Nav.SelectedItem = mi;
                else
                    NavigateTo("history");
                break;
            }
        }

        if (ContentFrame.Content is HistoryPage created) created.FilterByCategory(category);
    }

    /// <summary>首次检查等 5 秒，给刚启动的 Agent 写心跳的时间。</summary>
    private async System.Threading.Tasks.Task CheckAgentHealthAfterDelayAsync()
    {
        await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(5));
        DispatcherQueue.TryEnqueue(() => _ = CheckAgentHealthAsync());
    }

    /// <summary>
    /// 心跳超过 90 秒没更新就认为采集器掉了（Agent 每 30 秒写一次）。
    /// 命中后顶部 InfoBar 给出「重启采集器」，点一下即可拉起，不必重新开界面。
    /// </summary>
    private async System.Threading.Tasks.Task CheckAgentHealthAsync()
    {
        string? raw;
        try { raw = await System.Threading.Tasks.Task.Run(() => App.Current.Database.GetSetting("agent_heartbeat", "")); }
        catch { return; }

        DateTime? beat = null;
        try { beat = string.IsNullOrWhiteSpace(raw) ? null : ScreenTime.Data.Database.ParseIso(raw); } catch { }

        var stale = beat is null || (DateTime.UtcNow - beat.Value).TotalSeconds > 90;
        if (!stale)
        {
            _agentBarDismissed = false;
            AgentDownBar.IsOpen = false;
            return;
        }
        if (_agentBarDismissed) return;

        AgentDownBar.Message = beat is { } b
            ? $"最后一次心跳 {b.ToLocalTime():HH:mm}，之后的使用不会被记录。"
            : "还没有收到心跳，这段时间的使用不会被记录。";
        AgentDownBar.IsOpen = true;
    }

    private async void AgentRestart_Click(object sender, RoutedEventArgs e)
    {
        AgentDownBar.IsOpen = false;
        _agentBarDismissed = false;
        App.EnsureAgentRunning();
        await System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(5));
        _ = CheckAgentHealthAsync();
    }

    private void AgentBar_Closed(InfoBar sender, InfoBarClosedEventArgs args)
    {
        // 用户自己关掉就不再打扰（心跳恢复后 _agentBarDismissed 会重置）
        _agentBarDismissed = true;
    }

    /// <summary>轮询 Agent 写入的待标注片段（settings.pending_purpose），弹出用途选择。</summary>
    private void PollPendingPurpose()
    {
        // 提醒优先：Agent 写了 pending_reminder 就弹一次，处理完再轮到用途标注
        if (!ReminderTip.IsOpen && TryShowReminder()) return;

        // Tip 已开着就不重复弹，避免打断正在做的选择
        if (PurposeTip.IsOpen) return;
        try
        {
            var pending = App.Current.Database.GetSetting("pending_purpose", "");
            if (string.IsNullOrEmpty(pending)) return;
            var parts = pending.Split('|', 2);
            if (parts.Length != 2 || !long.TryParse(parts[0], out var segmentId)) return;

            _purposeSegmentId = segmentId;
            _purposeProcess = parts[1];
            PurposeRadio.SelectedIndex = 0;
            PurposeTip.Subtitle = $"「{parts[1]}」当前会话的用途";
            PurposeTip.IsOpen = true;
        }
        catch { }
    }

    private void PurposeTip_Confirm(TeachingTip sender, object args)
    {
        var labels = PurposeService.Purposes;
        var idx = PurposeRadio.SelectedIndex;
        if (idx >= 0 && idx < labels.Length)
            App.Current.Database.UpdateSegmentPurpose(_purposeSegmentId, labels[idx]);
        ClearPendingPurpose();
    }

    private void PurposeTip_Cancel(TeachingTip sender, object args)
    {
        // 保持默认用途，不做修改
        ClearPendingPurpose();
    }

    /// <summary>读取 Agent 写的 pending_reminder（long|应用|分钟 或 goal|分类|已用|目标）。</summary>
    private bool TryShowReminder()
    {
        try
        {
            var raw = App.Current.Database.GetSetting("pending_reminder", "");
            if (string.IsNullOrEmpty(raw)) return false;

            var parts = raw.Split('|');
            ReminderTip.Subtitle = parts[0] switch
            {
                "long" when parts.Length >= 3 =>
                    $"「{parts[1]}」已经连续使用 {parts[2]} 分钟，要不要起来活动一下？",
                "goal" when parts.Length >= 4 =>
                    $"{parts[1]}今天已用 {parts[2]} 分钟，超过每日目标 {parts[3]} 分钟。",
                _ => ""
            };
            if (ReminderTip.Subtitle.Length == 0) return false;

            ReminderTip.IsOpen = true;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void ReminderTip_Ok(TeachingTip sender, object args) => ClearReminder();

    private void ReminderTip_Snooze(TeachingTip sender, object args)
    {
        try
        {
            App.Current.Database.SetSetting("reminder_snooze_day", DateTime.Now.ToString("yyyy-MM-dd"));
        }
        catch { }
        ClearReminder();
    }

    private static void ClearReminder()
    {
        try { App.Current.Database.SetSetting("pending_reminder", ""); } catch { }
    }

    private static void ClearPendingPurpose()
    {
        try { App.Current.Database.SetSetting("pending_purpose", ""); } catch { }
    }
}
