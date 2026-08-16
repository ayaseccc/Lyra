namespace Player.Core.Audio.Spectrum;

/// <summary>
/// Real-time side of the spectrum pipeline. It only copies stereo interleaved float PCM into
/// the preallocated ring. No allocation, lock, logging, FFT, or channel downmix is allowed here.
/// </summary>
internal sealed class SpectrumPcmTap
{
    private readonly SpscFloatRing _ring;
    // Even = stopped, odd = active. Every Start/Stop transition advances the epoch so a callback
    // paused before Stop cannot become valid again after the next Start (active-bit ABA).
    private long _sessionEpoch;
    private int _callbacksInFlight;

    public SpectrumPcmTap(SpscFloatRing ring)
    {
        _ring = ring;
    }

    public bool IsActive => (Volatile.Read(ref _sessionEpoch) & 1) != 0;

    public void Start()
    {
        while (true)
        {
            var epoch = Volatile.Read(ref _sessionEpoch);
            if ((epoch & 1) != 0) return;
            if (Interlocked.CompareExchange(ref _sessionEpoch, unchecked(epoch + 1), epoch) == epoch)
                return;
        }
    }

    public void Stop()
    {
        while (true)
        {
            var epoch = Volatile.Read(ref _sessionEpoch);
            if ((epoch & 1) == 0) break;
            if (Interlocked.CompareExchange(ref _sessionEpoch, unchecked(epoch + 1), epoch) == epoch)
                break;
        }

        // Control thread only. Callbacks already counted here are drained. A callback that read
        // the old epoch but has not incremented yet will fail the epoch comparison before writing.
        var spinner = new SpinWait();
        while (Volatile.Read(ref _callbacksInFlight) != 0)
            spinner.SpinOnce();
    }

    public int CopyInterleaved(ReadOnlySpan<float> samples)
    {
        var epoch = Volatile.Read(ref _sessionEpoch);
        return CopyInterleaved(samples, epoch);
    }

    internal long SessionEpoch => Volatile.Read(ref _sessionEpoch);

    internal int CopyInterleaved(ReadOnlySpan<float> samples, long capturedEpoch)
    {
        if ((capturedEpoch & 1) == 0) return 0;
        Interlocked.Increment(ref _callbacksInFlight);
        try
        {
            if (capturedEpoch != Volatile.Read(ref _sessionEpoch)) return 0;
            var stereoSamples = samples.Length & ~1;
            return _ring.Write(samples[..stereoSamples]);
        }
        finally
        {
            Interlocked.Decrement(ref _callbacksInFlight);
        }
    }

    public int CopyInterleaved(IntPtr buffer, int lengthBytes)
    {
        var epoch = Volatile.Read(ref _sessionEpoch);
        return CopyInterleaved(buffer, lengthBytes, epoch);
    }

    internal int CopyInterleaved(IntPtr buffer, int lengthBytes, long capturedEpoch)
    {
        if ((capturedEpoch & 1) == 0 || lengthBytes < sizeof(float) * 2) return 0;
        Interlocked.Increment(ref _callbacksInFlight);
        try
        {
            if (capturedEpoch != Volatile.Read(ref _sessionEpoch)) return 0;
            var stereoSamples = (lengthBytes / sizeof(float)) & ~1;
            return _ring.Write(buffer, stereoSamples);
        }
        finally
        {
            Interlocked.Decrement(ref _callbacksInFlight);
        }
    }
}
