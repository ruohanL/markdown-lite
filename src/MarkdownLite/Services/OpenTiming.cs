using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace MarkdownLite.Services;

/// <summary>
/// 「打开文档」分阶段耗时测量（GUI 回归专用）：
/// 命令行 <c>--diag-open &lt;file&gt;</c> 时启用，从**进程启动**计时到下列三个阶段，
/// 分别写进 <c>%TEMP%</c> 下的三个报告文件（一阶段一个文件，各只记一次）：
///
/// <list type="bullet">
/// <item><c>mdreader-opentime-prepared.txt</c> —— 预处理完成（读盘/编码/字数/解析/渲染/落盘），**不依赖浏览器**</item>
/// <item><c>mdreader-opentime-presented.txt</c> —— 已交给 WebView2（Navigate 已发起）</item>
/// <item><c>mdreader-opentime-navcompleted.txt</c> —— 浏览器完成导航（依赖 WebView2 环境健康）</item>
/// </list>
///
/// <para>拆成三个阶段的原因：WebView2 环境故障（历史踩坑记录）会让
/// <c>NavigationCompleted</c> 长时间不出现，只有一个总时长就拿不到数据；
/// 拆开后至少「预处理」这一段在任何环境下都能测。</para>
///
/// <para>计时零点取 <see cref="App.StartupTimestamp"/>（App 类型初始化，托管代码里最早可取的点）。
/// 本类只写报告、不退出进程，由测量脚本负责收尾。</para>
/// </summary>
internal static class OpenTiming
{
    private static string? _targetPath;
    private static readonly HashSet<string> Reported = new();

    /// <summary>是否处于计时模式。</summary>
    public static bool Enabled => _targetPath is not null;

    /// <summary>启用计时模式并指定目标文档。</summary>
    public static void Enable(string path)
    {
        try { _targetPath = Path.GetFullPath(path); }
        catch (Exception) { _targetPath = path; }
    }

    /// <summary>预处理完成（准备文档已经落盘）。由 <c>PrepareDocumentAsync</c> 调用。</summary>
    public static void ReportPrepared(string? currentPath) => Write("prepared", currentPath);

    /// <summary>已交给 WebView2（Navigate 已发起）。由 <c>PresentDocumentAsync</c> 调用。</summary>
    public static void ReportPresented(string? currentPath) => Write("presented", currentPath);

    /// <summary>浏览器完成导航。由 <c>OnNavigationCompleted</c> 调用。</summary>
    public static void Report(string? currentPath) => Write("navcompleted", currentPath);

    private static void Write(string stage, string? currentPath)
    {
        if (_targetPath is null || currentPath is null) return;
        if (!Reported.Add(stage)) return;          // 每个阶段只记一次

        bool isTarget;
        try { isTarget = string.Equals(Path.GetFullPath(currentPath), _targetPath, StringComparison.OrdinalIgnoreCase); }
        catch (Exception) { return; }
        if (!isTarget) return;                     // 欢迎页 / 其它文档的导航不算

        var elapsedTicks = Stopwatch.GetTimestamp() - App.StartupTimestamp;
        var ms = elapsedTicks * 1000.0 / Stopwatch.Frequency;

        try
        {
            File.WriteAllText(
                Path.Combine(Path.GetTempPath(), $"mdreader-opentime-{stage}.txt"),
                ms.ToString("0", CultureInfo.InvariantCulture));
        }
        catch (Exception)
        {
            // 报告写不出就没有数据，但不影响应用本身
        }
    }
}
