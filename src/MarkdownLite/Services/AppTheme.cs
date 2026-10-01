using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace MarkdownLite.Services;

public enum ThemeMode
{
    System,
    Light,
    Dark,
}

/// <summary>
/// WPF 侧主题（黑白灰、毛玻璃风格）与 WebView2 页面主题的统一切换。
/// 画刷写入 Application.Current.Resources，界面用 DynamicResource 引用。
/// </summary>
public static class AppTheme
{
    public static ThemeMode Mode { get; private set; } = ThemeMode.System;

    public static event Action? Applied;

    /// <summary>WebView2 页面使用的主题字符串：light / dark / auto。</summary>
    public static string WebViewThemeValue => Mode switch
    {
        ThemeMode.Light => "light",
        ThemeMode.Dark => "dark",
        _ => "auto",
    };

    public static bool IsDarkEffective => Mode switch
    {
        ThemeMode.Light => false,
        ThemeMode.Dark => true,
        _ => IsSystemDark(),
    };

    public static void Apply(ThemeMode mode)
    {
        Mode = mode;
        var dark = IsDarkEffective;
        var res = Application.Current.Resources;

        if (dark)
        {
            // 暗色：标题栏 #262A32（与 #F0F3F9 同色系，只是压暗），其余统一 #1E1E1E
            res["Brush.TitleBar"] = Freeze(new SolidColorBrush(Color.FromRgb(0x26, 0x2A, 0x32)));
            res["Brush.Window"] = Freeze(new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)));
            res["Brush.Bar"] = Freeze(new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)));
            // 目录栏必须与正文同色，否则两侧拼合处会露出色带
            res["Brush.Toc"] = Freeze(new SolidColorBrush(Color.FromRgb(0x1E, 0x1E, 0x1E)));
            res["Brush.Border"] = Freeze(new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)));
            res["Brush.Text"] = Freeze(new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4)));
            res["Brush.TextDim"] = Freeze(new SolidColorBrush(Color.FromArgb(0x99, 0xD4, 0xD4, 0xD4)));
            res["Brush.Hover"] = Freeze(new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)));
            res["Brush.Accent"] = Freeze(new SolidColorBrush(Color.FromRgb(0x80, 0xBE, 0xFF)));
            res["Brush.Menu"] = Freeze(new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)));
            res["Brush.ScrollThumb"] = Freeze(new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)));

            // 菜单弹出层默认走系统浅色模板，用 SystemColors 键覆盖为暗色
            res[SystemColors.MenuBrushKey] = Freeze(new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20)));
            res[SystemColors.MenuTextBrushKey] = Freeze(new SolidColorBrush(Color.FromRgb(0xD4, 0xD4, 0xD4)));
            res[SystemColors.MenuHighlightBrushKey] = Freeze(new SolidColorBrush(Color.FromRgb(0x36, 0x36, 0x36)));
            res[SystemColors.HighlightBrushKey] = Freeze(new SolidColorBrush(Color.FromRgb(0x2E, 0x4C, 0x6E)));
            res[SystemColors.HighlightTextBrushKey] = Freeze(new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0xE8)));
        }
        else
        {
            // 亮色：标题栏 #F0F3F9（微冷调的浅灰），其余统一 #FFFFFF
            res["Brush.TitleBar"] = Freeze(new SolidColorBrush(Color.FromRgb(0xF0, 0xF3, 0xF9)));
            res["Brush.Window"] = Freeze(new SolidColorBrush(Colors.White));
            res["Brush.Bar"] = Freeze(new SolidColorBrush(Colors.White));
            res["Brush.Toc"] = Freeze(new SolidColorBrush(Colors.White));
            res["Brush.Border"] = Freeze(new SolidColorBrush(Color.FromArgb(0x26, 0x00, 0x00, 0x00)));
            res["Brush.Text"] = Freeze(new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1F)));
            res["Brush.TextDim"] = Freeze(new SolidColorBrush(Color.FromRgb(0x6E, 0x6E, 0x76)));
            res["Brush.Hover"] = Freeze(new SolidColorBrush(Color.FromArgb(0x0F, 0x00, 0x00, 0x00)));
            res["Brush.Accent"] = Freeze(new SolidColorBrush(Color.FromRgb(0x33, 0x51, 0x7B)));
            res["Brush.Menu"] = Freeze(new SolidColorBrush(Colors.White));
            res["Brush.ScrollThumb"] = Freeze(new SolidColorBrush(Color.FromArgb(0x40, 0x00, 0x00, 0x00)));

            res[SystemColors.MenuBrushKey] = Freeze(new SolidColorBrush(Colors.White));
            res[SystemColors.MenuTextBrushKey] = Freeze(new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1F)));
            res[SystemColors.MenuHighlightBrushKey] = Freeze(new SolidColorBrush(Color.FromRgb(0xE4, 0xE4, 0xEA)));
            res[SystemColors.HighlightBrushKey] = Freeze(new SolidColorBrush(Color.FromRgb(0xC2, 0xD5, 0xEE)));
            res[SystemColors.HighlightTextBrushKey] = Freeze(new SolidColorBrush(Color.FromRgb(0x1B, 0x1B, 0x1F)));
        }

        if (Application.Current.MainWindow is { } window)
            ApplyWindowChrome(window, dark);

        Applied?.Invoke();
    }

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }

    /// <summary>Win11：Mica 窗口背景 + 深色标题栏；失败时静默降级为纯色背景。</summary>
    public static void ApplyWindowChrome(Window window, bool dark)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            int build = Environment.OSVersion.Version.Build;

            if (build >= 22000)
            {
                int darkTitleBar = dark ? 1 : 0;
                int attribute = build >= 22621 ? DWMWA_USE_IMMERSIVE_DARK_MODE : DWMWA_USE_IMMERSIVE_DARK_MODE_OLD;
                DwmSetWindowAttribute(hwnd, attribute, ref darkTitleBar, sizeof(int));

                int backdrop = DWM_SYSTEMBACKDROP_TYPE; // 3 = Mica
                int v2 = DWMWA_SYSTEMBACKDROP_TYPE;
                if (DwmSetWindowAttribute(hwnd, v2, ref backdrop, sizeof(int)) != 0 && build == 22000)
                {
                    // 22000 内测版本使用的私有属性号
                    int v1 = 1029;
                    DwmSetWindowAttribute(hwnd, v1, ref backdrop, sizeof(int));
                }
            }
        }
        catch (DllNotFoundException)
        {
            // 无 dwmapi（理论上不会发生），降级为纯色
        }
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE_OLD = 19;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWM_SYSTEMBACKDROP_TYPE = 3; // Mica

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>读取 HKCU 注册表判断系统是否为浅色模式（跟随系统主题）。</summary>
    public static bool IsSystemDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            var value = key?.GetValue("AppsUseLightTheme");
            if (value is int i) return i == 0;
        }
        catch (Exception)
        {
            // 注册表不可读时按浅色处理
        }
        return false;
    }
}
