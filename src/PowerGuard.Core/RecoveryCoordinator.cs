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
    DateTimeOffset? WaitingOnAcSince);

/// <summary>
/// 断电保护与自动恢复状态机。所有改变状态的流程都在 _gate 内串行执行（防重入）。
/// 电源/显示事件由平台层推送进来；Tick 由低频定时器和 WM_DISPLAYCHANGE 驱动。
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
    private DateTimeOffset? _waitingOnAcSince;
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
        _lastEventAt, _lastEventText, _lastEventResult, _externalStableSince, _waitingOnAcSince);

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

    // ───────────────────────── 初始化 ─────────────────────────

    public async Task InitializeAsync(PowerSource initialPower)
    {
        await _gate.WaitAsync();
        try
        {
            lock (_powerLock) _power = initialPower;
            var topo = QueryTopology();
            _log.Log(LogCategory.App, $"初始化：电源={DisplayModeText.Of(initialPower)}；{topo}");

            var r = _store.Load();
            if (r.Status == LoadStatus.Corrupt)
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
                _waitingOnAcSince = null; _externalStableSince = null;
                if (_paused) { _log.Log(LogCategory.Recovery, "保护已暂停，忽略断电事件", LogResult.Warning); return; }
                if (!_settings().AutoSwitchOnPowerLoss) { _log.Log(LogCategory.Recovery, "设置中已禁用断电自动切屏，忽略断电事件", LogResult.Warning); return; }
                if (_flowStatus is RecoveryStatus.Normal or RecoveryStatus.RecoveryFailed)
                    await PowerLossFlowCoreAsync();
                else
                    _log.Log(LogCategory.Recovery, "已在恢复流程中，保持内屏优先，不重复切屏");
            }
            finally { _gate.Release(); }
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
            finally { _gate.Release(); }
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
        await SwitchToInternalCoreAsync(CancellationToken.None);
        _waitingOnAcSince = Power == PowerSource.Ac ? _clock.Now : null;
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
            var topo = QueryTopology();
            if (_paused || _flowStatus != RecoveryStatus.WaitingForExternal || _marker is null) return;
            if (Power != PowerSource.Ac) return; // 断电期间维持内屏，不因显示器波动切屏

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
        finally { _gate.Release(); }
    }

    private async Task<OperationResult> RestoreCoreAsync(DisplayMode preferred, bool automatic)
    {
        _flowCts?.Dispose();
        _flowCts = new CancellationTokenSource();
        var ct = _flowCts.Token;
        var s = _settings();
        var attempts = automatic ? 1 + s.MaxRetries : 1;
        SetStatus(RecoveryStatus.RestoringPreferredMode);
        Event(LogCategory.Recovery, $"{(automatic ? "自动" : "手动")}恢复首选模式：{DisplayModeText.Of(preferred)}（最多 {attempts} 次）", LogResult.Info);
        string err = "";
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
                    if (t.QuerySucceeded && t.Mode == preferred)
                    {
                        // 只有确认系统状态符合预期才清除标记
                        try { _store.Clear(); }
                        catch (Exception ex) { _log.Log(LogCategory.Error, $"清除恢复标记失败：{ex.Message}", LogResult.Failure); }
                        _marker = null; _externalStableSince = null; _waitingOnAcSince = null;
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
        _flowCts?.Cancel();
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
            if (!paused)
            {
                _flowStatus = _marker != null ? RecoveryStatus.WaitingForExternal : RecoveryStatus.Normal;
                _waitingOnAcSince = Power == PowerSource.Ac && _marker != null ? _clock.Now : null;
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
            _store.Clear();
            _marker = null; _externalStableSince = null; _waitingOnAcSince = null;
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
