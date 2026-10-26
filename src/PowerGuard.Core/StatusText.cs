namespace PowerGuard.Core;

/// <summary>控制台、托盘菜单、托盘提示统一使用的状态用语，避免同一状态在不同地方叫法不同。</summary>
public static class StatusText
{
    /// <summary>一句话状态，例如“断电保护中（使用内屏）”。</summary>
    public static string Headline(CoordinatorSnapshot s) => s.Status switch
    {
        RecoveryStatus.Paused => s.PausedUntil is { } u ? $"已暂停（{u:HH:mm} 自动恢复）" : "已暂停",
        RecoveryStatus.WaitingForExternal when s.Power != PowerSource.Ac => "断电保护中（使用内屏）",
        RecoveryStatus.WaitingForExternal => "已来电，等待外接显示器",
        RecoveryStatus.RecoveryFailed when s.Power != PowerSource.Ac => "恢复失败，断电中（使用内屏）",
        RecoveryStatus.RecoveryFailed => "恢复失败（使用内屏）",
        RecoveryStatus.Normal => "正常运行",
        _ => DisplayModeText.Of(s.Status) + "…",
    };

    /// <summary>补充说明（可为空），例如盖子合上、已纠正次数。</summary>
    public static string? Detail(CoordinatorSnapshot s)
    {
        var parts = new List<string>();
        if (s.Power != PowerSource.Ac && s.Status is RecoveryStatus.WaitingForExternal or RecoveryStatus.RecoveryFailed)
        {
            if (s.LidOpen == false) parts.Add("盖子已合上，开盖后自动切回内屏");
            if (s.GuardAttempts > 0) parts.Add($"已纠正 {s.GuardAttempts} 次");
        }
        return parts.Count == 0 ? null : string.Join("；", parts);
    }
}
