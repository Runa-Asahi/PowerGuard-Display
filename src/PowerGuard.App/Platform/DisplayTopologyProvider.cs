using System.Runtime.InteropServices;
using PowerGuard.Core;
using static PowerGuard.App.Native.NativeMethods;

namespace PowerGuard.App.Platform;

/// <summary>
/// 用 QueryDisplayConfig 读取真实的显示拓扑：
/// - QDC_ONLY_ACTIVE_PATHS：活动路径 → 推导当前模式（内屏/外接是否活动、是否同源=复制）。
/// - QDC_ALL_PATHS：所有路径中 targetAvailable=TRUE 的外接目标 → “Windows 检测到外接显示路径”。
/// targetAvailable 只说明 Windows 认为目标可连接，并不保证显示器已亮屏。
/// </summary>
public sealed class DisplayTopologyProvider : IDisplayTopologyProvider
{
    private static bool IsInternal(uint tech) =>
        tech is OUTPUT_TECH_INTERNAL or OUTPUT_TECH_LVDS or OUTPUT_TECH_DISPLAYPORT_EMBEDDED or OUTPUT_TECH_UDI_EMBEDDED;

    private static int Query(uint flags, out DISPLAYCONFIG_PATH_INFO[] paths)
    {
        paths = [];
        for (var tries = 0; tries < 5; tries++)
        {
            var err = GetDisplayConfigBufferSizes(flags, out var np, out var nm);
            if (err != ERROR_SUCCESS) return err;
            var p = new DISPLAYCONFIG_PATH_INFO[np];
            var m = new DISPLAYCONFIG_MODE_INFO[nm];
            err = QueryDisplayConfig(flags, ref np, p, ref nm, m, IntPtr.Zero);
            if (err == ERROR_INSUFFICIENT_BUFFER) continue; // 拓扑在两次调用之间变化
            if (err != ERROR_SUCCESS) return err;
            paths = p.Take((int)np).ToArray();
            return ERROR_SUCCESS;
        }
        return ERROR_INSUFFICIENT_BUFFER;
    }

    /// <summary>某些驱动在 path.targetInfo.outputTechnology 上不可靠，再用 DisplayConfigGetDeviceInfo 确认。</summary>
    private static uint TechOf(in DISPLAYCONFIG_PATH_INFO p)
    {
        var req = new DISPLAYCONFIG_TARGET_DEVICE_NAME
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DISPLAYCONFIG_DEVICE_INFO_GET_TARGET_NAME,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                adapterId = p.targetInfo.adapterId,
                id = p.targetInfo.id,
            },
            monitorFriendlyDeviceName = "",
            monitorDevicePath = "",
        };
        return DisplayConfigGetDeviceInfo(ref req) == ERROR_SUCCESS ? req.outputTechnology : p.targetInfo.outputTechnology;
    }

    public DisplayTopology Query()
    {
        try
        {
            var err = Query(QDC_ONLY_ACTIVE_PATHS, out var active);
            if (err != ERROR_SUCCESS) return DisplayTopology.Failed($"QueryDisplayConfig(活动路径) 返回 {err}");
            err = Query(QDC_ALL_PATHS, out var all);
            if (err != ERROR_SUCCESS) return DisplayTopology.Failed($"QueryDisplayConfig(全部路径) 返回 {err}");

            var activeInternal = new List<DISPLAYCONFIG_PATH_INFO>();
            var activeExternal = new List<DISPLAYCONFIG_PATH_INFO>();
            foreach (var p in active.Where(p => (p.flags & DISPLAYCONFIG_PATH_ACTIVE) != 0))
                (IsInternal(TechOf(p)) ? activeInternal : activeExternal).Add(p);

            var internalPresent = activeInternal.Count > 0;
            var externalAvailable = new HashSet<(uint, int, uint)>();
            foreach (var p in all)
            {
                var key = (p.targetInfo.adapterId.LowPart, p.targetInfo.adapterId.HighPart, p.targetInfo.id);
                var tech = TechOf(p);
                if (IsInternal(tech)) { internalPresent = true; continue; }
                if (p.targetInfo.targetAvailable) externalAvailable.Add(key);
            }
            foreach (var p in activeExternal)
                externalAvailable.Add((p.targetInfo.adapterId.LowPart, p.targetInfo.adapterId.HighPart, p.targetInfo.id));

            DisplayMode mode;
            if (activeInternal.Count > 0 && activeExternal.Count == 0) mode = DisplayMode.Internal;
            else if (activeInternal.Count == 0 && activeExternal.Count > 0) mode = DisplayMode.External;
            else if (activeInternal.Count > 0 && activeExternal.Count > 0)
            {
                // 内外屏共用同一源（同一适配器 + 同一 source id）即为复制
                var src = activeInternal.Select(p => (p.sourceInfo.adapterId.LowPart, p.sourceInfo.adapterId.HighPart, p.sourceInfo.id)).ToHashSet();
                mode = activeExternal.Any(p => src.Contains((p.sourceInfo.adapterId.LowPart, p.sourceInfo.adapterId.HighPart, p.sourceInfo.id)))
                    ? DisplayMode.Clone : DisplayMode.Extend;
            }
            else mode = DisplayMode.Unknown;

            var sig = $"{mode}|i{activeInternal.Count}|e{activeExternal.Count}|a{externalAvailable.Count}|p{internalPresent}";
            return new DisplayTopology(true, mode, internalPresent, activeInternal.Count > 0,
                externalAvailable.Count, activeExternal.Count, sig);
        }
        catch (Exception ex)
        {
            return DisplayTopology.Failed(ex.Message);
        }
    }
}
