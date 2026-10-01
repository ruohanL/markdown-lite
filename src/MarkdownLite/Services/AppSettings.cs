using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MarkdownLite.Services;

/// <summary>
/// 应用设置，仅写入 %APPDATA%\MarkdownLite\settings.json，绝不触碰源文件目录。
/// </summary>
public sealed class AppSettings
{
    /// <summary>目录侧栏的出厂默认：关闭。阅读优先，需要时按 Ctrl+T 再展开。</summary>
    public const bool DefaultTocVisible = false;

    /// <summary>
    /// 界面版式版本。凡是改动「出厂默认的窗口尺寸 / 面板开关 / 窗口状态」，
    /// 就把它 +1，旧版本记录下来的窗口几何与面板开关会一次性作废，新默认值即刻生效。
    /// </summary>
    public const int CurrentLayoutRevision = 3;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ThemeMode Theme { get; set; } = ThemeMode.System;

    public bool TocVisible { get; set; } = DefaultTocVisible;

    // ---- 版式版本（见 CurrentLayoutRevision）----

    public int LayoutRevision { get; set; }

    /// <summary>当前文档在磁盘上变化时是否自动重载。</summary>
    public bool AutoReload { get; set; } = true;

    // ---- 窗口几何（DIP）。Left/Top 为 null 表示尚未记录，交给系统居中 ----

    public double WindowWidth { get; set; }

    public double WindowHeight { get; set; }

    public double? WindowLeft { get; set; }

    public double? WindowTop { get; set; }

    public bool WindowMaximized { get; set; }

    /// <summary>WebView2 缩放系数，1.0 表示 100%。</summary>
    public double ZoomFactor { get; set; } = 1.0;

    private static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "MdReader", "settings.json");

    /// <summary>本实例对应的设置文件（测试可注入临时路径）。</summary>
    private string _path = DefaultPath;

    public static AppSettings Load(string? path = null)
    {
        var resolved = path ?? DefaultPath;
        try
        {
            if (File.Exists(resolved))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(
                    File.ReadAllText(resolved), JsonOptions);
                if (loaded is not null)
                {
                    loaded._path = resolved;
                    return loaded;
                }
            }
        }
        catch (Exception)
        {
            // 设置损坏时回退默认值
        }
        return new AppSettings { _path = resolved };
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllText(_path, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception)
        {
            // 保存失败不影响阅读
        }
    }

    /// <summary>
    /// 把早于 <see cref="CurrentLayoutRevision"/> 的记录迁移到当前版式：清掉旧窗口几何、
    /// 关闭侧栏、取消最大化，让本次启动直接采用新的出厂默认版式。
    /// 用户此后的调整仍会被正常记住。返回是否发生了迁移。
    /// </summary>
    public bool MigrateLayout()
    {
        if (LayoutRevision >= CurrentLayoutRevision) return false;

        LayoutRevision = CurrentLayoutRevision;
        TocVisible = DefaultTocVisible;
        WindowWidth = 0;
        WindowHeight = 0;
        WindowLeft = null;
        WindowTop = null;
        WindowMaximized = false;
        return true;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };
}
