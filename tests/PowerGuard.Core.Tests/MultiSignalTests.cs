using PowerGuard.Core;

namespace PowerGuard.Core.Tests;

/// <summary>v2：多信号触发（外接显示路径消失）、统一触发入口、启动规则。</summary>
public class MultiSignalTests
{
    private static async Task<Harness> StartOnAc(Action<AppSettings>? cfg = null)
    {
        var h = new Harness();
        cfg?.Invoke(h.Settings);
        await h.C.InitializeAsync(PowerSource.Ac);
        await h.RunFor(4); // 建立“外接活动”的基线快照
        return h;
    }

    [Fact] // 只关显示器（市电一直在）
    public async Task MonitorOffOnly_Recommended_TriggersAfterConfirm_AndRestoresWithoutAcChange()
    {
        var h = await StartOnAc();
        h.Display.ExternalConnected = false;
        await h.RunFor(4);
        Assert.Equal(RecoveryStatus.Normal, h.C.Status);          // 确认窗口内不触发
        Assert.NotNull(h.C.Snapshot().ExternalLostSince);
        await h.RunFor(8);
        Assert.Equal(RecoveryStatus.WaitingForExternal, h.C.Status);
        Assert.Equal(TriggerReason.ExternalDisplayLost, h.Store.State!.TriggerReason);
        Assert.Equal(TriggerReason.ExternalDisplayLost, h.C.Snapshot().LastTriggerReason);
        Assert.Equal(DisplayMode.Internal, h.Display.Query().Mode);
        h.Display.ExternalConnected = true;                       // 显示器重新通电，电源从未变化
        await h.RunFor(20);
        Assert.Equal(RecoveryStatus.Normal, h.C.Status);
        Assert.Equal(DisplayMode.External, h.Display.Mode);
        Assert.Null(h.Store.State);
    }

    [Fact]
    public async Task MonitorFlap_WithinConfirmWindow_DoesNotTrigger()
    {
        var h = await StartOnAc();
        for (var i = 0; i < 5; i++)
        {
            h.Display.ExternalConnected = false; await h.RunFor(4);
            h.Display.ExternalConnected = true; await h.RunFor(2);
        }
        Assert.Equal(RecoveryStatus.Normal, h.C.Status);
        Assert.Empty(h.Display.Launches);
        Assert.Null(h.Store.State);
        Assert.Contains(h.Log.Entries, e => e.Message.Contains("视为抖动"));
    }

    [Fact] // 用户按 Win+P 切到内屏：外接目标仍可用 → 不是显示器断电
    public async Task UserSwitchesToInternal_ExternalStillAvailable_NoTrigger()
    {
        var h = await StartOnAc();
        h.Display.Mode = DisplayMode.Internal;
        await h.RunFor(60);
        Assert.Equal(RecoveryStatus.Normal, h.C.Status);
        Assert.Null(h.Store.State);
    }

    [Fact]
    public async Task CompatibilityMode_IgnoresMonitorLoss()
    {
        var h = await StartOnAc(s => s.TriggerMode = TriggerMode.Compatibility);
        h.Display.ExternalConnected = false;
        await h.RunFor(120);
        Assert.Equal(RecoveryStatus.Normal, h.C.Status);
        Assert.Empty(h.Display.Launches);
    }

    [Fact]
    public async Task CompatibilityMode_StillHandlesPowerLoss()
    {
        var h = await StartOnAc(s => s.TriggerMode = TriggerMode.Compatibility);
        await h.C.OnPowerSourceChangedAsync(PowerSource.Battery);
        Assert.Equal(TriggerReason.PowerLoss, h.Store.State!.TriggerReason);
        Assert.Equal(DisplayMode.Internal, h.Display.Mode);
    }

    [Fact]
    public async Task MonitorLoss_PausedOrAutoSwitchDisabled_NoTrigger()
    {
        var h = await StartOnAc();
        await h.C.SetPausedAsync(true);
        h.Display.ExternalConnected = false;
        await h.RunFor(60);
        Assert.Empty(h.Display.Launches);

        var h2 = await StartOnAc(s => s.AutoSwitchOnPowerLoss = false);
        h2.Display.ExternalConnected = false;
        await h2.RunFor(60);
        Assert.Empty(h2.Display.Launches);
    }

    [Fact]
    public async Task QueryFailure_NeverTriggers()
    {
        var h = await StartOnAc();
        h.Display.QueryFails = true;
        await h.RunFor(60);
        Assert.Equal(RecoveryStatus.Normal, h.C.Status);
        Assert.Empty(h.Display.Launches);
    }

    [Fact] // 只拔笔记本电源（显示器仍亮）
    public async Task LaptopUnplugOnly_ProtectsInternal_RestoresOnlyAfterAcReturns()
    {
        var h = await StartOnAc();
        await h.C.OnPowerSourceChangedAsync(PowerSource.Battery);
        Assert.Equal(DisplayMode.Internal, h.Display.Mode);
        Assert.Equal(TriggerReason.PowerLoss, h.Store.State!.TriggerReason);
        await h.RunFor(300);                                       // 外接路径一直可用，但仍是电池
        Assert.Equal(RecoveryStatus.WaitingForExternal, h.C.Status);
        Assert.Single(h.Display.Launches);
        await h.C.OnPowerSourceChangedAsync(PowerSource.Ac);
        await h.RunFor(20);
        Assert.Equal(RecoveryStatus.Normal, h.C.Status);
        Assert.Equal(DisplayMode.External, h.Display.Mode);
    }

    [Fact] // 修复：显示器触发不能要求“市电恢复”事件
    public async Task MonitorLoss_PowerUnknown_StillRestores()
    {
        var h = await StartOnAc();
        await h.C.OnPowerSourceChangedAsync(PowerSource.Unknown);
        h.Display.ExternalConnected = false;
        await h.RunFor(12);
        Assert.Equal(RecoveryStatus.WaitingForExternal, h.C.Status);
        h.Display.ExternalConnected = true;
        await h.RunFor(20);
        Assert.Equal(RecoveryStatus.Normal, h.C.Status);
    }

    [Fact]
    public async Task MonitorLoss_ThenPowerLoss_UpgradesReason_RequiresAc()
    {
        var h = await StartOnAc();
        h.Display.ExternalConnected = false;
        await h.RunFor(12);
        Assert.Equal(TriggerReason.ExternalDisplayLost, h.Store.State!.TriggerReason);
        await h.C.OnPowerSourceChangedAsync(PowerSource.Battery);
        Assert.Equal(TriggerReason.PowerLoss, h.Store.State!.TriggerReason);
        h.Display.ExternalConnected = true;                       // 例如 UPS 供电的显示器
        await h.RunFor(60);
        Assert.Equal(RecoveryStatus.WaitingForExternal, h.C.Status); // 电池下不恢复
        await h.C.OnPowerSourceChangedAsync(PowerSource.Ac);
        await h.RunFor(20);
        Assert.Equal(RecoveryStatus.Normal, h.C.Status);
    }

    [Fact]
    public async Task MonitorLoss_WaitingHasNoTimeout_ButRetriesBounded()
    {
        var h = await StartOnAc();
        h.Display.ExternalConnected = false;
        await h.RunFor(12);
        await h.RunFor(8 * 3600);                                 // 显示器整晚关着
        Assert.Equal(RecoveryStatus.WaitingForExternal, h.C.Status);
        h.Display.Mode = DisplayMode.Internal;
        h.Display.SwitchHasNoEffect = true;
        h.Display.ExternalConnected = true;
        h.Display.Launches.Clear();
        await h.RunFor(600);
        Assert.Equal(RecoveryStatus.RecoveryFailed, h.C.Status);
        Assert.Equal(1 + h.Settings.MaxRetries, h.Display.Launches.Count(m => m == DisplayMode.External));
    }

    [Fact]
    public async Task OwnSwitchToInternal_DoesNotSelfTrigger()
    {
        var h = await StartOnAc();
        var r = await h.C.ManualSwitchToInternalAsync();
        Assert.True(r.Success);
        await h.RunFor(60);
        Assert.Equal(RecoveryStatus.Normal, h.C.Status);
        Assert.Null(h.Store.State);
    }

    // ───── 启动规则 ─────

    [Fact]
    public async Task Startup_MarkerButAlreadyPreferred_VerifiesAndClears()
    {
        var h = new Harness();
        h.Store.State = new RecoveryState { PreferredModeSnapshot = DisplayMode.External, StartedAt = h.Clock.Now, TriggerReason = TriggerReason.PowerLoss };
        h.Display.Mode = DisplayMode.External;
        await h.C.InitializeAsync(PowerSource.Ac);
        Assert.Equal(RecoveryStatus.Normal, h.C.Status);
        Assert.Null(h.Store.State);
        Assert.Empty(h.Display.Launches);
        Assert.Contains(h.Log.Entries, e => e.Message.Contains("启动时确认已处于首选模式"));
    }

    [Fact]
    public async Task Startup_PowerLossMarker_AlreadyPreferredButOnBattery_KeepsMarker()
    {
        var h = new Harness();
        h.Store.State = new RecoveryState { PreferredModeSnapshot = DisplayMode.External, StartedAt = h.Clock.Now };
        h.Display.Mode = DisplayMode.External;
        await h.C.InitializeAsync(PowerSource.Battery);
        Assert.NotNull(h.Store.State);
        Assert.Equal(DisplayMode.Internal, h.Display.Mode);
    }

    [Fact]
    public async Task Startup_DisplayLostMarker_MonitorStillOff_EnsuresInternalAndWaits()
    {
        var h = new Harness();
        h.Store.State = new RecoveryState { PreferredModeSnapshot = DisplayMode.External, StartedAt = h.Clock.Now, TriggerReason = TriggerReason.ExternalDisplayLost };
        h.Display.ExternalConnected = false;
        await h.C.InitializeAsync(PowerSource.Ac);
        Assert.Equal(RecoveryStatus.WaitingForExternal, h.C.Status);
        Assert.Equal(DisplayMode.Internal, h.Display.Query().Mode);
        h.Display.ExternalConnected = true;
        await h.RunFor(20);
        Assert.Equal(RecoveryStatus.Normal, h.C.Status);
    }

    [Fact]
    public async Task Startup_NoMarker_MonitorMissing_DoesNothing()
    {
        var h = new Harness();
        h.Display.ExternalConnected = false;
        await h.C.InitializeAsync(PowerSource.Ac);
        await h.RunFor(60); // 启动时外接本就不在：没有“活动 → 消失”的变化，不触发
        Assert.Equal(RecoveryStatus.Normal, h.C.Status);
        Assert.Empty(h.Display.Launches);
    }
}

public class ShutdownAttemptTests
{
    private static async Task<Harness> WaitingWithExternalBack(bool enable = true)
    {
        var h = new Harness();
        h.Settings.TryPreferredModeOnShutdown = enable;
        await h.C.InitializeAsync(PowerSource.Ac);
        await h.C.OnPowerSourceChangedAsync(PowerSource.Battery);
        await h.C.OnPowerSourceChangedAsync(PowerSource.Ac);
        await h.C.TickAsync(); // 外接可用，稳定计时刚开始，尚未恢复
        Assert.Equal(RecoveryStatus.WaitingForExternal, h.C.Status);
        h.Display.Launches.Clear();
        return h;
    }

    [Fact]
    public void Default_IsOff() => Assert.False(new AppSettings().TryPreferredModeOnShutdown);

    [Fact]
    public async Task Disabled_Skips_NoLaunch()
    {
        var h = await WaitingWithExternalBack(enable: false);
        var r = h.C.TryPreferredModeOnShutdown(TimeSpan.FromSeconds(2));
        Assert.False(r.Attempted);
        Assert.Empty(h.Display.Launches);
    }

    [Fact]
    public async Task Enabled_AttemptsOnce_NeverClearsMarker()
    {
        var h = await WaitingWithExternalBack();
        var r = h.C.TryPreferredModeOnShutdown(TimeSpan.FromSeconds(2));
        Assert.True(r.Attempted);
        Assert.True(r.Verified);
        Assert.Equal(DisplayMode.External, Assert.Single(h.Display.Launches));
        Assert.NotNull(h.Store.State);                               // 即使已确认也不清除
        Assert.True(h.Store.State!.Pending);
        Assert.NotNull(h.Store.State.LastShutdownAttemptAt);
        Assert.Contains("已调用", h.Store.State.LastShutdownAttemptResult);
        var again = h.C.TryPreferredModeOnShutdown(TimeSpan.FromSeconds(2));
        Assert.False(again.Attempted);                               // 只尝试一次
        Assert.Single(h.Display.Launches);
    }

    [Fact] // 下次开机：校验通过才清除
    public async Task NextBoot_AfterShutdownAttempt_VerifiesThenClears()
    {
        var h = await WaitingWithExternalBack();
        h.C.TryPreferredModeOnShutdown(TimeSpan.FromSeconds(2));
        h.Create();
        await h.C.InitializeAsync(PowerSource.Ac);
        Assert.Equal(RecoveryStatus.Normal, h.C.Status);
        Assert.Null(h.Store.State);
    }

    [Fact]
    public async Task NextBoot_ShutdownAttemptDidNotStick_KeepsWaiting()
    {
        var h = await WaitingWithExternalBack();
        h.Display.SwitchHasNoEffect = true;
        var r = h.C.TryPreferredModeOnShutdown(TimeSpan.FromSeconds(2));
        Assert.False(r.Verified);
        h.Display.SwitchHasNoEffect = false;
        h.Create();
        await h.C.InitializeAsync(PowerSource.Ac);
        Assert.Equal(RecoveryStatus.WaitingForExternal, h.C.Status);
        Assert.NotNull(h.Store.State);
    }

    [Fact]
    public async Task HangingSwitcher_ReturnsWithinBudget()
    {
        var h = await WaitingWithExternalBack();
        h.Display.Hang = true;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = h.C.TryPreferredModeOnShutdown(TimeSpan.FromMilliseconds(300));
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 2000, $"耗时 {sw.ElapsedMilliseconds} ms");
        Assert.Contains("时限内未返回", r.Message);
        Assert.NotNull(h.Store.State);
    }

    [Fact]
    public async Task OnBatteryAfterPowerLoss_Skips()
    {
        var h = new Harness();
        h.Settings.TryPreferredModeOnShutdown = true;
        await h.C.InitializeAsync(PowerSource.Ac);
        await h.C.OnPowerSourceChangedAsync(PowerSource.Battery);
        h.Display.Launches.Clear();
        var r = h.C.TryPreferredModeOnShutdown(TimeSpan.FromSeconds(1));
        Assert.False(r.Attempted);
        Assert.Contains("电源条件", r.Message);
        Assert.Empty(h.Display.Launches);
    }

    [Fact]
    public async Task NoExternal_Skips()
    {
        var h = await WaitingWithExternalBack();
        h.Display.ExternalConnected = false;
        var r = h.C.TryPreferredModeOnShutdown(TimeSpan.FromSeconds(1));
        Assert.False(r.Attempted);
        Assert.Empty(h.Display.Launches);
    }

    [Fact]
    public async Task NoMarker_Skips()
    {
        var h = new Harness();
        h.Settings.TryPreferredModeOnShutdown = true;
        await h.C.InitializeAsync(PowerSource.Ac);
        Assert.False(h.C.TryPreferredModeOnShutdown(TimeSpan.FromSeconds(1)).Attempted);
        Assert.Empty(h.Display.Launches);
    }
}

public class CompatibilityJsonTests
{
    [Fact]
    public void V1SettingsFile_LoadsWithNewDefaults()
    {
        using var d = new TempDir();
        var path = Path.Combine(d.Path, "settings.json");
        File.WriteAllText(path, """
        { "Version": 1, "AutoSwitchOnPowerLoss": true, "AutoRestoreOnPowerReturn": true, "StartWithWindows": true,
          "PreferredMode": "Extend", "StabilitySeconds": 20, "RecoveryTimeoutMinutes": 30, "RetryIntervalSeconds": 20,
          "MaxRetries": 3, "ShowNotifications": true, "RetainLogs": true, "VerifyDelaySeconds": 4,
          "PowerLossConfirmSeconds": 2, "DisplaySwitchArgStyle": "Auto" }
        """);
        var r = new SettingsStore(path).Load();
        Assert.Equal(LoadStatus.Loaded, r.Status);
        Assert.Equal(DisplayMode.Extend, r.Value!.PreferredMode);
        Assert.Equal(20, r.Value.StabilitySeconds);
        Assert.Equal(TriggerMode.Recommended, r.Value.TriggerMode);
        Assert.Equal(8, r.Value.ExternalLostConfirmSeconds);
        Assert.False(r.Value.TryPreferredModeOnShutdown);
    }

    [Fact]
    public void V1RecoveryState_LoadsAsPowerLoss()
    {
        using var d = new TempDir();
        var path = Path.Combine(d.Path, "recovery-state.json");
        File.WriteAllText(path, """
        { "Version": 1, "Pending": true, "PreferredModeSnapshot": "External", "ModeBeforePowerLoss": "External",
          "StartedAt": "2026-10-12T23:10:00+08:00", "LastAttemptAt": null, "AttemptCount": 0, "LastError": null }
        """);
        var r = new RecoveryStateStore(path).Load();
        Assert.Equal(LoadStatus.Loaded, r.Status);
        Assert.Equal(TriggerReason.PowerLoss, r.Value!.TriggerReason);
        Assert.Null(r.Value.LastShutdownAttemptAt);
    }

    [Fact]
    public void V2Fields_RoundTrip_AsStrings()
    {
        using var d = new TempDir();
        var path = Path.Combine(d.Path, "settings.json");
        new SettingsStore(path).Save(new AppSettings { TriggerMode = TriggerMode.Compatibility, TryPreferredModeOnShutdown = true, ExternalLostConfirmSeconds = 15 });
        Assert.Contains("\"Compatibility\"", File.ReadAllText(path));
        var r = new SettingsStore(path).Load().Value!;
        Assert.Equal(TriggerMode.Compatibility, r.TriggerMode);
        Assert.True(r.TryPreferredModeOnShutdown);
        Assert.Equal(15, r.ExternalLostConfirmSeconds);
        var sp = Path.Combine(d.Path, "recovery-state.json");
        new RecoveryStateStore(sp).Save(new RecoveryState { TriggerReason = TriggerReason.ExternalDisplayLost });
        Assert.Contains("\"ExternalDisplayLost\"", File.ReadAllText(sp));
    }

    [Fact]
    public void ExternalLostConfirm_Clamped()
    {
        var s = new AppSettings { ExternalLostConfirmSeconds = 0 };
        s.Normalize();
        Assert.Equal(2, s.ExternalLostConfirmSeconds);
    }
}
