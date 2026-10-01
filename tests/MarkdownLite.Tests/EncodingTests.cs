using System.Text;
using MarkdownLite.Services;
using Xunit;

namespace MarkdownLite.Tests;

/// <summary>编码探测：BOM / UTF-8 / UTF-16 / GB18030。</summary>
public class EncodingTests
{
    static EncodingTests()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    [Fact]
    public void Utf8WithBom_Is_DetectedAndStripped()
    {
        var bytes = new List<byte> { 0xEF, 0xBB, 0xBF };
        bytes.AddRange(Encoding.UTF8.GetBytes("# 标题\n正文 hello"));
        var text = PlainTextReader.Decode(bytes.ToArray(), out var name);
        Assert.Equal("UTF-8 (BOM)", name);
        // 必须用精确相等（xunit 对 string 为 Ordinal 比较）；
        // DoesNotContain/StartsWith 走 CurrentCulture 比较，BOM 属零宽可忽略字符会误判
        Assert.Equal("# 标题\n正文 hello", text);
    }

    [Fact]
    public void Utf8WithoutBom_Is_Detected()
    {
        var bytes = Encoding.UTF8.GetBytes("普通 UTF-8 文本，含中文与 emoji 🎉");
        var text = PlainTextReader.Decode(bytes, out var name);
        Assert.Equal("UTF-8", name);
        Assert.Contains("中文", text);
    }

    [Fact]
    public void Utf16Le_WithBom_Is_Detected()
    {
        var bytes = new List<byte> { 0xFF, 0xFE };
        bytes.AddRange(Encoding.Unicode.GetBytes("UTF-16 LE 测试"));
        var text = PlainTextReader.Decode(bytes.ToArray(), out var name);
        Assert.Equal("UTF-16 LE (BOM)", name);
        Assert.Equal("UTF-16 LE 测试", text);
    }

    [Fact]
    public void Utf16Be_WithBom_Is_Detected()
    {
        var bytes = new List<byte> { 0xFE, 0xFF };
        bytes.AddRange(Encoding.BigEndianUnicode.GetBytes("UTF-16 BE 测试"));
        var text = PlainTextReader.Decode(bytes.ToArray(), out var name);
        Assert.Equal("UTF-16 BE (BOM)", name);
        Assert.Equal("UTF-16 BE 测试", text);
    }

    [Fact]
    public void InvalidUtf8_FallsBackToGb18030()
    {
        // GB18030 编码的中文不是合法 UTF-8 序列，应回退解析
        var bytes = Encoding.GetEncoding("GB18030").GetBytes("这是简体中文旧编码文档");
        var text = PlainTextReader.Decode(bytes, out var name);
        Assert.Equal("GB18030", name);
        Assert.Equal("这是简体中文旧编码文档", text);
    }

    [Fact]
    public void EmptyInput_Is_Safe()
    {
        var text = PlainTextReader.Decode(Array.Empty<byte>(), out var name);
        Assert.Equal("UTF-8", name);
        Assert.Equal(string.Empty, text);
    }
}
