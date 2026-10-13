using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using PowerGuard.Core;

namespace PowerGuard.App;

public partial class MainWindow : Window
{
    private readonly App _app = App.Current;
    private readonly ObservableCollection<LogEntry> _logs = new();
    private readonly ICollectionView _logView;
    private readonly DispatcherTimer _refresh;
    private bool _busy;

    public MainWindow()
    {
        InitializeComponent();
        VersionText.Text = $"v{typeof(App).Assembly.GetName().Version?.ToString(3)}";

        foreach (var e in _app.Log.Recent) _logs.Add(e);
        _logView = CollectionViewSource.GetDefaultView(_logs);
        _logView.Filter = LogFilter;
        _logView.SortDescriptions.Add(new SortDescription(nameof(LogEntry.Time), ListSortDirection.Descending));
        LogGrid.ItemsSource = _logView;
        LogCategoryFilter.ItemsSource = new[] { "全部类型" }.Concat(Enum.GetValues<LogCategory>().Select(c => new LogEntry(default, c, "", default).CategoryText)).ToList();
        LogCategoryFilter.SelectedIndex = 0;
        LogResultFilter.ItemsSource = new[] { "全部结果", "信息", "成功", "警告", "失败" };
        LogResultFilter.SelectedIndex = 0;
        _app.Log.EntryAdded += OnLogEntry;

        _app.Coordinator.StateChanged += OnStateChanged;
        _app.StartupStatusChanged += (_, _) => Refresh();
        LoadSettingsToUi();

        // 计时显示（稳定倒计时等）每秒刷新，只读取内存快照
        _refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refresh.Tick += (_, _) => { if (IsVisible) Refresh(); };
        _refresh.Start();
        SizeChanged += (_, _) => StatusGrid.Columns = ActualWidth < 980 ? 2 : 5;
        Refresh();
    }

    private void OnStateChanged(object? s, EventArgs e) => Dispatcher.BeginInvoke(Refresh);

    private void OnLogEntry(LogEntry e) => Dispatcher.BeginInvoke(() =>
    {
        _logs.Add(e);
        if (_logs.Count > FileLogger.MaxRecent) _logs.RemoveAt(0);
    });

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_app.IsExiting)
        {
            e.Cancel = true; // 关闭只隐藏到托盘，后台保护继续
            Hide();
            return;
        }
        _app.Log.EntryAdded -= OnLogEntry;
        _app.Coordinator.StateChanged -= OnStateChanged;
        _refresh.Stop();
        base.OnClosing(e);
    }

    // ───────── 导航 ─────────
    public void SelectPage(int i) => Nav.SelectedIndex = i;

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (DashboardPage is null) return;
        DashboardPage.Visibility = Nav.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = Nav.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        LogsPage.Visibility = Nav.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        if (Nav.SelectedIndex == 1) LoadSettingsToUi();
    }
    private void GoSettings_Click(object s, RoutedEventArgs e) => SelectPage(1);
    private void GoLogs_Click(object s, RoutedEventArgs e) => SelectPage(2);
    private void OpenLogDir_Click(object s, RoutedEventArgs e)
    {
        try { App.OpenFolder(App.LogDir); }
        catch (Exception ex) { MessageBox.Show(this, $"无法打开日志目录：{ex.Message}\n路径：{App.LogDir}", "错误"); }
    }

    // ───────── 控制台 ─────────
    private Brush B(string key) => (Brush)FindResource(key);

    private void Refresh()
    {
        var snap = _app.Coordinator.Snapshot();
        var t = snap.Topology;
        var st = _app.Settings;

        string protection; Brush pb;
        if (!_app.MonitorRunning) { protection = "未运行"; pb = B("Bad"); }
        else if (snap.Paused) { protection = "已暂停"; pb = B("Warn"); }
        else if (!st.AutoSwitchOnPowerLoss) { protection = "已禁用（设置）"; pb = B("Warn"); }
        else { protection = "已启用"; pb = B("Ok"); }
        ProtectionText.Text = protection; ProtectionText.Foreground = pb;

        PowerText.Text = DisplayModeText.Of(snap.Power);
        PowerText.Foreground = snap.Power == PowerSource.Battery ? B("Warn") : snap.Power == PowerSource.Unknown ? B("TextSecondary") : B("TextPrimary");
        ModeText.Text = t.QuerySucceeded ? DisplayModeText.Of(t.Mode) : "未知（查询失败）";
        InternalText.Text = !t.QuerySucceeded ? "未知" : t.InternalActive ? "活动中" : t.InternalPresent ? "已关闭（未显示）" : "未检测到";
        InternalText.Foreground = t.QuerySucceeded && !t.InternalActive ? B("Warn") : B("TextPrimary");
        ExternalText.Text = !t.QuerySucceeded ? "未知（查询失败）" : $"{t.ExternalActiveCount} 条";
        ExternalAvailText.Text = !t.QuerySucceeded ? "未知（查询失败）" : $"{t.ExternalAvailableCount} 个";
        LostConfirmText.Text = snap.ExternalLostSince is { } lsc ? $"确认中（剩约 {Math.Max(0, st.ExternalLostConfirmSeconds - (int)(DateTimeOffset.Now - lsc).TotalSeconds)} 秒）" : "无";
        LostConfirmText.Foreground = snap.ExternalLostSince is null ? B("TextPrimary") : B("Warn");
        MarkerExistsText.Text = snap.Marker is null ? "无" : "有（未完成）";
        MarkerExistsText.Foreground = snap.Marker is null ? B("TextPrimary") : B("Warn");
        ShutdownTryText.Text = st.TryPreferredModeOnShutdown ? "已开启" : "未开启";
        TriggerModeText.Text = st.TriggerMode == TriggerMode.Recommended ? "推荐" : "兼容";
        TriggerReasonText.Text = snap.LastTriggerReason is { } tr ? $"{DisplayModeText.Of(tr)}" + (snap.LastTriggerAt is { } ta ? $" · {ta:MM-dd HH:mm}" : "") : "本次运行未触发";
        AutoRestoreText.Text = st.AutoRestoreOnPowerReturn ? "已启用" : "已禁用";
        FlowText.Text = DisplayModeText.Of(snap.Status);
        FlowText.Foreground = snap.Status switch { RecoveryStatus.RecoveryFailed => B("Bad"), RecoveryStatus.Normal => B("Ok"), _ => B("Warn") };

        var ss = _app.StartupStatus;
        StartupText.Text = ss is null ? "检查中…" : ss.Error != null && !ss.Exists ? "检查失败" : !ss.Exists ? (st.StartWithWindows ? "未创建（异常）" : "未开启") : !ss.Enabled ? "任务已禁用" : ss.PointsToCurrentExe ? "已开启" : "已开启（指向其他位置）";

        // 横幅
        string title, detail; Brush dot;
        switch (snap.Status)
        {
            case RecoveryStatus.Paused: title = "自动保护已暂停"; detail = "不会响应断电，也不会自动恢复。可随时手动切屏。"; dot = B("Warn"); break;
            case RecoveryStatus.RecoveryFailed: title = "恢复失败，正在使用内屏"; detail = (snap.Marker?.LastError ?? "") + "  可点击“尝试恢复首选显示模式”重试。"; dot = B("Bad"); break;
            case RecoveryStatus.WaitingForExternal:
                title = "断电保护中：等待外接显示器";
                if (snap.Marker?.TriggerReason != TriggerReason.ExternalDisplayLost && snap.Power != PowerSource.Ac) detail = "当前未接市电，保持笔记本内屏显示。来电后会等待外接显示路径稳定再恢复。";
                else if (snap.Marker?.TriggerReason == TriggerReason.ExternalDisplayLost && snap.Power == PowerSource.Battery) detail = "当前为电池供电，保持笔记本内屏显示。";
                else if (!st.AutoRestoreOnPowerReturn) detail = "恢复条件已满足，但自动恢复已禁用，请手动恢复。";
                else if (snap.ExternalStableSince is { } since)
                {
                    var left = st.StabilitySeconds - (int)(DateTimeOffset.Now - since).TotalSeconds;
                    detail = $"Windows 检测到外接显示路径，稳定计时中，约 {Math.Max(0, left)} 秒后尝试恢复（不代表显示器已亮屏）。";
                }
                else detail = snap.Marker?.TriggerReason == TriggerReason.ExternalDisplayLost
                    ? "因外接活动显示路径消失而切回内屏，等待 Windows 检测到外接显示路径可用（受恢复超时限制）。"
                    : "已来电，等待 Windows 检测到外接显示路径可用。";
                dot = B("Warn"); break;
            case RecoveryStatus.Normal:
                title = protection == "已启用" ? "保护已就绪" : "保护未启用";
                detail = st.TriggerMode == TriggerMode.Recommended
                    ? $"市电转电池且外接活动路径消失、或外接活动路径单独消失时切回笔记本内屏；条件恢复后恢复为“{DisplayModeText.Of(st.PreferredMode)}”。"
                    : $"市电转电池或外接活动路径消失时切回笔记本内屏；条件恢复后恢复为“{DisplayModeText.Of(st.PreferredMode)}”。";
                if (snap.ExternalLostSince is { } ls)
                {
                    var left = st.ExternalLostConfirmSeconds - (int)(DateTimeOffset.Now - ls).TotalSeconds;
                    title = "外接活动显示路径消失，确认中";
                    detail = $"若约 {Math.Max(0, left)} 秒内路径没有重新出现，将切回笔记本内屏。";
                }
                dot = protection == "已启用" ? B("Ok") : B("Warn"); break;
            default: title = DisplayModeText.Of(snap.Status) + "…"; detail = "正在执行，请稍候。"; dot = B("Accent"); break;
        }
        BannerTitle.Text = title; BannerDetail.Text = detail; BannerDot.Fill = dot;

        LastEventTime.Text = snap.LastEventAt?.ToString("yyyy-MM-dd HH:mm:ss") ?? "暂无";
        LastEventText.Text = snap.LastEventText ?? "";
        LastEventText.Foreground = snap.LastEventResult switch { LogResult.Failure => B("Bad"), LogResult.Warning => B("Warn"), LogResult.Success => B("Ok"), _ => B("TextPrimary") };
        MarkerText.Text = snap.Marker is { } m
            ? $"未完成的恢复流程（{DisplayModeText.Of(m.TriggerReason)}）：开始于 {m.StartedAt:MM-dd HH:mm:ss}，首选 {DisplayModeText.Of(m.PreferredModeSnapshot)}，已尝试 {m.AttemptCount} 次" + (m.LastAttemptAt is { } la ? $"，最近尝试 {la:HH:mm:ss}" : "") + (m.LastError is null ? "" : $"\n最近错误：{m.LastError}") + (m.LastShutdownAttemptAt is { } sa ? $"\n关机前尝试 {sa:MM-dd HH:mm}：{m.LastShutdownAttemptResult}" : "")
            : "无未完成的恢复流程";
        BtnDiscard.Visibility = snap.Marker != null ? Visibility.Visible : Visibility.Collapsed;
        BtnPause.Content = snap.Paused ? "恢复自动保护" : "暂停自动保护";
        BtnInternal.IsEnabled = BtnRestore.IsEnabled = !_busy;
    }

    private void ShowResult(OperationResult? r)
    {
        if (r is null) return;
        ResultBox.Visibility = Visibility.Visible;
        ResultBox.Background = new SolidColorBrush(r.Success ? Color.FromRgb(0xF1, 0xF8, 0xF1) : Color.FromRgb(0xFD, 0xF0, 0xEF));
        ResultText.Text = r.Message;
        ResultText.Foreground = r.Success ? B("Ok") : B("Bad");
        ResultNext.Text = r.NextStep is null ? "" : "下一步：" + r.NextStep;
    }

    private async Task Run(Func<Task<OperationResult?>> op)
    {
        _busy = true; Refresh();
        try { ShowResult(await op()); }
        finally { _busy = false; Refresh(); }
    }

    private async void BtnInternal_Click(object s, RoutedEventArgs e) => await Run(() => Actions.SwitchInternal(this));
    private async void BtnRestore_Click(object s, RoutedEventArgs e) => await Run(() => Actions.RestorePreferred(this));
    private async void BtnPause_Click(object s, RoutedEventArgs e) => await _app.Coordinator.SetPausedAsync(!_app.Coordinator.Paused);
    private async void BtnDiscard_Click(object s, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "放弃后将清除恢复标记，程序不会再自动恢复外接显示器（不会切换当前显示）。\n确定放弃吗？", "放弃未完成的恢复",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) == MessageBoxResult.OK)
            await _app.Coordinator.DiscardRecoveryAsync();
    }

    // ───────── 设置 ─────────
    private void LoadSettingsToUi()
    {
        var s = _app.Settings;
        SAutoSwitch.IsChecked = s.AutoSwitchOnPowerLoss;
        SAutoRestore.IsChecked = s.AutoRestoreOnPowerReturn;
        SStartup.IsChecked = s.StartWithWindows;
        SNotify.IsChecked = s.ShowNotifications;
        SRetainLogs.IsChecked = s.RetainLogs;
        SStability.Text = s.StabilitySeconds.ToString();
        STimeout.Text = s.RecoveryTimeoutMinutes.ToString();
        SRetryInterval.Text = s.RetryIntervalSeconds.ToString();
        SMaxRetries.Text = s.MaxRetries.ToString();
        SLostConfirm.Text = s.ExternalLostConfirmSeconds.ToString();
        SShutdownTry.IsChecked = s.TryPreferredModeOnShutdown;
        STriggerMode.SelectedItem = STriggerMode.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == s.TriggerMode.ToString());
        SPreferred.SelectedItem = SPreferred.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == s.PreferredMode.ToString());
    }

    private static bool ReadInt(TextBox box, int min, int max, string name, List<string> errors, out int v)
    {
        if (int.TryParse(box.Text.Trim(), out v) && v >= min && v <= max) return true;
        errors.Add($"{name} 必须是 {min}–{max} 之间的整数");
        return false;
    }

    private void RevertSettings_Click(object s, RoutedEventArgs e) { LoadSettingsToUi(); SettingsResult.Text = "已撤销未保存的更改"; }

    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        var errors = new List<string>();
        var n = _app.Settings.Clone();
        n.AutoSwitchOnPowerLoss = SAutoSwitch.IsChecked == true;
        n.AutoRestoreOnPowerReturn = SAutoRestore.IsChecked == true;
        n.ShowNotifications = SNotify.IsChecked == true;
        n.RetainLogs = SRetainLogs.IsChecked == true;
        if (ReadInt(SStability, 3, 600, "稳定等待时间", errors, out var a)) n.StabilitySeconds = a;
        if (ReadInt(STimeout, 1, 1440, "恢复超时时间", errors, out var b)) n.RecoveryTimeoutMinutes = b;
        if (ReadInt(SRetryInterval, 5, 3600, "重试间隔", errors, out var c)) n.RetryIntervalSeconds = c;
        if (ReadInt(SMaxRetries, 0, 10, "最大重试次数", errors, out var d)) n.MaxRetries = d;
        if (ReadInt(SLostConfirm, 2, 120, "外接路径消失确认时间", errors, out var f)) n.ExternalLostConfirmSeconds = f;
        n.TryPreferredModeOnShutdown = SShutdownTry.IsChecked == true;
        if (STriggerMode.SelectedItem is ComboBoxItem ti && Enum.TryParse<TriggerMode>((string)ti.Tag, out var tm)) n.TriggerMode = tm;
        if (SPreferred.SelectedItem is ComboBoxItem item && Enum.TryParse<DisplayMode>((string)item.Tag, out var mode)) n.PreferredMode = mode;
        if (errors.Count > 0) { SettingsResult.Foreground = B("Bad"); SettingsResult.Text = string.Join("；", errors); return; }

        var msg = new StringBuilder();
        var wantStartup = SStartup.IsChecked == true;
        if (wantStartup != _app.Settings.StartWithWindows || (wantStartup && _app.StartupStatus is { Exists: false }))
        {
            var r = wantStartup ? await _app.StartupTasks.EnableAsync(App.ExePath, App.CurrentUserId()) : await _app.StartupTasks.DisableAsync();
            _app.Log.Log(LogCategory.Startup, r.Message, r.Success ? LogResult.Success : LogResult.Failure);
            if (r.Success) { n.StartWithWindows = wantStartup; msg.Append(r.Message).Append("。"); }
            else
            {
                SStartup.IsChecked = _app.Settings.StartWithWindows;
                MessageBox.Show(this, r.Message + "\n\n" + r.NextStep, "自启动设置失败", MessageBoxButton.OK, MessageBoxImage.Warning);
                msg.Append("自启动设置失败，其余设置已保存。");
            }
            await _app.ReconcileStartupTaskAsync();
        }

        n.Normalize();
        if (_app.TrySaveSettings(n))
        {
            var modeChanged = n.PreferredMode != _app.Settings.PreferredMode;
            _app.Settings = n;
            _app.Log.Log(LogCategory.Settings, $"设置已保存（触发方式 {n.TriggerMode}，关机前尝试 {(n.TryPreferredModeOnShutdown ? "开" : "关")}，首选模式 {DisplayModeText.Of(n.PreferredMode)}，稳定 {n.StabilitySeconds}s，超时 {n.RecoveryTimeoutMinutes}min，重试 {n.MaxRetries}×{n.RetryIntervalSeconds}s）", LogResult.Success);
            SettingsResult.Foreground = B("Ok");
            SettingsResult.Text = "设置已保存。" + msg + (modeChanged ? "首选模式已更新，未切换当前显示。" : "");
        }
        else { SettingsResult.Foreground = B("Bad"); SettingsResult.Text = "保存失败，请查看日志。"; }
        Refresh();
    }

    // ───────── 日志 ─────────
    private bool LogFilter(object o)
    {
        if (o is not LogEntry e) return false;
        if (LogCategoryFilter.SelectedIndex > 0 && e.CategoryText != (string)LogCategoryFilter.SelectedItem) return false;
        if (LogResultFilter.SelectedIndex > 0 && e.ResultText != (string)LogResultFilter.SelectedItem) return false;
        var q = LogSearch.Text?.Trim();
        return string.IsNullOrEmpty(q) || e.Message.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void LogFilter_Changed(object s, EventArgs e) => _logView?.Refresh();

    private void CopySelectedLogs_Click(object s, RoutedEventArgs e) =>
        CopyLines(LogGrid.SelectedItems.Cast<LogEntry>());
    private void CopyAllLogs_Click(object s, RoutedEventArgs e) =>
        CopyLines(_logView.Cast<LogEntry>());

    private void CopyLines(IEnumerable<LogEntry> items)
    {
        var text = string.Join(Environment.NewLine, items.Select(i => i.ToString()));
        if (text.Length == 0) return;
        try { Clipboard.SetText(text); } catch (Exception ex) { MessageBox.Show(this, $"复制失败：{ex.Message}", "错误"); }
    }

    private void ClearLogs_Click(object s, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "将清空界面中的日志并删除日志目录下的所有日志文件，此操作无法撤销。\n确定清空吗？", "清空日志",
                MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) != MessageBoxResult.OK) return;
        _app.Log.ClearAll();
        _logs.Clear();
        _app.Log.Log(LogCategory.App, "用户清空了日志", LogResult.Info);
    }
}
