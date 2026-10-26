namespace PowerGuard.Core;

/// <summary>崩溃后自动重启的频率限制：一小时内最多 3 次，避免启动即崩溃时无限循环。</summary>
public static class CrashRelaunchPolicy
{
    public const int MaxPerHour = 3;

    public static bool ShouldRelaunch(IEnumerable<DateTimeOffset> previous, DateTimeOffset now) =>
        previous.Count(t => now - t < TimeSpan.FromHours(1) && t <= now) < MaxPerHour;

    /// <summary>历史记录文件：每行一个 ISO 时间。读取失败视为空。</summary>
    public static List<DateTimeOffset> Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? File.ReadAllLines(path).Select(l => DateTimeOffset.TryParse(l, out var t) ? t : (DateTimeOffset?)null).OfType<DateTimeOffset>().ToList()
                : [];
        }
        catch { return []; }
    }

    public static void Append(string path, DateTimeOffset now)
    {
        try
        {
            var keep = Load(path).Where(t => now - t < TimeSpan.FromHours(1)).Append(now).Select(t => t.ToString("O"));
            File.WriteAllLines(path, keep);
        }
        catch { }
    }
}
