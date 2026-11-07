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
powershell -File tools\publish.ps1 -Deploy     :: 自包含发布并镜像到 D:\ScreenTime
dotnet build ScreenTime.sln -c Release        :: 只编译
dotnet test ScreenTime.Tests\ScreenTime.Tests.csproj -c Release
dotnet run --project tools\menu-probe         :: 托盘菜单回归（弹出/关闭/不套娃），改菜单必跑
"%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" /DAppVersion=x.y.z /DSelfContained=1 installer\ScreenTime.iss
```

> 发布必须同时 publish **UI 和 Agent 两个进程到同一目录**，否则托盘/采集会缺文件。
>
> `build.cmd` 是**框架依赖**（`--self-contained false`），而运行目录 `D:\ScreenTime`
> 是**自包含**（自带 .NET 8 运行时）。同步运行目录请用 `tools\publish.ps1`，
> 它会用 `--self-contained true` 发布、校验 7 个 `.xbf`、镜像时排除 `.workbuddy-ai`，
> 自动恢复被 `-r win-x64` 还原改脏的 `packages.lock.json`，并执行 `Remove-PublishExtras`
> 精简产物（删 pdb / DiaSymReader / 多余语言资源，473 → 304 文件）。

## 3. 硬规矩（违反这些等于制造事故）

1. **不要创建 `ScreenTime-backup-*` 之类的目录副本。** 需要回滚就用 git：`git stash` / `git commit` / `git checkout <sha>`。
   历史教训：2026-10-29 一晚产生了 9 份 4.3 GB 的目录副本，全部是 git 里已有的状态。
2. **不要手改 `bin/`、`obj/`、`dist/`、`ScreenTime-WinUI/`、`qa/`。** 都是构建/本地产物，已在 .gitignore。
3. **不要提交** `*.zip`、`*.db`、`*.log`、安装包。
4. **不要在 `D:\ScreenTime` 里改代码。** 那是正在运行的发布产物，只读。
5. **版本号只在 `Directory.Build.props` 写一份**（v1.4.5 起），三个 csproj 不再各写 `<Version>`；
   CI / 发布脚本仍可用 `-p:Version=x.y.z` 覆盖。
6. 依赖用 `packages.lock.json` 锁定，改了 PackageReference 记得把锁文件一起提交。
7. **写日志统一用 `ScreenTime.LogFile`**，不要直接 `File.AppendAllText`——它负责 2 MB 滚动，
   保留 3 份历史（`xxx.log.1` ~ `.3`，最多 8 MB），否则常驻进程的日志会无限增长。
8. **部署要按发布口径**：`dotnet publish -c Release -r win-x64`（项目开了 `PublishReadyToRun`），
   `dotnet build` 出来的不是 R2R；换版本号时 `ScreenTime.{WinUI,Agent,Collect,Core}.dll` 必须整套替换。
9. **动了 XAML 就必须带上 `resources.pri`**：xbf 编译产物在里面，只换 dll 会让
   `SettingsPage.Connect` 这类生成的绑定代码取到错位的控件，抛 `InvalidCastException`，
   表现是「点导航没反应、页面切不过去」。完整清单：`ScreenTime.exe`、`ScreenTime.dll`、
   `resources.pri`、`ScreenTime.Agent.exe/.dll`、`ScreenTime.Collect.dll`、`ScreenTime.Core.dll`。
10. **安装器界面文案靠 `installer\languages\ChineseSimplified.isl`**（Inno 官方包不含中文）。
    不要改回 `compiler:Default.isl`，那会让向导变回英文。
11. **不要删 `Microsoft.UI.Xaml.Phone.dll`**：名字像手机专用，实际是 WinUI 启动期硬依赖，
    删掉后界面直接起不来（已实测）。发布精简只允许删 pdb、DiaSymReader、多余语言目录。
12. 安装器新增了「仅为我 / 为所有用户」选择（`PrivilegesRequiredOverridesAllowed=dialog`），
    静默安装必须带 `/CURRENTUSER` 或 `/ALLUSERS`，否则会卡在这个对话框。
13. **Win32 窗口只能在创建它的线程销毁**。定时器/线程池里看到窗口要关，一律
    `PostMessage(WM_APP_CLOSE)` 回消息线程（见 `TrayMenuWindow.RequestClose`），
    直接跨线程 `DestroyWindow` 会静默失败，窗口留在屏幕上且是 topmost，谁也关不掉——
    v1.4.1 的「菜单套娃」就是这么来的。
14. **弹窗类菜单不要指望 `SetForegroundWindow`**：托盘进程在后台，系统常会拒绝，
    拿不到 `WM_ACTIVATE` 就没有「点外面自动关」。「点外面关闭」用
    `SetWinEventHook(EVENT_SYSTEM_FOREGROUND)` 前台切换通知（见
    `TrayMenuWindow.InstallWinEventHook`），窗口清零后摘钩。
15. **禁止 `WH_MOUSE_LL` 低级鼠标钩子**（v1.4.2 的教训）：它拦截全系统每一次鼠标事件，
    回调稍有延迟整个桌面鼠标都会「被拖住」。感知外界变化用 WinEvent（不碰输入流）。
16. **消息泵必须阻塞式 `GetMessage`**，不能 `PeekMessage + Sleep` 轮询：
    WinEvent 回调、跨线程 PostMessage 的落地延迟全由泵决定，轮询平均多拖半个周期。
    **只有返回 0（WM_QUIT）才退出；返回 -1 是调用错误**，记录错误码后退避继续等消息—
    v1.4.4 曾把 -1 也当退出，Modern Standby 唤醒瞬间 Agent 静默消失，托盘空窗 8 小时。
17. `TrayMenuWindow.ShowAt` 的 X 坐标有两种语义：根菜单按**中心点**居中（托盘图标），
    子菜单传**左边**，用 `centerX` 参数区分。搞混了子菜单会左移半个宽度压住父菜单。

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
