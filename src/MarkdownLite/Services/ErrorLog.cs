using System.IO;

namespace MarkdownLite.Services;

/// <summary>
/// 极简错误日志：把关键路径上的异常落一份现场到
/// <c>%LOCALAPPDATA%\MarkdownLite\error.log</c>。
///
/// 应用原本只在弹窗里一次性显示异常，用户回传问题时无从下手；
/// 有了滚动日志，排障至少有个入口。日志只写异常与方法名，不记录文档内容，
/// 超过 <see cref="MaxBytes"/> 自动轮换，不会无限增长。
///
/// 另提供 <see cref="Enabled"/>（由环境变量 <c>MDREADER_DIAG=1</c> 打开）控制的
/// 详细诊断，用于 GUI 回归时观察页面消息桥；默认关闭，不影响正常使用。
/// </summary>
internal static class ErrorLog
{
    private const long MaxBytes = 256 * 1024;
    private static readonly object Gate = new();

    private static string AppDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MdReader");

    public static string FilePath => Path.Combine(AppDirectory, "error.log");

    /// <summary>是否输出详细诊断（GUI 回归用）。可由环境变量 MDREADER_DIAG=1 或命令行 --diag-log 打开。</summary>
    public static bool Enabled { get; set; } =
        Environment.GetEnvironmentVariable("MDREADER_DIAG") == "1";

    public static void Write(string context, Exception ex) =>
        Append($"{context}\n{ex}");

    /// <summary>记录一条不带异常的现场信息（如浏览器进程失败事件）。</summary>
    public static void Write(string message) => Append(message);

    public static void Info(string message)
    {
        if (Enabled) Append(message);
    }

    private static void Append(string text)
    {
        try
        {
            lock (Gate)
            {
                System.IO.Directory.CreateDirectory(AppDirectory);
                if (File.Exists(FilePath) && new FileInfo(FilePath).Length > MaxBytes)
                    File.Delete(FilePath);
                File.AppendAllText(FilePath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}\n\n");
            }
        }
        catch (Exception)
        {
            // 日志本身失败绝不影响阅读
        }
    }
}
