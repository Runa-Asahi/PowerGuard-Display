using PowerGuard.Core;

namespace PowerGuard.Core.Tests;

public sealed class FakeClock : IClock
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 13, 22, 0, 0, TimeSpan.FromHours(8));
    public void Advance(TimeSpan t) => Now += t;
}

/// <summary>立即完成并推进假时钟。</summary>
public sealed class FakeDelay(FakeClock clock) : IDelay
{
    /// <summary>每次等待时调用（用于在等待窗口内注入事件）。</summary>
    public Action<TimeSpan>? OnDelay { get; set; }
    public Task Delay(TimeSpan d, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        clock.Advance(d);
        OnDelay?.Invoke(d);
        return Task.CompletedTask;
    }
}

/// <summary>模拟一台“内屏 + 1 个 HDMI 显示器”的笔记本。</summary>
public sealed class FakeDisplay : IDisplayTopologyProvider, IDisplaySwitcher
{
    public DisplayMode Mode { get; set; } = DisplayMode.External;
    public bool ExternalConnected { get; set; } = true;
    public bool LaunchFails { get; set; }
    public bool SwitchHasNoEffect { get; set; }
    public bool QueryFails { get; set; }
    public int QueryCount { get; private set; }
    public List<DisplayMode> Launches { get; } = new();
    public Action<DisplayMode>? BeforeLaunch { get; set; }

    public DisplayTopology Query()
    {
        QueryCount++;
        if (QueryFails) return DisplayTopology.Failed("模拟失败");
        if (Override is not null) return Override;
        var mode = Mode;
        if (!ExternalConnected && mode != DisplayMode.Internal) mode = DisplayMode.Internal; // Windows 会自动回落
        var internalActive = mode is DisplayMode.Internal or DisplayMode.Extend or DisplayMode.Clone;
        var extActive = mode is DisplayMode.External or DisplayMode.Extend or DisplayMode.Clone ? 1 : 0;
        return new DisplayTopology(true, mode, true, internalActive, ExternalConnected ? 1 : 0, extActive,
            $"{mode}|{ExternalConnected}");
    }

    public bool Hang { get; set; }
    /// <summary>模拟显示器开着但 Windows 认为外接路径不可用的组合时使用：直接指定拓扑。</summary>
    public DisplayTopology? Override { get; set; }

    public async Task<SwitchLaunchResult> SwitchAsync(DisplayMode mode, CancellationToken ct)
    {
        BeforeLaunch?.Invoke(mode);
        Launches.Add(mode);
        if (Hang) await Task.Delay(Timeout.Infinite, ct);
        if (LaunchFails) return SwitchLaunchResult.Fail("模拟：找不到文件");
        if (!SwitchHasNoEffect && (mode == DisplayMode.Internal || ExternalConnected)) Mode = mode;
        return SwitchLaunchResult.Ok(0);
    }
}

public sealed class MemoryStateStore : IRecoveryStateStore
{
    public RecoveryState? State { get; set; }
    public bool Corrupt { get; set; }
    public int SaveCount { get; private set; }
    public LoadResult<RecoveryState> Load() => Corrupt
        ? new(null, LoadStatus.Corrupt, "corrupt")
        : State is null ? new(null, LoadStatus.Missing) : new(Copy(State), LoadStatus.Loaded);
    public void Save(RecoveryState s) { State = Copy(s); SaveCount++; Corrupt = false; }
    public void Clear() { State = null; Corrupt = false; }
    private static RecoveryState Copy(RecoveryState s) => System.Text.Json.JsonSerializer.Deserialize<RecoveryState>(System.Text.Json.JsonSerializer.Serialize(s))!;
}

public sealed class Harness
{
    public FakeClock Clock { get; } = new();
    public FakeDisplay Display { get; } = new();
    public MemoryStateStore Store { get; } = new();
    public MemoryLogger Log { get; } = new();
    public AppSettings Settings { get; set; } = new() { TriggerMode = TriggerMode.Compatibility, StabilitySeconds = 15, RecoveryTimeoutMinutes = 10, RetryIntervalSeconds = 20, MaxRetries = 2 };
    public RecoveryCoordinator C { get; private set; }

    public Harness() => C = Create();

    public FakeDelay Delay { get; private set; } = null!;

    public RecoveryCoordinator Create() =>
        C = new RecoveryCoordinator(() => Settings, Store, Display, Display, Clock, Delay = new FakeDelay(Clock), Log);

    /// <summary>模拟 n 秒，每 2 秒一次 Tick（与真实低频轮询一致）。</summary>
    public async Task RunFor(int seconds)
    {
        for (var i = 0; i < seconds; i += 2) { Clock.Advance(TimeSpan.FromSeconds(2)); await C.TickAsync(); }
    }
}
