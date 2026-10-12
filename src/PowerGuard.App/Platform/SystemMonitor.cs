using System.Runtime.InteropServices;
using System.Windows.Interop;
using PowerGuard.Core;
using static PowerGuard.App.Native.NativeMethods;

namespace PowerGuard.App.Platform;

/// <summary>
/// 隐藏的顶层窗口（非 HWND_MESSAGE，因为消息专用窗口收不到 WM_DISPLAYCHANGE 广播），
/// 注册 GUID_ACDC_POWER_SOURCE 通知并接收 WM_POWERBROADCAST / WM_DISPLAYCHANGE。
/// 必须在 UI（Dispatcher）线程创建，消息在该线程的消息循环中处理。
/// </summary>
public sealed class SystemMonitor : IDisposable
{
    private readonly IAppLogger _log;
    private HwndSource? _source;
    private IntPtr _powerNotify;

    public event Action<PowerSource>? PowerSourceChanged;
    public event Action? DisplayChanged;
    public event Action? ResumedFromSleep;
    public event Action? SessionEnding;

    public SystemMonitor(IAppLogger log) => _log = log;

    public static PowerSource ReadPowerStatus(IAppLogger? log = null)
    {
        if (!GetSystemPowerStatus(out var s))
        {
            log?.Log(LogCategory.Power, $"GetSystemPowerStatus 失败，错误码 {Marshal.GetLastWin32Error()}", LogResult.Failure);
            return PowerSource.Unknown;
        }
        return s.ACLineStatus switch { 1 => PowerSource.Ac, 0 => PowerSource.Battery, _ => PowerSource.Unknown };
    }

    public void Start()
    {
        var p = new HwndSourceParameters("PowerGuardDisplay.Monitor")
        {
            Width = 0, Height = 0, PositionX = -32000, PositionY = -32000,
            WindowStyle = unchecked((int)0x80000000), // WS_POPUP，不可见
            ExtendedWindowStyle = 0x00000080,         // WS_EX_TOOLWINDOW，不出现在任务栏
        };
        _source = new HwndSource(p);
        _source.AddHook(WndProc);
        var guid = GUID_ACDC_POWER_SOURCE;
        _powerNotify = RegisterPowerSettingNotification(_source.Handle, ref guid, DEVICE_NOTIFY_WINDOW_HANDLE);
        if (_powerNotify == IntPtr.Zero)
            _log.Log(LogCategory.Power, $"RegisterPowerSettingNotification 失败（错误码 {Marshal.GetLastWin32Error()}），改为依赖 PBT_APMPOWERSTATUSCHANGE 与低频轮询", LogResult.Failure);
        else
            _log.Log(LogCategory.Power, "已注册 GUID_ACDC_POWER_SOURCE 电源通知", LogResult.Success);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        try
        {
            switch (msg)
            {
                case WM_POWERBROADCAST:
                    var evt = wParam.ToInt32();
                    if (evt == PBT_POWERSETTINGCHANGE && lParam != IntPtr.Zero)
                    {
                        var s = Marshal.PtrToStructure<POWERBROADCAST_SETTING>(lParam);
                        if (s.PowerSetting == GUID_ACDC_POWER_SOURCE && s.DataLength >= 4)
                        {
                            var src = s.Data switch { 0 => PowerSource.Ac, 1 => PowerSource.Battery, _ => PowerSource.Unknown };
                            if (s.Data == 2) _log.Log(LogCategory.Power, "系统报告短时电源（PoAc 2，如 UPS），按未知处理", LogResult.Warning);
                            PowerSourceChanged?.Invoke(src);
                        }
                    }
                    else if (evt == PBT_APMPOWERSTATUSCHANGE)
                    {
                        // 作为补充：用 GetSystemPowerStatus 校验
                        PowerSourceChanged?.Invoke(ReadPowerStatus(_log));
                    }
                    else if (evt is PBT_APMRESUMEAUTOMATIC or PBT_APMRESUMESUSPEND)
                    {
                        ResumedFromSleep?.Invoke();
                    }
                    handled = true;
                    return new IntPtr(1);
                case WM_DISPLAYCHANGE:
                    DisplayChanged?.Invoke();
                    break;
                case WM_ENDSESSION:
                    if (wParam != IntPtr.Zero) SessionEnding?.Invoke();
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Log(LogCategory.Error, $"窗口消息处理异常：{ex.Message}", LogResult.Failure);
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_powerNotify != IntPtr.Zero)
        {
            if (!UnregisterPowerSettingNotification(_powerNotify))
                _log.Log(LogCategory.Power, $"UnregisterPowerSettingNotification 失败（{Marshal.GetLastWin32Error()}）", LogResult.Warning);
            _powerNotify = IntPtr.Zero;
        }
        _source?.RemoveHook(WndProc);
        _source?.Dispose();
        _source = null;
    }
}
