# AGENTS.md

给所有参与本项目的 AI 编码工具（Claude Code / Codex / Cursor / WorkBuddy / Copilot …）的作业须知。
**接手前先读完这一页，不要先动手。**

## 1. 这是什么

ScreenTime 是一个 Windows 屏幕使用时间统计工具，**WinUI 3 + 双进程架构**：

| 项目 | 职责 | 备注 |
|---|---|---|
| `ScreenTime.WinUI` | 界面主进程（WinUI 3 / .NET 8） | 关闭即退出，不做采集 |
| `ScreenTime.Agent` | 采集 + 托盘常驻进程（Win32 托盘，无 UI 框架） | 自启、写 SQLite |
| `ScreenTime.Collect` | 采集类库（前台窗口、浏览器标签、UWP、会话/显示状态） | 被 Agent 引用 |
| `ScreenTime.Core` | 数据层与业务服务（SQLite、统计、分类、用途） | 被两边共用 |
| `ScreenTime.Tests` | xUnit 单测 | 改 Core 必跑 |
| `Native/` | C++ 原生组件（Win32 辅助） | 很少动 |

数据全程本地 SQLite，**没有任何联网上报**。

## 2. 命令

```bat
build.cmd                                     :: 编译 UI + Agent 并 publish 到 .\ScreenTime-WinUI\
dotnet build ScreenTime.sln -c Release        :: 只编译
dotnet test ScreenTime.Tests\ScreenTime.Tests.csproj -c Release
ISCC.exe /DAppVersion=x.y.z installer\ScreenTime.iss   :: 打安装包（需 Inno Setup 6.3+）
```

> 发布必须同时 publish **UI 和 Agent 两个进程到同一目录**，否则托盘/采集会缺文件。

## 3. 硬规矩（违反这些等于制造事故）

1. **不要创建 `ScreenTime-backup-*` 之类的目录副本。** 需要回滚就用 git：`git stash` / `git commit` / `git checkout <sha>`。
   历史教训：2026-10-29 一晚产生了 9 份 4.3 GB 的目录副本，全部是 git 里已有的状态。
2. **不要手改 `bin/`、`obj/`、`dist/`、`ScreenTime-WinUI/`、`qa/`。** 都是构建/本地产物，已在 .gitignore。
3. **不要提交** `*.zip`、`*.db`、`*.log`、安装包。
4. **不要在 `D:\ScreenTime-v1.2.0-win-x64` 里改代码。** 那是正在运行的发布产物，只读。
5. **版本号在 3 个 csproj 里各写了一份**（`ScreenTime.WinUI`、`ScreenTime.Agent`、`ScreenTime.Collect` 的 `<Version>`），改版本必须三处同步。
6. 依赖用 `packages.lock.json` 锁定，改了 PackageReference 记得把锁文件一起提交。

## 4. 收尾动作（每次会话结束前）

1. `dotnet build` 通过；动了 Core 就跑 `dotnet test`。
2. **提交**，不要留下未提交的改动给下一个工具猜。提交信息首行说清做了什么，例如：
   - `修复亮屏时长虚高：收口悬空的 display_on 段`
   - `v1.2.1: 页面收编、网页页加搜索`
3. 用户可见的变化记到 `CHANGELOG.md`。
4. 验证"我现在跑的到底是哪版"：`powershell -File tools\whats-running.ps1`

## 5. 自查脚本

`tools\whats-running.ps1` 会打印：当前运行的进程路径、它的产物提交号、仓库 HEAD、
以及**运行版落后仓库多少个提交、影响了哪些文件**。换 agent 之前先跑一遍，两边认知就对上了。

## 6. 界面约定

- 主题：深色为主，跟随系统；配色见 `ScreenTime.WinUI/Themes/CategoryPalette.cs`
- 页面：`TodayPage` / `HistoryPage` / `InboxPage` / `WebPage` / `SettingsPage`（Apps 与 Reports 已收编进 History）
- 设计说明在 `ScreenTime.WinUI/DESIGN.md`，改 UI 前先读
