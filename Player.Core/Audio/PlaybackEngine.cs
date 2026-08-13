using ManagedBass;
using Serilog;

namespace Player.Core.Audio;

/// <summary>
/// P0 播放引擎：本地文件 → BASS 默认输出（DirectSound）。
/// 播放链路的抽象保持不变，P2 把输出段替换成 IOutputBackend（ASIO / WASAPI）即可（PLAN 第 4 节）。
/// </summary>
public sealed class PlaybackEngine : IPlaybackEngine
{
    private readonly object _gate = new();

    private int _stream;
    private double _volume = 0.6;
    private bool _disposed;

    /// <summary>State 会被 UI 线程与 BASS 回调线程同时读写，用 volatile 保证可见性。</summary>
    private volatile PlayerState _state = PlayerState.Stopped;

    /// <summary>必须用字段持有委托：BASS 只保存函数指针，托管委托被 GC 回收会导致回调时崩溃。</summary>
    private SyncProcedure? _endSyncProcedure;

    public TrackInfo? CurrentTrack { get; private set; }

    public PlayerState State
    {
        get => _state;
        private set => _state = value;
    }

    public event EventHandler<PlayerState>? StateChanged;
    public event EventHandler<TrackInfo>? TrackOpened;
    public event EventHandler? TrackEnded;
    public event EventHandler<string>? ErrorOccurred;

    public double Volume
    {
        get => _volume;
        set
        {
            var clamped = Math.Clamp(value, 0.0, 1.0);
            lock (_gate)
            {
                _volume = clamped;
                ApplyVolumeLocked();
            }
        }
    }

    public TimeSpan Duration => CurrentTrack?.Duration ?? TimeSpan.Zero;

    public TimeSpan Position
    {
        get
        {
            lock (_gate)
            {
                if (_stream == 0) return TimeSpan.Zero;
                var bytes = Bass.ChannelGetPosition(_stream);
                if (bytes < 0) return TimeSpan.Zero;
                var seconds = Bass.ChannelBytes2Seconds(_stream, bytes);
                return seconds <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(seconds);
            }
        }
    }

    public bool Open(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(path))
            return false;

        if (!File.Exists(path))
        {
            Log.Warning("文件不存在：{File}", path);
            ErrorOccurred?.Invoke(this, $"文件不存在：{Path.GetFileName(path)}");
            return false;
        }

        if (!BassRuntime.IsInitialized)
        {
            ErrorOccurred?.Invoke(this, "音频引擎尚未初始化");
            return false;
        }

        TrackInfo? track = null;
        string? failure = null;

        lock (_gate)
        {
            FreeStreamLocked();

            // 优先 32bit float 解码，设备/格式不支持时退回默认。
            // MP3 额外加 Prescan：没有 Xing 头的 VBR 文件靠它才能拿到准确时长与精确 seek。
            var flags = BassFlags.Float;
            if (string.Equals(Path.GetExtension(path), ".mp3", StringComparison.OrdinalIgnoreCase))
                flags |= BassFlags.Prescan;

            var handle = Bass.CreateStream(path, 0, 0, flags);
            if (handle == 0)
                handle = Bass.CreateStream(path, 0, 0, BassFlags.Default);

            if (handle == 0)
            {
                var error = Bass.LastError;
                Log.Error("打开文件失败 {File}：{Error}", path, error);
                failure = $"无法播放 {Path.GetFileName(path)}（BASS 错误：{error}，可能缺少对应格式插件或文件损坏）";
            }
            else
            {
                _stream = handle;
                track = BuildTrackInfo(path, handle);
                CurrentTrack = track;
                ApplyVolumeLocked();

                _endSyncProcedure = OnStreamEnded;
                if (Bass.ChannelSetSync(handle, SyncFlags.End, 0, _endSyncProcedure) == 0)
                    Log.Warning("设置播放结束回调失败：{Error}", Bass.LastError);

                Log.Information("已打开 {File}（{Info}，{Duration}）",
                    Path.GetFileName(path), track.TechnicalSummary, track.Duration);
            }
        }

        // 事件一律在锁外触发，避免持锁调用外部代码
        if (failure is not null)
        {
            // 旧流已经释放，这里必须强制广播一次"已停止"，
            // 否则界面会停在上一首且按钮全部失灵（无流可操作）
            State = PlayerState.Stopped;
            StateChanged?.Invoke(this, PlayerState.Stopped);
            ErrorOccurred?.Invoke(this, failure);
            return false;
        }

        TrackOpened?.Invoke(this, track!);
        SetState(PlayerState.Stopped);
        return true;
    }

    public bool Play()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        string? failure = null;

        lock (_gate)
        {
            if (_stream == 0) return false;
            if (!Bass.ChannelPlay(_stream))
            {
                var error = Bass.LastError;
                Log.Error("播放失败：{Error}", error);
                failure = $"播放失败（BASS 错误：{error}）";
            }
        }

        if (failure is not null)
        {
            ErrorOccurred?.Invoke(this, failure);
            return false;
        }

        SetState(PlayerState.Playing);
        return true;
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_stream == 0 || State != PlayerState.Playing) return;
            if (!Bass.ChannelPause(_stream))
            {
                Log.Debug("暂停失败：{Error}", Bass.LastError);
                return;
            }
        }

        SetState(PlayerState.Paused);
    }

    public void TogglePlayPause()
    {
        if (State == PlayerState.Playing) Pause();
        else Play();
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_stream == 0) return;
            Bass.ChannelStop(_stream);
            Bass.ChannelSetPosition(_stream, 0);
        }

        SetState(PlayerState.Stopped);
    }

    public void Seek(TimeSpan position)
    {
        lock (_gate)
        {
            if (_stream == 0) return;

            var total = CurrentTrack?.Duration.TotalSeconds ?? 0;
            var seconds = total > 0
                ? Math.Clamp(position.TotalSeconds, 0, Math.Max(0, total - 0.05))
                : Math.Max(0, position.TotalSeconds);

            var bytes = Bass.ChannelSeconds2Bytes(_stream, seconds);
            if (bytes < 0) return;

            if (!Bass.ChannelSetPosition(_stream, bytes))
                Log.Debug("Seek 到 {Seconds}s 失败：{Error}", seconds, Bass.LastError);
        }
    }

    /// <summary>
    /// 播放自然结束的回调。运行在 BASS 线程上：这里绝不能释放流或做耗时操作
    /// （PLAN 第 11 节第 4 条），只广播事件，由上层切回 UI 线程后再决定下一步。
    /// </summary>
    private void OnStreamEnded(int handle, int channel, int data, IntPtr user)
    {
        try
        {
            SetState(PlayerState.Stopped);
            TrackEnded?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理播放结束事件时异常");
        }
    }

    private static TrackInfo BuildTrackInfo(string path, int handle)
    {
        var sampleRate = 0;
        var channels = 0;
        var bitDepth = 0;

        if (Bass.ChannelGetInfo(handle, out var info))
        {
            sampleRate = info.Frequency;
            channels = info.Channels;
            bitDepth = info.OriginalResolution;
        }

        var lengthBytes = Bass.ChannelGetLength(handle);
        var seconds = lengthBytes > 0 ? Bass.ChannelBytes2Seconds(handle, lengthBytes) : 0;

        long fileSize = 0;
        try { fileSize = new FileInfo(path).Length; } catch { /* 忽略 */ }

        var (artist, title) = SplitFileName(Path.GetFileNameWithoutExtension(path));

        return new TrackInfo
        {
            Path = path,
            Title = title,
            Artist = artist,
            Duration = seconds > 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.Zero,
            SampleRate = sampleRate,
            Channels = channels,
            BitDepth = bitDepth,
            Format = Path.GetExtension(path).TrimStart('.').ToUpperInvariant(),
            FileSize = fileSize
        };
    }

    /// <summary>P0 的临时方案：按 "歌手 - 标题" 惯例拆文件名。P1 换成 TagLibSharp 读真实标签。</summary>
    private static (string Artist, string Title) SplitFileName(string fileName)
    {
        const string separator = " - ";
        var index = fileName.IndexOf(separator, StringComparison.Ordinal);
        if (index > 0 && index + separator.Length < fileName.Length)
            return (fileName[..index].Trim(), fileName[(index + separator.Length)..].Trim());
        return (string.Empty, fileName);
    }

    private void ApplyVolumeLocked()
    {
        if (_stream == 0) return;
        Bass.ChannelSetAttribute(_stream, ChannelAttribute.Volume, _volume);
    }

    private void FreeStreamLocked()
    {
        if (_stream == 0) return;

        try
        {
            Bass.ChannelStop(_stream);
            Bass.StreamFree(_stream);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "释放音频流失败");
        }

        _stream = 0;
        _endSyncProcedure = null;
        CurrentTrack = null;
    }

    private void SetState(PlayerState state)
    {
        if (State == state) return;
        State = state;
        StateChanged?.Invoke(this, state);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_gate)
        {
            FreeStreamLocked();
        }

        Log.Debug("PlaybackEngine 已释放");
    }
}
