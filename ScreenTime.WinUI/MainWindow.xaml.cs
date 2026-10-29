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

        // 初始导航（支持 --page=xxx 直达指定页面）
        NavigationViewItem initial = (NavigationViewItem)Nav.MenuItems[0];
        var pageArg = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith("--page="));
        if (pageArg != null)
        {
            var wanted = pageArg.Substring("--page=".Length);
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
            "reports" => typeof(ReportsPage),
            "apps" => typeof(AppsPage),
            "web" => typeof(WebPage),
            "inbox" => typeof(InboxPage),
            "settings" => typeof(SettingsPage),
            _ => null
        };

        if (pageType != null && ContentFrame.CurrentSourcePageType != pageType)
        {
            _navParam = null;
            ContentFrame.Navigate(pageType);
        }
    }

    /// <summary>刷新收件箱导航徽标数量。</summary>
    public void RefreshInboxBadge()
    {
        var count = InboxPage.CountUnclassified();
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

    public void ShowAndActivate()
    {
        _appWindow.Show();
        Activate();
    }

    /// <summary>下钻：导航到应用页并按分类筛选。</summary>
    public void DrillToCategory(string category)
    {
        ShowAndActivate();
        foreach (var mi in Nav.MenuItems.OfType<NavigationViewItem>())
        {
            if (mi.Tag is string t && t == "apps")
            {
                if ((Nav.SelectedItem as NavigationViewItem)?.Tag as string != "apps")
                    Nav.SelectedItem = mi;
                break;
            }
        }
        _navParam = category;
        ContentFrame.Navigate(typeof(AppsPage), category);
    }

    /// <summary>轮询 Agent 写入的待标注片段（settings.pending_purpose），弹出用途选择。</summary>
    private void PollPendingPurpose()
    {
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

    private static void ClearPendingPurpose()
    {
        try { App.Current.Database.SetSetting("pending_purpose", ""); } catch { }
    }
}
