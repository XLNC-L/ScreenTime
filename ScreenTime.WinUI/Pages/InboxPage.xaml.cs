using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ScreenTime.Services;

namespace ScreenTime.Pages;

public partial class InboxPage : Page
{
    private bool _loading;

    private record InboxItem(int Id, string Name, string ProcessName, ImageSource? Icon, string[] Categories);

    public InboxPage()
    {
        InitializeComponent();
        BatchBox.ItemsSource = CategoryService.Categories;
        Loaded += (_, _) => LoadInbox();
    }

    /// <summary>收件箱只看最近 30 天：更早的记录不再打扰用户，也让徽标查询保持廉价。</summary>
    private const int LookbackDays = 30;

    /// <summary>前台不足 1 分钟的应用不进收件箱（点开就关的窗口没有分类价值）。</summary>
    private const int MinForegroundSeconds = 60;

    /// <summary>未分类应用数量（供导航徽标）。</summary>
    public static int CountUnclassified()
        => GetInboxApps().Count;

    private static List<(int Id, string Name, string Category, string ProcessName, int Ignored, int MergedInto,
        string ExePath, string DefaultPurpose, int SegmentCount, string IconSource)> GetInboxApps()
    {
        var db = App.Current.Database;
        var apps = db.GetAllApps().Where(a =>
            a.Category == CategoryService.Unclassified &&
            a.Ignored == 0 &&
            a.MergedInto == 0 &&
            !AppDisplayService.IsInboxNoise(a.ProcessName, a.ExePath)).ToList();
        if (apps.Count == 0) return apps;

        // 之前这里是 QueryAppUsage(UnixEpoch, UtcNow)：全库明细读进内存再做区间并集，
        // 46 万行实测 522ms，而它会在 MainWindow 构造时同步执行。
        // 现在只针对这批未分类应用查（走 idx_seg_app），几十毫秒量级。
        var now = DateTime.UtcNow;
        var active = db.QueryAppsWithMinForeground(
            apps.Select(a => a.Id).ToList(), now.AddDays(-LookbackDays), now, MinForegroundSeconds);
        return apps.Where(a => active.Contains(a.Id)).ToList();
    }

    /// <summary>加载入口：失败时在页面内提示并可重试，而不是靠全局 handler 留白。</summary>
    private void LoadInbox()
    {
        try
        {
            LoadInboxCore();
            ErrorBar.IsOpen = false;
        }
        catch (Exception ex)
        {
            ErrorBar.Message = ex.Message;
            ErrorBar.IsOpen = true;
        }
    }

    private void RetryBtn_Click(object sender, RoutedEventArgs e) => LoadInbox();

    private void LoadInboxCore()
    {
        _loading = true;
        var apps = GetInboxApps();

        var items = apps.Select(a => new InboxItem(
            a.Id, a.Name, a.ProcessName, SafeIcon(a.ExePath),
            CategoryService.Categories.ToArray())).ToList();

        InboxList.ItemsSource = items;
        EmptyPanel.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        InboxList.Visibility = items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        BatchBox.SelectedIndex = -1;
        SelectAllBtn.Content = "全选";
        UpdateBatchButton();
        _loading = false;
    }

    private static ImageSource? SafeIcon(string exePath)
    {
        try { return AppIconService.GetIcon(exePath); }
        catch { return null; }
    }

    private void CategoryCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || sender is not ComboBox box || box.Tag is not int id ||
            box.SelectedItem is not string category)
            return;
        App.Current.Database.SetAppCategory(id, category);
        LoadInbox();
        // 通知导航刷新徽标
        App.Current.RefreshInboxBadge();
    }

    // ---- 批量归类 ----

    private void InboxList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateBatchButton();

    private void BatchBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateBatchButton();

    private void UpdateBatchButton()
    {
        var count = InboxList.SelectedItems.Count;
        ApplyBatchBtn.Content = $"应用到选中（{count}）";
        ApplyBatchBtn.IsEnabled = count > 0 && BatchBox.SelectedItem is string;
    }

    private void SelectAllBtn_Click(object sender, RoutedEventArgs e)
    {
        if (InboxList.SelectedItems.Count == InboxList.Items.Count && InboxList.Items.Count > 0)
        {
            InboxList.SelectedItems.Clear();
            SelectAllBtn.Content = "全选";
        }
        else
        {
            InboxList.SelectAll();
            SelectAllBtn.Content = "取消全选";
        }
    }

    private void ApplyBatchBtn_Click(object sender, RoutedEventArgs e)
    {
        if (BatchBox.SelectedItem is not string category) return;

        var ids = InboxList.SelectedItems.OfType<InboxItem>().Select(i => i.Id).ToList();
        if (ids.Count == 0) return;

        foreach (var id in ids)
            App.Current.Database.SetAppCategory(id, category);

        LoadInbox();
        App.Current.RefreshInboxBadge();
    }
}
