namespace Player.Core.Audio.Spectrum;

/// <summary>
/// Single-producer/single-consumer float ring. The audio callback is the only writer and the
/// analyzer thread is the only reader. A full ring drops new samples instead of touching the
/// consumer cursor, so neither side needs a lock.
/// </summary>
internal sealed class SpscFloatRing
{
    private readonly float[] _buffer;
    private readonly int _mask;

    private int _readSequence;
    private int _writeSequence;
    private long _droppedSamples;

    public SpscFloatRing(int capacity)
    {
        if (capacity < 2 || (capacity & (capacity - 1)) != 0)
            throw new ArgumentOutOfRangeException(nameof(capacity), "Capacity must be a power of two.");

        _buffer = new float[capacity];
        _mask = capacity - 1;
    }

    public int Capacity => _buffer.Length;

    public int Available
    {
        get
        {
            var write = Volatile.Read(ref _writeSequence);
            var read = Volatile.Read(ref _readSequence);
            return unchecked(write - read);
        }
    }

    public long DroppedSamples => Interlocked.Read(ref _droppedSamples);

    public int Write(ReadOnlySpan<float> source)
    {
        var write = _writeSequence;
        var read = Volatile.Read(ref _readSequence);
        var free = _buffer.Length - unchecked(write - read);
        var count = Math.Min(source.Length, free);

        CopyIntoBuffer(source[..count], write);
        Volatile.Write(ref _writeSequence, unchecked(write + count));

        if (count != source.Length)
            _droppedSamples += source.Length - count;

        return count;
    }

    public unsafe int Write(IntPtr source, int sampleCount)
    {
        if (source == IntPtr.Zero || sampleCount <= 0) return 0;

        var write = _writeSequence;
        var read = Volatile.Read(ref _readSequence);
        var free = _buffer.Length - unchecked(write - read);
        var count = Math.Min(sampleCount, free);
        var input = (float*)source;
        var target = write & _mask;
        var first = Math.Min(count, _buffer.Length - target);

        for (var i = 0; i < first; i++)
            _buffer[target + i] = input[i];
        for (var i = first; i < count; i++)
            _buffer[i - first] = input[i];

        Volatile.Write(ref _writeSequence, unchecked(write + count));

        if (count != sampleCount)
            _droppedSamples += sampleCount - count;

        return count;
    }

    public int Read(Span<float> destination)
    {
        var read = _readSequence;
        var write = Volatile.Read(ref _writeSequence);
        var count = Math.Min(destination.Length, unchecked(write - read));
        var source = read & _mask;
        var first = Math.Min(count, _buffer.Length - source);

        _buffer.AsSpan(source, first).CopyTo(destination);
        if (first != count)
            _buffer.AsSpan(0, count - first).CopyTo(destination[first..]);

        Volatile.Write(ref _readSequence, unchecked(read + count));
        return count;
    }

    /// <summary>Only call while the producer is detached and the consumer is stopped.</summary>
    public void Reset()
    {
        _readSequence = 0;
        _writeSequence = 0;
        _droppedSamples = 0;
        Array.Clear(_buffer);
    }

    private void CopyIntoBuffer(ReadOnlySpan<float> source, int write)
    {
        var target = write & _mask;
        var first = Math.Min(source.Length, _buffer.Length - target);
        source[..first].CopyTo(_buffer.AsSpan(target));
        if (first != source.Length)
            source[first..].CopyTo(_buffer);
    }
}
