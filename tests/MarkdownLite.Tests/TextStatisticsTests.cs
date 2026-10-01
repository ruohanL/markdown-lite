using MarkdownLite.Services;
using Xunit;

namespace MarkdownLite.Tests;

/// <summary>状态栏字数口径：中日韩按字、西文按词。</summary>
public class TextStatisticsTests
{
    [Theory]
    [InlineData("", 0)]
    [InlineData("   \n\t ", 0)]
    [InlineData("你好世界", 4)]
    [InlineData("hello", 1)]
    [InlineData("hello world", 2)]
    [InlineData("hello, world!", 2)]
    [InlineData("hello   world", 2)]
    [InlineData("テスト", 3)]
    [InlineData("2026 年 12 月", 4)]
    public void CountWords_Ideographs_ByChar_Latin_ByWord(string text, int expected)
    {
        Assert.Equal(expected, TextStatistics.CountWords(text));
    }

    [Fact]
    public void CountWords_Mixed_Chinese_And_English()
    {
        // 这是中文(4 字) + mixed + content + 混排(2 字) = 8
        Assert.Equal(8, TextStatistics.CountWords("这是中文 mixed content 混排"));
    }

    [Fact]
    public void CountWords_Ignores_Punctuation_And_Markdown_Syntax()
    {
        // 标题(2) + 粗体(2) + 与(1) + code(1 词) = 6；# * ` 等符号不计
        Assert.Equal(6, TextStatistics.CountWords("# 标题\n\n**粗体** 与 `code`\n"));
    }

    [Fact]
    public void CountWords_Splits_Words_Across_Newlines()
    {
        Assert.Equal(3, TextStatistics.CountWords("alpha\nbeta\ngamma"));
    }
}
