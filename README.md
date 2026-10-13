# PowerGuard Display（断电护屏）

一个轻量级 Windows 托盘工具：**宿舍断电（市电转电池）时自动切回笔记本内屏；来电后在外接显示器重新被 Windows 识别并稳定一段时间后，自动恢复首选显示模式；断电导致关机的话，下次登录后继续未完成的恢复流程。**

- C# / .NET 10 / WPF + XAML，Win32 P/Invoke
- 不需要管理员权限，不安装驱动，不修改注册表中的显示配置，不联网、不上传任何数据
- 所有状态都来自真实的 Windows 查询（`GetSystemPowerStatus`、`QueryDisplayConfig` 等），没有任何模拟数据

> ⚠️ **当前版本的状态**：第一版的**基础断电保护（市电转电池 → 切回内屏 → 来电恢复）已由用户在真实硬件上确认可用**。v2 新增的功能（推荐/兼容触发方式、“外接活动显示路径消失”检测、关机前尽力尝试、新的启动规则）只在 Linux 上通过了 95 个自动化测试（使用假实现），**尚未在真实 Windows / 硬件上验证**，见 §9 与 §8.4。

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
PowerGuardDisplay.slnx
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
dotnet build PowerGuardDisplay.slnx -c Release
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
| 外接显示器断电了，却仍显示“路径可用” | 有些显示器断电后仍保留 EDID/热插拔信号，Windows 会继续报告这条路径。程序只在**市电恢复后**才开始稳定计时，所以一般不受影响；如果仍然太早恢复，可以把“稳定等待时间”调大 |
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
程序能确认的只是 **Windows 是否认为外接目标可用、是否处于活动状态**。显示器是否真的通电、是否有画面，没有可靠的 API 能判断。界面上的“外接活动显示路径”和“外接可用目标”（Windows 检测）就是这个意思。恢复“成功”表示 Windows 已启用首选模式，实际画面请自己确认。

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
| 状态机、持久化、日志滚动、自启动 XML/参数、DisplaySwitch 参数映射（95 个 xUnit 测试，用假实现替代系统调用） | ✅ 已通过 |
| P/Invoke 结构体大小（72 / 64 / 420 / 12 字节）与偏移 | ✅ 已测试（与 Windows SDK 定义对照） |
| 基础断电保护：市电转电池切回内屏（第一版行为，即现在的兼容模式） | ✅ 用户已在真实硬件上确认 |
| v2：推荐模式下“只拔电源不切屏”、外接活动路径消失检测在 Y7000 + HDMI 显示器上的实际表现 | ⏳ 未在实机验证 |
| v2：关机前尽力尝试是否在关机时序内生效、下次开机 Windows 是否沿用 | ⏳ 未在实机验证 |
| v2：启动规则（标记存在且已是首选模式 → 校验后清除） | ⏳ 仅自动化测试 |
| 程序在 Windows 上启动、托盘、窗口、DPI 缩放 | ⏳ 新版界面需在 Windows 上复查 |
| `RegisterPowerSettingNotification` 实际收到拔插电源事件 | ✅ 随基础断电保护一并确认 |
| `QueryDisplayConfig` 在 Y7000 IRX10 上的内屏识别（outputTechnology）和模式推导 | ⏳ 需要实机 |
| DisplaySwitch.exe 四种模式在你的 Windows 版本上实际生效 | ⏳ 需要实机（§8.1） |
| schtasks 普通用户创建登录任务、登录后自动启动 | ⏳ 需要实机 |
| 真实断电、HDMI 重连、显示器亮屏时间 | ⏳ 只能人工测试 |

### 8.5 建议的人工测试步骤
1. 接市电、“仅第二屏幕”模式下启动 → 控制台应显示：市电 / 仅第二屏幕 / 外接活动路径 1 条 / 正常运行。
2. **拔掉笔记本电源适配器**（模拟断电，显示器保持通电）→ 约 2–6 秒后应切到内屏，状态变为“等待外接显示器”，`recovery-state.json` 出现。
3. 插回电源适配器 → 约 15 秒稳定期后恢复“仅第二屏幕”，标记文件消失。
4. 重复第 2 步，然后关掉显示器电源、插回电源适配器，观察是否等待；再打开显示器，确认恢复。
5. 在第 2 步之后从托盘退出程序、重启电脑并接市电登录 → 应提示发现未完成的恢复标记并继续恢复。
6. 开启/关闭自启动，在 `taskschd.msc` 中确认任务被创建/删除；从托盘退出后，确认程序不会被自动拉起。

## 9. v2：多信号触发与关机前尝试

### 9.1 两个概念要分开
- **外接活动显示路径**（`ExternalActiveCount`）：Windows 当前正在向外接目标输出画面的路径（`QDC_ONLY_ACTIVE_PATHS`）。
- **外接可用目标**（`ExternalAvailableCount`）：Windows 检测到外接显示路径**可以连接**（`QDC_ALL_PATHS` 中 `targetAvailable = TRUE`）。
- 两者是分开的字段，**可用目标 > 0 不等于外接正在显示**。两者也都不能证明显示器真的亮着；界面上只写“Windows 检测到外接显示路径”，不写“显示器已亮屏”。

### 9.2 触发规则（设置页“触发方式”，`settings.json` 的 `TriggerMode`）

| 信号 | 推荐 `Recommended`（默认） | 兼容 `Compatibility` |
| --- | --- | --- |
| 确认的市电转电池（2 秒确认，确认窗口内恢复市电视为抖动） | 再查询一次拓扑：**外接活动路径还在 → 只记录，不切屏**；外接活动路径为 0（查询成功）→ 触发 `CombinedEvidence`；查询失败 → 记录并重试 3 次，仍失败就**不当作断电证据** | 直接触发 `AcToBattery`，不看外接路径（第一版行为） |
| 外接活动显示路径消失 | ✅ 检测 | ✅ 检测 |

外接活动显示路径消失的判定：
1. 上一次**成功**的快照是“仅第二屏幕”，并且有外接活动路径；当前成功快照中外接活动路径为 0。
2. 进入确认窗口，时长为 `ExternalLostConfirmSeconds`（默认 8 秒）。窗口内外接活动路径重新出现，视为抖动，取消。
3. 窗口结束后**重新查询**一次，仍然为 0 才触发。原因为 `ExternalDisplayLost`；如果此时正在用电池，原因为 `CombinedEvidence`。

以下情况**不会**触发：
- 查询失败，或电源状态未知；
- 本程序切屏、用户在控制台或托盘手动切屏后的 15 秒内；
- 外接活动路径消失、但 Windows 仍报告外接目标可用（多数是用户按 Win+P 主动切换）；
- 扩展或复制模式（内屏仍在显示，不需要保护）；
- 自动保护已关闭或已暂停。

所有触发都走 `RecoveryCoordinator.TriggerProtectionCoreAsync(TriggerReason)` 这一个入口，任何时候最多只有**一个流程、一个恢复标记**。流程进行中出现新的证据时，只会把原因合并为 `CombinedEvidence`，不会重新开始。

触发后：
- 如果内屏已经在显示（Windows 已自动回落到仅内屏，或者处于扩展/复制模式），**不再发送切屏命令**，也不会强制切成仅内屏；
- 否则调用 DisplaySwitch 切到内屏，并重新查询拓扑校验。

### 9.3 典型场景

| 场景 | 推荐模式 | 兼容模式 |
| --- | --- | --- |
| **只拔笔记本电源（显示器仍在显示）** | 只记录“外接活动路径仍存在”，**不切屏**。如果之后外接活动路径真的消失，触发 `CombinedEvidence`，插回电源后再恢复 | 切回内屏（`AcToBattery`）；插回电源、外接目标稳定可用 15 秒后恢复 |
| **只关显示器 / 显示器单独断电（市电一直正常）** | Windows 去掉外接活动路径 → 确认 8 秒、重新查询后切回内屏（`ExternalDisplayLost`）。显示器重新开启、外接目标稳定可用后恢复，**不需要市电事件** | 同左 |
| 宿舍整体断电（显示器和电源同时断） | 市电转电池时，如果外接活动路径已经消失，触发 `CombinedEvidence`；如果当时 Windows 还没来得及去掉路径，就只记录，随后由路径消失检测触发 `CombinedEvidence`。都要等市电恢复后才恢复 | 立即触发 `AcToBattery` |

**API / HDMI 限制**：部分显示器关机或断电后仍保留 EDID/热插拔信号，Windows 可能一直保持外接活动路径。这种情况下“只关显示器”无法被检测到；在推荐模式下，断电时也会因为“外接活动路径仍存在”而不切屏。如果你的显示器是这样，请改用兼容模式。

### 9.4 恢复与超时
- 目标模式取恢复标记中的首选模式快照。
- **稳定计时**：从 Windows 检测到外接目标可用时开始。外接目标消失或可用目标数变化时，计时重置。
- **电源条件**：`AcToBattery` 和 `CombinedEvidence` 要求当前接着市电；`ExternalDisplayLost` 只要求当前不是电池供电，所以一直接着市电时不会阻塞恢复。
- 只有校验确认当前模式等于目标模式，才会清除恢复标记。重试次数上限（1 + 最大重试次数）不变。
- **超时（这次改动）**：恢复超时对**所有**触发原因都生效，从满足电源条件起计时（含市电证据的触发从市电恢复起，`ExternalDisplayLost` 从触发起）。超时后进入 RecoveryFailed：保留恢复标记、确保内屏可用、**不再自动切屏**，之后只能手动“尝试恢复首选显示模式”，或等下次启动或下次触发。
  - 注意：如果显示器可能整晚关着，又希望第二天开显示器后自动恢复，请把“恢复超时时间”调大（最大 1440 分钟）。

### 9.5 关机前尽力尝试（`TryPreferredModeOnShutdown`，**默认关闭**，实验性）
- **触发时机**：只在**正常关机或注销**时，即 `WM_QUERYENDSESSION`（WPF `Application.SessionEnding`），并以 `WM_ENDSESSION` 兜底；每次会话最多一次。**突然断电导致的关机不会触发**，因为程序没有机会运行。
- **前提条件**：
  - 设置已开启，保护未暂停，自动恢复已启用；
  - 存在未完成的恢复标记，且电源条件满足；
  - Windows 检测到外接目标可用，当前还不是首选模式；
  - 首选模式能映射为 DisplaySwitch 参数；
  - 没有其他切屏流程正在执行。
- **执行方式**：调用一次 DisplaySwitch，总时限约 3 秒。**不会阻止或取消关机**，**不会清除恢复标记**。日志写的是“已尝试（非已恢复）”。
- **无法保证** BIOS、开机画面或登录界面会显示在 HDMI 显示器上，也无法保证 Windows 下次开机时沿用该模式。

### 9.6 下次启动的规则
1. 没有恢复标记：什么都不做。
2. 有标记、电源条件满足，并且 Windows 已经处于首选模式（首选模式需要外接屏时，还要求有外接活动路径）：校验通过后清除标记。
3. 其余情况进入“等待外接显示器”。电源条件不满足或外接目标不可用、且内屏没在显示时，先切回内屏。
4. 损坏的标记按 `AcToBattery` 处理（要求接着市电才恢复，最保守）。

### 9.7 配置兼容
- v1 的 `settings.json` / `recovery-state.json` 可以直接读取。缺少的字段使用默认值：`TriggerMode=Recommended`、`ExternalLostConfirmSeconds=8`、`TryPreferredModeOnShutdown=false`、`TriggerReason=AcToBattery`。
- 开发过程中写入的 `"PowerLoss"` 也会被读作 `AcToBattery`。新写入的文件 `Version` 为 2。
- 注意：从 v1 升级后默认是推荐模式，**只拔电源而显示器仍在显示时不再切屏**。如果想保持第一版的行为，请在设置里选“兼容”。

### 9.8 界面与日志
- **控制台显示**：
  - 电源来源、实际显示模式、内屏是否在显示；
  - 外接活动路径数、外接可用目标数；
  - 恢复流程状态、触发方式、最近触发原因；
  - 外接路径消失是否在确认中、是否有未完成的恢复标记、关机前尝试是否开启。
- **设置页**：说明推荐和兼容两种模式的取舍，以及 HDMI 的 EDID 限制。
- **日志**：新增“触发”和“关机”两类，分别记录：
  - 市电转电池后的决策（只记录、触发、查询失败重试）；
  - 外接路径消失的确认开始、抖动取消、确认查询结果；
  - 原因合并；
  - 关机前尝试是跳过还是执行，以及结果。

### 9.9 v2 需要在实机上验证
1. 推荐模式下，显示器保持开着，只拔笔记本电源：应**不切屏**，日志出现“外接活动显示路径仍存在”。
2. 接着市电，只关掉显示器电源：看日志是否出现“外接活动显示路径消失 … 开始 8 秒确认”，之后是否切回内屏。如果一直没有出现，说明你的显示器关机后 Windows 仍保持活动路径（属于 §9.3 的限制）。
3. 重新打开显示器：确认约 15 秒后恢复。
4. 按 Win+P 切到“仅电脑屏幕”：确认**不会**触发保护。
5. 拔掉总电源（显示器和笔记本同时断电），对比推荐模式和兼容模式的表现。
6. 开启关机前尝试：在“等待外接显示器”状态下正常关机，下次开机时看日志里的“关机前已尝试”结果，以及是否出现“启动时确认已处于首选模式”。

### 9.10 DisplaySwitch.exe 参数（沿用 §8.1 的提醒）
Windows 10 使用 `/internal /clone /extend /external`；Windows 11 22H2 起据社区反馈改为 `1/2/3/4`。默认按系统版本自动选择，可以在 `settings.json` 的 `DisplaySwitchArgStyle` 中覆盖。切换后一律重新查询拓扑校验，不把命令执行成功当作切换成功。
