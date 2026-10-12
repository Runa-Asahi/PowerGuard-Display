using System.Text.Json.Serialization;

namespace PowerGuard.Core;

/// <summary>电源来源。Unknown 永远不能被当作断电。</summary>
public enum PowerSource { Unknown, Ac, Battery }

/// <summary>显示模式，对应 DisplaySwitch.exe 的 /internal /external /extend /clone。</summary>
[JsonConverter(typeof(JsonStringEnumConverter<DisplayMode>))]
public enum DisplayMode { Unknown, Internal, External, Extend, Clone }

/// <summary>恢复状态机的状态。</summary>
public enum RecoveryStatus
{
    Normal,
    PowerLossDetected,
    SwitchingToInternal,
    WaitingForExternal,
    RestoringPreferredMode,
    RecoveryFailed,
    Paused,
}

/// <summary>
/// 一次真实查询得到的 Windows 显示拓扑。
/// 注意：ExternalAvailableCount 表示“Windows 认为该目标可用（targetAvailable）”，
/// 不代表显示器真的亮着、有画面。
/// </summary>
public sealed record DisplayTopology(
    bool QuerySucceeded,
    DisplayMode Mode,
    bool InternalPresent,
    bool InternalActive,
    int ExternalAvailableCount,
    int ExternalActiveCount,
    string Signature,
    string? Error = null)
{
    public static DisplayTopology Failed(string error) =>
        new(false, DisplayMode.Unknown, false, false, 0, 0, "query-failed", error);

    public override string ToString() => QuerySucceeded
        ? $"模式={DisplayModeText.Of(Mode)}，内屏{(InternalActive ? "活动" : InternalPresent ? "存在但未活动" : "未检测到")}，外接可用={ExternalAvailableCount}，外接活动={ExternalActiveCount}"
        : $"拓扑查询失败：{Error}";
}

public sealed record SwitchLaunchResult(bool Started, int? ExitCode, string? Error)
{
    public static SwitchLaunchResult Ok(int? exitCode) => new(true, exitCode, null);
    public static SwitchLaunchResult Fail(string error) => new(false, null, error);
}

public sealed record OperationResult(bool Success, string Message, string? NextStep = null);

public static class DisplayModeText
{
    public static string Of(DisplayMode m) => m switch
    {
        DisplayMode.Internal => "仅电脑屏幕",
        DisplayMode.External => "仅第二屏幕",
        DisplayMode.Extend => "扩展",
        DisplayMode.Clone => "复制",
        _ => "未知",
    };

    public static string Of(PowerSource p) => p switch
    {
        PowerSource.Ac => "市电",
        PowerSource.Battery => "电池",
        _ => "未知",
    };

    public static string Of(RecoveryStatus s) => s switch
    {
        RecoveryStatus.Normal => "正常运行",
        RecoveryStatus.PowerLossDetected => "检测到断电",
        RecoveryStatus.SwitchingToInternal => "正在切换到内屏",
        RecoveryStatus.WaitingForExternal => "等待外接显示器",
        RecoveryStatus.RestoringPreferredMode => "正在恢复首选模式",
        RecoveryStatus.RecoveryFailed => "恢复失败",
        RecoveryStatus.Paused => "已暂停",
        _ => s.ToString(),
    };
}

/// <summary>DisplaySwitch.exe 参数映射（第一版唯一的切屏手段）。</summary>
/// <summary>
/// DisplaySwitch.exe 参数风格。Windows 10 使用 /internal /clone /extend /external；
/// 据 Microsoft Q&amp;A 社区反馈，Windows 11 22H2 起内置 DisplaySwitch.exe 改为数字参数 1/2/3/4，
/// 传 /xxx 会只弹出“投影”菜单。Auto = 按系统版本号（build ≥ 22000 视为 Windows 11）选择。
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<DisplaySwitchArgStyle>))]
public enum DisplaySwitchArgStyle { Auto, Slash, Numeric }

public static class DisplaySwitchArgs
{
    public static DisplaySwitchArgStyle Resolve(DisplaySwitchArgStyle style, int osBuild) =>
        style != DisplaySwitchArgStyle.Auto ? style : osBuild >= 22000 ? DisplaySwitchArgStyle.Numeric : DisplaySwitchArgStyle.Slash;

    public static string For(DisplayMode mode, DisplaySwitchArgStyle style) =>
        style == DisplaySwitchArgStyle.Numeric
            ? mode switch
            {
                DisplayMode.Internal => "1",
                DisplayMode.Clone => "2",
                DisplayMode.Extend => "3",
                DisplayMode.External => "4",
                _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "未知模式不能切换"),
            }
            : For(mode);

    public static string For(DisplayMode mode) => mode switch
    {
        DisplayMode.Internal => "/internal",
        DisplayMode.External => "/external",
        DisplayMode.Extend => "/extend",
        DisplayMode.Clone => "/clone",
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "未知模式不能切换"),
    };

    /// <summary>首选模式是否需要外接显示路径。</summary>
    public static bool RequiresExternal(DisplayMode mode) =>
        mode is DisplayMode.External or DisplayMode.Extend or DisplayMode.Clone;
}
