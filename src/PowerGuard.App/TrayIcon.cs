using System.Drawing;
using System.Windows;
using PowerGuard.Core;
using Forms = System.Windows.Forms;

namespace PowerGuard.App;

/// <summary>系统托盘图标（WinForms NotifyIcon）。</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly App _app;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _status, _pause;
    private RecoveryStatus? _lastStatus;

    public TrayIcon(App app)
    {
        _app = app;
        var menu = new Forms.ContextMenuStrip();
        _status = new Forms.ToolStripMenuItem("状态：初始化中") { Enabled = false };
        _pause = new Forms.ToolStripMenuItem("暂停自动保护", null, async (_, _) => await TogglePause());
        menu.Items.Add(new Forms.ToolStripMenuItem("显示主窗口", null, (_, _) => _app.ShowMainWindow()) { Font = new Font(Forms.Control.DefaultFont, System.Drawing.FontStyle.Bold) });
        menu.Items.Add(_status);
        menu.Items.Add(new Forms.ToolStripMenuItem("查看保护状态", null, (_, _) => _app.ShowPage(0)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_pause);
        menu.Items.Add(new Forms.ToolStripMenuItem("切回笔记本内屏", null, async (_, _) => await Actions.SwitchInternal(null)));
        menu.Items.Add(new Forms.ToolStripMenuItem("尝试恢复外接显示器", null, async (_, _) => await Actions.RestorePreferred(null)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("打开设置", null, (_, _) => _app.ShowPage(1)));
        menu.Items.Add(new Forms.ToolStripMenuItem("查看日志", null, (_, _) => _app.ShowPage(2)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("退出程序", null, (_, _) => ConfirmExit()));
        _icon = new Forms.NotifyIcon { ContextMenuStrip = menu, Visible = true, Icon = SystemIcons.Shield, Text = "断电护屏" };
        _icon.DoubleClick += (_, _) => _app.ShowMainWindow();
    }

    private async Task TogglePause() => await _app.Coordinator.SetPausedAsync(!_app.Coordinator.Paused);

    private void ConfirmExit()
    {
        var r = MessageBox.Show("退出后将不再监听断电，也不会自动恢复显示器。\n未完成的恢复标记会保留，下次启动时继续。\n\n确定退出吗？",
            "退出断电护屏", MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.OK) _app.ExitApp();
    }

    public void Update()
    {
        var snap = _app.Coordinator.Snapshot();
        var s = snap.Status;
        _status.Text = $"状态：{DisplayModeText.Of(s)} · {DisplayModeText.Of(snap.Power)}";
        _pause.Text = snap.Paused ? "恢复自动保护" : "暂停自动保护";
        _icon.Icon = s switch
        {
            RecoveryStatus.RecoveryFailed => SystemIcons.Warning,
            RecoveryStatus.Paused => SystemIcons.Information,
            _ when !_app.MonitorRunning => SystemIcons.Error,
            _ => SystemIcons.Shield,
        };
        var text = $"断电护屏 · {DisplayModeText.Of(s)}\n电源：{DisplayModeText.Of(snap.Power)}  显示：{DisplayModeText.Of(snap.Topology.Mode)}";
        _icon.Text = text.Length > 127 ? text[..127] : text;

        if (_lastStatus != null && _lastStatus != s)
        {
            if (s == RecoveryStatus.WaitingForExternal && _lastStatus == RecoveryStatus.SwitchingToInternal)
                Notify("检测到断电", "已切回笔记本内屏，来电后会在外接显示器稳定可用时尝试恢复。");
            else if (s == RecoveryStatus.Normal && _lastStatus == RecoveryStatus.RestoringPreferredMode)
                Notify("显示模式已恢复", "Windows 已启用首选显示模式。");
            else if (s == RecoveryStatus.RecoveryFailed)
                Notify("恢复失败", snap.Marker?.LastError ?? "请打开控制台查看详情。", warn: true);
        }
        _lastStatus = s;
    }

    public void Notify(string title, string text, bool warn = false, bool force = false)
    {
        if (!force && !_app.Settings.ShowNotifications) return;
        _icon.ShowBalloonTip(5000, title, text, warn ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
