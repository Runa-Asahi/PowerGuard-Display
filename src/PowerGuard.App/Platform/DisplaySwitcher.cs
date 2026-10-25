using System.Diagnostics;
using PowerGuard.Core;
using static PowerGuard.App.Native.NativeMethods;

namespace PowerGuard.App.Platform;

/// <summary>
/// 切换显示拓扑。默认使用原生 SetDisplayConfig(SDC_APPLY | SDC_TOPOLOGY_*)——Win+P 背后的同一接口，
/// 不依赖 DisplaySwitch.exe 的参数格式、不启动进程、能拿到明确的返回码；失败时（Auto）回退 DisplaySwitch.exe。
/// 不论哪种方式，切换结果都由调用方重新查询拓扑校验。
/// </summary>
public sealed class DisplaySwitcher : IDisplaySwitcher
{
    private readonly SemaphoreSlim _busy = new(1, 1);
    private readonly IAppLogger _log;
    private readonly Func<AppSettings> _settings;
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(15);

    public DisplaySwitcher(IAppLogger log, Func<AppSettings> settings) { _log = log; _settings = settings; }

    public static string ExePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "DisplaySwitch.exe");

    public static uint TopologyFlag(DisplayMode mode) => mode switch
    {
        DisplayMode.Internal => SDC_TOPOLOGY_INTERNAL,
        DisplayMode.Clone => SDC_TOPOLOGY_CLONE,
        DisplayMode.Extend => SDC_TOPOLOGY_EXTEND,
        DisplayMode.External => SDC_TOPOLOGY_EXTERNAL,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "未知模式不能切换"),
    };

    public async Task<SwitchLaunchResult> SwitchAsync(DisplayMode mode, CancellationToken ct)
    {
        if (!await _busy.WaitAsync(0, ct).ConfigureAwait(false))
            return SwitchLaunchResult.Fail("已有切屏命令正在执行（防重入）");
        try
        {
            var method = _settings().SwitchMethod;
            if (method is SwitchMethod.Auto or SwitchMethod.Native)
            {
                var native = SwitchNative(mode);
                if (native.Started || method == SwitchMethod.Native) return native;
                _log.Log(LogCategory.Switch, $"{native.Error}，回退到 DisplaySwitch.exe", LogResult.Warning);
            }
            return await SwitchWithDisplaySwitchAsync(mode, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return SwitchLaunchResult.Fail(ex.Message); }
        finally { _busy.Release(); }
    }

    /// <summary>同步调用 SetDisplayConfig；通常几百毫秒内返回。</summary>
    private SwitchLaunchResult SwitchNative(DisplayMode mode)
    {
        var flags = SDC_APPLY | TopologyFlag(mode);
        var rc = SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, flags);
        if (rc == ERROR_SUCCESS)
        {
            _log.Log(LogCategory.Switch, $"已调用 SetDisplayConfig 切换到{DisplayModeText.Of(mode)}");
            return SwitchLaunchResult.Ok(0);
        }
        return SwitchLaunchResult.Fail($"SetDisplayConfig 返回 {rc}（{Describe(rc)}）");
    }

    private static string Describe(int rc) => rc switch
    {
        87 => "参数无效：该拓扑当前不可用，例如没有外接显示器",
        50 => "不支持：显卡驱动不支持该操作",
        5 => "拒绝访问：可能处于锁屏 / 远程桌面 / 非交互会话",
        1610 => "数据库中没有该拓扑的配置",
        31 => "驱动执行失败",
        _ => "未知错误",
    };

    private async Task<SwitchLaunchResult> SwitchWithDisplaySwitchAsync(DisplayMode mode, CancellationToken ct)
    {
        var path = ExePath;
        if (!File.Exists(path)) return SwitchLaunchResult.Fail($"未找到 {path}");
        var style = DisplaySwitchArgs.Resolve(_settings().DisplaySwitchArgStyle, Environment.OSVersion.Version.Build);
        var arg = DisplaySwitchArgs.For(mode, style);
        var psi = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add(arg);
        using var p = Process.Start(psi);
        if (p is null) return SwitchLaunchResult.Fail("Process.Start 返回 null");
        _log.Log(LogCategory.Switch, $"已启动 DisplaySwitch.exe {arg}（参数风格 {style}）");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProcessTimeout);
        try
        {
            await p.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return SwitchLaunchResult.Ok(p.ExitCode);
        }
        catch (OperationCanceledException)
        {
            // 参数不被识别时 DisplaySwitch 会弹出“投影”菜单并一直运行：结束它，避免菜单残留
            try { if (!p.HasExited) p.Kill(); } catch { }
            if (ct.IsCancellationRequested) throw;
            _log.Log(LogCategory.Switch, $"DisplaySwitch.exe {ProcessTimeout.TotalSeconds:0} 秒内未退出（可能弹出了投影菜单），已结束该进程，继续校验实际拓扑", LogResult.Warning);
            return SwitchLaunchResult.Ok(null);
        }
    }
}
