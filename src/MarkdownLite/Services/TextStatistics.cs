namespace MarkdownLite.Services;

/// <summary>
/// 字数统计：中日韩表意文字按字符计数（一字算一个），西文与数字按连续单词计数，
/// 空白与标点不计入。与中文写作工具的「字数」口径一致——
/// 单纯数非空白字符会把 "hello world" 算成 11 个「字」，不符合直觉。
/// </summary>
public static class TextStatistics
{
    public static int CountWords(string text)
    {
        int count = 0;
        bool inWord = false;

        foreach (var ch in text)
        {
            if (IsIdeograph(ch))
            {
                count++;
                inWord = false;
                continue;
            }

            if (char.IsLetterOrDigit(ch))
            {
                if (!inWord)
                {
                    count++;
                    inWord = true;
                }
                continue;
            }

            inWord = false; // 空白或标点：结束当前单词
        }

        return count;
    }

    /// <summary>是否属于按单字计数的表意文字（CJK、假名、谚文）。</summary>
    private static bool IsIdeograph(char ch) =>
        (ch >= 0x4E00 && ch <= 0x9FFF) ||   // CJK 统一表意文字
        (ch >= 0x3400 && ch <= 0x4DBF) ||   // 扩展 A
        (ch >= 0xF900 && ch <= 0xFAFF) ||   // 兼容表意文字
        (ch >= 0x3040 && ch <= 0x30FF) ||   // 平假名 / 片假名
        (ch >= 0xAC00 && ch <= 0xD7AF);     // 谚文音节
}
