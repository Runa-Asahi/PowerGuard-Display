using PowerGuard.Core;

namespace PowerGuard.Core.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pgd-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public void Dispose() { try { Directory.Delete(Path, true); } catch { } }
}

public class PersistenceTests
{
    [Fact] // 用例 1
    public void FirstRun_NoFile_ReturnsSafeDefaults()
    {
        using var d = new TempDir();
        var s = new SettingsStore(System.IO.Path.Combine(d.Path, "settings.json"));
        var r = s.Load();
        Assert.Equal(LoadStatus.Missing, r.Status);
        Assert.True(r.Value!.AutoSwitchOnPowerLoss);
        Assert.False(r.Value.StartWithWindows);
        Assert.Equal(DisplayMode.External, r.Value.PreferredMode);
    }

    [Fact]
    public void Settings_RoundTrip_Persists()
    {
        using var d = new TempDir();
        var path = System.IO.Path.Combine(d.Path, "sub", "settings.json");
        new SettingsStore(path).Save(new AppSettings { PreferredMode = DisplayMode.Clone, MaxRetries = 5, StartWithWindows = true });
        var r = new SettingsStore(path).Load();
        Assert.Equal(LoadStatus.Loaded, r.Status);
        Assert.Equal(DisplayMode.Clone, r.Value!.PreferredMode);
        Assert.Equal(5, r.Value.MaxRetries);
        Assert.True(r.Value.StartWithWindows);
        Assert.Contains("\"Clone\"", File.ReadAllText(path));
        Assert.False(File.Exists(path + ".tmp"));
    }

    [Theory] // 用例 10
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("{\"PreferredMode\": \"Bogus\"}")]
    public void CorruptSettings_UsesDefaults_AndBacksUp(string content)
    {
        using var d = new TempDir();
        var path = System.IO.Path.Combine(d.Path, "settings.json");
        File.WriteAllText(path, content);
        var r = new SettingsStore(path).Load();
        Assert.Equal(LoadStatus.Corrupt, r.Status);
        Assert.NotNull(r.Value);
        Assert.False(File.Exists(path));
        Assert.Single(Directory.GetFiles(d.Path, "settings.json.corrupt-*"));
    }

    [Fact]
    public void OutOfRangeValues_AreClamped()
    {
        using var d = new TempDir();
        var path = System.IO.Path.Combine(d.Path, "settings.json");
        File.WriteAllText(path, "{\"StabilitySeconds\": -5, \"MaxRetries\": 999, \"PreferredMode\": \"Unknown\"}");
        var r = new SettingsStore(path).Load().Value!;
        Assert.Equal(3, r.StabilitySeconds);
        Assert.Equal(10, r.MaxRetries);
        Assert.Equal(DisplayMode.External, r.PreferredMode);
    }

    [Fact]
    public void RecoveryState_SaveLoadClear()
    {
        using var d = new TempDir();
        var path = System.IO.Path.Combine(d.Path, "recovery-state.json");
        var store = new RecoveryStateStore(path);
        Assert.Equal(LoadStatus.Missing, store.Load().Status);
        var t = DateTimeOffset.Now;
        store.Save(new RecoveryState { PreferredModeSnapshot = DisplayMode.Extend, StartedAt = t, LastError = "x" });
        var r = new RecoveryStateStore(path).Load();
        Assert.Equal(DisplayMode.Extend, r.Value!.PreferredModeSnapshot);
        Assert.Equal(t, r.Value.StartedAt);
        store.Clear();
        Assert.False(File.Exists(path));
    }

    [Fact] // 用例 11：以真实文件模拟进程崩溃后重启
    public async Task CrashMidRecovery_RealFiles_ResumesOnNextStart()
    {
        using var d = new TempDir();
        var path = System.IO.Path.Combine(d.Path, "recovery-state.json");
        var h = new Harness();
        var store = new RecoveryStateStore(path);
        var c1 = new RecoveryCoordinator(() => h.Settings, store, h.Display, h.Display, h.Clock, new FakeDelay(h.Clock), h.Log);
        await c1.InitializeAsync(PowerSource.Ac);
        await c1.OnPowerSourceChangedAsync(PowerSource.Battery);
        Assert.True(File.Exists(path));
        var c2 = new RecoveryCoordinator(() => h.Settings, new RecoveryStateStore(path), h.Display, h.Display, h.Clock, new FakeDelay(h.Clock), h.Log);
        await c2.InitializeAsync(PowerSource.Ac);
        for (var i = 0; i < 12; i++) { h.Clock.Advance(TimeSpan.FromSeconds(2)); await c2.TickAsync(); }
        Assert.Equal(RecoveryStatus.Normal, c2.Status);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void FileLogger_WritesRollsAndRetains()
    {
        using var d = new TempDir();
        var clock = new FakeClock();
        var log = new FileLogger(d.Path, clock) { MaxFileBytes = 300 };
        for (var i = 0; i < 10; i++) log.Log(LogCategory.App, $"消息 {i}", LogResult.Success);
        Assert.True(Directory.GetFiles(d.Path, "powerguard-*.log").Length > 1);
        File.WriteAllText(System.IO.Path.Combine(d.Path, "powerguard-20200101.log"), "{}");
        Assert.Equal(1, log.ApplyRetention(true));
        var log2 = new FileLogger(d.Path, clock);
        log2.LoadHistory();
        Assert.Equal(10, log2.Recent.Count);
        log2.ClearAll();
        Assert.Empty(Directory.GetFiles(d.Path, "powerguard-*.log"));
    }
}
