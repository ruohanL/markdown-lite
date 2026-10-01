using MarkdownLite.Services;
using Xunit;

namespace MarkdownLite.Tests;

/// <summary>相对路径解析：以当前 .md 所在目录为基准改写为本地虚拟主机 URL。</summary>
public class PathResolutionTests
{
    private const string DocDir = @"C:\docs\project";
    private const string Host = "http://mdreader.doc1.local";

    [Fact]
    public void RelativeImage_ResolvesToDocHost()
    {
        var url = MarkdownRenderer.SanitizeUrl("img/a.png", isImage: true, DocDir, Host);
        Assert.Equal(Host + "/img/a.png", url);
    }

    [Fact]
    public void DotRelativeImage_ResolvesWithinDocDir()
    {
        var url = MarkdownRenderer.SanitizeUrl("./pic.jpg", isImage: true, DocDir, Host);
        Assert.Equal(Host + "/pic.jpg", url);
    }

    [Fact]
    public void SubdirectoryParentRef_InsideDocDir_IsAllowed()
    {
        // img/../assets/x.png 仍在文档目录内 → 合法
        var url = MarkdownRenderer.SanitizeUrl("img/../assets/x.png", isImage: true, DocDir, Host);
        Assert.Equal(Host + "/assets/x.png", url);
    }

    [Fact]
    public void DirectoryTraversal_OutsideDocDir_IsBlocked()
    {
        var url = MarkdownRenderer.SanitizeUrl("../../windows/win.ini", isImage: true, DocDir, Host);
        Assert.Equal("#", url);
    }

    [Fact]
    public void AbsoluteWindowsPath_IsBlocked()
    {
        var url = MarkdownRenderer.SanitizeUrl(@"C:\temp\x.png", isImage: true, DocDir, Host);
        Assert.Equal("#", url); // 含盘符的伪相对路径被拒绝
    }

    [Fact]
    public void PercentEncodedSpaces_AreDecodedThenReEncoded()
    {
        var url = MarkdownRenderer.SanitizeUrl("my%20dir/a%20b.png", isImage: true, DocDir, Host);
        Assert.Equal(Host + "/my%20dir/a%20b.png", url);
    }

    [Fact]
    public void ChineseFilenames_AreUrlEncoded()
    {
        var url = MarkdownRenderer.SanitizeUrl("图片/说明.png", isImage: true, DocDir, Host);
        Assert.StartsWith(Host + "/", url);
        Assert.Contains(Uri.EscapeDataString("图片"), url);
        Assert.DoesNotContain("图片", url);
    }

    [Fact]
    public void FragmentOnRelativeLink_IsPreserved()
    {
        // 跨文档锚点必须保留，否则 [下一章](ch2.md#setup) 会跳到文件开头
        var url = MarkdownRenderer.SanitizeUrl("other.md#section", isImage: false, DocDir, Host);
        Assert.Equal(Host + "/other.md#section", url);
    }

    [Fact]
    public void FragmentOnRelativeLink_DoesNotEscapeDocDir()
    {
        // 带锚点的越界路径依然必须被拒绝
        var url = MarkdownRenderer.SanitizeUrl("../../windows/win.ini#x", isImage: false, DocDir, Host);
        Assert.Equal("#", url);
    }

    [Fact]
    public void RelativeWithoutDocDir_IsBlocked()
    {
        Assert.Equal("#", MarkdownRenderer.SanitizeUrl("a.png", isImage: true, null, null));
    }

    [Fact]
    public void Render_RewritesImageSrc_InBody()
    {
        var result = MarkdownRenderer.Render("![图](images/arch.png)\n", DocDir, Host);
        Assert.Contains("src=\"" + Host + "/images/arch.png\"", result.BodyHtml);
    }

    [Fact]
    public void Render_RewritesRelativeMarkdownLink_ToDocHost()
    {
        // 相对 .md 链接改写到文档主机；宿主层拦截该导航并在应用内打开
        var result = MarkdownRenderer.Render("[下一章](ch2.md)\n", DocDir, Host);
        Assert.Contains("href=\"" + Host + "/ch2.md\"", result.BodyHtml);
    }
}
