using System.IO;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using ScreenTime.Services;
using Windows.Storage.Pickers;

namespace ScreenTime.Pages;

public partial class SettingsPage : Page
{
    private bool _loading = true;

    public SettingsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loading = true;
        var db = App.Current.Database;

        AutoStartSwitch.IsOn = AutoStartService.IsEnabled();
        AdminSwitch.IsOn = AutoStartService.IsRunAsAdmin();
        AdminSwitch.IsEnabled = AutoStartSwitch.IsOn;
        AudioSwitch.IsOn = db.GetSetting("audio_enabled", "true") != "false";
        PurposeAskSwitch.IsOn = db.GetSetting("purpose_ask_enabled", "false") == "true";
        BrowserTabSwitch.IsOn = db.GetSetting("browser_tab_enabled", "true") != "false";
        HistoryBackfillSwitch.IsOn = db.GetSetting("history_backfill_enabled", "true") != "false";

        var idle = db.GetSetting("idle_limit", "180");
        foreach (ComboBoxItem item in IdleBox.Items)
            if ((string)item.Tag == idle) IdleBox.SelectedItem = item;

        var retention = db.GetSetting("title_retention", "30");
        foreach (ComboBoxItem item in RetentionBox.Items)
            if ((string)item.Tag == retention) RetentionBox.SelectedItem = item;

        DbPathText.Text = "数据库位置：" + db.DbPath;
        _loading = false;
    }

    private void AutoStartSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var enable = AutoStartSwitch.IsOn;
        var ok = enable ? AutoStartService.Enable(AdminSwitch.IsOn) : AutoStartService.Disable();
        if (!ok)
        {
            ShowInfoBar("设置开机自启失败，请确认有权限创建任务计划。", InfoBarSeverity.Warning);
        }
        RefreshAutoStartState();
    }

    /// <summary>切换「以最高权限运行」：删掉旧任务按新权限重建。</summary>
    private void AdminSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (!AutoStartSwitch.IsOn) return;

        var runAsAdmin = AdminSwitch.IsOn;
        AutoStartService.Disable();
        if (!AutoStartService.Enable(runAsAdmin))
        {
            ShowInfoBar(runAsAdmin
                ? "以最高权限创建任务失败，可能需要在 UAC 提示里允许。已恢复为普通权限。"
                : "重建任务失败，请确认有权限创建任务计划。", InfoBarSeverity.Warning);
        }
        RefreshAutoStartState();
    }

    /// <summary>把两个开关回读成任务的真实状态，避免界面和系统不一致。</summary>
    private void RefreshAutoStartState()
    {
        _loading = true;
        var enabled = AutoStartService.IsEnabled();
        AutoStartSwitch.IsOn = enabled;
        AdminSwitch.IsOn = enabled && AutoStartService.IsRunAsAdmin();
        AdminSwitch.IsEnabled = enabled;
        _loading = false;
    }

    private void AudioSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Current.Database.SetSetting("audio_enabled", AudioSwitch.IsOn ? "true" : "false");
        App.SignalSettingsChanged();
    }

    private void PurposeAskSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Current.Database.SetSetting("purpose_ask_enabled", PurposeAskSwitch.IsOn ? "true" : "false");
        App.SignalSettingsChanged();
    }

    private void BrowserTabSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Current.Database.SetSetting("browser_tab_enabled", BrowserTabSwitch.IsOn ? "true" : "false");
        App.SignalSettingsChanged();
    }

    private void HistoryBackfillSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        App.Current.Database.SetSetting("history_backfill_enabled", HistoryBackfillSwitch.IsOn ? "true" : "false");
        if (HistoryBackfillSwitch.IsOn) App.Current.HistoryImport.Start();
    }

    private void IdleBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || IdleBox.SelectedItem is not ComboBoxItem item) return;
        App.Current.Database.SetSetting("idle_limit", (string)item.Tag);
        App.SignalSettingsChanged();
    }

    private void RetentionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || RetentionBox.SelectedItem is not ComboBoxItem item) return;
        App.Current.Database.SetSetting("title_retention", (string)item.Tag);
    }

    private async void ChangeLocationBtn_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker();
        picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.Current.MainWindowHandle);
        var folder = await picker.PickSingleFolderAsync();
        if (folder == null) return;

        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "ScreenTime",
            Content = $"确定将数据库迁移到以下位置吗？\n\n{folder.Path}\n\n现有数据会被复制，之后将使用新位置。",
            PrimaryButtonText = "迁移",
            CloseButtonText = "取消"
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        var (ok, error) = App.Current.Database.ChangeDataLocation(folder.Path);
        if (ok)
        {
            ShowInfoBar("数据已迁移到新位置。", InfoBarSeverity.Success);
            DbPathText.Text = "数据库位置：" + App.Current.Database.DbPath;
        }
        else
        {
            ShowInfoBar($"迁移失败，已回退到原位置：{error}", InfoBarSeverity.Error);
        }
    }

    private void OpenFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        var folder = Path.GetDirectoryName(App.Current.Database.DbPath);
        if (folder != null)
            Process.Start(new ProcessStartInfo("explorer.exe", folder) { UseShellExecute = true });
    }

    private async void ClearBtn_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "ScreenTime",
            Content = "确定清除全部时长记录吗？应用分类与设置会保留，此操作不可恢复。",
            PrimaryButtonText = "清除",
            CloseButtonText = "取消"
        };
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return;

        App.Current.Database.DeleteSegmentsBefore(DateTime.UtcNow.AddYears(100));
        ShowInfoBar("已清除全部时长记录。", InfoBarSeverity.Success);
    }

    private async void ExportCsvBtn_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker();
        picker.FileTypeChoices.Add("CSV 表格", new List<string> { ".csv" });
        picker.SuggestedFileName = $"ScreenTime-{DateTime.Now:yyyyMMdd-HHmm}";
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.Current.MainWindowHandle);

        var file = await picker.PickSaveFileAsync();
        if (file == null) return;

        try
        {
            var (rows, csv) = App.Current.Database.BuildSegmentsCsv();
            // 带 BOM 写出，Excel 双击打开时中文才不会乱码
            File.WriteAllText(file.Path, csv, new System.Text.UTF8Encoding(true));
            ShowInfoBar($"已导出 {rows} 条记录到 {file.Name}。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowInfoBar($"导出失败：{ex.Message}", InfoBarSeverity.Error);
        }
    }

    private async void ExportJsonBtn_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker();
        picker.FileTypeChoices.Add("JSON 文件", new List<string> { ".json" });
        picker.SuggestedFileName = $"ScreenTime-{DateTime.Now:yyyyMMdd-HHmm}";
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.Current.MainWindowHandle);

        var file = await picker.PickSaveFileAsync();
        if (file == null) return;

        try
        {
            var (rows, json) = App.Current.Database.BuildSegmentsJson();
            // JSON 不写 BOM：部分解析器会把 BOM 当成非法字符
            File.WriteAllText(file.Path, json, new System.Text.UTF8Encoding(false));
            ShowInfoBar($"已导出 {rows} 条记录到 {file.Name}。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowInfoBar($"导出失败：{ex.Message}", InfoBarSeverity.Error);
        }
    }

    private async void BackupBtn_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker();
        picker.FileTypeChoices.Add("SQLite 数据库", new List<string> { ".db" });
        picker.SuggestedFileName = $"screentime-{DateTime.Now:yyyyMMdd-HHmm}";
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.Current.MainWindowHandle);

        var file = await picker.PickSaveFileAsync();
        if (file == null) return;

        try
        {
            App.Current.Database.BackupTo(file.Path);
            ShowInfoBar($"已备份到 {file.Name}。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowInfoBar($"备份失败：{ex.Message}", InfoBarSeverity.Error);
        }
    }

    private void ShowInfoBar(string message, InfoBarSeverity severity)
    {
        Feedback.Message = message;
        Feedback.Severity = severity;
        Feedback.IsOpen = true;
    }
}
