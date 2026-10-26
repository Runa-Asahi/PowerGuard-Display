namespace PowerGuard.Core;

/// <summary>一条 QueryDisplayConfig 路径中本程序关心的字段（与平台无关，便于测试）。</summary>
/// <remarks>source 与 target 的适配器可能不同（混合显卡），因此分别保存。</remarks>
public readonly record struct DisplayPath(
    (uint Low, int High) SourceAdapter, uint SourceId,
    (uint Low, int High) TargetAdapter, uint TargetId,
    bool IsInternal, bool TargetAvailable, bool Active)
{
    public (uint, int, uint) TargetKey => (TargetAdapter.Low, TargetAdapter.High, TargetId);
    public (uint, int, uint) SourceKey => (SourceAdapter.Low, SourceAdapter.High, SourceId);
}

/// <summary>由活动路径 + 全部路径推导显示模式与外接可用数量。</summary>
public static class TopologyClassifier
{
    /// <param name="active">QDC_ONLY_ACTIVE_PATHS 的结果。</param>
    /// <param name="all">QDC_ALL_PATHS 的结果；为 null 时只用活动路径（低频轮询的轻量模式）。</param>
    public static DisplayTopology Classify(IReadOnlyCollection<DisplayPath> active, IReadOnlyCollection<DisplayPath>? all)
    {
        var activeInternal = active.Where(p => p.Active && p.IsInternal).ToList();
        var activeExternal = active.Where(p => p.Active && !p.IsInternal).ToList();

        var internalPresent = activeInternal.Count > 0;
        var externalAvailable = new HashSet<(uint, int, uint)>();
        foreach (var p in all ?? [])
        {
            if (p.IsInternal) { internalPresent = true; continue; }
            if (p.TargetAvailable) externalAvailable.Add(p.TargetKey);
        }
        foreach (var p in activeExternal) externalAvailable.Add(p.TargetKey);

        DisplayMode mode;
        if (activeInternal.Count > 0 && activeExternal.Count == 0) mode = DisplayMode.Internal;
        else if (activeInternal.Count == 0 && activeExternal.Count > 0) mode = DisplayMode.External;
        else if (activeInternal.Count > 0 && activeExternal.Count > 0)
        {
            // 内外屏共用同一源（同一适配器 + 同一 source id）即为复制
            var src = activeInternal.Select(p => p.SourceKey).ToHashSet();
            mode = activeExternal.Any(p => src.Contains(p.SourceKey)) ? DisplayMode.Clone : DisplayMode.Extend;
        }
        else mode = DisplayMode.Unknown;

        var sig = $"{mode}|i{activeInternal.Count}|e{activeExternal.Count}|a{externalAvailable.Count}|p{internalPresent}";
        return new DisplayTopology(true, mode, internalPresent, activeInternal.Count > 0,
            externalAvailable.Count, activeExternal.Count, sig);
    }
}
