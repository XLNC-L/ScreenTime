# ScreenTime — WinUI 3 / Fluent 2 设计蓝图

技术栈：**WinUI 3 + Windows App SDK 1.6 + C#/XAML**，unpackaged（WindowsPackageType=None）× x64。
部署形态为**框架依赖的多文件输出**（约 190 个文件 / 120 MB，Windows App SDK 自带运行时、.NET 依赖目标机安装），
未启用单文件发布——WinUI 3 下 `PublishSingleFile` 限制较多，收益不明显。
逻辑层（Native/AudioMonitor/Database/WatcherService/CategoryService/StatsService）原样复用，仅重写 UI。

---

## 1. 应用骨架与窗口

- `MainWindow : Microsoft.UI.Xaml.Window`
  - `SystemBackdrop = new MicaBackdrop()`（主窗口 Mica，非 Acrylic）
  - `ExtendsContentIntoTitleBar = true`，用 `SetTitleBar` 设置 8px 拖拽区（标题栏保持系统原生按钮，不手绘）
  - 根容器：`NavigationView`，`PaneDisplayMode=Left`，`IsBackButtonVisible=Collapsed`
  - 内容承载：`Frame`，`NavigationView.SelectionChanged` 中 `Navigate`；或用 `NavigationViewItem` 的数据绑定
  - 关闭窗口（`AppWindow.Closing`）→ 取消关闭、`Hide()`（最小化到托盘）
- 托盘：`H.NotifyIcon.WinUI`（WinUI 无内置托盘），`MenuFlyout`：打开统计 / 退出；左键单击恢复
- 单实例：`Mutex`（沿用现有逻辑）

## 2. 导航项（NavigationView + Segoe Fluent Icons）

| 页面 | 图标 (FontIcon Glyph) |
|---|---|
| 今日 | `` Home (`\uE80F`) |
| 历史 | `` Calendar (`\uE787`) |
| 报告 | `` Diagnostic / BarChart (`\uE9F9`) |
| 应用 | `` AllApps (`\uE71D`) |
| 设置 | `` Settings (`\uE713`) |

---

## 3. 设计令牌（Design Tokens）

### 3.1 字体 — Segoe UI Variable，WinUI Type Ramp
| 用途 | 字号/字重 | Token 对应 |
|---|---|---|
| Caption | 12 Regular | `CaptionTextBlockStyle` |
| Body | 14 Regular | `BodyTextBlockStyle` |
| Body Strong | 14 SemiBold | `BodyStrongTextBlockStyle` |
| Subtitle | 20 SemiBold | `SubtitleTextBlockStyle` |
| Title | 28 SemiBold | `TitleTextBlockStyle` |
| Title Large | 40 SemiBold | `TitleLargeTextBlockStyle` |

统计卡片内的大数字用 **Title 28**；页面主标题用 **Title 28**；卡片标题用 **Subtitle 20**；标签用 **Caption 12（次色）**。

### 3.2 间距（唯一允许值）
4 / 8 / 12 / 16 / 24 / 32。
- 卡片内边距：16；卡片间距：12/16；页面左右边距：32；区块上下间距：24。

### 3.3 圆角
- 控件：`ControlCornerRadius`（默认 4）
- 卡片/浮层：`OverlayCornerRadius`（默认 8）
- 禁止 16/24 大圆角，禁止胶囊按钮。

### 3.4 材质层级
- 主窗口：**MicaBackdrop**（页面背景透明，让 Mica 透出）
- 卡片：`CardBackgroundFillColorDefaultBrush` + `CardStrokeColorDefaultBrush`（1px 描边，不透明，不浮起）
- 次级容器：`LayerFillColorDefaultBrush`
- 临时浮层（MenuFlyout / ComboBox 下拉 / TeachingTip / ToolTip）：系统默认 Acrylic
- 禁止全屏毛玻璃、禁止每张卡片半透明、禁止重阴影/渐变。

### 3.5 文本与系统画笔（全部 ThemeResource）
- 主文本：`TextFillColorPrimaryBrush`
- 次文本：`TextFillColorSecondaryBrush`（标签、副标题、时间）
- 三级文本：`TextFillColorTertiaryBrush`
- 强调：`AccentTextFillColorPrimaryBrush`（环比上涨）、`SystemFillColorSuccess`（下降）
- 卡片：`CardBackgroundFillColorDefaultBrush` + `CardStrokeColorDefaultBrush`
- 进度轨：`SubtleFillColorSecondaryBrush`

### 3.6 数据可视化分类色（浅/深两套）
| 分类 | Light | Dark |
|---|---|---|
| 浏览器 | #0078D4 | #4CC2FF |
| 开发 | #8764B8 | #C4B5FD |
| 游戏 | #107C10 | #6CCB5F |
| 办公 | #CA5010 | #FFB900 |
| 社交 | #00B7C3 | #4DD9E6 |
| 影音 | #C42B1C | #FF99A4 |
| 设计创作 | #B146C2 | #E689EE |
| 系统 | #6E6E6E | #A1A1A1 |
| 其他 | #848691 | #989BA5 |

实现方式：色值定义在 `Themes/CategoryPalette.cs`，`BrushFor(category)` 按 `CurrentTheme` 返回对应画刷。
主题切换时由 `MainWindow` 监听 `ContentFrame.ActualThemeChanged` 更新 `CurrentTheme` 并重建当前页，
环形图/柱状图/时间线的配色立即刷新，无需重启。
（未使用 `ThemeDictionaries` 资源字典：图表是自绘 `Path`/`Rectangle`，颜色由 C# 直接赋给 `Fill`，
走资源字典反而要多一层 `Application.Current.Resources` 查找。）

---

## 4. 各页面布局

### 4.1 今日页 TodayPage
- 页头：`TextBlock "今日" Title` + `TextBlock 日期 Body(次)`
- 概览卡片区：`ItemsWrapGrid`（或 `GridView`），4 卡片，最小宽 200：
  - 今日总时长 / 前台时长 / 后台播放 / 开机时间
  - 卡片：`Border CornerRadius=8` + Card 背景/描边，Padding 16
  - 内部：`Caption`(次) 标签 + `Title` 数值
- 中部两列（`Grid` 两列，间距 16）：
  - 左卡 **分类占比**：环形图（`Canvas` + `Path` ArcSegment 自绘）+ 图例（`ItemsControl`，色块 12px + 名称 + 百分比）
  - 右卡 **应用排行（前台/后台播放）**：`ListView`，项 = 名称(Body Strong) + 进度条(`ProgressBar` 或 `Rectangle` 自绘) + 时长(Body 次)
- 底卡 **今日时间线**：`Canvas` 自绘**自适应使用时段 + 动态刻度**（不是固定 24 小时轨道）——
  轨道只覆盖当天实际有记录的区间，两端各留一点余量，刻度按跨度自动选步长；
  各前台段按分类色绘制，`ToolTip` 显示 时段/应用/标题；当前时间游标 2px。
  （比固定 24h 轨道更好读：数据集中在白天时不会被压成一小段。）
- 10 秒 `DispatcherTimer` 刷新

### 4.2 历史页 HistoryPage
- 页头 + 控制条：`DatePicker`（起）、`DatePicker`（止）、`Button "查询"`（图标 + 文字）
- 概览卡片：总时长 / 日均 / 天数
- 分类占比卡 + 应用排行卡（同今日）

### 4.3 报告页 ReportsPage
- 页头 + 控制条：`ComboBox`（按周/按月）或 `ToggleButton` 分段；`Button` 上一/下一；周期标签 `Subtitle`
- 概览卡片：总时长 / 日均 / 环比（带 `FontIcon` 上下箭头，颜色按 3.5）
- **每日柱状图**：横向 `ScrollViewer`，`ItemsControl` 固定柱宽（柱间距 8，柱宽 26）；柱体 `Rectangle` 圆角 4，按当日主导分类色；顶部数值（Caption）；下方日期标签（≤10 天全显示，按月每 5 天一个）；`ToolTip`
- 分类占比卡
- 两列：**最活跃时段**（24 小时热力条，按小时切分累计）+ **Top 应用**（`ListView` Top 8）

### 4.4 应用页 AppsPage
- 页头工具条：`AutoSuggestBox`（占位文字"搜索应用"）+ 分类筛选 `ComboBox` + `Button "导入已安装应用"`
  + `CheckBox "仅显示有使用记录"`（隐藏导入后时长为 0 的条目）+ `ToggleSwitch "显示已忽略"`
- 两列：
  - 左：`ListView` 应用列表（名称 Body Strong + 进程名/分类 Caption 次）
  - 右卡 **应用详情**：应用名(Subtitle) + 进程/分类(Body 次)
    - 分类：`ComboBox`
    - 忽略：`ToggleSwitch`
    - 合并到：`ComboBox` + `Button "合并"`
    - 确认/危险操作使用 `ContentDialog`

### 4.5 网页页 WebPage
- 页头：`TextBlock "网页" Title` + **浏览器筛选 `ComboBox`**（全部 / 数据里出现过的浏览器）+ 时间范围 `ComboBox`（7/30/90 天）
- 摘要行：`{浏览器} · 近 N 天共 X 个页面，网页时长 …`（选「全部」时不带浏览器前缀）
- 列表：按域名分组的 `ItemsControl`
  - 分组头：域名（Body Strong）+ 该域名合计时长
  - 页面行：**行首 14px 浏览器图标**（来自 apps 表记录的 exe，浏览器没运行也能取到）+ 页面标题 + 页面地址（Caption 次）+ 时长
  - 同一网址在 Chrome 和 Edge 各算一行——浏览器是聚合维度的一部分，否则图标无法标明来源
  - 行可点击，用默认浏览器打开原始地址

### 4.6 设置页 SettingsPage
- 全部用 **SettingsCard**（分组）+ 可选 **SettingsExpander**：
  - 常规：开机自启 `ToggleSwitch`、音频计数 `ToggleSwitch`
  - 判定：无操作时长 `ComboBox`（60/120/180/300/600）、标题保留 `ComboBox`（7/30/90/永久）
  - 数据：更改数据位置 `Button`、打开文件夹 `Button`、导出 CSV `Button`（`FileSavePicker`）、
    备份数据库 `Button`、清除全部数据 `Button`（→ `ContentDialog` 二次确认）
  - 关于：版本/说明（Body 次）
- 操作反馈用 `InfoBar`（成功/警告），不使用弹消息框。

---

## 5. 状态、可访问性、动效
- 所有控件使用默认 Hover/Pressed/Disabled/Focus 视觉状态；焦点矩形 2px 可见；键盘可达。
- 图标统一 `FontIcon`（Segoe Fluent Icons），尺寸 16/20/24；不使用 emoji。
- 动效 150–250ms，使用系统默认过渡（`ThemeTransition`），尊重"减少动画"设置。

## 6. 图表自绘边界（非"手绘控件"）
- 控件（按钮/开关/下拉/卡片/列表/对话框）一律用 WinUI 原生。
- 环形图、时间线、柱状图属于**数据可视化**，用 `Microsoft.UI.Xaml.Shapes`（Path/Rectangle/ArcSegment）+ Canvas 绘制，是 WinUI 无内置图表时的标准做法，不引入第三方、不仿造系统控件。

---

## 7. 对照 WinUI 3 Gallery 自查清单（实现后逐项核对）
- [ ] 主窗口 MicaBackdrop，页面背景透明，无全屏 Acrylic
- [ ] 卡片 CardBackgroundFillColorDefaultBrush + 描边，不透明、不浮起
- [ ] 字体仅用 Type Ramp 六档，无随机字号/网页字体
- [ ] 间距仅 4/8/12/16/24/32；控件圆角 4、卡片 8，无大圆角/胶囊
- [ ] NavigationView 左导航 + FontIcon
- [ ] 设置页 SettingsCard/SettingsExpander + ToggleSwitch/ComboBox
- [ ] 颜色全部 ThemeResource，浅/深/高对比度正确
- [ ] 对话框 ContentDialog、反馈 InfoBar、浮层 MenuFlyout/TeachingTip
- [ ] 焦点矩形可见、键盘可达
- [ ] 无 emoji、无渐变、无重阴影、无自创控件
- [ ] 关闭=最小化托盘，托盘左键恢复、右键菜单
