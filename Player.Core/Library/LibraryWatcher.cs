using Serilog;

namespace Player.Core.Library;

/// <summary>
/// 监听媒体库根目录的变化（PLAN 第 5 节）。文件事件通常成串到来，
/// 这里做去抖：最后一次变化后静默若干秒才通知上层跑一次增量扫描。
/// </summary>
public sealed class LibraryWatcher : IDisposable
{
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly object _gate = new();
    private readonly Timer _debounceTimer;

    private bool _disposed;

    public LibraryWatcher()
    {
        _debounceTimer = new Timer(_ => Fire(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>变化已平息，可以跑增量扫描了。在线程池线程上触发。</summary>
    public event EventHandler? ChangesSettled;

    public TimeSpan DebounceDelay { get; set; } = TimeSpan.FromSeconds(3);

    public bool IsWatching
    {
        get { lock (_gate) { return _watchers.Count > 0; } }
    }

    public void Start(IEnumerable<string> roots)
    {
        Stop();

        lock (_gate)
        {
            foreach (var root in roots)
            {
                if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) continue;

                try
                {
                    var watcher = new FileSystemWatcher(root)
                    {
                        IncludeSubdirectories = true,
                        InternalBufferSize = 64 * 1024,
                        NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName |
                                       NotifyFilters.LastWrite | NotifyFilters.Size
                    };

                    watcher.Created += OnFileEvent;
                    watcher.Deleted += OnFileEvent;
                    watcher.Changed += OnFileEvent;
                    watcher.Renamed += OnFileEvent;
                    watcher.Error += OnError;
                    watcher.EnableRaisingEvents = true;

                    _watchers.Add(watcher);
                    Log.Information("已监听目录：{Root}", root);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "无法监听目录：{Root}", root);
                }
            }
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            foreach (var watcher in _watchers)
            {
                try
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Created -= OnFileEvent;
                    watcher.Deleted -= OnFileEvent;
                    watcher.Changed -= OnFileEvent;
                    watcher.Renamed -= OnFileEvent;
                    watcher.Error -= OnError;
                    watcher.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "停止目录监听时出错");
                }
            }

            _watchers.Clear();
        }

        _debounceTimer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        // 目录事件没有扩展名，也要放行（新建/删除整个专辑目录）
        var isDirectoryEvent = string.IsNullOrEmpty(Path.GetExtension(e.FullPath));
        if (!isDirectoryEvent && !Audio.AudioFormats.IsSupported(e.FullPath)) return;

        Schedule();
    }

    private void OnError(object sender, ErrorEventArgs e)
    {
        // 缓冲区溢出意味着漏事件了，直接安排一次增量扫描兜底
        Log.Warning(e.GetException(), "目录监听出错，将触发一次增量扫描");
        Schedule();
    }

    private void Schedule()
    {
        if (_disposed) return;

        try { _debounceTimer.Change(DebounceDelay, Timeout.InfiniteTimeSpan); }
        catch (ObjectDisposedException) { /* 正在退出，忽略 */ }
    }

    private void Fire()
    {
        if (_disposed) return;

        try { ChangesSettled?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex) { Log.Error(ex, "处理目录变化通知时异常"); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
        _debounceTimer.Dispose();
    }
}
