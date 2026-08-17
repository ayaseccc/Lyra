namespace Player.Core.Audio;

[Flags]
internal enum AsioNotificationFlags : uint
{
    None = 0,
    Rate = 1,
    Reset = 2
}

/// <summary>
/// ASIO 驱动通知的无分配邮箱。高 32 位是注册代际，低 32 位是待处理位。
/// 驱动回调只执行 CAS；所有读取 BASS 属性、日志和事件派发都在 Poll 线程完成。
/// </summary>
internal sealed class AsioNotificationMailbox
{
    private long _state;
    private int _nextGeneration;

    internal int BeginSession()
    {
        var generation = NextGeneration();
        Volatile.Write(ref _state, Pack(generation, AsioNotificationFlags.None));
        return generation;
    }

    internal int EndSession() => BeginSession();

    internal int CurrentGeneration => UnpackGeneration(Volatile.Read(ref _state));

    internal void Post(int generation, AsioNotificationFlags flags)
    {
        if (flags == AsioNotificationFlags.None) return;

        while (true)
        {
            var observed = Volatile.Read(ref _state);
            if (UnpackGeneration(observed) != generation) return;

            var current = UnpackFlags(observed);
            var nextFlags = current | flags;
            if (nextFlags == current) return;

            var updated = Pack(generation, nextFlags);
            if (Interlocked.CompareExchange(ref _state, updated, observed) == observed)
                return;
        }
    }

    internal AsioNotificationFlags Drain(int generation)
    {
        while (true)
        {
            var observed = Volatile.Read(ref _state);
            if (UnpackGeneration(observed) != generation) return AsioNotificationFlags.None;

            var flags = UnpackFlags(observed);
            if (flags == AsioNotificationFlags.None) return flags;

            var cleared = Pack(generation, AsioNotificationFlags.None);
            if (Interlocked.CompareExchange(ref _state, cleared, observed) == observed)
                return flags;
        }
    }

    private int NextGeneration()
    {
        var generation = Interlocked.Increment(ref _nextGeneration);
        return generation == 0 ? Interlocked.Increment(ref _nextGeneration) : generation;
    }

    private static long Pack(int generation, AsioNotificationFlags flags) =>
        ((long)(uint)generation << 32) | (uint)flags;

    private static int UnpackGeneration(long state) => (int)(state >> 32);

    private static AsioNotificationFlags UnpackFlags(long state) =>
        (AsioNotificationFlags)(uint)state;
}
