namespace Player.Core.Library;

/// <summary>
/// Coalesces directory-change notifications and transfers single-worker ownership
/// without a gap between "no work" and "worker released".  All state transitions
/// share one short lock so a request can never be stranded at worker shutdown.
/// </summary>
internal sealed class LibraryRescanQueue
{
    private readonly object _gate = new();
    private bool _pending;
    private bool _workerScheduled;
    private bool _closed;

    /// <summary>Records a change and reports whether the caller must start the worker.</summary>
    internal bool Request(out bool shouldSchedule)
    {
        lock (_gate)
        {
            shouldSchedule = false;
            if (_closed) return false;

            _pending = true;
            if (!_workerScheduled)
            {
                _workerScheduled = true;
                shouldSchedule = true;
            }

            return true;
        }
    }

    /// <summary>
    /// Claims one coalesced request.  When none remains, worker ownership is
    /// released under the same lock used by producers; a racing request will
    /// therefore either be claimed here or schedule a replacement worker.
    /// </summary>
    internal bool TryTake()
    {
        lock (_gate)
        {
            if (_closed)
            {
                _pending = false;
                _workerScheduled = false;
                return false;
            }

            if (_pending)
            {
                _pending = false;
                return true;
            }

            _workerScheduled = false;
            return false;
        }
    }

    /// <summary>Stops accepting work and drops any pending shutdown-time notification.</summary>
    internal void Close()
    {
        lock (_gate)
        {
            _closed = true;
            _pending = false;
            _workerScheduled = false;
        }
    }
}
