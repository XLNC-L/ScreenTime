using System.IO;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using ScreenTime.Data;
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
        AboutVersionText.Text = "ScreenTime " + CurrentVersion();
    }

    /// <summary>
    /// 版本号从程序集读，别再写死在 XAML 里——v1.3.5 那次就是忘了同步「关于」页，
    /// 界面显示的版本比实际装的老一版。
    /// </summary>
    private static string CurrentVersion()
    {
        try
        {
            var v = typeof(SettingsPage).Assembly.GetName().Version;
            if (v is null) return "未知";
            // 1.3.6.0 → 1.3.6
            return v.Build > 0 ? $"{v.Major}.{v.Minor}.{v.Build}" : $"{v.Major}.{v.Minor}";
        }
        catch
        {
            return "未知";
        }
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

            var refresh = db.GetSetting("refresh_seconds", "30");
            foreach (ComboBoxItem item in RefreshBox.Items)
                if ((string)item.Tag == refresh) RefreshBox.SelectedItem = item;
            if (RefreshBox.SelectedItem is null) RefreshBox.SelectedIndex = 3;  // 兜底 30 秒

            var detailRetention = db.GetSetting("detail_retention_days", "60");
            foreach (ComboBoxItem item in DetailRetentionBox.Items)
                if ((string)item.Tag == detailRetention) DetailRetentionBox.SelectedItem = item;

            // 目标与提醒：四个下拉共用一套读取
            foreach (var box in new[] { GoalGameBox, GoalVideoBox, GoalSocialBox, LongReminderBox })
            {
                if (box.Tag is not string key) continue;
                var value = db.GetSetting(key, "0");
                foreach (ComboBoxItem item in box.Items)
                    if ((string)item.Tag == value) box.SelectedItem = item;
            }

            DbPathText.Text = "数据库位置：" + db.DbPath;
            // 只回显用户追加的部分：内置名单固定生效，不需要（也不应该）让用户重写
            ExcludeDomainsBox.Text = db.GetSetting(WebPrivacy.SettingKey, "");
        }
        catch
        {
            // 读失败就用控件默认值，不阻塞页面
        }
        finally
        {
            _loading = false;
        }

        await RefreshHealthAsync();

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
        // 左侧「网页」入口跟随这个开关显示/隐藏（历史数据保留）
        App.Current.RefreshBrowserNav();
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

    /// <summary>
    /// 刷新间隔：今日页读这个值决定数字刷新节拍，Agent 读它决定托盘提示多久更新一次。
    /// 必须 SignalSettingsChanged，否则采集器要等下次启动才认。
    /// </summary>
    private async void RefreshBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || RefreshBox.SelectedItem is not ComboBoxItem item) return;
        var value = (string)item.Tag;
        await RunDbAsync(() =>
        {
            App.Current.Database.SetSetting("refresh_seconds", value);
            App.SignalSettingsChanged();
        });
    }

    private async void RetentionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || RetentionBox.SelectedItem is not ComboBoxItem item) return;
        var value = (string)item.Tag;
        await RunDbAsync(() => App.Current.Database.SetSetting("title_retention", value));
    }

    private async void DetailRetentionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DetailRetentionBox.SelectedItem is not ComboBoxItem item) return;
        var value = (string)item.Tag;
        await RunDbAsync(() =>
        {
            App.Current.Database.SetSetting(Database.DetailRetentionKey, value);
            // Agent 每 30 分钟会检查一次，改完立刻刷一次版本号让它尽快生效
            App.SignalSettingsChanged();
        });
        await RefreshHealthAsync();
    }

    /// <summary>目标与连续使用提醒：四个下拉共用，设置键取自各自的 Tag。</summary>
    private async void GoalBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || sender is not ComboBox { SelectedItem: ComboBoxItem item } box ||
            box.Tag is not string key) return;
        var value = (string)item.Tag;
        await RunDbAsync(() =>
        {
            App.Current.Database.SetSetting(key, value);
            App.SignalSettingsChanged();   // 让 Agent 立刻按新阈值判断
        });
    }

    private async void RefreshHealthBtn_Click(object sender, RoutedEventArgs e) => await RefreshHealthAsync();

    private void OpenLogsBtn_Click(object sender, RoutedEventArgs e)
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenTime");
        if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", dir) { UseShellExecute = true });
    }

    /// <summary>读取运行状态：库查询放后台线程，避免与 Agent 的写锁抢 UI 线程。</summary>
    private async System.Threading.Tasks.Task RefreshHealthAsync()
    {
        try
        {
            var health = await System.Threading.Tasks.Task.Run(() => App.Current.Database.GetHealth());
            ApplyHealth(health);
        }
        catch (Exception ex)
        {
            HealthCollectText.Text = "读取失败：" + ex.Message;
        }
    }

    private void ApplyHealth(Database.HealthSnapshot h)
    {
        var fg = ParseUtc(h.LastForegroundUtc);
        var au = ParseUtc(h.LastAudioUtc);
        HealthCollectText.Text = $"前台 {AgoText(fg)} · 音频 {AgoText(au)}";

        var beat = ParseUtc(h.AgentHeartbeat);
        if (beat is null)
            HealthAgentText.Text = "未运行（还没收到心跳）";
        else if ((DateTime.UtcNow - beat.Value).TotalSeconds > 90)
            HealthAgentText.Text = $"未运行（心跳停在 {beat.Value.ToLocalTime():MM-dd HH:mm}）";
        else
        {
            var started = ParseUtc(h.AgentStartedAt);
            var uptime = started is null
                ? ""
                : $"运行 {StatsService.FormatDuration((DateTime.UtcNow - started.Value).TotalSeconds)} · ";
            HealthAgentText.Text = $"{uptime}重启 {h.AgentRestartCount} 次";
        }

        HealthDbText.Text = h.CutoffDay is null
            ? $"{FormatBytes(h.DbBytes)} · 明细 {h.SegmentCount:N0} 条 · 明细永久保留"
            : $"{FormatBytes(h.DbBytes)} · 明细 {h.SegmentCount:N0} 条 · 已折叠 {h.FoldedDays} 天 · 保留 {h.RetentionDays} 天";

        HealthErrorText.Text = BuildErrorText(h);

        HealthWarnText.Text = h.Warnings.Count == 0
            ? "未发现异常"
            : string.Join("\n", h.Warnings);
    }

    /// <summary>
    /// 「最近异常」= 采集错误 + 界面崩溃。
    /// 界面崩溃只写 <c>error.log</c>，用户完全看不到——这里把最后一条贴出来，
    /// 才能回答「刚才是不是闪退过」。
    /// </summary>
    private static string BuildErrorText(Database.HealthSnapshot h)
    {
        var parts = new List<string>();
        if (h.CollectErrorCount > 0)
            parts.Add($"采集 {h.CollectErrorCount} 次 · {h.LastCollectError}");

        if (LastUiCrash() is { } crash)
            parts.Add(crash);

        return parts.Count == 0 ? "无" : string.Join("\n", parts);
    }

    /// <summary>读 error.log 最后一条崩溃记录（只看尾部 8 KB，避免整份读入）。</summary>
    private static string? LastUiCrash()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ScreenTime", "error.log");
            if (!File.Exists(path)) return null;

            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var take = (int)Math.Min(fs.Length, 8192);
            if (take <= 0) return null;
            fs.Seek(-take, SeekOrigin.End);
            var buf = new byte[take];
            var read = fs.Read(buf, 0, take);
            var text = System.Text.Encoding.UTF8.GetString(buf, 0, read);

            // 写入格式："{时间}\n{异常}\n\n"
            var blocks = text.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);
            for (var i = blocks.Length - 1; i >= 0; i--)
            {
                var lines = blocks[i].Split('\n', StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length < 2) continue;
                if (!DateTime.TryParse(lines[0].Trim(), out var when)) continue;

                var first = lines[1].Trim();
                if (first.Length > 90) first = first[..90] + "…";
                return $"界面 {when:MM-dd HH:mm} · {first}";
            }
        }
        catch { }
        return null;
    }

    private static DateTime? ParseUtc(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        try { return Database.ParseIso(raw); } catch { return null; }
    }

    private static string AgoText(DateTime? utc)
    {
        if (utc is null) return "暂无记录";
        var span = DateTime.UtcNow - utc.Value;
        if (span.TotalSeconds < 60) return "刚刚";
        if (span.TotalMinutes < 60) return $"{span.TotalMinutes:0} 分钟前";
        if (span.TotalHours < 24) return $"{span.TotalHours:0} 小时前";
        return utc.Value.ToLocalTime().ToString("MM-dd HH:mm");
    }

    private static string FormatBytes(long bytes)
    {
        if (bytes <= 0) return "0 KB";
        return bytes < 1024 * 1024
            ? $"{bytes / 1024.0:0} KB"
            : $"{bytes / 1024.0 / 1024.0:0.0} MB";
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

    private async void ExportPackageBtn_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker();
        picker.FileTypeChoices.Add("ScreenTime 数据包", new List<string> { ".zip" });
        picker.SuggestedFileName = $"ScreenTime-数据包-{DateTime.Now:yyyyMMdd}";
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.Current.MainWindowHandle);

        var file = await picker.PickSaveFileAsync();
        if (file == null) return;

        try
        {
            var r = await System.Threading.Tasks.Task.Run(
                () => App.Current.Database.ExportPackage(file.Path));
            ShowInfoBar($"已导出 {r.Apps} 个应用、{r.Segments} 条明细到 {file.Name}。", InfoBarSeverity.Success);
        }
        catch (Exception ex)
        {
            ShowInfoBar("导出失败：" + ex.Message, InfoBarSeverity.Error);
        }
    }

    private async void ImportPackageBtn_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        picker.FileTypeFilter.Add(".zip");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.Current.MainWindowHandle);

        var file = await picker.PickSingleFileAsync();
        if (file == null) return;

        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "导入数据包",
            Content = "当前库会先备份到 backups/，然后用数据包覆盖。导入完成后界面会自动刷新，建议再打开一次确认。",
            PrimaryButtonText = "导入",
            CloseButtonText = "取消"
        };
        if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

        // 必须先停 Agent：它持有写连接，直接覆盖文件会被占用并可能留下脏页
        await System.Threading.Tasks.Task.Run(StopAgent);
        var result = await System.Threading.Tasks.Task.Run(
            () => App.Current.Database.ImportPackage(file.Path));
        await System.Threading.Tasks.Task.Run(StartAgent);

        if (result.Ok)
        {
            ShowInfoBar($"已导入 {result.Apps} 个应用、{result.Segments} 条明细。原库备份：{result.BackupPath}",
                InfoBarSeverity.Success);
            await RefreshHealthAsync();
            App.Current.RefreshInboxBadge();
            App.Current.RefreshCurrentPage();
        }
        else
        {
            ShowInfoBar("导入失败：" + result.Error, InfoBarSeverity.Error);
        }
    }

    private static void StopAgent()
    {
        foreach (var p in Process.GetProcessesByName("ScreenTime.Agent"))
        {
            try { p.Kill(); p.WaitForExit(3000); } catch { }
        }
    }

    private static void StartAgent()
    {
        try
        {
            var exe = Path.Combine(AppContext.BaseDirectory, "ScreenTime.Agent.exe");
            if (File.Exists(exe))
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
        }
        catch { }
    }

    private async void SaveExcludeDomainsBtn_Click(object sender, RoutedEventArgs e)
    {
        var value = (ExcludeDomainsBox.Text ?? "").Trim();
        await RunDbAsync(() =>
        {
            App.Current.Database.SetSetting(WebPrivacy.SettingKey, value);
            // 采集器在内存里缓存了名单，推一次版本号让它 10 秒内重新读
            App.SignalSettingsChanged();
        });
        ShowInfoBar("敏感域名名单已保存，采集与展示都会按新名单生效。", InfoBarSeverity.Success);
        await RefreshHealthAsync();
    }

    private async void ClearWebBtn_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "ScreenTime",
            Content = "确定清除全部网页记录吗？所有网页地址与域名统计会被清空，应用使用时长保留。此操作不可恢复。",
            PrimaryButtonText = "清除",
            CloseButtonText = "取消"
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        await RunDbAsync(App.Current.Database.ClearWebData);
        ShowInfoBar("已清除网页记录。", InfoBarSeverity.Success);
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

        App.Current.Database.ClearAllUsage();
        ShowInfoBar("已清除全部时长记录。", InfoBarSeverity.Success);
        await RefreshHealthAsync();
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
