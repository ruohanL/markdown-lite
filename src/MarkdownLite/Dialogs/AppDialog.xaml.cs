using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using MarkdownLite.Services;
namespace MarkdownLite.Dialogs;

/// <summary>对话框正文的一节：可选的节标题 + 若干「键 / 值」行。</summary>
public sealed class DialogSection
{
    public string Heading { get; init; } = "";

    public IReadOnlyList<DialogRow> Rows { get; init; } = Array.Empty<DialogRow>();
}

/// <summary>对话框正文的一行；<see cref="Key"/> 为空时只显示正文并占满整行。</summary>
public sealed class DialogRow
{
    public string Key { get; init; } = "";

    public string Text { get; init; } = "";
}

/// <summary>
/// 应用自有风格的模态弹窗，替代系统 <c>MessageBox</c>。
///
/// 两个动机：
/// 1. **界面统一** —— MessageBox 是系统旧式对话框，字体、间距、配色与主界面完全脱节；
///    这里用同一套 Brush.* 令牌 + 同一套圆角/描边语言，并跟随深浅主题。
/// 2. **去掉提示音** —— <c>MessageBox.Show(..., MessageBoxImage.*)</c> 会触发系统提示音
///    （MessageBeep）。自绘窗口不会发出任何声音。
///
/// 用法：<see cref="ShowAbout"/> / <see cref="ShowError"/>。窗口本身无边框、可拖动标题区、
/// Esc 关闭、Enter 触发主按钮。
/// </summary>
public partial class AppDialog : Window
{
    public enum Kind
    {
        Info,
        Error,
    }

    private Action? _secondaryAction;

    private AppDialog()
    {
        InitializeComponent();
    }

    // ==================================================================
    // 对外入口
    // ==================================================================

    /// <summary>「关于」弹窗：品牌头 + 分节说明 + 快捷键表。</summary>
    public static void ShowAbout(Window? owner)
    {
        var version = typeof(AppDialog).Assembly.GetName().Version;
        var versionText = version is null ? "" : $"版本 {version.Major}.{version.Minor}.{version.Build}";

        Present(
            owner,
            Kind.Info,
            "关于 MarkdownLite",
            $"{versionText} · 纯只读 Markdown 阅读器",
            BuildAboutSections(),
            "确定",
            null);
    }

    /// <summary>错误弹窗：标题 + 原始错误信息（无提示音）。</summary>
    public static void ShowError(Window? owner, string title, string message)
    {
        Present(
            owner,
            Kind.Error,
            title,
            "",
            [new DialogSection { Rows = [new DialogRow { Text = message }] }],
            "确定",
            null);
    }

    // ==================================================================
    // 内容
    // ==================================================================

    private static IReadOnlyList<DialogSection> BuildAboutSections() =>
    [
        new DialogSection
        {
            Heading = "渲染",
            Rows =
            [
                new DialogRow { Key = "解析", Text = "Markdig（CommonMark + GFM：表格、任务列表、删除线、脚注、自动链接）" },
                new DialogRow { Key = "高亮", Text = "内置 highlight.js，完全离线，不加载任何远程资源" },
                new DialogRow { Key = "编码", Text = "自动探测 UTF-8 / GB18030 等，读取后源文件保持原样" },
            ],
        },
        new DialogSection
        {
            Heading = "安全",
            Rows =
            [
                new DialogRow { Key = "只读", Text = "不修改、不写回源文件；渲染缓存仅存于本机应用目录" },
                new DialogRow { Key = "隔离", Text = "原始 HTML 默认禁用，外链一律交系统浏览器打开" },
            ],
        },
        new DialogSection
        {
            Heading = "快捷键",
            Rows =
            [
                new DialogRow { Key = "Ctrl + O", Text = "打开文件" },
                new DialogRow { Key = "F5", Text = "重新加载当前文件" },
                new DialogRow { Key = "Ctrl + 加 / 减 / 0", Text = "放大 / 缩小 / 重置缩放" },
                new DialogRow { Key = "Ctrl + T", Text = "显示 / 隐藏目录侧栏" },
                new DialogRow { Key = "Ctrl + F", Text = "页内搜索" },
            ],
        },
    ];

    // ==================================================================
    // 构建与呈现
    // ==================================================================

    private static void Present(
        Window? owner,
        Kind kind,
        string title,
        string subtitle,
        IReadOnlyList<DialogSection> sections,
        string primaryText,
        (string Text, Action Action)? secondary)
    {
        var dialog = new AppDialog();

        var host = owner ?? Application.Current?.MainWindow;
        if (host is not null && !ReferenceEquals(host, dialog) && host.IsLoaded)
        {
            dialog.Owner = host;
        }
        else
        {
            // 没有可用宿主（例如启动阶段就抛异常）时退回屏幕居中
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        dialog.ApplyKind(kind);
        // 无边框窗口看不到 Title，但它决定任务栏/UIA/自动化工具识别到的窗口名，必须与内容一致
        dialog.Title = title;
        dialog.TitleText.Text = title;
        dialog.SubtitleText.Text = subtitle;
        dialog.SubtitleText.Visibility = string.IsNullOrWhiteSpace(subtitle)
            ? Visibility.Collapsed
            : Visibility.Visible;
        dialog.SectionsHost.ItemsSource = sections;
        dialog.PrimaryButton.Content = primaryText;

        if (secondary is { } extra)
        {
            dialog.SecondaryButton.Content = extra.Text;
            dialog.SecondaryButton.Visibility = Visibility.Visible;
            dialog._secondaryAction = extra.Action;
        }

        dialog.Loaded += (_, _) => dialog.PlayEntrance();
        dialog.ShowDialog();
    }

    /// <summary>与主窗口一致：Win11 系统圆角（DWM 合成层，不做 WPF 裁剪）。</summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowRounding.Apply(this);
    }

    private void ApplyKind(Kind kind)
    {
        // 信息态（关于）：真实应用图标；错误态：描边块里的「!」（项目禁用 emoji）
        var isError = kind == Kind.Error;
        IconImage.Visibility = isError ? Visibility.Collapsed : Visibility.Visible;
        IconBadge.Visibility = isError ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PlayEntrance()
    {
        Card.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
    }

    // ==================================================================
    // 交互
    // ==================================================================

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        e.Handled = true;
        Close();
    }

    private void OnHeaderDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 非「鼠标按下」状态调用 DragMove 会抛异常，忽略即可
        }
    }

    private void OnPrimaryClick(object sender, RoutedEventArgs e) => Close();

    private void OnSecondaryClick(object sender, RoutedEventArgs e)
    {
        _secondaryAction?.Invoke();
        Close();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
