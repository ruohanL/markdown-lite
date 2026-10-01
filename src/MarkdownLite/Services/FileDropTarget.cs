using System.Collections.Generic;
using System.Runtime.InteropServices;
using COMIDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace MarkdownLite.Services;

/// <summary>
/// 原生 OLE 拖放目标。用于接管 WebView2 子窗口的文件拖放——
/// WebView2 是原生子窗口（airspace），文件拖到它上方时 OLE 消息被 Chromium 子窗口吞掉，
/// WPF 窗口的 Drop 事件收不到。这里在 WebView2 的宿主/子窗口上注册自定义 IDropTarget，
/// 取出被拖入的文件路径。
///
/// 文件路径提取用 System.Windows.Forms.DataObject 包装传入的 COM IDataObject，
/// 由框架安全处理 CF_HDROP 内存，避免手写 STGMEDIUM/ReleaseStgMedium 造成访问违例。
/// </summary>
public sealed class FileDropTarget : IDropTarget
{
    private readonly Action<string[]> _onFiles;
    private bool _oleInited;

    public FileDropTarget(Action<string[]> onFiles) => _onFiles = onFiles;

    /// <summary>在 rootHwnd 及其所有子窗口上安装本拖放目标（覆盖 WebView2 自身的）。</summary>
    public void Attach(IntPtr rootHwnd)
    {
        if (rootHwnd == IntPtr.Zero) return;

        if (!_oleInited)
        {
            // RegisterDragDrop 要求线程已 OleInitialize；WPF UI 线程通常已初始化，
            // 这里再调一次兜底（重复调用返回 S_FALSE，无害）。
            OleInitialize(IntPtr.Zero);
            _oleInited = true;
        }

        var hwnds = new List<IntPtr> { rootHwnd };
        EnumChildWindows(rootHwnd, (h, _) => { hwnds.Add(h); return true; }, IntPtr.Zero);

        foreach (var hwnd in hwnds)
        {
            RevokeDragDrop(hwnd); // 移除 WebView2/系统原目标，失败忽略
            RegisterDragDrop(hwnd, this);
        }
    }

    int IDropTarget.DragEnter(COMIDataObject dataObj, int grfKeyState, POINTL pt, ref int pdwEffect)
    {
        try { pdwEffect = TryGetFiles(dataObj) is { Length: > 0 } ? DropEffect.Copy : DropEffect.None; }
        catch (Exception) { pdwEffect = DropEffect.None; }
        return 0;
    }

    int IDropTarget.DragOver(int grfKeyState, POINTL pt, ref int pdwEffect)
    {
        pdwEffect = DropEffect.Copy;
        return 0;
    }

    int IDropTarget.DragLeave() => 0;

    int IDropTarget.Drop(COMIDataObject dataObj, int grfKeyState, POINTL pt, ref int pdwEffect)
    {
        pdwEffect = DropEffect.None;
        try
        {
            var paths = TryGetFiles(dataObj);
            if (paths is { Length: > 0 })
            {
                pdwEffect = DropEffect.Copy;
                _onFiles(paths);
            }
        }
        catch (Exception)
        {
            // 拖放处理异常绝不让其穿过 COM 边界
        }
        return 0;
    }

    /// <summary>从拖放数据对象取文件路径；失败/无文件返回 null。</summary>
    private static string[]? TryGetFiles(COMIDataObject dataObj)
    {
        try
        {
            // WinForms DataObject 能包装外部 COM IDataObject 并正确处理 CF_HDROP
            var wrapper = new System.Windows.Forms.DataObject(dataObj);
            if (wrapper.GetData(System.Windows.Forms.DataFormats.FileDrop) is string[] files
                && files.Length > 0)
            {
                return files;
            }
        }
        catch (Exception)
        {
            // 数据格式异常时忽略，不影响拖放循环
        }
        return null;
    }

    // ---- 常量 ----

    private static class DropEffect
    {
        public const int None = 0;
        public const int Copy = 1;
    }

    // ---- Win32 ----

    // Win32 POINTL = 两个 32 位 LONG（共 8 字节）。必须用 int，
    // 若用 long(Int64) 会使结构体尺寸翻倍，OLE 经原生 vtable 传参时栈错位 → AccessViolation。
    [StructLayout(LayoutKind.Sequential)]
    public struct POINTL { public int x; public int y; }

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc cb, IntPtr lParam);

    [DllImport("ole32.dll")]
    private static extern int OleInitialize(IntPtr reserved);

    [DllImport("ole32.dll")]
    private static extern int RegisterDragDrop(IntPtr hwnd, IDropTarget target);

    [DllImport("ole32.dll")]
    private static extern int RevokeDragDrop(IntPtr hwnd);
}

/// <summary>COM IDropTarget 定义（原生 interop 需要，.NET 未内置公开版本）。</summary>
[ComImport]
[Guid("00000122-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IDropTarget
{
    [PreserveSig]
    int DragEnter(COMIDataObject dataObj, int grfKeyState, FileDropTarget.POINTL pt, ref int pdwEffect);

    [PreserveSig]
    int DragOver(int grfKeyState, FileDropTarget.POINTL pt, ref int pdwEffect);

    [PreserveSig]
    int DragLeave();

    [PreserveSig]
    int Drop(COMIDataObject dataObj, int grfKeyState, FileDropTarget.POINTL pt, ref int pdwEffect);
}
