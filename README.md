# ScreenTime

Windows 本地屏幕时间统计工具。常驻托盘，后台记录前台窗口与音频播放，
数据全部保存在本机 SQLite，**不联网、不需要账号、不上传任何内容**。

![状态](https://img.shields.io/badge/状态-可用-brightgreen) 平台：Windows 10 1809+ / Windows 11 · x64

---

## 快速开始

```bat
build.cmd
```

或手动：

```bat
dotnet build ScreenTime.WinUI\ScreenTime.WinUI.csproj -c Release
dotnet publish ScreenTime.WinUI\ScreenTime.WinUI.csproj -c Release -r win-x64 ^
    --self-contained false -o ScreenTime-WinUI
```

产物在 `ScreenTime-WinUI\ScreenTime.exe`，双击即可运行（首次启动会进托盘）。

**环境要求**：.NET 8 SDK（构建）、.NET 8 桌面运行时（运行）。
Windows App SDK 已随发布目录自带，无需单独安装。

## 命令行参数

| 参数 | 说明 |
|---|---|
| `--silent` | 静默启动，只创建托盘图标，不弹主窗口（开机自启用这个） |
| `--page=today\|history\|reports\|apps\|web\|inbox\|settings` | 启动后直达指定页面 |
| `--db=<路径>` | 使用指定的数据库文件，不读写默认位置配置（测试/演示用） |
| `--seed-demo` | 生成 90 天演示数据。**必须同时指定 `--db`**，否则拒绝执行 |

再次启动 exe（已有一个实例在跑时）会唤起已有实例的主窗口，而不是静默退出。

### 生成演示数据

库里只有几天真实数据时，周报/月报/环比的逻辑没法验证。可以生成一份独立演示库：

```bat
ScreenTime.exe --db=%TEMP%\st-demo\demo.db --seed-demo   :: 生成后立即退出
ScreenTime.exe --db=%TEMP%\st-demo\demo.db               :: 用演示库启动查看
```

演示库与真实库完全隔离，删掉那个文件夹即可清理。

## 数据

默认位置：`%AppData%\ScreenTime\screentime.db`（SQLite，WAL 模式）。
可在「设置 → 数据位置」迁移到其他文件夹，设置页还提供 **导出 CSV**（Excel 直接打开）、
**导出 JSON**（便于脚本处理）与 **备份数据库**。

### 表结构

| 表 | 内容 |
|---|---|
| `apps` | 应用清单：exe 路径、进程名、显示名、分类、默认用途、忽略/合并标记 |
| `usage_segments` | 使用片段：应用、类型（前台/音频）、起止时间、窗口标题、网页、用途 |
| `session_events` | 开机/锁屏/解锁/休眠/关机事件 |
| `settings` | 键值设置 |

## 代码结构

```
ScreenTime.WinUI/
├─ App.xaml.cs            启动、单实例、托盘、命令行
├─ MainWindow.xaml        导航骨架（Mica + 自定义标题栏）
├─ Native/                Win32 / Core Audio / 会话状态 / UIAutomation 封装
├─ Data/Database.cs       全部 SQL 与迁移
├─ Services/              采集、分类、用途、统计、图标、自启、浏览器历史回填
├─ Pages/                 今日 / 历史 / 报告 / 应用 / 网页 / 收件箱 / 设置
└─ UI/                    环形图、图例、命令等自绘组件
```

### 采集机制

- **双通道**：前台通道按窗口切换事件即时结算 + 5 秒心跳；音频通道按 Core Audio
  真实峰值判断，静音或暂停不计时。两段区间取**并集**，同一应用在前台播放声音不会重复计时。
- **干扰过滤**：系统外壳进程（开始菜单、搜索、锁屏等）直接穿透，不打断当前真实应用；
  UWP 通过 `ApplicationFrameHost` 子窗口还原真实进程；安装器在 Temp 解压出的瞬时进程不计入。
- **浏览器页面级**：UIA 读 Document 元素的 URL，拿不到时从 Chrome/Edge 本地历史库回填
  （只读、跳过邮件与网银域名）。
- **碎片治理**：标题在片段前 3 秒内连跳时只就地更新、不切分；每 5 分钟把最近 2 小时里
  被切碎的短片段并回相邻同应用片段。

## 已知限制

- 只有 x64；没有 ARM64 构建。
- 浏览器页面级统计仅支持 Chrome / Edge，Firefox 未实现。
- 单文件发布未启用：WinUI 3 下限制较多，当前是 193 个文件的框架依赖部署。
- 数据库为单机单实例，无多设备同步（这是设计取舍，不是缺陷）。

## 设计文档

界面规范见 [`ScreenTime.WinUI/DESIGN.md`](ScreenTime.WinUI/DESIGN.md)。
