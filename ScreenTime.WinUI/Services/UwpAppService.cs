using System.IO;
using System.Xml.Linq;
using ScreenTime.Data;
using Windows.ApplicationModel;
using Windows.ApplicationModel.Core;
using Windows.Foundation;
using Windows.Management.Deployment;

namespace ScreenTime.Services;

/// <summary>
/// 枚举已安装的 UWP / MSIX 应用（含商店应用），补进应用列表便于提前分类。
///
/// 采集侧本来就按前台窗口的进程 PID 工作，不依赖这里的导入——用过的 UWP 应用
/// 会自己出现在列表里。这里解决的是「还没用过、想先归类」的场景。
///
/// 图标取自包清单里的应用 Logo（UWP 的 exe 通常没有图标资源，从 exe 提取会得到空白），
/// 导出成 PNG 缓存到 %AppData%\ScreenTime\icons，路径写进 apps.icon_source。
///
/// 注意：全程 await，不要用 <c>.GetResult()</c> 同步阻塞 WinRT 异步——
/// 部分包的 AppListEntry / Logo 调用会因此死锁，表现为按钮永久变灰。
/// </summary>
public static class UwpAppService
{
    private static string IconCacheDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "ScreenTime", "icons");

    /// <summary>执行导入，返回新增的应用数量。</summary>
    public static async Task<int> ImportAsync(Database db)
    {
        PackageManager pm;
        try { pm = new PackageManager(); }
        catch { return 0; }

        List<Package> packages;
        try { packages = pm.FindPackagesForUser(string.Empty).ToList(); }
        catch { return 0; }

        var existing = new HashSet<string>(
            db.GetAllApps().Select(a => Key(a.ProcessName, a.Name)), StringComparer.OrdinalIgnoreCase);

        var added = 0;
        var importedProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pkg in packages)
        {
            try
            {
                // 框架包、资源包、系统包不是用户会「使用」的应用
                if (pkg.IsFramework || pkg.IsResourcePackage) continue;
                if (pkg.SignatureKind == PackageSignatureKind.System) continue;

                var root = pkg.InstalledLocation?.Path;
                if (string.IsNullOrEmpty(root)) continue;

                var manifestPath = Path.Combine(root, "AppxManifest.xml");
                if (!File.Exists(manifestPath)) continue;

                var applications = ReadApplications(manifestPath).ToList();
                if (applications.Count == 0) continue;

                var listEntries = await SafeAppListEntriesAsync(pkg);
                var publisher = SafePublisher(pkg);
                var familyName = SafeFamilyName(pkg);

                // 一个包常声明多个 Application（协议垫片、后台宿主、依赖管理器），
                // 全导入会让列表被同名条目塞满（实测「应用安装程序」出现 6 次）。
                // 只保留真正出现在开始菜单里的入口；一个入口都没有的包
                //（纯后台服务，如 ActionsServer / WidgetService）直接跳过。
                var visibleIds = VisibleAppIds(listEntries);
                if (visibleIds.Count == 0) continue;

                var filtered = applications.Where(a => visibleIds.Contains(a.AppId)).ToList();
                if (filtered.Count == 0) continue;
                applications = filtered;

                foreach (var (exe, appId) in applications)
                {
                    var process = Path.GetFileNameWithoutExtension(exe);
                    if (string.IsNullOrEmpty(process)) continue;

                    var display = ResolveDisplayName(pkg, listEntries, appId, familyName, process);
                    var exePath = Path.Combine(root, exe);
                    var category = CategoryService.Classify(process, exePath, publisher, display);
                    var icon = await CacheLogoAsync(process, listEntries, appId, familyName);

                    db.GetOrCreateApp(exePath, process, display, publisher, category, icon);

                    // 已存在的条目也要记进保留集，否则下面的清理会把它们全删掉
                    importedProcesses.Add(process);
                    if (existing.Add(Key(process, display))) added++;
                }
            }
            catch
            {
                // 受保护包读不到清单/图标，跳过即可，不影响其它包
            }
        }

        // 自愈：清掉上次导入留下的、这次已不该保留的 UWP 条目
        //（例如旧版本把协议垫片也导了进来）。只删无使用记录的。
        db.DeleteUnusedUwpApps(importedProcesses);

        return added;
    }

    private static string Key(string process, string name) =>
        process.ToLowerInvariant() + "\u0001" + name.ToLowerInvariant();

    /// <summary>WindowsApps 路径特征，用来识别 UWP 应用。</summary>
    private const string WindowsAppsMarker = @"\WindowsApps\";

    private static int _backfillRunning;
    private static int _backfillPending;
    private static DateTime _lastBackfillUtc = DateTime.MinValue;
    private static readonly object BackfillGate = new();

    /// <summary>
    /// 请求后台补一次 UWP 图标。采集时新建的 UWP 应用没有 icon_source
    ///（UWP 的 exe 通常不带图标资源），不补的话要等用户手动点「导入」才有图标。
    /// 30 秒内最多跑一次，运行中再请求会合并成「跑完再来一轮」。
    /// </summary>
    public static void RequestBackfill(Database db)
    {
        lock (BackfillGate)
        {
            if (DateTime.UtcNow - _lastBackfillUtc < TimeSpan.FromSeconds(30)) return;
            if (Interlocked.CompareExchange(ref _backfillRunning, 1, 0) != 0)
            {
                Interlocked.Exchange(ref _backfillPending, 1);
                return;
            }
            _lastBackfillUtc = DateTime.UtcNow;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                do
                {
                    Interlocked.Exchange(ref _backfillPending, 0);
                    await BackfillIconsAsync(db);
                }
                while (Interlocked.Exchange(ref _backfillPending, 0) != 0);
            }
            finally
            {
                Interlocked.Exchange(ref _backfillRunning, 0);
            }
        });
    }

    /// <summary>给「已记录但还没有图标」的 UWP 应用补图标，返回补上的数量。</summary>
    public static async Task<int> BackfillIconsAsync(Database db)
    {
        List<(int Id, string ProcessName, string IconSource)> targets;
        try
        {
            // 缺图标，或者缓存文件已经被删了（用户清过 %AppData%\ScreenTime\icons）
            targets = db.GetUwpIconCandidates(WindowsAppsMarker)
                .Where(a => string.IsNullOrEmpty(a.IconSource) || !File.Exists(a.IconSource))
                .ToList();
        }
        catch { return 0; }

        // 没事可做就直接返回，别去枚举包（那要 1~2 秒）
        if (targets.Count == 0) return 0;

        PackageManager pm;
        try { pm = new PackageManager(); }
        catch { return 0; }

        List<Package> packages;
        try { packages = pm.FindPackagesForUser(string.Empty).ToList(); }
        catch { return 0; }

        var byProcess = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pkg in packages)
        {
            try
            {
                if (pkg.IsFramework || pkg.IsResourcePackage) continue;
                if (pkg.SignatureKind == PackageSignatureKind.System) continue;

                var root = pkg.InstalledLocation?.Path;
                if (string.IsNullOrEmpty(root)) continue;
                var manifestPath = Path.Combine(root, "AppxManifest.xml");
                if (!File.Exists(manifestPath)) continue;

                var listEntries = await SafeAppListEntriesAsync(pkg);
                if (listEntries.Count == 0) continue;
                var visibleIds = VisibleAppIds(listEntries);
                var familyName = SafeFamilyName(pkg);

                foreach (var (exe, appId) in ReadApplications(manifestPath))
                {
                    if (visibleIds.Count > 0 && !visibleIds.Contains(appId)) continue;
                    var process = Path.GetFileNameWithoutExtension(exe);
                    if (string.IsNullOrEmpty(process) || byProcess.ContainsKey(process)) continue;

                    var icon = await CacheLogoAsync(process, listEntries, appId, familyName);
                    if (icon.Length > 0) byProcess[process] = icon;
                }
            }
            catch
            {
                // 受保护包读不到，跳过
            }
        }

        var filled = 0;
        foreach (var (appId, process, _) in targets)
            if (byProcess.TryGetValue(process, out var icon) && db.SetIconSource(appId, icon))
                filled++;
        return filled;
    }

    /// <summary>读 AppxManifest.xml 里声明的可执行文件（一个包可以有多个应用入口）。</summary>
    private static IEnumerable<(string Exe, string AppId)> ReadApplications(string manifestPath)
    {
        XDocument doc;
        try { doc = XDocument.Load(manifestPath); }
        catch { yield break; }

        foreach (var app in doc.Descendants().Where(e => e.Name.LocalName == "Application"))
        {
            var exe = (string?)app.Attribute("Executable");
            if (string.IsNullOrWhiteSpace(exe)) continue;
            if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            yield return (exe.Trim(), ((string?)app.Attribute("Id"))?.Trim() ?? "");
        }
    }

    private static async Task<IReadOnlyList<AppListEntry>> SafeAppListEntriesAsync(Package pkg)
    {
        try { return await pkg.GetAppListEntriesAsync(); }
        catch { return Array.Empty<AppListEntry>(); }
    }

    private static string SafePublisher(Package pkg)
    {
        try { return pkg.PublisherDisplayName ?? ""; }
        catch { return ""; }
    }

    private static string SafeFamilyName(Package pkg)
    {
        try { return pkg.Id.FamilyName ?? ""; }
        catch { return ""; }
    }

    /// <summary>从 AppListEntry 的 AUMID（FamilyName!AppId）里取出 AppId 集合。</summary>
    private static HashSet<string> VisibleAppIds(IReadOnlyList<AppListEntry> entries)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in entries)
        {
            try
            {
                var aumid = e.AppUserModelId;
                if (string.IsNullOrEmpty(aumid)) continue;
                var bang = aumid.LastIndexOf('!');
                if (bang >= 0 && bang + 1 < aumid.Length) set.Add(aumid[(bang + 1)..]);
            }
            catch { }
        }
        return set;
    }

    private static AppListEntry? PickEntry(IReadOnlyList<AppListEntry> entries, string appId, string familyName)
    {
        if (entries.Count == 0) return null;
        if (!string.IsNullOrEmpty(appId) && !string.IsNullOrEmpty(familyName))
        {
            var aumid = $"{familyName}!{appId}";
            foreach (var e in entries)
                if (string.Equals(e.AppUserModelId, aumid, StringComparison.OrdinalIgnoreCase))
                    return e;
        }
        return entries[0];
    }

    /// <summary>
    /// 显示名优先取 AppListEntry（会解析 ms-resource 本地化资源），
    /// 其次 Package.DisplayName，最后退回进程名。
    /// </summary>
    private static string ResolveDisplayName(
        Package pkg, IReadOnlyList<AppListEntry> entries, string appId, string familyName, string fallback)
    {
        var entry = PickEntry(entries, appId, familyName);
        if (entry != null)
        {
            try
            {
                var name = entry.DisplayInfo.DisplayName;
                if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
            }
            catch { }
        }

        try
        {
            var n = pkg.DisplayName;
            if (!string.IsNullOrWhiteSpace(n) &&
                !n.StartsWith("ms-resource:", StringComparison.OrdinalIgnoreCase))
                return n.Trim();
        }
        catch { }

        return AppDisplayService.DisplayName(fallback, fallback);
    }

    /// <summary>把包内 Logo 导出成 PNG，返回缓存文件路径（失败返回空串）。</summary>
    private static async Task<string> CacheLogoAsync(
        string process, IReadOnlyList<AppListEntry> entries, string appId, string familyName)
    {
        try
        {
            Directory.CreateDirectory(IconCacheDir);
            var target = Path.Combine(IconCacheDir, process.ToLowerInvariant() + ".png");

            // 一周内导出过就复用，避免每次导入都写盘
            if (File.Exists(target) && new FileInfo(target).Length > 0 &&
                File.GetLastWriteTimeUtc(target) > DateTime.UtcNow.AddDays(-7))
                return target;

            var entry = PickEntry(entries, appId, familyName);
            if (entry == null) return "";

            var logoRef = entry.DisplayInfo.GetLogo(new Size(48, 48));
            using var source = await logoRef.OpenReadAsync();
            if (source.Size == 0) return "";

            // 原样落盘。四周的透明留白由 AppIconService 在加载时裁掉——
            // 放在加载侧，历史缓存也能立刻受益，不必等下次导入。
            using (var net = source.AsStreamForRead())
            using (var file = File.Create(target))
                await net.CopyToAsync(file);

            if (!File.Exists(target) || new FileInfo(target).Length == 0)
            {
                TryDelete(target);
                return "";
            }
            return target;
        }
        catch
        {
            return "";
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { }
    }
}
