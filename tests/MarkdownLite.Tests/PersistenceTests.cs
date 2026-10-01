using System.IO;
using MarkdownLite.Services;
using Xunit;

namespace MarkdownLite.Tests;

/// <summary>
/// 设置与阅读进度的落盘 / 读取。全部使用临时文件，不触碰用户真实的
/// %APPDATA%\MarkdownLite 配置。
/// </summary>
public class PersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "mdreader-tests-" + Guid.NewGuid().ToString("N"));

    private string File(string name)
    {
        Directory.CreateDirectory(_dir);
        return Path.Combine(_dir, name);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }
        catch (Exception)
        {
            // 清理失败不影响测试结论
        }
    }

    // ---------------- AppSettings ----------------

    [Fact]
    public void Settings_MissingFile_FallsBackToDefaults()
    {
        var settings = AppSettings.Load(Path.Combine(_dir, "not-exists.json"));
        Assert.Equal(ThemeMode.System, settings.Theme);
        Assert.False(settings.TocVisible);   // 出厂默认：侧栏关闭，阅读优先
        Assert.True(settings.AutoReload);
        Assert.Equal(1.0, settings.ZoomFactor);
        Assert.Null(settings.WindowLeft);
    }

    [Fact]
    public void Settings_RoundTrip_PreservesAllFields()
    {
        var path = File("settings.json");
        var original = AppSettings.Load(path);
        original.MigrateLayout();            // 先迁移到当前版式，避免回读时被再次作废
        original.Theme = ThemeMode.Dark;
        original.TocVisible = false;
        original.AutoReload = false;
        original.WindowWidth = 1440;
        original.WindowHeight = 900;
        original.WindowLeft = 120;
        original.WindowTop = 60;
        original.WindowMaximized = true;
        original.ZoomFactor = 1.35;
        original.Save();

        var loaded = AppSettings.Load(path);

        Assert.Equal(ThemeMode.Dark, loaded.Theme);
        Assert.False(loaded.TocVisible);
        Assert.False(loaded.AutoReload);
        Assert.Equal(1440, loaded.WindowWidth);
        Assert.Equal(900, loaded.WindowHeight);
        Assert.Equal(120, loaded.WindowLeft);
        Assert.Equal(60, loaded.WindowTop);
        Assert.True(loaded.WindowMaximized);
        Assert.Equal(1.35, loaded.ZoomFactor);
    }

    [Fact]
    public void Settings_LegacyRecord_IsMigratedOnceToNewLayoutDefaults()
    {
        var path = File("legacy.json");
        // 模拟旧版式留下的记录：侧栏开着、窗口几何是旧默认值、上次最大化
        System.IO.File.WriteAllText(path, """
            {
              "Theme": "Light",
              "TocVisible": true,
              "AutoReload": true,
              "WindowWidth": 1180,
              "WindowHeight": 780,
              "WindowLeft": 200,
              "WindowTop": 80,
              "WindowMaximized": true,
              "ZoomFactor": 1
            }
            """);

        var settings = AppSettings.Load(path);
        Assert.True(settings.MigrateLayout());

        Assert.Equal(AppSettings.CurrentLayoutRevision, settings.LayoutRevision);
        Assert.False(settings.TocVisible);
        Assert.Equal(0, settings.WindowWidth);
        Assert.Equal(0, settings.WindowHeight);
        Assert.Null(settings.WindowLeft);
        Assert.Null(settings.WindowTop);
        Assert.False(settings.WindowMaximized);
        // 与版式无关的偏好必须保留
        Assert.Equal(ThemeMode.Light, settings.Theme);
        Assert.True(settings.AutoReload);
    }

    [Fact]
    public void Settings_Migration_IsIdempotent()
    {
        var path = File("twice.json");
        var settings = AppSettings.Load(path);
        Assert.True(settings.MigrateLayout());

        settings.WindowWidth = 1024;
        settings.WindowHeight = 720;
        settings.TocVisible = true;

        // 第二次调用不得再破坏用户已保存的版式偏好
        Assert.False(settings.MigrateLayout());
        Assert.Equal(1024, settings.WindowWidth);
        Assert.Equal(720, settings.WindowHeight);
        Assert.True(settings.TocVisible);
    }

    [Fact]
    public void Settings_CorruptFile_FallsBackToDefaults()
    {
        var path = File("broken.json");
        System.IO.File.WriteAllText(path, "{ this is not json ");
        var settings = AppSettings.Load(path);
        Assert.Equal(ThemeMode.System, settings.Theme);
    }

    // ---------------- ReadingProgress ----------------

    [Fact]
    public void Progress_MissingFile_ReturnsNull()
    {
        var progress = ReadingProgress.Load(Path.Combine(_dir, "none.json"));
        Assert.Null(progress.Get(@"C:\docs\a.md"));
        Assert.Equal(0, progress.Count);
    }

    [Fact]
    public void Progress_RoundTrip()
    {
        var path = File("reading.json");
        var progress = ReadingProgress.Load(path);
        progress.Set(@"C:\docs\a.md", 0.4213);
        progress.Save();

        var reloaded = ReadingProgress.Load(path);
        Assert.Equal(0.4213, reloaded.Get(@"C:\docs\a.md"));
        Assert.Null(reloaded.Get(@"C:\docs\b.md"));
    }

    [Theory]
    [InlineData(1.5, 1.0)]
    [InlineData(-0.2, 0.0)]
    [InlineData(0.5, 0.5)]
    public void Progress_ClampsRatio(double input, double expected)
    {
        var progress = ReadingProgress.Load(Path.Combine(_dir, "clamp.json"));
        progress.Set(@"C:\docs\a.md", input);
        Assert.Equal(expected, progress.Get(@"C:\docs\a.md"));
    }

    [Fact]
    public void Progress_IgnoresNaNRatio()
    {
        var progress = ReadingProgress.Load(Path.Combine(_dir, "nan.json"));
        progress.Set(@"C:\docs\a.md", double.NaN);
        Assert.Null(progress.Get(@"C:\docs\a.md"));
    }

    [Fact]
    public void Progress_PathLookup_IsCaseInsensitive()
    {
        var progress = ReadingProgress.Load(Path.Combine(_dir, "case.json"));
        progress.Set(@"C:\Docs\Readme.MD", 0.3);
        Assert.Equal(0.3, progress.Get(@"c:\docs\readme.md"));
    }

    [Fact]
    public void Progress_EvictsOldest_WhenOverLimit()
    {
        var progress = ReadingProgress.Load(Path.Combine(_dir, "many.json"));
        for (int i = 0; i < 260; i++)
            progress.Set($@"C:\docs\file{i}.md", 0.5);

        Assert.Equal(200, progress.Count);
        // 最新写入的必须仍在
        Assert.Equal(0.5, progress.Get(@"C:\docs\file259.md"));
    }
}
