using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using PowerGuard.Core;
using static PowerGuard.App.Native.NativeMethods;

namespace PowerGuard.App.Platform;

/// <summary>
/// 用 QueryDisplayConfig 读取真实的显示拓扑：
/// - QDC_ONLY_ACTIVE_PATHS：活动路径 → 推导当前模式（内屏/外接是否活动、是否同源=复制）。
/// - QDC_ALL_PATHS：所有路径中 targetAvailable=TRUE 的外接目标 → “Windows 检测到外接显示路径”。
/// targetAvailable 只说明 Windows 认为目标可连接，并不保证显示器已亮屏。
/// 推导逻辑在 Core 的 TopologyClassifier 中（有单元测试）；这里只负责 Win32 调用。
/// </summary>
public sealed class DisplayTopologyProvider : IDisplayTopologyProvider
{
    /// <summary>
    /// (适配器, 目标) → outputTechnology 缓存。目标的接口类型不会变化；
    /// QDC_ALL_PATHS 可能返回上百条路径，逐条调用 DisplayConfigGetDeviceInfo 是轮询中最主要的开销。
    /// WM_DISPLAYCHANGE（适配器 LUID 可能在驱动重置后变化）时调用 InvalidateCache。
    /// </summary>
    private readonly ConcurrentDictionary<(uint, int, uint), uint> _techCache = new();

    public void InvalidateCache() => _techCache.Clear();

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

    /// <summary>某些驱动在 path.targetInfo.outputTechnology 上不可靠，再用 DisplayConfigGetDeviceInfo 确认（结果缓存）。</summary>
    private uint TechOf(in DISPLAYCONFIG_PATH_INFO p)
    {
        var key = (p.targetInfo.adapterId.LowPart, p.targetInfo.adapterId.HighPart, p.targetInfo.id);
        if (_techCache.TryGetValue(key, out var cached)) return cached;
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
        if (DisplayConfigGetDeviceInfo(ref req) == ERROR_SUCCESS)
        {
            _techCache[key] = req.outputTechnology;
            return req.outputTechnology;
        }
        return p.targetInfo.outputTechnology; // 失败不缓存，下次重试
    }

    private DisplayPath ToPath(in DISPLAYCONFIG_PATH_INFO p) => new(
        (p.sourceInfo.adapterId.LowPart, p.sourceInfo.adapterId.HighPart), p.sourceInfo.id,
        (p.targetInfo.adapterId.LowPart, p.targetInfo.adapterId.HighPart), p.targetInfo.id,
        IsInternal(TechOf(p)), p.targetInfo.targetAvailable, (p.flags & DISPLAYCONFIG_PATH_ACTIVE) != 0);

    public DisplayTopology Query()
    {
        try
        {
            var err = Query(QDC_ONLY_ACTIVE_PATHS, out var active);
            if (err != ERROR_SUCCESS) return DisplayTopology.Failed($"QueryDisplayConfig(活动路径) 返回 {err}");
            err = Query(QDC_ALL_PATHS, out var all);
            if (err != ERROR_SUCCESS) return DisplayTopology.Failed($"QueryDisplayConfig(全部路径) 返回 {err}");
            var activePaths = active.Select(p => ToPath(p)).ToList();
            var allPaths = all.Select(p => ToPath(p)).ToList();
            return TopologyClassifier.Classify(activePaths, allPaths);
        }
        catch (Exception ex)
        {
            return DisplayTopology.Failed(ex.Message);
        }
    }
}
