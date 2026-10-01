using System.IO;
using MarkdownLite.Services;
using Xunit;

namespace MarkdownLite.Tests;

/// <summary>
/// 自动重载的监听层：文件被外部修改后必须触发回调，停止监听后必须不再触发。
/// 这一层用真实的 FileSystemWatcher 做端到端断言——它无法靠截图验证
/// （本机 WebView2 渲染受限），所以自动化测试是这层唯一的可靠保障。
/// </summary>
public class DocumentWatcherTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "mdreader-watch-" + Guid.NewGuid().ToString("N"));

    private string CreateFile(string name = "文档.md")
    {
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, "初始内容\n");
        return path;
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

    [Fact]
    public async Task Watch_Triggers_OnExternalAppend()
    {
        var file = CreateFile();
        var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new DocumentWatcher(() => signal.TrySetResult(true));

        watcher.Watch(file);
        await Task.Delay(300); // 等 FileSystemWatcher 真正就绪
        await File.AppendAllTextAsync(file, "外部追加\n");

        var finished = await Task.WhenAny(signal.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(finished == signal.Task, "文件被外部追加后未触发回调");
    }

    [Fact]
    public async Task Watch_Triggers_OnFileRewritten()
    {
        // 编辑器常见的「整体重写」保存方式
        var file = CreateFile();
        var signal = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watcher = new DocumentWatcher(() => signal.TrySetResult(true));

        watcher.Watch(file);
        await Task.Delay(300);
        File.WriteAllText(file, "全新内容\n");

        var finished = await Task.WhenAny(signal.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        Assert.True(finished == signal.Task, "文件被重写后未触发回调");
    }

    [Fact]
    public async Task Stop_Prevents_FurtherNotifications()
    {
        var file = CreateFile();
        var count = 0;
        using var watcher = new DocumentWatcher(() => Interlocked.Increment(ref count));

        watcher.Watch(file);
        await Task.Delay(300);
        watcher.Stop();
        await Task.Delay(250);
        Interlocked.Exchange(ref count, 0);

        await File.AppendAllTextAsync(file, "停止后写入\n");
        await Task.Delay(1200); // 远超 350ms 防抖窗口

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Watch_SwitchingTarget_OnlyWatchesLatest()
    {
        var first = CreateFile("a.md");
        var second = CreateFile("b.md");
        var count = 0;
        using var watcher = new DocumentWatcher(() => Interlocked.Increment(ref count));

        watcher.Watch(first);
        watcher.Watch(second);
        await Task.Delay(300);

        await File.AppendAllTextAsync(first, "旧目标\n");
        await Task.Delay(900);
        Assert.Equal(0, count);

        await File.AppendAllTextAsync(second, "新目标\n");
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (count == 0 && DateTime.UtcNow < deadline) await Task.Delay(100);
        Assert.True(count > 0, "切换到新目标后未触发回调");
    }

    [Fact]
    public void Watch_NonexistentDirectory_DoesNotThrow()
    {
        using var watcher = new DocumentWatcher(() => { });
        watcher.Watch(Path.Combine(_dir, "不存在", "a.md"));
    }
}
