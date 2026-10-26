using System.Diagnostics;
using System.Security.Principal;
using System.Windows;
using System.Windows.Threading;
using PowerGuard.App.Platform;
using PowerGuard.Core;

namespace PowerGuard.App;

public partial class App : Application
{
    private const string MutexName = @"Local\PowerGuardDisplay.SingleInstance";
    private const string ShowEventName = @"Local\PowerGuardDisplay.ShowWindow";
    /// <summary>新版本启动时用它请求正在运行的实例正常退出（替换升级）。</summary>
    private const string ExitEventName = @"Local\PowerGuardDisplay.ExitForUpgrade";

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;
    private EventWaitHandle? _exitEvent;
    private RegisteredWaitHandle? _exitWait;
    private Timer? _timer;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private bool _exiting;
    private int _pollBusy;
    private TimeSpan _pollPeriod;
    private DisplayTopologyProvider? _topology;

    /// <summary>正常状态下的轮询间隔（主要依赖电源通知与 WM_DISPLAYCHANGE，轮询只是兜底）。</summary>
    private static readonly TimeSpan IdlePoll = TimeSpan.FromSeconds(10);
    /// <summary>恢复流程中（需要稳定计时 / 超时判定）的轮询间隔。</summary>
    private static readonly TimeSpan ActivePoll = TimeSpan.FromSeconds(2);
    /// <summary>关机前恢复的总时长预算。Windows 约 5 秒后才会显示“程序阻止关机”界面。</summary>
    private static readonly TimeSpan ShutdownBudget = TimeSpan.FromSeconds(4);

    public static string DataDir { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PowerGuardDisplay");
    public static string LogDir => Path.Combine(DataDir, "logs");

    public FileLogger Log { get; private set; } = null!;
    public SettingsStore SettingsStore { get; private set; } = null!;
    public AppSettings Settings { get; set; } = new();
    public RecoveryCoordinator Coordinator { get; private set; } = null!;
    public StartupTaskService StartupTasks { get; private set; } = null!;
    public SystemMonitor Monitor { get; private set; } = null!;
    public bool MonitorRunning { get; private set; }
    public StartupTaskStatus? StartupStatus { get; private set; }
    public static new App Current => (App)Application.Current;

    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "PowerGuardDisplay.exe");
    public static string SchtasksPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe");

    public event EventHandler? StartupStatusChanged;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try { await StartupCoreAsync(e); }
        catch (Exception ex)
        {
            // async void：任何异常都必须在这里接住，否则进程直接崩溃且无提示
            try { Log?.Log(LogCategory.Error, $"启动失败：{ex}", LogResult.Failure); } catch { }
            MessageBox.Show($"断电护屏启动失败：{ex.Message}\n\n日志目录：{LogDir}", "断电护屏", MessageBoxButton.OK, MessageBoxImage.Error);
            try { _mutex?.ReleaseMutex(); } catch { }
            Shutdown(1);
        }
    }

    private async Task StartupCoreAsync(StartupEventArgs e)
    {

        _mutex = new Mutex(true, MutexName, out var first);
        if (!first && !TryTakeOverFromOtherVersion())
        {
            // 已有同版本实例：通知其显示窗口后退出
            try { using var ev = EventWaitHandle.OpenExisting(ShowEventName); ev.Set(); } catch { }
            _mutex.Dispose(); _mutex = null;
            Shutdown(0);
            return;
        }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => Dispatcher.BeginInvoke(ShowMainWindow), null, -1, false);
        _exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ExitEventName);
        _exitWait = ThreadPool.RegisterWaitForSingleObject(_exitEvent, (_, _) => Dispatcher.BeginInvoke(() =>
        {
            Log?.Log(LogCategory.App, "另一个版本的断电护屏请求替换本实例，正常退出（恢复标记已持久化）");
            ExitApp();
        }), null, -1, false);

        Directory.CreateDirectory(DataDir);
        Log = new FileLogger(LogDir);
        HookExceptions();
        var autostart = e.Args.Contains(StartupTaskService.AutostartArg);
        Log.Log(LogCategory.App, $"程序启动 {AppVersion.Display}（{ExePath}），Windows build {Environment.OSVersion.Version.Build}{(autostart ? "，由登录自启动任务启动" : "")}", LogResult.Success);

        SettingsStore = new SettingsStore(Path.Combine(DataDir, "settings.json"));
        var sr = SettingsStore.Load();
        Settings = sr.Value!;
        var firstRun = sr.Status == LoadStatus.Missing;
        if (sr.Status is LoadStatus.Corrupt or LoadStatus.Unreadable) Log.Log(LogCategory.Settings, $"{sr.Message}，本次使用安全默认值", LogResult.Warning);
        // Unreadable 时绝不覆盖用户的配置文件
        if (sr.Status is LoadStatus.Missing or LoadStatus.Corrupt) TrySaveSettings(Settings);
        else if (sr.Status == LoadStatus.Loaded && Settings.Normalize()) TrySaveSettings(Settings); // 版本迁移 / 越界修正后写回
        var removed = Log.ApplyRetention(Settings.RetainLogs); // 之后跨天时由 FileLogger 自动执行
        if (removed > 0) Log.Log(LogCategory.App, $"已清理 {removed} 个过期日志文件");
        Log.LoadHistory();

        var topology = _topology = new DisplayTopologyProvider();
        var switcher = new DisplaySwitcher(Log, () => Settings);
        Coordinator = new RecoveryCoordinator(() => Settings, new RecoveryStateStore(Path.Combine(DataDir, "recovery-state.json")),
            topology, switcher, new SystemClock(), new TaskDelay(), Log);
        StartupTasks = new StartupTaskService(new ProcessRunner(), SchtasksPath, DataDir);

        _tray = new TrayIcon(this);
        Coordinator.StateChanged += (_, _) => Dispatcher.BeginInvoke(() => _tray?.Update());

        // 先订阅再 Start：注册电源通知时 Windows 会立即推送一次当前值，不能丢
        Monitor = new SystemMonitor(Log);
        Monitor.PowerSourceChanged += p => Fire(() => Coordinator.OnPowerSourceChangedAsync(p));
        // WM_DISPLAYCHANGE 不单独记日志：拓扑真正变化时协调器会记录，避免刷屏
        Monitor.DisplayChanged += () => { _topology?.InvalidateCache(); Fire(Coordinator.TickAsync); };
        Monitor.ResumedFromSleep += () =>
        {
            Log.Log(LogCategory.Power, "系统从睡眠恢复，重新读取电源状态与显示拓扑");
            _topology?.InvalidateCache();
            Fire(async () => { await Coordinator.OnPowerSourceChangedAsync(SystemMonitor.ReadPowerStatus(Log)); await Coordinator.TickAsync(); });
        };
        Monitor.QueryEndSessionHandler = OnQueryEndSession;
        try { Monitor.Start(); MonitorRunning = true; }
        catch (Exception ex) { Log.Log(LogCategory.Error, $"系统事件监听启动失败：{ex.Message}", LogResult.Failure); }
        SessionEnding += (_, a) => Log.Log(LogCategory.App, $"Windows 会话结束（{a.ReasonSessionEnding}），恢复标记已持久化", LogResult.Info);

        var power = SystemMonitor.ReadPowerStatus(Log);
        await Coordinator.InitializeAsync(power);

        // 低频补充轮询：显示拓扑 + 电源状态（通知丢失时的兜底）。
        // 正常状态 10 秒一次；恢复流程中 2 秒一次。上一轮未结束时跳过，不叠加。
        _pollPeriod = IdlePoll;
        _timer = new Timer(_ => Poll(), null, TimeSpan.FromSeconds(2), IdlePoll);

        _tray.Update();
        _ = ReconcileStartupTaskAsync();

        if (firstRun)
        {
            ShowMainWindow();
            _tray.Notify("断电护屏已在后台运行", "首次运行：请在设置页确认首选显示模式，并按需开启登录自启动。", force: true);
        }
        else if (!autostart && Settings.ShowNotifications)
            _tray.Notify("断电护屏已在后台运行", "双击托盘图标打开控制台。");
    }

    /// <summary>
    /// 已有实例在运行时：如果它是另一个版本（例如升级后旧版仍在托盘），询问用户是否替换。
    /// 同意后先请求它正常退出（本版本起支持），旧版本不支持时再结束其进程。成功接管返回 true。
    /// 背景：v1.2.0 及更早版本中，新 exe 只会唤醒旧实例的窗口然后自己退出，看起来就像“新版本还是旧界面”。
    /// </summary>
    private bool TryTakeOverFromOtherVersion()
    {
        try
        {
            var me = Environment.ProcessId;
            var others = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(ExePath))
                .Where(p => p.Id != me && p.SessionId == Process.GetCurrentProcess().SessionId).ToList();
            if (others.Count == 0) return false;
            var other = others[0];
            string? otherPath = null;
            try { otherPath = other.MainModule?.FileName; } catch { }
            var otherVersion = AppVersion.Of(otherPath);
            var samePath = otherPath != null && string.Equals(Path.GetFullPath(otherPath), Path.GetFullPath(ExePath), StringComparison.OrdinalIgnoreCase);
            if (otherVersion == AppVersion.Version && samePath) return false; // 同一个程序：照常唤醒窗口

            var text = $"检测到另一个断电护屏正在运行：\n  版本：{(otherVersion is null ? "未知" : "v" + otherVersion)}\n  位置：{otherPath ?? "未知"}\n\n" +
                       $"当前启动的是 {AppVersion.Display}：\n  位置：{ExePath}\n\n是否关闭正在运行的那个并改用当前版本？\n（未完成的恢复标记会保留，新版本会接着处理）";
            if (MessageBox.Show(text, "断电护屏：替换正在运行的版本", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.Yes) != MessageBoxResult.Yes)
                return false;

            try { using var ev = EventWaitHandle.OpenExisting(ExitEventName); ev.Set(); } catch { /* 旧版本没有该事件 */ }
            if (!other.WaitForExit(5000))
            {
                try { other.Kill(); other.WaitForExit(5000); } catch { }
            }
            try { if (_mutex!.WaitOne(TimeSpan.FromSeconds(5))) return true; }
            catch (AbandonedMutexException) { return true; } // 旧进程被结束：互斥体被遗弃，已由本进程持有
            MessageBox.Show("未能关闭正在运行的实例，请从托盘菜单“退出程序”后再运行。", "断电护屏", MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }
        catch { return false; }
    }

    private void Poll()
    {
        if (Interlocked.Exchange(ref _pollBusy, 1) == 1) return;
        Task.Run(async () =>
        {
            try
            {
                await Coordinator.OnPowerSourceChangedAsync(SystemMonitor.ReadPowerStatus());
                await Coordinator.TickAsync();
            }
            catch (Exception ex) { Log.Log(LogCategory.Error, $"轮询异常：{ex.Message}", LogResult.Failure); }
            finally
            {
                var status = Coordinator.Status;
                var want = status is RecoveryStatus.Normal or RecoveryStatus.RecoveryFailed ? IdlePoll : ActivePoll;
                if (Coordinator.Paused) want = IdlePoll; // 定时暂停的到期检查精度 10 秒足够
                if (want != _pollPeriod && !_exiting)
                {
                    _pollPeriod = want;
                    try { _timer?.Change(want, want); } catch (ObjectDisposedException) { }
                }
                Volatile.Write(ref _pollBusy, 0);
            }
        });
    }

    /// <summary>
    /// 关机 / 重启 / 注销前（UI 线程同步调用）：尽力切回首选显示模式，使下次开机直接进入首选拓扑。
    /// 期间用 ShutdownBlockReasonCreate 告诉 Windows 原因；总时长受 ShutdownBudget 限制，超时即放弃，始终允许关机。
    /// </summary>
    private void OnQueryEndSession(IntPtr hwnd, string reason)
    {
        Log.Log(LogCategory.App, $"收到会话结束通知（{reason}）");
        if (!Settings.RestoreOnShutdown)
        {
            Log.Log(LogCategory.Switch, "关机前恢复：设置中已关闭，跳过");
            return;
        }
        var blocked = false;
        try
        {
            blocked = Native.NativeMethods.ShutdownBlockReasonCreate(hwnd, "断电护屏：正在切换回首选显示模式（最多几秒）…");
            // 在线程池上执行，避免 UI 同步上下文导致的死锁；UI 线程只做限时等待
            var work = Task.Run(() => Coordinator.PrepareForShutdownAsync(ShutdownBudget));
            if (!work.Wait(ShutdownBudget + TimeSpan.FromMilliseconds(500)))
                Log.Log(LogCategory.Switch, "关机前恢复：超出时间预算，已放弃等待（不影响关机）", LogResult.Warning);
        }
        catch (Exception ex) { Log.Log(LogCategory.Error, $"关机前恢复异常：{ex.Message}", LogResult.Failure); }
        finally
        {
            if (blocked) Native.NativeMethods.ShutdownBlockReasonDestroy(hwnd);
        }
    }

    private void HookExceptions()
    {
        DispatcherUnhandledException += (_, a) =>
        {
            Log.Log(LogCategory.Error, $"未处理的 UI 异常：{a.Exception}", LogResult.Failure);
            a.Handled = true; // 保持后台保护继续运行
        };
        AppDomain.CurrentDomain.UnhandledException += (_, a) =>
            Log.Log(LogCategory.Error, $"未处理的异常（终止={a.IsTerminating}）：{a.ExceptionObject}", LogResult.Failure);
        TaskScheduler.UnobservedTaskException += (_, a) =>
        {
            Log.Log(LogCategory.Error, $"未观察的任务异常：{a.Exception}", LogResult.Failure);
            a.SetObserved();
        };
    }

    private void Fire(Func<Task> work)
    {
        Task.Run(async () =>
        {
            try { await work(); }
            catch (Exception ex) { Log.Log(LogCategory.Error, $"后台操作异常：{ex.Message}", LogResult.Failure); }
        });
    }

    public bool TrySaveSettings(AppSettings s)
    {
        try { SettingsStore.Save(s); return true; }
        catch (Exception ex) { Log.Log(LogCategory.Settings, $"保存设置失败：{ex.Message}", LogResult.Failure); return false; }
    }

    public static string CurrentUserId()
    {
        try { return WindowsIdentity.GetCurrent().Name; } catch { return Environment.UserDomainName + "\\" + Environment.UserName; }
    }

    /// <summary>启动时核对自启动任务。偏好关闭时绝不擅自创建。</summary>
    public async Task ReconcileStartupTaskAsync()
    {
        try
        {
            var st = await StartupTasks.QueryAsync(ExePath);
            if (st.Error != null) Log.Log(LogCategory.Startup, $"检查自启动任务：{st.Error}", LogResult.Warning);
            if (Settings.StartWithWindows && (!st.Exists || (st.Command != null && !st.PointsToCurrentExe)))
            {
                var r = await StartupTasks.EnableAsync(ExePath, CurrentUserId());
                Log.Log(LogCategory.Startup, st.Exists ? $"自启动任务指向其他位置，重新创建：{r.Message}" : $"偏好为开启但任务不存在，重新创建：{r.Message}", r.Success ? LogResult.Success : LogResult.Failure);
                st = await StartupTasks.QueryAsync(ExePath);
            }
            else if (!Settings.StartWithWindows && st.Exists)
                Log.Log(LogCategory.Startup, "自启动偏好为关闭，但系统中存在同名任务（未自动修改，可在设置页处理）", LogResult.Warning);
            else
                Log.Log(LogCategory.Startup, $"自启动任务检查：{(st.Exists ? (st.Enabled ? "已启用" : "已禁用") : "不存在")}", LogResult.Info);
            StartupStatus = st;
        }
        catch (Exception ex) { Log.Log(LogCategory.Startup, $"检查自启动任务异常：{ex.Message}", LogResult.Failure); }
        await Dispatcher.InvokeAsync(() => StartupStatusChanged?.Invoke(this, EventArgs.Empty));
    }

    public void ShowMainWindow()
    {
        if (_exiting) return;
        _window ??= new MainWindow();
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    public void ShowPage(int index) { ShowMainWindow(); _window!.SelectPage(index); }

    /// <summary>窗口关闭只隐藏；只有这里才真正退出。</summary>
    public bool IsExiting => _exiting;

    public void ExitApp()
    {
        if (_exiting) return;
        _exiting = true;
        Log.Log(LogCategory.App, "用户退出程序", LogResult.Info);
        _timer?.Dispose();
        _showWait?.Unregister(null);
        _showEvent?.Dispose();
        _exitWait?.Unregister(null);
        _exitEvent?.Dispose();
        Monitor?.Dispose();
        _tray?.Dispose();
        Log.Dispose();
        _window?.Close();
        try { _mutex?.ReleaseMutex(); } catch { }
        _mutex?.Dispose();
        Shutdown(0); // 正常退出码 0：任务计划程序的失败重启不会拉起
    }

    public static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe")) { ArgumentList = { path }, UseShellExecute = false });
    }
}
