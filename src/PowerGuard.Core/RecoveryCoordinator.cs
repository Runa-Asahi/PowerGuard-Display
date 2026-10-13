namespace PowerGuard.Core;

public sealed record CoordinatorSnapshot(
    RecoveryStatus Status,
    PowerSource Power,
    DisplayTopology Topology,
    RecoveryState? Marker,
    bool Paused,
    DateTimeOffset? LastEventAt,
    string? LastEventText,
    LogResult LastEventResult,
    DateTimeOffset? ExternalStableSince,
    DateTimeOffset? WaitingSince,
    DateTimeOffset? ExternalLostSince,
    TriggerReason? LastTriggerReason,
    DateTimeOffset? LastTriggerAt,
    string? LastShutdownAttempt);

public sealed record ShutdownAttemptResult(bool Attempted, bool? Verified, string Message);

/// <summary>
/// 断电保护与自动恢复状态机。所有改变状态的流程都在 _gate 内串行执行（防重入）。
/// 触发统一走 TriggerProtectionCoreAsync(TriggerReason)：
///   - 市电 → 电池（确认窗口后仍为电池）：兼容模式直接触发 AcToBattery；
///     推荐模式再查询拓扑，外接活动路径仍在只记录，已消失才触发 CombinedEvidence，查询失败重试、不当作证据；
///   - 外接活动显示路径消失（两种模式）：上一成功快照为“仅外接”且有活动外接路径，
///     当前活动外接路径为 0，确认窗口后重新查询仍为 0 → ExternalDisplayLost（若正用电池则 CombinedEvidence）。
/// “外接活动路径”(ExternalActiveCount) 与 “外接目标可用”(ExternalAvailableCount) 严格区分。
/// Tick 由低频定时器和 WM_DISPLAYCHANGE 驱动，用前后两次拓扑快照做检测。
/// </summary>
public sealed class RecoveryCoordinator
{
    private readonly Func<AppSettings> _settings;
    private readonly IRecoveryStateStore _store;
    private readonly IDisplayTopologyProvider _topology;
    private readonly IDisplaySwitcher _switcher;
    private readonly IClock _clock;
    private readonly IDelay _delay;
    private readonly IAppLogger _log;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _powerLock = new();

    private RecoveryStatus _flowStatus = RecoveryStatus.Normal;
    private bool _paused;
    private PowerSource _power = PowerSource.Unknown;
    private long _powerSeq;
    private DisplayTopology _lastTopology = DisplayTopology.Failed("尚未查询");
    private RecoveryState? _marker;
    private DateTimeOffset? _externalStableSince;
    private int _stableAvailableCount;
    /// <summary>本程序或用户手动切屏后的一段时间内，不把活动路径变化当作显示器断电。</summary>
    private DateTimeOffset _suppressLostUntil = DateTimeOffset.MinValue;
    public static readonly TimeSpan OwnSwitchSuppression = TimeSpan.FromSeconds(15);
    /// <summary>满足恢复的电源条件后开始计时（含市电证据：市电恢复起；仅显示器：触发起）。</summary>
    private DateTimeOffset? _waitingSince;
    /// <summary>外接显示路径消失的候选时间（确认窗口）。</summary>
    private DateTimeOffset? _externalLostSince;
    private TriggerReason? _lastTriggerReason;
    private DateTimeOffset? _lastTriggerAt;
    private string? _lastShutdownAttempt;
    private int _shutdownAttempted;
    private DateTimeOffset? _lastEventAt;
    private string? _lastEventText;
    private LogResult _lastEventResult;
    private CancellationTokenSource? _flowCts;
    private bool _autoRestoreDisabledLogged;

    public event EventHandler? StateChanged;

    public RecoveryCoordinator(Func<AppSettings> settings, IRecoveryStateStore store, IDisplayTopologyProvider topology,
        IDisplaySwitcher switcher, IClock clock, IDelay delay, IAppLogger log)
    {
        _settings = settings; _store = store; _topology = topology; _switcher = switcher;
        _clock = clock; _delay = delay; _log = log;
    }

    public RecoveryStatus Status => _paused ? RecoveryStatus.Paused : _flowStatus;
    public bool Paused => _paused;
    public PowerSource Power { get { lock (_powerLock) return _power; } }
    public RecoveryState? Marker => _marker;

    public CoordinatorSnapshot Snapshot() => new(Status, Power, _lastTopology, _marker, _paused,
        _lastEventAt, _lastEventText, _lastEventResult, _externalStableSince, _waitingSince,
        _externalLostSince, _lastTriggerReason, _lastTriggerAt, _lastShutdownAttempt);

    private void SetStatus(RecoveryStatus s)
    {
        if (_flowStatus == s) return;
        _log.Log(LogCategory.Recovery, $"状态：{DisplayModeText.Of(_flowStatus)} → {DisplayModeText.Of(s)}");
        _flowStatus = s;
        Changed();
    }

    private void Event(LogCategory c, string text, LogResult r)
    {
        _log.Log(c, text, r);
        _lastEventAt = _clock.Now; _lastEventText = text; _lastEventResult = r;
        Changed();
    }

    private void Changed() { try { StateChanged?.Invoke(this, EventArgs.Empty); } catch { } }

    private DisplayTopology QueryTopology()
    {
        DisplayTopology t;
        try { t = _topology.Query(); }
        catch (Exception ex) { t = DisplayTopology.Failed(ex.Message); }
        if (t.Signature != _lastTopology.Signature)
        {
            _log.Log(LogCategory.Display, $"显示配置变化：{t}", t.QuerySucceeded ? LogResult.Info : LogResult.Warning);
            _lastTopology = t;
            Changed();
        }
        _lastTopology = t;
        return t;
    }

    private void SaveMarker()
    {
        if (_marker is null) return;
        try { _store.Save(_marker); }
        catch (Exception ex) { _log.Log(LogCategory.Error, $"保存恢复标记失败：{ex.Message}", LogResult.Failure); }
    }

    private void ClearMarker()
    {
        try { _store.Clear(); }
        catch (Exception ex) { _log.Log(LogCategory.Error, $"清除恢复标记失败：{ex.Message}", LogResult.Failure); }
        _marker = null; _externalStableSince = null; _waitingSince = null;
    }

    /// <summary>恢复所需的电源条件：含市电转电池证据的触发必须回到市电；仅显示器触发只要求不是电池。</summary>
    private bool PowerAllowsRestore(TriggerReason reason) =>
        reason == TriggerReason.ExternalDisplayLost ? Power != PowerSource.Battery : Power == PowerSource.Ac;

    private static TriggerReason Merge(TriggerReason a, TriggerReason b) => a == b ? a : TriggerReason.CombinedEvidence;

    private static bool IsInPreferred(DisplayTopology t, DisplayMode preferred) =>
        t.QuerySucceeded && t.Mode == preferred && (!DisplaySwitchArgs.RequiresExternal(preferred) || t.ExternalActiveCount > 0);

    private static bool ExternalReady(DisplayTopology t, DisplayMode preferred) =>
        !DisplaySwitchArgs.RequiresExternal(preferred) || (t.QuerySucceeded && t.ExternalAvailableCount > 0);

    // ───────────────────────── 初始化 ─────────────────────────

    public async Task InitializeAsync(PowerSource initialPower)
    {
        await _gate.WaitAsync();
        try
        {
            lock (_powerLock) _power = initialPower;
            var topo = QueryTopology();
            _log.Log(LogCategory.App, $"初始化：电源={DisplayModeText.Of(initialPower)}；触发方式={DisplayModeText.Of(_settings().TriggerMode)}；{topo}");

            var r = _store.Load();
            if (r.Status == LoadStatus.Corrupt)
            {
                // 文件存在说明之前有未完成流程；内容损坏时按 AcToBattery 重建（要求市电才恢复，最保守），宁可继续保护也不丢流程。
                _log.Log(LogCategory.Recovery, $"恢复标记损坏：{r.Message}。按未完成的断电流程处理，首选模式取当前设置。", LogResult.Warning);
                _marker = new RecoveryState { PreferredModeSnapshot = _settings().PreferredMode, StartedAt = _clock.Now, LastError = "恢复标记文件损坏后重建" };
                SaveMarker();
            }
            else _marker = r.Value is { Pending: true } ? r.Value : null;

            if (_marker is null)
            {
                // 没有标记：即使当前是电池或外接屏不在，也不切屏，不推断曾经断电。
                _flowStatus = RecoveryStatus.Normal;
                Event(LogCategory.App, initialPower == PowerSource.Battery ? "启动时为电池供电，但无恢复标记，不执行切屏" : "启动完成，无未完成的恢复流程", LogResult.Info);
                return;
            }

            var m = _marker;
            _lastTriggerReason = m.TriggerReason; _lastTriggerAt = m.StartedAt;
            Event(LogCategory.Recovery, $"启动时发现未完成的恢复标记（原因 {DisplayModeText.Of(m.TriggerReason)}，开始于 {m.StartedAt:yyyy-MM-dd HH:mm:ss}，首选 {DisplayModeText.Of(m.PreferredModeSnapshot)}）"
                + (m.LastShutdownAttemptAt is { } sa ? $"；关机前尝试 {sa:MM-dd HH:mm:ss}：{m.LastShutdownAttemptResult}" : ""), LogResult.Warning);

            // 规则 1：电源条件满足且 Windows 已处于首选模式（例如关机前尝试生效或 Windows 自行沿用）→ 校验通过后清除
            if (PowerAllowsRestore(m.TriggerReason) && IsInPreferred(topo, m.PreferredModeSnapshot))
            {
                ClearMarker();
                _flowStatus = RecoveryStatus.Normal;
                Event(LogCategory.Recovery, $"启动时确认已处于首选模式 {DisplayModeText.Of(topo.Mode)}，恢复标记已清除", LogResult.Success);
                return;
            }

            // 规则 2：继续等待；电源条件或外接路径不满足时确保内屏可用
            _flowStatus = RecoveryStatus.WaitingForExternal;
            _externalStableSince = null;
            _waitingSince = PowerAllowsRestore(m.TriggerReason) ? _clock.Now : null;
            var needInternal = !PowerAllowsRestore(m.TriggerReason) || !ExternalReady(topo, m.PreferredModeSnapshot);
            if (needInternal && (!topo.QuerySucceeded || !topo.InternalActive))
                await SwitchToInternalCoreAsync(CancellationToken.None);
            Changed();
        }
        finally { _gate.Release(); }
    }

    // ───────────────────────── 电源事件 ─────────────────────────

    /// <summary>电源来源通知（WM_POWERBROADCAST / GUID_ACDC_POWER_SOURCE）。重复通知被去重。</summary>
    public async Task OnPowerSourceChangedAsync(PowerSource p)
    {
        PowerSource prev; long seq;
        lock (_powerLock)
        {
            if (p == _power) return; // 去重：注册时 Windows 会立即发送一次当前值
            prev = _power; _power = p; seq = ++_powerSeq;
        }
        Event(LogCategory.Power, $"电源来源变化：{DisplayModeText.Of(prev)} → {DisplayModeText.Of(p)}", LogResult.Info);

        if (p == PowerSource.Battery && prev == PowerSource.Ac)
        {
            var s = _settings();
            if (s.PowerLossConfirmSeconds > 0)
            {
                try { await _delay.Delay(TimeSpan.FromSeconds(s.PowerLossConfirmSeconds), CancellationToken.None); } catch { }
                lock (_powerLock) if (_powerSeq != seq || _power != PowerSource.Battery)
                {
                    _log.Log(LogCategory.Power, "电源在确认窗口内已恢复，视为瞬时抖动，不触发断电保护");
                    return;
                }
            }
            await _gate.WaitAsync();
            try
            {
                if (Power != PowerSource.Battery) return;
                await OnConfirmedAcToBatteryCoreAsync();
            }
            finally { _gate.Release(); }
        }
        else if (p == PowerSource.Ac)
        {
            await _gate.WaitAsync();
            try
            {
                if (_flowStatus == RecoveryStatus.WaitingForExternal && _marker != null && Power == PowerSource.Ac)
                {
                    _waitingSince = _clock.Now; _externalStableSince = null;
                    Event(LogCategory.Recovery, "市电已恢复，等待外接显示路径稳定后再恢复（不会立即切回）", LogResult.Info);
                }
            }
            finally { _gate.Release(); }
        }
        // Unknown：只记录，绝不当作断电。
    }

    // ───────────────────────── 统一触发入口 ─────────────────────────

    /// <summary>确认市电转电池后的处理（需持有 _gate）。</summary>
    private async Task OnConfirmedAcToBatteryCoreAsync()
    {
        var s = _settings();
        if (s.TriggerMode == TriggerMode.Compatibility)
        {
            _waitingSince = null; _externalStableSince = null;
            await TriggerProtectionCoreAsync(TriggerReason.AcToBattery);
            return;
        }
        // 推荐模式：市电转电池本身不足以切屏，要看外接活动路径
        DisplayTopology t = DisplayTopology.Failed("未查询");
        for (var i = 1; i <= 3; i++)
        {
            t = QueryTopology();
            if (t.QuerySucceeded) break;
            _log.Log(LogCategory.Trigger, $"市电转电池后查询显示拓扑失败（第 {i}/3 次）：{t.Error}，稍后重试", LogResult.Warning);
            if (i < 3) await _delay.Delay(TimeSpan.FromSeconds(2), CancellationToken.None);
        }
        if (!t.QuerySucceeded)
        {
            Event(LogCategory.Trigger, "市电转电池，但无法查询显示拓扑：不把未知当作显示器断电，不切屏；后续定时检查会继续检测外接活动路径", LogResult.Warning);
            return;
        }
        if (t.ExternalActiveCount > 0)
        {
            Event(LogCategory.Trigger, $"市电转电池，但外接活动显示路径仍存在（{t.ExternalActiveCount} 条）：只记录，不切屏（推荐模式）", LogResult.Info);
            return;
        }
        _waitingSince = null; _externalStableSince = null;
        await TriggerProtectionCoreAsync(TriggerReason.CombinedEvidence);
    }

    /// <summary>所有自动保护触发的唯一入口（需持有 _gate）。</summary>
    private async Task TriggerProtectionCoreAsync(TriggerReason reason)
    {
        var s = _settings();
        var label = DisplayModeText.Of(reason);
        if (_paused) { _log.Log(LogCategory.Trigger, $"保护已暂停，忽略触发：{label}", LogResult.Warning); return; }
        if (!s.AutoSwitchOnPowerLoss) { _log.Log(LogCategory.Trigger, $"设置中已禁用自动保护，忽略触发：{label}", LogResult.Warning); return; }

        if (_flowStatus is not (RecoveryStatus.Normal or RecoveryStatus.RecoveryFailed) && _marker != null)
        {
            // 已在流程中：只保留一个流程和一个标记，合并证据
            var merged = Merge(_marker.TriggerReason, reason);
            if (merged != _marker.TriggerReason)
            {
                _marker.TriggerReason = merged; SaveMarker();
                _lastTriggerReason = merged; _lastTriggerAt = _clock.Now;
                Event(LogCategory.Trigger, $"流程中出现新证据（{label}），触发原因合并为：{DisplayModeText.Of(merged)}", LogResult.Warning);
            }
            else _log.Log(LogCategory.Trigger, $"已在恢复流程中，不重复切屏（{label}）");
            return;
        }

        _externalLostSince = null;
        _lastTriggerReason = reason; _lastTriggerAt = _clock.Now;
        SetStatus(RecoveryStatus.PowerLossDetected);
        var topo = QueryTopology();
        // 先原子保存恢复标记，再切屏。
        _marker = new RecoveryState
        {
            PreferredModeSnapshot = s.PreferredMode,
            ModeBeforePowerLoss = topo.Mode,
            StartedAt = _clock.Now,
            TriggerReason = reason,
        };
        SaveMarker();
        Event(LogCategory.Trigger, $"触发保护（原因：{label}），已保存恢复标记（首选 {DisplayModeText.Of(_marker.PreferredModeSnapshot)}，触发前 {DisplayModeText.Of(topo.Mode)}）", LogResult.Warning);
        SetStatus(RecoveryStatus.SwitchingToInternal);
        if (topo.QuerySucceeded && topo.InternalActive)
            // 已是仅内屏，或扩展/复制且内屏仍活动：不重复发切屏命令，不强制仅内屏
            Event(LogCategory.Switch, $"内屏已处于活动状态（{DisplayModeText.Of(topo.Mode)}），不发送切屏命令", LogResult.Success);
        else
            await SwitchToInternalCoreAsync(CancellationToken.None);
        _waitingSince = PowerAllowsRestore(reason) ? _clock.Now : null;
        _externalStableSince = null;
        SetStatus(RecoveryStatus.WaitingForExternal);
    }

    /// <summary>切到仅内屏并校验，最多 2 次尝试。</summary>
    private async Task<OperationResult> SwitchToInternalCoreAsync(CancellationToken ct)
    {
        var before = QueryTopology();
        if (before.QuerySucceeded && before.Mode == DisplayMode.Internal)
        {
            Event(LogCategory.Switch, "当前已是仅电脑屏幕，无需切换", LogResult.Success);
            return new(true, "当前已是仅电脑屏幕");
        }
        string err = "";
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var launch = await _switcher.SwitchAsync(DisplayMode.Internal, ct);
            _suppressLostUntil = _clock.Now + OwnSwitchSuppression;
            if (!launch.Started)
            {
                err = $"DisplaySwitch.exe 启动失败：{launch.Error}";
                _log.Log(LogCategory.Switch, $"切换到内屏（第 {attempt} 次）：{err}", LogResult.Failure);
            }
            else
            {
                _log.Log(LogCategory.Switch, $"已调用 DisplaySwitch.exe 切换到仅电脑屏幕（第 {attempt} 次，退出码 {launch.ExitCode?.ToString() ?? "未知"}），等待校验");
                await _delay.Delay(TimeSpan.FromSeconds(_settings().VerifyDelaySeconds), ct);
                var t = QueryTopology();
                if (t.QuerySucceeded && t.InternalActive)
                {
                    Event(LogCategory.Switch, $"已确认内屏处于活动状态（{t}）", LogResult.Success);
                    return new(true, "已切回笔记本内屏");
                }
                err = t.QuerySucceeded ? $"校验未通过：{t}" : $"无法校验：{t.Error}";
                _log.Log(LogCategory.Switch, $"切换到内屏（第 {attempt} 次）{err}", LogResult.Failure);
            }
            if (attempt < 2) await _delay.Delay(TimeSpan.FromSeconds(2), ct);
        }
        if (_marker != null) { _marker.LastError = err; SaveMarker(); }
        Event(LogCategory.Switch, $"切换到内屏失败：{err}", LogResult.Failure);
        return new(false, $"切换到内屏失败：{err}", "可按 Win+P 选择“仅电脑屏幕”，或按 Win+Ctrl+Shift+B 重置显卡驱动后重试。");
    }

    // ───────────────────────── 定时检查 ─────────────────────────

    /// <summary>低频轮询 / WM_DISPLAYCHANGE 触发。忙时直接跳过，不排队。</summary>
    public async Task TickAsync()
    {
        if (!await _gate.WaitAsync(0)) return;
        try
        {
            var prev = _lastTopology;
            var topo = QueryTopology();
            if (_paused) { _externalLostSince = null; return; }

            if (_flowStatus is RecoveryStatus.Normal or RecoveryStatus.RecoveryFailed)
            {
                await DetectExternalLostAsync(prev, topo);
                return;
            }
            if (_flowStatus != RecoveryStatus.WaitingForExternal || _marker is null) return;
            await WaitingTickAsync(topo);
        }
        catch (Exception ex) { _log.Log(LogCategory.Error, $"定时检查异常：{ex.Message}", LogResult.Failure); }
        finally { _gate.Release(); }
    }

    /// <summary>
    /// 基于快照的外接活动显示路径消失检测（两种触发模式都启用）：
    /// 上一成功快照为“仅第二屏幕”且外接活动路径 &gt; 0，当前成功快照外接活动路径 = 0 → 进入确认窗口；
    /// 窗口结束后重新查询，仍为 0 才触发。查询失败不改变候选状态、也不触发。
    /// 排除：本程序/手动切屏后的抑制期；外接目标仍“可用”（多为用户 Win+P 主动切换）。
    /// </summary>
    private async Task DetectExternalLostAsync(DisplayTopology prev, DisplayTopology topo)
    {
        var s = _settings();
        if (!s.AutoSwitchOnPowerLoss) { _externalLostSince = null; return; }
        if (!topo.QuerySucceeded) return;

        if (_externalLostSince is null)
        {
            if (!(prev.QuerySucceeded && prev.Mode == DisplayMode.External && prev.ExternalActiveCount > 0 && topo.ExternalActiveCount == 0)) return;
            if (_clock.Now < _suppressLostUntil)
            {
                _log.Log(LogCategory.Display, "外接活动路径消失发生在本程序/用户手动切屏之后的抑制期内，不视为显示器断电");
                return;
            }
            if (topo.ExternalAvailableCount > 0)
            {
                Event(LogCategory.Display, $"外接活动路径消失，但 Windows 仍报告外接目标可用（{topo.ExternalAvailableCount}），多为主动切换（如 Win+P），不触发保护", LogResult.Info);
                return;
            }
            _externalLostSince = _clock.Now;
            Event(LogCategory.Display, $"外接活动显示路径消失（上次快照为仅第二屏幕、{prev.ExternalActiveCount} 条活动路径），开始 {s.ExternalLostConfirmSeconds} 秒确认", LogResult.Warning);
            return;
        }
        if (topo.ExternalActiveCount > 0)
        {
            _externalLostSince = null;
            Event(LogCategory.Display, "确认窗口内外接活动路径重新出现，视为抖动，不触发保护", LogResult.Info);
            return;
        }
        if (_clock.Now - _externalLostSince.Value < TimeSpan.FromSeconds(s.ExternalLostConfirmSeconds)) return;

        var again = QueryTopology(); // 确认窗口结束后重新查询
        if (!again.QuerySucceeded) { _log.Log(LogCategory.Display, $"确认查询失败（{again.Error}），下次检查重试", LogResult.Warning); return; }
        _externalLostSince = null;
        if (again.ExternalActiveCount > 0) { Event(LogCategory.Display, "确认查询时外接活动路径已恢复，不触发保护", LogResult.Info); return; }
        await TriggerProtectionCoreAsync(Power == PowerSource.Battery ? TriggerReason.CombinedEvidence : TriggerReason.ExternalDisplayLost);
    }

    private async Task WaitingTickAsync(DisplayTopology topo)
    {
        var m = _marker!;
        if (!PowerAllowsRestore(m.TriggerReason))
        {
            // 断电触发且未回到市电，或任意触发下正在用电池：维持内屏，不因显示器波动切屏
            _externalStableSince = null; _waitingSince = null;
            return;
        }
        var s = _settings();
        if (!s.AutoRestoreOnPowerReturn)
        {
            if (!_autoRestoreDisabledLogged) { _log.Log(LogCategory.Recovery, "自动恢复已禁用，等待用户手动恢复", LogResult.Warning); _autoRestoreDisabledLogged = true; }
            return;
        }
        _autoRestoreDisabledLogged = false;
        var now = _clock.Now;
        _waitingSince ??= now;
        var preferred = m.PreferredModeSnapshot;

        if (ExternalReady(topo, preferred))
        {
            if (_externalStableSince is not null && topo.ExternalAvailableCount != _stableAvailableCount)
            {
                _externalStableSince = null;
                Event(LogCategory.Display, $"外接可用目标数变化（{_stableAvailableCount} → {topo.ExternalAvailableCount}），稳定计时重置", LogResult.Warning);
            }
            if (_externalStableSince is null)
            {
                _externalStableSince = now;
                _stableAvailableCount = topo.ExternalAvailableCount;
                Event(LogCategory.Display, $"Windows 检测到外接显示目标可用（{topo.ExternalAvailableCount}），开始 {s.StabilitySeconds} 秒稳定计时（不代表显示器已亮屏）", LogResult.Info);
            }
            else if (now - _externalStableSince.Value >= TimeSpan.FromSeconds(s.StabilitySeconds))
            {
                await RestoreCoreAsync(preferred, automatic: true);
                return;
            }
        }
        else if (_externalStableSince is not null)
        {
            _externalStableSince = null;
            Event(LogCategory.Display, "外接显示路径消失（连接抖动），稳定计时重置", LogResult.Warning);
        }

        // 所有触发原因统一使用恢复超时：从满足电源条件起计（含市电证据：市电恢复起；仅显示器：触发起）。
        // 超时 → RecoveryFailed，保留标记、确保内屏，不再自动切屏。
        if (_externalStableSince is null && now - _waitingSince.Value >= TimeSpan.FromMinutes(s.RecoveryTimeoutMinutes))
        {
            m.LastError = $"满足恢复电源条件后 {s.RecoveryTimeoutMinutes} 分钟内未检测到稳定可用的外接显示目标";
            SaveMarker();
            Event(LogCategory.Recovery, $"恢复超时：{m.LastError}。保留恢复标记，继续使用内屏", LogResult.Failure);
            SetStatus(RecoveryStatus.RecoveryFailed);
            await EnsureInternalSafeAsync();
        }
    }

    private async Task<OperationResult> RestoreCoreAsync(DisplayMode preferred, bool automatic)
    {
        _flowCts?.Dispose();
        _flowCts = new CancellationTokenSource();
        var ct = _flowCts.Token;
        var s = _settings();
        var attempts = automatic ? 1 + s.MaxRetries : 1;
        var reason = _marker?.TriggerReason;
        SetStatus(RecoveryStatus.RestoringPreferredMode);
        Event(LogCategory.Recovery, $"{(automatic ? "自动" : "手动")}恢复首选模式：{DisplayModeText.Of(preferred)}（最多 {attempts} 次）", LogResult.Info);
        string err = "";
        try
        {
            for (var i = 1; i <= attempts; i++)
            {
                if (_marker != null) { _marker.AttemptCount++; _marker.LastAttemptAt = _clock.Now; SaveMarker(); }
                var launch = await _switcher.SwitchAsync(preferred, ct);
                _suppressLostUntil = _clock.Now + OwnSwitchSuppression;
                if (!launch.Started)
                    err = $"DisplaySwitch.exe 启动失败：{launch.Error}";
                else
                {
                    _log.Log(LogCategory.Switch, $"已调用 DisplaySwitch.exe 切换到{DisplayModeText.Of(preferred)}（第 {i} 次，退出码 {launch.ExitCode?.ToString() ?? "未知"}），等待校验");
                    await _delay.Delay(TimeSpan.FromSeconds(s.VerifyDelaySeconds), ct);
                    var t = QueryTopology();
                    if (t.QuerySucceeded && t.Mode == preferred)
                    {
                        // 只有确认系统状态符合预期才清除标记
                        ClearMarker();
                        SetStatus(RecoveryStatus.Normal);
                        Event(LogCategory.Recovery, $"恢复成功，已确认当前模式为 {DisplayModeText.Of(preferred)}，恢复标记已清除", LogResult.Success);
                        return new(true, $"已恢复为{DisplayModeText.Of(preferred)}。注意：这只代表 Windows 已启用该显示路径，请以显示器实际画面为准。");
                    }
                    err = t.QuerySucceeded ? $"校验未通过，当前为 {DisplayModeText.Of(t.Mode)}（{t}）" : $"无法校验：{t.Error}";
                }
                _log.Log(LogCategory.Switch, $"恢复尝试 {i}/{attempts} 失败：{err}", LogResult.Failure);
                if (_marker != null) { _marker.LastError = err; SaveMarker(); }

                if (i < attempts)
                {
                    _log.Log(LogCategory.Recovery, $"{s.RetryIntervalSeconds} 秒后重试");
                    await _delay.Delay(TimeSpan.FromSeconds(s.RetryIntervalSeconds), ct);
                    var t = QueryTopology();
                    var powerOk = reason is null ? Power != PowerSource.Battery : PowerAllowsRestore(reason.Value);
                    if (!powerOk || !ExternalReady(t, preferred))
                    {
                        Event(LogCategory.Recovery, "重试前条件不再满足（电源或外接路径变化），返回等待并保护内屏", LogResult.Warning);
                        _externalStableSince = null;
                        if (!powerOk) _waitingSince = null;
                        SetStatus(_marker != null ? RecoveryStatus.WaitingForExternal : RecoveryStatus.Normal);
                        await EnsureInternalSafeAsync();
                        return new(false, "恢复中断：外接显示路径或市电已消失", "等待显示器稳定后再试。");
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            SetStatus(_marker != null ? RecoveryStatus.WaitingForExternal : RecoveryStatus.Normal);
            _externalStableSince = null;
            Event(LogCategory.Recovery, "自动恢复被用户操作中断", LogResult.Warning);
            return new(false, "恢复已中断");
        }

        if (_marker != null)
        {
            SetStatus(RecoveryStatus.RecoveryFailed);
            Event(LogCategory.Recovery, $"恢复失败（已尝试 {attempts} 次）：{err}。保留恢复标记，停止自动切屏", LogResult.Failure);
        }
        else Event(LogCategory.Recovery, $"手动恢复失败：{err}", LogResult.Failure);
        await EnsureInternalSafeAsync();
        return new(false, $"恢复失败：{err}",
            "请确认显示器已通电且 HDMI 连接正常，在 Windows“设置 > 系统 > 屏幕”中点“检测”，或按 Win+P 手动选择模式；稍后可再次点击“尝试恢复首选显示模式”。");
    }

    /// <summary>恢复失败后的安全策略：确保内屏可用。</summary>
    private async Task EnsureInternalSafeAsync()
    {
        var t = QueryTopology();
        if (t.QuerySucceeded && t.InternalActive) return;
        _log.Log(LogCategory.Recovery, "安全策略：内屏未处于活动状态，切回内屏", LogResult.Warning);
        await SwitchToInternalCoreAsync(CancellationToken.None);
    }

    // ───────────────────────── 关机前尽力尝试 ─────────────────────────

    /// <summary>
    /// 关机/注销（WM_QUERYENDSESSION → Application.SessionEnding，或 WM_ENDSESSION）时尽力尝试一次切到首选模式，
    /// 让 Windows 下次开机沿用该显示配置。严格限时、只尝试一次、绝不清除恢复标记、绝不取消关机。
    /// 下次启动时由 InitializeAsync 重新校验并决定是否清除标记。
    /// </summary>
    public ShutdownAttemptResult TryPreferredModeOnShutdown(TimeSpan budget)
    {
        if (Interlocked.Exchange(ref _shutdownAttempted, 1) == 1) return new(false, null, "本次会话已尝试过");
        ShutdownAttemptResult Skip(string why)
        {
            _log.Log(LogCategory.Shutdown, $"关机前尝试：跳过（{why}）");
            _lastShutdownAttempt = $"跳过：{why}";
            return new(false, null, why);
        }
        var s = _settings();
        if (!s.TryPreferredModeOnShutdown) return Skip("设置未开启");
        if (_paused) return Skip("保护已暂停");
        if (!s.AutoRestoreOnPowerReturn) return Skip("自动恢复已禁用");
        if (!_gate.Wait(0)) return Skip("其他切屏流程正在执行");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var m = _marker;
            if (m is null) return Skip("没有未完成的恢复流程");
            if (!PowerAllowsRestore(m.TriggerReason)) return Skip($"电源条件不满足（{DisplayModeText.Of(Power)}）");
            var before = QueryTopology();
            if (IsInPreferred(before, m.PreferredModeSnapshot)) return Skip("已处于首选模式");
            if (!ExternalReady(before, m.PreferredModeSnapshot)) return Skip("Windows 未检测到可用的外接显示目标");
            try { DisplaySwitchArgs.For(m.PreferredModeSnapshot, s.DisplaySwitchArgStyle == DisplaySwitchArgStyle.Numeric ? DisplaySwitchArgStyle.Numeric : DisplaySwitchArgStyle.Slash); }
            catch (ArgumentOutOfRangeException) { return Skip($"首选模式 {m.PreferredModeSnapshot} 无法映射为 DisplaySwitch 参数"); }

            using var cts = new CancellationTokenSource(budget);
            string result; bool? verified = null;
            try
            {
                var launchTask = Task.Run(() => _switcher.SwitchAsync(m.PreferredModeSnapshot, cts.Token));
                if (!launchTask.Wait(budget)) result = "DisplaySwitch.exe 在时限内未返回";
                else if (!launchTask.Result.Started) result = $"启动失败：{launchTask.Result.Error}";
                else
                {
                    // 在剩余预算内短暂等待后查询一次，只用于记录
                    var left = budget - sw.Elapsed;
                    if (left > TimeSpan.FromMilliseconds(200)) Thread.Sleep(TimeSpan.FromMilliseconds(Math.Min(1500, left.TotalMilliseconds - 100)));
                    var after = QueryTopology();
                    verified = IsInPreferred(after, m.PreferredModeSnapshot);
                    result = verified == true ? $"已尝试，关机前查询为 {DisplayModeText.Of(after.Mode)}（未保证下次开机生效）" : $"已尝试，关机前未确认（当前 {DisplayModeText.Of(after.Mode)}）";
                }
            }
            catch (AggregateException ae) when (ae.InnerException is OperationCanceledException) { result = "DisplaySwitch.exe 在时限内未返回"; }
            catch (Exception ex) { result = $"异常：{(ex as AggregateException)?.InnerException?.Message ?? ex.Message}"; }

            m.LastShutdownAttemptAt = _clock.Now;
            m.LastShutdownAttemptResult = result;
            SaveMarker(); // 只更新诊断字段，标记保持 Pending
            _lastShutdownAttempt = result;
            _log.Log(LogCategory.Shutdown, $"关机前已尝试（非已恢复）切到 {DisplayModeText.Of(m.PreferredModeSnapshot)}：{result}（耗时 {sw.ElapsedMilliseconds} ms，恢复标记保留，下次启动校验）",
                verified == true ? LogResult.Success : LogResult.Warning);
            return new(true, verified, result);
        }
        finally { _gate.Release(); }
    }

    // ───────────────────────── 用户操作 ─────────────────────────

    public async Task<OperationResult> ManualSwitchToInternalAsync()
    {
        _flowCts?.Cancel();
        await _gate.WaitAsync();
        try
        {
            _log.Log(LogCategory.Switch, "用户手动请求切回笔记本内屏");
            _externalLostSince = null;
            _suppressLostUntil = _clock.Now + OwnSwitchSuppression;
            var r = await SwitchToInternalCoreAsync(CancellationToken.None);
            if (_marker != null && _flowStatus == RecoveryStatus.RecoveryFailed)
            {
                // 用户手动处理后重新进入等待，由超时/重试上限再次限制
                _waitingSince = PowerAllowsRestore(_marker.TriggerReason) ? _clock.Now : null;
                _externalStableSince = null;
                SetStatus(RecoveryStatus.WaitingForExternal);
            }
            return r;
        }
        catch (Exception ex) { _log.Log(LogCategory.Error, ex.ToString(), LogResult.Failure); return new(false, ex.Message); }
        finally { _gate.Release(); }
    }

    public async Task<OperationResult> ManualRestorePreferredAsync()
    {
        _flowCts?.Cancel();
        await _gate.WaitAsync();
        try
        {
            var preferred = _marker?.PreferredModeSnapshot ?? _settings().PreferredMode;
            var t = QueryTopology();
            if (DisplaySwitchArgs.RequiresExternal(preferred) && t.QuerySucceeded && t.ExternalAvailableCount == 0)
            {
                var msg = "Windows 当前未检测到外接显示路径，未执行切换（避免黑屏）";
                Event(LogCategory.Recovery, msg, LogResult.Warning);
                return new(false, msg, "请确认显示器已通电、HDMI 已插好，然后在“设置 > 系统 > 屏幕 > 多显示器”中点击“检测”。");
            }
            return await RestoreCoreAsync(preferred, automatic: false);
        }
        catch (Exception ex) { _log.Log(LogCategory.Error, ex.ToString(), LogResult.Failure); return new(false, ex.Message); }
        finally { _gate.Release(); }
    }

    public async Task SetPausedAsync(bool paused)
    {
        if (paused) _flowCts?.Cancel();
        await _gate.WaitAsync();
        try
        {
            if (_paused == paused) return;
            _paused = paused;
            _externalLostSince = null;
            if (!paused)
            {
                _flowStatus = _marker != null ? RecoveryStatus.WaitingForExternal : RecoveryStatus.Normal;
                _waitingSince = _marker != null && PowerAllowsRestore(_marker.TriggerReason) ? _clock.Now : null;
                _externalStableSince = null;
            }
            Event(LogCategory.Recovery, paused ? "用户暂停了自动保护（恢复标记保留）" : "用户恢复了自动保护", LogResult.Info);
        }
        finally { _gate.Release(); }
    }

    /// <summary>放弃未完成的恢复流程（用户明确操作）。</summary>
    public async Task DiscardRecoveryAsync()
    {
        _flowCts?.Cancel();
        await _gate.WaitAsync();
        try
        {
            ClearMarker();
            _flowStatus = RecoveryStatus.Normal;
            Event(LogCategory.Recovery, "用户放弃了未完成的恢复流程，恢复标记已清除", LogResult.Info);
        }
        finally { _gate.Release(); }
    }

    public void RefreshTopology()
    {
        if (!_gate.Wait(0)) return;
        try { QueryTopology(); } finally { _gate.Release(); }
    }
}
