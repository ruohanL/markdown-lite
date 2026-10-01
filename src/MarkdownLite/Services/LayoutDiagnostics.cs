using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace MarkdownLite.Services;

/// <summary>
/// GUI 回归用的布局度量出口（命令行 <c>--diag-layout</c>）。
///
/// 背景：无边框窗口最大化时的几何关系由三股力量叠加而成——
/// 1) <c>WM_GETMINMAXINFO</c> 把最大化尺寸约束到显示器工作区；
/// 2) <c>WindowChrome</c> 又把窗口按 ResizeBorderThickness 撑出工作区（让边框落到屏幕外）；
/// 3) <c>RootBorder</c> 的 Margin 再把内容拉回工作区。
/// 三者必须精确配套，多了会留空隙、少了会被屏幕边缘裁切，而单元测试无法覆盖。
///
/// 用法：<c>MarkdownLite.exe --diag-layout</c>。程序最大化后把度量写入
/// <c>%TEMP%\mdreader-layout.txt</c> 并自动退出。
/// 判定标准：<c>gap L/T/R/B 四项全为 0</c> 即内容区与工作区完全重合（无裁切、无空隙）。
/// </summary>
internal static class LayoutDiagnostics
{
    public static bool Enabled { get; set; }

    public static string ReportPath => Path.Combine(Path.GetTempPath(), "mdreader-layout.txt");

    /// <summary>最大化窗口、等待布局稳定后写出度量报告，最后关闭窗口。</summary>
    public static async Task RunAsync(Window window, FrameworkElement content, int settleMs = 1500)
    {
        window.WindowState = WindowState.Maximized;
        await Task.Delay(settleMs);

        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            GetWindowRect(hwnd, out var wr);
            var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            var mi = new MonitorInfo { cbSize = Marshal.SizeOf<MonitorInfo>() };
            GetMonitorInfo(monitor, ref mi);

            var dpi = VisualTreeHelper.GetDpi(window);
            var origin = content.PointToScreen(new Point(0, 0));
            double right = origin.X + content.ActualWidth * dpi.DpiScaleX;
            double bottom = origin.Y + content.ActualHeight * dpi.DpiScaleY;

            var ci = CultureInfo.InvariantCulture;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine("dpiScale=" + dpi.DpiScaleX.ToString("0.###", ci));
            sb.AppendLine($"windowRect={wr.left},{wr.top},{wr.right},{wr.bottom} size={wr.right - wr.left}x{wr.bottom - wr.top}");
            sb.AppendLine($"workArea={mi.rcWork.left},{mi.rcWork.top},{mi.rcWork.right},{mi.rcWork.bottom} size={mi.rcWork.right - mi.rcWork.left}x{mi.rcWork.bottom - mi.rcWork.top}");
            sb.AppendLine($"contentArea={origin.X:0.#},{origin.Y:0.#},{right:0.#},{bottom:0.#} size={content.ActualWidth * dpi.DpiScaleX:0.#}x{content.ActualHeight * dpi.DpiScaleY:0.#}");
            sb.AppendLine($"gap L={origin.X - mi.rcWork.left:0.#} T={origin.Y - mi.rcWork.top:0.#} R={mi.rcWork.right - right:0.#} B={mi.rcWork.bottom - bottom:0.#}");
            File.WriteAllText(ReportPath, sb.ToString());
        }
        catch (Exception ex)
        {
            File.WriteAllText(ReportPath, "ERR: " + ex);
        }

        window.Close();
    }

    private const int MonitorDefaultToNearest = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MonitorInfo lpmi);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int left; public int top; public int right; public int bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int cbSize;
        public Rect rcMonitor;
        public Rect rcWork;
        public int dwFlags;
    }
}
