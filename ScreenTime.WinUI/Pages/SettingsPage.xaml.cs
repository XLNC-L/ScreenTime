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

    /// <summary>
    /// 数据库写操作放后台：UI 与 Agent 共享同一个库，在 UI 线程同步写时
    /// 一旦撞上 Agent 的心跳写就会原地等锁（busy_timeout=3s），表现为「点了没反应」。
    /// </summary>
    private static async System.Threading.Tasks.Task RunDbAsync(Action action)
    {
        try { await System.Threading.Tasks.Task.Run(action); } catch { }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // _loading 必须用 try/finally 兜住：读设置撞上 Agent 的写锁会抛 SQLITE_BUSY，
        // 一旦 _loading 卡在 true，页面上所有开关都会变成「点了没反应」。
        _loading = true;
        try
        {
            var db = App.Current.Database;

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
        }
        catch
        {
            // 读失败就用控件默认值，不阻塞页面
        }
        finally
        {
            _loading = false;
        }

        // 自启状态要读任务文件/schtasks（可能几百毫秒），放后台
        try
        {
            var state = await System.Threading.Tasks.Task.Run(AutoStartService.GetState);
            _loading = true;
            AutoStartSwitch.IsOn = state.Enabled;
            AdminSwitch.IsOn = state.RunAsAdmin;
            AdminSwitch.IsEnabled = state.Enabled;
        }
        catch
        {
        }
        finally
        {
            _loading = false;
        }
    }

    private async void AutoStartSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var enable = AutoStartSwitch.IsOn;
        var runAsAdmin = AdminSwitch.IsOn;

        var ok = await System.Threading.Tasks.Task.Run(() =>
            enable ? AutoStartService.Enable(runAsAdmin) : AutoStartService.Disable());

        if (ok)
        {
            // 让 Agent 知道这是用户的主动选择：关掉后不再自动把任务建回来。
            await RunDbAsync(() =>
                App.Current.Database.SetSetting("autostart_disabled", enable ? "0" : "1"));
        }
        else
        {
            ShowInfoBar("设置开机自启失败，请确认有权限创建任务计划。", InfoBarSeverity.Warning);
        }
        RefreshAutoStartState();
    }

    /// <summary>切换「以最高权限运行」：删掉旧任务按新权限重建。</summary>
    private async void AdminSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        if (!AutoStartSwitch.IsOn) return;

        var runAsAdmin = AdminSwitch.IsOn;
        var ok = await System.Threading.Tasks.Task.Run(() =>
        {
            AutoStartService.Disable();
            return AutoStartService.Enable(runAsAdmin);
        });

        if (!ok)
        {
            ShowInfoBar(runAsAdmin
                ? "以最高权限创建任务失败，可能需要在 UAC 提示里允许。已恢复为普通权限。"
                : "重建任务失败，请确认有权限创建任务计划。", InfoBarSeverity.Warning);
        }
        else
        {
            await RunDbAsync(() => App.Current.Database.SetSetting("autostart_disabled", "0"));
        }
        RefreshAutoStartState();
    }

    /// <summary>把两个开关回读成任务的真实状态，避免界面和系统不一致。</summary>
    private async void RefreshAutoStartState()
    {
        try
        {
            var state = await System.Threading.Tasks.Task.Run(AutoStartService.GetState);
            _loading = true;
            AutoStartSwitch.IsOn = state.Enabled;
            AdminSwitch.IsOn = state.RunAsAdmin;
            AdminSwitch.IsEnabled = state.Enabled;
        }
        catch
        {
        }
        finally
        {
            _loading = false;
        }
    }

    private async void AudioSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var value = AudioSwitch.IsOn ? "true" : "false";
        await RunDbAsync(() =>
        {
            App.Current.Database.SetSetting("audio_enabled", value);
            App.SignalSettingsChanged();
        });
    }

    private async void PurposeAskSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var value = PurposeAskSwitch.IsOn ? "true" : "false";
        await RunDbAsync(() =>
        {
            App.Current.Database.SetSetting("purpose_ask_enabled", value);
            App.SignalSettingsChanged();
        });
    }

    private async void BrowserTabSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var value = BrowserTabSwitch.IsOn ? "true" : "false";
        await RunDbAsync(() =>
        {
            App.Current.Database.SetSetting("browser_tab_enabled", value);
            App.SignalSettingsChanged();
        });
    }

    private async void HistoryBackfillSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var value = HistoryBackfillSwitch.IsOn ? "true" : "false";
        // 回填跑在 Agent 进程里：推一次版本号，让它 10 秒内重新读设置（开着就立刻跑一轮）
        await RunDbAsync(() =>
        {
            App.Current.Database.SetSetting("history_backfill_enabled", value);
            App.SignalSettingsChanged();
        });
    }

    private async void IdleBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || IdleBox.SelectedItem is not ComboBoxItem item) return;
        var value = (string)item.Tag;
        await RunDbAsync(() =>
        {
            App.Current.Database.SetSetting("idle_limit", value);
            App.SignalSettingsChanged();
        });
    }

    private async void RetentionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || RetentionBox.SelectedItem is not ComboBoxItem item) return;
        var value = (string)item.Tag;
        await RunDbAsync(() => App.Current.Database.SetSetting("title_retention", value));
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
