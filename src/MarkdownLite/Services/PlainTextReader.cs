using System.IO;
using System.Text;

namespace MarkdownLite.Services;

/// <summary>
/// 只读方式读取文本文件，自动探测 BOM / UTF-8 / GB18030 等常见编码。
/// 全程不写回磁盘。
/// </summary>
public static class PlainTextReader
{
    /// <summary>
    /// 读取文件并解码为字符串。
    /// 探测顺序：BOM（UTF-8 / UTF-16 LE / UTF-16 BE）→ 严格 UTF-8 → GB18030 → Latin-1 兜底。
    /// </summary>
    /// <param name="path">源文件绝对路径，仅以只读方式打开。</param>
    /// <param name="encodingName">输出：探测到的编码名称。</param>
    public static string Read(string path, out string encodingName)
    {
        var bytes = ReadAllBytesReadOnly(path);
        return Decode(bytes, out encodingName);
    }

    /// <summary>以 FileShare.ReadWrite 只读打开，避免源文件被占用或修改。</summary>
    public static byte[] ReadAllBytesReadOnly(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var buffer = new byte[stream.Length];
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = stream.Read(buffer, offset, buffer.Length - offset);
            if (read <= 0) break;
            offset += read;
        }
        return buffer;
    }

    /// <summary>GB18030 编码对象缓存（见 <see cref="Decode"/> 第 3 步）。未注册时保持 null 并允许下次重试。</summary>
    private static Encoding? _gb18030;

    private static Encoding? TryGetGb18030()
    {
        if (_gb18030 is not null) return _gb18030;
        try { return _gb18030 = Encoding.GetEncoding("GB18030"); }
        catch (Exception) { return null; }   // CodePages provider 未注册等极端情况
    }

    /// <summary>解码字节流并探测编码（供测试直接调用）。</summary>
    public static string Decode(byte[] bytes, out string encodingName)
    {
        // 1. BOM
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            encodingName = "UTF-8 (BOM)";
            return new UTF8Encoding(false).GetString(bytes, 3, bytes.Length - 3);
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            encodingName = "UTF-16 LE (BOM)";
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        }
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            encodingName = "UTF-16 BE (BOM)";
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        }

        // 2. 严格 UTF-8（遇非法字节序列即失败，避免误判）
        try
        {
            var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true)
                .GetString(bytes);
            encodingName = "UTF-8";
            return text;
        }
        catch (DecoderFallbackException)
        {
            // 继续回退
        }

        // 3. GB18030（简体中文 Windows 常见遗留编码，向下兼容 GBK/GB2312）
        //    编码对象缓存下来：GetEncoding 每次都要做一次名称查找。
        //    查不到（provider 未注册等）时返回 null 并**下次重试**，与原先「每次都 try」的语义一致，
        //    避免在 provider 注册前就初始化导致永久拿不到编码。
        if (TryGetGb18030() is { } gb)
        {
            var text = gb.GetString(bytes);
            encodingName = "GB18030";
            return text;
        }

        // 4. Latin-1 兜底：永不失败，保证至少能显示内容
        encodingName = "Latin-1 (ISO-8859-1)";
        return Encoding.Latin1.GetString(bytes);
    }
}
