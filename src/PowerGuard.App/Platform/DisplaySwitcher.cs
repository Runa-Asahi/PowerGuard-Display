using System.Diagnostics;
using PowerGuard.Core;

namespace PowerGuard.App.Platform;

/// <summary>
/// 调用 %SystemRoot%\System32\DisplaySwitch.exe。不依赖工作目录或 PATH，不经过 shell。
/// 进程启动/退出只说明命令被执行，切换结果由调用方重新查询拓扑校验。
/// </summary>
public sealed class DisplaySwitcher : IDisplaySwitcher
{
    private readonly SemaphoreSlim _busy = new(1, 1);
    private readonly IAppLogger _log;
    private readonly Func<AppSettings> _settings;

    public DisplaySwitcher(IAppLogger log, Func<AppSettings> settings) { _log = log; _settings = settings; }

    public static string ExePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "DisplaySwitch.exe");

    public async Task<SwitchLaunchResult> SwitchAsync(DisplayMode mode, CancellationToken ct)
    {
        if (!await _busy.WaitAsync(0, ct))
            return SwitchLaunchResult.Fail("已有切屏命令正在执行（防重入）");
        try
        {
            var path = ExePath;
            if (!File.Exists(path)) return SwitchLaunchResult.Fail($"未找到 {path}");
            var style = DisplaySwitchArgs.Resolve(_settings().DisplaySwitchArgStyle, Environment.OSVersion.Version.Build);
            var arg = DisplaySwitchArgs.For(mode, style);
            var psi = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add(arg);
            return await Task.Run(async () =>
            {
                using var p = Process.Start(psi);
                if (p is null) return SwitchLaunchResult.Fail("Process.Start 返回 null");
                _log.Log(LogCategory.Switch, $"已启动 DisplaySwitch.exe {arg}（参数风格 {style}）");
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(15));
                try { await p.WaitForExitAsync(timeout.Token); return SwitchLaunchResult.Ok(p.ExitCode); }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // 参数不被识别时 DisplaySwitch 可能弹出“投影”菜单并保持运行
                    _log.Log(LogCategory.Switch, "DisplaySwitch.exe 15 秒内未退出（可能弹出了投影菜单），继续校验实际拓扑", LogResult.Warning);
                    return SwitchLaunchResult.Ok(null);
                }
            }, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return SwitchLaunchResult.Fail(ex.Message); }
        finally { _busy.Release(); }
    }
}
