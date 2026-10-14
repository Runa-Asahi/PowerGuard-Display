# PowerGuard Display（断电护屏）

![.NET 10](https://img.shields.io/badge/.NET-10-512BD4) ![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-0078D6)

> **English:** PowerGuard Display is a lightweight Windows tray app for laptops driving an external HDMI monitor in "second screen only" mode. When mains power fails it switches back to the built-in panel, and once the external display path is stable again it restores your preferred mode, resuming after a reboot if needed. No admin rights, no drivers, no network. Quick start: `dotnet build PowerGuardDisplay.slnx -c Release`, `dotnet test`, or `./publish.ps1` for a self-contained win-x64 build.

一个轻量级 Windows 托盘工具：**宿舍断电（市电转电池）时自动切回笔记本内屏；来电后在外接显示器重新被 Windows 识别并稳定一段时间后，自动恢复首选显示模式；断电导致关机的话，下次登录后继续未完成的恢复流程。**

- C# / .NET 10 / WPF + XAML，Win32 P/Invoke
- 不需要管理员权限，不安装驱动，不修改注册表中的显示配置，不联网、不上传任何数据
- 所有状态都来自真实的 Windows 查询（`GetSystemPowerStatus`、`QueryDisplayConfig` 等），没有任何模拟数据

> ⚠️ **当前版本的状态**：代码已在 Linux 上交叉编译通过并生成 win-x64 发布包，平台无关的状态机/持久化/自启动参数逻辑有 57 个自动化测试全部通过；但**尚未在真实 Windows 机器上运行过**。托盘、电源通知、显示拓扑查询、DisplaySwitch 切屏、任务计划程序都需要在你的电脑上实际验证，见文末“待验证事项”。

---

## 1. 依赖

| 用途 | 需要 |
| --- | --- |
| 运行发布包 | Windows 10 / 11 x64。自包含发布，**不需要**另外安装 .NET 运行时 |
| 开发构建 | [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)（本仓库用 10.0.401 构建；`global.json` 允许 10.0.x 更高版本） |
| 系统组件 | `%SystemRoot%\System32\DisplaySwitch.exe`、`%SystemRoot%\System32\schtasks.exe`（Windows 自带） |

第三方依赖：无（测试项目使用 xUnit）。

## 2. 目录结构

```
PowerGuardDisplay.sln
global.json
publish.ps1                      发布脚本
src/
  PowerGuard.Core/               平台无关的类库（net10.0），可在任何系统上测试
    Models.cs                    电源/显示模式/状态枚举，DisplaySwitch 参数映射
    Settings.cs                  AppSettings（settings.json）、RecoveryState（recovery-state.json）
    JsonFileStore.cs             原子写入（临时文件 + Flush + 替换）、损坏文件备份
    Logging.cs                   滚动日志（按天 + 按大小，JSON Lines）
    RecoveryCoordinator.cs       断电保护与自动恢复状态机
    StartupTask.cs               任务计划程序 XML 生成、schtasks 调用与解析
    Abstractions.cs              IClock / IDelay / IDisplayTopologyProvider / IDisplaySwitcher / IProcessRunner …
  PowerGuard.App/                WPF 应用（net10.0-windows）
    Native/NativeMethods.cs      P/Invoke 声明
    Platform/SystemMonitor.cs    隐藏窗口：RegisterPowerSettingNotification、WM_POWERBROADCAST、WM_DISPLAYCHANGE
    Platform/DisplayTopologyProvider.cs  QueryDisplayConfig / DisplayConfigGetDeviceInfo
    Platform/DisplaySwitcher.cs  调用 System32\DisplaySwitch.exe
    Platform/ProcessRunner.cs    无 shell 拼接的进程调用
    App.xaml(.cs)                生命周期、单实例、组装各服务
    TrayIcon.cs                  托盘图标与右键菜单
    MainWindow.xaml(.cs)         控制台 / 设置 / 运行日志 三个页面
    Actions.cs                   切屏前确认、失败时给出下一步
tests/
  PowerGuard.Core.Tests/         xUnit 测试
```

## 3. 构建与测试

```powershell
dotnet build PowerGuardDisplay.sln -c Release
dotnet test  tests/PowerGuard.Core.Tests -c Release
```

在 Linux/macOS 上也能编译（项目已设置 `EnableWindowsTargeting=true`）和运行测试，但 WPF 程序只能在 Windows 上运行。

## 4. 发布（自包含单文件）

```powershell
./publish.ps1
```

等价命令：

```powershell
dotnet publish src/PowerGuard.App/PowerGuard.App.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
  -o publish/win-x64
```

产物：`publish/win-x64/PowerGuardDisplay.exe`（约 72 MB，包含 .NET 运行时；`.pdb` 为调试符号，可不复制）。

## 5. 运行与首次配置

1. 把 `PowerGuardDisplay.exe` 复制到**固定位置**，例如 `%LOCALAPPDATA%\Programs\PowerGuardDisplay\`。自启动任务会指向 exe 的实际路径，以后移动 exe 需要重新开一次自启动（程序启动时也会检测并修正）。
2. 双击运行。首次运行会打开控制台窗口；之后启动默认只在托盘运行。
3. 打开“设置”：
   - **首选显示模式**：你平时用“仅第二屏幕”，就选 **仅外接显示器（external）**（默认值）。
   - 开启 **当前用户登录后自动启动**，这样断电关机后第二天登录也能继续恢复。
   - 其他值默认即可：稳定等待 15 秒、恢复超时 30 分钟、重试间隔 20 秒、最多重试 3 次。
4. 建议先手动测试：在控制台点“立即切回笔记本内屏”，再点“尝试恢复首选显示模式”，确认两种切换在你的电脑上都生效（见 §8）。

- 关闭主窗口只会隐藏到托盘，后台保护继续运行；**真正退出请用托盘菜单“退出程序”**。
- 再次运行 exe 不会启动第二个实例，而是打开已有实例的窗口。

数据位置：`%LOCALAPPDATA%\PowerGuardDisplay\`
- `settings.json`：配置
- `recovery-state.json`：未完成的恢复标记（文件存在 = 有未完成的流程）
- `logs\powerguard-yyyyMMdd.log`：日志（JSON Lines，单文件 2 MB 滚动，默认保留 14 天）

## 6. 工作原理

### 6.1 状态机

```
Normal ──市电→电池(确认 2 秒仍为电池)──► PowerLossDetected
   ▲                                       │ 先原子写入 recovery-state.json
   │                                       ▼
   │                               SwitchingToInternal ── DisplaySwitch 内屏 → 重新查询拓扑校验
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
- 调用 DisplaySwitch.exe 后等待 4 秒（`VerifyDelaySeconds`）再用 `QueryDisplayConfig` 校验，进程启动成功不算切换成功。
- 所有切屏操作串行执行（防重入），不在 UI 线程上等待。
- 恢复失败后不会无限循环：自动尝试次数 = 1 + 最大重试次数；超时后停止，等待用户手动操作或下一次断电/重启。
- 用户手动“切回内屏”/“恢复首选模式”/“暂停”会中断正在进行的自动恢复等待。
- 恢复标记文件损坏时按“有未完成流程”处理（宁可继续保护内屏），首选模式取当前设置。

### 6.2 电源检测

隐藏的顶层窗口通过 `RegisterPowerSettingNotification(GUID_ACDC_POWER_SOURCE)` 接收 `WM_POWERBROADCAST / PBT_POWERSETTINGCHANGE`（数据 0 = 市电，1 = 电池，2 = 短时电源/UPS → 按未知处理），同时处理 `PBT_APMPOWERSTATUSCHANGE`、睡眠恢复。启动时和每 3 秒用 `GetSystemPowerStatus` 兜底校验，重复的通知会被去重。退出时调用 `UnregisterPowerSettingNotification`。

### 6.3 显示检测

`QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS)` 读取活动路径，用 `outputTechnology` 区分内屏（INTERNAL / LVDS / 内嵌 DP / 内嵌 UDI）和外接屏，并推导当前模式（内外屏同源为“复制”，不同源为“扩展”）。`QueryDisplayConfig(QDC_ALL_PATHS)` 中 `targetAvailable = TRUE` 的外接目标被视为“Windows 检测到外接显示路径”。通过 `WM_DISPLAYCHANGE` 触发检查，再加每 3 秒一次的低频轮询。

### 6.4 自启动

使用 `schtasks.exe /Create /XML`，任务名 `PowerGuardDisplay`：
- 触发器：**仅当前用户**登录（`LogonTrigger` + `UserId`），延迟 10 秒
- 以当前用户、`InteractiveToken`、`LeastPrivilege` 运行，不需要管理员，不是 Windows 服务
- `DisallowStartIfOnBatteries=false`、`StopIfGoingOnBatteries=false`：电池供电时也能启动，断电时不会被任务计划程序停止
- `MultipleInstancesPolicy=IgnoreNew`；`RestartOnFailure` 最多 3 次、间隔 1 分钟。这个重启只在任务启动失败时生效，用户从托盘正常退出（退出码 0）不会被重新拉起
- 设置页关闭自启动会删除该任务；偏好关闭时，程序重启也不会自己再创建

## 7. 故障排查

| 现象 | 处理 |
| --- | --- |
| 切屏后内外屏都没有画面 | 按 **Win+P** 选“仅电脑屏幕”；或按 **Win+Ctrl+Shift+B** 重置显卡驱动；或从托盘菜单点“切回笔记本内屏” |
| 点切换后只弹出 Windows 的“投影”菜单、没有真正切换 | Windows 11 的 DisplaySwitch.exe 参数格式变了（见 §8.1）。到 `settings.json` 把 `"DisplaySwitchArgStyle"` 从 `"Auto"` 改为 `"Numeric"` 或 `"Slash"`，然后重启程序 |
| 控制台显示“恢复失败” | 看“最近错误”和运行日志。确认显示器已通电、HDMI 已插好，在“设置 > 系统 > 屏幕”点“检测”，然后点“尝试恢复首选显示模式” |
| 外接显示器断电了，却仍显示“已识别” | 有些显示器断电后仍保留 EDID/热插拔信号，Windows 会继续报告这条路径。程序只在**市电恢复后**才开始稳定计时，所以一般不受影响；如果仍然太早恢复，可以把“稳定等待时间”调大 |
| 自启动开启失败 | 界面会显示 schtasks 的错误信息。按 Win+R 输入 `taskschd.msc` 查看任务计划程序库里的 `PowerGuardDisplay`，或运行 `schtasks /Query /TN PowerGuardDisplay /V /FO LIST`。公司/学校电脑的组策略可能禁止普通用户创建任务 |
| 托盘图标不见了 | 可能被收进了任务栏溢出区；再次运行 exe 会打开已有实例的窗口 |
| 配置丢失或程序行为异常 | `settings.json` 损坏时，程序会改名备份为 `settings.json.corrupt-时间戳` 并使用默认值，日志中有记录 |
| 想重置 | 从托盘退出程序，删除 `%LOCALAPPDATA%\PowerGuardDisplay\`，再在设置页重新开启一次自启动 |

## 8. 局限与待验证事项（务必阅读）

### 8.1 DisplaySwitch.exe 参数（需要实机确认）
Windows 10 的 `DisplaySwitch.exe` 接受 `/internal /external /extend /clone`。据 Microsoft Q&A 社区的反馈（[链接](https://learn.microsoft.com/en-us/answers/questions/1036148/displayswitch-exe-behavior-on-windows-11-22h2)），**Windows 11 22H2 起内置版本改为数字参数**：`1`=内屏、`2`=复制、`3`=扩展、`4`=外接，传 `/xxx` 只会弹出“投影”菜单。微软没有为这个工具发布官方参数文档。因此：
- 默认 `DisplaySwitchArgStyle = Auto`：系统 build ≥ 22000（Windows 11）用数字参数，否则用 `/xxx` 参数；
- 不论用哪种参数，每次切换后都会重新查询拓扑来校验；校验没通过就记为失败，不会当作成功；
- 如果你的系统表现不一样，在 `settings.json` 里手动指定 `"Slash"` 或 `"Numeric"`。

### 8.2 “检测到显示路径” ≠ “显示器亮了”
程序能确认的只是 **Windows 是否认为外接目标可用、是否处于活动状态**。显示器是否真的通电、是否有画面，没有可靠的 API 能判断。界面上的“外接显示器（Windows 识别）”就是这个意思。恢复“成功”表示 Windows 已启用首选模式，实际画面请自己确认。

### 8.3 其他限制
- 只有电源适配器真正断电（市电转电池）才能被检测到。如果宿舍断电时笔记本**没有接电源**，或者电池已经耗尽导致直接关机，就不会触发保护。
- 如果在恢复标记写入前电脑就已经关机（几乎不可能，写入发生在检测到断电的约 2 秒后），下次启动没有标记，程序不会擅自恢复外接模式，需要你手动操作。
- “暂停保护”只在本次运行中有效，不写入配置，重启程序后恢复保护。
- 首选模式快照在断电时保存。断电期间改了首选模式，只对下一次断电生效。
- 托盘图标用的是系统自带图标（盾牌 / 警告 / 信息），暂时没有自定义图标。

### 8.4 已验证 / 未验证
| 项目 | 状态 |
| --- | --- |
| 编译（Linux 交叉编译 Release，0 警告 0 错误） | ✅ 已验证 |
| win-x64 自包含单文件发布 | ✅ 已生成（未在 Windows 上启动过） |
| 状态机、持久化、日志滚动、自启动 XML/参数、DisplaySwitch 参数映射（57 个 xUnit 测试，用假实现替代系统调用） | ✅ 已通过 |
| P/Invoke 结构体大小（72 / 64 / 420 / 12 字节）与偏移 | ✅ 已测试（与 Windows SDK 定义对照） |
| 程序在 Windows 上启动、托盘、窗口、DPI 缩放 | ⏳ 需要在 Windows 上验证 |
| `RegisterPowerSettingNotification` 实际收到拔插电源事件 | ⏳ 需要实机：拔掉电源适配器测试 |
| `QueryDisplayConfig` 在 Y7000 IRX10 上的内屏识别（outputTechnology）和模式推导 | ⏳ 需要实机 |
| DisplaySwitch.exe 四种模式在你的 Windows 版本上实际生效 | ⏳ 需要实机（§8.1） |
| schtasks 普通用户创建登录任务、登录后自动启动 | ⏳ 需要实机 |
| 真实断电、HDMI 重连、显示器亮屏时间 | ⏳ 只能人工测试 |

### 8.5 建议的人工测试步骤
1. 接市电、“仅第二屏幕”模式下启动 → 控制台应显示：市电 / 仅第二屏幕 / 外接已识别·显示中 / 正常运行。
2. **拔掉笔记本电源适配器**（模拟断电，显示器保持通电）→ 约 2–6 秒后应切到内屏，状态变为“等待外接显示器”，`recovery-state.json` 出现。
3. 插回电源适配器 → 约 15 秒稳定期后恢复“仅第二屏幕”，标记文件消失。
4. 重复第 2 步，然后关掉显示器电源、插回电源适配器，观察是否等待；再打开显示器，确认恢复。
5. 在第 2 步之后从托盘退出程序、重启电脑并接市电登录 → 应提示发现未完成的恢复标记并继续恢复。
6. 开启/关闭自启动，在 `taskschd.msc` 中确认任务被创建/删除；从托盘退出后，确认程序不会被自动拉起。
