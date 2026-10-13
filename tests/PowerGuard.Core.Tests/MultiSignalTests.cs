using PowerGuard.Core;

namespace PowerGuard.Core.Tests;

/// <summary>v2：多信号触发、统一触发入口。外接“活动路径”与“可用目标”严格区分。</summary>
public class MultiSignalTests
{
    private static async Task<Harness> StartOnAc(TriggerMode mode = TriggerMode.Recommended, Action<AppSettings>? cfg = null)
    {
        var h = new Harness();
        h.Settings.TriggerMode = mode;
        cfg?.Invoke(h.Settings);
        await h.C.InitializeAsync(PowerSource.Ac);
        await h.RunFor(4); // 建立“仅第二屏幕、外接活动”的基线快照
        return h;
    }

    // ───── 推荐模式：市电转电池 ─────

    [Fact] // 只拔笔记本电源，外接活动路径仍在 → 只记录不切屏（问题 #1）
    public async Task Recommended_AcToBattery_ExternalStillActive_NoSwitch()
    {
        var h = await StartOnAc();
        await h.C.OnPowerSourceChangedAsync(PowerSource.Battery);
        await h.RunFor(300);
        Assert.Equal(RecoveryStatus.Normal, h.C.Status);
        Assert.Empty(h.Display.Launches);
        Assert.Null(h.Store.State);
        Assert.Equal(DisplayMode.External, h.Display.Query().Mode);
        Assert.Contains(h.Log.Entries, e => e.Message.Contains("外接活动显示路径仍存在"));
    }

    [Fact] // 市电转电池后在确认窗口内恢复 → 抖动
    public async Task Recommended_AcToBattery_ThenAcWithinConfirm_IsFlicker()
    {
        var h = await StartOnAc();
        h.Display.ExternalConnected = false; // 即使外接也没了，市电抖动本身不构成证据
        var once = false;
        h.Delay.OnDelay = d => { if (!once) { once = true; _ = h.C.OnPowerSourceChangedAsync(PowerSource.Ac); } };
        await h.C.OnPowerSourceChangedAsync(PowerSource.Battery);
        h.Delay.OnDelay = null;
        Assert.Contains(h.Log.Entries, e => e.Message.Contains("瞬时抖动"));
        Assert.DoesNotContain(h.Log.Entries, e => e.Category == LogCategory.Trigger && e.Message.Contains("触发保护"));
    }

    [Fact] // 市电转电池 + 外接活动路径消失 → CombinedEvidence
    public async Task Recommended_AcToBattery_ExternalActiveGone_ProtectsCombined()
    {
        var h = await StartOnAc();
        h.Display.ExternalConnected = false;
        await h.C.OnPowerSourceChangedAsync(PowerSource.Battery);
        Assert.Equal(RecoveryStatus.WaitingForExternal, h.C.Status);
        Assert.Equal(TriggerReason.CombinedEvidence, h.Store.State!.TriggerReason);
        Assert.Equal(DisplayMode.Internal, h.Display.Query().Mode);
        Assert.Empty(h.Display.Launches); // Windows 已回落到内屏 → 不重复发命令
        h.Display.ExternalConnected = true;
        await h.RunFor(60);
        Assert.Equal(RecoveryStatus.WaitingForExternal, h.C.Status); // 含市电证据：电池下不恢复
        await h.C.OnPowerSourceChangedAsync(PowerSource.Ac);
        await h.RunFor(20);
        Assert.Equal(RecoveryStatus.Normal, h.C.Status);
        Assert.Equal(DisplayMode.External, h.Display.Mode);
        Assert.Null(h.Store.State);
    }

    [Fact] // 市电转电池后拓扑查询失败 → 不当作断电证据
    public async Task Recommended_AcToBattery_QueryFails_NoTrigger()
    {
        var h = await StartOnAc();
        h.Display.QueryFails = true;
        await h.C.OnPowerSourceChangedAsync(PowerSource.Battery);
        await h.RunFor(60);
        Assert.Equal(RecoveryStatus.Normal, h.C.Status);
        Assert.Null(h.Store.State);
        Assert.Empty(h.Display.Launches);
        Assert.Equal(3, h.Log.Entries.Count(e => e.Message.Contains("查询显示拓扑失败")));
    }

    [Fact] // 外接活动仍在时只记录；之后显示器真的断了 → CombinedEvidence
    public async Task Recommended_AcToBattery_ThenMonitorDiesLater_CombinedEvidence()
    {
        var h = await StartOnAc();
        await h.C.OnPowerSourceChangedAsync(PowerSource.Battery);
        Assert.Null(h.Store.State);
        h.Display.ExternalConnected = false;
        await h.RunFor(12);
        Assert.Equal(TriggerReason.CombinedEvidence, h.Store.State!.TriggerReason);
    }

    // ───── 外接活动路径消失（两种模式） ─────

    [Theory] // 市电一直在，只关显示器
    [InlineData(TriggerMode.Recommended)]
    [InlineData(TriggerMode.Compatibility)]
    public async Task AcStable_ExternalActiveLost_ProtectsAfterConfirm_AndRestoresOnAc(TriggerMode mode)
    {
        var h = await StartOnAc(mode);
        h.Display.ExternalConnected = false;
        await h.RunFor(4);
        Assert.Equal(RecoveryStatus.Normal, h.C.Status);              // 确认窗口内
        Assert.NotNull(h.C.Snapshot().ExternalLostSince);
        await h.RunFor(8);
        Assert.Equal(RecoveryStatus.WaitingForExternal, h.C.Status);
        Assert.Equal(TriggerReason.ExternalDisplayLost, h.Store.State!.TriggerReason);
        h.Display.ExternalConnected = true;                           // 市电从未变化，不能阻塞恢复
        await h.RunFor(20);
        Assert.Equal(RecoveryStatus.Normal, h.C.Status);
        Assert.Equal(DisplayMode.External, h.Display.Mode);
        Assert.Null(h.Store.State);
    }

    [Fact]
    public async Task ExternalActiveLost_ConfirmRequiresRequery()
    {
        var h = await StartOnAc();
        h.Display.ExternalConnected = false;
        await h.RunFor(8);
        Assert.Null(h.Store.State);
        var before = h.Display.QueryCount;
        await h.RunFor(2);
        Assert.True(h.Display.QueryCount - before >= 2); // Tick 查询 + 确认重新查询
        Assert.NotNull(h.Store.State);
    }

    [Fact] // 路径在确认窗口内恢复 → 取消
    public async Task PathReturnsWithinWindow_Cancels()
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

    [Fact] // 查询失败 / 电源未知 → 不触发
    public async Task QueryFailureOrUnknownPower_NeverTriggers()
    {
        var h = await StartOnAc();
        h.Display.QueryFails = true;
        await h.RunFor(60);
        await h.C.OnPowerSourceChangedAsync(PowerSource.Unknown);
        await h.RunFor(60);
        Assert.Equal(RecoveryStatus.Normal, h.C.Status);
        Assert.Empty(h.Display.Launches);
        Assert.Null(h.Store.State);
    }

    [Fact] // 可用目标 > 0 但活动路径 = 0：不当作“外接活动”，也不当作显示器断电（多为 Win+P）
    public async Task AvailableButNotActive_NotTreatedAsActive_NorAsLoss()
    {
        var h = await StartOnAc();
        h.Display.Mode = DisplayMode.Internal;            // 用户 Win+P 切到仅电脑屏幕，显示器仍接着
        await h.RunFor(60);
        var t = h.Display.Query();
        Assert.Equal(0, t.ExternalActiveCount);
        Assert.Equal(1, t.ExternalAvailableCount);
        Assert.Equal(RecoveryStatus.Normal, h.C.Status);
        Assert.Null(h.Store.State);
        // 推荐模式下此时市电转电池：可用目标不等于活动路径 → 视为外接已无画面 → CombinedEvidence
        await h.C.OnPowerSourceChangedAsync(PowerSource.Battery);
        Assert.Equal(TriggerReason.CombinedEvidence, h.Store.State!.TriggerReason);
        Assert.Empty(h.Display.Launches);                  // 已是仅内屏，不重复发命令
    }

    [Fact] // 扩展模式且内屏活动：不强制仅内屏
    public async Task ExtendWithInternalActive_NoForcedInternal()
    {
        var h = new Harness();
        h.Settings.TriggerMode = TriggerMode.Compatibility;
        h.Display.Mode = DisplayMode.Extend;
        await h.C.InitializeAsync(PowerSource.Ac);
        await h.C.OnPowerSourceChangedAsync(PowerSource.Battery);
        Assert.Empty(h.Display.Launches);
        Assert.Equal(DisplayMode.Extend, h.Display.Query().Mode);
        Assert.Equal(RecoveryStatus.WaitingForExternal, h.C.Status);
        Assert.Contains(h.Log.Entries, e => e.Message.Contains("不发送切屏命令"));
        // 扩展模式下外接断开不满足“上次为仅第二屏幕”，不会触发显示器断电检测
        var h2 = new Harness(); h2.Settings.TriggerMode = TriggerMode.Recommended; h2.Display.Mode = DisplayMode.Extend;
        await h2.C.InitializeAsync(PowerSource.Ac); await h2.RunFor(4);
        h2.Display.ExternalConnected = false; await h2.RunFor(60);
        Assert.Null(h2.Store.State);
    }

    [Fact] // 已是仅内屏：不发送多余命令
    public async Task AlreadyInternalOnly_NoRedundantCommand()
    {
        var h = new Harness();
        h.Display.Mode = DisplayMode.Internal;
        await h.C.InitializeAsync(PowerSource.Ac);
        await h.C.OnPowerSourceChangedAsync(PowerSource.Battery); // 兼容模式
        Assert.Empty(h.Display.Launches);
        Assert.NotNull(h.Store.State);
    }

    [Fact] // 近乎同时的两个证据 → 一个流程、一个标记
    public async Task NearSimultaneousEvents_OneFlowOneMarker()
    {
        var h = await StartOnAc(TriggerMode.Compatibility);
        h.Display.ExternalConnected = false;
        await h.C.TickAsync();                                      // 进入外接路径消失确认
        await Task.WhenAll(h.C.OnPowerSourceChangedAsync(PowerSource.Battery), h.C.TickAsync());
        var started = h.Store.State!.StartedAt;
        await h.RunFor(30);
        Assert.Equal(RecoveryStatus.WaitingForExternal, h.C.Status);
        Assert.Equal(started, h.Store.State!.StartedAt);           // 没有第二个流程/标记
        Assert.Equal(1, h.Log.Entries.Count(e => e.Message.StartsWith("触发保护")));
        Assert.True(h.Display.Launches.Count(m => m == DisplayMode.Internal) <= 1);
    }

    [Fact] // 显示器触发后又市电转电池 → 合并为 CombinedEvidence，需市电恢复
    public async Task DisplayLostThenAcToBattery_MergesToCombined()
    {
        var h = await StartOnAc(TriggerMode.Compatibility);
        h.Display.ExternalConnected = false;
        await h.RunFor(12);
        Assert.Equal(TriggerReason.ExternalDisplayLost, h.Store.State!.TriggerReason);
        await h.C.OnPowerSourceChangedAsync(PowerSource.Battery);
        Assert.Equal(TriggerReason.CombinedEvidence, h.Store.State!.TriggerReason);
        h.Display.ExternalConnected = true;
        await h.RunFor(60);
        Assert.Equal(RecoveryStatus.WaitingForExternal, h.C.Status);
        await h.C.OnPowerSourceChangedAsync(PowerSource.Ac);
        await h.RunFor(20);
        Assert.Equal(RecoveryStatus.Normal, h.C.Status);
    }

    [Fact] // 兼容模式：外接仍活动也立即切屏（v1 行为）
    public async Task Compatibility_AcToBattery_SwitchesEvenWithExternalActive()
    {
        var h = await StartOnAc(TriggerMode.Compatibility);
        await h.C.OnPowerSourceChangedAsync(PowerSource.Battery);
        Assert.Equal(TriggerReason.AcToBattery, h.Store.State!.TriggerReason);
        Assert.Equal(DisplayMode.Internal, Assert.Single(h.Display.Launches));
        Assert.Equal(DisplayMode.Internal, h.Display.Mode);
    }

    [Fact]
    public async Task MonitorLoss_PausedOrAutoProtectionDisabled_NoTrigger()
    {
        var h = await StartOnAc();
        await h.C.SetPausedAsync(true);
        h.Display.ExternalConnected = false;
        await h.RunFor(60);
        Assert.Empty(h.Display.Launches);
        Assert.Null(h.Store.State);

        var h2 = await StartOnAc(cfg: s => s.AutoSwitchOnPowerLoss = false);
        h2.Display.ExternalConnected = false;
        await h2.RunFor(60);
        Assert.Null(h2.Store.State);
    }

    [Fact] // 显示器触发的等待同样受恢复超时约束：RecoveryFailed、保留标记、不再切屏
    public async Task DisplayLost_TimeoutApplies_MarkerKept_NoInfiniteSwitching()
    {
        var h = await StartOnAc();
        h.Display.ExternalConnected = false;
        await h.RunFor(12);
        await h.RunFor(11 * 60);
        Assert.Equal(RecoveryStatus.RecoveryFailed, h.C.Status);
        Assert.NotNull(h.Store.State);
        var n = h.Display.Launches.Count;
        await h.RunFor(3600);
        Assert.Equal(n, h.Display.Launches.Count);
        // 用户手动恢复仍可用
        h.Display.ExternalConnected = true;
        Assert.True((await h.C.ManualRestorePreferredAsync()).Success);
        Assert.Null(h.Store.State);
    }

    [Fact] // 稳定计时在可用目标数变化时重置
    public async Task Stability_ResetsOnSignificantChange()
    {
        var h = await StartOnAc();
        h.Display.ExternalConnected = false;
        await h.RunFor(12);
        h.Display.ExternalConnected = true;
        await h.RunFor(10);
        h.Display.Override = new DisplayTopology(true, DisplayMode.Internal, true, true, 2, 0, "two");
        await h.RunFor(10);
        Assert.Equal(RecoveryStatus.WaitingForExternal, h.C.Status);
        Assert.Contains(h.Log.Entries, e => e.Message.Contains("可用目标数变化"));
        h.Display.Override = null;
    }

    [Fact] // 本程序/手动切屏不自触发
    public async Task OwnOrManualSwitch_DoesNotSelfTrigger()
    {
        var h = await StartOnAc();
        Assert.True((await h.C.ManualSwitchToInternalAsync()).Success);
        await h.RunFor(60);
        Assert.Equal(RecoveryStatus.Normal, h.C.Status);
        Assert.Null(h.Store.State);
    }

    // ───── 启动规则 ─────

    [Fact]
    public async Task Startup_MarkerButAlreadyPreferred_VerifiesAndClears()
    {
        var h = new Harness();
        h.Store.State = new RecoveryState { PreferredModeSnapshot = DisplayMode.External, StartedAt = h.Clock.Now, TriggerReason = TriggerReason.AcToBattery };
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
        Assert.StartsWith("已尝试", h.Store.State.LastShutdownAttemptResult);
        Assert.Contains(h.Log.Entries, e => e.Category == LogCategory.Shutdown && e.Message.Contains("已尝试（非已恢复）"));
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
        Assert.Equal(TriggerReason.AcToBattery, r.Value!.TriggerReason);
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
    public void LegacyPowerLossString_ReadsAsAcToBattery_AndCombinedRoundTrips()
    {
        using var d = new TempDir();
        var path = Path.Combine(d.Path, "recovery-state.json");
        File.WriteAllText(path, """{ "Pending": true, "PreferredModeSnapshot": "External", "TriggerReason": "PowerLoss" }""");
        Assert.Equal(TriggerReason.AcToBattery, new RecoveryStateStore(path).Load().Value!.TriggerReason);
        new RecoveryStateStore(path).Save(new RecoveryState { TriggerReason = TriggerReason.CombinedEvidence });
        Assert.Contains("\"CombinedEvidence\"", File.ReadAllText(path));
        Assert.Equal(TriggerReason.CombinedEvidence, new RecoveryStateStore(path).Load().Value!.TriggerReason);
        File.WriteAllText(path, """{ "Pending": true, "TriggerReason": "Bogus" }""");
        Assert.Equal(LoadStatus.Corrupt, new RecoveryStateStore(path).Load().Status);
    }

    [Fact]
    public void ExternalLostConfirm_Clamped()
    {
        var s = new AppSettings { ExternalLostConfirmSeconds = 0 };
        s.Normalize();
        Assert.Equal(2, s.ExternalLostConfirmSeconds);
    }
}
