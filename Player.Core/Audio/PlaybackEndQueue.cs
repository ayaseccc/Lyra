using System.Collections.Concurrent;

namespace Player.Core.Audio;

internal enum PlaybackEndKind
{
    Ended,
    Transitioned
}

internal readonly record struct PlaybackEndCompletion(
    int IdentityRevision,
    int ControlRevision,
    int Channel,
    TrackInfo Track,
    PlaybackEndKind Kind,
    string Path);

/// <summary>FIFO mailbox with single-worker scheduling for END sync notifications.</summary>
internal sealed class PlaybackEndQueue
{
    private readonly ConcurrentQueue<PlaybackEndCompletion> _queue = new();
    private int _workerScheduled;

    internal void Enqueue(in PlaybackEndCompletion completion, out bool shouldSchedule)
    {
        _queue.Enqueue(completion);
        shouldSchedule = Interlocked.CompareExchange(ref _workerScheduled, 1, 0) == 0;
    }

    internal bool TryTake(out PlaybackEndCompletion completion) =>
        _queue.TryDequeue(out completion);

    /// <summary>Discard notifications during engine shutdown without retaining track metadata.</summary>
    internal void Clear()
    {
        while (_queue.TryDequeue(out _)) { }
        Volatile.Write(ref _workerScheduled, 0);
    }

    /// <summary>
    /// Releases worker ownership after the queue looks empty. If a producer raced
    /// with the release, the current worker reclaims ownership and keeps draining;
    /// otherwise it exits (or lets the producer-scheduled worker take over).
    /// </summary>
    internal bool TryRetainWorker()
    {
        Volatile.Write(ref _workerScheduled, 0);
        if (_queue.IsEmpty) return false;
        return Interlocked.CompareExchange(ref _workerScheduled, 1, 0) == 0;
    }
}
