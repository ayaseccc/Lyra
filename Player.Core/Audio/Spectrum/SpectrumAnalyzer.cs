using System.Diagnostics;

namespace Player.Core.Audio.Spectrum;

/// <summary>
/// Non-real-time spectrum worker. It drains stereo PCM from the SPSC ring and publishes a
/// double-buffered 16-bar snapshot that readers can copy without locks or allocations.
/// </summary>
internal sealed class SpectrumAnalyzer : IDisposable
{
    public const int BarCount = 16;

    private const int MinFftSize = 2048;
    private const int MaxFftSize = 8192;
    private const int Channels = 2;
    private const float SilenceFloorDb = -72f;
    private const int WorkerIntervalMs = 30;
    private const int NoPcmGracePeriodMs = 180;
    private const float DecayPerWorkerInterval = 0.82f;

    private readonly object _lifecycle = new();
    private readonly SpscFloatRing _ring;
    private readonly float[] _drain = new float[MaxFftSize * Channels * 2];
    private readonly float[] _history = new float[MaxFftSize * Channels];
    private readonly double[] _realLeft = new double[MaxFftSize];
    private readonly double[] _imagLeft = new double[MaxFftSize];
    private readonly double[] _realRight = new double[MaxFftSize];
    private readonly double[] _imagRight = new double[MaxFftSize];
    private readonly double[] _window = new double[MaxFftSize];
    private readonly double[] _twiddleReal = new double[MaxFftSize / 2];
    private readonly double[] _twiddleImaginary = new double[MaxFftSize / 2];
    private readonly int[] _barLowBins = new int[BarCount];
    private readonly int[] _barHighBins = new int[BarCount];
    private readonly float[] _smoothed = new float[BarCount];
    private readonly float[] _levelsA = new float[BarCount];
    private readonly float[] _levelsB = new float[BarCount];

    private float[] _published;
    private Thread? _worker;
    private volatile bool _running;
    private int _sampleRate;
    private int _fftSize;
    private int _historyWriteFrame;
    private int _historyFrameCount;
    private int _publishVersion;
    private long _lastPcmTimestamp;
    private long _lastDecayTimestamp;
    private double _amplitudeScaleSquared;
    private bool _disposed;

    public SpectrumAnalyzer(SpscFloatRing ring)
    {
        _ring = ring;
        _published = _levelsA;
    }

    public bool IsRunning => _running;

    public int SampleRate => Volatile.Read(ref _sampleRate);

    public int FftSize => Volatile.Read(ref _fftSize);

    public static int SelectFftSize(int sampleRate) => sampleRate switch
    {
        <= 0 => MinFftSize,
        <= 48000 => 2048,
        <= 96000 => 4096,
        _ => 8192
    };

    public void Restart(int sampleRate)
    {
        lock (_lifecycle)
        {
            ThrowIfDisposed();
            StopWorkerLocked();
            ResetStateLocked(sampleRate);

            _running = true;
            _worker = new Thread(WorkerLoop)
            {
                IsBackground = true,
                Name = "Player spectrum analyzer",
                Priority = ThreadPriority.BelowNormal
            };
            _worker.Start();
        }
    }

    public void Stop()
    {
        lock (_lifecycle)
        {
            StopWorkerLocked();
            ResetStateLocked(_sampleRate);
        }
    }

    public bool TryCopyLevels(Span<float> destination)
    {
        if (destination.Length < BarCount || !_running)
        {
            destination[..Math.Min(destination.Length, BarCount)].Clear();
            return false;
        }

        var spinner = new SpinWait();
        while (true)
        {
            var before = Volatile.Read(ref _publishVersion);
            if ((before & 1) != 0)
            {
                spinner.SpinOnce();
                continue;
            }

            Volatile.Read(ref _published).AsSpan().CopyTo(destination);
            if (before != Volatile.Read(ref _publishVersion)) continue;

            if (_running) return true;
            destination[..BarCount].Clear();
            return false;
        }
    }

    private void WorkerLoop()
    {
        while (_running)
        {
            var received = DrainRing();
            var now = Stopwatch.GetTimestamp();
            if (received)
            {
                _lastPcmTimestamp = now;
                _lastDecayTimestamp = now;
            }

            if (_historyFrameCount >= _fftSize && received)
                AnalyzeAndPublish();
            else if (!received && ElapsedMilliseconds(_lastPcmTimestamp, now) >= NoPcmGracePeriodMs)
            {
                var elapsed = ElapsedMilliseconds(_lastDecayTimestamp, now);
                _lastDecayTimestamp = now;
                DecayAndPublish(elapsed);
            }
            else if (!received)
            {
                // DirectSound normally delivers PCM in batches. Do not accumulate the batch gap
                // into the first decay step once the grace period expires.
                _lastDecayTimestamp = now;
            }

            Thread.Sleep(WorkerIntervalMs);
        }
    }

    private bool DrainRing()
    {
        var received = false;
        while (_running)
        {
            var count = _ring.Read(_drain);
            count &= ~1;
            if (count == 0) break;

            received = true;
            for (var i = 0; i < count; i += Channels)
            {
                var target = _historyWriteFrame * Channels;
                _history[target] = _drain[i];
                _history[target + 1] = _drain[i + 1];

                _historyWriteFrame++;
                if (_historyWriteFrame == MaxFftSize) _historyWriteFrame = 0;
                if (_historyFrameCount < MaxFftSize) _historyFrameCount++;
            }
        }

        return received;
    }

    private void AnalyzeAndPublish()
    {
        var fftSize = _fftSize;
        var start = _historyWriteFrame - fftSize;
        if (start < 0) start += MaxFftSize;

        for (var i = 0; i < fftSize; i++)
        {
            var frame = start + i;
            if (frame >= MaxFftSize) frame -= MaxFftSize;
            var source = frame * Channels;
            var window = _window[i];
            _realLeft[i] = _history[source] * window;
            _imagLeft[i] = 0;
            _realRight[i] = _history[source + 1] * window;
            _imagRight[i] = 0;
        }

        Fft(_realLeft, _imagLeft, fftSize);
        Fft(_realRight, _imagRight, fftSize);

        for (var bar = 0; bar < BarCount; bar++)
        {
            var power = 0.0;
            for (var bin = _barLowBins[bar]; bin < _barHighBins[bar]; bin++)
            {
                var leftPower = _realLeft[bin] * _realLeft[bin] + _imagLeft[bin] * _imagLeft[bin];
                var rightPower = _realRight[bin] * _realRight[bin] + _imagRight[bin] * _imagRight[bin];
                power += (leftPower + rightPower) * 0.5 * _amplitudeScaleSquared;
            }

            var db = power > 1e-20 ? 10.0 * Math.Log10(power) : SilenceFloorDb;
            var target = (float)Math.Clamp((db - SilenceFloorDb) / -SilenceFloorDb, 0.0, 1.0);
            var coefficient = target > _smoothed[bar] ? 0.62f : 0.16f;
            _smoothed[bar] += (target - _smoothed[bar]) * coefficient;
        }

        PublishSmoothed();
    }

    private void DecayAndPublish(double elapsedMilliseconds)
    {
        var intervalCount = Math.Max(0.0, elapsedMilliseconds / WorkerIntervalMs);
        var decay = (float)Math.Pow(DecayPerWorkerInterval, intervalCount);
        var changed = false;
        for (var i = 0; i < _smoothed.Length; i++)
        {
            if (_smoothed[i] <= 0.0005f)
            {
                if (_smoothed[i] != 0) changed = true;
                _smoothed[i] = 0;
                continue;
            }

            _smoothed[i] *= decay;
            if (_smoothed[i] <= 0.0005f)
                _smoothed[i] = 0;
            changed = true;
        }

        if (changed) PublishSmoothed();
    }

    private void PublishSmoothed()
    {
        var current = Volatile.Read(ref _published);
        var target = ReferenceEquals(current, _levelsA) ? _levelsB : _levelsA;
        Interlocked.Increment(ref _publishVersion);
        _smoothed.AsSpan().CopyTo(target);
        Volatile.Write(ref _published, target);
        Interlocked.Increment(ref _publishVersion);
    }

    private void StopWorkerLocked()
    {
        _running = false;
        var worker = _worker;
        _worker = null;
        if (worker is not null && worker != Thread.CurrentThread)
            worker.Join();
    }

    private void ResetStateLocked(int sampleRate)
    {
        _sampleRate = sampleRate > 0 ? sampleRate : 48000;
        var fftSize = SelectFftSize(_sampleRate);
        if (_fftSize != fftSize)
        {
            _fftSize = fftSize;
            PrepareTransformTables(fftSize);
        }
        PrepareBarRanges(_sampleRate, fftSize);
        _historyWriteFrame = 0;
        _historyFrameCount = 0;
        _lastPcmTimestamp = Stopwatch.GetTimestamp();
        _lastDecayTimestamp = _lastPcmTimestamp;
        Array.Clear(_history);
        Array.Clear(_smoothed);
        Interlocked.Increment(ref _publishVersion);
        Array.Clear(_levelsA);
        Array.Clear(_levelsB);
        Volatile.Write(ref _published, _levelsA);
        Interlocked.Increment(ref _publishVersion);
        _ring.Reset();
    }

    private static double ElapsedMilliseconds(long start, long end) =>
        (end - start) * 1000.0 / Stopwatch.Frequency;

    private void PrepareTransformTables(int fftSize)
    {
        var windowSum = 0.0;
        for (var i = 0; i < fftSize; i++)
        {
            var value = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (fftSize - 1));
            _window[i] = value;
            windowSum += value;
        }

        var amplitudeScale = 2.0 / Math.Max(1.0, windowSum);
        _amplitudeScaleSquared = amplitudeScale * amplitudeScale;

        for (var i = 0; i < fftSize / 2; i++)
        {
            var angle = -2 * Math.PI * i / fftSize;
            _twiddleReal[i] = Math.Cos(angle);
            _twiddleImaginary[i] = Math.Sin(angle);
        }
    }

    private void PrepareBarRanges(int sampleRate, int fftSize)
    {
        var nyquist = sampleRate * 0.5;
        var topFrequency = Math.Min(20000.0, nyquist);
        const double bottomFrequency = 50.0;
        var frequencyRange = Math.Max(1.0, topFrequency / bottomFrequency);

        for (var bar = 0; bar < BarCount; bar++)
        {
            var lowFrequency = bottomFrequency * Math.Pow(frequencyRange, (double)bar / BarCount);
            var highFrequency = bottomFrequency * Math.Pow(frequencyRange, (double)(bar + 1) / BarCount);
            var lowBin = Math.Max(1, (int)Math.Floor(lowFrequency * fftSize / sampleRate));
            var highBin = Math.Min(fftSize / 2, (int)Math.Ceiling(highFrequency * fftSize / sampleRate));
            if (highBin <= lowBin) highBin = Math.Min(fftSize / 2, lowBin + 1);
            _barLowBins[bar] = lowBin;
            _barHighBins[bar] = highBin;
        }
    }

    private void Fft(double[] real, double[] imaginary, int length)
    {
        var j = 0;
        for (var i = 1; i < length; i++)
        {
            var bit = length >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j)
            {
                (real[i], real[j]) = (real[j], real[i]);
                (imaginary[i], imaginary[j]) = (imaginary[j], imaginary[i]);
            }
        }

        for (var size = 2; size <= length; size <<= 1)
        {
            var half = size >> 1;
            var twiddleStride = length / size;
            for (var offset = 0; offset < length; offset += size)
            {
                for (var k = 0; k < half; k++)
                {
                    var twiddle = k * twiddleStride;
                    var wr = _twiddleReal[twiddle];
                    var wi = _twiddleImaginary[twiddle];
                    var upper = offset + k;
                    var lower = upper + half;
                    var tr = wr * real[lower] - wi * imaginary[lower];
                    var ti = wr * imaginary[lower] + wi * real[lower];
                    real[lower] = real[upper] - tr;
                    imaginary[lower] = imaginary[upper] - ti;
                    real[upper] += tr;
                    imaginary[upper] += ti;
                }
            }
        }
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        lock (_lifecycle)
        {
            if (_disposed) return;
            StopWorkerLocked();
            ResetStateLocked(_sampleRate);
            _disposed = true;
        }
    }
}
