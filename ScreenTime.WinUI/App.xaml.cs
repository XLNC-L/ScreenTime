using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using ScreenTime.Data;
using ScreenTime.Services;
using ScreenTime.Themes;

namespace ScreenTime;

public partial class App : Application
{
    /// <summary>已运行实例的唤起通道：第二个实例连上来就说明用户想打开主窗口。</summary>
    private const string ActivatePipeName = "ScreenTime_Activate";

    private MainWindow? _mainWindow;
    private DispatcherQueue? _uiQueue;

    public Database Database { get; private set; } = null!;
    public HistoryImportService HistoryImport { get; private set; } = null!;

    public static new App Current => (App)Application.Current;

    public App()
    {
        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, ev) => WriteError(ev.ExceptionObject);

        // 单实例检查已挪到 Program.Main（WinUI 初始化之前），这里不再处理

        InitializeComponent();

        // 主题：跟随系统
        CategoryPalette.CurrentTheme = RequestedTheme == ApplicationTheme.Light
            ? ElementTheme.Light
            : ElementTheme.Dark;

        Database = new Database(CommandLineValue("--db="));
        Database.Init();
        // 结构升级/数据迁移（重分类、去重、补列）对大库可能较慢，
        // 放后台线程执行，不挡窗口出现。新库 Init 已带全部列，旧库由 Migrate 幂等补齐；
        // 采集由 Agent 进程负责，Agent 心跳 5 秒后才首次写库，与迁移有充分缓冲。
        _ = System.Threading.Tasks.Task.Run(Database.Migrate);
        HistoryImport = new HistoryImportService(Database);
    }

    /// <summary>读取 <c>--key=value</c> 形式的命令行参数（未提供返回 null）。</summary>
    private static string? CommandLineValue(string prefix)
    {
        var arg = Environment.GetCommandLineArgs()
            .FirstOrDefault(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return arg?[prefix.Length..];
    }

    private static bool HasFlag(string flag) =>
        Environment.GetCommandLineArgs().Any(a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));

    /// <summary>通知已运行实例显示主窗口（命名管道，失败即忽略）。</summary>
    internal static void SignalExistingInstance()
    {
        try
        {
            using var client = new NamedPipeClientStream(".", ActivatePipeName, PipeDirection.Out);
            client.Connect(1000);
            client.WriteByte(1);
            client.Flush();
        }
        catch
        {
            // 旧实例没在监听：直接退出即可
        }
    }

    /// <summary>后台监听唤起请求；每次连接后重建管道，等待下一次。</summary>
    private void StartActivationListener()
    {
        // 应用通常由任务计划程序以「最高权限」启动，而用户双击 exe 是非提升的。
        // 默认管道 DACL 只给 SYSTEM / Administrators，非提升进程连接会直接被拒，
        // 表现为「再点一次图标毫无反应」。显式放开当前用户，跨权限也能唤起。
        PipeSecurity? security = null;
        try
        {
            security = new PipeSecurity();
            using var self = WindowsIdentity.GetCurrent();
            if (self.User is { } sid)
                security.AddAccessRule(new PipeAccessRule(
                    sid, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        }
        catch
        {
            security = null;
        }

        System.Threading.Tasks.Task.Run(() =>
        {
            while (true)
            {
                try
                {
                    using var server = security is null
                        ? new NamedPipeServerStream(ActivatePipeName, PipeDirection.In,
                            1, PipeTransmissionMode.Byte, PipeOptions.None)
                        : NamedPipeServerStreamAcl.Create(ActivatePipeName, PipeDirection.In,
                            1, PipeTransmissionMode.Byte, PipeOptions.None, 0, 0, security);

                    server.WaitForConnection();
                    try { server.ReadByte(); } catch { }
                    _uiQueue?.TryEnqueue(ShowMainWindow);
                }
                catch
                {
                    // 管道被占用或实例正在退出：稍等再试，避免空转
                    Thread.Sleep(1000);
                }
            }
        });
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _uiQueue = DispatcherQueue.GetForCurrentThread();

        // 演示数据：只允许配合 --db 使用，杜绝写进真实数据库。
        // 生成完直接退出，方便脚本化验证；之后用同一个 --db 参数启动即可查看。
        if (HasFlag("--seed-demo"))
        {
            if (!Database.IsOverrideLocation)
            {
                WriteError("--seed-demo 必须同时指定 --db=<路径>，已忽略（保护真实数据）。");
            }
            else
            {
                try
                {
                    var started = DateTime.Now;
                    var seeded = DemoSeeder.Seed(Database, 90);
                    DemoSeeder.WriteLog(Database,
                        $"{started:yyyy-MM-dd HH:mm:ss} 写入 {seeded} 条演示数据，耗时 {(DateTime.Now - started).TotalSeconds:0.0}s -> {Database.DbPath}");
                }
                catch (Exception ex)
                {
                    WriteError(ex);
                }
                Environment.Exit(0);
                return;
            }
        }

        // 采集/托盘/自启由 Agent 进程（ScreenTime.Agent.exe）承担，UI 进程不常驻。
        // 启动时确保它在跑：不在则拉起（Agent 有自己单实例，多拉无害）。
        EnsureAgentRunning();
        HistoryImport.Start();
        StartActivationListener();

        _mainWindow = new MainWindow();
        _mainWindow.Activate();
    }

    /// <summary>确保采集器进程在跑（Agent 常驻托盘负责采集；UI 显示完退出不影响采集）。</summary>
    private static void EnsureAgentRunning()
    {
        try
        {
            if (Process.GetProcessesByName("ScreenTime.Agent").Length > 0) return;
            var agentExe = Path.Combine(AppContext.BaseDirectory, "ScreenTime.Agent.exe");
            if (File.Exists(agentExe))
                Process.Start(new ProcessStartInfo(agentExe) { UseShellExecute = true });
        }
        catch { }
    }

    /// <summary>主窗口句柄（供文件/文件夹选择器初始化）。</summary>
    public nint MainWindowHandle
    {
        get
        {
            if (_mainWindow == null) ShowMainWindow();
            return WinRT.Interop.WindowNative.GetWindowHandle(_mainWindow!);
        }
    }

    public void ShowMainWindow()
    {
        if (_mainWindow == null)
        {
            _mainWindow = new MainWindow();
            _mainWindow.Activate();
        }
        else
        {
            _mainWindow.ShowAndActivate();
        }
    }

    /// <summary>下钻到应用页并按分类筛选（转发到主窗口）。</summary>
    public void DrillToCategory(string category)
    {
        if (_mainWindow == null) ShowMainWindow();
        _mainWindow?.DrillToCategory(category);
    }

    /// <summary>设置变更信号：Agent 采集器轮询到版本号变化后热重载（音频/用途/浏览器开关等）。</summary>
    public static void SignalSettingsChanged()
    {
        try { Current.Database.SetSetting("settings_version", DateTime.UtcNow.Ticks.ToString()); } catch { }
    }

    /// <summary>刷新收件箱导航徽标。</summary>
    public void RefreshInboxBadge() => _mainWindow?.RefreshInboxBadge();

    public void ExitApp()
    {
        try { HistoryImport.Dispose(); } catch { }
        Exit();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        WriteError(e.Exception);
        e.Handled = true;
    }

    private static void WriteError(object? ex)
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ScreenTime", "error.log");
            File.AppendAllText(path, $"{DateTime.Now}\n{ex}\n\n");
        }
        catch { }
    }
}
