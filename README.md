# ScreenTime

Windows 本地屏幕时间统计工具。常驻托盘，后台记录前台窗口与音频播放，
数据全部保存在本机 SQLite，**不联网、不需要账号、不上传任何内容**。

![状态](https://img.shields.io/badge/状态-可用-brightgreen) 平台：Windows 10 1809+ / Windows 11 · x64

---

## 安装

从 [Releases](../../releases) 下载 `ScreenTime-Setup-x.y.z.exe`，双击安装。

- **装到用户目录**（`%LocalAppData%\Programs\ScreenTime`），**全程不需要管理员权限、不弹 UAC**
- 需要 **.NET 8 Runtime（x64）**；安装向导会检测，缺了会给出下载链接
- 安装包约 22 MB（压缩后），装完约 95 MB / 281 个文件

> 未做代码签名，首次运行 Windows 可能提示"未知发布者"，点「更多信息 → 仍要运行」即可。
> 可对照 Release 里的 `SHA256SUMS.txt` 校验下载的文件。

### 卸载

设置 → 应用 → ScreenTime → 卸载（或开始菜单里右键卸载）。卸载时会弹窗说明，
可勾选是否**同时删除使用数据**（`%AppData%\ScreenTime`，默认保留）。

⚠️ 卸载前请先在托盘图标上右键退出，否则程序文件被占用会导致卸载失败。

## 从源码构建

```bat
dotnet build ScreenTime.sln -c Release      :: 构建三个工程
dotnet test  ScreenTime.sln -c Release      :: 跑单元测试
build.cmd                                   :: 构建 + 发布到 ScreenTime-WinUI\
```

**环境要求**：Windows 10 1809+ / 11，x64，.NET 8 SDK。
**不需要任何环境变量、配置文件或账号。**

手动发布（等价于 `build.cmd`）：

```bat
dotnet publish ScreenTime.WinUI\ScreenTime.WinUI.csproj -c Release -r win-x64 ^
    --self-contained false -o ScreenTime-WinUI
dotnet publish ScreenTime.Agent\ScreenTime.Agent.csproj -c Release -r win-x64 ^
    --self-contained false -o ScreenTime-WinUI
```

两种发布形态都支持，按用途选：

| 形态 | 参数 | 体积 | 目标机要求 |
|---|---|---|---|
| 框架依赖 | `--self-contained false` | 约 95 MB / 280 文件 | 需装 .NET 8 Desktop Runtime |
| 自包含 | `--self-contained true` | 约 210 MB / 380 文件 | 无需任何运行时，解压即用 |

> `WindowsPackageType=None`（非 MSIX）下 `dotnet publish` **不会自动复制 `.xbf`**，
> 少了它发布版会直接启动失败。项目里已有 `CopyXbfToPublishDir` 目标自动补齐，
> 如果你改发布脚本后启动即崩，先检查发布目录里有没有 `App.xbf` 和 `Pages\*.xbf`。

打安装包（需要 [Inno Setup 6.3+](https://jrsoftware.org/isdl.php)）：

```bat
ISCC.exe /DAppVersion=1.0.0 installer\ScreenTime.iss
:: 产物：dist\ScreenTime-Setup-1.0.0.exe
```

打 tag 推上去会由 `.github/workflows/release.yml` 自动完成上面全部步骤并挂到 Release。

## 项目结构

```
ScreenTime.sln
├─ ScreenTime.Core/      纯逻辑：数据访问 / 分类 / 统计 / 用途（net8.0，不依赖 WinUI，可单测）
├─ ScreenTime.Collect/   采集层：前台窗口 / 音频 / 会话事件 / UIA / 浏览器历史回填（无 UI）
├─ ScreenTime.Agent/     采集器进程：Win32 托盘 + 常驻采集，无 XAML 依赖
├─ ScreenTime.WinUI/     界面进程：WinUI 3，关闭窗口即退出
├─ ScreenTime.Tests/     xUnit 单元测试，只引用 Core
├─ Directory.Build.props 生成 packages.lock.json（依赖锁定）
└─ build.cmd             一键构建 + 发布
```

v1.2.0 起是**双进程架构**：`ScreenTime.Agent.exe` 常驻托盘负责采集（Private 约 13-16MB），
`ScreenTime.exe` 只负责界面、关掉就退出。两者共享同一个 SQLite 库，通过 `settings` 表通信
（用途标注 `pending_purpose`、设置热重载 `settings_version`）。

`ScreenTime.Core` 与 `ScreenTime.Collect` 刻意不引用 WinUI / WindowsAppSDK —— 这样测试不需要拉起 XAML 运行时，
也不需要 Visual Studio 的打包任务，`dotnet test` 在任何装了 .NET 8 SDK 的机器上都能跑。

## 命令行参数

| 参数 | 说明 |
|---|---|
| `--page=today\|history\|web\|inbox\|settings` | 启动后直达指定页面（`reports` / `apps` 作为旧名兼容，都落到 `history`） |
| `--db=<路径>` | 使用指定的数据库文件，不读写默认位置配置（测试/演示用） |
| `--seed-demo` | 生成 90 天演示数据。**必须同时指定 `--db`**，否则拒绝执行 |

再次启动 exe（已有一个实例在跑时）会唤起已有实例的主窗口，而不是静默退出。

> `--silent`（只建托盘不弹窗）已在 v1.2.0 移除：托盘与采集都归 Agent 进程，
> UI 进程不再常驻。开机自启的任务现在直接指向 `ScreenTime.Agent.exe`。

## 开机自启

在「设置 → 开机自启」里开启。实现方式是任务计划程序（`ONLOGON`），不是注册表 Run 键。

**默认以普通权限运行，创建任务不需要管理员权限、不会弹 UAC。**

实测结论：普通权限的 ScreenTime 能完整记录管理员应用——进程名、exe 路径、
窗口标题都能拿到（用任务管理器验证过）。所以「以最高权限运行」默认关闭，
只有个别应用读不到标题时才需要打开（打开后创建任务会走 UAC，
且每次登录都以管理员身份启动）。

**关闭/卸载**：设置页把「开机自启」关掉即可，会删除计划任务 `ScreenTime`；
再配合「清除全部数据」和删除程序目录就是完整卸载。

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

ScreenTime.Collect/         采集层，UI 与 Agent 共用
├─ Native/                  Win32 / Core Audio / 会话状态 / UIAutomation / 显示器电源
└─ Services/                前台与音频采集、浏览器标签、UWP 枚举、历史回填

ScreenTime.Agent/           采集器进程（无 UI）
├─ Program.cs               单实例、采集启动、自启注册、崩溃自恢复
└─ Native/TrayIcon.cs       纯 Win32 托盘（NOTIFYICON + 消息泵）

ScreenTime.WinUI/           界面进程
├─ Program.cs               入口（单实例检查在 WinUI 初始化之前）
├─ App.xaml.cs              启动、命名管道唤起、拉起 Agent
├─ MainWindow.xaml          导航骨架（Mica + 自定义标题栏）
├─ Services/                图标、自启、UWP 枚举
├─ Pages/                   今日 / 历史（含应用管理）/ 网页 / 待办 / 设置
└─ UI/                      环形图、图例、右键菜单等自绘组件
```

### 采集机制

- **双通道**：前台通道按窗口切换事件即时结算 + 5 秒心跳；音频通道按 Core Audio
  真实峰值判断，静音或暂停不计时。两段区间取**并集**，同一应用在前台播放声音不会重复计时。
- **干扰过滤**：系统外壳进程（开始菜单、搜索、锁屏等）直接穿透，不打断当前真实应用；
  UWP 通过 `ApplicationFrameHost` 子窗口还原真实进程；安装器在 Temp 解压出的瞬时进程不计入。
- **浏览器页面级**：UIA 读 Document 元素的 URL，拿不到时从 Chrome/Edge 本地历史库回填
  （只读、跳过邮件与网银域名）。回填跑在 Agent 进程里，UI 关掉也照常补。
- **碎片治理**：标题在片段前 3 秒内连跳时只就地更新、不切分；每 5 分钟把最近 2 小时里
  被切碎的短片段并回相邻同应用片段。
- **噪声过滤**：系统宿主 / 安装器 / Temp 解压出的瞬时进程（`msiexec`、`wixstdba`、
  `svchost` 等 40 余个）在采集、收件箱、历史统计三处共用同一份名单，不会出现
  「收件箱里看不见、历史里还在计时」。

## 测试

```bat
dotnet test ScreenTime.sln -c Release
```

135 个用例，覆盖 `ScreenTime.Core` 的纯逻辑：

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
- **待办只看最近 30 天**：更早的未分类应用不再提醒（避免老数据一直堆在待办里）。
- **数据库没有自动清理**：明细永久保留（实测约 286 B/行、日均 545 行，
  约 155 KB/天、4.6 MB/月、57 MB/年）。想删旧数据目前只能「清除全部数据」。
- 单文件发布未启用：WinUI 3 下限制较多，当前是 279 个文件 / 95 MB 的框架依赖部署。
- 数据库为单机单实例，无多设备同步（这是设计取舍，不是缺陷）。

## 设计文档

界面规范见 [`ScreenTime.WinUI/DESIGN.md`](ScreenTime.WinUI/DESIGN.md)。
