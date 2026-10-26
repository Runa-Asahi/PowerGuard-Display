using System.Diagnostics;
using PowerGuard.Core;
using static PowerGuard.App.Native.NativeMethods;

namespace PowerGuard.App.Platform;

/// <summary>
/// 切换显示拓扑。两种方式：
/// - DisplaySwitch.exe（Win+P 同款工具）：v1.0.0 起在实机（Lenovo Y7000）上验证过断电切内屏有效，
///   Windows 会把结果记入显示配置数据库，之后显示器热插拔时沿用该配置。
/// - 原生 SetDisplayConfig(SDC_APPLY | SDC_TOPOLOGY_*)：按数据库中“该拓扑上一次的配置”切换。
///   注意：官方文档规定 SDC_SAVE_TO_DATABASE 只能与 SDC_USE_SUPPLIED_DISPLAY_CONFIG 一起使用，不能与 SDC_TOPOLOGY_* 组合。
/// SwitchMethod.Auto：先用实机验证过的 DisplaySwitch.exe，启动失败时用原生接口；alternate=true 时两者顺序互换
/// （协调器在第一次校验未通过时用备用方式再试一次）。不论哪种方式，结果都由调用方重新查询拓扑校验。
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

    public async Task<SwitchLaunchResult> SwitchAsync(DisplayMode mode, CancellationToken ct, bool alternate = false)
    {
        if (!await _busy.WaitAsync(0, ct).ConfigureAwait(false))
            return SwitchLaunchResult.Fail("已有切屏命令正在执行（防重入）");
        try
        {
            var method = _settings().SwitchMethod;
            if (method == SwitchMethod.Native) return SwitchNative(mode);
            if (method == SwitchMethod.DisplaySwitch) return await SwitchWithDisplaySwitchAsync(mode, ct).ConfigureAwait(false);

            // Auto
            if (!alternate)
            {
                var r = await SwitchWithDisplaySwitchAsync(mode, ct).ConfigureAwait(false);
                if (r.Started) return r;
                _log.Log(LogCategory.Switch, $"{r.Error}，改用 SetDisplayConfig", LogResult.Warning);
                return SwitchNative(mode);
            }
            var n = SwitchNative(mode);
            if (n.Started) return n;
            _log.Log(LogCategory.Switch, $"{n.Error}，改用 DisplaySwitch.exe", LogResult.Warning);
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
