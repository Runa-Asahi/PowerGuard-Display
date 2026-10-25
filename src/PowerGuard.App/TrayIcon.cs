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
    private readonly Forms.ToolStripMenuItem _status, _pause, _pause1h, _internal, _restore;
    private RecoveryStatus? _lastStatus;
    private bool _busy;
    private static readonly Dictionary<string, Icon> Icons = new();

    /// <summary>从嵌入资源加载自定义图标（失败时回退系统图标）。</summary>
    private static Icon LoadIcon(string name, Icon fallback)
    {
        lock (Icons)
        {
            if (Icons.TryGetValue(name, out var cached)) return cached;
            try
            {
                using var st = typeof(TrayIcon).Assembly.GetManifestResourceStream($"PowerGuard.App.Assets.tray-{name}.ico");
                if (st is null) return fallback;
                // 托盘使用小尺寸；按系统托盘图标大小选取（Per-Monitor DPI 下由 Windows 缩放）
                var icon = new Icon(st, Forms.SystemInformation.SmallIconSize);
                Icons[name] = icon;
                return icon;
            }
            catch { return fallback; }
        }
    }

    public TrayIcon(App app)
    {
        _app = app;
        var menu = new Forms.ContextMenuStrip();
        _status = new Forms.ToolStripMenuItem("状态：初始化中") { Enabled = false };
        _pause = new Forms.ToolStripMenuItem("暂停自动保护", null, async (_, _) => await TogglePause());
        _pause1h = new Forms.ToolStripMenuItem("暂停 1 小时", null, async (_, _) => await _app.Coordinator.SetPausedAsync(true, TimeSpan.FromHours(1)));
        _internal = new Forms.ToolStripMenuItem("切回笔记本内屏", null, async (_, _) => await RunBusy(() => Actions.SwitchInternal(null)));
        _restore = new Forms.ToolStripMenuItem("尝试恢复外接显示器", null, async (_, _) => await RunBusy(() => Actions.RestorePreferred(null)));
        menu.Items.Add(new Forms.ToolStripMenuItem("显示主窗口", null, (_, _) => _app.ShowMainWindow()) { Font = new Font(Forms.Control.DefaultFont, System.Drawing.FontStyle.Bold) });
        menu.Items.Add(_status);
        menu.Items.Add(new Forms.ToolStripMenuItem("查看保护状态", null, (_, _) => _app.ShowPage(0)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(_pause);
        menu.Items.Add(_pause1h);
        menu.Items.Add(_internal);
        menu.Items.Add(_restore);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("打开设置", null, (_, _) => _app.ShowPage(1)));
        menu.Items.Add(new Forms.ToolStripMenuItem("查看日志", null, (_, _) => _app.ShowPage(2)));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(new Forms.ToolStripMenuItem("退出程序", null, (_, _) => ConfirmExit()));
        _icon = new Forms.NotifyIcon { ContextMenuStrip = menu, Visible = true, Icon = LoadIcon("ok", SystemIcons.Shield), Text = "断电护屏" };
        _icon.DoubleClick += (_, _) => _app.ShowMainWindow();
    }

    /// <summary>切屏操作执行期间置灰菜单项，防止重复点击。</summary>
    private async Task RunBusy(Func<Task<OperationResult?>> op)
    {
        if (_busy) return;
        _busy = true; Update();
        try { await op(); }
        finally { _busy = false; Update(); }
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
        _pause1h.Visible = !snap.Paused;
        _internal.Enabled = _restore.Enabled = !_busy;
        _icon.Icon = s switch
        {
            RecoveryStatus.RecoveryFailed => LoadIcon("bad", SystemIcons.Warning),
            RecoveryStatus.Paused => LoadIcon("paused", SystemIcons.Information),
            _ when !_app.MonitorRunning => LoadIcon("bad", SystemIcons.Error),
            RecoveryStatus.Normal => LoadIcon("ok", SystemIcons.Shield),
            _ => LoadIcon("warn", SystemIcons.Shield),
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
