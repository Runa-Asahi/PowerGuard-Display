using System.Diagnostics;
using System.Reflection;

namespace PowerGuard.App;

/// <summary>程序版本：来自程序集的 InformationalVersion（由 csproj 的 Version 或 CI 按 tag 传入的 -p:Version 生成）。</summary>
public static class AppVersion
{
    private static readonly string Informational =
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(AppVersion).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    /// <summary>例如 "1.2.0"。</summary>
    public static string Version => Informational.Split('+')[0];

    /// <summary>构建所用提交的短 SHA（可能为空）。</summary>
    public static string? Commit => Informational.Contains('+') ? Informational.Split('+')[1] is { Length: > 0 } c ? c[..Math.Min(7, c.Length)] : null : null;

    /// <summary>例如 "v1.2.0 (cd822e6)"。</summary>
    public static string Display => Commit is null ? $"v{Version}" : $"v{Version} ({Commit})";

    /// <summary>读取另一个 exe 的产品版本（用于识别正在运行的旧实例）。</summary>
    public static string? Of(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath) || !File.Exists(exePath)) return null;
        try { return FileVersionInfo.GetVersionInfo(exePath).ProductVersion?.Split('+')[0]; } catch { return null; }
    }
}
