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

    /// <summary>未分类应用数量（供导航徽标）。</summary>
    public static int CountUnclassified()
        => App.Current.Database.GetAllApps().Count(a => a.Category == CategoryService.Unclassified);

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
        var apps = App.Current.Database.GetAllApps()
            .Where(a => a.Category == CategoryService.Unclassified)
            .ToList();

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
