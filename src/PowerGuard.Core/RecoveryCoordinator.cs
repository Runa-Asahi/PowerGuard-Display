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
    DateTimeOffset? WaitingOnAcSince,
    DateTimeOffset? PausedUntil = null,
    int GuardAttempts = 0,
    bool? LidOpen = null,
    bool DdcSupported = false);

/// <summary>关机前切屏的执行结果（用于日志与测试）。</summary>
public sealed record ShutdownRestoreOutcome(bool Attempted, bool Success, ShutdownDecision Decision, string Message);

/// <summary>
/// 断电保护与自动恢复状态机。所有改变状态的流程都在 _gate 内串行执行（防重入）。
/// 电源/显示事件由平台层推送进来；Tick 由低频定时器和 WM_DISPLAYCHANGE 驱动。
/// 线程模型：可变字段只在持有 _gate 时修改；其他线程（UI、托盘）只读取通过 Publish() 发布的不可变快照。
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
    private readonly object _ctsLock = new();
    private volatile CoordinatorSnapshot _snapshot;

    private RecoveryStatus _flowStatus = RecoveryStatus.Normal;
    private bool _paused;
    private PowerSource _power = PowerSource.Unknown;
    private long _powerSeq;
    private DisplayTopology _lastTopology = DisplayTopology.Failed("尚未查询");
    private RecoveryState? _marker;
    private DateTimeOffset? _externalStableSince;
    private DateTimeOffset? _waitingOnAcSince;
    private DateTimeOffset? _lastEventAt;
    private string? _lastEventText;
    private LogResult _lastEventResult;
    private CancellationTokenSource? _flowCts;
    private bool _autoRestoreDisabledLogged;
    private DateTimeOffset? _pausedUntil;
    private DateTimeOffset? _lastGuardAt;
    private int _guardAttempts;
    private bool? _lidOpen;
    private bool _lidClosedLogged;
    private readonly IMonitorProbe? _probe;
    private bool _ddcSupported;
    private bool _ddcUsedThisFlow;
    private DateTimeOffset? _lastDdcLearnAt;

    /// <summary>断电期间内屏守护：两次重新切回内屏之间的最短间隔，连续失败后的退避间隔。</summary>
    public static readonly TimeSpan GuardInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan GuardBackoff = TimeSpan.FromSeconds(60);
    public const int GuardFastAttempts = 6;

    public event EventHandler? StateChanged;

    /// <summary>需要立刻引起用户注意的问题（标题, 内容）：内屏切换失败、断电期间无法保持内屏。App 用通知 + 提示音呈现。</summary>
    public event Action<string, string>? Alert;

    private void RaiseAlert(string title, string text)
    {
        _log.Log(LogCategory.Recovery, $"{title}：{text}", LogResult.Failure);
        try { Alert?.Invoke(title, text); } catch { }
    }

    public RecoveryCoordinator(Func<AppSettings> settings, IRecoveryStateStore store, IDisplayTopologyProvider topology,
        IDisplaySwitcher switcher, IClock clock, IDelay delay, IAppLogger log, IMonitorProbe? probe = null)
    {
        _settings = settings; _store = store; _topology = topology; _switcher = switcher;
        _clock = clock; _delay = delay; _log = log; _probe = probe;
        _snapshot = BuildSnapshot();
    }

    // 以下只读属性都来自已发布的快照，任何线程读取都安全。
    public RecoveryStatus Status => _snapshot.Status;
    public bool Paused => _snapshot.Paused;
    public PowerSource Power { get { lock (_powerLock) return _power; } }
    /// <summary>恢复标记的副本（修改它不影响协调器）。</summary>
    public RecoveryState? Marker => _snapshot.Marker?.Copy();

    public CoordinatorSnapshot Snapshot() => _snapshot;

    private CoordinatorSnapshot BuildSnapshot() => new(_paused ? RecoveryStatus.Paused : _flowStatus, Power, _lastTopology, _marker?.Copy(), _paused,
        _lastEventAt, _lastEventText, _lastEventResult, _externalStableSince, _waitingOnAcSince, _pausedUntil, _guardAttempts, _lidOpen, _ddcSupported);

    /// <summary>发布新快照。只在持有 _gate（或构造期间）调用。</summary>
    private void Publish() => _snapshot = BuildSnapshot();

    private void CancelFlow() { lock (_ctsLock) _flowCts?.Cancel(); }

    private CancellationToken NewFlowToken()
    {
        lock (_ctsLock)
        {
            _flowCts?.Dispose();
            _flowCts = new CancellationTokenSource();
            return _flowCts.Token;
        }
    }

    private void ReleaseGate() { Publish(); _gate.Release(); }

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

    private void Changed() { Publish(); try { StateChanged?.Invoke(this, EventArgs.Empty); } catch { } }

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

    // ───────────────────────── 初始化 ─────────────────────────

    public async Task InitializeAsync(PowerSource initialPower)
    {
        await _gate.WaitAsync();
        try
        {
            lock (_powerLock) _power = initialPower;
            var topo = QueryTopology();
            _log.Log(LogCategory.App, $"初始化：电源={DisplayModeText.Of(initialPower)}；{topo}");

            LoadResult<RecoveryState> r;
            try { r = _store.Load(); }
            catch (Exception ex) { r = new(null, LoadStatus.Unreadable, ex.Message); }
            if (r.Status == LoadStatus.Unreadable)
            {
                // 文件存在但读不出来：同样按未完成流程保守处理，但不覆盖原文件（之后保存时才会写入）。
                _log.Log(LogCategory.Recovery, $"恢复标记无法读取：{r.Message}。按未完成流程处理，首选模式取当前设置。", LogResult.Warning);
                _marker = new RecoveryState { PreferredModeSnapshot = _settings().PreferredMode, StartedAt = _clock.Now, LastError = "恢复标记文件无法读取" };
            }
            else if (r.Status == LoadStatus.Corrupt)
            {
                // 文件存在说明之前有未完成流程；内容损坏时用当前首选模式重建，宁可继续保护也不丢流程。
                _log.Log(LogCategory.Recovery, $"恢复标记损坏：{r.Message}。按未完成流程处理，首选模式取当前设置。", LogResult.Warning);
                _marker = new RecoveryState { PreferredModeSnapshot = _settings().PreferredMode, StartedAt = _clock.Now, LastError = "恢复标记文件损坏后重建" };
                SaveMarker();
            }
            else _marker = r.Value is { Pending: true } ? r.Value : null;

            if (_marker is null)
            {
                // 没有标记：即使当前是电池也不切屏，不推断曾经断电。
                _flowStatus = RecoveryStatus.Normal;
                Event(LogCategory.App, initialPower == PowerSource.Battery ? "启动时为电池供电，但无恢复标记，不执行切屏" : "启动完成，无未完成的恢复流程", LogResult.Info);
                return;
            }

            Event(LogCategory.Recovery, $"启动时发现未完成的恢复标记（开始于 {_marker.StartedAt:yyyy-MM-dd HH:mm:ss}，首选模式 {DisplayModeText.Of(_marker.PreferredModeSnapshot)}）", LogResult.Warning);
            _flowStatus = RecoveryStatus.WaitingForExternal;
            _externalStableSince = null;
            if (initialPower == PowerSource.Ac) _waitingOnAcSince = _clock.Now;
            else
            {
                _waitingOnAcSince = null;
                // 仍在电池/未知：继续保护内屏
                if (!topo.QuerySucceeded || !topo.InternalActive) await SwitchToInternalCoreAsync(CancellationToken.None);
            }
            Changed();
        }
        finally { ReleaseGate(); }
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
        // 与 v1.0.0 一致：电源变化立即记录，不排队等锁（否则恢复流程持锁时会推迟断电确认）。
        var powerText = $"电源来源变化：{DisplayModeText.Of(prev)} → {DisplayModeText.Of(p)}";
        if (_gate.Wait(0))
        {
            try { Event(LogCategory.Power, powerText, LogResult.Info); }
            finally { ReleaseGate(); }
        }
        else _log.Log(LogCategory.Power, powerText);

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
                _waitingOnAcSince = null; _externalStableSince = null;
                if (_paused) { _log.Log(LogCategory.Recovery, "保护已暂停，忽略断电事件", LogResult.Warning); return; }
                if (!_settings().AutoSwitchOnPowerLoss) { _log.Log(LogCategory.Recovery, "设置中已禁用断电自动切屏，忽略断电事件", LogResult.Warning); return; }
                if (_flowStatus is RecoveryStatus.Normal or RecoveryStatus.RecoveryFailed)
                    await PowerLossFlowCoreAsync();
                else
                    _log.Log(LogCategory.Recovery, "已在恢复流程中，保持内屏优先，不重复切屏");
            }
            finally { ReleaseGate(); }
        }
        else if (p == PowerSource.Ac)
        {
            await _gate.WaitAsync();
            try
            {
                if (_flowStatus == RecoveryStatus.WaitingForExternal && Power == PowerSource.Ac)
                {
                    _waitingOnAcSince = _clock.Now; _externalStableSince = null;
                    Event(LogCategory.Recovery, "市电已恢复，等待外接显示路径稳定后再恢复（不会立即切回）", LogResult.Info);
                }
            }
            finally { ReleaseGate(); }
        }
        // Unknown：只记录，绝不当作断电。
    }

    private async Task PowerLossFlowCoreAsync()
    {
        SetStatus(RecoveryStatus.PowerLossDetected);
        var topo = QueryTopology();
        // 先原子保存恢复标记，再切屏。
        _marker = new RecoveryState
        {
            PreferredModeSnapshot = _settings().PreferredMode,
            ModeBeforePowerLoss = topo.Mode,
            StartedAt = _clock.Now,
        };
        SaveMarker();
        Event(LogCategory.Recovery, $"检测到市电转电池，已保存恢复标记（首选 {DisplayModeText.Of(_marker.PreferredModeSnapshot)}，断电前 {DisplayModeText.Of(topo.Mode)}）", LogResult.Warning);
        SetStatus(RecoveryStatus.SwitchingToInternal);
        // 断电时总是下发切内屏命令，即使此刻读到的已是“仅电脑屏幕”：
        // 断电瞬间外接显示器会短暂消失，Windows 自己回落到内屏；随后显示器（仍保留 EDID）被重新识别时，
        // Windows 会按数据库中“内屏+外接”这组显示器上次的配置（仅第二屏幕）切回去。
        // 显式切一次内屏，并在断电期间持续守护（见 TickAsync），才能让内屏保持点亮。
        await SwitchToInternalCoreAsync(CancellationToken.None, force: true);
        _lastGuardAt = _clock.Now; _guardAttempts = 0; _ddcUsedThisFlow = false;
        _waitingOnAcSince = Power == PowerSource.Ac ? _clock.Now : null;
        _externalStableSince = null;
        SetStatus(RecoveryStatus.WaitingForExternal);
    }

    /// <summary>切到仅内屏并校验，最多 2 次尝试（第 2 次用备用切屏方式）。</summary>
    /// <param name="force">true = 即使当前已是仅电脑屏幕也下发命令（断电保护 / 断电期间守护）。</param>
    private async Task<OperationResult> SwitchToInternalCoreAsync(CancellationToken ct, bool force = false)
    {
        var before = QueryTopology();
        if (!force && before.QuerySucceeded && before.Mode == DisplayMode.Internal)
        {
            Event(LogCategory.Switch, "当前已是仅电脑屏幕，无需切换", LogResult.Success);
            return new(true, "当前已是仅电脑屏幕");
        }
        string err = "";
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            var launch = await _switcher.SwitchAsync(DisplayMode.Internal, ct, alternate: attempt > 1);
            if (!launch.Started)
            {
                err = $"切屏命令执行失败：{launch.Error}";
                _log.Log(LogCategory.Switch, $"切换到内屏（第 {attempt} 次）：{err}", LogResult.Failure);
            }
            else
            {
                _log.Log(LogCategory.Switch, $"已下发切换到仅电脑屏幕的命令（第 {attempt} 次{(attempt > 1 ? "，备用方式" : "")}，退出码 {launch.ExitCode?.ToString() ?? "未知"}），等待校验");
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
        RaiseAlert("无法切回笔记本内屏", "请按 Win+P 选择“仅电脑屏幕”；仍无画面时按 Win+Ctrl+Shift+B 重置显卡驱动。");
        return new(false, $"切换到内屏失败：{err}", "可按 Win+P 选择“仅电脑屏幕”，或按 Win+Ctrl+Shift+B 重置显卡驱动后重试。");
    }

    // ───────────────────────── 定时检查 ─────────────────────────

    /// <summary>低频轮询 / WM_DISPLAYCHANGE 触发。忙时直接跳过，不排队。</summary>
    public async Task TickAsync()
    {
        if (!await _gate.WaitAsync(0)) return;
        try
        {
            var topo = QueryTopology();
            if (_paused && _pausedUntil is { } until && _clock.Now >= until) ResumeCore("暂停时间已到，自动恢复保护");
            LearnDdcSupport(topo);
            if (!_paused && _marker is not null && _flowStatus == RecoveryStatus.RecoveryFailed && Power != PowerSource.Ac)
            {
                await GuardInternalOnBatteryAsync(topo); // 恢复失败后又断电：同样守护内屏
                return;
            }
            if (_paused || _flowStatus != RecoveryStatus.WaitingForExternal || _marker is null) return;
            if (Power != PowerSource.Ac)
            {
                // 断电期间：绝不自动恢复外接；若 Windows 自己把显示切回了外接（显示器仍被识别），重新切回内屏
                await GuardInternalOnBatteryAsync(topo);
                return;
            }
            _guardAttempts = 0;

            var s = _settings();
            if (!s.AutoRestoreOnPowerReturn)
            {
                if (!_autoRestoreDisabledLogged) { _log.Log(LogCategory.Recovery, "自动恢复已禁用，等待用户手动恢复", LogResult.Warning); _autoRestoreDisabledLogged = true; }
                return;
            }
            _autoRestoreDisabledLogged = false;
            var now = _clock.Now;
            _waitingOnAcSince ??= now;
            var preferred = _marker.PreferredModeSnapshot;
            var ready = !DisplaySwitchArgs.RequiresExternal(preferred) || (topo.QuerySucceeded && topo.ExternalAvailableCount > 0);

            if (ready)
            {
                if (_externalStableSince is null)
                {
                    _externalStableSince = now;
                    Event(LogCategory.Display, $"Windows 检测到外接显示路径可用，开始 {s.StabilitySeconds} 秒稳定计时", LogResult.Info);
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

            if (now - _waitingOnAcSince.Value >= TimeSpan.FromMinutes(s.RecoveryTimeoutMinutes))
            {
                _marker.LastError = $"市电恢复后 {s.RecoveryTimeoutMinutes} 分钟内未检测到稳定的外接显示路径";
                SaveMarker();
                Event(LogCategory.Recovery, $"恢复超时：{_marker.LastError}。保留恢复标记，继续使用内屏", LogResult.Failure);
                SetStatus(RecoveryStatus.RecoveryFailed);
                await EnsureInternalSafeAsync();
            }
        }
        catch (Exception ex) { _log.Log(LogCategory.Error, $"定时检查异常：{ex.Message}", LogResult.Failure); }
        finally { ReleaseGate(); }
    }

    // ───────────────────────── 盖子 / DDC ─────────────────────────

    /// <summary>笔记本盖子状态变化（GUID_LIDSWITCH_STATE_CHANGE）。断电期间开盖时立即尝试切回内屏。</summary>
    public async Task OnLidChangedAsync(bool open)
    {
        await _gate.WaitAsync();
        try
        {
            if (_lidOpen == open) return;
            _lidOpen = open; _lidClosedLogged = false;
            _log.Log(LogCategory.Power, open ? "笔记本盖子已打开" : "笔记本盖子已合上");
            if (open && Power != PowerSource.Ac && !_paused && _marker != null &&
                _flowStatus is RecoveryStatus.WaitingForExternal or RecoveryStatus.RecoveryFailed)
            {
                _lastGuardAt = null; // 不受限速约束，立刻检查
                await GuardInternalOnBatteryAsync(QueryTopology());
            }
        }
        finally { ReleaseGate(); }
    }

    /// <summary>正常使用外接显示器时，每 60 秒尝试一次 DDC/CI，确认它是否支持（只在开启实验功能时）。</summary>
    private void LearnDdcSupport(DisplayTopology topo)
    {
        if (_probe is null || _ddcSupported || !_settings().VerifyRestoreWithDdc) return;
        if (_flowStatus != RecoveryStatus.Normal || Power != PowerSource.Ac || !topo.QuerySucceeded || topo.ExternalActiveCount == 0) return;
        var now = _clock.Now;
        if (_lastDdcLearnAt is { } last && now - last < TimeSpan.FromSeconds(60)) return;
        _lastDdcLearnAt = now;
        MonitorResponse r;
        try { r = _probe.ProbeActiveExternal(); } catch { r = MonitorResponse.Unsupported; }
        if (r == MonitorResponse.Responding)
        {
            _ddcSupported = true;
            _log.Log(LogCategory.Display, "外接显示器支持 DDC/CI，恢复后将用它确认显示器真的在应答", LogResult.Success);
            Changed();
        }
    }

    /// <summary>
    /// 恢复后确认外接显示器真的在应答。只在：开启实验功能、已确认支持 DDC、本流程尚未回退过、首选模式需要外接 时检查；
    /// 3 次（间隔 2 秒）都明确“无应答”才判定为没电，任何“无法判断”都视为正常（回到已验证的行为）。
    /// </summary>
    private async Task<bool> ExternalReallyRespondingAsync(DisplayMode preferred, CancellationToken ct)
    {
        if (_probe is null || !_ddcSupported || _ddcUsedThisFlow || !_settings().VerifyRestoreWithDdc || !DisplaySwitchArgs.RequiresExternal(preferred)) return true;
        for (var i = 0; i < 3; i++)
        {
            MonitorResponse r;
            try { r = _probe.ProbeActiveExternal(); } catch { r = MonitorResponse.Unsupported; }
            if (r != MonitorResponse.NotResponding) return true;
            if (i < 2) await _delay.Delay(TimeSpan.FromSeconds(2), ct);
        }
        _log.Log(LogCategory.Display, "恢复后外接显示器连续 3 次没有 DDC/CI 应答", LogResult.Warning);
        return false;
    }

    /// <summary>
    /// 断电期间的内屏守护：只要不是市电、存在恢复标记且内屏不在活动状态（或拓扑读不出来），就重新切回内屏。
    /// 限速：前 GuardFastAttempts 次至少间隔 GuardInterval，之后每 GuardBackoff 一次，避免与驱动来回拉扯。
    /// 内屏恢复活动后计数清零。
    /// </summary>
    private async Task GuardInternalOnBatteryAsync(DisplayTopology topo)
    {
        if (topo.QuerySucceeded && topo.InternalActive) { _guardAttempts = 0; return; }
        if (_lidOpen == false)
        {
            // 盖子合上时内屏无法点亮，反复切换没有意义；开盖时（OnLidChangedAsync）立即重试
            if (!_lidClosedLogged) { _log.Log(LogCategory.Recovery, "内屏守护：笔记本盖子已合上，开盖后再切回内屏", LogResult.Warning); _lidClosedLogged = true; }
            return;
        }
        var now = _clock.Now;
        var interval = _guardAttempts >= GuardFastAttempts ? GuardBackoff : GuardInterval;
        if (_lastGuardAt is { } last && now - last < interval) return;
        _lastGuardAt = now; _guardAttempts++;
        if (_guardAttempts == GuardFastAttempts + 1)
            RaiseAlert("内屏反复被切走", $"断电期间 Windows 已连续 {GuardFastAttempts} 次把显示切回外接显示器，程序改为每 {GuardBackoff.TotalSeconds:0} 秒重试一次。可按 Win+P 选择“仅电脑屏幕”。");
        Event(LogCategory.Recovery, topo.QuerySucceeded
            ? $"内屏守护：断电期间显示被切回{DisplayModeText.Of(topo.Mode)}（多为 Windows 重新识别到仍保留信号的外接显示器），重新切回内屏（第 {_guardAttempts} 次）"
            : $"内屏守护：断电期间无法读取显示拓扑（{topo.Error}），保守地重新切回内屏（第 {_guardAttempts} 次）", LogResult.Warning);
        await SwitchToInternalCoreAsync(CancellationToken.None, force: true);
    }

    private async Task<OperationResult> RestoreCoreAsync(DisplayMode preferred, bool automatic)
    {
        var ct = NewFlowToken();
        var s = _settings();
        var attempts = automatic ? 1 + s.MaxRetries : 1;
        SetStatus(RecoveryStatus.RestoringPreferredMode);
        Event(LogCategory.Recovery, $"{(automatic ? "自动" : "手动")}恢复首选模式：{DisplayModeText.Of(preferred)}（最多 {attempts} 次）", LogResult.Info);
        string err = "";
        var current = QueryTopology();
        if (current.QuerySucceeded && current.Mode == preferred)
        {
            // 例如关机前已切回首选模式：无需再切，直接确认并清除标记
            CompleteRestore(preferred, "当前已是首选模式，无需切换");
            return new(true, $"当前已是{DisplayModeText.Of(preferred)}");
        }
        try
        {
            for (var i = 1; i <= attempts; i++)
            {
                if (_marker != null) { _marker.AttemptCount++; _marker.LastAttemptAt = _clock.Now; SaveMarker(); }
                var launch = await _switcher.SwitchAsync(preferred, ct);
                if (!launch.Started)
                    err = $"DisplaySwitch.exe 启动失败：{launch.Error}";
                else
                {
                    _log.Log(LogCategory.Switch, $"已调用 DisplaySwitch.exe 切换到{DisplayModeText.Of(preferred)}（第 {i} 次，退出码 {launch.ExitCode?.ToString() ?? "未知"}），等待校验");
                    await _delay.Delay(TimeSpan.FromSeconds(s.VerifyDelaySeconds), ct);
                    var t = QueryTopology();
                    if (t.QuerySucceeded && t.Mode == preferred && automatic && !await ExternalReallyRespondingAsync(preferred, ct))
                    {
                        _ddcUsedThisFlow = true;
                        RaiseAlert("外接显示器没有应答", "Windows 显示已切到外接，但显示器通过 DDC/CI 没有应答（可能其实没电），已切回内屏并继续等待。");
                        await SwitchToInternalCoreAsync(CancellationToken.None, force: true);
                        _externalStableSince = null;
                        SetStatus(_marker != null ? RecoveryStatus.WaitingForExternal : RecoveryStatus.Normal);
                        return new(false, "外接显示器没有应答，已切回内屏", "确认显示器已开机后再试；若误判，可在设置中关闭“用 DDC/CI 确认显示器”。");
                    }
                    if (t.QuerySucceeded && t.Mode == preferred)
                    {
                        // 只有确认系统状态符合预期才清除标记
                        CompleteRestore(preferred, $"恢复成功，已确认当前模式为 {DisplayModeText.Of(preferred)}");
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
                    if (Power != PowerSource.Ac || (DisplaySwitchArgs.RequiresExternal(preferred) && t.ExternalAvailableCount == 0))
                    {
                        Event(LogCategory.Recovery, "重试前条件不再满足（市电或外接路径消失），返回等待并保护内屏", LogResult.Warning);
                        _externalStableSince = null;
                        _waitingOnAcSince = Power == PowerSource.Ac ? _waitingOnAcSince : null;
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

    private void CompleteRestore(DisplayMode preferred, string text)
    {
        try { _store.Clear(); }
        catch (Exception ex) { _log.Log(LogCategory.Error, $"清除恢复标记失败：{ex.Message}", LogResult.Failure); }
        _marker = null; _externalStableSince = null; _waitingOnAcSince = null;
        SetStatus(RecoveryStatus.Normal);
        Event(LogCategory.Recovery, $"{text}，恢复标记已清除", LogResult.Success);
    }

    /// <summary>恢复失败后的安全策略：确保内屏可用。</summary>
    private async Task EnsureInternalSafeAsync()
    {
        var t = QueryTopology();
        if (t.QuerySucceeded && t.InternalActive) return;
        _log.Log(LogCategory.Recovery, "安全策略：内屏未处于活动状态，切回内屏", LogResult.Warning);
        await SwitchToInternalCoreAsync(CancellationToken.None);
    }

    // ───────────────────────── 用户操作 ─────────────────────────

    public async Task<OperationResult> ManualSwitchToInternalAsync()
    {
        CancelFlow();
        await _gate.WaitAsync();
        try
        {
            _log.Log(LogCategory.Switch, "用户手动请求切回笔记本内屏");
            var r = await SwitchToInternalCoreAsync(CancellationToken.None);
            if (_marker != null && _flowStatus == RecoveryStatus.RecoveryFailed)
            {
                // 用户手动处理后重新进入等待，由超时机制再次限制
                _waitingOnAcSince = Power == PowerSource.Ac ? _clock.Now : null;
                _externalStableSince = null;
                SetStatus(RecoveryStatus.WaitingForExternal);
            }
            return r;
        }
        catch (Exception ex) { _log.Log(LogCategory.Error, ex.ToString(), LogResult.Failure); return new(false, ex.Message); }
        finally { ReleaseGate(); }
    }

    public async Task<OperationResult> ManualRestorePreferredAsync()
    {
        CancelFlow();
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
        finally { ReleaseGate(); }
    }

    /// <param name="duration">暂停时长；null = 直到用户手动恢复（仍只在本次运行有效）。</param>
    public async Task SetPausedAsync(bool paused, TimeSpan? duration = null)
    {
        if (paused) CancelFlow();
        await _gate.WaitAsync();
        try
        {
            if (paused)
            {
                var until = duration is { } d && d > TimeSpan.Zero ? _clock.Now + d : (DateTimeOffset?)null;
                if (_paused && _pausedUntil == until) return;
                _paused = true; _pausedUntil = until;
                Event(LogCategory.Recovery, until is { } u ? $"用户暂停了自动保护，将于 {u:HH:mm} 自动恢复（恢复标记保留）" : "用户暂停了自动保护（恢复标记保留）", LogResult.Info);
            }
            else if (_paused) ResumeCore("用户恢复了自动保护");
        }
        finally { ReleaseGate(); }
    }

    private void ResumeCore(string text)
    {
        _paused = false; _pausedUntil = null;
        _flowStatus = _marker != null ? RecoveryStatus.WaitingForExternal : RecoveryStatus.Normal;
        _waitingOnAcSince = Power == PowerSource.Ac && _marker != null ? _clock.Now : null;
        _externalStableSince = null;
        Event(LogCategory.Recovery, text, LogResult.Info);
    }

    /// <summary>放弃未完成的恢复流程（用户明确操作）。</summary>
    public async Task DiscardRecoveryAsync()
    {
        CancelFlow();
        await _gate.WaitAsync();
        try
        {
            try { _store.Clear(); }
            catch (Exception ex) { _log.Log(LogCategory.Error, $"删除恢复标记文件失败：{ex.Message}（内存中已放弃该流程）", LogResult.Failure); }
            _marker = null; _externalStableSince = null; _waitingOnAcSince = null;
            _flowStatus = RecoveryStatus.Normal;
            Event(LogCategory.Recovery, "用户放弃了未完成的恢复流程，恢复标记已清除", LogResult.Info);
        }
        finally { ReleaseGate(); }
    }

    public void RefreshTopology()
    {
        if (!_gate.Wait(0)) return;
        try { QueryTopology(); } finally { ReleaseGate(); }
    }

    /// <summary>等待外接期间：外接路径是否已连续可用至少稳定时间（否则关机前不切外接，避免显示器其实没电）。</summary>
    private bool ExternalStableEnough() =>
        _flowStatus != RecoveryStatus.WaitingForExternal ||
        (_externalStableSince is { } since && _clock.Now - since >= TimeSpan.FromSeconds(_settings().StabilitySeconds));

    // ───────────────────────── 关机前恢复 ─────────────────────────

    /// <summary>
    /// 关机 / 重启 / 注销前尽力切回首选显示模式，使下次开机直接进入首选拓扑。
    /// 整个过程受 budget 限制（获取锁、切屏、校验都计入），超时即放弃，绝不阻止关机。
    /// 不修改恢复标记：下次启动若已是首选模式，恢复流程会直接确认并清除标记。
    /// </summary>
    public async Task<ShutdownRestoreOutcome> PrepareForShutdownAsync(TimeSpan budget)
    {
        var deadline = _clock.Now + budget;
        using var cts = new CancellationTokenSource(budget);
        CancelFlow(); // 打断可能正在进行的重试等待
        var lockWait = TimeSpan.FromMilliseconds(Math.Min(800, budget.TotalMilliseconds / 3));
        if (!await _gate.WaitAsync(lockWait))
        {
            var busy = ShutdownDecision.Skip(_snapshot.Marker?.PreferredModeSnapshot ?? _settings().PreferredMode, "正在执行其他切屏操作，跳过关机前恢复");
            _log.Log(LogCategory.Switch, $"关机前恢复：{busy.Reason}", LogResult.Warning);
            return new(false, false, busy, busy.Reason);
        }
        try
        {
            var topo = QueryTopology();
            var decision = ShutdownRestorePolicy.Decide(_settings(), Status == RecoveryStatus.Paused || _paused ? RecoveryStatus.Paused : _flowStatus,
                _paused, Power, _marker, topo, ExternalStableEnough());
            if (!decision.Apply)
            {
                _log.Log(LogCategory.Switch, $"关机前恢复：跳过（{decision.Reason}）", LogResult.Info);
                return new(false, false, decision, decision.Reason);
            }
            _log.Log(LogCategory.Switch, $"关机前恢复：{decision.Reason}", LogResult.Info);
            try
            {
                var launch = await _switcher.SwitchAsync(decision.Target, cts.Token);
                if (!launch.Started)
                {
                    var m = $"关机前恢复失败：{launch.Error}";
                    _log.Log(LogCategory.Switch, m, LogResult.Failure);
                    return new(true, false, decision, m);
                }
                // 校验：在剩余时间内多次短间隔查询
                while (true)
                {
                    var t = QueryTopology();
                    if (t.QuerySucceeded && t.Mode == decision.Target)
                    {
                        var ok = $"关机前恢复成功：已确认为{DisplayModeText.Of(decision.Target)}";
                        _log.Log(LogCategory.Switch, ok, LogResult.Success);
                        return new(true, true, decision, ok);
                    }
                    if (_clock.Now + TimeSpan.FromMilliseconds(300) >= deadline) break;
                    await _delay.Delay(TimeSpan.FromMilliseconds(300), cts.Token);
                }
            }
            catch (OperationCanceledException) { }
            var fail = $"关机前恢复：{budget.TotalSeconds:0.#} 秒内未能确认切换为{DisplayModeText.Of(decision.Target)}（已放弃，不影响关机）";
            _log.Log(LogCategory.Switch, fail, LogResult.Warning);
            return new(true, false, decision, fail);
        }
        catch (Exception ex)
        {
            var d = ShutdownDecision.Skip(_settings().PreferredMode, ex.Message);
            _log.Log(LogCategory.Error, $"关机前恢复异常：{ex.Message}", LogResult.Failure);
            return new(false, false, d, ex.Message);
        }
        finally { ReleaseGate(); }
    }
}
