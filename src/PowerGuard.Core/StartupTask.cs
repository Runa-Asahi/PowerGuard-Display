using System.Security;
using System.Text;
using System.Xml.Linq;

namespace PowerGuard.Core;

public sealed record StartupTaskStatus(bool Exists, bool Enabled, string? Command, bool PointsToCurrentExe, string? Error);

/// <summary>
/// 通过 schtasks.exe 管理“当前用户登录时启动”的计划任务。
/// 使用 XML 定义：LogonTrigger 限定当前用户、InteractiveToken、LeastPrivilege（无需管理员），
/// 允许在电池下运行，有限的失败重启（只在任务启动失败时生效，用户正常退出 exit code 0 不会被拉起）。
/// </summary>
public sealed class StartupTaskService
{
    public const string TaskName = "PowerGuardDisplay";
    public const string AutostartArg = "--autostart";
    private readonly IProcessRunner _runner;
    private readonly string _schtasks;
    private readonly string _tempDir;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <param name="tempDir">临时任务 XML 的存放目录。默认使用程序自己的数据目录而不是公共 %TEMP%。</param>
    public StartupTaskService(IProcessRunner runner, string schtasksPath, string? tempDir = null)
    {
        _runner = runner; _schtasks = schtasksPath; _tempDir = tempDir ?? Path.GetTempPath();
    }

    public static string BuildTaskXml(string exePath, string userId)
    {
        XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
        var doc = new XDocument(new XDeclaration("1.0", "UTF-16", null),
            new XElement(ns + "Task", new XAttribute("version", "1.2"),
                new XElement(ns + "RegistrationInfo",
                    new XElement(ns + "Description", "PowerGuard Display（断电护屏）：当前用户登录后启动，后台托盘运行。")),
                new XElement(ns + "Triggers",
                    new XElement(ns + "LogonTrigger",
                        new XElement(ns + "Enabled", "true"),
                        new XElement(ns + "UserId", userId),
                        new XElement(ns + "Delay", "PT10S"))),
                new XElement(ns + "Principals",
                    new XElement(ns + "Principal", new XAttribute("id", "Author"),
                        new XElement(ns + "UserId", userId),
                        new XElement(ns + "LogonType", "InteractiveToken"),
                        new XElement(ns + "RunLevel", "LeastPrivilege"))),
                new XElement(ns + "Settings",
                    new XElement(ns + "MultipleInstancesPolicy", "IgnoreNew"),
                    new XElement(ns + "DisallowStartIfOnBatteries", "false"),
                    new XElement(ns + "StopIfGoingOnBatteries", "false"),
                    new XElement(ns + "AllowHardTerminate", "true"),
                    new XElement(ns + "StartWhenAvailable", "true"),
                    new XElement(ns + "RunOnlyIfNetworkAvailable", "false"),
                    new XElement(ns + "IdleSettings",
                        new XElement(ns + "StopOnIdleEnd", "false"),
                        new XElement(ns + "RestartOnIdle", "false")),
                    new XElement(ns + "AllowStartOnDemand", "true"),
                    new XElement(ns + "Enabled", "true"),
                    new XElement(ns + "Hidden", "false"),
                    new XElement(ns + "RunOnlyIfIdle", "false"),
                    new XElement(ns + "WakeToRun", "false"),
                    new XElement(ns + "ExecutionTimeLimit", "PT0S"),
                    new XElement(ns + "Priority", "7"),
                    new XElement(ns + "RestartOnFailure",
                        new XElement(ns + "Interval", "PT1M"),
                        new XElement(ns + "Count", "3"))),
                new XElement(ns + "Actions", new XAttribute("Context", "Author"),
                    new XElement(ns + "Exec",
                        new XElement(ns + "Command", exePath),
                        new XElement(ns + "Arguments", AutostartArg),
                        new XElement(ns + "WorkingDirectory", Path.GetDirectoryName(exePath) ?? "")))));
        var sb = new StringBuilder();
        using (var w = new Utf16StringWriter(sb)) doc.Save(w);
        return sb.ToString();
    }

    private sealed class Utf16StringWriter(StringBuilder sb) : StringWriter(sb)
    {
        public override Encoding Encoding => Encoding.Unicode;
    }

    public static IReadOnlyList<string> CreateArgs(string xmlPath) => ["/Create", "/TN", TaskName, "/XML", xmlPath, "/F"];
    public static IReadOnlyList<string> DeleteArgs() => ["/Delete", "/TN", TaskName, "/F"];
    public static IReadOnlyList<string> QueryArgs() => ["/Query", "/TN", TaskName, "/XML"];

    public async Task<StartupTaskStatus> QueryAsync(string currentExe)
    {
        var r = await _runner.RunAsync(_schtasks, QueryArgs(), Timeout);
        if (r.StartError != null) return new(false, false, null, false, $"无法启动 schtasks.exe：{r.StartError}");
        if (r.ExitCode != 0) return new(false, false, null, false, null); // 任务不存在
        return ParseQuery(r.StdOut, currentExe);
    }

    public static StartupTaskStatus ParseQuery(string xml, string currentExe)
    {
        try
        {
            var doc = XDocument.Parse(xml.Trim().TrimStart('\uFEFF'));
            var ns = doc.Root!.Name.Namespace;
            var enabled = doc.Descendants(ns + "Settings").Elements(ns + "Enabled").FirstOrDefault()?.Value;
            var cmd = doc.Descendants(ns + "Exec").Elements(ns + "Command").FirstOrDefault()?.Value?.Trim().Trim('"');
            var points = cmd != null && string.Equals(Path.GetFullPath(cmd), Path.GetFullPath(currentExe), StringComparison.OrdinalIgnoreCase);
            return new(true, !string.Equals(enabled, "false", StringComparison.OrdinalIgnoreCase), cmd, points, null);
        }
        catch (Exception ex)
        {
            return new(true, true, null, false, $"任务存在，但无法解析其定义：{ex.Message}");
        }
    }

    public async Task<OperationResult> EnableAsync(string exePath, string userId)
    {
        var xmlPath = Path.Combine(_tempDir, $"PowerGuardDisplay-task-{Guid.NewGuid():N}.xml");
        try
        {
            Directory.CreateDirectory(_tempDir);
            await File.WriteAllTextAsync(xmlPath, BuildTaskXml(exePath, userId), Encoding.Unicode);
            var r = await _runner.RunAsync(_schtasks, CreateArgs(xmlPath), Timeout);
            if (r.Ok) return new(true, "已创建登录自启动任务");
            return new(false, $"创建自启动任务失败：{Describe(r)}", ManualHelp);
        }
        catch (Exception ex) { return new(false, $"创建自启动任务失败：{ex.Message}", ManualHelp); }
        finally { try { File.Delete(xmlPath); } catch { } }
    }

    public async Task<OperationResult> DisableAsync()
    {
        var r = await _runner.RunAsync(_schtasks, DeleteArgs(), Timeout);
        if (r.Ok) return new(true, "已删除登录自启动任务");
        // 任务本来就不存在时 schtasks 返回 1；再查询一次确认
        var q = await _runner.RunAsync(_schtasks, QueryArgs(), Timeout);
        if (q.StartError is null && q.ExitCode != 0) return new(true, "自启动任务不存在（已是禁用状态）");
        return new(false, $"删除自启动任务失败：{Describe(r)}", ManualHelp);
    }

    private static string Describe(ProcessResult r) =>
        r.StartError ?? $"退出码 {r.ExitCode}：{(string.IsNullOrWhiteSpace(r.StdErr) ? r.StdOut : r.StdErr).Trim()}";

    public const string ManualHelp = "手动排查：按 Win+R 输入 taskschd.msc 打开任务计划程序，在“任务计划程序库”中查找 PowerGuardDisplay；" +
        "或在命令提示符运行 schtasks /Query /TN PowerGuardDisplay /V /FO LIST 查看详情。若组策略禁止普通用户创建计划任务，请联系管理员或手动创建。";
}

/// <summary>控制台上“登录自启动”相关的提醒文案（纯函数，便于测试）。</summary>
public static class StartupAdvice
{
    public static string? Warning(StartupTaskStatus? st)
    {
        if (st is null || (st.Error != null && !st.Exists)) return null; // 未知时不打扰
        if (!st.Exists) return "未开启登录自启动：断电关机后再次开机，程序不会自动运行，也就不会自动恢复外接显示器。可在“设置”中开启。";
        if (!st.Enabled) return "登录自启动任务已被禁用（任务计划程序中）：重新开机后不会自动运行。可在“设置”中重新保存以修复。";
        if (st.Command != null && !st.PointsToCurrentExe) return "登录自启动指向其他位置的程序（可能是旧版本）：可在“设置”中重新保存，改为当前程序。";
        return null;
    }
}
