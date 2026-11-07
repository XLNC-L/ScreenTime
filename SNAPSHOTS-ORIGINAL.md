# ScreenTime 历史备份说明

归档时间：2026-10-30 12:25 ｜ 清理时间：2026-10-30 12:34
合计：**33.1 MB**（清理前 4358.74 MB，剔除 4326 MB 构建垃圾后）

## 这些是什么

2026-10-29 14:31 打出 `v1.2.1+a15cc23` 之后，到 10-30 01:34 之间陆续手工复制的快照。
它们是 AI 反复改 UI/采集逻辑时留下的中间态。

**与 git 的关系（重要）**：

| 备份 | 在 git 里吗 |
|---|---|
| `224055` | 是 —— 内容等于提交 `a15cc23`，可 `git checkout` 取回 |
| `before-cleanup-0036` | 接近但不等于 `c9cf351`：是那次提交前的**未提交状态** |
| 其余 7 份（followup / process-noise / layout / inbox-ui / performance / history-report-merge / peak-time） | **否** —— 从未提交，git 里不存在 |

所以这 9 个文件夹现在是那 7 个中间态的唯一副本。体积只有 33 MB，留着不心疼。

## 清理做了什么

2026-10-30 12:34 删除的内容（全部可重新生成，源码零丢失）：

| 项目 | 回收 |
|---|---|
| 各备份内 `bin/`、`obj/`（56 个目录） | 3744 MB |
| 各备份内 `ScreenTime-WinUI.zip` 旧打包（9 个） | 387 MB |
| 各备份内 `ScreenTime-WinUI/` publish 副本（1 个） | 110 MB |
| `before-cleanup` 内的 `dist/` 副本（已验证与仓库 dist 逐字节相同） | 85 MB |
| 仓库内各项目 `bin/`、`obj/`、`ScreenTime-WinUI/`、根目录两个旧 zip | 约 1277 MB |

保留：`before-cleanup` 的 `.git`（6 MB，完整仓库副本，以防万一）、全部源码、全部 `qa/` 报告截图。

## 清单

| 文件夹 | 创建时间 | 现在大小 | 与运行版(a15cc23)源码差异 | 改动焦点 |
|---|---|---|---|---|
| ScreenTime-backup-20261029-224055 | 10-29 22:40 | 3.0 MB | **0（完全一致）** | a15cc23 的纯拷贝 |
| ScreenTime-backup-followup-20261029-232301 | 10-29 23:23 | 3.0 MB | 4 个文件 | Database.cs、TodayPage.xaml、WebPage.xaml(.cs) |
| ScreenTime-backup-process-noise-20261029-233735 | 10-29 23:37 | 3.0 MB | 11 个文件 | 进程噪声过滤：WatcherService、Database、AppDisplayService |
| ScreenTime-backup-layout-20261029-235130 | 10-29 23:51 | 3.0 MB | 14 个文件 | 布局：7 个页面 xaml、SettingsPage、AppActionMenu |
| ScreenTime-backup-inbox-ui-20261030-000002 | 10-30 00:00 | 3.0 MB | 15 个文件 | 收件箱 UI：InboxPage.xaml.cs、HistoryPage、ReportsPage |
| ScreenTime-backup-performance-20261030-000211 | 10-30 00:02 | 3.0 MB | 18 个文件 | 性能：MainWindow、11 个页面 |
| ScreenTime-backup-history-report-merge-20261030-001532 | 10-30 00:15 | 3.0 MB | 22 个文件 | 历史/报告页合并 |
| ScreenTime-backup-peak-time-20261030-001850 | 10-30 00:18 | 3.0 MB | 22 个文件 | 峰值时段：App.xaml.cs、MainWindow、AppIconService |
| ScreenTime-backup-before-cleanup-20261030-0036 | 10-30 00:36 | 9.3 MB | 约 28 个文件 | v1.2.1 收尾前的未提交状态（含 6 MB 的 .git） |

> 完整分析见 `..\ScreenTime-版本对比清单-20261030.md`。

## 想恢复某个版本怎么办

1. **从 git 取**（a15cc23 / c9cf351 / 3c4eb27 都在 GitHub 上）：
   ```powershell
   cd D:\XLNC\文档\ScreenTime
   git checkout a15cc23     # 运行版代码
   git checkout 3c4eb27     # 当前最新
   ```
2. 中间态（7 份）只能从这些文件夹里拿：把需要的文件复制回仓库。
3. 确认彻底不需要了，直接删掉本目录即可 —— 33 MB，不急。
