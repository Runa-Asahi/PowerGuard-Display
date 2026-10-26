using System.Collections.ObjectModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PowerGuard.Core;

[JsonConverter(typeof(JsonStringEnumConverter<LogCategory>))]
public enum LogCategory { App, Startup, Power, Display, Switch, Recovery, Settings, Error }

[JsonConverter(typeof(JsonStringEnumConverter<LogResult>))]
public enum LogResult { Info, Success, Warning, Failure }

public sealed record LogEntry(DateTimeOffset Time, LogCategory Category, string Message, LogResult Result)
{
    public string CategoryText => Category switch
    {
        LogCategory.App => "程序",
        LogCategory.Startup => "自启动",
        LogCategory.Power => "电源",
        LogCategory.Display => "显示器",
        LogCategory.Switch => "切屏",
        LogCategory.Recovery => "恢复流程",
        LogCategory.Settings => "设置",
        LogCategory.Error => "异常",
        _ => Category.ToString(),
    };
    public string ResultText => Result switch
    {
        LogResult.Success => "成功",
        LogResult.Warning => "警告",
        LogResult.Failure => "失败",
        _ => "信息",
    };
    public override string ToString() => $"{Time:yyyy-MM-dd HH:mm:ss}\t{CategoryText}\t{ResultText}\t{Message}";
}

public interface IAppLogger
{
    void Log(LogCategory category, string message, LogResult result = LogResult.Info);
    event Action<LogEntry>? EntryAdded;
}

/// <summary>仅内存，用于测试。</summary>
public sealed class MemoryLogger : IAppLogger
{
    public List<LogEntry> Entries { get; } = new();
    public event Action<LogEntry>? EntryAdded;
    public void Log(LogCategory c, string m, LogResult r = LogResult.Info)
    {
        var e = new LogEntry(DateTimeOffset.Now, c, m, r);
        lock (Entries) Entries.Add(e);
        EntryAdded?.Invoke(e);
    }
}

/// <summary>
/// 滚动日志：按天分文件（powerguard-yyyyMMdd.log，JSON Lines），
/// 单文件超过 MaxFileBytes 时追加序号；保留天数可配置。
/// 写入使用常驻的 StreamWriter（AutoFlush，进程异常退出也不丢已写入的行），
/// 跨天时自动切换文件并执行一次保留策略。
/// </summary>
public sealed class FileLogger : IAppLogger, IDisposable
{
    private readonly object _lock = new();
    private readonly IClock _clock;
    private readonly List<LogEntry> _recent = new();
    public const int MaxRecent = 3000;
    public long MaxFileBytes { get; init; } = 2 * 1024 * 1024;
    /// <summary>跨天时自动执行的保留策略（由设置同步）。</summary>
    public bool RetainLogs { get; set; } = true;
    public int KeepDays { get; set; } = 14;

    private StreamWriter? _writer;
    private string? _writerPath;
    private long _writerBytes;
    private DateTime _writerDay;
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    public string Directory { get; }
    public event Action<LogEntry>? EntryAdded;

    public FileLogger(string directory, IClock? clock = null)
    {
        Directory = directory;
        _clock = clock ?? new SystemClock();
        System.IO.Directory.CreateDirectory(directory);
    }

    public IReadOnlyList<LogEntry> Recent { get { lock (_lock) return _recent.ToList(); } }

    public void Log(LogCategory category, string message, LogResult result = LogResult.Info)
    {
        var entry = new LogEntry(_clock.Now, category, message, result);
        lock (_lock)
        {
            _recent.Add(entry);
            if (_recent.Count > MaxRecent) _recent.RemoveRange(0, _recent.Count - MaxRecent);
            try
            {
                var line = JsonSerializer.Serialize(entry) + "\n";
                var bytes = Utf8NoBom.GetByteCount(line);
                EnsureWriter(entry.Time, bytes);
                _writer!.Write(line);
                _writerBytes += bytes;
            }
            catch
            {
                // 日志写失败不能影响保护逻辑；条目仍保留在内存中供 UI 显示。下次重新打开文件。
                CloseWriter();
            }
        }
        try { EntryAdded?.Invoke(entry); } catch { }
    }

    private void EnsureWriter(DateTimeOffset t, int nextBytes)
    {
        var day = t.Date;
        if (_writer != null && day == _writerDay && _writerBytes + nextBytes <= MaxFileBytes) return;
        var dayChanged = _writer != null && day != _writerDay;
        CloseWriter();
        if (dayChanged)
        {
            try { ApplyRetentionCore(RetainLogs, KeepDays); } catch { }
        }
        var path = CurrentFile(t);
        var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(fs, Utf8NoBom) { AutoFlush = true };
        _writerPath = path; _writerBytes = fs.Length; _writerDay = day;
    }

    private void CloseWriter()
    {
        try { _writer?.Dispose(); } catch { }
        _writer = null; _writerPath = null; _writerBytes = 0;
    }

    /// <summary>当前正在写入的文件（测试 / 诊断用）。</summary>
    public string? CurrentPath { get { lock (_lock) return _writerPath; } }

    private string CurrentFile(DateTimeOffset t)
    {
        var baseName = $"powerguard-{t:yyyyMMdd}";
        for (var i = 0; ; i++)
        {
            var name = Path.Combine(Directory, i == 0 ? baseName + ".log" : $"{baseName}.{i}.log");
            var fi = new FileInfo(name);
            if (!fi.Exists || fi.Length < MaxFileBytes) return name;
        }
    }

    /// <summary>读取今天及之前文件中的最近条目（用于启动时填充日志页）。</summary>
    public void LoadHistory(int maxEntries = 1000)
    {
        static (string day, int idx) Key(string f)
        {
            var parts = Path.GetFileNameWithoutExtension(f).Split('.');
            return (parts[0], parts.Length > 1 && int.TryParse(parts[1], out var n) ? n : 0);
        }
        var files = System.IO.Directory.GetFiles(Directory, "powerguard-*.log").Select(f => (f, k: Key(f))).ToList();
        var days = files.Select(x => x.k.day).Distinct().OrderBy(d => d, StringComparer.Ordinal).TakeLast(3).ToHashSet();
        var loaded = new List<LogEntry>();
        foreach (var f in files.Where(x => days.Contains(x.k.day)).OrderBy(x => x.k.day, StringComparer.Ordinal).ThenBy(x => x.k.idx).Select(x => x.f))
        {
            try
            {
                using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(fs, Encoding.UTF8);
                while (reader.ReadLine() is { } line)
                {
                    try { var e = JsonSerializer.Deserialize<LogEntry>(line); if (e != null) loaded.Add(e); } catch { }
                }
            }
            catch { }
        }
        lock (_lock)
        {
            _recent.InsertRange(0, loaded.TakeLast(maxEntries));
            if (_recent.Count > MaxRecent) _recent.RemoveRange(0, _recent.Count - MaxRecent);
        }
    }

    /// <summary>清理过期日志。retain=false 时只保留今天的文件。</summary>
    public int ApplyRetention(bool retain, int keepDays = 14)
    {
        lock (_lock)
        {
            RetainLogs = retain; KeepDays = keepDays;
            return ApplyRetentionCore(retain, keepDays);
        }
    }

    private int ApplyRetentionCore(bool retain, int keepDays)
    {
        var cutoff = retain ? _clock.Now.Date.AddDays(-keepDays) : _clock.Now.Date;
        var removed = 0;
        foreach (var f in System.IO.Directory.GetFiles(Directory, "powerguard-*.log"))
        {
            var name = Path.GetFileNameWithoutExtension(f).Split('.')[0];
            if (DateTime.TryParseExact(name.Replace("powerguard-", ""), "yyyyMMdd", null, System.Globalization.DateTimeStyles.None, out var d) && d < cutoff)
            {
                try { File.Delete(f); removed++; } catch { }
            }
        }
        return removed;
    }

    public void ClearAll()
    {
        lock (_lock)
        {
            _recent.Clear();
            CloseWriter();
            foreach (var f in System.IO.Directory.GetFiles(Directory, "powerguard-*.log"))
            {
                try { File.Delete(f); } catch { }
            }
        }
    }

    public void Dispose() { lock (_lock) CloseWriter(); }
}
