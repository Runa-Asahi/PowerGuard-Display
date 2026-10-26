using PowerGuard.Core;

namespace PowerGuard.Core.Tests;

/// <summary>
/// 复现 2026-10-26 23:33 Lenovo Y7000 实机日志：
/// 23:33:08 市电 → 电池；23:33:10 保存标记；23:33:11 拓扑读到“仅电脑屏幕、外接可用=0”（外接瞬时消失，Windows 自行回落），
/// 旧逻辑记录“当前已是仅电脑屏幕，无需切换”且未下发任何命令；23:33:13 Windows 重新识别仍保留 EDID 的外接显示器，
/// 按数据库恢复“仅第二屏幕”，内屏黑屏约 4.5 分钟且程序不再处理。
/// </summary>
public class PowerLossRegressionTests
{
    /// <summary>按实机日志编排：断电确认期间外接消失，切屏后 2 秒 Windows 自动切回外接。</summary>
    private static async Task<Harness> ReproduceAsync()
    {
        var h = new Harness();
        h.Display.Mode = DisplayMode.External;
        await h.C.InitializeAsync(PowerSource.Ac);
        h.Display.ExternalConnected = false;          // 23:33:11 外接瞬时消失 → Windows 回落到内屏
        await h.C.OnPowerSourceChangedAsync(PowerSource.Battery);
        // 23:33:13 显示器被重新识别（仍保留 EDID），Windows 按数据库切回“仅第二屏幕”
        h.Display.ExternalConnected = true;
        h.Display.Mode = DisplayMode.External;
        return h;
    }

    [Fact]
    public async Task PowerLoss_AlwaysIssuesInternalSwitch_EvenIfAlreadyInternal()
    {
        var h = new Harness();
        h.Display.Mode = DisplayMode.External;
        await h.C.InitializeAsync(PowerSource.Ac);
        h.Display.ExternalConnected = false;
        await h.C.OnPowerSourceChangedAsync(PowerSource.Battery);
        Assert.Contains(DisplayMode.Internal, h.Display.Launches); // 旧版本这里为空
        Assert.DoesNotContain(h.Log.Entries, e => e.Message.Contains("无需切换"));
    }

    [Fact]
    public async Task WindowsRevertsToExternalOnBattery_GuardReassertsInternal()
    {
        var h = await ReproduceAsync();
        var before = h.Display.Launches.Count;
        await h.RunFor(10);
        Assert.Equal(DisplayMode.Internal, h.Display.Mode);           // 内屏重新点亮
        Assert.True(h.Display.Launches.Count > before);
        Assert.All(h.Display.Launches, m => Assert.Equal(DisplayMode.Internal, m)); // 断电期间绝不切外接
        Assert.Contains(h.Log.Entries, e => e.Message.Contains("内屏守护"));
        Assert.Equal(RecoveryStatus.WaitingForExternal, h.C.Status);
    }

    [Fact]
    public async Task RepeatedReverts_DuringLongOutage_AreAllCorrected()
    {
        var h = await ReproduceAsync();
        for (var i = 0; i < 5; i++)
        {
            await h.RunFor(10);
            Assert.Equal(DisplayMode.Internal, h.Display.Mode);
            h.Display.Mode = DisplayMode.External; // Windows 再次热插拔切回
        }
        await h.RunFor(10);
        Assert.Equal(DisplayMode.Internal, h.Display.Mode);
    }

    [Fact]
    public async Task Guard_IsRateLimited_WhenSwitchHasNoEffect()
    {
        var h = await ReproduceAsync();
        h.Display.SwitchHasNoEffect = true;
        h.Display.Launches.Clear();
        await h.RunFor(600); // 10 分钟
        // 每次守护最多 2 次尝试；前 6 次间隔 ≥5 秒（实际受校验延迟限制），之后每 60 秒一次
        Assert.InRange(h.Display.Launches.Count, 1, 2 * (RecoveryCoordinator.GuardFastAttempts + 10 + 1));
        Assert.All(h.Display.Launches, m => Assert.Equal(DisplayMode.Internal, m));
    }

    [Fact]
    public async Task SecondAttempt_UsesAlternateSwitchMethod()
    {
        var h = new Harness();
        await h.C.InitializeAsync(PowerSource.Ac);
        h.Display.SwitchHasNoEffect = true;
        await h.C.OnPowerSourceChangedAsync(PowerSource.Battery);
        Assert.Equal([false, true], h.Display.Alternates.Take(2));
    }

    [Fact]
    public async Task NeverAutoRestores_WhileOnBattery_EvenIfExternalDetectedForLong()
    {
        var h = await ReproduceAsync();
        await h.RunFor(45 * 60);
        Assert.DoesNotContain(DisplayMode.External, h.Display.Launches);
        Assert.Equal(DisplayMode.Internal, h.Display.Mode);
        Assert.NotNull(h.Store.State);
        Assert.Equal(RecoveryStatus.WaitingForExternal, h.C.Status); // 电池期间不计恢复超时
    }

    [Fact]
    public async Task AfterPowerReturns_RestoresExternal_AndGuardStops()
    {
        var h = await ReproduceAsync();
        await h.RunFor(10);
        await h.C.OnPowerSourceChangedAsync(PowerSource.Ac);
        await h.RunFor(30);
        Assert.Equal(DisplayMode.External, h.Display.Mode);
        Assert.Equal(RecoveryStatus.Normal, h.C.Status);
        Assert.Null(h.Store.State);
        var n = h.Display.Launches.Count;
        await h.RunFor(60);
        Assert.Equal(n, h.Display.Launches.Count); // 来电恢复后守护不再动作
    }

    [Fact]
    public async Task Paused_DuringOutage_GuardDoesNotFightUser()
    {
        var h = await ReproduceAsync();
        await h.C.SetPausedAsync(true);
        var n = h.Display.Launches.Count;
        await h.RunFor(30);
        Assert.Equal(n, h.Display.Launches.Count);
    }

    [Fact]
    public async Task ShutdownDuringOutage_DoesNotSwitchToExternal()
    {
        var h = await ReproduceAsync();
        await h.RunFor(10);
        var r = await h.C.PrepareForShutdownAsync(TimeSpan.FromSeconds(4));
        Assert.False(r.Attempted);
        Assert.Equal(DisplayMode.Internal, h.Display.Mode);
    }
}

public class AlertTests
{
    [Fact]
    public async Task InternalSwitchFailure_RaisesAlert()
    {
        var h = new Harness();
        var alerts = new List<string>();
        h.C.Alert += (t, _) => alerts.Add(t);
        await h.C.InitializeAsync(PowerSource.Ac);
        h.Display.SwitchHasNoEffect = true;
        await h.C.OnPowerSourceChangedAsync(PowerSource.Battery);
        Assert.Contains("无法切回笔记本内屏", alerts);
    }

    [Fact]
    public async Task PersistentReverts_RaiseBackoffAlertOnce()
    {
        var h = new Harness();
        var alerts = new List<string>();
        h.C.Alert += (t, _) => alerts.Add(t);
        await h.C.InitializeAsync(PowerSource.Ac);
        await h.C.OnPowerSourceChangedAsync(PowerSource.Battery);
        h.Display.SwitchHasNoEffect = true;
        h.Display.Mode = DisplayMode.External;
        await h.RunFor(600);
        Assert.Equal(1, alerts.Count(a => a == "内屏反复被切走"));
        Assert.True(h.C.Snapshot().GuardAttempts > RecoveryCoordinator.GuardFastAttempts);
    }
}

public sealed class FakeProbe : IMonitorProbe
{
    public Queue<MonitorResponse> Script { get; } = new();
    public MonitorResponse Default { get; set; } = MonitorResponse.Responding;
    public int Calls { get; private set; }
    public MonitorResponse ProbeActiveExternal() { Calls++; return Script.Count > 0 ? Script.Dequeue() : Default; }
}

public class V12CoherenceTests
{
    private static (Harness h, FakeProbe p, RecoveryCoordinator c) WithProbe(bool enabled = true)
    {
        var h = new Harness();
        h.Settings = h.Settings with { VerifyRestoreWithDdc = enabled };
        var p = new FakeProbe();
        var c = new RecoveryCoordinator(() => h.Settings, h.Store, h.Display, h.Display, h.Clock, new FakeDelay(h.Clock), h.Log, p);
        return (h, p, c);
    }

    private static async Task Run(Harness h, RecoveryCoordinator c, int seconds)
    {
        for (var i = 0; i < seconds; i += 2) { h.Clock.Advance(TimeSpan.FromSeconds(2)); await c.TickAsync(); }
    }

    [Fact]
    public async Task Ddc_Disabled_ByDefault_NeverProbed()
    {
        var (h, p, c) = WithProbe(enabled: false);
        await c.InitializeAsync(PowerSource.Ac);
        await Run(h, c, 120);
        Assert.Equal(0, p.Calls);
    }

    [Fact]
    public async Task Ddc_LearnedSupport_RestoreToDeadMonitor_RevertsOnce_ThenRestoresWithoutDdc()
    {
        var (h, p, c) = WithProbe();
        await c.InitializeAsync(PowerSource.Ac);
        await Run(h, c, 4);                         // 正常使用时学到支持 DDC
        Assert.True(c.Snapshot().DdcSupported);
        await c.OnPowerSourceChangedAsync(PowerSource.Battery);
        await c.OnPowerSourceChangedAsync(PowerSource.Ac);
        p.Default = MonitorResponse.NotResponding;  // 显示器其实没电（EDID 仍在）
        await Run(h, c, 20);
        Assert.Equal(DisplayMode.Internal, h.Display.Mode);
        Assert.Equal(RecoveryStatus.WaitingForExternal, c.Status);
        Assert.NotNull(h.Store.State);
        await Run(h, c, 30);                        // 本流程不再用 DDC 回退：回到已验证的行为
        Assert.Equal(DisplayMode.External, h.Display.Mode);
        Assert.Equal(RecoveryStatus.Normal, c.Status);
    }

    [Fact]
    public async Task Ddc_UnsupportedOrInconclusive_DoesNotChangeBehavior()
    {
        var (h, p, c) = WithProbe();
        p.Default = MonitorResponse.Unsupported;
        await c.InitializeAsync(PowerSource.Ac);
        await Run(h, c, 4);
        Assert.False(c.Snapshot().DdcSupported);
        await c.OnPowerSourceChangedAsync(PowerSource.Battery);
        await c.OnPowerSourceChangedAsync(PowerSource.Ac);
        p.Default = MonitorResponse.NotResponding;
        await Run(h, c, 30);
        Assert.Equal(DisplayMode.External, h.Display.Mode); // 未确认支持：不使用 DDC 判定
    }

    [Fact]
    public async Task LidClosed_GuardWaits_LidOpen_ReassertsImmediately()
    {
        var h = new Harness();
        await h.C.InitializeAsync(PowerSource.Ac);
        await h.C.OnPowerSourceChangedAsync(PowerSource.Battery);
        await h.C.OnLidChangedAsync(false);
        h.Display.Mode = DisplayMode.External;
        var n = h.Display.Launches.Count;
        await h.RunFor(30);
        Assert.Equal(n, h.Display.Launches.Count);
        Assert.Contains("盖子已合上", StatusText.Detail(h.C.Snapshot()));
        await h.C.OnLidChangedAsync(true);
        Assert.Equal(DisplayMode.Internal, h.Display.Mode);
    }

    [Fact]
    public async Task Headline_IsConsistentAcrossStates()
    {
        var h = new Harness();
        await h.C.InitializeAsync(PowerSource.Ac);
        Assert.Equal("正常运行", StatusText.Headline(h.C.Snapshot()));
        await h.C.OnPowerSourceChangedAsync(PowerSource.Battery);
        Assert.Equal("断电保护中（使用内屏）", StatusText.Headline(h.C.Snapshot()));
        await h.C.OnPowerSourceChangedAsync(PowerSource.Ac);
        Assert.Equal("已来电，等待外接显示器", StatusText.Headline(h.C.Snapshot()));
        await h.C.SetPausedAsync(true, TimeSpan.FromHours(1));
        Assert.StartsWith("已暂停（", StatusText.Headline(h.C.Snapshot()));
    }

    [Fact]
    public async Task ManualRestore_OnBattery_ClearsFlow_GuardDoesNotFightUser()
    {
        var h = new Harness();
        await h.C.InitializeAsync(PowerSource.Ac);
        await h.C.OnPowerSourceChangedAsync(PowerSource.Battery);
        var r = await h.C.ManualRestorePreferredAsync(); // 用户明确要求（例如显示器接了 UPS）
        Assert.True(r.Success);
        var n = h.Display.Launches.Count;
        await h.RunFor(60);
        Assert.Equal(n, h.Display.Launches.Count);
        Assert.Equal(DisplayMode.External, h.Display.Mode);
    }

    [Fact]
    public void SettingsV2_MigratesToV3_WithSafeDefaults()
    {
        var s = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("""{ "Version": 2, "RestoreOnShutdown": false }""", JsonFileStore<AppSettings>.Options)!;
        Assert.True(s.Normalize());
        Assert.Equal(3, s.Version);
        Assert.False(s.VerifyRestoreWithDdc);
        Assert.True(s.RelaunchAfterCrash);
        Assert.False(s.RestoreOnShutdown);
    }
}

public class CrashRelaunchPolicyTests
{
    [Fact]
    public void AllowsThreePerHour()
    {
        var now = new DateTimeOffset(2026, 10, 27, 0, 0, 0, TimeSpan.FromHours(8));
        var hist = new List<DateTimeOffset>();
        for (var i = 0; i < 3; i++) { Assert.True(CrashRelaunchPolicy.ShouldRelaunch(hist, now)); hist.Add(now); now += TimeSpan.FromMinutes(5); }
        Assert.False(CrashRelaunchPolicy.ShouldRelaunch(hist, now));
        Assert.True(CrashRelaunchPolicy.ShouldRelaunch(hist, now + TimeSpan.FromHours(1)));
    }
}

public class DdcManualTests
{
    [Fact]
    public async Task Ddc_ManualRestore_IsNeverOverridden()
    {
        var h = new Harness();
        h.Settings = h.Settings with { VerifyRestoreWithDdc = true };
        var p = new FakeProbe();
        var c = new RecoveryCoordinator(() => h.Settings, h.Store, h.Display, h.Display, h.Clock, new FakeDelay(h.Clock), h.Log, p);
        await c.InitializeAsync(PowerSource.Ac);
        for (var i = 0; i < 3; i++) { h.Clock.Advance(TimeSpan.FromSeconds(2)); await c.TickAsync(); }
        await c.OnPowerSourceChangedAsync(PowerSource.Battery);
        await c.OnPowerSourceChangedAsync(PowerSource.Ac);
        p.Default = MonitorResponse.NotResponding;
        var r = await c.ManualRestorePreferredAsync();
        Assert.True(r.Success);
        Assert.Equal(DisplayMode.External, h.Display.Mode);
    }
}

public class StartupAdviceTests
{
    [Fact] public void Unknown_NoWarning() { Assert.Null(StartupAdvice.Warning(null)); Assert.Null(StartupAdvice.Warning(new(false, false, null, false, "err"))); }
    [Fact] public void Missing_Warns() => Assert.Contains("未开启登录自启动", StartupAdvice.Warning(new(false, false, null, false, null)));
    [Fact] public void Disabled_Warns() => Assert.Contains("已被禁用", StartupAdvice.Warning(new(true, false, "x", true, null)));
    [Fact] public void OtherExe_Warns() => Assert.Contains("其他位置", StartupAdvice.Warning(new(true, true, "x", false, null)));
    [Fact] public void Ok_NoWarning() => Assert.Null(StartupAdvice.Warning(new(true, true, "x", true, null)));
}

public class DdcAlreadyPreferredTests
{
    [Fact]
    public async Task Ddc_WindowsAlreadyOnDeadExternal_RevertsToInternal()
    {
        var h = new Harness();
        h.Settings = h.Settings with { VerifyRestoreWithDdc = true };
        var p = new FakeProbe();
        var c = new RecoveryCoordinator(() => h.Settings, h.Store, h.Display, h.Display, h.Clock, new FakeDelay(h.Clock), h.Log, p);
        await c.InitializeAsync(PowerSource.Ac);
        for (var i = 0; i < 3; i++) { h.Clock.Advance(TimeSpan.FromSeconds(2)); await c.TickAsync(); }
        await c.OnPowerSourceChangedAsync(PowerSource.Battery);
        await c.OnPowerSourceChangedAsync(PowerSource.Ac);
        h.Display.Mode = DisplayMode.External; // Windows 自己切回了外接
        p.Default = MonitorResponse.NotResponding;
        for (var i = 0; i < 10; i++) { h.Clock.Advance(TimeSpan.FromSeconds(2)); await c.TickAsync(); }
        Assert.Equal(DisplayMode.Internal, h.Display.Mode);
        Assert.Equal(RecoveryStatus.WaitingForExternal, c.Status);
    }
}

public class ResumeTests
{
    [Fact]
    public async Task Resume_ClearsStaleLidClosed_GuardWorksAgain()
    {
        var h = new Harness();
        await h.C.InitializeAsync(PowerSource.Ac);
        await h.C.OnPowerSourceChangedAsync(PowerSource.Battery);
        await h.C.OnLidChangedAsync(false);
        await h.C.OnResumedFromSleepAsync();
        h.Display.Mode = DisplayMode.External;
        await h.RunFor(10);
        Assert.Equal(DisplayMode.Internal, h.Display.Mode);
    }
}

public class DdcLearnCapTests
{
    [Fact]
    public async Task Ddc_Learning_StopsAfterFiveFailures()
    {
        var h = new Harness();
        h.Settings = h.Settings with { VerifyRestoreWithDdc = true };
        var p = new FakeProbe { Default = MonitorResponse.NotResponding };
        var c = new RecoveryCoordinator(() => h.Settings, h.Store, h.Display, h.Display, h.Clock, new FakeDelay(h.Clock), h.Log, p);
        await c.InitializeAsync(PowerSource.Ac);
        for (var i = 0; i < 400; i++) { h.Clock.Advance(TimeSpan.FromSeconds(3)); await c.TickAsync(); }
        Assert.Equal(5, p.Calls);
    }
}
