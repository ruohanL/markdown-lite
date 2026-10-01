using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using Markdig.Extensions.AutoIdentifiers;
using Markdig.Extensions.Yaml;
using Markdig.Renderers.Html;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace MarkdownLite.Services;

/// <summary>目录条目。</summary>
public sealed record TocEntry(int Level, string Id, string Text);

/// <summary>渲染结果。Title 来自 front matter（无则为 null，由调用方回退文件名）。</summary>
public sealed record RenderResult(string BodyHtml, IReadOnlyList<TocEntry> Toc, string? Title);

/// <summary>
/// Markdown -&gt; HTML 的纯函数式渲染管线（无 WPF / WebView2 依赖，可单元测试）。
///
/// 安全策略（对应"仅阅读、不联网、防 XSS"要求）：
/// 1. AST 阶段剥离所有原始 HTML 块/内联节点（HtmlBlock / HtmlInline），默认禁用原始 HTML；
/// 2. 链接与图片 URL 按协议白名单过滤：javascript:/vbscript:/file:(越界)/未知协议一律替换为 #；
/// 3. 相对路径基于当前 .md 所在目录解析，并改写为本地虚拟主机 URL；禁止目录穿越（..）逃逸；
/// 4. 生成的完整文档带严格 CSP：default-src 'none'，仅允许三个本地虚拟主机与 data: 图片，
///    connect-src 'none' 保证页面无法发起任何网络请求。
/// </summary>
public static class MarkdownRenderer
{
    public const string AssetsHost = "markdownlite.assets.local";
    public const string CacheHost = "markdownlite.cache.local";
    public static readonly string[] DocHosts = ["markdownlite.doc1.local", "markdownlite.doc2.local"];

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAutoLinks()
        .UsePipeTables()
        .UseGridTables()
        .UseEmphasisExtras()
        .UseTaskLists()
        .UseFootnotes()
        .UseYamlFrontMatter()
        .UseAutoIdentifiers(AutoIdentifierOptions.GitHub)
        .Build();

    /// <summary>
    /// 将 Markdown 文本渲染为 body 片段并生成目录。
    /// </summary>
    /// <param name="markdown">源文本。</param>
    /// <param name="documentDirectory">当前 .md 所在目录；null 时相对链接/图片将不可用。</param>
    /// <param name="documentHostBase">
    /// 该目录映射到的虚拟主机基址（形如 http://markdownlite.doc1.local）；
    /// 由调用方轮换并提前完成映射，null 时相对 URL 被替换为 #。
    /// </param>
    public static RenderResult Render(string markdown, string? documentDirectory, string? documentHostBase)
    {
        var document = Markdown.Parse(markdown, Pipeline);

        var title = ExtractFrontMatterTitle(markdown);
        // 剥离原始 HTML / front matter 与链接改写合并为**一次** AST 遍历（原来是两趟）
        StripAndRewrite(document, documentDirectory, documentHostBase);

        // 这里必须带 Pipeline：不带的话 ToHtml 会用默认管线建渲染器，
        // 表格 / 任务列表 / 脚注等扩展节点将不再被正确渲染（实测语义会变，不能省）。
        // 注：Parse 里的 Setup(document) 与这里的 Setup(renderer) 作用对象不同，
        // 不是重复工作，没有可以去掉的那一次。
        var body = Markdown.ToHtml(document, Pipeline);

        // TOC 直接从 AST 取，不再对渲染后的 HTML 做两遍全量正则扫描
        var toc = ExtractTocFromAst(document);

        return new RenderResult(body, toc, title);
    }

    /// <summary>
    /// 把 body 片段套入带严格 CSP 的完整 HTML 文档。
    ///
    /// 用 <see cref="StringBuilder"/> 一次拼成：原实现是「模板 .Replace(标题).Replace(正文)」，
    /// 会额外产生两份与文档同量级的中间字符串（正文可达 1MB+）。
    /// 预分配容量后 <see cref="StringBuilder.ToString"/> 基本只有一次最终分配。
    /// </summary>
    public static string BuildDocument(string bodyHtml, string? title = null)
    {
        var encodedTitle = WebUtility.HtmlEncode(title ?? "MarkdownLite");
        var sb = new StringBuilder(
            DocumentHead.Length + DocumentTail.Length + 512
            + encodedTitle.Length + bodyHtml.Length);
        sb.Append(DocumentHead).Append(encodedTitle);
        AppendDocumentMid(sb);
        sb.Append(bodyHtml).Append(DocumentTail);
        return sb.ToString();
    }

    /// <summary>
    /// 文档内容的短版本号（FNV-1a 64 位，稳定、无依赖），用于给渲染结果的 URL 做缓存键：
    /// **内容不变 → URL 不变**（可复用缓存、不会让缓存无限膨胀）；
    /// **内容一变 → URL 立刻变**（不可能读到上一次的旧文档）。
    ///
    /// 之所以不干脆去掉版本号：实测 WebView2 会把虚拟主机资源按 URL 缓存下来，
    /// 改文件不改 URL 时会继续吃旧缓存（历史踩坑记录），文档 URL 同样有这个风险。
    /// </summary>
    public static string ContentVersion(string html)
    {
        unchecked
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            var hash = offset;
            foreach (var ch in html)
            {
                hash ^= (byte)ch;
                hash *= prime;
                hash ^= (byte)(ch >> 8);
                hash *= prime;
            }
            return hash.ToString("x16");
        }
    }

    // ------------------------------------------------------------------
    // 原始 HTML 剥离 + 链接改写（单次遍历）
    // ------------------------------------------------------------------

    /// <summary>
    /// 一次遍历同时完成两件事：
    /// ① 标记「原始 HTML 块 / 内联 HTML / YAML front matter 块」为待删除；
    /// ② 对 <see cref="LinkInline"/> 做 URL 白名单过滤与相对路径改写。
    ///
    /// <para>删除姿势很重要：**先收集、遍历结束后统一 Remove**。
    /// 原来的实现是在遍历容器的过程中同步 `RemoveAt` / `Remove`，那就必须倒序走索引、
    /// 并且时刻注意别再用到已删节点；合并遍历后这种写法很容易踩坑。
    /// 这里遍历阶段只读、不做任何结构性修改。</para>
    ///
    /// <para>不需要担心「父节点被删后子节点再删一次」：HtmlBlock / YamlFrontMatterBlock 都是块级且互不嵌套，
    /// HtmlInline 之间也不会嵌套，而 <see cref="MarkdownObject.Remove"/> 对已不在父集合里的节点是空操作。</para>
    /// </summary>
    private static void StripAndRewrite(MarkdownObject root, string? docDir, string? hostBase)
    {
        var toRemove = new List<MarkdownObject>();

        foreach (var node in root.Descendants())
        {
            switch (node)
            {
                // 原始 HTML 与 YAML front matter 都不进入输出
                case HtmlBlock or HtmlInline or YamlFrontMatterBlock:
                    toRemove.Add(node);
                    break;

                case LinkInline { Url: not null } link:
                    link.Url = SanitizeUrl(link.Url, link.IsImage, docDir, hostBase);
                    link.Title = null;
                    break;
            }
        }

        foreach (var node in toRemove)
        {
            switch (node)
            {
                // 内联层：Inline 自带 Remove()（把自己从所属 ContainerInline 里摘掉）
                case Inline inline:
                    inline.Remove();
                    break;

                // 块级：按索引从所属 ContainerBlock 里摘掉
                case Block block:
                    if (block.Parent is ContainerBlock parent)
                        parent.RemoveAt(parent.IndexOf(block));
                    break;
            }
        }
    }

    // ------------------------------------------------------------------
    // YAML front matter
    // ------------------------------------------------------------------

    /// <summary>
    /// 从文档开头提取 YAML front matter 的 <c>title</c>。
    /// 仅在首行为 <c>---</c> <b>且存在闭合分隔符</b> 时才认定为 front matter，
    /// 以免把普通文档开头的分隔线误判为元数据。
    /// </summary>
    public static string? ExtractFrontMatterTitle(string markdown)
    {
        using var reader = new StringReader(markdown);
        if (reader.ReadLine()?.TrimEnd() is not "---") return null;

        var closed = false;
        string? title = null;
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var trimmed = line.Trim();
            if (trimmed is "---" or "...")
            {
                closed = true;
                break;
            }
            if (title is null && trimmed.StartsWith("title:", StringComparison.OrdinalIgnoreCase))
            {
                var value = trimmed["title:".Length..].Trim().Trim('"', '\'').Trim();
                if (value.Length > 0) title = value;
            }
        }

        return closed ? title : null;
    }

    // ------------------------------------------------------------------
    // 链接 / 图片 URL 过滤与改写
    // ------------------------------------------------------------------

    /// <summary>
    /// 单个 URL 的安全过滤与改写（public 供单元测试直接验证）。
    /// 规则：http/https 原样保留（由宿主层拦截后用系统浏览器打开）；#锚点保留；
    /// 图片允许 data:image/*；其余协议（javascript:/vbscript:/未知 scheme）替换为 #；
    /// 相对路径解析到文档目录内并改写为虚拟主机 URL，越界（../）替换为 #。
    /// 片段（#anchor）在 http(s)/mailto 外链上原样保留，在相对路径上于解析出真实路径后重新拼回，
    /// 以便外链分节链接与跨文档锚点都能正确定位。
    /// </summary>
    public static string SanitizeUrl(string url, bool isImage, string? docDir, string? docHostBase)
    {
        var trimmed = url.Trim();
        if (trimmed.Length == 0) return "#";
        if (trimmed[0] == '#') return trimmed; // 页内锚点

        // 拆分片段：路径部分用于协议判定与解析，片段在最终改写时拼回
        var hashIndex = trimmed.IndexOf('#');
        var pathPart = hashIndex >= 0 ? trimmed[..hashIndex] : trimmed;
        var fragment = hashIndex >= 0 ? trimmed[hashIndex..] : string.Empty;
        if (pathPart.Length == 0) return "#";

        if (Uri.TryCreate(pathPart, UriKind.Absolute, out var absolute))
        {
            switch (absolute.Scheme.ToLowerInvariant())
            {
                case "http":
                case "https":
                    return trimmed; // 外链保留片段，如 https://site/page#section
                case "mailto":
                    return isImage ? "#" : trimmed;
                case "data":
                    return isImage && pathPart.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)
                        ? pathPart
                        : "#";
                case "file":
                    var localPath = TryGetLocalPath(absolute);
                    var fileMapped = MapLocalPathToHost(localPath, docDir, docHostBase);
                    return fileMapped is null ? "#" : fileMapped + fragment;
                default:
                    // javascript:, vbscript:, about:, blob:, ms-*: 等一律拒绝
                    return "#";
            }
        }

        // 相对路径
        if (docDir is null || docHostBase is null) return "#";
        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(pathPart);
        }
        catch (Exception)
        {
            return "#";
        }
        if (decoded.Contains(':')) return "#"; // 盘符或协议碎片

        var combined = Path.GetFullPath(
            Path.Combine(docDir, decoded.Replace('/', Path.DirectorySeparatorChar)));
        var mapped = MapLocalPathToHost(combined, docDir, docHostBase);
        return mapped is null ? "#" : mapped + fragment;
    }

    private static string? MapLocalPathToHost(string? fullPath, string? docDir, string? docHostBase)
    {
        if (fullPath is null || docDir is null || docHostBase is null) return null;

        var root = Path.GetFullPath(docDir + Path.DirectorySeparatorChar);
        var target = Path.GetFullPath(fullPath);

        // 防止 ../ 目录穿越到文档目录之外
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return null;

        var relative = Path.GetRelativePath(root, target);
        if (relative.Length == 0) return null;

        var segments = relative.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries);
        var encoded = string.Join('/', segments.Select(Uri.EscapeDataString));
        return docHostBase.TrimEnd('/') + "/" + encoded;
    }

    private static string? TryGetLocalPath(Uri fileUri)
    {
        try
        {
            return fileUri.IsFile ? fileUri.LocalPath : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------
    // 目录（TOC）提取：直接从 AST 取标题
    // ------------------------------------------------------------------

    /// <summary>
    /// 从 AST 直接提取目录：遍历 <see cref="HeadingBlock"/>，标题文本取自内联节点、锚点取自带属性 Id。
    ///
    /// <para>替代了原来的做法（对渲染后的 HTML 跑两遍正则：`HeadingRegex` 扫全文 + `TagRegex` 去标签）。
    /// 好处有两点：省掉一次对**最长字符串（整篇 HTML）**的全量扫描；
    /// 标题文本直接来自源码 AST，不需要再 `HtmlDecode`（也就不会把标题里本义就是 `&amp;` 的文本二次解码）。</para>
    ///
    /// <para>前提是管线启用了 <c>UseAutoIdentifiers(AutoIdentifierOptions.GitHub)</c>，
    /// 它会把生成的 id 写进标题的属性里（<c>GetAttributes().Id</c>）。</para>
    /// </summary>
    public static IReadOnlyList<TocEntry> ExtractTocFromAst(MarkdownDocument document)
    {
        var toc = new List<TocEntry>();
        foreach (var heading in document.Descendants<HeadingBlock>())
        {
            var id = heading.GetAttributes().Id;
            if (string.IsNullOrEmpty(id)) continue;

            var text = CollectInlineText(heading.Inline);
            if (text.Length > 0)
                toc.Add(new TocEntry(heading.Level, id, text));
        }
        return toc;
    }

    /// <summary>把一个标题的内联节点拼成纯文本（LiteralInline / CodeInline 取内容，软换行折算成空格）。</summary>
    private static string CollectInlineText(ContainerInline? inline)
    {
        if (inline is null) return string.Empty;

        var sb = new StringBuilder();
        foreach (var node in inline.Descendants())
        {
            switch (node)
            {
                case LiteralInline literal: sb.Append(literal.Content.ToString()); break;
                case CodeInline code: sb.Append(code.Content); break;
                case LineBreakInline: sb.Append(' '); break;
            }
        }
        return sb.ToString().Trim();
    }

    // ------------------------------------------------------------------
    // HTML 文档模板
    // ------------------------------------------------------------------

    private const string Csp =
        "default-src 'none'; " +
        "img-src http://markdownlite.assets.local http://markdownlite.cache.local " +
        "http://markdownlite.doc1.local http://markdownlite.doc2.local data:; " +
        // style-src 必须带 'unsafe-inline'：文档里那段「让页面透明」的规则是**内联 <style>**，
        // CSP 默认会把它整块拦掉（实测：规则写在 HTML 里但完全不生效，正文背景照旧）。
        // 只放开样式、script-src 仍是白名单，且 Markdig 阶段已剥离全部原始 HTML，
        // 页面上不存在攻击者可控的内联样式，所以这个放宽不引入实际风险。
        "style-src 'self' 'unsafe-inline' http://markdownlite.assets.local; " +
        "script-src http://markdownlite.assets.local; " +
        "font-src 'none'; connect-src 'none'; " +
        "frame-src 'none'; object-src 'none'; base-uri 'none'; form-action 'none'";

    // ------------------------------------------------------------------
    // 阅读区底色
    // ------------------------------------------------------------------

    /// <summary>浅色下阅读区的底色。<b>必须与 WPF 侧的 <c>Brush.Window</c> 一致</b>。</summary>
    public const string LightBackground = "#ffffff";

    /// <summary>深色下阅读区的底色。<b>必须与 WPF 侧的 <c>Brush.Window</c> 一致</b>。</summary>
    public const string DarkBackground = "#1e1e1e";

    /// <summary>
    /// 注入到文档里的内联样式：显式设定阅读区底色，并归零默认边距。
    ///
    /// 为什么不靠「页面透明 + WebView2 透出窗口底色」：那条链路依赖
    /// `WebView2.DefaultBackgroundColor`、CSP 是否放行内联样式、以及 viewer.css 的缓存状态，
    /// 任何一环出问题都会让阅读区显出别的颜色（症状隐蔽：改完看着正常、
    /// 实际底色还是旧的）。这里直接写死颜色，**不吃任何缓存**
    /// （文档 URL 带 ?v=ticks，每次都是新文档），结果一定是确定的。
    ///
    /// 颜色按 <c>data-theme</c> 分支：reader.js 会在运行时按应用主题设置该属性，
    /// `auto` 则跟随系统（与应用内「跟随系统」语义一致）。用 <c>!important</c> 压住
    /// viewer.css 里的 `--bg`，避免两者打架。
    /// </summary>
    private const string BackgroundStyle =
        "<style>" +
        "html,body{background-color:" + LightBackground + " !important;margin:0;padding:0;}" +
        "html[data-theme=\"dark\"],html[data-theme=\"dark\"] body{background-color:" +
        DarkBackground + " !important;}" +
        "@media (prefers-color-scheme:dark){html[data-theme=\"auto\"]," +
        "html[data-theme=\"auto\"] body{background-color:" + DarkBackground + " !important;}}" +
        "</style>\n";

    /// <summary>
    /// 静态资源（CSS/JS）的缓存版本 = **资源内容的 FNV 哈希**。
    ///
    /// <para>WebView2 会把虚拟主机的资源**按 URL** 缓存下来，改了 <c>wwwroot</c> 里的文件但 URL 没变，
    /// 浏览器会继续吃旧缓存（历史踩坑记录）。
    /// 把「文件内容」编进 URL 就从根上解决了：改文件 → 哈希变 → URL 变 → 必然拿到新的。</para>
    ///
    /// <para>结果按文件名缓存，一次会话只读一遍文件。读不到（单测环境没有 wwwroot 等）时退回固定值，
    /// URL 依旧稳定可用。</para>
    /// </summary>
    public static string ResourceVersion(string fileName)
    {
        lock (ResourceVersionCache)
        {
            if (ResourceVersionCache.TryGetValue(fileName, out var cached)) return cached;
        }

        var version = "1";
        try
        {
            var dir = AssetDirectory ?? Path.Combine(AppContext.BaseDirectory, "wwwroot");
            version = Fnv(File.ReadAllBytes(Path.Combine(dir, fileName)));
        }
        catch (Exception)
        {
            // 读不到资源（如单测环境）：退回固定值，URL 依旧稳定可用
        }

        lock (ResourceVersionCache)
        {
            ResourceVersionCache[fileName] = version;
        }
        return version;
    }

    private static readonly Dictionary<string, string> ResourceVersionCache = new();

    /// <summary>wwwroot 所在目录。由宿主在初始化时注入（<see cref="SetAssetDirectory"/>）；
    /// 未注入时回退到 <c>AppContext.BaseDirectory\wwwroot</c>。</summary>
    public static void SetAssetDirectory(string directory) => AssetDirectory = directory;

    private static string? AssetDirectory { get; set; }

    /// <summary>FNV-1a 64 位：稳定（跨进程一致）、无依赖、足够散。</summary>
    private static string Fnv(ReadOnlySpan<byte> data)
    {
        unchecked
        {
            const ulong offset = 14695981039346656037UL;
            const ulong prime = 1099511628211UL;
            var hash = offset;
            foreach (var b in data)
            {
                hash ^= b;
                hash *= prime;
            }
            return hash.ToString("x16");
        }
    }

    // ⚠ DocumentMid 不再是静态常量：CSS/JS 的缓存 id 取自**资源内容哈希**，
    //   需要在 BuildDocument 时才计算（见 AppendDocumentMid）。

    private const string DocumentHead = "<!DOCTYPE html>\n" +
        "<html data-theme=\"auto\" lang=\"zh-CN\">\n" + "<head>\n" +
        "<meta charset=\"utf-8\" />\n" +
        "<meta http-equiv=\"Content-Security-Policy\" content=\"" + Csp + "\" />\n" +
        "<title>";

    private const string DocumentTail = "\n</body>\n</html>\n";

    /// <summary>
    /// 输出 &lt;/title&gt; 之后的样式/脚本引用。每个资源的缓存 id 取自**该文件内容的 FNV 哈希**，
    /// 因此「改了 wwwroot 里的某个文件 → 只有那个文件的 URL 变」，其余资源继续命中缓存。
    /// </summary>
    private static void AppendDocumentMid(StringBuilder sb)
    {
        sb.Append("</title>\n");
        AppendStylesheet(sb, "hljs-github.min.css", "hljs-light");
        AppendStylesheet(sb, "hljs-github-dark.min.css", "hljs-dark", extraDisabled: true);
        AppendStylesheet(sb, "viewer.css");
        sb.Append(BackgroundStyle);
        AppendScript(sb, "highlight.min.js");
        AppendScript(sb, "reader.js");
        sb.Append("</head>\n").Append("<body class=\"markdown-body\">\n");
    }

    private static void AppendStylesheet(StringBuilder sb, string file, string? elementId = null, bool extraDisabled = false)
    {
        sb.Append("<link rel=\"stylesheet\" href=\"http://").Append(AssetsHost)
          .Append('/').Append(file).Append("?id=").Append(ResourceVersion(file)).Append('"');
        if (elementId is not null) sb.Append(" id=\"").Append(elementId).Append('"');
        if (extraDisabled) sb.Append(" disabled");
        sb.Append(" />\n");
    }

    private static void AppendScript(StringBuilder sb, string file)
    {
        sb.Append("<script src=\"http://").Append(AssetsHost)
          .Append('/').Append(file).Append("?id=").Append(ResourceVersion(file))
          .Append("\"></script>\n");
    }
}
