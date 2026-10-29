# ScreenTime 版本记录

本文件记录每个版本号的改动，随仓库一起发布；GitHub Release 的说明也从这里整理。

---

## v1.2.1（2026-10-30）双进程接缝修复 + 收件箱查询提速

**这一版没有新功能，全部是 v1.2.0 双进程拆分留下的接缝问题。**

- **开机自启关不掉（重要）**。根因：`schtasks /Query /XML` 返回的 `<Command>` 带双引号
  （`"D:\...\ScreenTime.Agent.exe"`），与 `Environment.ProcessPath` 比较永不相等，
  Agent 每次启动都 `/F` 重建任务。表现：设置页关掉自启后，只要再开一次 UI
  （UI 会拉起 Agent），自启就悄悄恢复了。
  修法：比较前剥引号；并新增 `settings.autostart_disabled` 标记——用户在设置页主动关闭后，
  Agent 不再自动建回来。
- **分类下钻是空转的**。`MainWindow.DrillToCategory(category)` 的参数从未被使用，
  只切到历史页重载全量数据。现在通过导航参数传给历史页，排行按分类筛选，
  卡片右上角显示「分类：xxx + 清除筛选」。
- **报告页 / 应用页的去留**。报告页内容已完全并入历史页（总时长/日均/最活跃时段/
  分类占比/用途分布/每日柱状），删除 `ReportsPage`。应用页有不可替代的功能
  （导入已安装应用、单应用使用历史、站点使用），加回导航，`--page=apps` 恢复；
  `--page=reports` 作为旧名映射到历史页。
- **收件箱徽标全库聚合**。`CountUnclassified()` 原来走 `QueryAppUsage(UnixEpoch, UtcNow)`，
  把全库明细读进内存再做区间并集，且在主窗口构造时同步执行。
  46 万行实测 **522ms**。改为只针对未分类应用 id 的 SQL 聚合，配合新增的
  `idx_seg_app_day` 复合索引，同样数据量 **73ms**；徽标本身也改成后台线程刷新。
- **用户「恢复统计」被覆盖**。`MarkNoiseAppsIgnored` 每次启动无条件把噪声应用设成
  `ignored=1`，用户点恢复后重启又变回去。新增 `apps.ignored_manual` 列，
  用户手动设过的行不再被自动规则覆盖。
- **噪声规则两套且不一致**。收件箱有 40+ 个进程名，数据库自动忽略只有 9 个，
  导致「收件箱看不见、历史还在计时」。现在统一到 `AppDisplayService.NoiseProcessNames` 一份。
- **Temp 路径过滤从来没生效**。原 SQL 写的是 `LIKE '%\\appdata\\local\\temp\\%'`，
  在 SQLite 里 `\` 是普通字符，这个模式要求两个连续反斜杠，实际路径只有一个。
  Autoruns 这类从 Temp 解压运行的程序一直没被忽略。改用 `instr()` 子串匹配。
- **浏览器历史回填搬到 Agent**。`HistoryImportService` 从 WinUI 移到 Collect，
  由 Agent 常驻执行；UI 关掉后也能继续补网页地址。设置页开关通过 `settings_version` 热生效。
- **UI 与 Agent 并发迁移**。`Migrate()` 加跨进程互斥，两边同时启动时只有一个真正执行；
  `EnsureColumn` 容忍 `duplicate column`（PRAGMA 与 ALTER 之间的竞态）；
  UI 侧的 `Task.Run(Migrate)` 补上 try/catch，异常不再被静默吞掉。
- **其他**：
  - 网页时间线圆点颜色恒为写死的蓝色（`SystemAccentColor` 是 Color，`as Brush` 恒 null），
    改为解析 `AccentFillColorDefaultBrush`，跟随系统强调色。
  - 应用页 `AppItem` 未实现 `INotifyPropertyChanged`，后台加载的图标不会刷新。
  - 历史页排行卡片高度同步加相等判断，避免反复触发布局。
  - Agent 顶层异常后自恢复（10 分钟内只重启一次，避免启动期崩溃造成无限重启）。

### 同批次：把 UI 线程上的数据库操作挪走（针对「未响应」）

- **写操作全部后台化**。右键菜单（改分类 / 设用途 / 忽略 / 合并）和设置页的所有开关，
  原来都在 UI 线程同步写库；撞上 Agent 的心跳写就会原地等 `busy_timeout`（3 秒），
  表现是「点了没反应」。现在统一走 `Task.Run`，剪贴板与 `ShellExecute` 仍留在 UI 线程。
- **`Init()` 不再抢排他锁**。`PRAGMA journal_mode=WAL` 改为「先读，已经是 wal 就跳过」——
  切换 journal_mode 需要排他锁，每次启动都设一遍会在 Agent 正在写时白等，
  这是「双击图标半天不出窗口」的原因之一。
- **噪声扫描改为每天一次**（`settings.noise_scan_day`）。同时新应用入库时
  （`GetOrCreateApp`）就地判定噪声，不必等全表扫描——既减少启动时的全表 UPDATE，
  又让新装的安装器立刻被忽略。
- **历史页查询防抖**。连点「查询」、快速切主题、右键连续操作都会触发多次全量查询，
  200ms 内合并成一次；首次进入页面仍然立即查询。
- 设置页的自启状态读取合并为一次 `schtasks`（`AutoStartService.GetState`），
  并放到后台线程，切到设置页不再卡一下。

**验证**：构建 0 警告 0 错误；135 单测全过；46 万行压测库上徽标查询 522ms → 73ms；
运行日志确认 `noise_scan_day` 已写入、Agent 重启不再重复注册自启任务。

### 同批次：页面收编与交互精简

- **删除「应用」页**。导入已安装应用、单应用使用历史、分类 / 用途 / 忽略 / 合并全部并入历史页：
  - 历史页应用排行右键是完整菜单，新增「使用历史…」（近 30 天按天明细，弹窗展示）；
  - 排行卡片标题栏新增「导入应用」按钮（原应用页的导入已安装应用 / UWP 应用）；
  - `--page=apps` 作为旧名映射到历史页。
- **今日页右键菜单精简为 3 项**（打开应用 / 定位文件 / 复制 exe 路径）。日常查看的页面不该
  塞满管理操作，分类、用途、忽略、合并统一放历史页。
- **网页页加视图切换**：用 `SelectorBar` 在「时间线」与「网站分类」之间切换，一次只看一种，
  不再上下堆叠造成割裂。
- **历史页 / 网页页查询异步化**（v1.2.1 的第四条）：
  - 历史页 `RunQueryCore` 拆成「后台 `BuildSnapshot`（查库 + 纯计算）」和「UI `BindSnapshot`（只做绑定）」；
    每日分桶、24 小时分桶抽成静态纯函数 `BuildDayTotals` / `BuildHourSeconds`。
  - 用 `SemaphoreSlim(1,1)` 门控 + `_queryPending` 补跑，保证连续切换时不丢最后一次；
    用 `_queryVersion` 丢弃过期结果，`Unloaded` 时 `_queryVersion++` 让在途结果作废。
  - 网页页同样处理：两个跨表查询进线程池，聚合与绑定留在 UI（纯内存，毫秒级）。
- **自启开关「点了没反应」修复**。根因是 `SettingsPage.OnLoaded` 在 UI 线程同步读设置，
  一旦撞上 Agent 的写锁抛 `SQLITE_BUSY`，`_loading` 就卡在 `true`，之后**所有开关**都被
  `if (_loading) return;` 拦掉。改为 `try/finally` 兜底复位。
  同时自启状态读取不再依赖 `schtasks` 的输出解析，改为直接读 `%SystemRoot%\System32\Tasks\ScreenTime`
  （UTF-16 XML），失败才退回 schtasks；`schtasks` 失败会写 `%APPDATA%\ScreenTime\autostart.log`。
- **采集器自己不再出现在排行里**：`screentime.agent` 加入噪声名单。
  （之前应用排行同时有 `ScreenTime` 和 `ScreenTime.Agent` 两行，后者是采集器自己。）

### 同批次：网页页瘦身 + 命名调整

- **网页页删掉「网站分类」视图**，只保留时间线。原来「上面时间线、下面网站分类」两种视角堆在一页里，
  即使加了切换也显得累赘；域名维度的聚合在历史页的分类占比里已经能看。
  连带清理了 `PageRow` / `PageGroup` / `HostOf` / `NormalizeTitle` / `PageRow_Tapped` 等死代码。
- **网页页加搜索**：顶部搜索框按标题 / 域名 / 网址过滤时间线，输入即过滤（200ms 防抖），
  回车或点放大镜立即生效。过滤在本地内存完成，不重新查库；摘要同步显示当前筛选后的条数与时长。
- **「分类待办」改名为「待办」**（导航、页面标题、空状态、文档同步）。
  这个名字更短，也和「待办事项」的心智更接近。

## v1.2.0（2026-10-29）采集与 UI 双进程架构（第三档）

**核心变化：托盘常驻态内存从 ~110MB（Private）压到 ~13-16MB。**

- **采集组件抽离为 `ScreenTime.Collect` 类库**（提交 `a0b57bf`，git 识别为纯 rename，可单独回退）：
  `Native\{Win32, AudioMonitor, DisplayStateWatcher, SessionState, UIAutomation}` + `Services\{WatcherService, BrowserTabService, UwpAppService}` 整体移入。
  WatcherService 移除 DispatcherQueue 依赖（PurposePrompt 直接回调，线程切换交由订阅方），为无 UI 进程复用铺路。
- **新增 `ScreenTime.Agent` 采集器进程**（无 UI 的 WinExe，可独立回退）：
  - 纯 Win32 托盘（NOTIFYICON + 隐藏消息窗口 + TrackPopupMenu：打开统计 / 退出），不依赖 XAML。
  - 常驻采集（前台窗口/音频/锁屏/会话事件）+ UWP 图标回填 + 开机自启注册（ONLOGON 无参数，任务名 `ScreenTime`）。
  - 单实例 Mutex（`ScreenTime_Agent_Mutex`）；日志 `%APPDATA%\ScreenTime\agent.log`。
  - 托盘「打开统计」启动同目录 ScreenTime.exe（UI 单实例自动唤起）。
  - 旧 UI 自启任务自动迁移：Agent 启动时读取任务 XML，指向非自己则 /F 重建。
  - **实测：常驻 Private 12.5-16MB**（原 WinUI 进程托盘态约 110MB Private / 75MB 框架底数）。
- **UI 进程瘦身**（可独立回退）：
  - 删除 Watcher 采集初始化、TrayService、--silent 托盘模式（采集/托盘/自启全部移交 Agent）。
  - **关闭窗口即退出进程**（不再隐藏托盘）：显示态内存随进程一起释放，采集由 Agent 不中断。
  - 启动时 EnsureAgentRunning：Agent 不在则拉起同目录 ScreenTime.Agent.exe。
  - 用途标注跨进程：Agent 写 `settings.pending_purpose`，UI 每 30 秒轮询弹 TeachingTip（确认写库+清标记；取消仅清标记）。
  - 设置热重载跨进程：UI 改音频/用途/浏览器开关后写 `settings_version`，Agent 每 10 秒轮询变化并 ReloadSettings。
- **发布参数**：WinUI 与 Agent 均启用 PublishReadyToRun（发布时预热启动路径）。
- 验证：构建 0 警告 0 错误；129 单测全过；全链路冒烟（seed 演示库 → Agent 常驻 16MB → UI 显示 122MB → 关闭窗口 UI 进程退出 → Agent 继续常驻并写库）。

### 热修复（同日，仍为 v1.2.0，未改版本号）

- **托盘左键点击无反应**。根因：托盘回调（`WM_APP+1`）是在消息泵里拦截的，`WndProc` 只调 `DefWindowProc`。
  但 shell 投递回调走的是窗口过程，消息泵那条分支根本收不到，点击被静默丢弃。
- 修法：回调改在 `WndProc` 处理（`SendMessage` / `PostMessage` 两条路都能收到），消息泵恢复标准派发；
  顺带补 `WM_LBUTTONDBLCLK`、`NIN_SELECT` / `WM_CONTEXTMENU`（NOTIFYICON_VERSION_4 兼容）、
  菜单结束后 `WM_NULL`，并在 `Disable` 时先 `NIM_DELETE`。
- 新增诊断：`tray: NIM_ADD ok=…` / `tray: rect=…`（`Shell_NotifyIconGetRect`）/ `tray: callback 0x…`，
  以及 `--tray-selftest`（自投递一次左键回调，用于区分「消息没送到」和「收到了没处理」）。
- 验证：真实左键点击 → `tray: callback 0x202` → UI 进程拉起，实测通过。
- `build.cmd` 同步修正：原来只发布 UI，Agent 不会被发出去（照旧脚本打的包托盘功能会缺文件）。

- **亮屏时长虚高**（同日第二处热修复）。根因：`display_off` 一次都没被记录过
  （全库 36 条 `display_on`、0 条 `display_off`）。关机/断电/强杀时进程被直接终止，
  熄灭事件来不及写；`GetDisplayOnSeconds` 把"没等到 off 的 on"当成一直亮着，
  于是从当天第一条 `display_on` 一路算到此刻。实测症状：14 小时 21 分，
  其中 11.8 小时机器是关着的。
- 修法：
  - 启动时 `Database.RepairDisplaySpans()` 扫描全部 display 事件，
    把"连续两条 `display_on` 之间没有 `display_off`"的悬空段收口到该段内最后一次活动时刻；
    空档超过 30 分钟才补，正常秒级重启不受影响。
  - `WatcherService.Dispose()` 主动退出时补写 `display_off`，退出时间点已知，不必靠猜。
- 效果：同一份数据修完，今日亮屏从 14 小时 21 分 → 2 小时 43 分。
- 新增 `DisplayOnRepairTests`（6 个用例）覆盖中段空档、末尾悬空、秒级重启、正常 on/off 对、阈值可调。

## v1.1.2（2026-10-29）启动提速 + 常驻内存平稳 + 报表后台化

- **启动提速**：`Database.Migrate()`（重分类/去重/补列，大库可能耗时数百毫秒到数秒）从启动同步路径移到后台线程；`Watcher.Start()` 的 7 天碎片清理 + 标题清洗同样移后台。冷启动窗口不再等数据迁移完成。新库建表已补齐全部列，旧库仍由 Migrate 幂等升级，采集心跳（5 秒后首次写库）与迁移有充分缓冲。
- **常驻内存平稳**：窗口在托盘态（页面已释放）时，每 30 分钟自动做一次积极 GC + 工作集修剪，防止长会话内内存缓慢爬升。只在隐藏态触发，不影响显示态。
- **报表页异步化**：月度/周报的 4 次区间查询（AppUsage / Segments / 环比 / 用途）+ 环比并集计算移到后台线程，切月/切周不再卡 UI；带版本号防竞态，快速连点丢弃过期结果。
- **DonutChart 防抖**：环形图 SizeChanged 直接重绘改为 100ms 防抖，拖动窗口边缘不再逐帧重绘。

## v1.1.1（2026-10-29）WinUI3 内存与跟手度优化

- 排行列表 ItemsControl + ScrollViewer 改 ListView，启用虚拟化（只实例化可见项）。
- 今日页时间线 150ms 防抖；全量重建从 30 秒一次降为 5 分钟一次（跨天/主题/尺寸变化仍强制重建）。
- 主题切换由「整页重建」改为 IThemeRefreshable 轻量刷新配色（今日/历史/报告三页），切换不丢页面状态。
- AppIconService 图标缓存加 256 条上限（字节层与 UI 层同上限，到顶整表清空）。
- 关闭窗口 = 隐藏到托盘：清空导航栈 + Content=null + 清图标缓存 + 延迟积极 GC + 工作集修剪。
  实测（90 天演示库）：显示态工作集 186MB → 托盘态 21MB；内存转储证实页面对象已从 GC 堆移除。
  注意：三个页面因此移除了导航缓存（NavigationCacheMode 会强引用页面、阻止释放），切页会重建。

## v1.1.0（历史）体验优化批次

- 分钟粒度统计、三级节拍、高清图标、报告页提速；图标空白根因修复；托盘右键修复。
