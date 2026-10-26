# 设计与实现细节

> 面向开发者。使用说明见 [README](../README.md)。

## 目录结构

```
PowerGuardDisplay.slnx           解决方案（新版 XML 格式，需要 .NET 9.0.200+ / 10 SDK）
global.json
publish.ps1                      发布脚本（自包含 + 依赖框架两种包）
.github/workflows/ci.yml         windows-latest：构建、测试、发布产物
tools/gen-icons.py               生成托盘 / 程序图标
src/
  PowerGuard.Core/               平台无关的类库（net10.0），可在任何系统上测试
    Models.cs                    电源/显示模式/状态枚举，DisplaySwitch 参数映射
    Settings.cs                  AppSettings（settings.json）、RecoveryState（recovery-state.json）
    JsonFileStore.cs             原子写入（临时文件 + Flush + 替换）、损坏文件备份
    Logging.cs                   滚动日志（按天 + 按大小，JSON Lines）
    RecoveryCoordinator.cs       断电保护与自动恢复状态机（含关机前恢复）
    ShutdownRestorePolicy.cs     关机前是否切回首选模式的决策（纯函数）
    TopologyClassifier.cs        由显示路径推导当前模式（纯函数）
    StartupTask.cs               任务计划程序 XML 生成、schtasks 调用与解析
    Abstractions.cs              IClock / IDelay / IDisplayTopologyProvider / IDisplaySwitcher / IProcessRunner …
  PowerGuard.App/                WPF 应用（net10.0-windows）
    Native/NativeMethods.cs      P/Invoke 声明
    Platform/SystemMonitor.cs    隐藏窗口：RegisterPowerSettingNotification、WM_POWERBROADCAST、WM_DISPLAYCHANGE
    Platform/DisplayTopologyProvider.cs  QueryDisplayConfig / DisplayConfigGetDeviceInfo
    Platform/DisplaySwitcher.cs  DisplaySwitch.exe 切屏（v1.0.0 实机验证），第二次尝试 / 启动失败时用 SetDisplayConfig
    Platform/ProcessRunner.cs    无 shell 拼接的进程调用
    App.xaml(.cs)                生命周期、单实例、组装各服务
    TrayIcon.cs                  托盘图标与右键菜单
    MainWindow.xaml(.cs)         控制台 / 设置 / 运行日志 三个页面
    Actions.cs                   切屏前确认、失败时给出下一步
tests/
  PowerGuard.Core.Tests/         xUnit 测试
```

## 构建与测试

```powershell
dotnet build PowerGuardDisplay.slnx -c Release
dotnet test  tests/PowerGuard.Core.Tests -c Release
```

在 Linux/macOS 上也能编译（项目已设置 `EnableWindowsTargeting=true`）和运行测试，但 WPF 程序只能在 Windows 上运行。

## 发布

```powershell
./publish.ps1
```

等价命令：

```powershell
dotnet publish src/PowerGuard.App/PowerGuard.App.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
  -o publish/win-x64
```

`publish.ps1` 会生成两种包（加 `-Zip` 同时打成 zip，CI 和 Release 用的就是这两个 zip）：

| 目录 | 说明 |
| --- | --- |
| `publish/win-x64/PowerGuardDisplay.exe` | **自包含单文件**，约 70 MB，内含 .NET 运行时，直接运行 |
| `publish/win-x64-fd/PowerGuardDisplay.exe` | **依赖框架单文件**，只有几 MB，需要先安装 [.NET 10 Desktop Runtime x64](https://dotnet.microsoft.com/download/dotnet/10.0) |

WPF 不支持 IL 裁剪，所以想要小体积请用依赖框架版。也可以直接到 [Releases](https://github.com/Runa-Asahi/PowerGuard-Display/releases) 下载。

## 状态机

```
Normal ──市电→电池(确认 2 秒仍为电池)──► PowerLossDetected
   ▲                                       │ 先原子写入 recovery-state.json
   │                                       ▼
   │                               SwitchingToInternal ── 切到仅内屏 → 重新查询拓扑校验
   │                                       ▼
   │                               WaitingForExternal ◄──────────────┐
   │       （仍是电池：保持内屏，不因显示器波动切屏）                  │ 重试前条件不满足 / 用户中断
   │       （市电恢复：等待外接路径连续可用 ≥ 稳定时间，路径消失则重新计时）│
   │                                       ▼                          │
   └──校验模式 = 首选 才清除标记── RestoringPreferredMode ─────────────┘
                                           │ 1 + 最大重试次数 次均失败 / 市电恢复后超时
                                           ▼
                                    RecoveryFailed（保留标记，确保内屏活动，不再自动切屏）
Paused：用户暂停，忽略断电事件，不自动恢复；恢复保护后回到 Normal 或 WaitingForExternal
```

要点：
- **未知电源状态绝不当作断电**；只有“市电 → 电池”的确认转换才触发保护。
- **启动时只因为是电池供电，不会切屏**；只有存在恢复标记才继续流程。
- 恢复标记写入在切屏**之前**；清除恢复标记在**确认**当前模式等于首选模式**之后**。
- 切屏（`SwitchMethod=Auto`）先用 DisplaySwitch.exe（v1.0.0 起在 Lenovo Y7000 上实机验证）；启动失败，或第一次校验未通过后的第二次尝试，改用 `SetDisplayConfig(SDC_APPLY | SDC_TOPOLOGY_*)`。官方文档规定 `SDC_SAVE_TO_DATABASE` 只能与 `SDC_USE_SUPPLIED_DISPLAY_CONFIG` 组合，所以这里不使用。
- **断电时总是下发切内屏命令**，即使读到的已是“仅电脑屏幕”。断电瞬间外接显示器会短暂消失，Windows 先自行回落到内屏；显示器（仍保留 EDID）被重新识别后，Windows 会按数据库把这组显示器恢复成“仅第二屏幕”（2026-10-26 实机日志）。
- **断电期间内屏守护**：`TickAsync` 在非市电、有恢复标记、未暂停时，如果内屏不在活动状态或拓扑读不出来，就重新切回内屏。前 6 次至少间隔 5 秒，之后每 60 秒一次，进入退避时发出一次警报。断电期间绝不自动恢复外接。切换后等待 4 秒（`VerifyDelaySeconds`）再用 `QueryDisplayConfig` 校验，调用成功不算切换成功。
- 如果开始恢复时 Windows 已经是首选模式（例如关机前已切回，或你自己按了 Win+P），会直接确认并清除标记，不再切屏。
- 所有切屏操作串行执行（防重入），不在 UI 线程上等待。
- 恢复失败后不会无限循环：自动尝试次数 = 1 + 最大重试次数；超时后停止，等待用户手动操作或下一次断电/重启。
- 用户手动“切回内屏”/“恢复首选模式”/“暂停”会中断正在进行的自动恢复等待。
- 恢复标记文件损坏时按“有未完成流程”处理（宁可继续保护内屏），首选模式取当前设置。

## 电源检测

隐藏的顶层窗口通过 `RegisterPowerSettingNotification(GUID_ACDC_POWER_SOURCE)` 接收 `WM_POWERBROADCAST / PBT_POWERSETTINGCHANGE`（数据 0 = 市电，1 = 电池，2 = 短时电源/UPS → 忽略该通知，以轮询结果为准），同时处理 `PBT_APMPOWERSTATUSCHANGE`、睡眠恢复。启动时和低频轮询（正常 3 秒 / 恢复流程中 2 秒，上一轮未完成不叠加）用 `GetSystemPowerStatus` 兜底校验，重复的通知会被去重。退出时调用 `UnregisterPowerSettingNotification`。

## 显示检测

`QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS)` 读取活动路径，用 `outputTechnology` 区分内屏（INTERNAL / LVDS / 内嵌 DP / 内嵌 UDI）和外接屏，并推导当前模式（内外屏同源为“复制”，不同源为“扩展”）。`QueryDisplayConfig(QDC_ALL_PATHS)` 中 `targetAvailable = TRUE` 的外接目标被视为“Windows 检测到外接显示路径”。通过 `WM_DISPLAYCHANGE` 触发检查，再加上述低频轮询；`outputTechnology` 查询结果按（适配器, 目标）缓存，`WM_DISPLAYCHANGE` / 睡眠恢复时清空。

## 关机前恢复首选显示模式（v1.2 新增，默认开启）
目的：**下次开机时 Windows 直接进入首选拓扑**（比如“仅第二屏幕”）。Windows 会记住最后一次使用的显示拓扑，所以关机前切回首选模式，开机和登录界面就直接显示在外接显示器上。

- 隐藏窗口收到 `WM_QUERYENDSESSION`（关机 / 重启 / 注销）时，先用 `ShutdownBlockReasonCreate` 告诉 Windows 原因（“正在切换回首选显示模式”），再在后台线程执行切屏，并在剩余时间内多次查询拓扑确认结果。
- **总时长不超过约 4 秒**（锁等待、切屏、校验都算在内），超时就放弃。**始终允许关机**，不会拦着 Windows。
- 只在安全时才切（决策逻辑在 `ShutdownRestorePolicy`，有单元测试）：
  - 设置中已开启，且没有暂停保护；
  - **接的是市电**（电池 / 未知时可能还在断电，外接显示器可能没电，切过去开机会黑屏）；
  - 能读到显示拓扑，当前不是首选模式，且首选模式需要外接时 Windows 检测到了外接显示路径；
  - 不在“恢复失败”或正在执行切屏的过程中。
- 首选模式优先用恢复标记里的快照（断电时的首选），否则用当前设置。恢复标记不会被改动：如果关机被取消或切换没成功，正常的恢复流程会继续。
- 重启管理器（安装更新时）只要求关闭程序的 `ENDSESSION_CLOSEAPP` 不会触发切屏。每次关机流程只执行一次；关机被取消后会复位。
- 所有决策和结果都写入日志（类型“切屏”，关键字“关机前恢复”）。
- 设置页“关机 / 重启 / 注销前尽力切回首选显示模式”可以关闭（对应 `settings.json` 的 `RestoreOnShutdown`）。

## 自启动

使用 `schtasks.exe /Create /XML`，任务名 `PowerGuardDisplay`：
- 触发器：**仅当前用户**登录（`LogonTrigger` + `UserId`），延迟 10 秒
- 以当前用户、`InteractiveToken`、`LeastPrivilege` 运行，不需要管理员，不是 Windows 服务
- `DisallowStartIfOnBatteries=false`、`StopIfGoingOnBatteries=false`：电池供电时也能启动，断电时不会被任务计划程序停止
- `MultipleInstancesPolicy=IgnoreNew`；`RestartOnFailure` 最多 3 次、间隔 1 分钟。这个重启只在任务启动失败时生效，用户从托盘正常退出（退出码 0）不会被重新拉起
- 设置页关闭自启动会删除该任务；偏好关闭时，程序重启也不会自己再创建

# 局限与已知问题

## DisplaySwitch.exe 参数（仅回退时使用，需要实机确认）
Windows 10 的 `DisplaySwitch.exe` 接受 `/internal /external /extend /clone`。据 Microsoft Q&A 社区的反馈（[链接](https://learn.microsoft.com/en-us/answers/questions/1036148/displayswitch-exe-behavior-on-windows-11-22h2)），**Windows 11 22H2 起内置版本改为数字参数**：`1`=内屏、`2`=复制、`3`=扩展、`4`=外接，传 `/xxx` 只会弹出“投影”菜单。微软没有为这个工具发布官方参数文档。因此：
- 默认 `DisplaySwitchArgStyle = Auto`：系统 build ≥ 22000（Windows 11）用数字参数，否则用 `/xxx` 参数；
- 不论用哪种参数，每次切换后都会重新查询拓扑来校验；校验没通过就记为失败，不会当作成功；
- 如果你的系统表现不一样，在 `settings.json` 里手动指定 `"Slash"` 或 `"Numeric"`。

## “检测到显示路径” ≠ “显示器亮了”
程序能确认的只是 **Windows 是否认为外接目标可用、是否处于活动状态**。显示器是否真的通电、是否有画面，没有可靠的 API 能判断。界面上的“外接显示器（Windows 识别）”就是这个意思。恢复“成功”表示 Windows 已启用首选模式，实际画面请自己确认。

## 其他限制
- 只有电源适配器真正断电（市电转电池）才能被检测到。如果宿舍断电时笔记本**没有接电源**，或者电池已经耗尽导致直接关机，就不会触发保护。
- 如果在恢复标记写入前电脑就已经关机（几乎不可能，写入发生在检测到断电的约 2 秒后），下次启动没有标记，程序不会擅自恢复外接模式，需要你手动操作。
- “暂停保护”只在本次运行中有效，不写入配置，重启程序后恢复保护。也可以选“暂停 1 小时”，到时自动恢复。
- 关机前恢复是“尽力而为”：Windows 强制关机、电池耗尽、驱动在开机时重新枚举显示器都可能让它不生效。
- 首选模式快照在断电时保存。断电期间改了首选模式，只对下一次断电生效。
- 托盘图标为自定义盾牌：蓝 = 正常，橙 = 流程进行中，红 = 恢复失败 / 监听未运行，灰 = 已暂停。通知用托盘气泡，Windows 10/11 会显示成系统通知。
