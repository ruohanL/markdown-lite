using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Interop;
using System.Windows.Threading;
using MarkdownLite.Dialogs;
using MarkdownLite.Services;
using Microsoft.Web.WebView2.Core;

namespace MarkdownLite;

public partial class MainWindow : Window
{
    public static readonly RoutedUICommand OpenFileCommand = new("打开", "OpenFile", typeof(MainWindow));
    public static readonly RoutedUICommand BackCommand = new("返回上一文档", "Back", typeof(MainWindow),
        new InputGestureCollection { new KeyGesture(Key.Left, ModifierKeys.Alt) });
    public static readonly RoutedUICommand ReloadCommand = new("重新加载", "Reload", typeof(MainWindow));
    public static readonly RoutedUICommand ToggleTocCommand = new("目录侧栏", "ToggleToc", typeof(MainWindow));
    public static readonly RoutedUICommand ZoomInCommand = new("放大", "ZoomIn", typeof(MainWindow));
    public static readonly RoutedUICommand ZoomOutCommand = new("缩小", "ZoomOut", typeof(MainWindow));
    public static readonly RoutedUICommand ZoomResetCommand = new("重置缩放", "ZoomReset", typeof(MainWindow));
    public static readonly RoutedUICommand FindCommand = new("页内搜索", "Find", typeof(MainWindow));
    public static readonly RoutedUICommand CloseFindCommand = new("关闭搜索", "CloseFind", typeof(MainWindow));

    /// <summary>命令行传入的待打开文件，在 WebView2 初始化完成后消费。</summary>
    public string? PendingFilePath { get; set; }

    /// <summary>上一文档路径（Alt+← 返回用）。只保留一格：返回时与当前文档自动互换，可在两份文档间来回切换。</summary>
    private string? _previousDocumentPath;

    /// <summary>诊断模式（--diag-search）：启动即展开页内搜索栏，供 GUI 回归截图检查其版式。</summary>
    internal static bool DiagShowSearchBar;

    private static readonly string AppDataBase = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MdReader");
    private static readonly string CacheDir = Path.Combine(AppDataBase, "cache");
    private static readonly string WebViewUserDataDir = App.WebViewUserDataDir;   // 与预创建的环境保持一致
    private static readonly string AssetsDir = Path.Combine(AppContext.BaseDirectory, "wwwroot");
    private const string ViewFileName = "view.html";

    // Chromium 能内建展示的媒体类型：链接指向它们时由虚拟主机按文件直接提供；
    // 其余一切存在的文件（含无扩展名的 LICENSE、Makefile 等）都留在应用内按文本打开。
    private static readonly HashSet<string> MediaFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp", ".ico", ".svg", ".pdf",
        ".mp4", ".webm", ".mp3", ".ogg", ".wav", ".m4a"
    };

    private static readonly HashSet<string> DocHostSet = new(MarkdownRenderer.DocHosts);

    private readonly AppSettings _settings;
    private readonly ReadingProgress _progress;
    private string? _currentPath;
    private int _docHostToggle;
    private readonly Dictionary<string, string> _docHostDirectories = new();

    private bool _webReady;
    private bool _webCrashed;   // 浏览器进程崩溃后置位，等待自动恢复
    private bool _recovering;
    // 打开流程的重入与取消：新请求会取消旧请求（见 OpenFileAsync / CancelPendingOpen）
    private CancellationTokenSource? _openCts;
    private long _lastRenderMs;
    private int _wordCount;
    private string _lastEncoding = "";
    private long _lastFileSize;
    private string? _pendingAnchor;          // 跨文档链接带来的 #anchor，导航完成后消费
    private double? _pendingScrollRatio;     // 阅读进度记忆：待恢复的滚动百分比
    private ObservableCollection<TocViewItem> _tocItems = new();

    // 标题栏拖动：按下只记起点，越过拖动阈值才真正开始拖（见 OnTitleBarMouseDown）
    private bool _titleBarPressed;
    private Point _titleBarPressedAt;
    private readonly DocumentWatcher _watcher;
    private readonly DispatcherTimer _searchDebounce;
    // 阅读进度落盘：改为「滚动停止 + 距上次保存 ≥ 5 秒」才写，退出/切文档时强制补一次
    private readonly DispatcherTimer _progressIdleTimer;
    private DateTime _lastProgressSave = DateTime.UtcNow;
    private bool _progressDirty;
    private const int ProgressIdleMs = 800;                       // 滚动停止判定窗口
    private static readonly TimeSpan ProgressSaveInterval = TimeSpan.FromSeconds(5);
    private readonly FileDropTarget _fileDropTarget;

    public MainWindow()
    {
        InitializeComponent();
        _settings = AppSettings.Load();
        _progress = ReadingProgress.Load();

        // 文件被外部修改 → 防抖后自动重载（可在「文件」菜单关闭）
        _watcher = new DocumentWatcher(() => Dispatcher.InvokeAsync(() => _ = AutoReloadAsync()));

        // 搜索输入防抖，避免每敲一个字符就遍历一次 DOM
        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            RunSearch();
        };

        // 阅读进度落盘：滚动停止 800ms 后触发一次判定，
        // 距上次保存 ≥ 5 秒才真正写盘；没到 5 秒就排一个一次性补存，
        // 保证最迟在上次保存 + 5 秒时落盘（不会因为用户停止滚动而一直不写）。
        _progressIdleTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ProgressIdleMs) };
        _progressIdleTimer.Tick += (_, _) => OnProgressIdle();

        _fileDropTarget = new FileDropTarget(files =>
        {
            if (files.FirstOrDefault(App.IsAcceptablePath) is { } file)
                Dispatcher.InvokeAsync(() => OpenFileAsync(file));
        });

        CommandBindings.Add(new CommandBinding(OpenFileCommand, (_, _) => ExecuteOpenDialog()));
        CommandBindings.Add(new CommandBinding(BackCommand,
            (_, _) => ExecuteBack(),
            (_, e) => e.CanExecute = _previousDocumentPath is not null));
        CommandBindings.Add(new CommandBinding(ReloadCommand, (_, _) => _ = ExecuteReloadAsync()));
        CommandBindings.Add(new CommandBinding(ToggleTocCommand, (_, _) => MenuToc.IsChecked = !MenuToc.IsChecked));
        CommandBindings.Add(new CommandBinding(ZoomInCommand, (_, _) => AdjustZoom(+0.1)));
        CommandBindings.Add(new CommandBinding(ZoomOutCommand, (_, _) => AdjustZoom(-0.1)));
        CommandBindings.Add(new CommandBinding(ZoomResetCommand, (_, _) => SetZoom(1.0)));
        CommandBindings.Add(new CommandBinding(FindCommand, (_, _) => ShowSearchBar()));
        CommandBindings.Add(new CommandBinding(CloseFindCommand, (_, _) => HideSearchBar()));

        AppTheme.Applied += () => _ = ApplyWebViewThemeAsync();

        Loaded += OnLoaded;
        ContentRendered += (_, _) => AppTheme.Apply(_settings.Theme);
        Closing += OnClosing;

        // 版式改版后，旧记录下来的窗口几何 / 侧栏开关一次性作废（详见 AppSettings.CurrentLayoutRevision）
        _settings.MigrateLayout();

        MenuToc.IsChecked = _settings.TocVisible;
        MenuAutoReload.IsChecked = _settings.AutoReload;
        RestoreWindowGeometry();
    }

    // ==================================================================
    // 初始化
    // ==================================================================

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // P0：WebView2 环境创建（浏览器进程冷启，通常几百毫秒）与文档预处理
        // （读盘 + 编码探测 + 字数 + Markdig 解析 + 渲染 + 拼文档 + 落盘）互不依赖，
        // 同时起跑、最后汇合，总耗时从「两者之和」降到「两者的最大值」。
        // 预处理不接触 WebView2；只有末尾的虚拟主机映射与 Navigate 需要 core。
        var webInit = InitializeWebViewAsync();

        Task<PreparedDocument?>? prewarm = null;
        if (PendingFilePath is { } pending)
        {
            PendingFilePath = null;
            prewarm = PrepareStandaloneAsync(pending);
        }

        try
        {
            await webInit;
            _webReady = true;
            // 恢复上次缩放（读取的是控件属性，必须在 WebView2 初始化后设置）
            ApplyZoom(_settings.ZoomFactor);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("WebView2 初始化失败", ex);
            AppDialog.ShowError(
                this,
                "WebView2 运行时初始化失败",
                ex.Message +
                "\n\n请确认系统已安装 Microsoft Edge WebView2 Runtime（Windows 10 1803+ / Windows 11 通常自带）。");
            UpdateStatus("WebView2 初始化失败");
            return;
        }

        if (prewarm is not null)
        {
            // 预热失败时（文件不存在 / 打开失败）不落欢迎页，避免覆盖错误提示
            if (await prewarm is { } prepared && TryGetCore() is { } core)
            {
                try
                {
                    await PresentDocumentAsync(core, prepared);
                }
                catch (Exception ex)
                {
                    ErrorLog.Write("启动呈现文档失败: " + prepared.Path, ex);
                    UpdateStatus("呈现失败");
                }
            }
        }
        else
        {
            ShowWelcome();
        }

        if (LayoutDiagnostics.Enabled)
        {
            await LayoutDiagnostics.RunAsync(this, RootBorder);
            return;
        }

        if (DiagShowSearchBar) ShowSearchBar();
    }

    private async Task InitializeWebViewAsync()
    {
        Directory.CreateDirectory(CacheDir);
        Directory.CreateDirectory(WebViewUserDataDir);

        // 首帧背景与当前主题一致，避免暗色模式下先闪白屏。
        // P6：取值统一走 MarkdownRenderer 的两个常量（与页面内联样式 / Brush.Window 同源），
        // 不再各写一份十六进制——原来这里硬编码的 #FBFBFD / #161616 是旧配色，已经过期。
        Web.DefaultBackgroundColor = System.Drawing.ColorTranslator.FromHtml(
            AppTheme.IsDarkEffective
                ? MarkdownRenderer.DarkBackground
                : MarkdownRenderer.LightBackground);

        // 环境对象在 App.OnStartup 就开始创建（与窗口构造、BAML 解析并行），这里只等它。
        // 若预创建失败，回退为「在这里再创建一次」的原逻辑，而不是把失败当成本窗口的问题。
        var environment = await GetWebView2EnvironmentAsync();
        await Web.EnsureCoreWebView2Async(environment);

        var core = Web.CoreWebView2;

        // 安全设置：仅允许本地受控脚本（reader.js / highlight.js），关掉一切浏览器 UI
        var settings = core.Settings;
        settings.IsScriptEnabled = true;
        settings.AreDevToolsEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.AreBrowserAcceleratorKeysEnabled = false; // F5/Ctrl+R 等由宿主接管
        settings.IsZoomControlEnabled = true;              // 允许 Ctrl+滚轮缩放
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsSwipeNavigationEnabled = false;

        // 注入 wwwroot 目录：CSS/JS 的缓存 id 取自资源内容哈希，需要从这里读文件算出来
        MarkdownRenderer.SetAssetDirectory(AssetsDir);

        // 本地虚拟主机：静态资源 / 渲染缓存 / 当前文档目录（轮换映射）
        core.SetVirtualHostNameToFolderMapping(
            MarkdownRenderer.AssetsHost, AssetsDir, CoreWebView2HostResourceAccessKind.Allow);
        core.SetVirtualHostNameToFolderMapping(
            MarkdownRenderer.CacheHost, CacheDir, CoreWebView2HostResourceAccessKind.Allow);
        foreach (var host in MarkdownRenderer.DocHosts)
        {
            core.SetVirtualHostNameToFolderMapping(
                host, CacheDir, CoreWebView2HostResourceAccessKind.Allow);
            _docHostDirectories[host] = CacheDir;
        }

        core.NavigationStarting += OnNavigationStarting;
        core.NavigationCompleted += OnNavigationCompleted;
        core.NewWindowRequested += OnNewWindowRequested;
        core.DownloadStarting += OnDownloadStarting;
        core.FrameNavigationStarting += OnFrameNavigationStarting;
        core.WebMessageReceived += OnWebMessageReceived;
        core.ProcessFailed += OnProcessFailed;

        // 接管 WebView2 子窗口的文件拖放（airspace 问题，见 FileDropTarget 注释）
        AttachNativeDropTarget();
    }

    /// <summary>
    /// 取 WebView2 环境对象：优先等 <see cref="App.WebViewEnvironmentTask"/>（OnStartup 里就开始创建的那份），
    /// 预创建失败时回退为就地创建一次——避免把「环境创建失败」误判成「本窗口初始化失败」。
    /// </summary>
    private static async Task<CoreWebView2Environment> GetWebView2EnvironmentAsync()
    {
        var pending = App.WebViewEnvironmentTask;
        if (pending is not null)
        {
            try
            {
                return await pending;
            }
            catch (Exception ex)
            {
                ErrorLog.Write("预创建的 WebView2 环境失败，回退为就地创建", ex);
            }
        }

        return await CoreWebView2Environment.CreateAsync(null, App.WebViewUserDataDir, null);
    }

    private void AttachNativeDropTarget()
    {
        if (_webCrashed) return;
        try
        {
            // Web.Handle：WebView2 宿主 HWND；其内部 Chrome 子窗口由 Attach 递归枚举
            _fileDropTarget.Attach(Web.Handle);
        }
        catch (Exception)
        {
            // 拖放接管失败不影响其它打开方式
        }
    }

    /// <summary>
    /// 安全获取 CoreWebView2。浏览器进程崩溃后访问该属性会抛
    /// InvalidOperationException("browser process crashed")，这里统一兜住并转入恢复流程，
    /// 避免把底层异常原样抛给调用方。
    /// </summary>
    private CoreWebView2? TryGetCore()
    {
        if (_webCrashed) return null;
        try
        {
            return Web.CoreWebView2;
        }
        catch (Exception ex)
        {
            _webCrashed = true;
            ErrorLog.Write("访问 CoreWebView2 失败（浏览器进程可能已崩溃）", ex);
            return null;
        }
    }

    /// <summary>
    /// WebView2 进程失败回调。官方文档明确建议监听此事件来管理控件生命周期：
    /// 浏览器进程一旦崩溃，托管包装器会永久失效，之后每次访问 CoreWebView2 都抛异常。
    /// 这里重建控件并重新初始化，尽量让阅读不中断。
    /// </summary>
    private void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
    {
        _webCrashed = true;
        _webReady = false;
        ErrorLog.Write(
            $"WebView2 进程失败：kind={e.ProcessFailedKind} reason={e.Reason} " +
            $"exitCode={e.ExitCode} description={e.ProcessDescription}");

        if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited)
            _ = RecoverWebViewAsync();
    }

    private async Task RecoverWebViewAsync()
    {
        if (_recovering) return;
        _recovering = true;
        UpdateStatus("内置浏览器进程已重启，正在恢复…");
        try
        {
            await Task.Delay(300);

            if (Web.Parent is not Panel parent)
            {
                ErrorLog.Write("无法恢复：WebView2 不在可视树中");
                return;
            }

            var column = Grid.GetColumn(Web);
            parent.Children.Remove(Web);
            try
            {
                Web.Dispose();
            }
            catch (Exception)
            {
                // 崩溃后的控件释放失败可忽略
            }

            var fresh = new Microsoft.Web.WebView2.Wpf.WebView2();
            Grid.SetColumn(fresh, column);
            parent.Children.Add(fresh);
            Web = fresh;

            await InitializeWebViewAsync();
            _webCrashed = false;
            _webReady = true;
            ApplyZoom(_settings.ZoomFactor);

            if (_currentPath is { } path && File.Exists(path))
                await OpenFileAsync(path);
            else
                ShowWelcome();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("恢复 WebView2 失败", ex);
            UpdateStatus("内置浏览器进程不可用，请重启应用");
        }
        finally
        {
            _recovering = false;
        }
    }

    private void ShowWelcome()
    {
        Title = "MarkdownLite";
        TryGetCore()?.Navigate($"http://{MarkdownRenderer.AssetsHost}/welcome.html");
        UpdateStatus("未打开文件（Ctrl+O 打开，或将 .md 文件拖入窗口）");
    }

    // ==================================================================
    // 打开 / 渲染
    // ==================================================================

    /// <summary>
    /// 返回上一文档（Alt+←）。历史只保留一格：返回动作会再次经过呈现流程，
    /// current/previous 自动互换，因此来回按 Alt+← 可在两份文档间切换。
    /// </summary>
    private void ExecuteBack()
    {
        if (_previousDocumentPath is not { } target || !File.Exists(target))
        {
            UpdateStatus("没有可返回的上一文档");
            return;
        }
        _ = OpenFileAsync(target);
    }

    private void OnBackClick(object sender, RoutedEventArgs e) => ExecuteBack();

    private void ExecuteOpenDialog()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "打开 Markdown 文件",
            Filter = "Markdown 文件 (*.md)|*.md;*.markdown;*.mdown;*.mkd|文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
        };
        if (_currentPath is not null && Directory.Exists(Path.GetDirectoryName(_currentPath)))
            dialog.InitialDirectory = Path.GetDirectoryName(_currentPath);

        if (dialog.ShowDialog(this) == true)
            _ = OpenFileAsync(dialog.FileName);
    }

    private async Task ExecuteReloadAsync()
    {
        if (_currentPath is { } path)
            await OpenFileAsync(path);
    }

    /// <summary>「预处理完成、尚未呈现」的一份文档。纯数据，生成过程不接触 WebView2。</summary>
    private sealed record PreparedDocument(
        string Path,
        string DocHost,
        string? DocDir,
        string Encoding,
        int WordCount,
        string DisplayTitle,
        IReadOnlyList<TocEntry> Toc,
        long RenderMs,
        string? Anchor,
        string ContentVersion);

    /// <summary>
    /// 打开文件的入口：**预处理**（纯计算，见 <see cref="PrepareDocumentAsync"/>）
    /// 与**呈现**（映射虚拟主机 + 导航，见 <see cref="PresentDocumentAsync"/>）。
    ///
    /// <para>P5：每次进入都会先取消上一次未完成的打开请求，因此快速连续打开多个文件时，
    /// 落在后面的那次会真正生效，而不是像以前那样被 <c>_loading</c> 标志直接丢弃（表现为「点了没反应」）。</para>
    /// </summary>
    private async Task OpenFileAsync(string path, string? anchor = null)
    {
        var cts = new CancellationTokenSource();
        CancelPendingOpen();
        _openCts = cts;

        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var prepared = await PrepareDocumentAsync(path, anchor, cts.Token);
            if (prepared is null) return;          // 文件不存在 / 打开失败 / 已被更新的请求取代

            var core = TryGetCore();
            if (core is null)
            {
                UpdateStatus(_recovering ? "内置浏览器正在重启，请稍候…" : "内置浏览器引擎不可用");
                return;
            }

            try
            {
                await PresentDocumentAsync(core, prepared);
            }
            catch (Exception ex)
            {
                // 呈现阶段直接接触 WebView2，是最靠近「无日志闪退」的一环：
                // 兜住写日志 + 可见提示，绝不让异常裸抛（2026-10-01 返回文档闪退反馈）
                ErrorLog.Write("呈现文档失败: " + prepared.Path, ex);
                AppDialog.ShowError(this, "呈现文档失败", ex.Message);
                UpdateStatus("呈现失败");
            }
        }
        finally
        {
            // 只有「仍是当前请求」时才清理，避免被取消的旧请求把新请求的光标/登记一并清掉
            if (ReferenceEquals(_openCts, cts))
            {
                _openCts = null;
                Mouse.OverrideCursor = null;
            }
            cts.Dispose();
        }
    }

    /// <summary>启动预热：与 <see cref="OpenFileAsync"/> 共用预处理，但不参与「新请求取消旧请求」的登记。</summary>
    private async Task<PreparedDocument?> PrepareStandaloneAsync(string path)
    {
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            return await PrepareDocumentAsync(path, anchor: null, CancellationToken.None);
        }
        finally
        {
            Mouse.OverrideCursor = null;
        }
    }

    /// <summary>
    /// **预处理**：读文件 → 编码探测 → 字数 → Markdig 解析与渲染 → 拼完整文档 → 原子落盘。
    /// 全程在 <see cref="Task.Run"/> 上执行，是「打开文件」的耗时主体；**完全不接触 WebView2**。
    /// 返回 null 表示文件不存在 / 打开失败 / 已被取消（状态栏或弹窗已在内部给出提示）。
    /// </summary>
    private async Task<PreparedDocument?> PrepareDocumentAsync(string path, string? anchor, CancellationToken ct)
    {
        try
        {
            path = Path.GetFullPath(path);
            if (!File.Exists(path))
            {
                UpdateStatus("文件不存在: " + path);
                return null;
            }

            // 提前预留文档主机（轮换）：这样 hostBase 在渲染前就是确定的，
            // 不必等 WebView2 就绪、也就不用事后对整篇 HTML 做一次 string.Replace。
            var docHost = MarkdownRenderer.DocHosts[_docHostToggle];
            _docHostToggle = (_docHostToggle + 1) % MarkdownRenderer.DocHosts.Length;
            var docDir = Path.GetDirectoryName(path);
            var hostBase = docDir is null ? null : "http://" + docHost;

            var stopwatch = Stopwatch.StartNew();

            ct.ThrowIfCancellationRequested();
            var (text, encoding, wordCount) = await Task.Run(() =>
            {
                var content = PlainTextReader.Read(path, out var enc);
                return (content, enc, TextStatistics.CountWords(content));
            }, ct);

            ct.ThrowIfCancellationRequested();
            var result = await Task.Run(() => MarkdownRenderer.Render(text, docDir, hostBase), ct);

            var displayTitle = result.Title ?? Path.GetFileName(path);
            var html = MarkdownRenderer.BuildDocument(result.BodyHtml, displayTitle);

            // 只写应用自身缓存目录（%LOCALAPPDATA%\MdReader\cache，数据目录名有意沿用旧名），绝不写源文件目录
            ct.ThrowIfCancellationRequested();
            await Task.Run(() => WriteAtomic(Path.Combine(CacheDir, ViewFileName), html), ct);
            ct.ThrowIfCancellationRequested();
            stopwatch.Stop();
            OpenTiming.ReportPrepared(path);       // --diag-open：预处理完成

            return new PreparedDocument(
                path, docHost, docDir, encoding, wordCount, displayTitle,
                result.Toc, stopwatch.ElapsedMilliseconds, anchor,
                MarkdownRenderer.ContentVersion(html));
        }
        catch (OperationCanceledException)
        {
            return null;                           // 被更新的请求取代：静默结束
        }
        catch (Exception ex)
        {
            ErrorLog.Write($"打开文件失败: {path}", ex);
            AppDialog.ShowError(this, "打开文件失败", ex.Message);
            UpdateStatus("打开失败");
            return null;
        }
    }

    /// <summary>
    /// **呈现**：把预处理好的文档交给 WebView2 —— 补上文档目录的虚拟主机映射、
    /// 更新标题 / 目录 / 状态栏 / 进度，然后导航。这一步不做任何计算。
    /// </summary>
    private async Task PresentDocumentAsync(CoreWebView2 core, PreparedDocument doc)
    {
        core.SetVirtualHostNameToFolderMapping(
            doc.DocHost, doc.DocDir ?? CacheDir, CoreWebView2HostResourceAccessKind.Allow);
        _docHostDirectories[doc.DocHost] = doc.DocDir ?? CacheDir;

        // 导航历史（Alt+←「返回上一文档」）：切换到不同文档时把当前这份记为「上一文档」；
        // 返回动作本身也会走到这里，current/previous 因此自动互换，来回按 Alt+← 可在两份文档间切换。
        // 注意必须写在下方 _currentPath 赋值之前（读的是旧值）。
        if (_currentPath is { } current && !string.Equals(current, doc.Path, StringComparison.OrdinalIgnoreCase))
        {
            _previousDocumentPath = current;
            BackButton.Visibility = Visibility.Visible;     // 顶栏返回按钮
            TocBackButton.Visibility = Visibility.Visible;  // 侧栏返回按钮（更直观，用户点名要求）
            CommandManager.InvalidateRequerySuggested();
        }
        _currentPath = doc.Path;
        _lastRenderMs = doc.RenderMs;
        _lastEncoding = doc.Encoding;
        _wordCount = doc.WordCount;
        _lastFileSize = new FileInfo(doc.Path).Length;
        // 切文档前把上一份的阅读进度落盘：可能还没到 5 秒的判定窗口，不补这一次就会丢
        FlushReadingProgress();
        _pendingAnchor = doc.Anchor;
        // 跨文档锚点优先；否则恢复上次阅读位置
        _pendingScrollRatio = doc.Anchor is null ? _progress.Get(doc.Path) : null;

        Title = doc.DisplayTitle;
        SetToc(doc.Toc);
        // P4：版本号跟着**内容**走（FNV 哈希），而不是「每次导航都换一个」——
        // 内容不变时 URL 不变，可复用缓存、不会让 WebView2 缓存无限膨胀；
        // 内容一变 URL 立刻变，也不可能读到上一次的旧文档。
        core.Navigate($"http://{MarkdownRenderer.CacheHost}/{ViewFileName}?v={doc.ContentVersion}");
        OpenTiming.ReportPresented(doc.Path);   // --diag-open：已交给 WebView2
        UpdateStatus(doc.Path);
        UpdateWatcher();

        await Task.CompletedTask;   // 保持异步签名，便于以后在导航前插入异步准备工作
    }

    /// <summary>取消当前正在进行的打开请求（若存在）。新的打开请求会先调用这里。</summary>
    private void CancelPendingOpen()
    {
        var old = _openCts;
        _openCts = null;
        if (old is null) return;

        try { old.Cancel(); }
        catch (ObjectDisposedException) { /* 上一次请求已自行收尾并释放，无需再取消 */ }
    }

    private static void WriteAtomic(string targetPath, string content)
    {
        var tempPath = targetPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(tempPath, content, new System.Text.UTF8Encoding(false));
        File.Move(tempPath, targetPath, overwrite: true);
    }

    // ==================================================================
    // WebView2 事件：导航白名单 / 外链外抛 / 消息桥
    // ==================================================================

    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri))
        {
            e.Cancel = true;
            return;
        }

        switch (uri.Host)
        {
            case var host when host == MarkdownRenderer.AssetsHost:
                return; // 应用自带的只读资源目录，放行

            case var host when host == MarkdownRenderer.CacheHost:
                if (uri.AbsolutePath == "/" + ViewFileName) return;
                e.Cancel = true; // 缓存目录内只承认渲染页
                return;
        }

        if (DocHostSet.Contains(uri.Host) && _docHostDirectories.TryGetValue(uri.Host, out var rootDir))
        {
            var localPath = HostPathToLocal(uri, rootDir);
            if (localPath is null)
            {
                e.Cancel = true;
                return;
            }

            var extension = Path.GetExtension(localPath).ToLowerInvariant();
            var anchor = uri.Fragment.Length > 1 ? Uri.UnescapeDataString(uri.Fragment[1..]) : null;

            // 相对链接指向另一个 .md/.txt：留在应用内打开（保留 #anchor 以便跳转到对应章节）
            if (App.IsAcceptablePath(localPath) && File.Exists(localPath))
            {
                e.Cancel = true;
                Dispatcher.InvokeAsync(() => OpenFileAsync(localPath, anchor));
                return;
            }

            // 本地 HTML 不在应用内执行（避免脚本），交给系统浏览器；
            // 必须用 Uri.AbsoluteUri 转义，否则路径含 #、% 或空格时链接会失效
            if (extension is ".html" or ".htm")
            {
                e.Cancel = true;
                OpenExternal(new Uri(localPath).AbsoluteUri);
                return;
            }

            // 存在但不是媒体文件的链接（如无扩展名的 LICENSE、Makefile）：不能放行给虚拟主机直出。
            // Chromium 会显示一张没有 reader.js 的裸文本页——目录点击、主题、状态栏全部失效，
            // 且应用没有「返回」，用户就卡死在那了（实测踩到：README 里的 [MIT](LICENSE)）。
            // 改为留在应用内按文本打开，与普通文档共用同一套渲染与状态管理。
            if (File.Exists(localPath))
            {
                if (!MediaFileExtensions.Contains(extension))
                {
                    e.Cancel = true;
                    Dispatcher.InvokeAsync(() => OpenFileAsync(localPath, anchor));
                }
                return; // 图片、pdf 等媒体资源由虚拟主机按文件直接提供
            }

            // 链接目标不存在：取消导航并提示。放行会落到虚拟主机的 404 错误页，
            // 同样是一张无法交互、无法返回的裸页。
            e.Cancel = true;
            UpdateStatus("链接目标不存在：" + Path.GetFileName(localPath));
        }

        // 其它一律取消；http/https/mailto 用系统默认浏览器打开
        e.Cancel = true;
        if (uri.Scheme is "http" or "https" or "mailto")
            OpenExternal(uri.AbsoluteUri);
    }

    private static void OnFrameNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        // 页面本身不使用 iframe，全部拦截（与 CSP frame-src 'none' 双保险）
        e.Cancel = true;
    }

    private static void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true; // 永不弹新窗口
        if (Uri.TryCreate(e.Uri, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto")
            OpenExternal(uri.AbsoluteUri);
    }

    private static void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        e.Cancel = true; // 只读场景：不允许触发下载对话框
    }

    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string? message;
        try
        {
            message = e.TryGetWebMessageAsString();
        }
        catch (Exception)
        {
            return;
        }

        if (string.IsNullOrEmpty(message)) return;
        ErrorLog.Info("webmsg: " + message);

        // 本地 reader.js 的受控消息桥（解决 WebView2 获焦时快捷键不到达 WPF 的问题）
        switch (message)
        {
            case "shortcut:open": ExecuteOpenDialog(); return;
            case "shortcut:back": ExecuteBack(); return;
            case "shortcut:reload": _ = ExecuteReloadAsync(); return;
            case "shortcut:zoom-in": AdjustZoom(+0.1); return;
            case "shortcut:zoom-out": AdjustZoom(-0.1); return;
            case "shortcut:zoom-reset": SetZoom(1.0); return;
            case "shortcut:toggle-toc": MenuToc.IsChecked = !MenuToc.IsChecked; return;
            case "shortcut:find": ShowSearchBar(); return;
            case "shortcut:escape": HideSearchBar(); return;
        }

        if (message.StartsWith("copied:", StringComparison.Ordinal))
        {
            // 右键菜单点「复制」成功后的上报（ShowPageContextMenu）
            if (int.TryParse(message["copied:".Length..], out var copiedLength) && copiedLength > 0)
                UpdateStatus($"已复制 {copiedLength} 个字符");
            return;
        }

        if (message.StartsWith("ctx:", StringComparison.Ordinal))
        {
            // 页面右键（reader.js 只上报视口坐标，选区文本不经过消息桥）
            var parts = message.Split(':');
            if (parts.Length == 3
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var ctxX)
                && double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var ctxY))
            {
                ShowPageContextMenu(ctxX, ctxY);
            }
            return;
        }

        if (message.StartsWith("scrollperf:", StringComparison.Ordinal))
        {
            DiagPerf.OnScrollPerfReport(message);   // --diag-scroll / --diag-memory 的采样结果
            return;
        }

        if (message.StartsWith("scroll:", StringComparison.Ordinal))
        {
            OnScrollReport(message);
            return;
        }

        if (message.StartsWith("search:", StringComparison.Ordinal))
        {
            OnSearchReport(message);
        }
    }

    /// <summary>
    /// 页面右键菜单：在鼠标位置弹圆角菜单（仅「复制」），视觉与顶部菜单栏弹层同款
    /// （透明 Popup + 圆角边框 + 阴影，黑白灰令牌）。
    /// 用自建 Popup 而不是 ContextMenu：ContextMenu 的弹层 HWND 不支持透明，
    /// 圆角外会露出系统底色；自建 Popup 的 AllowsTransparency 完全可控。
    /// 坐标链：页面 CSS 像素 × ZoomFactor = 控件 DIP → PointToScreen → PointFromScreen 窗口 DIP。
    /// 选中文本不经过消息桥（避免进诊断日志），点「复制」时才经 ExecuteScriptAsync 取回。
    /// </summary>
    private Popup? _pageMenu;

    private void ShowPageContextMenu(double cssX, double cssY)
    {
        var core = TryGetCore();
        if (core is null) return;

        var screen = Web.PointToScreen(new Point(cssX * Web.ZoomFactor, cssY * Web.ZoomFactor));
        var inWindow = PointFromScreen(screen);

        var copyItem = new MenuItem { Header = "复制" };
        var popup = new Popup
        {
            AllowsTransparency = true,
            Placement = PlacementMode.RelativePoint,
            PlacementTarget = this,
            StaysOpen = false,          // 点菜单外任意处自动关闭
            PopupAnimation = PopupAnimation.Fade,
            HorizontalOffset = inWindow.X,
            VerticalOffset = inWindow.Y,
        };
        _pageMenu = popup;

        copyItem.Click += async (_, _) =>
        {
            try
            {
                var json = await core.ExecuteScriptAsync("document.getSelection().toString()");
                var text = JsonSerializer.Deserialize<string>(json) ?? string.Empty;
                if (text.Length == 0) { UpdateStatus("没有选中的内容"); return; }
                Clipboard.SetText(text);
                UpdateStatus($"已复制 {text.Length} 个字符");
            }
            catch (Exception ex)
            {
                ErrorLog.Write("右键复制失败", ex);
                UpdateStatus("复制失败");
            }
            finally
            {
                popup.IsOpen = false;
            }
        };

        var items = new StackPanel();
        items.Children.Add(copyItem);
        var border = new Border
        {
            Background = (Brush)FindResource("Brush.Menu"),
            BorderBrush = (Brush)FindResource("Brush.Border"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(5),
            Margin = new Thickness(12),     // 给阴影留出透明边距
            Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 18, ShadowDepth = 4, Opacity = 0.25, Color = Colors.Black
            },
        };
        border.Child = items;
        popup.Child = border;
        popup.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) popup.IsOpen = false;
        };
        popup.IsOpen = true;
    }

    /// <summary>页面滚动上报：`scroll:<0~1 比例>:<当前标题 id>`。</summary>
    private void OnScrollReport(string message)
    {
        var parts = message.Split(':', 3);
        if (parts.Length < 3) return;

        if (double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var ratio)
            && _currentPath is { } path)
        {
            // 只在内存累积；落盘交给「滚动停止 + 距上次保存 ≥ 5 秒」的判定（见 OnProgressIdle），
            // 避免每 250ms 的滚动上报都触发一次磁盘写入
            _progress.Set(path, ratio);
            _progressDirty = true;
            KickProgressIdleTimer();
        }

        HighlightTocHeading(parts[2]);
    }

    /// <summary>重置「滚动停止」判定窗口。</summary>
    private void KickProgressIdleTimer()
    {
        _progressIdleTimer.Interval = TimeSpan.FromMilliseconds(ProgressIdleMs);
        _progressIdleTimer.Stop();
        _progressIdleTimer.Start();
    }

    /// <summary>滚动停止后触发的落盘判定（见字段注释）。</summary>
    private void OnProgressIdle()
    {
        _progressIdleTimer.Stop();
        if (!_progressDirty) return;

        var elapsed = DateTime.UtcNow - _lastProgressSave;
        if (elapsed < ProgressSaveInterval)
        {
            // 还没到 5 秒：排一个一次性补存，保证最迟在上次保存 + 5 秒时落盘
            _progressIdleTimer.Interval = ProgressSaveInterval - elapsed;
            _progressIdleTimer.Start();
            return;
        }

        FlushReadingProgress();
    }

    /// <summary>把累积在内存里的阅读进度写盘（有脏数据才写）。</summary>
    private void FlushReadingProgress()
    {
        if (!_progressDirty) return;

        _progressDirty = false;
        _lastProgressSave = DateTime.UtcNow;
        _progress.Save();
    }

    /// <summary>搜索上报：`search:<命中总数>:<当前序号(1 基，0 表示无)>`。</summary>
    private void OnSearchReport(string message)
    {
        if (SearchBar.Visibility != Visibility.Visible) return;

        var parts = message.Split(':', 3);
        if (parts.Length < 3) return;

        SearchStatus.Text = parts[1] switch
        {
            "0" => "无结果",
            var total => $"{parts[2]} / {total}",
        };
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        ErrorLog.Info($"navcompleted ok={e.IsSuccess} status={e.WebErrorStatus} uri={e.NavigationId}");
        OpenTiming.Report(_currentPath);     // --diag-open：目标文档导航完成即写出耗时报告并退出
        DiagPerf.RunOnDocumentReady(TryGetCore(), _currentPath);   // --diag-scroll / --diag-memory
        if (!e.IsSuccess && _currentPath is not null)
            UpdateStatus("页面加载失败（0x" + e.WebErrorStatus + "）");
        // 导航可能重建内部子窗口，重新接管拖放
        AttachNativeDropTarget();
        _ = ApplyWebViewThemeAsync();
        _ = ApplyPendingPositionAsync();
        _ = ProbeBridgeAsync();
    }

    /// <summary>诊断用：确认页面侧受控脚本是否已加载（仅 MDREADER_DIAG=1 时执行）。</summary>
    private async Task ProbeBridgeAsync()
    {
        var core = TryGetCore();
        if (!ErrorLog.Enabled || core is null) return;
        try
        {
            var result = await core.ExecuteScriptAsync(
                "[typeof mdApplyTheme, typeof mdScrollTo, typeof mdScrollToRatio, typeof mdSearch, typeof mdSearchStep, !!window.chrome].join('|')");
            ErrorLog.Info("bridge: " + result);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("桥探针失败", ex);
        }
    }

    /// <summary>导航完成后消费待处理的锚点 / 滚动位置，并重建搜索命中。</summary>
    private async Task ApplyPendingPositionAsync()
    {
        var core = TryGetCore();
        if (core is null) return;

        var anchor = _pendingAnchor;
        var ratio = _pendingScrollRatio;
        _pendingAnchor = null;
        _pendingScrollRatio = null;

        try
        {
            if (anchor is not null)
            {
                var payload = JsonSerializer.Serialize(anchor);
                await core.ExecuteScriptAsync("mdScrollTo(" + payload + ")");
            }
            else if (ratio is { } value)
            {
                await core.ExecuteScriptAsync(
                    "mdScrollToRatio(" + value.ToString("0.#####", CultureInfo.InvariantCulture) + ")");
            }
        }
        catch (Exception)
        {
            // 页面尚未就绪时忽略
        }

        // 重载 / 切换文件后，搜索栏若仍打开则重建命中
        if (SearchBar.Visibility == Visibility.Visible && SearchBox.Text.Length > 0)
            RunSearch();
    }

    private static string? HostPathToLocal(Uri uri, string rootDir)
    {
        try
        {
            var relative = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'))
                .Replace('/', Path.DirectorySeparatorChar);
            var full = Path.GetFullPath(Path.Combine(rootDir, relative));
            var root = Path.GetFullPath(rootDir + Path.DirectorySeparatorChar);
            return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static void OpenExternal(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch (Exception)
        {
            // 无关联程序时静默失败，不打断阅读
        }
    }

    // ==================================================================
    // 主题 / 缩放 / 目录 / 状态栏
    // ==================================================================

    private async Task ApplyWebViewThemeAsync()
    {
        if (!_webReady) return;
        var core = TryGetCore();
        if (core is null) return;
        try
        {
            var script = "mdApplyTheme(" + JsonSerializer.Serialize(AppTheme.WebViewThemeValue) + ")";
            await core.ExecuteScriptAsync(script);
        }
        catch (Exception)
        {
            // 页面尚未就绪时忽略
        }
    }

    private void SetZoom(double zoom)
    {
        ApplyZoom(Math.Round(zoom, 2));
        UpdateStatus(_currentPath ?? "未打开文件");
    }

    private void AdjustZoom(double delta)
    {
        ApplyZoom(Math.Round(Web.ZoomFactor + delta, 2));
        UpdateStatus(_currentPath ?? "未打开文件");
    }

    /// <summary>应用缩放并记忆到设置；WebView2 尚未就绪时只记录，待 OnLoaded 生效。</summary>
    private void ApplyZoom(double zoom)
    {
        var clamped = Math.Clamp(zoom, 0.25, 5.0);
        _settings.ZoomFactor = clamped;
        if (_webReady) Web.ZoomFactor = clamped;
    }

    private void SetToc(IReadOnlyList<TocEntry> entries)
    {
        // 每次换**新集合实例**并重新赋 ItemsSource。绝不能对已挂到 ItemsSource 的
        // List<T> 做 Clear/AddRange：List 不发 CollectionChanged，ListBox 内部生成器
        // 的记账会与实际集合脱节，下次布局直接抛 InvalidOperationException 闪退
        //（2026-10-01 实测：01→无标题文档→返回，坑 35）。
        _tocItems = new ObservableCollection<TocViewItem>(entries.Select(t => new TocViewItem(t)));
        TocList.ItemsSource = _tocItems;
        TocEmpty.Visibility = _tocItems.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ApplyTocVisibility();
    }

    /// <summary>把正文当前所在章节高亮到侧栏，并让其滚动可见。</summary>
    private void HighlightTocHeading(string id)
    {
        if (_tocItems.Count == 0) return;

        TocViewItem? current = null;
        foreach (var item in _tocItems)
        {
            var isCurrent = id.Length > 0 && string.Equals(item.Id, id, StringComparison.Ordinal);
            item.IsCurrent = isCurrent;
            if (isCurrent) current = item;
        }

        if (current is not null) TocList.ScrollIntoView(current);
    }

    private const double TocWidth = 236;
    private bool _tocShown;

    private void ApplyTocVisibility()
    {
        // 目录为空（纯文本/无标题文档）也保持侧栏：自动收起会让版面跳变，
        // 返回有目录的文档时又弹出来，观感突兀（实测反馈）。空目录显示占位文案。
        bool shouldShow = _settings.TocVisible && _currentPath is not null;
        if (shouldShow == _tocShown) return;
        _tocShown = shouldShow;
        AnimateToc(shouldShow);
    }

    private void AnimateToc(bool open)
    {
        var duration = TimeSpan.FromMilliseconds(open ? 220 : 170);
        var easing = new CubicEase { EasingMode = open ? EasingMode.EaseOut : EasingMode.EaseIn };

        if (open)
        {
            TocPanel.Visibility = Visibility.Visible;
            TocPanel.Width = 0;
            var anim = new System.Windows.Media.Animation.DoubleAnimation(TocWidth, duration)
            {
                EasingFunction = easing,
            };
            anim.Completed += (_, _) =>
            {
                TocPanel.BeginAnimation(Border.WidthProperty, null);
                TocPanel.Width = TocWidth;
            };
            TocPanel.BeginAnimation(Border.WidthProperty, anim);
        }
        else
        {
            var anim = new System.Windows.Media.Animation.DoubleAnimation(0, duration)
            {
                EasingFunction = easing,
            };
            anim.Completed += (_, _) =>
            {
                TocPanel.BeginAnimation(Border.WidthProperty, null);
                TocPanel.Width = 0;
                TocPanel.Visibility = Visibility.Collapsed;
            };
            TocPanel.BeginAnimation(Border.WidthProperty, anim);
        }
    }

    private void OnTocSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TocList.SelectedItem is not TocViewItem item) return;
        var core = TryGetCore();
        if (core is null) return;
        var payload = JsonSerializer.Serialize(item.Id);
        _ = core.ExecuteScriptAsync("mdScrollTo(" + payload + ")");
        TocList.SelectedIndex = -1; // 复位以便重复点击同一条目
    }

    private void UpdateStatus(string pathForDisplay)
    {
        StatusPath.Text = pathForDisplay;
        StatusWords.Text = _wordCount > 0 ? $"· 字数 {_wordCount:N0}" : "";

        var parts = new List<string>();
        if (_currentPath is not null)
        {
            // 用打开时缓存的大小，避免每次缩放都触发一次磁盘 I/O
            parts.Add($"{_lastFileSize / 1024.0:F0} KB");
            if (_lastEncoding.Length > 0) parts.Add(_lastEncoding);
            parts.Add($"渲染 {_lastRenderMs} ms");
        }
        if (_webReady)
        {
            var zoom = (int)Math.Round(Web.ZoomFactor * 100);
            parts.Add($"缩放 {zoom}%");
        }
        StatusInfo.Text = string.Join(" · ", parts);
    }

    // ==================================================================
    // 会话记忆：窗口几何 / 自动重载
    // ==================================================================

    private void RestoreWindowGeometry()
    {
        if (_settings.WindowWidth >= MinWidth && _settings.WindowHeight >= MinHeight)
        {
            Width = _settings.WindowWidth;
            Height = _settings.WindowHeight;
        }

        // 显示器可能已拔插或改过分辨率：窗口完全落在屏幕外时退回系统居中
        if (_settings is { WindowLeft: { } left, WindowTop: { } top }
            && IsVisibleOnAnyScreen(left, top, Width, Height))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }

        if (_settings.WindowMaximized) WindowState = WindowState.Maximized;
    }

    private static bool IsVisibleOnAnyScreen(double left, double top, double width, double height) =>
        left + width > SystemParameters.VirtualScreenLeft
        && top + height > SystemParameters.VirtualScreenTop
        && left < SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth
        && top < SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        SaveWindowGeometry();
        _settings.Save();
        // 退出时强制保存一次阅读进度（无论是否到达 5 秒判定窗口）
        _progress.Save();
        _watcher.Dispose();
    }

    private void SaveWindowGeometry()
    {
        // 最大化时记录「还原」尺寸，避免下次还原退回异常大小
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;

        if (bounds.Width >= MinWidth && bounds.Height >= MinHeight)
        {
            _settings.WindowWidth = bounds.Width;
            _settings.WindowHeight = bounds.Height;
            _settings.WindowLeft = bounds.Left;
            _settings.WindowTop = bounds.Top;
        }

        _settings.WindowMaximized = WindowState == WindowState.Maximized;
    }

    /// <summary>按设置开关对当前文档的自动重载监听。</summary>
    private void UpdateWatcher()
    {
        if (_settings.AutoReload && _currentPath is { } path && File.Exists(path))
            _watcher.Watch(path);
        else
            _watcher.Stop();
    }

    private async Task AutoReloadAsync()
    {
        if (!_settings.AutoReload || _currentPath is not { } path || !File.Exists(path)) return;
        await OpenFileAsync(path);
    }

    private void OnAutoReloadChecked(object sender, RoutedEventArgs e)
    {
        _settings.AutoReload = true;
        UpdateWatcher();
    }

    private void OnAutoReloadUnchecked(object sender, RoutedEventArgs e)
    {
        _settings.AutoReload = false;
        UpdateWatcher();
    }

    // ==================================================================
    // 页内搜索（Ctrl+F）
    // ==================================================================

    private void ShowSearchBar()
    {
        if (!_webReady) return;
        SearchBar.Visibility = Visibility.Visible;
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void HideSearchBar()
    {
        if (SearchBar.Visibility != Visibility.Visible) return;
        SearchBar.Visibility = Visibility.Collapsed;
        SearchStatus.Text = "";
        _searchDebounce.Stop();
        SearchBox.Text = string.Empty; // 触发 TextChanged，最终以 mdSearch('') 清空页面高亮
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private void OnSearchBoxKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                SearchStep(Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? -1 : 1);
                e.Handled = true;
                break;
            case Key.Escape:
                HideSearchBar();
                e.Handled = true;
                break;
        }
    }

    private void RunSearch()
    {
        var core = TryGetCore();
        if (core is null) return;
        var payload = JsonSerializer.Serialize(SearchBox.Text);
        _ = core.ExecuteScriptAsync("mdSearch(" + payload + ")");
    }

    private void SearchStep(int delta)
    {
        var core = TryGetCore();
        if (core is null) return;
        _ = core.ExecuteScriptAsync("mdSearchStep(" + delta + ")");
    }

    private void OnSearchPrevClick(object sender, RoutedEventArgs e) => SearchStep(-1);
    private void OnSearchNextClick(object sender, RoutedEventArgs e) => SearchStep(1);
    private void OnSearchCloseClick(object sender, RoutedEventArgs e) => HideSearchBar();

    // ==================================================================
    // 拖拽
    // ==================================================================

    private void OnWindowDragOver(object sender, DragEventArgs e)
    {
        e.Effects = GetDroppedFile(e.Data) is not null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnWindowDrop(object sender, DragEventArgs e)
    {
        if (GetDroppedFile(e.Data) is { } file)
            _ = OpenFileAsync(file);
        e.Handled = true;
    }

    private static string? GetDroppedFile(IDataObject data)
    {
        if (!data.GetDataPresent(DataFormats.FileDrop)) return null;
        if (data.GetData(DataFormats.FileDrop) is not string[] files) return null;
        return files.FirstOrDefault(App.IsAcceptablePath);
    }

    // ==================================================================
    // 菜单事件
    // ==================================================================

    private void OnOpenClick(object sender, RoutedEventArgs e) => ExecuteOpenDialog();
    private void OnReloadClick(object sender, RoutedEventArgs e) => _ = ExecuteReloadAsync();
    private void OnExitClick(object sender, RoutedEventArgs e) => Close();

    // 标题栏窗口按钮
    private void OnMinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnMaxRestoreClick(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 标题栏按下。<c>WindowChrome.CaptionHeight</c> 为 0，系统不认这块是标题栏，
    /// 所以拖动、双击最大化都得自己做。
    ///
    /// <para>**这里故意不直接调 <see cref="Window.DragMove"/>**：按下即拖的话，单纯点一下
    /// 标题栏也会进入原生移动循环，于是「最大化时点标题栏」会莫名其妙把窗口还原并挪位置。
    /// 改成按下只记录起点，等鼠标真正越过系统拖动阈值（<see cref="OnTitleBarMouseMove"/>）
    /// 才还原窗口并开始拖动。</para>
    /// </summary>
    private void OnTitleBarMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        // 双击：最大化 / 还原。从最大化还原交给系统按 RestoreBounds 放回原位——
        // 双击时光标必然贴着屏幕上沿，若按光标重新落位会把标题栏顶到屏幕外。
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            return;
        }

        _titleBarPressed = true;
        _titleBarPressedAt = e.GetPosition(this);
    }

    private void OnTitleBarMouseMove(object sender, MouseEventArgs e)
    {
        if (!_titleBarPressed) return;

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _titleBarPressed = false;
            return;
        }

        var now = e.GetPosition(this);
        var movedEnough =
            Math.Abs(now.X - _titleBarPressedAt.X) >= SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(now.Y - _titleBarPressedAt.Y) >= SystemParameters.MinimumVerticalDragDistance;
        if (!movedEnough) return;

        _titleBarPressed = false;

        if (WindowState == WindowState.Maximized)
        {
            // 从最大化拖出来：先还原，再把「落位 + 进入拖动」放到本次状态切换之后再跑。
            // 不能连着直接 DragMove()——实测窗口会还原但不再跟手（状态切换过程中 WPF
            // 记录的鼠标按下状态被重置，DragMove 内部校验不过、静默失败）。
            //
            // ⚠ 横向比例必须用**窗口左上角的真实屏幕坐标**来算，不能用 Window.Left：
            // 最大化时 WPF 的 Left/Top 返回的是「还原后」的位置，不是当前实际位置
            //（实测最大化时 Left=544，而窗口真实左上角在 -7.2），拿它当基准会算歪。
            var restore = RestoreBounds;                // 只有最大化时有效，务必在改状态之前取
            var origin = CursorInDip(new Point(0, 0));  // 当前（最大化）窗口左上角，DIP
            var cursor = CursorInDip(now);
            var ratio = ActualWidth > 0
                ? Math.Clamp((cursor.X - origin.X) / ActualWidth, 0, 1)
                : 0.5;

            WindowState = WindowState.Normal;
            Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
            {
                Left = cursor.X - restore.Width * ratio;
                // 保持光标在标题栏上的相对高度，拖起来才不会跳
                Top = cursor.Y - now.Y;
                DragWindow();
            }));
            return;
        }

        DragWindow();
    }

    private void OnTitleBarMouseUp(object sender, MouseButtonEventArgs e) => _titleBarPressed = false;

    private void DragWindow()
    {
        try
        {
            // 内部走 WM_SYSCOMMAND / SC_MOVE 的原生移动循环，因此贴边吸附仍然有效
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 调用时鼠标已松开等情况下 DragMove 会抛异常，忽略即可
        }
    }

    /// <summary>
    /// 把「窗口内坐标（DIP）」换算成「屏幕坐标（DIP）」。
    /// <see cref="Visual.PointToScreen"/> 返回的是**物理像素**，而 <c>Left</c> / <c>Top</c> /
    /// <see cref="Window.RestoreBounds"/> 全是 DIP——两者直接相减在 125% 缩放下会算错位置，
    /// 必须先用 DPI 比例归一。
    /// </summary>
    private Point CursorInDip(Point positionInWindow)
    {
        var dpi = System.Windows.Media.VisualTreeHelper.GetDpi(this);
        var screen = PointToScreen(positionInWindow);
        return new Point(screen.X / dpi.DpiScaleX, screen.Y / dpi.DpiScaleY);
    }

    private void OnThemeSystemClick(object sender, RoutedEventArgs e) => SetTheme(ThemeMode.System);
    private void OnThemeLightClick(object sender, RoutedEventArgs e) => SetTheme(ThemeMode.Light);
    private void OnThemeDarkClick(object sender, RoutedEventArgs e) => SetTheme(ThemeMode.Dark);

    private void SetTheme(ThemeMode mode)
    {
        _settings.Theme = mode;
        MenuThemeSystem.IsChecked = mode == ThemeMode.System;
        MenuThemeLight.IsChecked = mode == ThemeMode.Light;
        MenuThemeDark.IsChecked = mode == ThemeMode.Dark;
        AppTheme.Apply(mode);
    }

    private void OnZoomInClick(object sender, RoutedEventArgs e) => AdjustZoom(+0.1);
    private void OnZoomOutClick(object sender, RoutedEventArgs e) => AdjustZoom(-0.1);
    private void OnZoomResetClick(object sender, RoutedEventArgs e) => SetZoom(1.0);

    private void OnToggleTocClick(object sender, RoutedEventArgs e)
    {
        // 与菜单项/Ctrl+T 共用同一状态源，Checked/Unchecked 事件负责实际应用
        MenuToc.IsChecked = !MenuToc.IsChecked;
    }

    private void OnTocChecked(object sender, RoutedEventArgs e)
    {
        _settings.TocVisible = true;
        ApplyTocVisibility();
    }

    private void OnTocUnchecked(object sender, RoutedEventArgs e)
    {
        _settings.TocVisible = false;
        ApplyTocVisibility();
    }

    private void OnAboutClick(object sender, RoutedEventArgs e) => AppDialog.ShowAbout(this);

    /// <summary>目录列表的显示模型。</summary>
    public sealed class TocViewItem : INotifyPropertyChanged
    {
        private bool _isCurrent;

        public TocViewItem(TocEntry entry)
        {
            Id = entry.Id;
            Text = entry.Text;
            Indent = new Thickness(14 * (entry.Level - 1), 0, 0, 0);
        }

        public string Id { get; }
        public string Text { get; }
        public Thickness Indent { get; }

        /// <summary>正文滚动时该条目是否为当前所在章节（由滚动上报驱动）。</summary>
        public bool IsCurrent
        {
            get => _isCurrent;
            set
            {
                if (_isCurrent == value) return;
                _isCurrent = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrent)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    // ==================================================================
    // 无边框窗口：修正最大化尺寸（不遮任务栏、支持多屏）
    // ==================================================================

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)?.AddHook(WndProc);
        WindowRounding.Apply(this);   // Win11 系统圆角（DWM 合成层，不做 WPF 裁剪）
    }

    private static IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmGetMinMaxInfoId)
            FixMaximizedBounds(hwnd, lParam);
        return IntPtr.Zero;
    }

    private const int WmGetMinMaxInfoId = 0x0024;

    private static void FixMaximizedBounds(IntPtr hwnd, IntPtr lParam)
    {
        try
        {
            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero)
            {
                var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                if (GetMonitorInfo(monitor, ref info))
                {
                    RECT work = info.rcWork;
                    RECT mon = info.rcMonitor;
                    mmi.ptMaxPosition.x = work.left - mon.left;
                    mmi.ptMaxPosition.y = work.top - mon.top;
                    mmi.ptMaxSize.x = work.right - work.left;
                    mmi.ptMaxSize.y = work.bottom - work.top;
                }
            }
            Marshal.StructureToPtr(mmi, lParam, true);
        }
        catch (Exception)
        {
            // 修复失败时保持系统默认行为
        }
    }

    private const int MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, int flags);

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x; public int y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int left; public int top; public int right; public int bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
    }
}
