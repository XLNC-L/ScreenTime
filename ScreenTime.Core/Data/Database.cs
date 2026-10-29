using System.IO;
using Microsoft.Data.Sqlite;
using ScreenTime.Services;

namespace ScreenTime.Data;

/// <summary>聚合查询结果行。</summary>
public record AppUsageRow(
    int AppId,
    string AppName,
    string ProcessName,
    string Category,
    double ForegroundSeconds,
    double AudioSeconds,
    double TotalSeconds);

public record AppDailyUsageRow(string Day, double Seconds);

public record TimelineRow(
    string StartUtc,
    string EndUtc,
    string Kind,
    string AppName,
    string Title,
    double DurationSeconds,
    string Category);

public record SessionEventRow(int Id, string Type, string TsUtc, string Note);

/// <summary>单个应用下的网页使用行（site 为归一化聚合键，url 为可跳转原始地址）。</summary>
public record PageUsageRow(string Site, string Title, string Url, double Seconds);

/// <summary>跨浏览器的网页使用行。ProcessName 用于区分同一页面的来源浏览器。</summary>
public record WebPageRow(string Site, string Title, string Url, string AppName, string ProcessName, double Seconds);

/// <summary>网页时间线中的单个浏览片段。</summary>
public record WebTimelineRow(
    DateTime StartUtc, DateTime EndUtc, string Site, string Title, string Url,
    string AppName, string ProcessName);

/// <summary>待回填的片段：无 site 的浏览器前台记录。</summary>
public record BackfillCandidateRow(long Id, int AppId, DateTime StartUtc, DateTime EndUtc, string Title);

/// <summary>SQLite 数据访问层。数据库位置可由 exe 同目录的 screentime.location 指定，默认 %AppData%\ScreenTime。</summary>
public sealed class Database
{
    private string _connString;

    public string DbPath { get; private set; }

    /// <summary>位置配置文件（exe 同目录，纯文本存储数据文件夹路径）。</summary>
    private static string LocationConfigPath =>
        Path.Combine(AppContext.BaseDirectory, "screentime.location");

    /// <param name="overridePath">
    /// 直接指定数据库文件（<c>--db=</c> 命令行参数）。仅用于测试与演示数据，
    /// 不写位置配置文件，避免污染用户真实数据的存放位置。
    /// </param>
    public Database(string? overridePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            var full = Path.GetFullPath(overridePath);
            var dir = Path.GetDirectoryName(full);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            DbPath = full;
            _connString = new SqliteConnectionStringBuilder { DataSource = DbPath }.ToString();
            IsOverrideLocation = true;
            return;
        }

        var dataDir = ResolveDataDirectory();
        Directory.CreateDirectory(dataDir);
        DbPath = Path.Combine(dataDir, "screentime.db");
        _connString = new SqliteConnectionStringBuilder { DataSource = DbPath }.ToString();
    }

    /// <summary>该库是否由 --db 指定的临时/演示库（不参与位置迁移）。</summary>
    public bool IsOverrideLocation { get; private set; }

    private static string DefaultDataDirectory() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ScreenTime");

    private static string ResolveDataDirectory()
    {
        try
        {
            if (File.Exists(LocationConfigPath))
            {
                var text = File.ReadAllText(LocationConfigPath).Trim();
                if (!string.IsNullOrEmpty(text) && Directory.Exists(text)) return text;
            }
        }
        catch { }
        return DefaultDataDirectory();
    }

    /// <summary>
    /// 将数据库迁移到新文件夹：WAL checkpoint → 复制主库 → 写入位置配置 → 切换连接。
    /// 失败时回退到原位置。
    /// </summary>
    public (bool Ok, string? Error) ChangeDataLocation(string newDir)
    {
        try
        {
            newDir = newDir.Trim();
            if (!Directory.Exists(newDir)) Directory.CreateDirectory(newDir);
            var newPath = Path.Combine(newDir, "screentime.db");

            if (string.Equals(Path.GetFullPath(newPath), Path.GetFullPath(DbPath),
                StringComparison.OrdinalIgnoreCase))
                return (true, null);

            // checkpoint：把 WAL 合并进主库，确保复制的是完整数据
            using (var c = Open())
            using (var ck = c.CreateCommand())
            {
                ck.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                ck.ExecuteNonQuery();
            }

            File.Copy(DbPath, newPath, overwrite: true);
            File.WriteAllText(LocationConfigPath, newDir);

            DbPath = newPath;
            _connString = new SqliteConnectionStringBuilder { DataSource = newPath }.ToString();
            return (true, null);
        }
        catch (Exception ex)
        {
            var dir = ResolveDataDirectory();
            DbPath = Path.Combine(dir, "screentime.db");
            _connString = new SqliteConnectionStringBuilder { DataSource = DbPath }.ToString();
            return (false, ex.Message);
        }
    }

    /// <summary>恢复使用默认数据位置（%AppData%\ScreenTime）。</summary>
    public (bool Ok, string? Error) ResetDataLocation()
    {
        try
        {
            if (File.Exists(LocationConfigPath)) File.Delete(LocationConfigPath);
        }
        catch { }
        var dir = DefaultDataDirectory();
        Directory.CreateDirectory(dir);
        DbPath = Path.Combine(dir, "screentime.db");
        _connString = new SqliteConnectionStringBuilder { DataSource = DbPath }.ToString();
        return (true, null);
    }

    private SqliteConnection Open()
    {
        var c = new SqliteConnection(_connString);
        c.Open();
        using (var pragma = c.CreateCommand())
        {
            // 采集心跳在后台线程、页面查询在工作线程，两边可能同时写。
            // WAL 只在初始化时设置；每次查询都切换 journal_mode 会制造额外锁竞争。
            pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA busy_timeout=3000;";
            pragma.ExecuteNonQuery();
        }
        return c;
    }

    public void Init()
    {
        using var c = Open();
        // 只在还不是 WAL 时才切换：切 journal_mode 需要排他锁，
        // 每次启动都设一遍会在 Agent 正在写库时白等（表现为「双击图标半天不出窗口」）。
        if (!IsWalMode(c))
        {
            using var wal = c.CreateCommand();
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            wal.ExecuteNonQuery();
        }
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS apps (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                exe_path TEXT NOT NULL DEFAULT '',
                process_name TEXT NOT NULL DEFAULT '',
                app_name TEXT NOT NULL DEFAULT '',
                company TEXT NOT NULL DEFAULT '',
                category TEXT NOT NULL DEFAULT '未分类',
                category_manual INTEGER NOT NULL DEFAULT 0,
                ignored_manual INTEGER NOT NULL DEFAULT 0,
                default_purpose TEXT NOT NULL DEFAULT '',
                icon_source TEXT NOT NULL DEFAULT '',
                merged_into INTEGER NOT NULL DEFAULT 0,
                ignored INTEGER NOT NULL DEFAULT 0,
                UNIQUE(exe_path, process_name)
            );

            CREATE TABLE IF NOT EXISTS usage_segments (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                app_id INTEGER NOT NULL,
                kind TEXT NOT NULL,                 -- foreground / audio
                start_ts TEXT NOT NULL,
                end_ts TEXT NOT NULL,
                title TEXT NOT NULL DEFAULT '',
                purpose TEXT NOT NULL DEFAULT '',
                site TEXT NOT NULL DEFAULT '',
                url TEXT NOT NULL DEFAULT '',
                day TEXT NOT NULL,
                FOREIGN KEY(app_id) REFERENCES apps(id)
            );
            CREATE INDEX IF NOT EXISTS idx_seg_day ON usage_segments(day);
            CREATE INDEX IF NOT EXISTS idx_seg_app ON usage_segments(app_id);
            -- 收件箱徽标/单应用历史都按「应用 + 日期区间」查，复合索引省掉回表扫描
            CREATE INDEX IF NOT EXISTS idx_seg_app_day ON usage_segments(app_id, day);

            CREATE TABLE IF NOT EXISTS session_events (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                type TEXT NOT NULL,
                ts TEXT NOT NULL,
                note TEXT NOT NULL DEFAULT ''
            );

            CREATE TABLE IF NOT EXISTS settings (
                key TEXT PRIMARY KEY,
                value TEXT NOT NULL
            );
            """;
        cmd.ExecuteNonQuery();
        // 旧库补列必须在任何引用这些列的 SQL 之前完成：
        // MarkNoiseAppsIgnored 会读 ignored_manual，放到 Migrate 里补列就晚了。
        EnsureSchemaColumns(c);

        // 噪声扫描是全表 UPDATE，一天跑一次足够；新应用入库时已就地判定（见 GetOrCreateApp）。
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        if (GetSettingRaw(c, "noise_scan_day", "") != today)
        {
            MarkNoiseAppsIgnored(c);
            SetSettingRaw(c, "noise_scan_day", today);
        }
    }

    private static bool IsWalMode(SqliteConnection c)
    {
        try
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode;";
            return string.Equals(cmd.ExecuteScalar() as string, "wal", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string GetSettingRaw(SqliteConnection c, string key, string fallback)
    {
        try
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText = "SELECT value FROM settings WHERE key=$k";
            cmd.Parameters.AddWithValue("$k", key);
            return cmd.ExecuteScalar() as string ?? fallback;
        }
        catch
        {
            return fallback;
        }
    }

    private static void SetSettingRaw(SqliteConnection c, string key, string value)
    {
        try
        {
            using var cmd = c.CreateCommand();
            cmd.CommandText =
                "INSERT INTO settings(key,value) VALUES($k,$v) ON CONFLICT(key) DO UPDATE SET value=$v";
            cmd.Parameters.AddWithValue("$k", key);
            cmd.Parameters.AddWithValue("$v", value);
            cmd.ExecuteNonQuery();
        }
        catch
        {
            // 标记写失败不影响主流程，下次启动会再扫一遍
        }
    }

    /// <summary>补齐旧库缺失的列（幂等）。</summary>
    private static void EnsureSchemaColumns(SqliteConnection c)
    {
        EnsureColumn(c, "apps", "category_manual", "INTEGER NOT NULL DEFAULT 0");
        // 用户亲手改过「忽略」标记：自动噪声忽略不再覆盖
        EnsureColumn(c, "apps", "ignored_manual", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn(c, "apps", "default_purpose", "TEXT NOT NULL DEFAULT ''");
        // 显式图标来源（UWP 包内 PNG 等）；为空时回退到从 exe 提取
        EnsureColumn(c, "apps", "icon_source", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(c, "usage_segments", "purpose", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn(c, "usage_segments", "site", "TEXT NOT NULL DEFAULT ''");
        // 原始可跳转地址（site 为归一化聚合键，url 仅供点击跳转，不含追踪参数）
        EnsureColumn(c, "usage_segments", "url", "TEXT NOT NULL DEFAULT ''");
    }

    /// <summary>
    /// 把明显的系统/安装器噪声应用标记为忽略，使历史统计也不再计入。
    /// 进程名单来自 <see cref="AppDisplayService.NoiseProcessNames"/>，与收件箱过滤共用同一份，
    /// 避免两处规则漂移。用户手动恢复过的应用（ignored_manual=1）不在此覆盖。
    /// <para>
    /// 路径匹配用 instr 而不是 LIKE：LIKE 的 <c>\</c> 在 SQLite 里是普通字符，
    /// 原先写的 <c>'%\\appdata\\local\\temp\\%'</c> 要求两个连续反斜杠，实际路径只有一个，
    /// 这条过滤从来没生效过（Autoruns 这类 Temp 解压程序一直没被忽略）。
    /// </para>
    /// </summary>
    private static void MarkNoiseAppsIgnored(SqliteConnection c)
    {
        // process_name 有的存不带 .exe、有的带，两种形式都放进 IN 列表
        var names = AppDisplayService.NoiseProcessNames
            .SelectMany(n => new[] { n, n + ".exe" })
            .ToList();
        var inList = string.Join(",", names.Select((_, i) => $"$n{i}"));
        var pathClauses = string.Join(" OR ", AppDisplayService.NoisePathFragments
            .Select((_, i) => $"instr(lower(exe_path), $p{i}) > 0"));

        using var cmd = c.CreateCommand();
        cmd.CommandText = $"""
            UPDATE apps SET ignored=1
            WHERE ignored_manual = 0
              AND (lower(process_name) IN ({inList}) OR {pathClauses});
            """;
        for (var i = 0; i < names.Count; i++) cmd.Parameters.AddWithValue($"$n{i}", names[i]);
        for (var i = 0; i < AppDisplayService.NoisePathFragments.Length; i++)
            cmd.Parameters.AddWithValue($"$p{i}", AppDisplayService.NoisePathFragments[i].ToLowerInvariant());
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 结构升级与数据迁移：对历史数据重分类、合并重复记录。幂等，可重复执行。
    /// <para>
    /// UI 与 Agent 启动时都会调用：用跨进程互斥把两边串起来，避免同时 ALTER / 合并互相踩。
    /// 拿不到锁说明另一个进程正在迁移，直接跳过即可（对方做完结果一样）。
    /// </para>
    /// </summary>
    public void Migrate()
    {
        using var gate = new Mutex(false, "ScreenTime_Migrate_Mutex");
        var acquired = false;
        try
        {
            try { acquired = gate.WaitOne(TimeSpan.FromSeconds(30)); }
            catch (AbandonedMutexException) { acquired = true; } // 上个进程异常退出，锁归我们
            if (!acquired) return;
            MigrateCore();
        }
        finally
        {
            if (acquired) gate.ReleaseMutex();
        }
    }

    private void MigrateCore()
    {
        using var c = Open();
        // 列已在 Init → EnsureSchemaColumns 补齐，这里只做数据层面的迁移

        // 1) 全局重映射旧分类名 → 新分类名（类别改名，手动分类也同步）
        var rename = new List<(int Id, string Category)>();
        using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT id, category FROM apps";
            using var r = q.ExecuteReader();
            while (r.Read())
            {
                var old = r.GetString(1);
                var mapped = CategoryService.MapLegacy(old);
                if (mapped != old) rename.Add((r.GetInt32(0), mapped));
            }
        }
        foreach (var (id, category) in rename)
        {
            using var u = c.CreateCommand();
            u.CommandText = "UPDATE apps SET category=$ca WHERE id=$id";
            u.Parameters.AddWithValue("$ca", category);
            u.Parameters.AddWithValue("$id", id);
            u.ExecuteNonQuery();
        }

        // 2) 对未手动修改过分类的应用，用新规则重分类
        var pending = new List<(int Id, string Category)>();
        using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT id, process_name, exe_path, company FROM apps WHERE category_manual=0";
            using var r = q.ExecuteReader();
            while (r.Read())
                pending.Add((r.GetInt32(0),
                    CategoryService.Classify(r.GetString(1), r.GetString(2), r.GetString(3), "")));
        }
        foreach (var (id, category) in pending)
        {
            using var u = c.CreateCommand();
            u.CommandText = "UPDATE apps SET category=$ca WHERE id=$id";
            u.Parameters.AddWithValue("$ca", category);
            u.Parameters.AddWithValue("$id", id);
            u.ExecuteNonQuery();
        }

        // 合并重复的自身（ScreenTime）记录
        MergeAppsByProcess(c, "ScreenTime");

        // 合并「同进程名 + 同显示名」的重复条目：exe 被移动/升级（如 D:\Doubao\app\Doubao.exe
        // → D:\Doubao\Doubao.exe）时旧代码会按 UNIQUE(exe_path, process_name) 新建一条，
        // 导致应用页出现两个「豆包」。保留使用记录最多的那条，其余并入。
        MergeDuplicateApps(c);

        // 清掉浏览器 PWA 启动器的残留条目：它们永远不会有自己的时长
        DeletePwaLauncherApps(c);

        // 3) 系统外壳应用标记忽略（保留数据，可在应用页恢复）
        var shellIds = new List<int>();
        using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT id, process_name, exe_path FROM apps";
            using var r = q.ExecuteReader();
            while (r.Read())
                if (AppDisplayService.IsShell(r.GetString(1), r.GetString(2)))
                    shellIds.Add(r.GetInt32(0));
        }
        foreach (var id in shellIds)
        {
            using var u = c.CreateCommand();
            u.CommandText = "UPDATE apps SET ignored=1 WHERE id=$id";
            u.Parameters.AddWithValue("$id", id);
            u.ExecuteNonQuery();
        }

        // 4) 按进程名重算中文显示名
        var renames = new List<(int Id, string Name)>();
        using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT id, process_name, app_name FROM apps";
            using var r = q.ExecuteReader();
            while (r.Read())
            {
                var zh = AppDisplayService.ChineseName(r.GetString(1));
                if (zh != null && zh != r.GetString(2))
                    renames.Add((r.GetInt32(0), zh));
            }
        }
        foreach (var (id, name) in renames)
        {
            using var u = c.CreateCommand();
            u.CommandText = "UPDATE apps SET app_name=$n WHERE id=$id";
            u.Parameters.AddWithValue("$n", name);
            u.Parameters.AddWithValue("$id", id);
            u.ExecuteNonQuery();
        }

        // 补索引：按开始时间范围查询
        using (var idx = c.CreateCommand())
        {
            idx.CommandText = "CREATE INDEX IF NOT EXISTS idx_seg_start ON usage_segments(start_ts)";
            idx.ExecuteNonQuery();
        }
    }

    private static void EnsureColumn(SqliteConnection c, string table, string column, string declaration)
    {
        var exists = false;
        using (var q = c.CreateCommand())
        {
            q.CommandText = $"PRAGMA table_info({table})";
            using var r = q.ExecuteReader();
            while (r.Read())
                if (r.GetString(1) == column) { exists = true; break; }
        }
        if (exists) return;
        using var add = c.CreateCommand();
        add.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {declaration}";
        try
        {
            add.ExecuteNonQuery();
        }
        catch (SqliteException ex) when (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase))
        {
            // PRAGMA 与 ALTER 之间被另一个进程抢先补上了，结果一致，忽略
        }
    }

    /// <summary>把同一进程名的多个条目合并为最早的一条（转移片段后删除多余项）。</summary>
    private static void MergeAppsByProcess(SqliteConnection c, string processName)
    {
        var ids = new List<int>();
        using (var q = c.CreateCommand())
        {
            q.CommandText = "SELECT id FROM apps WHERE process_name=$n ORDER BY id";
            q.Parameters.AddWithValue("$n", processName);
            using var r = q.ExecuteReader();
            while (r.Read()) ids.Add(r.GetInt32(0));
        }
        if (ids.Count <= 1) return;
        var keep = ids[0];
        foreach (var id in ids.Skip(1))
        {
            using (var u = c.CreateCommand())
            {
                u.CommandText = "UPDATE usage_segments SET app_id=$k WHERE app_id=$i";
                u.Parameters.AddWithValue("$k", keep);
                u.Parameters.AddWithValue("$i", id);
                u.ExecuteNonQuery();
            }
            using var d = c.CreateCommand();
            d.CommandText = "DELETE FROM apps WHERE id=$i";
            d.Parameters.AddWithValue("$i", id);
            d.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// 合并「同进程名 + 同显示名」的重复应用条目（exe 路径变化导致的分裂）。
    /// 保留片段数最多的一条（并列取 id 最小），把其余条目的片段与合并关系转过去后删除。
    /// 返回被删除的条目数。
    /// </summary>
    private static int MergeDuplicateApps(SqliteConnection c)
    {
        var groups = new Dictionary<string, List<(int Id, int Segments)>>(StringComparer.Ordinal);

        using (var q = c.CreateCommand())
        {
            q.CommandText = """
                SELECT a.id, a.process_name, a.app_name,
                       (SELECT COUNT(*) FROM usage_segments s WHERE s.app_id = a.id) AS segs
                FROM apps a
                """;
            using var r = q.ExecuteReader();
            while (r.Read())
            {
                var process = r.GetString(1).Trim();
                if (process.Length == 0) continue;
                var key = process.ToLowerInvariant() + "\u0001" + r.GetString(2).Trim().ToLowerInvariant();
                if (!groups.TryGetValue(key, out var list))
                {
                    list = new List<(int, int)>();
                    groups[key] = list;
                }
                list.Add((r.GetInt32(0), r.GetInt32(3)));
            }
        }

        var removed = 0;
        foreach (var list in groups.Values)
        {
            if (list.Count <= 1) continue;

            var keep = list.OrderByDescending(x => x.Segments).ThenBy(x => x.Id).First().Id;
            foreach (var (id, _) in list)
            {
                if (id == keep) continue;

                using (var move = c.CreateCommand())
                {
                    move.CommandText = "UPDATE usage_segments SET app_id=$keep WHERE app_id=$drop";
                    move.Parameters.AddWithValue("$keep", keep);
                    move.Parameters.AddWithValue("$drop", id);
                    move.ExecuteNonQuery();
                }
                using (var chain = c.CreateCommand())
                {
                    chain.CommandText = "UPDATE apps SET merged_into=$keep WHERE merged_into=$drop";
                    chain.Parameters.AddWithValue("$keep", keep);
                    chain.Parameters.AddWithValue("$drop", id);
                    chain.ExecuteNonQuery();
                }
                using (var del = c.CreateCommand())
                {
                    del.CommandText = "DELETE FROM apps WHERE id=$drop";
                    del.Parameters.AddWithValue("$drop", id);
                    del.ExecuteNonQuery();
                }
                removed++;
            }
        }
        return removed;
    }

    /// <summary>
    /// 删除浏览器 PWA 启动器的条目。
    /// Chromium 系浏览器（Chrome / Edge / Brave / Vivaldi / Opera 等）的网页应用
    /// 启动器一律叫 <c>&lt;brand&gt;_proxy.exe</c>，它只在点开瞬间闪一下，
    /// PWA 窗口实际由浏览器进程承载，永远不会产生自己的时长。
    /// 有片段的（理论上不会有）保留，避免误删。
    /// </summary>
    private static int DeletePwaLauncherApps(SqliteConnection c)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            DELETE FROM apps
            WHERE merged_into = 0
              AND id NOT IN (SELECT app_id FROM usage_segments)
              AND process_name LIKE '%\_proxy' ESCAPE '\'
            """;
        return cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 删除「这次导入后已不该保留」的 UWP 条目：只动 WindowsApps 下、无使用记录、
    /// 未参与合并的条目。用于让重复导入自愈（例如旧版本误把协议垫片也导了进来）。
    /// </summary>
    public int DeleteUnusedUwpApps(IReadOnlyCollection<string> keepProcessNames)
    {
        using var c = Open();
        var keep = new HashSet<string>(keepProcessNames, StringComparer.OrdinalIgnoreCase);

        var drop = new List<int>();
        using (var q = c.CreateCommand())
        {
            q.CommandText = """
                SELECT id, process_name FROM apps
                WHERE exe_path LIKE '%\WindowsApps\%'
                  AND merged_into = 0
                  AND id NOT IN (SELECT app_id FROM usage_segments)
                """;
            using var r = q.ExecuteReader();
            while (r.Read())
                if (!keep.Contains(r.GetString(1))) drop.Add(r.GetInt32(0));
        }

        foreach (var id in drop)
        {
            using var d = c.CreateCommand();
            d.CommandText = "DELETE FROM apps WHERE id=$id";
            d.Parameters.AddWithValue("$id", id);
            d.ExecuteNonQuery();
        }
        return drop.Count;
    }

    /// <summary>
    /// 取出路径匹配的应用及其图标来源，供后台判断哪些需要（重新）导出图标。
    /// 返回全部而不是只返回缺图标的：调用方还要检查缓存文件是否已被删除。
    /// </summary>
    public List<(int Id, string ProcessName, string IconSource)> GetUwpIconCandidates(string pathMarker)
    {
        var list = new List<(int, string, string)>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT id, process_name, COALESCE(icon_source, '') FROM apps
            WHERE exe_path LIKE $m
            """;
        cmd.Parameters.AddWithValue("$m", "%" + pathMarker + "%");
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add((r.GetInt32(0), r.GetString(1), r.GetString(2)));
        return list;
    }

    /// <summary>该应用是否已有图标来源。</summary>
    public bool AppHasIcon(int appId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(icon_source, '') FROM apps WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", appId);
        return cmd.ExecuteScalar() is string s && s.Length > 0;
    }

    /// <summary>写入图标来源，返回是否真的改动了。</summary>
    public bool SetIconSource(int appId, string iconSource)
    {
        if (string.IsNullOrEmpty(iconSource)) return false;
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE apps SET icon_source=$s WHERE id=$id AND icon_source<>$s";
        cmd.Parameters.AddWithValue("$s", iconSource);
        cmd.Parameters.AddWithValue("$id", appId);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>获取应用的默认用途（未设置返回空串）。</summary>
    public string GetAppDefaultPurpose(int appId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT default_purpose FROM apps WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", appId);
        return cmd.ExecuteScalar() as string ?? "";
    }

    /// <summary>设置应用的默认用途。</summary>
    public void SetAppDefaultPurpose(int appId, string purpose)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE apps SET default_purpose=$p WHERE id=$id";
        cmd.Parameters.AddWithValue("$p", purpose);
        cmd.Parameters.AddWithValue("$id", appId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>更新应用的可执行文件路径（自身归一化后路径会随构建目录变化）。</summary>
    /// <summary>更新应用的 exe 路径，返回是否真的变了。</summary>
    public bool UpdateAppExePath(int appId, string exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return false;
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE apps SET exe_path=$p WHERE id=$id AND exe_path<>$p";
        cmd.Parameters.AddWithValue("$p", exePath);
        cmd.Parameters.AddWithValue("$id", appId);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// 删除指定 exe 且从未产生使用记录的应用（用于清理导入时误加入的系统工具）。
    /// 已有使用记录或已被合并的不会被删。返回是否删除。
    /// </summary>
    public bool DeleteAppIfUnused(string exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return false;
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            DELETE FROM apps
            WHERE exe_path=$p AND merged_into=0
              AND id NOT IN (SELECT app_id FROM usage_segments)
            """;
        cmd.Parameters.AddWithValue("$p", exePath);
        return cmd.ExecuteNonQuery() > 0;
    }

    /// <summary>
    /// 把未标注用途的历史片段按应用分类归入推荐用途（一键归类）。
    /// 返回写入的片段数。
    /// </summary>
    public int NormalizePurposes()
    {
        var total = 0;
        using var c = Open();
        foreach (var category in CategoryService.Categories.Append(CategoryService.Unclassified))
        {
            var purpose = PurposeService.AutoFromCategory(category);
            if (string.IsNullOrEmpty(purpose)) continue;

            using var cmd = c.CreateCommand();
            cmd.CommandText = """
                UPDATE usage_segments SET purpose=$p
                WHERE (purpose IS NULL OR purpose='')
                  AND app_id IN (SELECT id FROM apps WHERE category=$c AND merged_into=0)
                """;
            cmd.Parameters.AddWithValue("$p", purpose);
            cmd.Parameters.AddWithValue("$c", category);
            total += cmd.ExecuteNonQuery();
        }
        return total;
    }

    /// <summary>仅按进程名查找/创建应用（用于自身归一化，避免不同构建路径产生重复条目）。</summary>
    public int GetOrCreateAppByProcess(string processName, string appName, string category)
    {
        using var c = Open();
        using var find = c.CreateCommand();
        find.CommandText = "SELECT id FROM apps WHERE process_name=$n ORDER BY id LIMIT 1";
        find.Parameters.AddWithValue("$n", processName);
        if (find.ExecuteScalar() is long id) return (int)id;

        using var ins = c.CreateCommand();
        ins.CommandText = """
            INSERT INTO apps (exe_path, process_name, app_name, category, ignored)
            VALUES ('',$n,$a,$ca,$ig);
            SELECT last_insert_rowid();
            """;
        ins.Parameters.AddWithValue("$n", processName);
        ins.Parameters.AddWithValue("$a", appName);
        ins.Parameters.AddWithValue("$ca", category);
        ins.Parameters.AddWithValue("$ig", AppDisplayService.IsInboxNoise(processName, "") ? 1 : 0);
        return (int)(long)ins.ExecuteScalar()!;
    }

    public int GetOrCreateApp(string exePath, string processName, string appName, string company,
        string category, string iconSource = "")
    {
        using var c = Open();

        // 1) 先按「进程名 + 显示名」找。exe 被移动或升级时路径会变，
        //    旧逻辑按 UNIQUE(exe_path, process_name) 匹配会新建一条重复记录。
        using (var byName = c.CreateCommand())
        {
            byName.CommandText = """
                SELECT id FROM apps
                WHERE process_name=$n AND app_name=$a COLLATE NOCASE
                ORDER BY id LIMIT 1
                """;
            byName.Parameters.AddWithValue("$n", processName);
            byName.Parameters.AddWithValue("$a", appName);
            if (byName.ExecuteScalar() is long found)
            {
                var foundId = (int)found;
                SyncExePath(c, foundId, exePath);
                SyncIconSource(c, foundId, iconSource);
                return foundId;
            }
        }

        // 2) 退回按路径匹配：显示名随版本变化时仍能命中旧条目
        using (var find = c.CreateCommand())
        {
            find.CommandText = "SELECT id FROM apps WHERE exe_path=$p AND process_name=$n";
            find.Parameters.AddWithValue("$p", exePath);
            find.Parameters.AddWithValue("$n", processName);
            if (find.ExecuteScalar() is long id)
            {
                SyncIconSource(c, (int)id, iconSource);
                return (int)id;
            }
        }

        using var ins = c.CreateCommand();
        ins.CommandText = """
            INSERT INTO apps (exe_path, process_name, app_name, company, category, icon_source, ignored)
            VALUES ($p,$n,$a,$co,$ca,$ic,$ig);
            SELECT last_insert_rowid();
            """;
        ins.Parameters.AddWithValue("$p", exePath);
        ins.Parameters.AddWithValue("$n", processName);
        ins.Parameters.AddWithValue("$a", appName);
        ins.Parameters.AddWithValue("$co", company);
        ins.Parameters.AddWithValue("$ca", category);
        ins.Parameters.AddWithValue("$ic", iconSource);
        // 新应用入库时就判定噪声，不必等每日全表扫描
        ins.Parameters.AddWithValue("$ig", AppDisplayService.IsInboxNoise(processName, exePath) ? 1 : 0);
        return (int)(long)ins.ExecuteScalar()!;
    }

    /// <summary>同步 exe 路径；与已有条目冲突时跳过（UNIQUE 约束），不影响统计。</summary>
    private static void SyncExePath(SqliteConnection c, int appId, string exePath)
    {
        if (string.IsNullOrEmpty(exePath)) return;
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE OR IGNORE apps SET exe_path=$p WHERE id=$id AND exe_path<>$p";
        cmd.Parameters.AddWithValue("$p", exePath);
        cmd.Parameters.AddWithValue("$id", appId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>记录显式图标来源；路径变了就更新（例如图标缓存换版本）。</summary>
    private static void SyncIconSource(SqliteConnection c, int appId, string iconSource)
    {
        if (string.IsNullOrEmpty(iconSource)) return;
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE apps SET icon_source=$s WHERE id=$id AND icon_source<>$s";
        cmd.Parameters.AddWithValue("$s", iconSource);
        cmd.Parameters.AddWithValue("$id", appId);
        cmd.ExecuteNonQuery();
    }

    public void UpdateAppInfo(int id, string appName, string company)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE apps SET app_name=$a, company=$co WHERE id=$id AND (app_name='' OR app_name=$n)";
        cmd.Parameters.AddWithValue("$a", appName);
        cmd.Parameters.AddWithValue("$co", company);
        cmd.Parameters.AddWithValue("$n", "");
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public long InsertSegment(int appId, string kind, DateTime startUtc, DateTime endUtc,
        string title, string day, string purpose, string site = "", string url = "")
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO usage_segments (app_id, kind, start_ts, end_ts, title, day, purpose, site, url)
            VALUES ($a,$k,$s,$e,$t,$d,$p,$si,$u);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("$a", appId);
        cmd.Parameters.AddWithValue("$k", kind);
        cmd.Parameters.AddWithValue("$s", ToIso(startUtc));
        cmd.Parameters.AddWithValue("$e", ToIso(endUtc));
        cmd.Parameters.AddWithValue("$t", title);
        cmd.Parameters.AddWithValue("$d", day);
        cmd.Parameters.AddWithValue("$p", purpose);
        cmd.Parameters.AddWithValue("$si", site);
        cmd.Parameters.AddWithValue("$u", url);
        return (long)cmd.ExecuteScalar()!;
    }

    /// <summary>更新片段用途（用于灰区应用会话级标注）。</summary>
    public void UpdateSegmentPurpose(long segmentId, string purpose)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE usage_segments SET purpose=$p WHERE id=$id";
        cmd.Parameters.AddWithValue("$p", purpose);
        cmd.Parameters.AddWithValue("$id", segmentId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>删除单个片段（用于丢弃零长度片段）。</summary>
    public void DeleteSegment(long segmentId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM usage_segments WHERE id=$id";
        cmd.Parameters.AddWithValue("$id", segmentId);
        cmd.ExecuteNonQuery();
    }

    public void UpdateSegmentEnd(long segmentId, DateTime endUtc, string? title = null, string? site = null)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        var sets = new List<string> { "end_ts=$e" };
        if (title != null) sets.Add("title=$t");
        if (site != null) sets.Add("site=$si");

        cmd.CommandText = $"UPDATE usage_segments SET {string.Join(", ", sets)} WHERE id=$id";
        cmd.Parameters.AddWithValue("$e", ToIso(endUtc));
        cmd.Parameters.AddWithValue("$id", segmentId);
        if (title != null) cmd.Parameters.AddWithValue("$t", title);
        if (site != null) cmd.Parameters.AddWithValue("$si", site);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 把过短的片段并入相邻的同应用同类型片段，压缩碎片化。
    /// 判定条件：片段时长 &lt; <paramref name="maxSeconds"/>，且与前一保留片段的应用、
    /// 类型一致、间隔不超过 2 秒——说明只是同一次使用被标题变化切碎。
    /// 合并后前一保留片段延长到较晚的结束时间；无法并入的零长度片段直接删除。
    /// </summary>
    /// <param name="beforeUtc">
    /// 只处理结束时间早于该时刻的片段。采集期间传 <c>now-2min</c>，
    /// 避免把正在计时的开放片段合并掉（它的 RowId 还被 WatcherService 持有）。
    /// </param>
    /// <returns>处理的片段数。</returns>
    public int MergeShortSegments(double maxSeconds, DateTime fromUtc, DateTime? beforeUtc = null)
    {
        using var c = Open();
        var until = beforeUtc ?? DateTime.MaxValue;

        var rows = new List<(long Id, int AppId, string Kind, string Start, string End, string Title, string Site)>();
        using (var q = c.CreateCommand())
        {
            q.CommandText = """
                SELECT id, app_id, kind, start_ts, end_ts, COALESCE(title, ''), COALESCE(site, '')
                FROM usage_segments
                WHERE end_ts >= $f AND end_ts <= $u
                ORDER BY start_ts, id
                """;
            q.Parameters.AddWithValue("$f", ToIso(fromUtc));
            q.Parameters.AddWithValue("$u", ToIso(until));
            using var r = q.ExecuteReader();
            while (r.Read())
                rows.Add((r.GetInt64(0), r.GetInt32(1), r.GetString(2),
                    r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6)));
        }
        if (rows.Count == 0) return 0;

        var gap = TimeSpan.FromSeconds(2);
        var touched = 0;

        using var tx = c.BeginTransaction();

        // kept 始终指向"当前仍然存活、可作为合并目标"的最后一段
        var kept = 0;
        for (var i = 1; i < rows.Count; i++)
        {
            var cur = rows[i];
            var curStart = ParseIso(cur.Start);
            var curEnd = ParseIso(cur.End);

            // 够长的片段自成一段，直接作为新的合并目标
            if ((curEnd - curStart).TotalSeconds >= maxSeconds) { kept = i; continue; }

            var prev = rows[kept];
            if (prev.AppId != cur.AppId || prev.Kind != cur.Kind) { kept = i; continue; }

            var prevEnd = ParseIso(prev.End);
            if (curStart - prevEnd > gap) { kept = i; continue; }

            // 并入前一段：延长结束时间，标题/地址保留更"有内容"的那个
            var newEnd = curEnd > prevEnd ? curEnd : prevEnd;
            var keepTitle = string.IsNullOrEmpty(prev.Title) ? cur.Title : prev.Title;
            var keepSite = string.IsNullOrEmpty(prev.Site) ? cur.Site : prev.Site;

            using (var u = c.CreateCommand())
            {
                u.Transaction = tx;
                u.CommandText = "UPDATE usage_segments SET end_ts=$e, title=$t, site=$s WHERE id=$id";
                u.Parameters.AddWithValue("$e", ToIso(newEnd));
                u.Parameters.AddWithValue("$t", keepTitle);
                u.Parameters.AddWithValue("$s", keepSite);
                u.Parameters.AddWithValue("$id", prev.Id);
                u.ExecuteNonQuery();
            }
            using (var d = c.CreateCommand())
            {
                d.Transaction = tx;
                d.CommandText = "DELETE FROM usage_segments WHERE id=$id";
                d.Parameters.AddWithValue("$id", cur.Id);
                d.ExecuteNonQuery();
            }

            rows[kept] = prev with { End = ToIso(newEnd), Title = keepTitle, Site = keepSite };
            touched++;
        }

        // 清掉无法并入的零长度片段（读取失败/瞬时切换留下的空行）
        using (var z = c.CreateCommand())
        {
            z.Transaction = tx;
            z.CommandText = """
                DELETE FROM usage_segments
                WHERE end_ts >= $f AND end_ts <= $u
                  AND (julianday(end_ts) - julianday(start_ts)) * 86400 < 0.5
                """;
            z.Parameters.AddWithValue("$f", ToIso(fromUtc));
            z.Parameters.AddWithValue("$u", ToIso(until));
            touched += z.ExecuteNonQuery();
        }

        tx.Commit();
        return touched;
    }

    public void AddSessionEvent(string type, string note = "")
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO session_events (type, ts, note) VALUES ($t,$s,$n)";
        cmd.Parameters.AddWithValue("$t", type);
        cmd.Parameters.AddWithValue("$s", ToIso(DateTime.UtcNow));
        cmd.Parameters.AddWithValue("$n", note);
        cmd.ExecuteNonQuery();
    }

    /// <summary>记录会话事件，可指定 UTC 时间戳（用于回溯真实开机时间）。</summary>
    public void AddSessionEventAt(string type, DateTime tsUtc, string note = "")
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "INSERT INTO session_events (type, ts, note) VALUES ($t,$s,$n)";
        cmd.Parameters.AddWithValue("$t", type);
        cmd.Parameters.AddWithValue("$s", ToIso(tsUtc));
        cmd.Parameters.AddWithValue("$n", note);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 修补亮屏时间线：把「连续两个 display_on 之间没有 display_off」的悬空段收口。
    ///
    /// 关机、断电、任务管理器强杀时进程被直接终止，熄灭事件没机会写。
    /// 连续两条 display_on 之间夹着的空档就会被当成「一直亮着」——
    /// 实际踩到过：一天报 14 小时亮屏，中间 11.8 小时机器是关着的。
    ///
    /// 收口时间取该段内「最后一次有活动证据」的时刻（最后一条片段结束）。
    /// 只有空档超过 <paramref name="minGap"/> 才补，避免正常秒级重启被切成碎片。
    /// </summary>
    /// <returns>补写的 display_off 条数。</returns>
    public int RepairDisplaySpans(TimeSpan? minGap = null)
    {
        var gap = minGap ?? TimeSpan.FromMinutes(30);

        var events = new List<(bool On, DateTime Ts)>();
        using (var c = Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = "SELECT type, ts FROM session_events " +
                              "WHERE type IN ('display_on','display_off') ORDER BY ts";
            using var r = cmd.ExecuteReader();
            while (r.Read()) events.Add((r.GetString(0) == "display_on", ParseIso(r.GetString(1))));
        }
        if (events.Count == 0) return 0;

        var repairs = new List<DateTime>();
        DateTime? openAt = null;

        foreach (var (on, ts) in events)
        {
            if (!on)
            {
                openAt = null;
                continue;
            }

            if (openAt is { } start)
            {
                // 上一段没等到 off 就来了新的 on：上一段是悬空的
                var end = LastActivityIn(start, ts);
                if (ts - end >= gap) repairs.Add(end);
            }
            openAt = ts;
        }

        // 末尾还开着：距最后活动超过阈值就收口（进程已被杀掉，之后不可能有数据）
        if (openAt is { } tail)
        {
            var now = DateTime.UtcNow;
            var end = LastActivityIn(tail, now);
            if (now - end >= gap) repairs.Add(end);
        }

        foreach (var at in repairs) AddSessionEventAt("display_off", at, "repaired");
        return repairs.Count;
    }

    /// <summary>区间内最后一次有采集活动的时刻；没有则返回区间起点。</summary>
    private DateTime LastActivityIn(DateTime fromUtc, DateTime toUtc)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT MAX(end_ts) FROM usage_segments WHERE end_ts >= $f AND end_ts <= $t";
        cmd.Parameters.AddWithValue("$f", ToIso(fromUtc));
        cmd.Parameters.AddWithValue("$t", ToIso(toUtc));
        var raw = cmd.ExecuteScalar() as string;
        if (string.IsNullOrEmpty(raw)) return fromUtc;
        var d = ParseIso(raw);
        return d > fromUtc ? d : fromUtc;
    }

    /// <summary>返回最近一次 boot 事件的 UTC 时间；没有则 null。</summary>
    public DateTime? GetLastBootTime()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT MAX(ts) FROM session_events WHERE type='boot'";
        var r = cmd.ExecuteScalar();
        if (r is null || r is DBNull) return null;
        return ParseIso(r.ToString()!);
    }

    /// <summary>
    /// 统计区间内「屏幕亮着」的累计秒数。
    /// 依据 display_on / display_off 事件配对得出：从一条 display_on 起算，
    /// 到下一条 display_off 为止，与查询区间求交后累加。
    /// 区间起点之前若没有事件，视为灭屏（那时候程序还没在跑，无从得知）。
    /// </summary>
    public double GetDisplayOnSeconds(DateTime fromUtc, DateTime toUtc)
    {
        // 查询区间通常是"整天"（DayRange 给到次日 0 点），必须截到现在，
        // 否则最后一段会一直算到今晚 24 点，数字凭空多出几个小时。
        var now = DateTime.UtcNow;
        if (toUtc > now) toUtc = now;
        if (toUtc <= fromUtc) return 0;

        var events = new List<(bool On, DateTime Ts)>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT type, ts FROM session_events
            WHERE type IN ('display_on','display_off') AND ts <= $t
            ORDER BY ts
            """;
        cmd.Parameters.AddWithValue("$t", ToIso(toUtc));
        using (var r = cmd.ExecuteReader())
            while (r.Read())
                events.Add((r.GetString(0) == "display_on", ParseIso(r.GetString(1))));

        var total = 0.0;
        var on = false;
        var cursor = DateTime.MinValue;

        foreach (var (isOn, ts) in events)
        {
            if (on && ts > fromUtc)
            {
                var start = cursor > fromUtc ? cursor : fromUtc;
                var end = ts < toUtc ? ts : toUtc;
                if (end > start) total += (end - start).TotalSeconds;
            }
            on = isOn;
            cursor = ts;
        }

        // 最后一条是"亮"且还没遇到关闭事件：算到区间末尾
        if (on)
        {
            var start = cursor > fromUtc ? cursor : fromUtc;
            if (toUtc > start) total += (toUtc - start).TotalSeconds;
        }

        return total;
    }

    public string? GetSetting(string key, string? fallback = null)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        return cmd.ExecuteScalar() as string ?? fallback;
    }

    public void SetSetting(string key, string value)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO settings (key, value) VALUES ($k,$v)
            ON CONFLICT(key) DO UPDATE SET value=excluded.value
            """;
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 按应用聚合时间范围内的前台/音频时长。总时长为前台区间 ∪ 音频区间的并集
    /// （同一应用在前台播放声音时不重复计算）。
    /// </summary>
    public List<AppUsageRow> QueryAppUsage(DateTime fromUtc, DateTime toUtc)
    {
        // 1) 应用信息索引
        var appInfo = new Dictionary<int, AppInfoRow>();
        using (var c = Open())
        using (var infoCmd = c.CreateCommand())
        {
            infoCmd.CommandText = "SELECT id, app_name, process_name, category, ignored, merged_into FROM apps";
            using var ir = infoCmd.ExecuteReader();
            while (ir.Read())
                appInfo[ir.GetInt32(0)] = new AppInfoRow(
                    ir.GetString(1), ir.GetString(2), ir.GetString(3), ir.GetInt32(4), ir.GetInt32(5));
        }

        // 2) 范围内片段，归属到合并后的应用
        var buckets = new Dictionary<int, (List<(DateTime S, DateTime E)> Fg, List<(DateTime S, DateTime E)> Au)>();
        using (var c = Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                SELECT s.app_id, s.kind, s.start_ts, s.end_ts
                FROM usage_segments s
                WHERE s.end_ts > $f AND s.start_ts < $t
                """;
            cmd.Parameters.AddWithValue("$f", ToIso(fromUtc));
            cmd.Parameters.AddWithValue("$t", ToIso(toUtc));
            using var sr = cmd.ExecuteReader();
            while (sr.Read())
            {
                var srcId = sr.GetInt32(0);
                if (!appInfo.TryGetValue(srcId, out var src)) continue;

                var resolvedId = src.MergedInto != 0 && appInfo.ContainsKey(src.MergedInto)
                    ? src.MergedInto : srcId;
                var target = appInfo[resolvedId];

                // 源或目标任一被忽略则跳过
                if (src.Ignored != 0 || target.Ignored != 0) continue;

                // 与查询范围求交：跨零点/跨区间的片段按范围裁剪，
                // 否则整段会被算给起始日，导致当天统计偏大、次日偏小
                var start = ParseIso(sr.GetString(2));
                var end = ParseIso(sr.GetString(3));
                if (start < fromUtc) start = fromUtc;
                if (end > toUtc) end = toUtc;
                if (end <= start) continue;

                var interval = (start, end);
                if (!buckets.TryGetValue(resolvedId, out var b))
                {
                    b = (new List<(DateTime, DateTime)>(), new List<(DateTime, DateTime)>());
                    buckets[resolvedId] = b;
                }
                if (sr.GetString(1) == "foreground") b.Fg.Add(interval);
                else b.Au.Add(interval);
            }
        }

        // 3) 聚合
        var rows = new List<AppUsageRow>();
        foreach (var (resolvedId, b) in buckets)
        {
            var info = appInfo[resolvedId];
            var fg = b.Fg.Sum(x => (x.E - x.S).TotalSeconds);
            var au = b.Au.Sum(x => (x.E - x.S).TotalSeconds);
            var total = StatsService.UnionSeconds(b.Fg.Concat(b.Au).ToList());
            rows.Add(new AppUsageRow(resolvedId, info.AppName, info.ProcessName,
                info.Category, fg, au, total));
        }
        return rows;
    }

    /// <summary>查询单个应用按本地日期拆分的使用时长，前台与音频重叠只计算一次。</summary>
    public List<AppDailyUsageRow> QueryAppDailyUsage(int appId, DateTime fromUtc, DateTime toUtc)
    {
        using var c = Open();
        var map = LoadAppInfo(c);
        if (!map.TryGetValue(appId, out var src)) return [];
        var effective = ResolveEffectiveAppId(map, appId);
        if (!map.TryGetValue(effective, out var target) || src.Ignored != 0 || target.Ignored != 0)
            return [];

        var ids = map.Keys.Where(id => ResolveEffectiveAppId(map, id) == effective).ToList();
        if (ids.Count == 0) return [];

        var buckets = new Dictionary<DateTime, List<(DateTime Start, DateTime End)>>();
        using var cmd = c.CreateCommand();
        var inList = string.Join(",", ids.Select((_, i) => $"$a{i}"));
        cmd.CommandText = $"""
            SELECT start_ts, end_ts FROM usage_segments
            WHERE app_id IN ({inList}) AND end_ts > $f AND start_ts < $t
            """;
        for (var i = 0; i < ids.Count; i++) cmd.Parameters.AddWithValue($"$a{i}", ids[i]);
        cmd.Parameters.AddWithValue("$f", ToIso(fromUtc));
        cmd.Parameters.AddWithValue("$t", ToIso(toUtc));
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var start = ParseIso(r.GetString(0));
            var end = ParseIso(r.GetString(1));
            if (start < fromUtc) start = fromUtc;
            if (end > toUtc) end = toUtc;
            while (start < end)
            {
                var localDay = start.ToLocalTime().Date;
                var nextDayUtc = localDay.AddDays(1).ToUniversalTime();
                var partEnd = end < nextDayUtc ? end : nextDayUtc;
                if (!buckets.TryGetValue(localDay, out var intervals))
                    buckets[localDay] = intervals = new List<(DateTime, DateTime)>();
                intervals.Add((start, partEnd));
                start = partEnd;
            }
        }

        return buckets.OrderByDescending(x => x.Key)
            .Select(x => new AppDailyUsageRow(x.Key.ToString("yyyy-MM-dd"), StatsService.UnionSeconds(x.Value)))
            .Where(x => x.Seconds > 0)
            .ToList();
    }

    /// <summary>
    /// 查询给定应用中，前台使用达到阈值的 id。
    /// <para>
    /// 只针对传入的 id 查（走 idx_seg_app），而不是全库 GROUP BY —— 收件箱只关心那几十个
    /// 未分类应用，没必要扫全表。SUM 不去重（重叠片段会重复计），这里只用于
    /// 「这个应用到底有没有被用过」的判断，对阈值判断没有影响。
    /// </para>
    /// </summary>
    public HashSet<int> QueryAppsWithMinForeground(IReadOnlyCollection<int> appIds,
        DateTime fromUtc, DateTime toUtc, double minSeconds)
    {
        var set = new HashSet<int>();
        if (appIds.Count == 0) return set;

        using var c = Open();
        var ids = appIds.ToList();
        // SQLite 参数数量有上限，分批查
        const int batchSize = 500;
        for (var offset = 0; offset < ids.Count; offset += batchSize)
        {
            var batch = ids.Skip(offset).Take(batchSize).ToList();
            using var cmd = c.CreateCommand();
            var inList = string.Join(",", batch.Select((_, i) => $"$a{i}"));
            cmd.CommandText = $"""
                SELECT app_id
                FROM usage_segments
                WHERE app_id IN ({inList})
                  AND day >= $dayFrom
                  AND kind = 'foreground'
                  AND end_ts > $f AND start_ts < $t
                GROUP BY app_id
                HAVING SUM((julianday(end_ts) - julianday(start_ts)) * 86400.0) >= $min
                """;
            for (var i = 0; i < batch.Count; i++) cmd.Parameters.AddWithValue($"$a{i}", batch[i]);
            cmd.Parameters.AddWithValue("$dayFrom", fromUtc.ToLocalTime().ToString("yyyy-MM-dd"));
            cmd.Parameters.AddWithValue("$f", ToIso(fromUtc));
            cmd.Parameters.AddWithValue("$t", ToIso(toUtc));
            cmd.Parameters.AddWithValue("$min", minSeconds);
            using var r = cmd.ExecuteReader();
            while (r.Read()) set.Add(r.GetInt32(0));
        }
        return set;
    }

    private record AppInfoRow(string AppName, string ProcessName, string Category, int Ignored, int MergedInto);

    /// <summary>加载应用合并/忽略元信息（id → 信息）。</summary>
    private static Dictionary<int, AppInfoRow> LoadAppInfo(SqliteConnection c)
    {
        var map = new Dictionary<int, AppInfoRow>();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, app_name, process_name, category, ignored, merged_into FROM apps";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            map[r.GetInt32(0)] = new AppInfoRow(
                r.GetString(1), r.GetString(2), r.GetString(3), r.GetInt32(4), r.GetInt32(5));
        return map;
    }

    /// <summary>
    /// 沿 merged_into 链解析有效 app_id（最多 5 层，遇环或缺失即停）。
    /// 查询与回填必须共用本函数，否则合并后写入的片段在视图中不可见。
    /// </summary>
    public int ResolveEffectiveAppId(int appId)
    {
        using var c = Open();
        return ResolveEffectiveAppId(LoadAppInfo(c), appId);
    }

    private static int ResolveEffectiveAppId(Dictionary<int, AppInfoRow> map, int appId)
    {
        var cur = appId;
        for (var depth = 0; depth < 5; depth++)
        {
            if (!map.TryGetValue(cur, out var info)) break;
            var next = info.MergedInto;
            if (next == 0 || next == cur || !map.ContainsKey(next)) break;
            cur = next;
        }
        return cur;
    }

    /// <summary>
    /// 按用途聚合时间范围时长（前台 ∪ 音频并集口径，不重复）。
    /// 片段用途优先取显式标注，否则按所属应用分类映射。
    /// </summary>
    public List<(string Purpose, double TotalSeconds)> QueryPurposeUsage(DateTime fromUtc, DateTime toUtc)
    {
        var appInfo = new Dictionary<int, AppInfoRow>();
        using (var c = Open())
        using (var infoCmd = c.CreateCommand())
        {
            infoCmd.CommandText = "SELECT id, app_name, process_name, category, ignored, merged_into FROM apps";
            using var ir = infoCmd.ExecuteReader();
            while (ir.Read())
                appInfo[ir.GetInt32(0)] = new AppInfoRow(
                    ir.GetString(1), ir.GetString(2), ir.GetString(3), ir.GetInt32(4), ir.GetInt32(5));
        }

        var byPurpose = new Dictionary<string, List<(DateTime S, DateTime E)>>();
        using (var c = Open())
        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                SELECT s.app_id, s.kind, s.start_ts, s.end_ts, s.purpose
                FROM usage_segments s
                WHERE s.end_ts > $f AND s.start_ts < $t
                """;
            cmd.Parameters.AddWithValue("$f", ToIso(fromUtc));
            cmd.Parameters.AddWithValue("$t", ToIso(toUtc));
            using var sr = cmd.ExecuteReader();
            while (sr.Read())
            {
                var srcId = sr.GetInt32(0);
                if (!appInfo.TryGetValue(srcId, out var src)) continue;
                var resolvedId = src.MergedInto != 0 && appInfo.ContainsKey(src.MergedInto)
                    ? src.MergedInto : srcId;
                var target = appInfo[resolvedId];
                if (src.Ignored != 0 || target.Ignored != 0) continue;

                var explicitPurpose = sr.GetString(4);
                var purpose = PurposeService.Resolve(explicitPurpose, target.Category);

                var start = ParseIso(sr.GetString(2));
                var end = ParseIso(sr.GetString(3));
                if (start < fromUtc) start = fromUtc;
                if (end > toUtc) end = toUtc;
                if (end <= start) continue;

                var interval = (start, end);
                if (!byPurpose.TryGetValue(purpose, out var list))
                {
                    list = new List<(DateTime, DateTime)>();
                    byPurpose[purpose] = list;
                }
                list.Add(interval);
            }
        }

        return byPurpose
            .Select(kv => (kv.Key, StatsService.UnionSeconds(kv.Value)))
            .OrderByDescending(x => x.Item2)
            .ToList();
    }

    /// <summary>时间线（前台片段，含标题）。跨零点的片段按当天范围裁剪。</summary>
    public List<TimelineRow> QueryTimeline(DateTime localDay)
    {
        var rows = new List<TimelineRow>();
        var (fromUtc, toUtc) = StatsService.DayRange(localDay);

        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT MAX(s.start_ts, $f), MIN(s.end_ts, $t), s.kind,
                   COALESCE(ca.app_name, a.app_name), s.title,
                   (julianday(MIN(s.end_ts, $t)) - julianday(MAX(s.start_ts, $f))) * 86400,
                   COALESCE(ca.category, a.category)
            FROM usage_segments s
            JOIN apps a ON a.id = s.app_id
            LEFT JOIN apps ca ON ca.id = a.merged_into
            WHERE s.end_ts > $f AND s.start_ts < $t
              AND a.ignored = 0
              AND (a.merged_into = 0 OR ca.ignored = 0)
            ORDER BY s.start_ts
            """;
        cmd.Parameters.AddWithValue("$f", ToIso(fromUtc));
        cmd.Parameters.AddWithValue("$t", ToIso(toUtc));
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            rows.Add(new TimelineRow(r.GetString(0), r.GetString(1), r.GetString(2),
                r.GetString(3), r.GetString(4), r.GetDouble(5), r.GetString(6)));
        }
        return rows;
    }

    /// <summary>
    /// 浏览器页面使用明细：按（地址, 标题, 原始地址）分组返回前台时长。
    /// 走 merged_into 解析：指定应用的所有被合并条目都计入同一行，避免合并后漏算。
    /// 地址为空的片段（旧版本记录或读取失败）也会带上标题，便于上层按标题聚合，减少"未知"。
    /// </summary>
    public List<PageUsageRow> QueryPageUsage(int appId, DateTime fromUtc, DateTime toUtc)
    {
        var list = new List<PageUsageRow>();
        using var c = Open();
        var map = LoadAppInfo(c);
        if (!map.ContainsKey(appId)) return list;

        // 回填与查询共用同一个有效 app_id
        var effective = ResolveEffectiveAppId(map, appId);
        if (map[effective].Ignored != 0) return list;

        var ids = map.Keys.Where(id => ResolveEffectiveAppId(map, id) == effective).ToList();
        if (ids.Count == 0) return list;

        using var cmd = c.CreateCommand();
        var inList = string.Join(",", ids.Select((_, i) => $"$a{i}"));
        cmd.CommandText = $"""
            SELECT COALESCE(s.site, ''), COALESCE(s.title, ''), COALESCE(s.url, ''),
                   SUM((julianday(MIN(s.end_ts, $t)) - julianday(MAX(s.start_ts, $f))) * 86400)
            FROM usage_segments s
            WHERE s.app_id IN ({inList}) AND s.kind = 'foreground'
              AND s.end_ts > $f AND s.start_ts < $t
            GROUP BY 1, 2, 3
            """;
        for (var i = 0; i < ids.Count; i++) cmd.Parameters.AddWithValue($"$a{i}", ids[i]);
        cmd.Parameters.AddWithValue("$f", ToIso(fromUtc));
        cmd.Parameters.AddWithValue("$t", ToIso(toUtc));
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new PageUsageRow(r.GetString(0), r.GetString(1), r.GetString(2), r.GetDouble(3)));
        return list;
    }

    /// <summary>
    /// 全部浏览器的网页明细（近 N 天），按（有效应用, 地址, 标题, 原始地址）聚合前台时长。
    /// 地址为空的片段也会返回（带标题），由上层按标题归入「未识别页面」。
    /// </summary>
    public List<WebPageRow> QueryWebPages(DateTime fromUtc, DateTime toUtc)
    {
        var list = new List<WebPageRow>();
        using var c = Open();
        var map = LoadAppInfo(c);
        var rows = new List<(int AppId, string Site, string Title, string Url, double Seconds)>();

        using (var cmd = c.CreateCommand())
        {
            cmd.CommandText = """
                SELECT s.app_id, COALESCE(s.site, ''), COALESCE(s.title, ''), COALESCE(s.url, ''),
                       SUM((julianday(MIN(s.end_ts, $t)) - julianday(MAX(s.start_ts, $f))) * 86400)
                FROM usage_segments s
                WHERE s.kind = 'foreground'
                  AND s.end_ts > $f AND s.start_ts < $t
                  AND (COALESCE(s.site, '') <> '' OR COALESCE(s.title, '') <> '')
                GROUP BY 1, 2, 3, 4
                """;
            cmd.Parameters.AddWithValue("$f", ToIso(fromUtc));
            cmd.Parameters.AddWithValue("$t", ToIso(toUtc));
            using var r = cmd.ExecuteReader();
            while (r.Read())
                rows.Add((r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetDouble(4)));
        }

        var merged = new Dictionary<(int, string, string, string), double>();
        foreach (var row in rows)
        {
            if (!map.TryGetValue(row.AppId, out var src)) continue;
            var effective = ResolveEffectiveAppId(map, row.AppId);
            if (src.Ignored != 0 || map[effective].Ignored != 0) continue;
            if (!BrowserCatalog.IsBrowser(map[effective].ProcessName)) continue;

            var key = (effective, row.Site, row.Title, row.Url);
            merged.TryGetValue(key, out var sec);
            merged[key] = sec + row.Seconds;
        }

        foreach (var ((appId, site, title, url), seconds) in merged)
            list.Add(new WebPageRow(site, title, url, map[appId].AppName,
                map[appId].ProcessName, seconds));
        return list;
    }

    /// <summary>查询浏览器网页前台片段，供网页页绘制时间线。</summary>
    public List<WebTimelineRow> QueryWebTimeline(DateTime fromUtc, DateTime toUtc)
    {
        var list = new List<WebTimelineRow>();
        using var c = Open();
        var map = LoadAppInfo(c);
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT s.app_id, MAX(s.start_ts, $f), MIN(s.end_ts, $t),
                   COALESCE(s.site, ''), COALESCE(s.title, ''), COALESCE(s.url, '')
            FROM usage_segments s
            WHERE s.kind = 'foreground'
              AND s.end_ts > $f AND s.start_ts < $t
              AND (COALESCE(s.site, '') <> '' OR COALESCE(s.title, '') <> '')
            ORDER BY s.start_ts
            """;
        cmd.Parameters.AddWithValue("$f", ToIso(fromUtc));
        cmd.Parameters.AddWithValue("$t", ToIso(toUtc));
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (!map.TryGetValue(r.GetInt32(0), out var src)) continue;
            var effective = ResolveEffectiveAppId(map, r.GetInt32(0));
            if (!map.TryGetValue(effective, out var app) || src.Ignored != 0 || app.Ignored != 0 ||
                !BrowserCatalog.IsBrowser(app.ProcessName)) continue;

            var start = ParseIso(r.GetString(1));
            var end = ParseIso(r.GetString(2));
            if (end <= start) continue;
            list.Add(new WebTimelineRow(start, end, r.GetString(3), r.GetString(4), r.GetString(5),
                app.AppName, app.ProcessName));
        }
        return list;
    }

    /// <summary>
    /// 待回填的浏览器前台片段：没有 site 的片段（含无法识别进程名，由调用方过滤）。
    /// </summary>
    public List<BackfillCandidateRow> QueryBackfillCandidates(DateTime fromUtc, DateTime toUtc)
    {
        var list = new List<BackfillCandidateRow>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT s.id, s.app_id, s.start_ts, s.end_ts, COALESCE(s.title, '')
            FROM usage_segments s
            WHERE s.kind = 'foreground'
              AND COALESCE(s.site, '') = ''
              AND s.end_ts > $f AND s.start_ts < $t
            """;
        cmd.Parameters.AddWithValue("$f", ToIso(fromUtc));
        cmd.Parameters.AddWithValue("$t", ToIso(toUtc));
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new BackfillCandidateRow(r.GetInt64(0), r.GetInt32(1),
                ParseIso(r.GetString(2)), ParseIso(r.GetString(3)), r.GetString(4)));
        return list;
    }

    /// <summary>写入回填结果。幂等：仅当该片段仍无 site 时更新，避免覆盖实时采集值。</summary>
    public int BackfillPage(long segmentId, string site, string url)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            UPDATE usage_segments SET site=$si, url=$u
            WHERE id=$id AND COALESCE(site, '') = ''
            """;
        cmd.Parameters.AddWithValue("$si", site);
        cmd.Parameters.AddWithValue("$u", url);
        cmd.Parameters.AddWithValue("$id", segmentId);
        return cmd.ExecuteNonQuery();
    }

    /// <summary>每日全部片段（用于计算每日总使用时长并集）。</summary>
    public List<(DateTime Start, DateTime End, int AppId, string Kind)> QuerySegments(DateTime fromUtc, DateTime toUtc)
    {
        var list = new List<(DateTime, DateTime, int, string)>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT s.start_ts, s.end_ts, COALESCE(NULLIF(a.merged_into,0), s.app_id), s.kind
            FROM usage_segments s
            JOIN apps a ON a.id = s.app_id
            WHERE s.end_ts > $f AND s.start_ts < $t
              AND COALESCE(
                (SELECT ig.ignored FROM apps ig WHERE ig.id = NULLIF(a.merged_into,0)), a.ignored) = 0
            """;
        cmd.Parameters.AddWithValue("$f", ToIso(fromUtc));
        cmd.Parameters.AddWithValue("$t", ToIso(toUtc));
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var start = ParseIso(r.GetString(0));
            var end = ParseIso(r.GetString(1));
            if (start < fromUtc) start = fromUtc;
            if (end > toUtc) end = toUtc;
            if (end <= start) continue;
            list.Add((start, end, r.GetInt32(2), r.GetString(3)));
        }
        return list;
    }

    public List<SessionEventRow> QuerySessionEvents(DateTime fromUtc, DateTime toUtc)
    {
        var list = new List<SessionEventRow>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, type, ts, note FROM session_events WHERE ts >= $f AND ts < $t ORDER BY ts";
        cmd.Parameters.AddWithValue("$f", ToIso(fromUtc));
        cmd.Parameters.AddWithValue("$t", ToIso(toUtc));
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new SessionEventRow(r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3)));
        return list;
    }

    public List<(int Id, string Name, string Category, string ProcessName, int Ignored, int MergedInto,
        string ExePath, string DefaultPurpose, int SegmentCount, string IconSource)> GetAllApps()
    {
        var list = new List<(int, string, string, string, int, int, string, string, int, string)>();
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT a.id, a.app_name, a.category, a.process_name, a.ignored, a.merged_into,
                   a.exe_path, a.default_purpose,
                   (SELECT COUNT(*) FROM usage_segments s WHERE s.app_id = a.id),
                   COALESCE(a.icon_source, '')
            FROM apps a
            ORDER BY a.app_name
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add((r.GetInt32(0), r.GetString(1), r.GetString(2), r.GetString(3),
                r.GetInt32(4), r.GetInt32(5), r.GetString(6), r.GetString(7),
                r.GetInt32(8), r.GetString(9)));
        return list;
    }

    public void SetAppCategory(int id, string category)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE apps SET category=$ca, category_manual=1 WHERE id=$id";
        cmd.Parameters.AddWithValue("$ca", category);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void SetAppIgnored(int id, bool ignored)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        // ignored_manual：这是用户亲手设的，之后自动噪声忽略不再覆盖它
        cmd.CommandText = "UPDATE apps SET ignored=$ig, ignored_manual=1 WHERE id=$id";
        cmd.Parameters.AddWithValue("$ig", ignored ? 1 : 0);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    public void MergeApp(int id, int targetId)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE apps SET merged_into=$t WHERE id=$id";
        cmd.Parameters.AddWithValue("$t", targetId);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>删除指定时间之前的片段标题（保留时长，清空标题）。</summary>
    public void ScrubTitles(DateTime beforeUtc)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE usage_segments SET title='' WHERE start_ts < $b";
        cmd.Parameters.AddWithValue("$b", ToIso(beforeUtc));
        cmd.ExecuteNonQuery();
    }

    public void DeleteSegmentsBefore(DateTime beforeUtc)
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM usage_segments WHERE start_ts < $b";
        cmd.Parameters.AddWithValue("$b", ToIso(beforeUtc));
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 生成全部片段的 CSV 文本（UTF-8 带 BOM，Excel 双击即可正确识别中文）。
    /// 标题按保留期已被清洗的记录会导出空标题，时长仍然完整。
    /// </summary>
    public (int Rows, string Csv) BuildSegmentsCsv()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT s.start_ts, s.end_ts, s.kind, a.app_name, a.category, COALESCE(s.purpose, ''),
                   COALESCE(s.title, ''), COALESCE(s.site, ''), COALESCE(s.url, '')
            FROM usage_segments s
            JOIN apps a ON a.id = s.app_id
            ORDER BY s.start_ts
            """;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("开始时间,结束时间,类型,应用,分类,用途,窗口标题,网页,地址");
        var rows = 0;

        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var start = ParseIso(r.GetString(0)).ToLocalTime();
            var end = ParseIso(r.GetString(1)).ToLocalTime();
            // 用途按报表同一口径解析：优先显式标注，否则由分类推断
            var purpose = PurposeService.Resolve(r.GetString(5), r.GetString(4));
            sb.Append(Csv(start.ToString("yyyy-MM-dd HH:mm:ss"))).Append(',')
              .Append(Csv(end.ToString("yyyy-MM-dd HH:mm:ss"))).Append(',')
              .Append(Csv(r.GetString(2) == "foreground" ? "前台" : "音频")).Append(',')
              .Append(Csv(r.GetString(3))).Append(',')
              .Append(Csv(r.GetString(4))).Append(',')
              .Append(Csv(PurposeService.Display(purpose))).Append(',')
              .Append(Csv(r.GetString(6))).Append(',')
              .Append(Csv(r.GetString(7))).Append(',')
              .Append(Csv(r.GetString(8)))
              .AppendLine();
            rows++;
        }

        return (rows, sb.ToString());
    }

    /// <summary>导出全部片段为 CSV 文件。返回导出行数。</summary>
    public int ExportSegmentsCsv(string path)
    {
        var (rows, csv) = BuildSegmentsCsv();
        File.WriteAllText(path, csv, new System.Text.UTF8Encoding(true));
        return rows;
    }

    /// <summary>
    /// 生成全部片段的 JSON（字段与 CSV 对齐，额外带秒数与进程名），
    /// 便于脚本处理或二次分析。中文不转义，直接可读。
    /// </summary>
    public (int Rows, string Json) BuildSegmentsJson()
    {
        using var c = Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT s.start_ts, s.end_ts, s.kind, a.app_name, a.process_name, a.category,
                   COALESCE(s.purpose, ''), COALESCE(s.title, ''), COALESCE(s.site, ''), COALESCE(s.url, '')
            FROM usage_segments s
            JOIN apps a ON a.id = s.app_id
            ORDER BY s.start_ts
            """;

        var items = new List<object>();
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var start = ParseIso(r.GetString(0)).ToLocalTime();
            var end = ParseIso(r.GetString(1)).ToLocalTime();
            var category = r.GetString(5);
            items.Add(new
            {
                start = start.ToString("yyyy-MM-dd HH:mm:ss"),
                end = end.ToString("yyyy-MM-dd HH:mm:ss"),
                seconds = Math.Round((end - start).TotalSeconds, 1),
                kind = r.GetString(2),
                app = r.GetString(3),
                process = r.GetString(4),
                category,
                purpose = PurposeService.Resolve(r.GetString(6), category),
                title = r.GetString(7),
                site = r.GetString(8),
                url = r.GetString(9),
            });
        }

        var payload = new
        {
            exportedAt = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:sszzz"),
            count = items.Count,
            segments = items,
        };

        var json = System.Text.Json.JsonSerializer.Serialize(payload,
            new System.Text.Json.JsonSerializerOptions
            {
                WriteIndented = true,
                // 不把中文转成 \uXXXX，导出的文件人也要能读
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            });
        return (items.Count, json);
    }

    private static string Csv(string? value)
    {
        var v = (value ?? "").Replace('\r', ' ').Replace('\n', ' ');
        return v.Contains(',') || v.Contains('"')
            ? "\"" + v.Replace("\"", "\"\"") + "\""
            : v;
    }

    /// <summary>把数据库复制到指定路径；先 checkpoint，确保 WAL 里的最新数据一并写入。</summary>
    public void BackupTo(string path)
    {
        using (var c = Open())
        using (var ck = c.CreateCommand())
        {
            ck.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
            ck.ExecuteNonQuery();
        }
        File.Copy(DbPath, path, overwrite: true);
    }

    public static string ToIso(DateTime t) => t.ToString("yyyy-MM-ddTHH:mm:ss.fffZ");
    public static DateTime ParseIso(string s) => DateTime.Parse(s, null, System.Globalization.DateTimeStyles.RoundtripKind);
}
