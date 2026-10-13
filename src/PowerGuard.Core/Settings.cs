namespace PowerGuard.Core;

/// <summary>用户配置（settings.json）。与恢复状态分开保存。</summary>
public sealed class AppSettings
{
    public int Version { get; set; } = 2;
    public bool AutoSwitchOnPowerLoss { get; set; } = true;
    public bool AutoRestoreOnPowerReturn { get; set; } = true;
    /// <summary>用户登录后自启动的偏好。默认关闭，必须由用户在设置页开启。</summary>
    public bool StartWithWindows { get; set; } = false;
    public DisplayMode PreferredMode { get; set; } = DisplayMode.External;
    public int StabilitySeconds { get; set; } = 15;
    public int RecoveryTimeoutMinutes { get; set; } = 30;
    public int RetryIntervalSeconds { get; set; } = 20;
    public int MaxRetries { get; set; } = 3;
    public bool ShowNotifications { get; set; } = true;
    public bool RetainLogs { get; set; } = true;
    /// <summary>调用 DisplaySwitch 后等待多久再查询拓扑校验（秒，仅配置文件可改）。</summary>
    public int VerifyDelaySeconds { get; set; } = 4;
    /// <summary>市电转电池后确认多久仍为电池才判定断电（秒，过滤瞬时抖动）。</summary>
    public int PowerLossConfirmSeconds { get; set; } = 2;

    /// <summary>触发方式（旧配置文件没有该字段时为 Recommended）。</summary>
    public TriggerMode TriggerMode { get; set; } = TriggerMode.Recommended;
    /// <summary>外接显示路径消失后需持续多少秒才判定为显示器断电（过滤抖动）。</summary>
    public int ExternalLostConfirmSeconds { get; set; } = 8;
    /// <summary>
    /// 关机/注销时尽力尝试一次切到首选模式，让 Windows 下次开机沿用。默认关闭。
    /// 不会清除恢复标记，也不会阻止关机。
    /// </summary>
    public bool TryPreferredModeOnShutdown { get; set; } = false;

    /// <summary>DisplaySwitch.exe 参数风格（Auto/Slash/Numeric），仅配置文件可改。</summary>
    public DisplaySwitchArgStyle DisplaySwitchArgStyle { get; set; } = DisplaySwitchArgStyle.Auto;

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    /// <summary>把越界值拉回安全范围，返回是否做了修正。</summary>
    public bool Normalize()
    {
        var before = System.Text.Json.JsonSerializer.Serialize(this);
        if (PreferredMode == DisplayMode.Unknown || !Enum.IsDefined(PreferredMode)) PreferredMode = DisplayMode.External;
        StabilitySeconds = Math.Clamp(StabilitySeconds, 3, 600);
        RecoveryTimeoutMinutes = Math.Clamp(RecoveryTimeoutMinutes, 1, 24 * 60);
        RetryIntervalSeconds = Math.Clamp(RetryIntervalSeconds, 5, 3600);
        MaxRetries = Math.Clamp(MaxRetries, 0, 10);
        VerifyDelaySeconds = Math.Clamp(VerifyDelaySeconds, 1, 30);
        PowerLossConfirmSeconds = Math.Clamp(PowerLossConfirmSeconds, 0, 30);
        ExternalLostConfirmSeconds = Math.Clamp(ExternalLostConfirmSeconds, 2, 120);
        if (!Enum.IsDefined(TriggerMode)) TriggerMode = TriggerMode.Recommended;
        return before != System.Text.Json.JsonSerializer.Serialize(this);
    }
}

/// <summary>未完成的断电恢复流程标记（recovery-state.json）。文件存在即表示有未完成流程。</summary>
public sealed class RecoveryState
{
    public int Version { get; set; } = 2;
    public bool Pending { get; set; } = true;
    public DisplayMode PreferredModeSnapshot { get; set; }
    public DisplayMode ModeBeforePowerLoss { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
    /// <summary>触发原因（v2 新增，旧文件缺省为 PowerLoss）。</summary>
    public TriggerReason TriggerReason { get; set; } = TriggerReason.PowerLoss;
    /// <summary>最近一次关机前尝试的时间与结果（v2 新增，仅供诊断）。</summary>
    public DateTimeOffset? LastShutdownAttemptAt { get; set; }
    public string? LastShutdownAttemptResult { get; set; }
}
