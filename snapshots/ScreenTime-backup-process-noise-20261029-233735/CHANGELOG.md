# ScreenTime 版本记录

本文件记录每个版本号的改动，**仅保存在本地**（不推 GitHub），用于区分不同版本的行为差异。

---

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
