using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using Microsoft.Web.WebView2.Core;

namespace MarkdownLite.Services;

/// <summary>
/// 滚动流畅度与内存测量（GUI 回归专用）。
///
/// <para>由命令行开关启用，默认关闭，正常使用路径零开销：</para>
/// <list type="bullet">
/// <item><c>--diag-scroll</c>：文档打开后滚动 10 秒，用 reader.js 的 <c>mdScrollPerfTest</c>
///   以 rAF 计帧，统计平均 FPS / 最低 FPS / 掉帧数。</item>
/// <item><c>--diag-memory</c>：文档打开后滚动 30 秒，记录进程工作集、GC 各代回收次数、托管堆大小；
///   msedgewebview2.exe 子进程内存由外部测量脚本按命令行过滤采集（本机可能有其它 WebView2 宿主，
///   按用户数据目录路径区分，宿主侧拿不到可靠归属）。</item>
/// </list>
///
/// <para>测量结果写到 <c>%TEMP%\mdreader-diag\&lt;时间戳&gt;\</c>，写完自动退出进程
/// （这是测量工具，不是应用路径）。</para>
/// </summary>
internal static class DiagPerf
{
    private static bool _scrollFps;
    private static bool _memory;
    private static bool _started;
    private static string? _label;

    private static readonly string OutDir = Path.Combine(
        Path.GetTempPath(), "mdreader-diag", DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));

    /// <summary>是否处于任一测量模式。</summary>
    public static bool Enabled => _scrollFps || _memory;

    public static void EnableScrollFps()
    {
        _scrollFps = true;
        EnsureOutDir();
    }

    public static void EnableMemory()
    {
        _memory = true;
        EnsureOutDir();
    }

    private static void EnsureOutDir()
    {
        try { Directory.CreateDirectory(OutDir); }
        catch (Exception) { /* 写不出就不写报告 */ }
    }

    /// <summary>
    /// 目标文档导航完成后调用一次（<see cref="MainWindow.OnNavigationCompleted"/>）。
    /// 启动 reader.js 的滚动 + rAF 采样；结果经 WebMessage 回来后由
    /// <see cref="OnScrollPerfReport"/> 统计并写报告。
    /// </summary>
    public static void RunOnDocumentReady(CoreWebView2? core, string? currentPath)
    {
        if (_started || !Enabled) return;
        if (core is null || currentPath is null) return;

        _started = true;
        _label = Path.GetFileNameWithoutExtension(currentPath);
        var durationMs = _memory ? 30000 : 10000;
        _ = RunAsync(core, durationMs);
    }

    private static async Task RunAsync(CoreWebView2 core, int durationMs)
    {
        try
        {
            await Task.Delay(800);   // 等首帧样式与字体稳定，避免把初始化卡顿算进滚动帧率
            await core.ExecuteScriptAsync($"mdScrollPerfTest({durationMs}, 0.05)");
        }
        catch (Exception ex)
        {
            ErrorLog.Write("滚动性能采样失败", ex);
        }
    }

    /// <summary>
    /// 收 reader.js 回传的 <c>scrollperf:&lt;时长ms&gt;:&lt;每秒帧数列表&gt;</c>，统计并写报告。
    /// </summary>
    public static void OnScrollPerfReport(string message)
    {
        var parts = message.Split(':', 3);
        if (parts.Length < 3) return;

        if (!int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var elapsedMs))
            return;
        var perSecond = parts[2]
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0)
            .Where(v => v >= 0)
            .ToList();
        if (perSecond.Count == 0) return;

        var frames = perSecond.Sum();
        var avg = frames * 1000.0 / Math.Max(1, elapsedMs);
        var min = perSecond.Min();
        // 「机器可达帧率」以最好的一秒为准（不假设刷新率，兼容 60/120/144Hz 屏）
        var achievable = perSecond.Max();
        var dropped = perSecond.Sum(f => Math.Max(0, achievable - f));

        var lines = new StringBuilder();
        lines.AppendLine($"avgFps={avg.ToString("0.0", CultureInfo.InvariantCulture)}");
        lines.AppendLine($"minFps={min}");
        lines.AppendLine($"droppedFrames={dropped}");
        lines.AppendLine($"elapsedMs={elapsedMs}");
        lines.AppendLine("perSecond=" + string.Join(",", perSecond));
        WriteReport((_scrollFps ? "scrollfps" : "scroll") + $"-{_label}.txt", lines.ToString());

        if (_scrollFps)
        {
            Environment.Exit(0);
            return;
        }

        // --diag-memory：滚动结束后再采集 .NET 侧内存
        CollectMemory();
    }

    private static void CollectMemory()
    {
        var proc = Process.GetCurrentProcess();
        var lines = new StringBuilder();
        lines.AppendLine($"workingSetMB={(proc.WorkingSet64 / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture)}");
        lines.AppendLine($"privateMB={(proc.PrivateMemorySize64 / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture)}");
        lines.AppendLine($"gcGen0={GC.CollectionCount(0)}");
        lines.AppendLine($"gcGen1={GC.CollectionCount(1)}");
        lines.AppendLine($"gcGen2={GC.CollectionCount(2)}");
        lines.AppendLine($"managedHeapMB={(GC.GetTotalMemory(false) / 1048576.0).ToString("0.0", CultureInfo.InvariantCulture)}");
        WriteReport($"memory-{_label}.txt", lines.ToString());

        // 留 3 秒给外部脚本采样 msedgewebview2.exe 子进程内存，然后退出
        Thread.Sleep(3000);
        Environment.Exit(0);
    }

    private static void WriteReport(string fileName, string content)
    {
        try
        {
            Directory.CreateDirectory(OutDir);
            File.WriteAllText(Path.Combine(OutDir, fileName), content);
        }
        catch (Exception)
        {
            // 报告写不出就没有数据，不影响退出
        }
    }
}
