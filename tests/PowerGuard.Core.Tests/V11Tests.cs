using System.Text.Json;
using PowerGuard.Core;

namespace PowerGuard.Core.Tests;

/// <summary>在指定时长处阻塞（可取消），其余时长立即完成并推进假时钟。</summary>
public sealed class GatedDelay(FakeClock clock, TimeSpan blockOn) : IDelay
{
    public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public async Task Delay(TimeSpan d, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (d == blockOn)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
        }
        clock.Advance(d);
    }
}

public sealed class ThrowingStore : IRecoveryStateStore
{
    public LoadResult<RecoveryState> Load() => throw new IOException("locked");
    public void Save(RecoveryState state) => throw new IOException("locked");
    public void Clear() => throw new IOException("locked");
}

public class ConcurrencyAndRobustnessTests
{
    [Fact]
    public async Task ManualAction_CancelsAutoRestoreRetryWait_WithoutErrors()
    {
        var h = new Harness();
        var delay = new GatedDelay(h.Clock, TimeSpan.FromSeconds(h.Settings.RetryIntervalSeconds));
        var c = new RecoveryCoordinator(() => h.Settings, h.Store, h.Display, h.Display, h.Clock, delay, h.Log);
        await c.InitializeAsync(PowerSource.Ac);
        await c.OnPowerSourceChangedAsync(PowerSource.Battery);
        await c.OnPowerSourceChangedAsync(PowerSource.Ac);
        h.Display.SwitchHasNoEffect = true;

        var restore = Task.Run(async () =>
        {
            for (var i = 0; i < 20 && c.Status != RecoveryStatus.RestoringPreferredMode; i++)
            { h.Clock.Advance(TimeSpan.FromSeconds(2)); await c.TickAsync(); }
        });
        await delay.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // UI 线程同时读取快照
        var snap = c.Snapshot();
        Assert.Equal(RecoveryStatus.RestoringPreferredMode, snap.Status);

        h.Display.SwitchHasNoEffect = false;
        var r = await c.ManualSwitchToInternalAsync().WaitAsync(TimeSpan.FromSeconds(10));
        await restore.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(r.Success);
        Assert.Equal(RecoveryStatus.WaitingForExternal, c.Status);
        Assert.Contains(h.Log.Entries, e => e.Message.Contains("被用户操作中断"));
        Assert.DoesNotContain(h.Log.Entries, e => e.Category == LogCategory.Error);
    }

    [Fact]
    public async Task ConcurrentTicksAndEvents_DoNotThrow()
    {
        var h = new Harness();
        await h.C.InitializeAsync(PowerSource.Ac);
        var tasks = Enumerable.Range(0, 50).Select(i => Task.Run(async () =>
        {
            if (i % 5 == 0) await h.C.OnPowerSourceChangedAsync(i % 10 == 0 ? PowerSource.Battery : PowerSource.Ac);
            await h.C.TickAsync();
            _ = h.C.Snapshot();
        }));
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.DoesNotContain(h.Log.Entries.ToList(), e => e.Category == LogCategory.Error);
    }

    [Fact]
    public async Task SnapshotMarker_IsACopy()
    {
        var h = new Harness();
        await h.C.InitializeAsync(PowerSource.Ac);
        await h.C.OnPowerSourceChangedAsync(PowerSource.Battery);
        var m = h.C.Marker!;
        m.AttemptCount = 999;
        Assert.NotEqual(999, h.C.Marker!.AttemptCount);
    }

    [Fact]
    public async Task UnreadableStore_TreatedAsPending_AndDoesNotThrow()
    {
        var h = new Harness();
        var c = new RecoveryCoordinator(() => h.Settings, new ThrowingStore(), h.Display, h.Display, h.Clock, new FakeDelay(h.Clock), h.Log);
        await c.InitializeAsync(PowerSource.Ac);
        Assert.Equal(RecoveryStatus.WaitingForExternal, c.Status);
        await c.DiscardRecoveryAsync(); // Clear 抛异常也不能冒出
        Assert.Equal(RecoveryStatus.Normal, c.Status);
    }

    [Fact]
    public async Task TimedPause_AutoResumes()
    {
        var h = new Harness();
        await h.C.InitializeAsync(PowerSource.Ac);
        await h.C.SetPausedAsync(true, TimeSpan.FromMinutes(1));
        Assert.Equal(RecoveryStatus.Paused, h.C.Status);
        Assert.NotNull(h.C.Snapshot().PausedUntil);
        await h.C.OnPowerSourceChangedAsync(PowerSource.Battery);
        Assert.Empty(h.Display.Launches); // 暂停期间忽略断电
        await h.C.OnPowerSourceChangedAsync(PowerSource.Ac);
        await h.RunFor(62);
        Assert.Equal(RecoveryStatus.Normal, h.C.Status);
        Assert.Null(h.C.Snapshot().PausedUntil);
        await h.C.OnPowerSourceChangedAsync(PowerSource.Battery);
        Assert.Single(h.Display.Launches);
    }

    [Fact]
    public async Task ManualRestore_WhenAlreadyPreferred_ClearsMarkerWithoutSwitching()
    {
        var h = new Harness();
        await h.C.InitializeAsync(PowerSource.Ac);
        await h.C.OnPowerSourceChangedAsync(PowerSource.Battery);
        await h.C.OnPowerSourceChangedAsync(PowerSource.Ac);
        h.Display.Mode = DisplayMode.External; // 用户自己按了 Win+P
        h.Display.Launches.Clear();
        var r = await h.C.ManualRestorePreferredAsync();
        Assert.True(r.Success);
        Assert.Empty(h.Display.Launches);
        Assert.Null(h.Store.State);
    }
}

public class TopologyClassifierTests
{
    private static readonly (uint, int) A = (1, 0), B = (2, 0);
    private static DisplayPath P(uint src, uint tgt, bool internalScreen, bool active = true, bool avail = true, (uint, int)? sa = null, (uint, int)? ta = null)
        => new(sa ?? A, src, ta ?? A, tgt, internalScreen, avail, active);

    [Fact]
    public void InternalOnly()
    {
        var t = TopologyClassifier.Classify([P(0, 1, true)], [P(0, 1, true), P(0, 2, false, false, avail: false)]);
        Assert.Equal(DisplayMode.Internal, t.Mode);
        Assert.Equal(0, t.ExternalAvailableCount);
        Assert.True(t.InternalActive);
    }

    [Fact]
    public void InternalActive_ExternalAvailableButInactive()
    {
        var t = TopologyClassifier.Classify([P(0, 1, true)], [P(0, 1, true), P(0, 2, false, false), P(1, 2, false, false)]);
        Assert.Equal(DisplayMode.Internal, t.Mode);
        Assert.Equal(1, t.ExternalAvailableCount); // 同一目标的多条候选路径只计一次
    }

    [Fact]
    public void ExternalOnly_InternalStillPresent()
    {
        var t = TopologyClassifier.Classify([P(0, 2, false)], [P(0, 1, true, false), P(0, 2, false)]);
        Assert.Equal(DisplayMode.External, t.Mode);
        Assert.True(t.InternalPresent);
        Assert.False(t.InternalActive);
    }

    [Fact]
    public void Extend_DifferentSources() =>
        Assert.Equal(DisplayMode.Extend, TopologyClassifier.Classify([P(0, 1, true), P(1, 2, false)], null).Mode);

    [Fact]
    public void Clone_SameSource() =>
        Assert.Equal(DisplayMode.Clone, TopologyClassifier.Classify([P(0, 1, true), P(0, 2, false)], null).Mode);

    [Fact]
    public void HybridGraphics_SourceAdapterDiffersFromTarget()
    {
        // 内屏由 A 输出；外接目标在 B 上，但 source 在 A（同源）→ 复制
        var t = TopologyClassifier.Classify([P(0, 1, true), P(0, 7, false, sa: A, ta: B)], [P(0, 7, false, false, sa: B, ta: B)]);
        Assert.Equal(DisplayMode.Clone, t.Mode);
        Assert.Equal(1, t.ExternalAvailableCount);
    }

    [Fact]
    public void NoActivePaths_Unknown() => Assert.Equal(DisplayMode.Unknown, TopologyClassifier.Classify([], []).Mode);
}

public class SettingsV2Tests
{
    [Fact]
    public void V1File_MigratesToV2_WithNewDefaults()
    {
        var v1 = """{ "Version": 1, "PreferredMode": "Extend", "StabilitySeconds": 20 }""";
        var s = JsonSerializer.Deserialize<AppSettings>(v1, JsonFileStore<AppSettings>.Options)!;
        Assert.True(s.Normalize());
        Assert.Equal(AppSettings.CurrentVersion, s.Version);
        Assert.True(s.RestoreOnShutdown);
        Assert.Equal(SwitchMethod.Auto, s.SwitchMethod);
        Assert.Equal(DisplayMode.Extend, s.PreferredMode);
    }

    [Fact]
    public void Defaults_NormalizeReportsNoChange_AndCopyIsEqualButIndependent()
    {
        var s = new AppSettings();
        Assert.False(s.Normalize());
        var c = s.Copy();
        Assert.Equal(s, c);
        c.MaxRetries = 7;
        Assert.NotEqual(s, c);
        Assert.Equal(3, s.MaxRetries);
    }

    [Fact]
    public void SwitchMethod_SerializesAsString()
    {
        var json = JsonSerializer.Serialize(new AppSettings { SwitchMethod = SwitchMethod.DisplaySwitch }, JsonFileStore<AppSettings>.Options);
        Assert.Contains("\"DisplaySwitch\"", json);
    }
}

public class PersistenceV11Tests
{
    [Fact]
    public void LockedFile_IsUnreadable_AndNotRenamedOrOverwritten()
    {
        using var d = new TempDir();
        var path = Path.Combine(d.Path, "settings.json");
        File.WriteAllText(path, "{}");
        var store = new SettingsStore(path);
        LoadResult<AppSettings> r;
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            r = store.Load();
        if (OperatingSystem.IsWindows()) Assert.Equal(LoadStatus.Unreadable, r.Status);
        Assert.NotNull(r.Value); // 安全默认值
        Assert.True(File.Exists(path));
        Assert.Empty(Directory.GetFiles(d.Path, "*.corrupt-*"));
    }

    [Fact]
    public void Logger_DayRollover_AppliesRetention_AndSwitchesFile()
    {
        using var d = new TempDir();
        var clock = new FakeClock();
        var old = Path.Combine(d.Path, $"powerguard-{clock.Now.AddDays(-20):yyyyMMdd}.log");
        File.WriteAllText(old, "");
        var log = new FileLogger(d.Path, clock) { RetainLogs = true, KeepDays = 14 };
        log.Log(LogCategory.App, "day1");
        var first = log.CurrentPath;
        Assert.True(File.Exists(old)); // 同一天内不清理
        clock.Advance(TimeSpan.FromDays(1));
        log.Log(LogCategory.App, "day2");
        Assert.NotEqual(first, log.CurrentPath);
        Assert.False(File.Exists(old));
        log.Dispose();
    }

    [Fact]
    public void Logger_HistoryCanBeReadWhileWriting_AndIsTrimmed()
    {
        using var d = new TempDir();
        var clock = new FakeClock();
        var log = new FileLogger(d.Path, clock);
        for (var i = 0; i < FileLogger.MaxRecent + 50; i++) log.Log(LogCategory.App, "m" + i);
        var log2 = new FileLogger(d.Path, clock);
        for (var i = 0; i < 10; i++) log2.Log(LogCategory.App, "n" + i);
        log2.LoadHistory(maxEntries: FileLogger.MaxRecent + 100);
        Assert.True(log2.Recent.Count <= FileLogger.MaxRecent);
        log.Dispose(); log2.Dispose();
    }

    [Fact]
    public async Task StartupTask_WritesTempXmlIntoGivenDirectory()
    {
        using var d = new TempDir();
        var r = new FakeRunner();
        var res = await new StartupTaskService(r, "schtasks", d.Path).EnableAsync(@"C:\x\PowerGuardDisplay.exe", "u");
        Assert.True(res.Success);
        var xmlArg = r.Calls.Single().Args[4];
        Assert.StartsWith(d.Path, xmlArg);
        Assert.NotNull(r.Calls.Single().XmlContent);
    }
}
