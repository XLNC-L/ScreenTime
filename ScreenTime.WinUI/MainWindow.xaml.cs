using System.Runtime;
using System.Threading;
using System.Threading.Tasks;
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
    private string? _currentTag;

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

    /// <summary>
    /// 关闭窗口 = 隐藏到托盘，不退出进程。
    /// 同时释放页面可视树与图标缓存，并延迟触发一次积极 GC：
    /// 页面不做导航缓存（见各页面注释），Content=null 后没有其他强引用，
    /// 时间线/排行/图表对象图会被真正回收——这是 WinUI 3 托盘态内存瘦身的关键。
    /// 注意：不能销毁窗口，WinUI 3 最后一个窗口关闭后进程会直接退出。
    /// </summary>
    private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        args.Cancel = true;
        _appWindow.Hide();
        ReleaseUiForTray();
    }

    private void ReleaseUiForTray()
    {
        // WinUI 3 的 Frame 导航栈/缓存可能持有页面实例（已知泄漏点）：
        // 清空 Back/Forward 栈后再卸 Content，确保页面对象图无外部引用、能被 GC 回收。
        // （用内存转储验证过：隐藏后 TodayPage 已从 GC 堆移除——释放链路有效）
        ContentFrame.BackStack.Clear();
        ContentFrame.ForwardStack.Clear();
        ContentFrame.Content = null;
        AppIconService.ClearUiCache();
        _ = Task.Delay(500).ContinueWith(_ =>
        {
            try
            {
                GC.Collect(2, GCCollectionMode.Aggressive, true, true);
                GC.WaitForPendingFinalizers();
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Aggressive, true, true);
                // .NET 堆段申请后不还给 OS（Private 不变），但页面对象已回收，
                // 主动修剪工作集让 Windows 把空闲物理页收回去——任务管理器里的
                // 「内存」列（工作集）会立刻回到接近 silent 底数的水平。
                using var self = System.Diagnostics.Process.GetCurrentProcess();
                SetProcessWorkingSetSize(self.Handle, new IntPtr(-1), new IntPtr(-1));
            }
            catch { }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize);

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

    private void NavigateTo(string tag, bool force = false)
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

        // force：隐藏到托盘时 Content 被置 null 释放页面，但 CurrentSourcePageType 仍指向原页面，
        // 不强制导航会直接跳过，恢复显示时白屏
        if (pageType != null && (force || ContentFrame.CurrentSourcePageType != pageType))
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
        // 隐藏到托盘时释放了可视树（Content=null），重新显示要强制回到当前导航项
        if (ContentFrame.Content == null && _currentTag != null)
            NavigateTo(_currentTag, force: true);
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
