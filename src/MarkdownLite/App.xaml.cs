using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;

namespace MarkdownLite;

public partial class App : Application
{
    /// <summary>可打开的文件扩展名（只读展示，不代表编辑）。</summary>
    public static readonly string[] SupportedExtensions =
        [".md", ".markdown", ".mdown", ".mkd", ".txt"];

    /// <summary>
    /// 进程启动时刻（App 类型初始化即触发，是托管代码里最早可取的点）。
    /// 供 <c>OpenTiming</c> 做打开耗时测量的计时零点。
    /// </summary>
    internal static readonly long StartupTimestamp = Stopwatch.GetTimestamp();

    /// <summary>WebView2 用户数据目录。环境对象在 <see cref="OnStartup"/> 里提前创建，路径必须双方一致。</summary>
    public static string WebViewUserDataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MdReader", "webview");

    /// <summary>
    /// 预创建的 WebView2 环境。在 <see cref="OnStartup"/> 里就开始创建（与窗口构造、BAML 解析并行），
    /// <c>MainWindow.InitializeWebViewAsync</c> 只需等它，不再重复创建。
    /// </summary>
    public static Task<CoreWebView2Environment>? WebViewEnvironmentTask { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 注册 GB18030 等代码页编码，供编码探测回退使用
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        DispatcherUnhandledException += OnDispatcherUnhandledException;

        // 最后一道日志：非 UI 线程或不可恢复异常（DispatcherUnhandledException 接不住的那些）
        // 进程终止前尽量留一条痕迹，否则「闪退无日志」无从排查
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            try
            {
                if (args.ExceptionObject is Exception ex)
                    Services.ErrorLog.Write(
                        args.IsTerminating ? "未处理异常（进程即将终止）" : "后台线程未处理异常", ex);
                else
                    Services.ErrorLog.Info("未处理异常对象（非 Exception 类型）：" + args.ExceptionObject);
            }
            catch
            {
                // 日志本身失败时无路可去，吞掉保证终止流程不被干扰
            }
        };

        // WebView2 环境对象提前创建：它与窗口构造 / BAML 解析互不依赖，
        // 现在并行掉这一段，窗口出现后基本不再为环境创建等待。
        // 任务异常在这里先观察掉，避免无人等待时触发 UnobservedTaskException；
        // 真正的失败由 InitializeWebViewAsync 的回退逻辑处理。
        Directory.CreateDirectory(WebViewUserDataDir);
        WebViewEnvironmentTask = CoreWebView2Environment.CreateAsync(null, WebViewUserDataDir, null);
        WebViewEnvironmentTask.ContinueWith(
            static t => _ = t.Exception,
            TaskContinuationOptions.OnlyOnFaulted);

        var window = new MainWindow();

        // 命令行参数：MarkdownLite.exe "C:\path\file.md"
        for (var i = 0; i < e.Args.Length; i++)
        {
            var arg = e.Args[i];
            if (arg is "--diag-layout")
            {
                Services.LayoutDiagnostics.Enabled = true;
                continue;
            }
            if (arg is "--diag-log")
            {
                Services.ErrorLog.Enabled = true;
                continue;
            }
            if (arg is "--diag-search")
            {
                MarkdownLite.MainWindow.DiagShowSearchBar = true;
                continue;
            }
            if (arg is "--diag-scroll")
            {
                Services.DiagPerf.EnableScrollFps();
                continue;
            }
            if (arg is "--diag-memory")
            {
                Services.DiagPerf.EnableMemory();
                continue;
            }
            // --diag-open <file>：测「进程启动 → 该文档 NavigationCompleted」，写报告后自动退出
            if (arg is "--diag-open" && i + 1 < e.Args.Length)
            {
                var target = e.Args[++i];
                Services.OpenTiming.Enable(target);
                if (IsAcceptablePath(target)) window.PendingFilePath = target;
                continue;
            }
            if (IsAcceptablePath(arg))
            {
                window.PendingFilePath = arg;
                break;
            }
        }

        MainWindow = window;
        window.Show();
    }

    internal static bool IsAcceptablePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (path.StartsWith('-')) return false; // 跳过开关类参数
        if (!File.Exists(path)) return false;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return Array.Exists(SupportedExtensions, x => x == ext);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // 必须先落日志再弹窗：崩溃排查全靠这里（2026-10-01 闪退反馈因无日志无法定位）
        Services.ErrorLog.Write("UI 线程未处理异常", e.Exception);
        try
        {
            Dialogs.AppDialog.ShowError(
                MainWindow as Window,
                "发生未处理的错误",
                e.Exception.Message);
        }
        catch (Exception dialogEx)
        {
            // 弹窗自身若再抛（例如原始异常在布局路径上、弹窗布局又会触发它），
            // 不能让异常从兜底处理器里逃出去把进程带崩——留日志、标记已处理
            Services.ErrorLog.Write("异常弹窗呈现失败", dialogEx);
        }
        e.Handled = true;
    }
}
