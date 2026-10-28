using System.Diagnostics;
using Microsoft.UI.Xaml;
using ScreenTime.Data;
using ScreenTime.Native;

namespace ScreenTime.Services;

/// <summary>一个正在计时的开放片段。</summary>
internal sealed class OpenSegment
{
    public long RowId;
    public required int AppId;
    public required string Kind;
    public required DateTime StartUtc;
    public DateTime EndUtc;
    public string Title = "";
    public string Purpose = "";
    public string ProcessName = "";
    public string Site = "";
    public bool PurposeAsked;
}

/// <summary>
/// 核心采集服务：前台通道（事件 + 5 秒心跳）+ 音频通道（峰值检测），
/// 叠加 idle / 锁屏 / 睡眠判定。
/// </summary>
public sealed class WatcherService : IDisposable
{
    private readonly Database _db;
    private readonly ForegroundWatcher _fgWatcher = new();
    private readonly SessionState _session = new();
    private readonly AudioMonitor _audio = new();
    private readonly BrowserTabService _browserTabs = new();
    private readonly DispatcherTimer _timer;

    private OpenSegment? _fgSegment;
    private readonly Dictionary<uint, OpenSegment> _audioSegments = new();

    private int _idleLimitSeconds = 180;
    private bool _audioEnabled = true;
    private bool _purposeAskEnabled;
    private bool _browserTabEnabled = true;
    private string _lastScrubDay = "";
    private DateTime _lastCompactUtc = DateTime.MinValue;

    /// <summary>
    /// 片段"年轻期"：窗口标题在这段时间内连续变化时只就地更新标题，不切分片段。
    /// 浏览器加载页面时标题会连跳几次（新标签页 → 加载中 → 正式标题），
    /// 旧逻辑每次都 close+open，留下大量零长度片段。
    /// </summary>
    private const double MinSegmentSeconds = 3;

    /// <summary>压缩碎片化的间隔与回看窗口。</summary>
    private static readonly TimeSpan CompactInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan CompactLookback = TimeSpan.FromHours(2);

    /// <summary>请求对灰区应用当前片段进行用途标注（参数：片段ID、进程名）。</summary>
    public event Action<long, string>? PurposePrompt;

    public bool IsRunning { get; private set; }

    public WatcherService(Database db)
    {
        _db = db;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += (_, _) => Heartbeat();
    }

    public void Start()
    {
        if (IsRunning) return;
        IsRunning = true;

        _idleLimitSeconds = int.TryParse(_db.GetSetting("idle_limit", "180"), out var n) ? n : 180;
        _audioEnabled = _db.GetSetting("audio_enabled", "true") != "false";
        _purposeAskEnabled = _db.GetSetting("purpose_ask_enabled", "false") == "true";
        _browserTabEnabled = _db.GetSetting("browser_tab_enabled", "true") != "false";

        _fgWatcher.ForegroundChanged += OnForegroundChanged;
        _fgWatcher.Start();

        _session.Locked += () => { _db.AddSessionEvent("lock"); CloseForeground(); };
        _session.Unlocked += () => _db.AddSessionEvent("unlock");
        _session.Suspended += () => { _db.AddSessionEvent("suspend"); CloseAll(); };
        _session.Resumed += () => _db.AddSessionEvent("resume");
        _session.Start();

        // 仅在真正开机时记录一次 boot：用系统启动时间（now - TickCount64）与最近 boot 事件比对
        var systemBootUtc = DateTime.UtcNow - TimeSpan.FromMilliseconds(Environment.TickCount64);
        var lastBoot = _db.GetLastBootTime();
        if (lastBoot is null || Math.Abs((systemBootUtc - lastBoot.Value).TotalSeconds) > 90)
            _db.AddSessionEventAt("boot", systemBootUtc);

        // 历史碎片清理：旧版本在标题变化时无脑切分，留下大量零长度片段。
        // 启动时把最近 7 天的过短片段并入相邻同应用片段，之后由心跳增量维护。
        try
        {
            _db.MergeShortSegments(MinSegmentSeconds, DateTime.UtcNow.AddDays(-7));
            _lastCompactUtc = DateTime.UtcNow;
        }
        catch { /* 清理失败不影响采集 */ }

        ScrubOldTitles();
        _timer.Start();
        Heartbeat();
    }

    public void ReloadSettings()
    {
        _idleLimitSeconds = int.TryParse(_db.GetSetting("idle_limit", "180"), out var n) ? n : 180;
        _audioEnabled = _db.GetSetting("audio_enabled", "true") != "false";
        _purposeAskEnabled = _db.GetSetting("purpose_ask_enabled", "false") == "true";
        _browserTabEnabled = _db.GetSetting("browser_tab_enabled", "true") != "false";
        if (!_audioEnabled)
        {
            foreach (var seg in _audioSegments.Values)
                CloseSegment(seg);
            _audioSegments.Clear();
        }
    }

    private void OnForegroundChanged()
    {
        // 窗口切换事件：立即结算，提高精度
        try { TickForeground(DateTime.UtcNow); } catch { /* ignore */ }
    }

    private void Heartbeat()
    {
        var now = DateTime.UtcNow;
        try
        {
            TickForeground(now);
            TickAudio(now);

            var today = now.ToString("yyyy-MM-dd");
            if (today != _lastScrubDay)
            {
                _lastScrubDay = today;
                ScrubOldTitles();
            }

            // 增量压缩：每 5 分钟把最近 2 小时里被切碎的片段并回去。
            // 只处理结束时间早于 now-2min 的片段，避免碰到正在计时的开放片段。
            if (now - _lastCompactUtc > CompactInterval)
            {
                _lastCompactUtc = now;
                _db.MergeShortSegments(MinSegmentSeconds,
                    now - CompactLookback, now - TimeSpan.FromMinutes(2));
            }
        }
        catch
        {
            // 单次心跳异常不影响后续
        }
    }

    private void TickForeground(DateTime now)
    {
        if (_session.IsLocked || _session.IsSuspended || Win32.GetIdleSeconds() > _idleLimitSeconds)
        {
            CloseForeground();
            return;
        }

        var info = Win32.GetForegroundInfo();
        if (info == null || IsTransientInstaller(info.ExePath, info.ProcessName))
        {
            CloseForeground();
            return;
        }

        // 系统外壳（开始菜单/搜索/通知/锁屏等）穿透：不记录外壳、不切换
        // 应用，当前真实应用片段继续延长。锁屏/睡眠/idle 已在上方处理。
        if (AppDisplayService.IsShell(info.ProcessName, info.ExePath))
        {
            if (_fgSegment != null)
            {
                _fgSegment.EndUtc = now;
                _db.UpdateSegmentEnd(_fgSegment.RowId, now);
            }
            return;
        }

        var (appId, category, purpose) = EnsureApp(info);

        // 浏览器标签页：读取当前活动网站，并据此细化用途
        var site = "";
        if (_browserTabEnabled && BrowserTabService.IsBrowser(info.ProcessName))
        {
            site = _browserTabs.GetSite(info.Hwnd, info.ProcessName);
            if (site != "")
                purpose = PurposeService.FromSite(site, purpose);
        }

        if (_fgSegment == null)
        {
            _fgSegment = OpenSegment(appId, "foreground", now, info.Title, purpose, info.ProcessName, site);
            MaybeAskPurpose(_fgSegment, category);
        }
        else if (_fgSegment.AppId != appId)
        {
            CloseSegment(_fgSegment, now);
            _fgSegment = OpenSegment(appId, "foreground", now, info.Title, purpose, info.ProcessName, site);
            MaybeAskPurpose(_fgSegment, category);
        }
        else
        {
            var titleChanged = _fgSegment.Title != info.Title && info.Title != "";
            var siteChanged = site != "" && _fgSegment.Site != site;
            if (titleChanged || siteChanged)
            {
                if ((now - _fgSegment.StartUtc).TotalSeconds < MinSegmentSeconds)
                {
                    // 片段还很"年轻"：就地更新标题/地址，不切分。
                    // 页面加载时标题会连跳几次，切分只会得到一串零长度片段。
                    if (info.Title != "") _fgSegment.Title = info.Title;
                    if (site != "") _fgSegment.Site = site;
                    _fgSegment.EndUtc = now;
                    _db.UpdateSegmentEnd(_fgSegment.RowId, now, _fgSegment.Title, _fgSegment.Site);
                }
                else
                {
                    // 标题变化（切换文档/标签）或网站变化：切分片段以保留准确归属
                    CloseSegment(_fgSegment, now);
                    _fgSegment = OpenSegment(appId, "foreground", now, info.Title, purpose, info.ProcessName, site);
                }
            }
            else
            {
                _fgSegment.EndUtc = now;
                _db.UpdateSegmentEnd(_fgSegment.RowId, now);
            }
        }
    }

    private void TickAudio(DateTime now)
    {
        // 睡眠时无声音输出；锁屏后继续放歌仍计时（按需求）。
        if (!_audioEnabled || _session.IsSuspended)
        {
            CloseAllAudio(now);
            return;
        }

        HashSet<uint> active;
        try
        {
            active = _audio.GetActiveProcessIds();
        }
        catch
        {
            return;
        }

        // 停止发声的：关闭片段
        foreach (var pid in _audioSegments.Keys.Where(k => !active.Contains(k)).ToList())
        {
            CloseSegment(_audioSegments[pid], now);
            _audioSegments.Remove(pid);
        }

        // 正在发声的：开启 / 延长
        foreach (var pid in active)
        {
            if (_audioSegments.TryGetValue(pid, out var seg))
            {
                seg.EndUtc = now;
                _db.UpdateSegmentEnd(seg.RowId, now);
            }
            else
            {
                var info = Win32.GetInfoForPid(pid);
                if (info == null || IsTransientInstaller(info.ExePath, info.ProcessName)) continue;
                var (appId, category, purpose) = EnsureApp(info);
                _audioSegments[pid] = OpenSegment(appId, "audio", now, "", purpose, info.ProcessName);
            }
        }
    }

    private static readonly string SelfProcessName = Process.GetCurrentProcess().ProcessName;

    /// <summary>
    /// 判断是否为安装器在临时目录解压出的瞬时进程（如 Inno Setup 的
    /// Temp\is-xxxx.tmp\XxxSetup-stable-xxxx.tmp）。这类进程不应计入统计。
    /// </summary>
    private static bool IsTransientInstaller(string exePath, string processName)
    {
        if (string.IsNullOrEmpty(exePath)) return false;
        var p = exePath.Replace('/', '\\');
        bool inTemp = p.Contains(@"\Temp\");
        if (!inTemp) return false;
        return processName.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
            || processName.Contains("setup", StringComparison.OrdinalIgnoreCase);
    }

    private (int AppId, string Category, string Purpose) EnsureApp(ForegroundInfo info)
    {
        // 自身归一化：不同构建路径（Debug/Release/publish）的 ScreenTime 统一归到一个应用
        if (info.ProcessName.Equals(SelfProcessName, StringComparison.OrdinalIgnoreCase))
        {
            var selfId = _db.GetOrCreateAppByProcess(info.ProcessName, info.AppName, "系统与工具");
            // 路径随构建目录变化，同步后再提取图标，避免排行榜里自己没图标
            _db.UpdateAppExePath(selfId, info.ExePath);
            return (selfId, "系统与工具", PurposeService.Life);
        }

        var displayName = AppDisplayService.DisplayName(info.ProcessName, info.AppName);
        var category = CategoryService.Classify(info.ProcessName, info.ExePath, info.Company, info.Title);
        var appId = _db.GetOrCreateApp(info.ExePath, info.ProcessName, displayName, info.Company, category);
        _db.UpdateAppInfo(appId, displayName, info.Company);
        var purpose = PurposeService.Resolve(_db.GetAppDefaultPurpose(appId), category);
        return (appId, category, purpose);
    }

    /// <summary>灰区应用开启新前台片段时，触发一次用途标注（受开关控制）。</summary>
    private void MaybeAskPurpose(OpenSegment seg, string category)
    {
        if (!_purposeAskEnabled || seg.PurposeAsked) return;
        seg.PurposeAsked = true;
        if (!PurposeService.IsGrayArea(seg.ProcessName, category)) return;
        try { PurposePrompt?.Invoke(seg.RowId, seg.ProcessName); } catch { }
    }

    /// <summary>用途标注回调：更新当前前台片段用途（若仍在该片段）。</summary>
    public void ApplyForegroundPurpose(long segmentId, string purpose)
    {
        if (_fgSegment != null && _fgSegment.RowId == segmentId)
            _fgSegment.Purpose = purpose;
        _db.UpdateSegmentPurpose(segmentId, purpose);
    }

    private OpenSegment OpenSegment(int appId, string kind, DateTime now, string title,
        string purpose, string processName, string site = "")
    {
        var day = now.ToLocalTime().ToString("yyyy-MM-dd");
        var rowId = _db.InsertSegment(appId, kind, now, now, title, day, purpose, site);
        return new OpenSegment
        {
            RowId = rowId,
            AppId = appId,
            Kind = kind,
            StartUtc = now,
            EndUtc = now,
            Title = title,
            Purpose = purpose,
            ProcessName = processName,
            Site = site
        };
    }

    private void CloseSegment(OpenSegment seg, DateTime? at = null)
    {
        var end = at ?? DateTime.UtcNow;
        if (end > seg.EndUtc) seg.EndUtc = end;

        // 开了就切走（时长不足半秒）的片段没有任何信息量，
        // 直接删掉而不是留一条空行——否则要等 5 分钟后的压缩任务才清理。
        if ((seg.EndUtc - seg.StartUtc).TotalSeconds < 0.5)
        {
            _db.DeleteSegment(seg.RowId);
            return;
        }

        _db.UpdateSegmentEnd(seg.RowId, seg.EndUtc, seg.Kind == "foreground" ? seg.Title : null);
    }

    private void CloseForeground()
    {
        if (_fgSegment != null)
        {
            CloseSegment(_fgSegment);
            _fgSegment = null;
        }
    }

    private void CloseAllAudio(DateTime? at = null)
    {
        foreach (var seg in _audioSegments.Values)
            CloseSegment(seg, at);
        _audioSegments.Clear();
    }

    private void CloseAll()
    {
        CloseForeground();
        CloseAllAudio();
    }

    private void ScrubOldTitles()
    {
        var days = int.TryParse(_db.GetSetting("title_retention", "30"), out var d) ? d : 30;
        if (days > 0)
            _db.ScrubTitles(DateTime.UtcNow.AddDays(-days));
    }

    public void Dispose()
    {
        CloseAll();
        _db.AddSessionEvent("shutdown");
        _timer.Stop();
        _fgWatcher.Dispose();
        _session.Dispose();
    }
}
