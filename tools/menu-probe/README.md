# 托盘菜单探针（MenuProbe）

验证自绘托盘菜单的生命周期：**不套娃、点菜单外面即关、移出即关、子菜单父子一起关**。

它不是单元测试——单元测试碰不到 Win32 窗口，这里直接编译
`ScreenTime.Agent/Native/TrayMenuWindow.cs` 的同一份源码，用真实注入的鼠标事件驱动窗口消息。

## 用法

```bat
dotnet run --project tools\menu-probe                  :: 进程内全套用例（9 项断言）
dotnet run --project tools\menu-probe -- live          :: 对正在运行的 Agent 做端到端验证
dotnet run --project tools\menu-probe -- count         :: 看系统里还残留几个菜单窗口（应为 0）
```

退出码 0 = 全部通过。`live` 模式会移动并点击鼠标（落点是一个临时透明小窗，不会误点桌面图标），
跑之前先把鼠标放开。

## 判据

| 场景 | 期望 |
|---|---|
| 弹出一次 | 1 个窗口 |
| 连续弹出三次 | 仍是 1 个（旧版会叠） |
| 点菜单外 | 立刻 0 个 |
| 指针移出菜单 | 250ms 后 0 个 |
| 悬停「暂停统计」 | 2 个（父 + 子）；点外面一起归零 |
| 点菜单项 | 回调触发 1 次且窗口归零 |
| 反复 20 次开合 | 无残留 |

## 历史

- v1.4.2 之前：关闭逻辑跑在定时器线程，`DestroyWindow` 跨线程静默失败，
  窗口句柄又被移出路由表 → 菜单永远留在屏幕上、每点一次多一层。
  这套用例在旧代码上有 6 项失败（窗口数从 1 递增到 3），修复后全绿。

## 注意

- 用 `SetCursorPos` **瞬移**不会产生 `WM_MOUSELEAVE`，要逐点移动（见 `GlideTo`）。
- 探针的窗口类名过滤必须区分「本进程」和「Agent 进程」，否则会把 Agent 的窗口算进来。
- 本工程不加入 `ScreenTime.sln`，`build.cmd` / `tools\publish.ps1` 都不会碰它。
