using System.IO;
using System.Text.Json;

namespace MarkdownLite.Services;

/// <summary>
/// 阅读进度记忆：记录「文件绝对路径 → 滚动百分比（0~1）」。
///
/// 单独存放于 %APPDATA%\MarkdownLite\reading.json，避免把人工可读的 settings.json 撑大；
/// 条目按最后访问时间排序，超过上限时淘汰最久未用的，防止长期使用后无限增长。
/// 只读定位不变：这里记录的是「读到哪儿」，不涉及任何文件内容。
/// </summary>
public sealed class ReadingProgress
{
    private const int MaxEntries = 200;

    /// <summary>单条进度记录。</summary>
    public sealed class Entry
    {
        public double Ratio { get; set; }
        public DateTime LastSeenUtc { get; set; }
    }

    private readonly Dictionary<string, Entry> _entries;
    private readonly string _path;

    private ReadingProgress(Dictionary<string, Entry> entries, string path)
    {
        _entries = entries;
        _path = path;
    }

    private static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MdReader", "reading.json");

    /// <summary>已记录的文件数（测试用于验证上限淘汰）。</summary>
    public int Count => _entries.Count;

    public static ReadingProgress Load(string? path = null)
    {
        var resolved = path ?? DefaultPath;
        try
        {
            if (File.Exists(resolved))
            {
                var map = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(resolved));
                if (map is not null)
                {
                    // 重新套用大小写不敏感比较器（Windows 路径语义）
                    return new ReadingProgress(
                        new Dictionary<string, Entry>(map, StringComparer.OrdinalIgnoreCase), resolved);
                }
            }
        }
        catch (Exception)
        {
            // 进度文件损坏时从头开始，不影响阅读
        }
        return new ReadingProgress(
            new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase), resolved);
    }

    /// <summary>取该文件上次的滚动百分比；从未记录过则返回 null。</summary>
    public double? Get(string path)
    {
        return _entries.TryGetValue(Normalize(path), out var entry) ? entry.Ratio : null;
    }

    /// <summary>记录滚动百分比（自动截断到 0~1）并清理超额条目。</summary>
    public void Set(string path, double ratio)
    {
        if (double.IsNaN(ratio) || double.IsInfinity(ratio)) return;

        _entries[Normalize(path)] = new Entry
        {
            Ratio = Math.Clamp(ratio, 0, 1),
            LastSeenUtc = DateTime.UtcNow,
        };

        if (_entries.Count <= MaxEntries) return;

        var stale = _entries
            .OrderByDescending(pair => pair.Value.LastSeenUtc)
            .Skip(MaxEntries)
            .Select(pair => pair.Key)
            .ToList();
        foreach (var key in stale)
            _entries.Remove(key);
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(_entries, JsonOptions));
        }
        catch (Exception)
        {
            // 保存失败不影响阅读
        }
    }

    private static string Normalize(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception)
        {
            return path;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };
}
