# ScreenTime

Windows 本地屏幕时间统计工具。常驻托盘，后台记录前台窗口与音频播放，
数据全部保存在本机 SQLite，**不联网、不需要账号、不上传任何内容**。

![状态](https://img.shields.io/badge/状态-可用-brightgreen) 平台：Windows 10 1809+ / Windows 11 · x64

---

## 快速开始

**环境要求**：Windows 10 1809+ / 11，x64。需要 .NET 8 SDK（构建）。
运行需要 .NET 8 桌面运行时；Windows App SDK 已随发布目录自带，无需单独安装。

**不需要任何环境变量、配置文件或账号。**

```bat
git clone <repo> && cd new-chat
dotnet build ScreenTime.sln -c Release      :: 构建三个工程
dotnet test  ScreenTime.sln -c Release      :: 跑单元测试
build.cmd                                   :: 构建 + 发布到 ScreenTime-WinUI\
```

产物在 `ScreenTime-WinUI\ScreenTime.exe`，双击即可运行（首次启动会进托盘）。

手动发布（等价于 `build.cmd`）：

```bat
dotnet publish ScreenTime.WinUI\ScreenTime.WinUI.csproj -c Release -r win-x64 ^
    --self-contained false -o ScreenTime-WinUI
```

## 项目结构

```
ScreenTime.sln
├─ ScreenTime.Core/      纯逻辑：数据访问 / 分类 / 统计 / 用途（net8.0，不依赖 WinUI，可单测）
├─ ScreenTime.WinUI/     WinUI 3 界面与采集（WinExe，x64）
├─ ScreenTime.Tests/     xUnit 单元测试，只引用 Core
├─ Directory.Build.props 生成 packages.lock.json（依赖锁定）
└─ build.cmd             一键构建 + 发布
```

`ScreenTime.Core` 刻意不引用 WinUI / WindowsAppSDK —— 这样测试不需要拉起 XAML 运行时，
也不需要 Visual Studio 的打包任务，`dotnet test` 在任何装了 .NET 8 SDK 的机器上都能跑。

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
ScreenTime.Core/            纯逻辑，无 UI 依赖
├─ Data/Database.cs         全部 SQL 与迁移
└─ Services/                分类、统计口径、用途推断、显示名清洗、浏览器标题

ScreenTime.WinUI/           WinUI 3 界面与采集
├─ Program.cs               入口（单实例检查在 WinUI 初始化之前）
├─ App.xaml.cs              启动、托盘、命令行
├─ MainWindow.xaml          导航骨架（Mica + 自定义标题栏）
├─ Native/                  Win32 / Core Audio / 会话状态 / UIAutomation / 显示器电源
├─ Services/                采集、图标、自启、浏览器历史回填、UWP 枚举
├─ Pages/                   今日 / 历史 / 报告 / 应用 / 网页 / 收件箱 / 设置
└─ UI/                      环形图、图例、命令等自绘组件
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

## 测试

```bat
dotnet test ScreenTime.sln -c Release
```

114 个用例，覆盖 `ScreenTime.Core` 的纯逻辑：

- **统计口径**（`StatsService`）：区间并集（重叠 / 嵌套 / 相邻 / 乱序 / 倒序 / 空）、
  时长格式化（0、秒级、分钟级、小时级、NaN、Infinity、超大值）
- **分类规则**（`CategoryService`）：空值、大小写、`.exe` 后缀、关键字层、路径层、厂商兜底、
  100 万字符超长输入
- **文本规则**：显示名清洗（通用 ProductName、位数后缀）、外壳/瞬时窗口判断、
  浏览器标题清洗、用途解析
- **数据层**（`Database`）：应用去重、区间裁剪、碎片合并、亮屏事件配对、
  CSV 转义（逗号/引号/换行）、忽略应用排除

测试只引用 `ScreenTime.Core`，不依赖 WinUI 运行时。

## 已知限制

- 只有 x64；没有 ARM64 构建。
- 浏览器页面级统计仅支持 Chrome / Edge，Firefox 未实现。
- **浏览器的「网页应用」(PWA) 不单独计时**：Chrome/Edge 安装的 PWA 窗口与浏览器
  共用同一个进程，时间只能记在浏览器名下。这类启动器（`*_proxy.exe`）不会出现在
  应用列表里，具体网页的时长在「网页」页看得到。
- 单文件发布未启用：WinUI 3 下限制较多，当前是 193 个文件的框架依赖部署。
- 数据库为单机单实例，无多设备同步（这是设计取舍，不是缺陷）。

## 设计文档

界面规范见 [`ScreenTime.WinUI/DESIGN.md`](ScreenTime.WinUI/DESIGN.md)。
