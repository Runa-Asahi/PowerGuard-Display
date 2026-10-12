using PowerGuard.Core;

namespace PowerGuard.Core.Tests;

public class CoordinatorTests
{
    [Fact] // 用例 2
    public async Task StartOnAc_NoMarker_StaysNormal_NoSwitch()
    {
        var h = new Harness();
        await h.C.InitializeAsync(PowerSource.Ac);
        await h.RunFor(60);
        Assert.Equal(RecoveryStatus.Normal, h.C.Status);
        Assert.Empty(h.Display.Launches);
    }

    [Fact] // 用例 3
    public async Task StartOnBattery_NoMarker_DoesNotSwitch()
    {
        var h = new Harness();
        await h.C.InitializeAsync(PowerSource.Battery);
        await h.RunFor(60);
        Assert.Equal(RecoveryStatus.Normal, h.C.Status);
        Assert.Empty(h.Display.Launches);
        Assert.Null(h.Store.State);
    }

    [Fact] // 用例 4
    public async Task AcToBattery_SavesMarkerBeforeSwitching_ThenInternal()
    {
        var h = new Harness();
        await h.C.InitializeAsync(PowerSource.Ac);
        bool markerSavedBeforeLaunch = false;
        h.Display.BeforeLaunch = _ => markerSavedBeforeLaunch = h.Store.State is { Pending: true };
        await h.C.OnPowerSourceChangedAsync(PowerSource.Battery);
        Assert.True(markerSavedBeforeLaunch);
        Assert.Equal(DisplayMode.Internal, h.Display.Mode);
        Assert.Equal(RecoveryStatus.WaitingForExternal, h.C.Status);
        Assert.Equal(DisplayMode.External, h.Store.State!.PreferredModeSnapshot);
        Assert.Equal(DisplayMode.External, h.Store.State!.ModeBeforePowerLoss);
    }

    [Fact]
    public async Task DuplicateNotifications_AreIgnored()
    {
        var h = new Harness();
        await h.C.InitializeAsync(PowerSource.Ac);
        await h.C.OnPowerSourceChangedAsync(PowerSource.Ac);
        await h.C.OnPowerSourceChangedAsync(PowerSource.Battery);
        await h.C.OnPowerSourceChangedAsync(PowerSource.Battery);
        Assert.Single(h.Display.Launches);
    }

    [Fact]
    public async Task UnknownPower_IsNeverTreatedAsPowerLoss()
    {
        var h = new Harness();
        await h.C.InitializeAsync(PowerSource.Ac);
        await h.C.OnPowerSourceChangedAsync(PowerSource.Unknown);
        await h.C.OnPowerSourceChangedAsync(PowerSource.Battery); // Unknown→Battery 不是确认的市电转电池
        Assert.Empty(h.Display.Launches);
        Assert.Equal(RecoveryStatus.Normal, h.C.Status);
    }

    [Fact] // 用例 5、7
    public async Task AcReturns_WaitsForStableExternal_ThenRestoresAndClearsMarker()
    {
        var h = new Harness();
        await h.C.InitializeAsync(PowerSource.Ac);
        await h.C.OnPowerSourceChangedAsync(PowerSource.Battery);
        h.Display.ExternalConnected = false;            // 显示器断电
        await h.RunFor(600);                             // 断电期间
        Assert.Single(h.Display.Launches);               // 不反复切屏
        await h.C.OnPowerSourceChangedAsync(PowerSource.Ac);
        await h.C.TickAsync();
        Assert.Single(h.Display.Launches);               // 市电恢复不立即切回
        h.Display.ExternalConnected = true;              // 显示器重新可用
        await h.RunFor(10);
        Assert.Equal(RecoveryStatus.WaitingForExternal, h.C.Status); // 尚未稳定
        await h.RunFor(10);
        Assert.Equal(RecoveryStatus.Normal, h.C.Status);
        Assert.Equal(DisplayMode.External, h.Display.Mode);
        Assert.Null(h.Store.State);
    }

    [Fact] // 用例 6 + 17
    public async Task ExternalNeverReturns_TimesOut_KeepsMarkerAndInternal()
    {
        var h = new Harness();
        await h.C.InitializeAsync(PowerSource.Ac);
        await h.C.OnPowerSourceChangedAsync(PowerSource.Battery);
        h.Display.ExternalConnected = false;
        await h.C.OnPowerSourceChangedAsync(PowerSource.Ac);
        await h.RunFor(11 * 60);
        Assert.Equal(RecoveryStatus.RecoveryFailed, h.C.Status);
        Assert.NotNull(h.Store.State);
        Assert.Contains("分钟内未检测到", h.Store.State!.LastError);
        Assert.Equal(DisplayMode.Internal, h.Display.Mode);
        var launches = h.Display.Launches.Count;
        await h.RunFor(3600);
        Assert.Equal(launches, h.Display.Launches.Count); // 不无限循环
    }

    [Fact] // 用例 8
    public async Task FlappingExternal_ResetsStabilityTimer()
    {
        var h = new Harness();
        await h.C.InitializeAsync(PowerSource.Ac);
        await h.C.OnPowerSourceChangedAsync(PowerSource.Battery);
        await h.C.OnPowerSourceChangedAsync(PowerSource.Ac);
        for (var i = 0; i < 10; i++)
        {
            h.Display.ExternalConnected = true; await h.RunFor(10);
            h.Display.ExternalConnected = false; await h.RunFor(4);
        }
        Assert.Single(h.Display.Launches); // 只有断电时的那次
        h.Display.ExternalConnected = true;
        await h.RunFor(20);
        Assert.Equal(RecoveryStatus.Normal, h.C.Status);
    }

    [Fact] // 用例 9 + 17
    public async Task DisplaySwitchLaunchFailure_BoundedRetries_ThenFailed()
    {
        var h = new Harness();
        await h.C.InitializeAsync(PowerSource.Ac);
        await h.C.OnPowerSourceChangedAsync(PowerSource.Battery);
        h.Display.LaunchFails = true;
        await h.C.OnPowerSourceChangedAsync(PowerSource.Ac);
        h.Display.Launches.Clear();
        await h.RunFor(60);
        Assert.Equal(RecoveryStatus.RecoveryFailed, h.C.Status);
        Assert.Equal(1 + h.Settings.MaxRetries, h.Display.Launches.Count(m => m == DisplayMode.External));
        Assert.Contains("启动失败", h.Store.State!.LastError);
        await h.RunFor(3600);
        Assert.Equal(1 + h.Settings.MaxRetries, h.Display.Launches.Count(m => m == DisplayMode.External));
    }

    [Fact]
    public async Task SwitchHasNoEffect_VerificationFails_NotTreatedAsSuccess()
    {
        var h = new Harness();
        await h.C.InitializeAsync(PowerSource.Ac);
        await h.C.OnPowerSourceChangedAsync(PowerSource.Battery);
        await h.C.OnPowerSourceChangedAsync(PowerSource.Ac);
        h.Display.SwitchHasNoEffect = true;
        await h.RunFor(120);
        Assert.Equal(RecoveryStatus.RecoveryFailed, h.C.Status);
        Assert.NotNull(h.Store.State);
        Assert.Contains("校验未通过", h.Store.State!.LastError);
    }

    [Fact] // 用例 11、12：恢复中异常退出 → 重新登录（市电 + 显示器可用）
    public async Task MarkerOnLogin_OnAc_RestoresAfterStability()
    {
        var h = new Harness();
        await h.C.InitializeAsync(PowerSource.Ac);
        await h.C.OnPowerSourceChangedAsync(PowerSource.Battery);
        // 进程“崩溃”：丢弃协调器，只保留持久化的标记
        Assert.NotNull(h.Store.State);
        h.Display.Mode = DisplayMode.Internal;
        h.Create();
        await h.C.InitializeAsync(PowerSource.Ac);
        Assert.Equal(RecoveryStatus.WaitingForExternal, h.C.Status);
        Assert.Contains(h.Log.Entries, e => e.Message.Contains("发现未完成的恢复标记"));
        await h.RunFor(20);
        Assert.Equal(RecoveryStatus.Normal, h.C.Status);
        Assert.Equal(DisplayMode.External, h.Display.Mode);
        Assert.Null(h.Store.State);
    }

    [Fact] // 用例 12：带标记登录但仍是电池 → 继续保护内屏
    public async Task MarkerOnLogin_OnBattery_ProtectsInternal()
    {
        var h = new Harness();
        h.Store.State = new RecoveryState { PreferredModeSnapshot = DisplayMode.Extend, StartedAt = h.Clock.Now };
        h.Display.Mode = DisplayMode.External; // 例如 Windows 记住了仅第二屏幕
        await h.C.InitializeAsync(PowerSource.Battery);
        Assert.Equal(DisplayMode.Internal, h.Display.Mode);
        await h.RunFor(600);
        Assert.Equal(RecoveryStatus.WaitingForExternal, h.C.Status);
        Assert.DoesNotContain(DisplayMode.Extend, h.Display.Launches);
    }

    [Fact]
    public async Task CorruptMarker_TreatedAsPending()
    {
        var h = new Harness();
        h.Store.Corrupt = true;
        await h.C.InitializeAsync(PowerSource.Battery);
        Assert.Equal(RecoveryStatus.WaitingForExternal, h.C.Status);
        Assert.NotNull(h.Store.State);
    }

    [Fact] // 用例 13
    public async Task Paused_IgnoresPowerLoss_AndResumeRestoresFlow()
    {
        var h = new Harness();
        await h.C.InitializeAsync(PowerSource.Ac);
        await h.C.SetPausedAsync(true);
        Assert.Equal(RecoveryStatus.Paused, h.C.Status);
        await h.C.OnPowerSourceChangedAsync(PowerSource.Battery);
        Assert.Empty(h.Display.Launches);
        await h.C.SetPausedAsync(false);
        Assert.Equal(RecoveryStatus.Normal, h.C.Status);
    }

    [Fact]
    public async Task Paused_DuringWaiting_DoesNotAutoRestore()
    {
        var h = new Harness();
        await h.C.InitializeAsync(PowerSource.Ac);
        await h.C.OnPowerSourceChangedAsync(PowerSource.Battery);
        await h.C.SetPausedAsync(true);
        await h.C.OnPowerSourceChangedAsync(PowerSource.Ac);
        await h.RunFor(120);
        Assert.Equal(DisplayMode.Internal, h.Display.Mode);
        Assert.NotNull(h.Store.State);
        await h.C.SetPausedAsync(false);
        await h.RunFor(20);
        Assert.Equal(DisplayMode.External, h.Display.Mode);
    }

    [Fact]
    public async Task AutoSwitchDisabled_IgnoresPowerLoss()
    {
        var h = new Harness();
        h.Settings.AutoSwitchOnPowerLoss = false;
        await h.C.InitializeAsync(PowerSource.Ac);
        await h.C.OnPowerSourceChangedAsync(PowerSource.Battery);
        Assert.Empty(h.Display.Launches);
    }

    [Fact]
    public async Task AutoRestoreDisabled_WaitsForManual()
    {
        var h = new Harness();
        h.Settings.AutoRestoreOnPowerReturn = false;
        await h.C.InitializeAsync(PowerSource.Ac);
        await h.C.OnPowerSourceChangedAsync(PowerSource.Battery);
        await h.C.OnPowerSourceChangedAsync(PowerSource.Ac);
        await h.RunFor(120);
        Assert.Equal(DisplayMode.Internal, h.Display.Mode);
        var r = await h.C.ManualRestorePreferredAsync();
        Assert.True(r.Success);
        Assert.Null(h.Store.State);
    }

    [Fact]
    public async Task ChangingPreferredMode_DoesNotSwitch()
    {
        var h = new Harness();
        await h.C.InitializeAsync(PowerSource.Ac);
        h.Settings.PreferredMode = DisplayMode.Clone;
        await h.RunFor(60);
        Assert.Empty(h.Display.Launches);
    }

    [Fact]
    public async Task ManualRestore_NoExternal_DoesNotSwitch_GivesNextStep()
    {
        var h = new Harness();
        await h.C.InitializeAsync(PowerSource.Ac);
        h.Display.ExternalConnected = false;
        var r = await h.C.ManualRestorePreferredAsync();
        Assert.False(r.Success);
        Assert.NotNull(r.NextStep);
        Assert.Empty(h.Display.Launches);
    }

    [Fact]
    public async Task ManualSwitchInternal_Works()
    {
        var h = new Harness();
        await h.C.InitializeAsync(PowerSource.Ac);
        var r = await h.C.ManualSwitchToInternalAsync();
        Assert.True(r.Success);
        Assert.Equal(DisplayMode.Internal, h.Display.Mode);
        Assert.Equal(RecoveryStatus.Normal, h.C.Status);
    }

    [Fact]
    public async Task PowerReturnsDuringRetry_AbortsAndProtectsInternal()
    {
        var h = new Harness();
        await h.C.InitializeAsync(PowerSource.Ac);
        await h.C.OnPowerSourceChangedAsync(PowerSource.Battery);
        await h.C.OnPowerSourceChangedAsync(PowerSource.Ac);
        h.Display.SwitchHasNoEffect = true;
        h.Display.BeforeLaunch = m => { if (m == DisplayMode.External) h.Display.ExternalConnected = false; };
        await h.RunFor(20);
        Assert.Equal(RecoveryStatus.WaitingForExternal, h.C.Status);
        Assert.Equal(1, h.Display.Launches.Count(m => m == DisplayMode.External));
    }
}
