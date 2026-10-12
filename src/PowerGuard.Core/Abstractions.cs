namespace PowerGuard.Core;

public interface IClock { DateTimeOffset Now { get; } }
public sealed class SystemClock : IClock { public DateTimeOffset Now => DateTimeOffset.Now; }

public interface IDelay { Task Delay(TimeSpan duration, CancellationToken ct); }
public sealed class TaskDelay : IDelay { public Task Delay(TimeSpan d, CancellationToken ct) => Task.Delay(d, ct); }

/// <summary>真实实现使用 QueryDisplayConfig。</summary>
public interface IDisplayTopologyProvider { DisplayTopology Query(); }

/// <summary>真实实现调用 System32\DisplaySwitch.exe。启动成功 ≠ 切换成功。</summary>
public interface IDisplaySwitcher { Task<SwitchLaunchResult> SwitchAsync(DisplayMode mode, CancellationToken ct); }

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
