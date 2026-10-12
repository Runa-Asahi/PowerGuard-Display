using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PowerGuard.Core;

public enum LoadStatus { Loaded, Missing, Corrupt }

public sealed record LoadResult<T>(T? Value, LoadStatus Status, string? Message = null);

/// <summary>
/// JSON 文件存储：写入临时文件 → Flush 到磁盘 → 原子替换目标文件。
/// 损坏的文件会被改名保留（*.corrupt-时间戳）便于诊断。
/// </summary>
public class JsonFileStore<T> where T : class
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly object _lock = new();
    public string Path { get; }

    public JsonFileStore(string path) => Path = path;

    public LoadResult<T> LoadCore()
    {
        lock (_lock)
        {
            if (!File.Exists(Path)) return new(null, LoadStatus.Missing);
            try
            {
                var text = File.ReadAllText(Path, Encoding.UTF8);
                var value = JsonSerializer.Deserialize<T>(text, Options);
                if (value is null) throw new JsonException("内容为空或为 null");
                return new(value, LoadStatus.Loaded);
            }
            catch (Exception ex) when (ex is JsonException or NotSupportedException or ArgumentException)
            {
                var backup = $"{Path}.corrupt-{DateTime.Now:yyyyMMddHHmmss}";
                try { File.Move(Path, backup, overwrite: true); } catch { /* 记录在消息中 */ }
                return new(null, LoadStatus.Corrupt, $"{System.IO.Path.GetFileName(Path)} 已损坏（{ex.Message}），已备份为 {System.IO.Path.GetFileName(backup)}");
            }
        }
    }

    public void SaveCore(T value)
    {
        lock (_lock)
        {
            var dir = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = Path + ".tmp";
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Options);
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                fs.Write(bytes);
                fs.Flush(flushToDisk: true);
            }
            // Windows 上为 MoveFileEx(MOVEFILE_REPLACE_EXISTING)，同卷内为原子替换。
            File.Move(tmp, Path, overwrite: true);
        }
    }

    public void DeleteCore()
    {
        lock (_lock)
        {
            if (File.Exists(Path)) File.Delete(Path);
        }
    }
}

public sealed class SettingsStore : JsonFileStore<AppSettings>, ISettingsStore
{
    public SettingsStore(string path) : base(path) { }
    public LoadResult<AppSettings> Load()
    {
        var r = LoadCore();
        if (r.Value is null) return new(new AppSettings(), r.Status, r.Message); // 安全默认值
        r.Value.Normalize();
        return r;
    }
    public void Save(AppSettings s) => SaveCore(s);
}

public sealed class RecoveryStateStore : JsonFileStore<RecoveryState>, IRecoveryStateStore
{
    public RecoveryStateStore(string path) : base(path) { }
    public LoadResult<RecoveryState> Load() => LoadCore();
    public void Save(RecoveryState s) => SaveCore(s);
    public void Clear() => DeleteCore();
}
