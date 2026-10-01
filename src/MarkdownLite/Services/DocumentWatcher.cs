using System.IO;

namespace MarkdownLite.Services;

/// <summary>
/// 监听当前文档，在磁盘上被外部修改 / 替换时回调（用于自动重载）。
///
/// 只读定位不变：只订阅变更通知，不打开、不写入、不锁定源文件。
/// 事件来自线程池线程，因此做了防抖（编辑器保存往往触发多次 Changed），
/// 回调由调用方自行切回 UI 线程。
/// </summary>
public sealed class DocumentWatcher : IDisposable
{
    private const int DebounceMs = 350;

    private readonly Action _onChanged;
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private bool _disposed;

    public DocumentWatcher(Action onChanged) => _onChanged = onChanged;

    /// <summary>开始监听指定文件（重复调用会替换上一个目标）。</summary>
    public void Watch(string filePath)
    {
        Stop();
        if (_disposed) return;

        try
        {
            var directory = Path.GetDirectoryName(filePath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return;

            _watcher = new FileSystemWatcher(directory, Path.GetFileName(filePath))
            {
                NotifyFilter = NotifyFilters.LastWrite
                               | NotifyFilters.Size
                               | NotifyFilters.FileName
                               | NotifyFilters.CreationTime,
            };
            _watcher.Changed += OnChanged;
            _watcher.Created += OnChanged;
            _watcher.Renamed += OnChanged;
            _watcher.Deleted += OnChanged;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception)
        {
            // 目录无权限或已消失：自动重载降级为不可用，不影响阅读
            Stop();
        }
    }

    /// <summary>停止监听（切换文件或关闭自动重载时调用）。</summary>
    public void Stop()
    {
        if (_watcher is not null)
        {
            try
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Changed -= OnChanged;
                _watcher.Created -= OnChanged;
                _watcher.Renamed -= OnChanged;
                _watcher.Deleted -= OnChanged;
                _watcher.Dispose();
            }
            catch (Exception)
            {
                // 忽略释放异常
            }
            _watcher = null;
        }

        _debounce?.Dispose();
        _debounce = null;
    }

    private void OnChanged(object sender, FileSystemEventArgs e)
    {
        _debounce?.Dispose();
        _debounce = new Timer(_ => _onChanged(), null, DebounceMs, Timeout.Infinite);
    }

    public void Dispose()
    {
        _disposed = true;
        Stop();
    }
}
