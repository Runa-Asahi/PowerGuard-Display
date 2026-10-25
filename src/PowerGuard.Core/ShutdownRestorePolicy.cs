namespace PowerGuard.Core;

/// <summary>关机前切屏决策的结果。Apply=false 时 Reason 说明跳过原因（写入日志）。</summary>
public sealed record ShutdownDecision(bool Apply, DisplayMode Target, string Reason)
{
    public static ShutdownDecision Skip(DisplayMode target, string reason) => new(false, target, reason);
}

/// <summary>
/// 关机 / 重启 / 注销前“尽力切回首选显示模式”的决策（纯函数，便于测试）。
/// 目的：下次开机时 Windows 直接沿用首选拓扑（例如“仅第二屏幕”）。
/// 原则：只在“切过去不会黑屏”时才动手；任何不确定都跳过，绝不阻止关机。
/// </summary>
public static class ShutdownRestorePolicy
{
    public static ShutdownDecision Decide(AppSettings settings, RecoveryStatus status, bool paused,
        PowerSource power, RecoveryState? marker, DisplayTopology topology)
    {
        var target = marker?.PreferredModeSnapshot is { } m && m != DisplayMode.Unknown ? m : settings.PreferredMode;

        if (!settings.RestoreOnShutdown) return ShutdownDecision.Skip(target, "设置中已关闭“关机前恢复首选显示模式”");
        if (paused || status == RecoveryStatus.Paused) return ShutdownDecision.Skip(target, "自动保护已暂停，不自动切屏");
        if (target == DisplayMode.Unknown) return ShutdownDecision.Skip(target, "首选模式未知");
        if (!topology.QuerySucceeded) return ShutdownDecision.Skip(target, $"无法读取显示拓扑（{topology.Error}），为避免黑屏不切换");
        if (topology.Mode == target) return ShutdownDecision.Skip(target, $"当前已是{DisplayModeText.Of(target)}，无需切换");
        if (power != PowerSource.Ac)
            return ShutdownDecision.Skip(target, $"当前电源为{DisplayModeText.Of(power)}（可能仍在断电），外接显示器可能没电，保持当前模式");
        if (status == RecoveryStatus.RecoveryFailed)
            return ShutdownDecision.Skip(target, "本次恢复已失败，外接显示器状态不可靠，保持当前模式");
        if (status is RecoveryStatus.PowerLossDetected or RecoveryStatus.SwitchingToInternal or RecoveryStatus.RestoringPreferredMode)
            return ShutdownDecision.Skip(target, $"正在执行“{DisplayModeText.Of(status)}”，不打断");
        if (DisplaySwitchArgs.RequiresExternal(target) && topology.ExternalAvailableCount == 0)
            return ShutdownDecision.Skip(target, "Windows 未检测到外接显示路径，切换会导致黑屏，跳过");

        return new(true, target, $"关机前切换为{DisplayModeText.Of(target)}（当前 {DisplayModeText.Of(topology.Mode)}，外接可用 {topology.ExternalAvailableCount}）");
    }
}
