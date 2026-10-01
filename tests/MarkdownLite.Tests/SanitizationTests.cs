using MarkdownLite.Services;
using Xunit;

namespace MarkdownLite.Tests;

/// <summary>XSS / 危险协议 / 原始 HTML 过滤。</summary>
public class SanitizationTests
{
    private static string Render(string markdown, string? docDir = null, string? hostBase = null) =>
        MarkdownRenderer.Render(markdown, docDir, hostBase).BodyHtml;

    [Fact]
    public void RawHtmlBlock_Script_Is_Stripped()
    {
        var body = Render("<script>alert('xss')</script>\n");
        Assert.DoesNotContain("<script", body);
        Assert.DoesNotContain("alert(", body);
    }

    [Fact]
    public void RawHtmlInline_Is_Stripped()
    {
        var body = Render("正常文字 <img src=x onerror=alert(1)> 结尾\n");
        Assert.DoesNotContain("onerror", body);
        Assert.DoesNotContain("<img", body);
        Assert.Contains("正常文字", body);
        Assert.Contains("结尾", body);
    }

    [Fact]
    public void RawHtmlDivWithEventHandler_Is_Stripped()
    {
        // 位于段落中间的原始 HTML 会被当作内联节点剥离，文字内容保留
        var body = Render("触发 <div onclick=\"steal()\">内联事件</div> 结束\n");
        Assert.DoesNotContain("onclick", body);
        Assert.DoesNotContain("<div", body);
        Assert.Contains("内联事件", body);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JavaScript:alert(1)")]   // 大小写混淆
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("file:///C:/Windows/win.ini")]
    [InlineData("about:blank")]
    [InlineData("blob:https://evil/1234")]
    public void DangerousLinkSchemes_Are_Neutralized(string dangerous)
    {
        var url = MarkdownRenderer.SanitizeUrl(dangerous, isImage: false, docDir: null, docHostBase: null);
        Assert.Equal("#", url);
    }

    [Fact]
    public void MarkdownLinkWithJavascriptHref_Neutralized_In_Render()
    {
        var body = Render("[点我](javascript:alert(document.cookie))\n");
        Assert.DoesNotContain("javascript:", body);
        Assert.Contains("href=\"#\"", body);
    }

    [Fact]
    public void ExternalLinkWithAnchor_InRenderedBody_KeepsFragment()
    {
        var body = Render("[点我](https://example.com/p#sec)\n");
        Assert.Contains("href=\"https://example.com/p#sec\"", body);
    }

    [Theory]
    [InlineData("http://example.com/a.png")]
    [InlineData("https://example.com/x")]
    [InlineData("#section-anchor")]
    [InlineData("mailto:someone@example.com")]
    public void SafeUrls_PassThrough(string safe)
    {
        var url = MarkdownRenderer.SanitizeUrl(safe, isImage: false, docDir: null, docHostBase: null);
        Assert.Equal(safe, url);
    }

    [Theory]
    [InlineData("https://example.com/page#section")]
    [InlineData("http://example.com/a/b?x=1#frag")]
    [InlineData("mailto:a@b.com#note")]
    public void ExternalUrlFragment_IsPreserved(string url)
    {
        // 外链的分节锚点必须保留，否则点开后会跳到页首
        Assert.Equal(url, MarkdownRenderer.SanitizeUrl(url, isImage: false, docDir: null, docHostBase: null));
    }

    [Fact]
    public void DangerousScheme_WithFragment_IsStillNeutralized()
    {
        // 片段保留不得成为绕过协议白名单的缺口
        Assert.Equal("#", MarkdownRenderer.SanitizeUrl(
            "javascript:alert(1)#x", isImage: false, docDir: null, docHostBase: null));
        Assert.Equal("#", MarkdownRenderer.SanitizeUrl(
            "data:text/html,<script>1</script>#x", isImage: false, docDir: null, docHostBase: null));
    }

    [Fact]
    public void DataUri_Allowed_ForImages_ButNotForLinks()
    {
        const string png = "data:image/png;base64,iVBORw0KGgo=";
        Assert.Equal(png, MarkdownRenderer.SanitizeUrl(png, isImage: true, docDir: null, docHostBase: null));
        Assert.Equal("#", MarkdownRenderer.SanitizeUrl(png, isImage: false, docDir: null, docHostBase: null));
        Assert.Equal("#", MarkdownRenderer.SanitizeUrl(
            "data:text/html,<script>1</script>", isImage: true, docDir: null, docHostBase: null));
    }

    [Fact]
    public void EscapedHtmlInsideCode_Is_PreservedAsLiteralText()
    {
        // 代码块里的伪 XSS 样本必须转义为纯文本，绝不成为可执行标签
        var body = Render("```\n<script>alert(1)</script>\n```\n");
        Assert.DoesNotContain("<script>", body);
        Assert.Contains("&lt;script&gt;", body);
    }

    [Fact]
    public void ImageWithJavascriptSrc_Neutralized()
    {
        var body = Render("![x](javascript:alert(1))\n");
        Assert.DoesNotContain("javascript:", body);
    }
}
