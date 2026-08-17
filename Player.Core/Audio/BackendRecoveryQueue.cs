namespace Player.Core.Audio;

[Flags]
internal enum BackendRecoveryKind : uint
{
    None = 0,
    FormatChanged = 1,
    DeviceLost = 2
}

internal readonly record struct BackendRecoveryRequest(
    object Backend,
    int Generation,
    BackendRecoveryKind Kind,
    string Reason);

/// <summary>
/// 合并后端异常通知，并保证同一批通知只唤起一个控制线程工作项。
/// 生产者不会执行恢复；它只在短锁内记录身份、代际和最高优先级原因。
/// </summary>
internal sealed class BackendRecoveryQueue
{
    private readonly object _gate = new();
    private object? _activeBackend;
    private int _generation;
    private BackendRecoveryKind _pendingKind;
    private object? _pendingBackend;
    private int _pendingGeneration;
    private string? _pendingReason;
    private bool _workerScheduled;

    internal int Activate(object backend)
    {
        lock (_gate)
        {
            _activeBackend = backend;
            _generation = NextGeneration(_generation);
            ClearPendingLocked();
            return _generation;
        }
    }

    internal int AdvanceSession(object backend)
    {
        lock (_gate)
        {
            if (!ReferenceEquals(_activeBackend, backend))
                _activeBackend = backend;

            _generation = NextGeneration(_generation);
            ClearPendingLocked();
            return _generation;
        }
    }

    internal int CurrentGeneration
    {
        get { lock (_gate) return _generation; }
    }

    internal bool TryEnqueue(object? sender, BackendRecoveryKind kind, string reason,
        out bool shouldSchedule)
    {
        shouldSchedule = false;
        if (sender is null || kind == BackendRecoveryKind.None) return false;

        lock (_gate)
        {
            if (_activeBackend is null || !ReferenceEquals(sender, _activeBackend))
                return false;

            if (_pendingKind == BackendRecoveryKind.None ||
                !ReferenceEquals(_pendingBackend, sender) ||
                _pendingGeneration != _generation)
            {
                _pendingBackend = sender;
                _pendingGeneration = _generation;
                _pendingKind = kind;
                _pendingReason = reason;
            }
            else
            {
                _pendingKind = Merge(_pendingKind, kind);
                if ((kind & BackendRecoveryKind.DeviceLost) != 0 ||
                    _pendingReason is null)
                    _pendingReason = reason;
            }

            if (!_workerScheduled)
            {
                _workerScheduled = true;
                shouldSchedule = true;
            }

            return true;
        }
    }

    internal bool TryTake(out BackendRecoveryRequest request)
    {
        lock (_gate)
        {
            if (_pendingKind == BackendRecoveryKind.None || _pendingBackend is null)
            {
                _workerScheduled = false;
                request = default;
                return false;
            }

            request = new BackendRecoveryRequest(
                _pendingBackend,
                _pendingGeneration,
                _pendingKind,
                _pendingReason ?? string.Empty);
            ClearPendingLocked();
            return true;
        }
    }

    internal bool IsCurrent(in BackendRecoveryRequest request)
    {
        lock (_gate)
        {
            return ReferenceEquals(_activeBackend, request.Backend) &&
                   _generation == request.Generation;
        }
    }

    internal void Cancel()
    {
        lock (_gate) ClearPendingLocked();
    }

    private void ClearPendingLocked()
    {
        _pendingKind = BackendRecoveryKind.None;
        _pendingBackend = null;
        _pendingGeneration = 0;
        _pendingReason = null;
    }

    private static BackendRecoveryKind Merge(BackendRecoveryKind current,
        BackendRecoveryKind incoming)
    {
        if ((current & BackendRecoveryKind.DeviceLost) != 0 ||
            (incoming & BackendRecoveryKind.DeviceLost) != 0)
            return BackendRecoveryKind.DeviceLost;

        return current | incoming;
    }

    private static int NextGeneration(int current)
    {
        var next = unchecked(current + 1);
        return next == 0 ? 1 : next;
    }
}
