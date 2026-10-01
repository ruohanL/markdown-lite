using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace MarkdownLite.Services;

/// <summary>
/// Windows 11 系统圆角（<c>DWMWA_WINDOW_CORNER_PREFERENCE</c>）。
///
/// <para>走 DWM 合成层实现，**不做 WPF 裁剪**：没有 Clip 几何开销，也没有
/// OpacityMask 那种「强制软件渲染」的代价，调整窗口大小时不会掉帧。</para>
///
/// <para>Windows 10 不支持该属性，调用失败就静默保持直角——整体退回直角，
/// 不会出现「WebView2 方角露边 / 黑边 / 白边」这类半圆角的不一致状态。</para>
///
/// <para>⚠ 子 HWND（WebView2）无法被 WPF 裁剪，这里也**刻意不做**裁剪：
/// 阅读区底色与窗口同色，圆角由 DWM 统一裁切。</para>
/// </summary>
internal static class WindowRounding
{
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwcpRound = 2;

    /// <summary>给窗口启用系统圆角。须在 HWND 建立之后调用（OnSourceInitialized）。</summary>
    public static void Apply(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            var preference = DwmwcpRound;
            DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref preference, sizeof(int));
        }
        catch (Exception)
        {
            // 不支持圆角的系统：静默保持直角
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
