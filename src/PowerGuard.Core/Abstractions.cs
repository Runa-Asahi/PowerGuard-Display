namespace PowerGuard.Core;

public interface IClock { DateTimeOffset Now { get; } }
public sealed class SystemClock : IClock { public DateTimeOffset Now => DateTimeOffset.Now; }

public interface IDelay { Task Delay(TimeSpan duration, CancellationToken ct); }
public sealed class TaskDelay : IDelay { public Task Delay(TimeSpan d, CancellationToken ct) => Task.Delay(d, ct); }

/// <summary>真实实现使用 QueryDisplayConfig。</summary>
public interface IDisplayTopologyProvider { DisplayTopology Query(); }

/// <summary>真实实现调用 System32\DisplaySwitch.exe。启动成功 ≠ 切换成功。</summary>
/// <param name="alternate">true = 使用备用切屏方式（例如首选方式校验未通过后的第二次尝试）。</param>
public interface IDisplaySwitcher { Task<SwitchLaunchResult> SwitchAsync(DisplayMode mode, CancellationToken ct, bool alternate = false); }

public interface IRecoveryStateStore
{
    LoadResult<RecoveryState> Load();
    void Save(RecoveryState state);
    void Clear();
}

public interface ISettingsStore
{
    LoadResult<AppSettings> Load();
    void Save(AppSettings settings);
}

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr, string? StartError = null)
{
    public bool Ok => StartError is null && ExitCode == 0;
}

public interface IProcessRunner
{
    /// <summary>以参数数组启动进程（不经过 shell 拼接）。</summary>
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> args, TimeSpan timeout, CancellationToken ct = default);
}

/// <summary>外接显示器通过 DDC/CI 的应答情况。Unsupported = 无法判断（未接、不支持 DDC、接口出错）。</summary>
public enum MonitorResponse { Unsupported, Responding, NotResponding }

/// <summary>
/// 独立于 Windows 显示拓扑（可能因显示器断电后仍保留 EDID 而“过时”）的显示器探测。
/// 真实实现：对当前活动的外接显示器读取 DDC/CI VCP 0xD6（电源模式），显示器真正断电时 I²C 不会应答。
/// 只能探测处于活动状态（有桌面输出）的外接显示器。
/// </summary>
public interface IMonitorProbe { MonitorResponse ProbeActiveExternal(); }
