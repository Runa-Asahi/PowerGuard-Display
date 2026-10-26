using System.Windows;
using PowerGuard.Core;

namespace PowerGuard.App;

/// <summary>
/// 手动切屏操作，结果给出下一步。
/// 切回内屏是“安全方向”（断电时用户最需要它，可能还看不清屏幕），不再弹确认框；
/// 恢复外接可能导致内屏熄灭，需要确认，电池供电时额外警告。
/// </summary>
public static class Actions
{
    public static async Task<OperationResult?> SwitchInternal(Window? owner)
    {
        return Show(owner, await App.Current.Coordinator.ManualSwitchToInternalAsync());
    }

    public static async Task<OperationResult?> RestorePreferred(Window? owner)
    {
        var app = App.Current;
        var mode = app.Coordinator.Marker?.PreferredModeSnapshot ?? app.Settings.PreferredMode;
        var battery = app.Coordinator.Power != PowerSource.Ac
            ? "⚠ 当前没有接市电（可能仍在断电），外接显示器很可能没电，切过去内屏会黑屏。\n\n" : "";
        if (!Confirm(owner, "恢复首选显示模式",
            battery + $"将切换到首选显示模式“{DisplayModeText.Of(mode)}”。\n切换时屏幕可能会黑屏数秒。\n若显示器实际未亮起而选择了“仅第二屏幕”，内屏会熄灭：此时可按 Win+P 选择“仅电脑屏幕”，\n或使用托盘菜单“切回笔记本内屏”。\n\n继续吗？"))
            return null;
        return Show(owner, await app.Coordinator.ManualRestorePreferredAsync());
    }

    private static bool Confirm(Window? owner, string title, string text) =>
        (owner is null
            ? MessageBox.Show(text, title, MessageBoxButton.OKCancel, MessageBoxImage.Information, MessageBoxResult.Cancel, MessageBoxOptions.DefaultDesktopOnly)
            : MessageBox.Show(owner, text, title, MessageBoxButton.OKCancel, MessageBoxImage.Information)) == MessageBoxResult.OK;

    private static OperationResult Show(Window? owner, OperationResult r)
    {
        if (owner is null && !r.Success)
            MessageBox.Show(r.Message + (r.NextStep is null ? "" : "\n\n下一步：" + r.NextStep), "操作未成功",
                MessageBoxButton.OK, MessageBoxImage.Warning, MessageBoxResult.OK, MessageBoxOptions.DefaultDesktopOnly);
        return r;
    }
}
