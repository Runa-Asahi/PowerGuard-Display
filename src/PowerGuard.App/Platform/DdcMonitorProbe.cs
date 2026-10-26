using System.Runtime.InteropServices;
using PowerGuard.Core;

namespace PowerGuard.App.Platform;

/// <summary>
/// DDC/CI 探测（dxva2.dll，physicalmonitorenumerationapi.h / lowlevelmonitorconfigurationapi.h）。
/// 对所有活动显示器读取 VCP 0xD6（电源模式）：任一应答 = Responding；全部失败 = NotResponding；没有可枚举的物理显示器 = Unsupported。
/// 笔记本 eDP 内屏通常不支持 DDC/CI；协调器只在“外接独占”等需要外接的模式下、且之前确认过支持时才使用判定结果。
/// 任何异常都返回 Unsupported（回到已验证的行为）。
/// </summary>
public sealed class DdcMonitorProbe : IMonitorProbe
{
    private const byte VcpPowerMode = 0xD6;

    public MonitorResponse ProbeActiveExternal()
    {
        try
        {
            var monitors = new List<IntPtr>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (h, _, _, _) => { monitors.Add(h); return true; }, IntPtr.Zero);
            int physical = 0, responding = 0;
            foreach (var hmon in monitors)
            {
                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hmon, out var n) || n == 0) continue;
                var arr = new PHYSICAL_MONITOR[n];
                if (!GetPhysicalMonitorsFromHMONITOR(hmon, n, arr)) continue;
                try
                {
                    foreach (var pm in arr)
                    {
                        physical++;
                        if (GetVCPFeatureAndVCPFeatureReply(pm.hPhysicalMonitor, VcpPowerMode, IntPtr.Zero, out _, out _)) responding++;
                    }
                }
                finally { DestroyPhysicalMonitors(n, arr); }
            }
            if (physical == 0) return MonitorResponse.Unsupported;
            return responding > 0 ? MonitorResponse.Responding : MonitorResponse.NotResponding;
        }
        catch { return MonitorResponse.Unsupported; }
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szPhysicalMonitorDescription;
    }

    [DllImport("user32.dll")] private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
    [DllImport("dxva2.dll", SetLastError = true)] private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, out uint count);
    [DllImport("dxva2.dll", SetLastError = true)] private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint size, [Out] PHYSICAL_MONITOR[] arr);
    [DllImport("dxva2.dll", SetLastError = true)] private static extern bool DestroyPhysicalMonitors(uint size, PHYSICAL_MONITOR[] arr);
    [DllImport("dxva2.dll", SetLastError = true)] private static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr hMonitor, byte code, IntPtr type, out uint current, out uint max);
}
