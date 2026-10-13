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

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;
    private Timer? _timer;
    private TrayIcon? _tray;
    private MainWindow? _window;
    private bool _exiting;

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

        _mutex = new Mutex(true, MutexName, out var first);
        if (!first)
        {
            // 已有实例：通知其显示窗口后退出
            try { using var ev = EventWaitHandle.OpenExisting(ShowEventName); ev.Set(); } catch { }
            _mutex.Dispose(); _mutex = null;
            Shutdown(0);
            return;
        }
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => Dispatcher.BeginInvoke(ShowMainWindow), null, -1, false);

        Directory.CreateDirectory(DataDir);
        Log = new FileLogger(LogDir);
        HookExceptions();
        var autostart = e.Args.Contains(StartupTaskService.AutostartArg);
        Log.Log(LogCategory.App, $"程序启动 v{typeof(App).Assembly.GetName().Version}，Windows build {Environment.OSVersion.Version.Build}{(autostart ? "，由登录自启动任务启动" : "")}", LogResult.Success);

        SettingsStore = new SettingsStore(Path.Combine(DataDir, "settings.json"));
        var sr = SettingsStore.Load();
        Settings = sr.Value!;
        var firstRun = sr.Status == LoadStatus.Missing;
        if (sr.Status == LoadStatus.Corrupt) Log.Log(LogCategory.Settings, $"{sr.Message}，已使用安全默认值", LogResult.Warning);
        if (sr.Status != LoadStatus.Loaded) TrySaveSettings(Settings);
        var removed = Log.ApplyRetention(Settings.RetainLogs);
        if (removed > 0) Log.Log(LogCategory.App, $"已清理 {removed} 个过期日志文件");
        Log.LoadHistory();

        var topology = new DisplayTopologyProvider();
        var switcher = new DisplaySwitcher(Log, () => Settings);
        Coordinator = new RecoveryCoordinator(() => Settings, new RecoveryStateStore(Path.Combine(DataDir, "recovery-state.json")),
            topology, switcher, new SystemClock(), new TaskDelay(), Log);
        StartupTasks = new StartupTaskService(new ProcessRunner(), SchtasksPath);

        _tray = new TrayIcon(this);
        Coordinator.StateChanged += (_, _) => Dispatcher.BeginInvoke(() => _tray?.Update());

        Monitor = new SystemMonitor(Log);
        try { Monitor.Start(); MonitorRunning = true; }
        catch (Exception ex) { Log.Log(LogCategory.Error, $"系统事件监听启动失败：{ex.Message}", LogResult.Failure); }
        Monitor.PowerSourceChanged += p => Fire(() => Coordinator.OnPowerSourceChangedAsync(p));
        Monitor.DisplayChanged += () => { Log.Log(LogCategory.Display, "收到 WM_DISPLAYCHANGE"); Fire(Coordinator.TickAsync); };
        Monitor.ResumedFromSleep += () => { Log.Log(LogCategory.Power, "系统从睡眠恢复，重新读取电源状态"); Fire(() => Coordinator.OnPowerSourceChangedAsync(SystemMonitor.ReadPowerStatus(Log))); };
        // 关机/注销：WM_QUERYENDSESSION（Application.SessionEnding）优先，WM_ENDSESSION 兜底；协调器内部保证只尝试一次。
        // 绝不设置 e.Cancel，不阻止关机；最多占用约 3 秒。
        SessionEnding += (_, a) =>
        {
            Log.Log(LogCategory.Shutdown, $"Windows 会话即将结束（{a.ReasonSessionEnding}），恢复标记已持久化");
            OnSessionEnding();
        };
        Monitor.SessionEnding += OnSessionEnding;

        var power = SystemMonitor.ReadPowerStatus(Log);
        await Coordinator.InitializeAsync(power);

        // 低频补充轮询：显示拓扑 + 电源状态（通知丢失时的兜底），3 秒一次
        _timer = new Timer(_ => Fire(async () =>
        {
            await Coordinator.OnPowerSourceChangedAsync(SystemMonitor.ReadPowerStatus());
            await Coordinator.TickAsync();
        }), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));

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

    public static readonly TimeSpan ShutdownBudget = TimeSpan.FromSeconds(3);

    private void OnSessionEnding()
    {
        try { Coordinator.TryPreferredModeOnShutdown(ShutdownBudget); }
        catch (Exception ex) { Log.Log(LogCategory.Shutdown, $"关机前尝试异常：{ex.Message}", LogResult.Failure); }
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
        Monitor?.Dispose();
        _tray?.Dispose();
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
