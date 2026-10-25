using PowerGuard.Core;

namespace PowerGuard.Core.Tests;

/// <summary>关机 / 重启 / 注销前恢复首选显示模式：决策逻辑（纯函数）与协调器行为。</summary>
public class ShutdownRestoreTests
{
    private static DisplayTopology Topo(DisplayMode mode, int extAvail = 1, bool ok = true) => ok
        ? new(true, mode, true, mode != DisplayMode.External, extAvail, mode == DisplayMode.Internal ? 0 : Math.Min(1, extAvail), $"{mode}|{extAvail}")
        : DisplayTopology.Failed("x");

    private static ShutdownDecision D(AppSettings? s = null, RecoveryStatus st = RecoveryStatus.Normal, bool paused = false,
        PowerSource p = PowerSource.Ac, RecoveryState? marker = null, DisplayTopology? t = null) =>
        ShutdownRestorePolicy.Decide(s ?? new AppSettings(), st, paused, p, marker, t ?? Topo(DisplayMode.Internal));

    [Fact]
    public void Applies_WhenOnAc_ExternalAvailable_AndNotInPreferred()
    {
        var d = D();
        Assert.True(d.Apply);
        Assert.Equal(DisplayMode.External, d.Target);
    }

    [Fact]
    public void Skips_WhenDisabledInSettings()
        => Assert.False(D(new AppSettings { RestoreOnShutdown = false }).Apply);

    [Fact]
    public void Skips_WhenAlreadyPreferred()
    {
        var d = D(t: Topo(DisplayMode.External));
        Assert.False(d.Apply);
        Assert.Contains("无需切换", d.Reason);
    }

    [Theory]
    [InlineData(PowerSource.Battery)]
    [InlineData(PowerSource.Unknown)]
    public void Skips_WhenNotOnAc(PowerSource p) => Assert.False(D(p: p).Apply);

    [Fact]
    public void Skips_WhenNoExternalPath_WouldBlackScreen()
    {
        var d = D(t: Topo(DisplayMode.Internal, extAvail: 0));
        Assert.False(d.Apply);
        Assert.Contains("黑屏", d.Reason);
    }

    [Fact]
    public void InternalPreferred_DoesNotNeedExternal()
    {
        var d = D(new AppSettings { PreferredMode = DisplayMode.Internal }, t: Topo(DisplayMode.Extend, extAvail: 0));
        Assert.True(d.Apply);
        Assert.Equal(DisplayMode.Internal, d.Target);
    }

    [Fact]
    public void Skips_WhenTopologyQueryFails() => Assert.False(D(t: Topo(DisplayMode.Unknown, ok: false)).Apply);

    [Fact]
    public void Skips_WhenPaused()
    {
        Assert.False(D(paused: true).Apply);
        Assert.False(D(st: RecoveryStatus.Paused).Apply);
    }

    [Theory]
    [InlineData(RecoveryStatus.RecoveryFailed)]
    [InlineData(RecoveryStatus.PowerLossDetected)]
    [InlineData(RecoveryStatus.SwitchingToInternal)]
    [InlineData(RecoveryStatus.RestoringPreferredMode)]
    public void Skips_InUnsafeOrBusyStates(RecoveryStatus st) => Assert.False(D(st: st).Apply);

    [Fact]
    public void WaitingForExternal_OnAc_WithExternal_Applies_UsingMarkerSnapshot()
    {
        var marker = new RecoveryState { PreferredModeSnapshot = DisplayMode.Extend };
        var d = D(new AppSettings { PreferredMode = DisplayMode.External }, RecoveryStatus.WaitingForExternal, marker: marker);
        Assert.True(d.Apply);
        Assert.Equal(DisplayMode.Extend, d.Target); // 以断电时的快照为准
    }

    // ───────── 协调器 ─────────

    [Fact]
    public async Task Coordinator_SwitchesToPreferred_BeforeShutdown()
    {
        var h = new Harness();
        h.Display.Mode = DisplayMode.Internal;
        await h.C.InitializeAsync(PowerSource.Ac);
        var r = await h.C.PrepareForShutdownAsync(TimeSpan.FromSeconds(4));
        Assert.True(r.Attempted);
        Assert.True(r.Success);
        Assert.Equal(DisplayMode.External, h.Display.Mode);
        Assert.Equal([DisplayMode.External], h.Display.Launches);
        Assert.Contains(h.Log.Entries, e => e.Message.Contains("关机前恢复成功"));
    }

    [Fact]
    public async Task Coordinator_OnBattery_DoesNotSwitch()
    {
        var h = new Harness();
        await h.C.InitializeAsync(PowerSource.Ac);
        await h.C.OnPowerSourceChangedAsync(PowerSource.Battery); // 断电 → 内屏
        h.Display.Launches.Clear();
        var r = await h.C.PrepareForShutdownAsync(TimeSpan.FromSeconds(4));
        Assert.False(r.Attempted);
        Assert.Empty(h.Display.Launches);
        Assert.Equal(DisplayMode.Internal, h.Display.Mode);
        Assert.Contains(h.Log.Entries, e => e.Message.Contains("关机前恢复：跳过"));
    }

    [Fact]
    public async Task Coordinator_SwitchWithoutEffect_ReportsFailure_WithinBudget()
    {
        var h = new Harness();
        h.Display.Mode = DisplayMode.Internal;
        await h.C.InitializeAsync(PowerSource.Ac);
        h.Display.SwitchHasNoEffect = true;
        var start = h.Clock.Now;
        var r = await h.C.PrepareForShutdownAsync(TimeSpan.FromSeconds(3));
        Assert.True(r.Attempted);
        Assert.False(r.Success);
        Assert.True(h.Clock.Now - start <= TimeSpan.FromSeconds(3));
    }

    [Fact]
    public async Task Coordinator_DoesNotTouchMarker_AndNextBootClearsItWithoutSwitching()
    {
        var h = new Harness();
        await h.C.InitializeAsync(PowerSource.Ac);
        await h.C.OnPowerSourceChangedAsync(PowerSource.Battery);
        await h.C.OnPowerSourceChangedAsync(PowerSource.Ac); // 来电，等待稳定期间用户关机
        var r = await h.C.PrepareForShutdownAsync(TimeSpan.FromSeconds(4));
        Assert.True(r.Success);
        Assert.NotNull(h.Store.State); // 标记保留，关机失败时下次仍可继续

        // 下次开机：已是首选模式 → 稳定后直接确认并清除标记，不再切屏
        h.Display.Launches.Clear();
        h.Create();
        await h.C.InitializeAsync(PowerSource.Ac);
        await h.RunFor(30);
        Assert.Equal(RecoveryStatus.Normal, h.C.Status);
        Assert.Null(h.Store.State);
        Assert.Empty(h.Display.Launches);
    }

    [Fact]
    public async Task Coordinator_Disabled_DoesNothing()
    {
        var h = new Harness();
        h.Settings = h.Settings with { RestoreOnShutdown = false };
        h.Display.Mode = DisplayMode.Internal;
        await h.C.InitializeAsync(PowerSource.Ac);
        var r = await h.C.PrepareForShutdownAsync(TimeSpan.FromSeconds(4));
        Assert.False(r.Attempted);
        Assert.Empty(h.Display.Launches);
    }
}
