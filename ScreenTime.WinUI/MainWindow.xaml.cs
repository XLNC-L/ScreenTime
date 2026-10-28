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
    private object? _navParam;

    public MainWindow()
    {
        InitializeComponent();

        // 灰区用途标注：订阅采集服务事件
        App.Current.Watcher.PurposePrompt += OnPurposePrompt;

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

    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        // 关闭窗口 = 最小化到托盘，不退出
        args.Cancel = true;
        _appWindow.Hide();
    }

    private void OnThemeChanged(FrameworkElement sender, object args)
    {
        CategoryPalette.CurrentTheme = ContentFrame.ActualTheme;
        if (ContentFrame.CurrentSourcePageType is { } type)
            ContentFrame.Navigate(type, _navParam);
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string tag)
            return;

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

    private void OnPurposePrompt(long segmentId, string processName)
    {
        _purposeSegmentId = segmentId;
        PurposeRadio.SelectedIndex = 0;
        PurposeTip.Subtitle = $"「{processName}」当前会话的用途";
        PurposeTip.IsOpen = true;
    }

    private void PurposeTip_Confirm(TeachingTip sender, object args)
    {
        var labels = PurposeService.Purposes;
        var idx = PurposeRadio.SelectedIndex;
        if (idx >= 0 && idx < labels.Length)
            App.Current.Watcher.ApplyForegroundPurpose(_purposeSegmentId, labels[idx]);
    }

    private void PurposeTip_Cancel(TeachingTip sender, object args)
    {
        // 保持默认用途，不做修改
    }
}
