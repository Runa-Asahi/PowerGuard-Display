namespace PowerGuard.Core;

/// <summary>
/// 用户配置（settings.json）。与恢复状态分开保存。
/// record：值相等语义 + <c>with</c> 拷贝；运行时把整个对象当作不可变快照整体替换，不在原对象上修改。
/// </summary>
public sealed record AppSettings
{
    /// <summary>当前配置结构版本。v1 → v2：新增 RestoreOnShutdown、SwitchMethod；v2 → v3：新增 VerifyRestoreWithDdc、RelaunchAfterCrash。</summary>
    public const int CurrentVersion = 3;

    public int Version { get; set; } = CurrentVersion;
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

    /// <summary>DisplaySwitch.exe 参数风格（Auto/Slash/Numeric），仅配置文件可改。</summary>
    public DisplaySwitchArgStyle DisplaySwitchArgStyle { get; set; } = DisplaySwitchArgStyle.Auto;

    /// <summary>切屏方式：Auto = 先用 DisplaySwitch.exe（v1.0.0 实机验证过），失败或校验未通过再用原生 SetDisplayConfig。仅配置文件可改。</summary>
    public SwitchMethod SwitchMethod { get; set; } = SwitchMethod.Auto;

    /// <summary>关机 / 重启 / 注销前尽力切回首选显示模式（仅在安全时，见 ShutdownRestorePolicy）。</summary>
    public bool RestoreOnShutdown { get; set; } = true;

    /// <summary>
    /// 实验功能（默认关闭）：恢复外接后用 DDC/CI 确认显示器真的在应答；若连续无应答（显示器其实没电），切回内屏继续等待。
    /// 只有在之前正常使用时读到过该显示器的 DDC 应答（确认支持）才会生效，每个恢复流程最多回退一次。
    /// </summary>
    public bool VerifyRestoreWithDdc { get; set; } = false;

    /// <summary>程序意外崩溃时自动重新启动（每小时最多 3 次），保证断电保护不中断。</summary>
    public bool RelaunchAfterCrash { get; set; } = true;

    public AppSettings Copy() => this with { };

    /// <summary>把越界值拉回安全范围，返回是否做了修正。</summary>
    public bool Normalize()
    {
        var before = Copy();
        if (Version < CurrentVersion) Version = CurrentVersion; // v1 缺少的新字段已由默认值补齐
        if (!Enum.IsDefined(SwitchMethod)) SwitchMethod = SwitchMethod.Auto;
        if (!Enum.IsDefined(DisplaySwitchArgStyle)) DisplaySwitchArgStyle = DisplaySwitchArgStyle.Auto;
        if (PreferredMode == DisplayMode.Unknown || !Enum.IsDefined(PreferredMode)) PreferredMode = DisplayMode.External;
        StabilitySeconds = Math.Clamp(StabilitySeconds, 3, 600);
        RecoveryTimeoutMinutes = Math.Clamp(RecoveryTimeoutMinutes, 1, 24 * 60);
        RetryIntervalSeconds = Math.Clamp(RetryIntervalSeconds, 5, 3600);
        MaxRetries = Math.Clamp(MaxRetries, 0, 10);
        VerifyDelaySeconds = Math.Clamp(VerifyDelaySeconds, 1, 30);
        PowerLossConfirmSeconds = Math.Clamp(PowerLossConfirmSeconds, 0, 30);
        return before != this;
    }
}

/// <summary>未完成的断电恢复流程标记（recovery-state.json）。文件存在即表示有未完成流程。</summary>
public sealed class RecoveryState
{
    public RecoveryState Copy() => (RecoveryState)MemberwiseClone();

    public int Version { get; set; } = 1;
    public bool Pending { get; set; } = true;
    public DisplayMode PreferredModeSnapshot { get; set; }
    public DisplayMode ModeBeforePowerLoss { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public int AttemptCount { get; set; }
    public string? LastError { get; set; }
}
