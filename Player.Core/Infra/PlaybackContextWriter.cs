using Serilog;

namespace Player.Core.Infra;

/// <summary>
/// 将播放上下文侧车写入移出 UI 线程。队列变化很密集时只保留最新快照，
/// 退出时等待单个后台写入者排空，避免万曲队列触发同步 JSON 序列化。
/// </summary>
public sealed class PlaybackContextWriter : IDisposable
{
    private readonly object _gate = new();
    private PersistedPlaybackContext? _pending;
    private Task? _worker;
    private bool _disposed;

    public void Queue(PersistedPlaybackContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        lock (_gate)
        {
            if (_disposed) return;
            _pending = context;
            if (_worker is { IsCompleted: false }) return;
            _worker = Task.Run(Drain);
        }
    }

    private void Drain()
    {
        while (true)
        {
            PersistedPlaybackContext? next;
            lock (_gate)
            {
                next = _pending;
                _pending = null;
                if (next is null)
                {
                    _worker = null;
                    return;
                }
            }

            try
            {
                PlaybackContextStore.Save(next);
            }
            catch (Exception ex)
            {
                // Save already handles normal I/O failures. An unexpected
                // exception must not strand a newer snapshot queued meanwhile.
                Log.Warning(ex, "播放上下文后台写入异常");
            }
        }
    }

    public void Dispose()
    {
        Task? worker;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            worker = _worker;
        }

        worker?.GetAwaiter().GetResult();
    }
}
