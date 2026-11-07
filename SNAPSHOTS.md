# 2026-10-30 中间态快照归档

本分支（`archive/2026-10-30-snapshots`）是**纯归档分支，不参与开发、不合并回 main**。

## 为什么存在

2026-10-29 打出 `v1.2.1+a15cc23` 之后，到 10-30 01:34 之间，AI 反复改 UI / 采集逻辑留下了 9 份手工复制的目录快照
（原位置 `D:\XLNC\文档\ScreenTime-历史备份`，清理后 33.1 MB）。

其中 7 份**从未提交进 git**，本地文件夹是唯一副本。把它们纳入版本库，是为了不再依赖某台机器的某个目录。

## 归档口径

- 每份快照一个提交，整树放在 `snapshots/<原文件夹名>/` 下，文件名与目录结构保持原样。
- 剔除的都是可重新生成或按 .gitignore 不应入库的内容：`bin/`、`obj/`、`dist/`、`ScreenTime-WinUI/`、`.git/`、`*.zip`、`*.db`、`*.log`。
- `qa/` 报告与截图保留（原 `.gitignore` 忽略 qa/，归档时强制加入）。

## 与 git 正式历史的对应关系

| 快照 | 在 git 里吗 | 本分支是否归档 |
|---|---|---|
| `ScreenTime-backup-20261029-224055` | 是，内容等于提交 `a15cc23` | 否（git 可直接取回，不重复占空间） |
| `ScreenTime-backup-followup-20261029-232301` | 否 | 是 |
| `ScreenTime-backup-process-noise-20261029-233735` | 否 | 是 |
| `ScreenTime-backup-layout-20261029-235130` | 否 | 是 |
| `ScreenTime-backup-inbox-ui-20261030-000002` | 否 | 是 |
| `ScreenTime-backup-performance-20261030-000211` | 否 | 是 |
| `ScreenTime-backup-history-report-merge-20261030-001532` | 否 | 是 |
| `ScreenTime-backup-peak-time-20261030-001850` | 否 | 是 |
| `ScreenTime-backup-before-cleanup-20261030-0036` | 接近但不等于 `c9cf351`（是那次提交前的未提交状态） | 是 |

## 怎么取用

```powershell
git fetch origin archive/2026-10-30-snapshots
git show origin/archive/2026-10-30-snapshots:snapshots/ScreenTime-backup-layout-20261029-235130/ScreenTime.WinUI/Pages/TodayPage.xaml
```

需要整份对比时，把对应目录复制回源码仓，或另建临时分支。**不要把本分支合并进 main**。

原始说明（含清理记录与体积明细）见 `SNAPSHOTS-ORIGINAL.md`。
