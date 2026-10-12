using System.Windows;
using PowerGuard.Core;

namespace PowerGuard.App;

/// <summary>可能导致短暂黑屏的操作，统一先确认、后执行，结果给出下一步。</summary>
public static class Actions
{
    public static async Task<OperationResult?> SwitchInternal(Window? owner)
    {
        if (!Confirm(owner, "切回笔记本内屏",
            "将调用 Windows 自带的 DisplaySwitch.exe 切换到“仅电脑屏幕”。\n切换时所有屏幕可能会黑屏 1–3 秒，外接显示器将停止显示。\n\n继续吗？"))
            return null;
        return Show(owner, await App.Current.Coordinator.ManualSwitchToInternalAsync());
    }

    public static async Task<OperationResult?> RestorePreferred(Window? owner)
    {
        var app = App.Current;
        var mode = app.Coordinator.Marker?.PreferredModeSnapshot ?? app.Settings.PreferredMode;
        if (!Confirm(owner, "恢复首选显示模式",
            $"将切换到首选显示模式“{DisplayModeText.Of(mode)}”。\n切换时屏幕可能会黑屏数秒。\n若显示器实际未亮起而选择了“仅第二屏幕”，内屏会熄灭：此时可按 Win+P 选择“仅电脑屏幕”，\n或使用托盘菜单“切回笔记本内屏”。\n\n继续吗？"))
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
