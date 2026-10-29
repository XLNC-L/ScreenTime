# 性能与流畅度方案

记录 v1.2.1 之后的性能现状、已定位的卡顿根因，以及分档可执行的优化清单。
所有数字都来自本机实测（46 万行压测库 / 3 千行真实库），不是估算。

---

## 1. 现状基线

| 指标 | 实测值 | 说明 |
|---|---|---|
| UI 进程工作集（历史页） | 222 MB | 刚启动、7 天数据 |
| Agent 进程 Private | 20.6 MB | 含历史回填服务 |
| 徽标查询（46 万行） | 73 ms | v1.2.1 前是 522 ms |
| `QueryAppUsage` 全库（46 万行） | 521 ms | 读明细到内存做区间并集 |
| `QueryAppUsage` 近 7 天（46 万行） | 108 ms | 时间窗能砍掉 80% |
| 数据库写入量 | 日均 ~1200 条片段 | 约 0.4 MB/天 |

一年后的库约 45 万行，上面两个 500ms 级查询会变成启动/切页的固定成本。

---

## 2. 「未响应」的三个根因

WinUI 的「未响应」只有一个含义：**UI 线程被阻塞超过 5 秒**（Windows 判定无响应）。
所以问题不在渲染，而在**UI 线程上做了什么**。

### 根因 A：UI 线程写库时被 Agent 的写锁挡住（最可能）

- 两个进程共享一个 SQLite 文件，`busy_timeout=3000`。
- Agent 每 5 秒心跳写一次，回填/迁移时可能持有写锁更久。
- UI 侧所有写操作（改分类、设用途、忽略、合并）都是**在 UI 线程同步执行**的
  （`AppActionMenu` → `Database.SetAppCategory` → `ExecuteNonQuery`）。
- 一旦撞上 Agent 的写事务，UI 线程就原地等，最长 3 秒；连续几次操作会叠加成"点了没反应"。

**判断方法**：卡顿是否只发生在「右键改分类 / 点忽略」这类写操作之后？
如果是，基本可以确定。

### 根因 B：历史页 / 网页页的查询与聚合在 UI 线程

- `HistoryPage.RunQuery()` → `RunQueryCore()` 全程同步：查应用、查片段、算并集、
  再跑 `LoadReportExtras()`（每日分桶 + 24 小时分桶，双重循环）。
- 7 天 3 千行约 100 ms，感觉不到；用户把日期拉到 30 天/自定义大范围时是 O(行数 × 天数)，
  很容易到秒级。
- `WebPage` 的聚合同理。
- 对照：`TodayPage` 已经在 v1.1.2 改成 `Task.Run` + 门控，所以今日页不卡、历史页卡，
  这个差异本身就是证据。

### 根因 C：启动时同步初始化数据库

- `App` 构造函数里同步 `Database.Init()`：建表、补列、`MarkNoiseAppsIgnored`（全表 UPDATE）、
  以及 `PRAGMA journal_mode=WAL`。
- **切换 journal_mode 需要排他锁**：如果此刻 Agent 正在写，UI 会一直等到拿锁为止，
  窗口迟迟不出现。这是"双击图标半天没反应"的典型。
- `Migrate()` 已经挪到后台，但 `Init()` 还在前台。

---

## 3. 跟手度优化清单

按「收益 / 工作量」排序，前三项建议先做。

### 第一档：小改动，直接消除卡顿

**3.1 所有写操作移出 UI 线程**

```csharp
// AppActionMenu 里现在是同步调用
App.Current.Database.SetAppCategory(app.Id, value);
App.Current.RefreshCurrentPage();
```

改成：

```csharp
await Task.Run(() => App.Current.Database.SetAppCategory(app.Id, value));
App.Current.RefreshCurrentPage();
```

- 菜单项点击是 `async void` 事件，天然支持。
- 改完 UI 线程不再碰数据库写，根因 A 直接消失。
- 工作量：`AppActionMenu.cs` + `SettingsPage` 的几个开关，约 1 小时。

**3.2 `Init()` 只做必要的事**

- `PRAGMA journal_mode=WAL` 改成「先读 `PRAGMA journal_mode`，已经是 wal 就跳过」，
  避免每次启动都去抢排他锁。
- `MarkNoiseAppsIgnored` 加个「上次执行时间」标记（存 settings），
  一天最多跑一次，而不是每次启动都全表 UPDATE。
- 工作量：半小时。

**3.3 日期/范围切换加防抖**

- `FromPicker`/`ToPicker` 的 `DateChanged` 直接 `RunQuery()`，
  连续点两下日期就是两次全量查询。
- 加 200 ms 防抖（和 `TimelineGrid` 的 150 ms 防抖同一套写法）。
- 工作量：20 分钟。

### 第二档：中等改动，解决大范围查询

**3.4 历史页查询异步化**

把 `RunQueryCore()` 拆成「后台查询 + UI 绑定」两段，模式照抄 `TodayPage.RefreshChartsAsync`：

```csharp
private async Task RunQueryAsync()
{
    if (!await _queryGate.WaitAsync(0)) return;
    try
    {
        var snapshot = await Task.Run(() =>
        {
            var apps = _db.QueryAppUsage(from, to);
            var segments = _db.QuerySegments(from, to);
            return (apps, segments);
        });
        // 回到 UI 线程只做绑定与绘制
        BindUi(snapshot.apps, snapshot.segments);
    }
    finally { _queryGate.Release(); }
}
```

- `LoadReportExtras` 的双重循环也放进 `Task.Run`，只把算好的柱子数据带回 UI。
- 工作量：半天。

**3.5 网页页同理**

`WebPage` 的 `QueryWebPages` + `QueryWebTimeline` + 分组聚合一起挪到后台。

**3.6 列表增量更新**

现在每次刷新都是 `AppList.ItemsSource = 新的 List`，等于整表重建，
ListView 的虚拟化优势被抵消一半。

- 改成 `ObservableCollection<AppRankItem>`，只增删变化的项。
- 或者至少：数据没变时不重设 `ItemsSource`（比较行数 + 首尾 id）。
- 工作量：半天，收益在"每 30 秒自动刷新"的场景下明显。

### 第三档：大改，收益看场景

**3.7 `{Binding}` → `x:Bind`**

- 项目里 DataTemplate 全是 `{Binding}`，走反射；`x:Bind` 是编译期生成的强类型绑定，
  首屏和滚动都更快。
- 代价：每个 DataTemplate 要加 `x:DataType`，绑定路径要改，约 1-2 天。
- 建议：只在应用排行、网页列表这两个长列表上做，别全项目铺开。

**3.8 页面缓存策略**

- 现在 `NavigationCacheMode=Disabled`（为了切页能释放内存），代价是每次切页都重建 + 重新查库 + 重新取图标。
- 折中：给 `Frame` 设 `CacheSize=2`，保留最近两页；或在页面里缓存查询结果 30 秒。
- 内存换跟手度，按你的偏好选。

---

## 4. 内存优化清单

### 4.1 现状拆解（UI 222 MB）

| 组成 | 估计 | 说明 |
|---|---|---|
| WinUI 3 + WindowsAppSDK 基线 | 70-80 MB | 框架依赖，压不下去 |
| 页面对象 + XAML 树 | 40-60 MB | 历史页控件最多 |
| 图标（ImageSource） | 20-40 MB | 128 条上限，到顶整表清空 |
| 数据列表 / 中间集合 | 20-40 MB | 每次刷新重建 |
| 其他（字体、着色器、缓存） | 20-30 MB | |

### 4.2 可做的

**4.2.1 图标缓存改 LRU**

现在是「到 128 条就 `Clear()` 全清」，清空后下一屏又要重新从 exe 提取一遍 ——
既费 CPU 又造成滚动时白块。改成 LRU 淘汰最久未用的 32 条，命中率会高很多。

**4.2.2 图标解码尺寸**

`AppIconService` 已经是按 24 DIP 解码（2x 下 48px），这点做得对，不用改。

**4.2.3 切页时主动释放**

`NavigationCacheMode=Disabled` 已经让页面能被回收，但 `Frame` 的 `Content` 在切页瞬间
仍持有旧页面直到 GC。可以在 `Unloaded` 里显式把大集合置空：

```csharp
Unloaded += (_, _) =>
{
    _allRows = new();
    AppList.ItemsSource = null;
    _iconLoadCts?.Cancel();
};
```

**4.2.4 定期修剪工作集**

v1.1.2 在托盘态做过「每 30 分钟 GC + 工作集修剪」。现在 UI 关闭即退出，
这个逻辑已经没必要；但如果用户会长时间开着窗口，可以在 `Window.Activated`
失焦时做一次 `GC.Collect(2, GCCollectionMode.Optimized)` + `SetProcessWorkingSetSize`。
注意别在用户正在操作时做。

**4.2.5 Agent 侧**

20 MB 属于正常范围（Win32 消息泵 + 采集 + 回填服务）。
如果还想压，可以把 `HistoryImportService` 的定时器改成「只在有候选片段时才启动」，
平时不占 Timer 和 Sqlite 连接。

---

## 5. 怎么验证改完了有没有用

1. **卡顿**：改之前先用 `dotnet-trace collect --providers Microsoft-Windows-DotNETRuntime`
   抓一次「改分类」的火焰图，看 UI 线程是不是卡在 `SqliteCommand.ExecuteNonQuery`。
2. **启动**：`dotnet-counters monitor --process-id <pid> -c System.Runtime` 看
   `ThreadPool Queue Length` 和 GC 次数。
3. **内存**：任务管理器看「专用工作集」；或 `dotnet-counters` 看 `GC Heap Size`。
4. **数据库**：`sqlite3 screentime.db "PRAGMA wal_checkpoint(TRUNCATE)"` 看 WAL 大小，
   WAL 长期不 checkpoint 会让读变慢。

---

## 6. 一句话结论

「未响应」几乎肯定不是渲染问题，而是 **UI 线程在等数据库**：
等 Agent 的写锁（根因 A）、等自己的同步查询（根因 B）、等启动时的 journal_mode 切换（根因 C）。
把这三处的阻塞移出 UI 线程，跟手度会有质的变化；内存则是「够用就行」，
222 MB 对 WinUI 3 应用不算异常，真正该做的是图标缓存改 LRU 和切页释放。
