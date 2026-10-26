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
    private IntPtr _lidNotify;
    /// <summary>盖子状态：true=打开。没有盖子传感器的设备不会触发。</summary>
    public event Action<bool>? LidChanged;

    public event Action<PowerSource>? PowerSourceChanged;
    public event Action? DisplayChanged;
    public event Action? ResumedFromSleep;
    public event Action? SessionEnding;

    /// <summary>
    /// 收到 WM_QUERYENDSESSION（或未先收到它的 WM_ENDSESSION）时在 UI 线程上同步调用，参数为窗口句柄与原因描述。
    /// 处理程序必须自行限时；返回后本窗口始终允许结束会话。每次会话结束流程只调用一次。
    /// </summary>
    public Action<IntPtr, string>? QueryEndSessionHandler { get; set; }
    private bool _endSessionHandled;

    public IntPtr Handle => _source?.Handle ?? IntPtr.Zero;

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
        var lid = GUID_LIDSWITCH_STATE_CHANGE;
        _lidNotify = RegisterPowerSettingNotification(_source.Handle, ref lid, DEVICE_NOTIFY_WINDOW_HANDLE); // 失败不影响核心功能
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
                            if (s.Data == 2)
                            {
                                // 短时电源（UPS）：不上报，避免与轮询读到的市电之间来回翻转；以 GetSystemPowerStatus 为准
                                _log.Log(LogCategory.Power, "系统报告短时电源（PoAc 2，如 UPS），忽略该通知，以轮询结果为准", LogResult.Warning);
                            }
                            else PowerSourceChanged?.Invoke(src);
                        }
                        else if (s.PowerSetting == GUID_LIDSWITCH_STATE_CHANGE && s.DataLength >= 4)
                            LidChanged?.Invoke(s.Data != 0);
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
                case WM_QUERYENDSESSION:
                    RunEndSessionHandler(hwnd, lParam, "WM_QUERYENDSESSION");
                    handled = true;
                    return new IntPtr(1); // 始终允许关机 / 注销
                case WM_ENDSESSION:
                    if (wParam != IntPtr.Zero)
                    {
                        RunEndSessionHandler(hwnd, lParam, "WM_ENDSESSION");
                        SessionEnding?.Invoke();
                    }
                    else
                    {
                        _endSessionHandled = false; // 关机被取消，下次重新处理
                        _log.Log(LogCategory.App, "会话结束已被取消");
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            _log.Log(LogCategory.Error, $"窗口消息处理异常：{ex.Message}", LogResult.Failure);
        }
        return IntPtr.Zero;
    }

    private void RunEndSessionHandler(IntPtr hwnd, IntPtr lParam, string source)
    {
        if (_endSessionHandled) return;
        _endSessionHandled = true;
        var flags = lParam.ToInt64();
        var kind = (flags & ENDSESSION_LOGOFF) != 0 ? "注销" : "关机/重启";
        if ((flags & ENDSESSION_CRITICAL) != 0) kind += "（强制）";
        if ((flags & ENDSESSION_CLOSEAPP) != 0)
        {
            // 重启管理器（安装更新等）只是要求关闭本程序，并不是关机：不切屏
            _log.Log(LogCategory.App, $"{source}：系统要求关闭程序（重启管理器），不执行关机前恢复");
            return;
        }
        try { QueryEndSessionHandler?.Invoke(hwnd, $"{source}：{kind}"); }
        catch (Exception ex) { _log.Log(LogCategory.Error, $"会话结束处理异常：{ex.Message}", LogResult.Failure); }
    }

    public void Dispose()
    {
        if (_powerNotify != IntPtr.Zero)
        {
            if (!UnregisterPowerSettingNotification(_powerNotify))
                _log.Log(LogCategory.Power, $"UnregisterPowerSettingNotification 失败（{Marshal.GetLastWin32Error()}）", LogResult.Warning);
            _powerNotify = IntPtr.Zero;
        }
        if (_lidNotify != IntPtr.Zero) { UnregisterPowerSettingNotification(_lidNotify); _lidNotify = IntPtr.Zero; }
        _source?.RemoveHook(WndProc);
        _source?.Dispose();
        _source = null;
    }
}
