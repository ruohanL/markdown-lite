using MarkdownLite.Services;
using Xunit;

namespace MarkdownLite.Tests;

/// <summary>GFM 解析能力：标题/表格/任务列表/删除线/脚注/自动链接/代码块等。</summary>
public class MarkdownRenderingTests
{
    private static string Render(string markdown) =>
        MarkdownRenderer.Render(markdown, documentDirectory: null, documentHostBase: null).BodyHtml;

    [Fact]
    public void Heading_Renders_With_AutoIdentifier()
    {
        var body = Render("# Hello World\n");
        Assert.Contains("<h1 id=", body);
        Assert.Contains("Hello World", body);
    }

    [Fact]
    public void PipeTable_Renders()
    {
        var body = Render("| 名称 | 数量 |\n| ---- | ---- |\n| 苹果 | 3 |\n");
        Assert.Contains("<table>", body);
        Assert.Contains("<th", body);
        Assert.Contains("<td>苹果</td>", body);
    }

    [Fact]
    public void TaskList_Renders_Disabled_Checkbox()
    {
        var body = Render("- [x] 已完成\n- [ ] 未完成\n");
        Assert.Contains("checkbox", body);
        Assert.Contains("disabled", body);   // 复选框只能是禁用态——只读
        Assert.Contains("checked", body);
    }

    [Fact]
    public void Strikethrough_Renders()
    {
        var body = Render("这是 ~~删除线~~ 文本\n");
        Assert.Contains("<del>删除线</del>", body);
    }

    [Fact]
    public void Footnotes_Renders()
    {
        var body = Render("文本[^1]\n\n[^1]: 脚注内容\n");
        Assert.Contains("footnote", body.ToLowerInvariant());
        Assert.Contains("脚注内容", body);
    }

    [Fact]
    public void AutoLink_BareUrl_BecomesAnchor()
    {
        var body = Render("访问 https://example.com 了解更多\n");
        Assert.Contains("<a href=\"https://example.com\"", body);
    }

    [Fact]
    public void CodeFence_Renders_LanguageClass()
    {
        var body = Render("```csharp\nint x = 1;\n```\n");
        Assert.Contains("<pre", body);
        Assert.Contains("language-csharp", body);
        Assert.Contains("int x = 1;", body);
    }

    [Fact]
    public void InlineCode_Renders()
    {
        var body = Render("使用 `dotnet build` 构建\n");
        Assert.Contains("<code>dotnet build</code>", body);
    }

    [Fact]
    public void Blockquote_Renders()
    {
        var body = Render("> 引用内容\n");
        Assert.Contains("<blockquote>", body);
        Assert.Contains("引用内容", body);
    }

    [Fact]
    public void NestedList_Renders()
    {
        var body = Render("- 一级\n  - 二级\n");
        Assert.Contains("<ul>", body);
        Assert.Contains("一级", body);
        Assert.Contains("二级", body);
    }

    [Fact]
    public void Toc_ExtractsHeadingsWithIds()
    {
        var result = MarkdownRenderer.Render(
            "# 总览\n\n## 安装\n\n### 细节\n\n## 使用\n",
            documentDirectory: null, documentHostBase: null);

        var texts = result.Toc.Select(t => t.Text).ToList();
        Assert.Contains("总览", texts);
        Assert.Contains("使用", texts);
        Assert.Equal(2, result.Toc.First(t => t.Text == "使用").Level);
        Assert.All(result.Toc, t => Assert.False(string.IsNullOrEmpty(t.Id)));
    }

    [Fact]
    public void BuildDocument_Contains_Csp_And_OfflineAssets()
    {
        var html = MarkdownRenderer.BuildDocument("<p>hi</p>", "测试.md");
        Assert.Contains("Content-Security-Policy", html);
        Assert.Contains("default-src 'none'", html);
        Assert.Contains("connect-src 'none'", html);
        Assert.Contains("highlight.min.js", html);
        Assert.Contains("hljs-github.min.css", html);
        Assert.Contains("hljs-github-dark.min.css", html);
        Assert.DoesNotContain("cdn.", html); // 不引用任何 CDN
        Assert.Contains("<p>hi</p>", html);
        Assert.Contains("测试.md", html);
    }

    // ------------------------------------------------------------------
    // YAML front matter
    // ------------------------------------------------------------------

    [Fact]
    public void FrontMatter_Is_Not_Rendered()
    {
        var body = Render("---\ntitle: 我的文档\ntags: [a, b]\n---\n\n# 正文\n");
        Assert.DoesNotContain("title:", body);
        Assert.DoesNotContain("tags:", body);
        Assert.DoesNotContain("<hr", body); // 分隔线不应残留
        Assert.Contains("正文", body);
    }

    [Fact]
    public void FrontMatter_Title_Is_Extracted()
    {
        var result = MarkdownRenderer.Render(
            "---\ntitle: 我的文档\n---\n\n# 正文\n", documentDirectory: null, documentHostBase: null);
        Assert.Equal("我的文档", result.Title);
    }

    [Theory]
    [InlineData("title: \"带引号\"", "带引号")]
    [InlineData("title: '单引号'", "单引号")]
    [InlineData("Title: 大写键", "大写键")]
    public void FrontMatter_Title_Accepts_Quotes_And_Case(string line, string expected)
    {
        var result = MarkdownRenderer.Render(
            $"---\n{line}\n---\n\n正文\n", documentDirectory: null, documentHostBase: null);
        Assert.Equal(expected, result.Title);
    }

    [Fact]
    public void FrontMatter_Without_Title_Returns_Null()
    {
        var result = MarkdownRenderer.Render(
            "---\ntags: [a]\n---\n\n正文\n", documentDirectory: null, documentHostBase: null);
        Assert.Null(result.Title);
    }

    [Fact]
    public void UnclosedFrontMatter_Is_Not_Misdetected()
    {
        // 首行 --- 但无闭合分隔符：不得当作元数据，也不得吞掉正文
        var result = MarkdownRenderer.Render(
            "---\ntitle: 不该被采用\n\n# 正文\n", documentDirectory: null, documentHostBase: null);
        Assert.Null(result.Title);
        Assert.Contains("正文", result.BodyHtml);
    }

    [Fact]
    public void HorizontalRule_In_Body_Still_Renders()
    {
        var body = Render("# 标题\n\n正文\n\n---\n\n后面\n");
        Assert.Contains("<hr", body);
    }
}
