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
