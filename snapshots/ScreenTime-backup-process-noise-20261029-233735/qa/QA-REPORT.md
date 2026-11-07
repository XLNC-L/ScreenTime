# ScreenTime v1.2.0 独立验收报告（QA·修正口径版）

- 验收方：独立 QA（不信任前提）
- 验收对象：`D:\XLNC\文档\ScreenTime`，commit `3986f08`（main，v1.2.0 双进程架构）
- 验收日期：2026-10-29
- 复现环境：Windows 11 x64 / PowerShell 7.6.4 / .NET 8 SDK（本机，Asia/Shanghai）

> **前置缺陷注记（P4）**：本报告的 B 组证据**仅本机有效**——注入测试、进程级破坏性测试、真机计时测试全部在本机执行，未在其他机器/环境复现；且 QA 注入程序（`qa/`）尚未纳入仓库，第三方复现需先取得该目录。在满足「干净环境 + 仓库内 QA 工具」之前，B 组结论的可信度受限。

## 一、总体结论

**不可交付。**

一句话理由：**核心能力清单第 3 项「限额」功能不存在**（A5，A 组 FAIL，按验收规则任一 A 组 FAIL 即不可交付）；另有托盘菜单缺「暂停记录」（F4）、README 与 v1.2.0 严重脱节（H2）两项必须修复。

**真实统计**（口径：PASS=完全有证据；PASS(擦边/部分)=基本达标但有已知偏差；PASS(代码级)=代码+单测证据、无真机证据，**不计入 PASS 数**；N/A=不适用，单列）：

**PASS 20 / PASS(擦边·部分) 6 / FAIL 3 / BLOCKED 2 / N/A 3 / PASS(代码级) 2（不计入）**

- PASS：A2、A3、A4、A6、B1、B3、B4、B6、B7、B8、C2、C3、E1、E2、F1、F3、G1、G3、H1、H3
- PASS(擦边/部分)：A1、C1、E3、F2、F5、G2
- FAIL：A5、F4、H2
- BLOCKED：B9、H4
- N/A（单列）：D1、D2、D3
- PASS(代码级，不计入)：B2、B5

## 二、致命问题（必须修）

### P1 | A5 每日限额功能整体缺失（核心能力第 3 条）★ 一票否决项
- **现象**：验收清单 A5「设置限额为 1 分钟，实际使用超限后确实触发可见提醒」无法执行——产品没有任何限额能力。
- **最小复现**：`Get-ChildItem -Recurse -File -Include *.cs -Path ScreenTime.Core,ScreenTime.WinUI | Select-String -Pattern '限额|每日限|limit|Limit'` → **零命中**；README.md 全文无「限额/每日限/限时」字样。
- **期望 vs 实际**：期望为应用/分类设置每日限额并在超限时可见提醒；实际不存在功能入口、数据结构、提醒逻辑。
- **建议修法**：`settings` 表加 `limit_minutes`（应用/分类维度）；`QueryAppUsage` 已有当日累计，UI 今日页/托盘对比阈值；超限用应用内 Banner 或托盘提示。需先与用户确认这是产品缺失还是刻意不做（README 当前未声明此限制，按缺失处理）。

### P2 | F4 托盘菜单缺「暂停记录」项
- **现象**：验收清单 F4 要求托盘菜单包含「打开主界面 / 暂停记录 / 退出」，实际只有「打开统计 / 退出」。
- **最小复现**：右键 `ScreenTime.Agent.exe` 托盘图标 → 菜单仅两项（`Native\TrayIcon.cs` 中 cmd 1=打开统计、2=退出）。
- **建议修法**：Agent 托盘加「暂停记录/恢复记录」切换项，写 `settings.paused=true/false`，`WatcherService.Heartbeat` 检查后跳过计时。

### P3 | H2 README 与 v1.2.0 严重脱节
- **现象**：README 描述 v1.1.x 状态：第 69-78 行仍列 `--silent`（v1.2.0 已删）；第 57-63 行结构缺 Collect/Agent；第 154 行写「114 个用例」（实际 129）。
- **建议修法**：删 --silent、补双进程结构、改 129、补「关闭窗口即退出、采集由 Agent 常驻」行为说明。

### P4 | QA 可复现性缺陷（升格：影响结论可信度的前置缺陷）
- **现象**：B 组断言依赖 `qa/` 注入程序与库检查工具，该目录**不在仓库内**（工作区未提交）；全部实测仅在本机执行。
- **影响**：第三方无法一条命令复现 B 组结论；结论可信度限定于本机（见文首注记）。
- **建议修法**：将 `qa/` 纳入仓库（或独立发布），并附 `QA-REPORT.md`；在干净环境重跑 B 组后移除注记。

## 三、其他问题（按严重度排序）

- **O1 | C1 限额临界值无单测**：与 P1 同源。现有 129 用例覆盖空/单条/跨天/跨分类/重叠/倒序/格式化；修 P1 后补限额临界值用例。
- **O2 | G2 无「不采集窗口标题」开关**：默认采集窗口标题，有 `title_retention`（30 天）清洗保时长、历史回填排除邮件/网银域名；但无「完全不采集标题」开关。
- **O3 | B9 24h 长跑证据缺失**：本环境后台任务约 5 分钟被中断，无法长跑；代码层有 Monitor 防心跳叠加，Agent 实测 CPU≈1.5%、Private 14.9MB，但「内存无持续增长」无长时间证据。
- **O4 | A1 系统性正偏**：三次采样全部正偏（+3%~+9%），非随机误差；建议核对片段结算边界（切换瞬间的双计）。
- **O5 | F5 内容区裁剪**：最小尺寸（400×300）布局无错乱，但数据卡片被裁出视野（详见 F5 行）。

## 四、验收明细表

| 编号 | 检查项 | 判定 | 证据 |
|---|---|---|---|
| A1 | 3 应用各计时 2min，误差≤10% | **PASS(擦边)** | 实测 3×30s：记事本 32.7s(+9%)、画图 32.3s(+7.7%)、PowerShell 30.9s(+3%)；对照 60s 段 pwsh 60.3s(+0.5%)。**系统性正偏**（三次全正）。命令：`qa\bin\Release\net8.0\QaTests.exe apps %TEMP%\st-qa-a1c\a1c.db` |
| A2 | 多窗口切换不重复/不丢失 | PASS | 双 notepad 窗口交替 4×10s → 单条 40.8s(+2%)，same-app-overlap-pairs=0；a1c check overlap=0 |
| A3 | 本月≥本周≥今日 | PASS | 注入 30/40/100min：今日1920s ≤ 本周4440s ≤ 本月4440s。`dotnet run --project qa -c Release` |
| A4 | 分类修改后历史重新归类 | PASS | 注入 10min：改前=游戏600s，改后=办公与文档600s（历史立即随新分类） |
| A5 | 限额+超限可见提醒 | **FAIL** | 全仓库搜索 限额/Limit 零命中；无功能入口/数据结构/提醒 |
| A6 | 重启后数字一致 | PASS | force kill 后重启：integrity=ok、segments=1→1 不丢失不重复 |
| B1 | 跨午夜切分 | PASS | 注入 23:58→00:02：day1=120s、day2=120s、总和240s |
| B2 | 休眠/锁屏不计时 | **PASS(代码级，不计入)** | 代码：suspend/lock 关闭片段 + SegmentClock 120s 断档兜底；单测 `ResolveEnd_SleptThreeHours`；**真机休眠未做** |
| B3 | 空闲超阈值停止，恢复继续 | PASS | 实测 idle_limit=5：20s 无输入仅记 5s（=阈值）；恢复输入 12s 记 10s |
| B4 | 时钟回拨无负数/未来记录 | PASS | 注入倒序/零长不计负时长(1800s 不变)；单测 `IsStale_ClockGoingBackwards`、`ResolveEnd_AtBeforeSegmentEnd_NeverShrinks` |
| B5 | 时区/DST 当天不错乱 | **PASS(代码级，不计入)** | 存储 UTC ISO（实测 start.Kind=Utc）+ DayRange/WeekRange/MonthRange 转 UTC；**真机改时区未做** |
| B6 | 强杀后数据不损坏不重复 | PASS | force kill → 重启：integrity_check=ok、overlap=0、片段数不变 |
| B7 | 删库优雅降级 | PASS | 运行中删 db+wal+shm → 12s 后进程存活 14.9MB（库自动重建，丢数据但可用） |
| B8 | 月边界正确重置 | PASS | 注入上月最后一天数据不计入本月（A3：本月=本周=4440s） |
| B9 | 24h 内存无持续增长 | BLOCKED | 环境后台任务约 5min 被中断，无长跑证据 |
| C1 | 聚合单测覆盖 | PASS(部分) | 129 用例覆盖空/单条/跨天/跨分类/重叠/倒序等；限额临界值无测试（O1） |
| C2 | 时间不依赖本地字符串比较 | PASS | 注入实测 `start.Kind=Utc`；`ToIso` 输出 UTC；范围查询转 UTC |
| C3 | 写入幂等不重复累加 | PASS | 同片段 10 次延长 → 总 310s（15:00→15:05:10），不重复累加 |
| D1 | 权限申请与说明 | N/A | 产品设计无需权限（不弹 UAC/不申请辅助功能/录屏/通知）；README 明确权限模型 |
| D2 | 拒绝权限后仍启动 | N/A | 无权限可拒；删库场景实测仍可启动（B7），拒绝场景不适用 |
| D3 | 通知关闭兜底 | N/A | 不依赖系统通知（无限额/通知提醒），交互走托盘与页面内 |
| E1 | 空闲 CPU<3%、内存合理 | PASS | Agent 20s 累计 CPU 0.3s(≈1.5%)、Private 14.9MB、WS 67.6MB |
| E2 | 7 天查询<200ms | PASS | 90 天库 2054 片段：7d_query_ms=92 |
| E3 | 数据增长上限/清理策略 | PASS(部分) | 有 title_retention（30 天清标题保时长）；明细无自动清理（README 已列已知限制） |
| F1 | 主界面 3s 看懂 | PASS | 截图：今日总时长 3小时15分 + 应用排行（微信 50分最高） |
| F2 | hover 反馈/空状态 | PASS(部分) | Web「暂无网页记录…」、Inbox EmptyPanel、Reports「暂无数据」；系统控件自带 hover |
| F3 | 深/浅色对比度 | **PASS** | **实测**：深色（main_today.png、main_dark.png）与浅色（main_light.png）双主题截图，文字/卡片清晰可读 |
| F4 | 托盘菜单三项 | **FAIL** | 仅「打开统计/退出」，无「暂停记录」 |
| F5 | 最小尺寸不破版 | **PASS(擦边)** | **实测**：MoveWindow 400×300（main_min_light.png）——导航/标题正常、无重叠错位；数据卡片被裁出视野（内容区裁剪，非布局错乱） |
| G1 | 数据仅存本地无上报 | PASS | 全源码搜索 HttpClient/WebClient/fetch/axios/sentry/telemetry/upload/Socket 零命中 |
| G2 | 敏感内容采集开关 | PASS(部分) | 标题 30 天清洗 + 回填排除邮件/网银域名；无「不采集标题」开关（O2） |
| G3 | 导出无路径/用户名泄露 | PASS | BuildSegmentsCsv/Json 列无 exe_path、无用户名 |
| H1 | 测试命令全绿 | PASS | `dotnet test ScreenTime.sln -c Release`：129 通过 0 失败 441ms |
| H2 | README 安装/运行/权限/限制 | **FAIL** | 内容为 v1.1.x：`--silent`、114 用例、结构缺 Collect/Agent（P3） |
| H3 | 无调试残留/硬编码路径 | PASS | 搜索 Console.Write/Debug.Write/TODO/FIXME 零命中 |
| H4 | 干净环境可运行 | BLOCKED | 无干净环境；验证方式：`build.cmd` 发布 → 新虚拟机运行 ScreenTime-WinUI 目录（未执行） |

## 五、我无法验证的部分（BLOCKED 项）

1. **B9（24h 内存长跑）**：需要 ≥1 小时无中断环境。本验收环境后台任务约 5 分钟被自动终止。可提供：Agent 常驻 24h 后采样 WorkingSet/Private 两个时点，或 1 小时采样曲线。
2. **H4（干净环境运行）**：需要一台无 .NET SDK 的 Windows 10/11 x64 虚拟机。我提供发布命令，环境由你提供。
3. **B2 真机休眠 / B5 真机改时区**：休眠会中断会话、改系统时间有副作用，未执行（代码级通过，不计入 PASS 数）。

## 六、复验方式（修完后）

| 修复项 | 复验命令/步骤 |
|---|---|
| P1 A5 限额 | `QaTests.exe check <db>` 增加限额断言；或手动：设置页限额 1 分钟 → 使用 2 分钟 → 见可见提醒（截图存 qa/screenshots） |
| P2 F4 暂停记录 | 托盘右键 →「暂停记录」→ 等 15s → `QaTests.exe check <db>` recent90s 不再增长；再点「恢复记录」→ 增长恢复 |
| P3 H2 README | 重新通读 README，对照 `--page=` 参数表与 `dotnet test` 数字 |
| B9 长跑 | 你提供环境后：Agent 起 → 1 小时 → 采样 → 再 1 小时 → 对比 |
| O1 限额单测 | `dotnet test ScreenTime.sln -c Release`（用例数增加且全绿） |
| O2 标题开关 | 设置页关「记录标题」→ 新片段 title 为空、时长正常 |
| O4 A1 正偏 | 核对片段结算边界：切换瞬间是否双计（期望修正后误差回到 ±随机范围） |

---

*复现基线命令汇总：*
- 全量单测：`dotnet test ScreenTime.sln -c Release`
- QA 注入测试（B1/A3/A4/C3/B4/C2）：`dotnet run --project qa -c Release`
- 库完整性/查询耗时（B6/A6/E2）：`qa\bin\Release\net8.0\QaTests.exe check <db路径>`
- 分应用时长（A1/A2）：`qa\bin\Release\net8.0\QaTests.exe apps <db路径>`
- 写设置（B3）：`qa\bin\Release\net8.0\QaTests.exe set <db> idle_limit=5`
- F3/F5 截图：见 `qa\screenshots\{main_today,main_dark,main_light,main_min_light}.png`
