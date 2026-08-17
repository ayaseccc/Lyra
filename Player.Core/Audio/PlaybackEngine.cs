using ManagedBass;
using ManagedBass.Mix;
using Player.Core.Audio.Spectrum;
using Serilog;

namespace Player.Core.Audio;

/// <summary>
/// 播放引擎（P2 版）。链路：<b>解码流 → bassmix mixer → IOutputBackend</b>。
///
/// 并发模型（很重要，改动前务必读完）：
/// <list type="bullet">
/// <item><b>_control</b>：控制路径的锁（Open / 建链拆链 / 切后端 / Dispose）。
///   这些操作会调用 <c>StreamFree</c>、<c>BassAsio.Free</c> 等**会等待音频线程退出**的 API，
///   因此音频回调**绝不允许**去抢这把锁，否则必然死锁。</item>
/// <item><b>_swap</b>：只保护句柄指针（当前流 / 预载流 / 待回收列表）的极短临界区。
///   控制路径与混音回调都用它，但两边都只在里面做指针搬运，不做任何可能阻塞的调用。</item>
/// </list>
///
/// 无缝衔接：给当前解码流挂一个 <b>mixtime</b> 的 END sync，在回调里把预载好的下一曲
/// 加进 mixer（MixerChanNoRampin 去掉淡入），交接发生在样本边界上。
/// </summary>
public sealed class PlaybackEngine : IPlaybackEngine
{
    /// <summary>控制路径锁。音频回调绝不能拿它。</summary>
    private readonly object _control = new();

    /// <summary>句柄指针锁。临界区必须极短，里面不能有阻塞调用。</summary>
    private readonly object _swap = new();

    private readonly Timer _watchdog;

    private static readonly WaitCallback EndNotificationCallback = static state =>
        ((PlaybackEngine)state!).ProcessEndNotifications();

    private readonly PlaybackEndQueue _endNotifications = new();

    /// <summary>所有后台预载都在这里单飞并跟踪；BASS 退出前必须先排空。</summary>
    private readonly PreloadWorkTracker _preloadWork = new();

    /// <summary>共享委托由引擎终身持有；每次注册用 user generation 区分。</summary>
    private readonly SyncProcedure _endSyncProcedure;

    /// <summary>等着被释放的旧解码流：混音回调里不能释放，攒起来由看门狗回收。</summary>
    private readonly List<int> _pendingFree = new(4);

    private IOutputBackend _backend;
    private readonly BackendRecoveryQueue _backendRecovery = new();
    private OutputSettings _settings = new();

    private int _mixer;
    private int _mixerRate;
    private int _mixerGeneration;

    private readonly SpscFloatRing _spectrumRing;
    private readonly SpectrumPcmTap _spectrumTap;
    private readonly SpectrumAnalyzer _spectrumAnalyzer;
    private readonly DSPProcedure _spectrumDspProcedure;
    private int _spectrumConsumers;
    private bool _legacySpectrumEnabled;
    private int _spectrumDspHandle;
    private int _spectrumDspMixer;
    private int _spectrumDspGeneration;

    private int _current;
    // Track identity and control intent are deliberately separate. A seamless
    // transition must still reach the UI when Pause/Seek/Stop or an output
    // rebuild happens while its notification is queued; a natural-end
    // notification, on the other hand, must be cancelled by those controls.
    private int _trackIdentityGeneration;
    private int _currentTrackIdentityGeneration;
    private int _controlRevision;
    private int _currentControlRevision;
    private int _endSyncGeneration;
    private int _currentEndSyncGeneration;
    private int _next;
    private string? _nextPath;
    private TrackInfo? _nextInfo;
    private int _preloadGeneration;

    /// <summary>已经判定"无法无缝"的路径，避免每个 tick 重新开一次文件。</summary>
    private string? _rejectedPreloadPath;

    private double _volume = 0.6;
    private volatile PlayerState _state = PlayerState.Stopped;
    private volatile bool _disposed;
    private bool _resampling;

    public PlaybackEngine()
    {
        // 262144 floats = 131072 stereo frames: 341 ms even at 384 kHz.
        _spectrumRing = new SpscFloatRing(262144);
        _spectrumTap = new SpectrumPcmTap(_spectrumRing);
        _spectrumAnalyzer = new SpectrumAnalyzer(_spectrumRing);
        _spectrumDspProcedure = OnSpectrumDsp;
        _endSyncProcedure = OnSourceEnded;

        _backend = new DirectSoundBackend();
        _backendRecovery.Activate(_backend);
        AttachBackendEvents(_backend);

        // 看门狗：ASIO 有驱动通知，WASAPI / DirectSound 只能靠轮询发现设备掉线；
        // 顺便回收无缝交接攒下的旧句柄（整张专辑连播时不会走 Open，必须有人回收）
        _watchdog = new Timer(_ =>
        {
            if (_disposed) return;

            lock (_control)
            {
                if (_disposed) return;

                // Native Poll must not cross Start/Stop/Dispose on the same backend.
                // Its events only enqueue recovery work, so this lock cannot re-enter.
                try { _backend.Poll(); }
                catch (Exception ex) { Log.Debug(ex, "输出自检失败"); }

                try { FreePendingHandles(); }
                catch (Exception ex) { Log.Debug(ex, "回收旧解码流失败"); }
            }
        }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(1));
    }

    public TrackInfo? CurrentTrack { get; private set; }

    public PlayerState State
    {
        get => _state;
        private set => _state = value;
    }

    public int PlaybackRevision => Volatile.Read(ref _currentTrackIdentityGeneration);

    public bool IsPlaybackEventCurrent(PlaybackTrackEventArgs playbackEvent)
    {
        ArgumentNullException.ThrowIfNull(playbackEvent);

        lock (_swap)
        {
            return !_disposed && ShouldPublishEnd(
                playbackEvent.RequiresControlMatch ? PlaybackEndKind.Ended : PlaybackEndKind.Transitioned,
                playbackEvent.Revision,
                playbackEvent.ControlRevision,
                playbackEvent.Channel,
                playbackEvent.Track,
                PlaybackRevision,
                Volatile.Read(ref _currentControlRevision),
                _current,
                CurrentTrack);
        }
    }

    public event EventHandler<PlayerState>? StateChanged;
    public event EventHandler<TrackInfo>? TrackOpened;
    public event EventHandler<PlaybackTrackEventArgs>? TrackEnded;
    public event EventHandler<PlaybackTrackEventArgs>? TrackTransitioned;
    public event EventHandler? OutputChanged;
    public event EventHandler<string>? ErrorOccurred;

    // ================= 输出 =================

    public OutputSettings OutputSettings => _settings.Clone();

    public OutputBackendKind ActiveBackend => _backend.Kind;

    public string OutputDescription => _backend.Description;

    public int OutputSampleRate => _backend.IsRunning ? _backend.SampleRate : _mixerRate;

    /// <summary>
    /// 位完美判据：音量 100%、没有重采样、且后端本身不经过系统混音。
    /// DirectSound 与 WASAPI 共享模式都由系统重采样，一律不算位完美。
    /// </summary>
    public bool IsBitPerfect
    {
        get
        {
            if (Math.Abs(_volume - 1.0) > 0.0001) return false;
            if (_resampling) return false;
            if (!_backend.IsRunning) return false;

            return _backend.Kind switch
            {
                OutputBackendKind.Asio => true,
                OutputBackendKind.Wasapi => _settings.Exclusive,
                _ => false
            };
        }
    }

    public IReadOnlyList<OutputDeviceInfo> EnumerateDevices(OutputBackendKind kind)
    {
        try
        {
            return kind switch
            {
                OutputBackendKind.Asio => new AsioBackend().EnumerateDevices(),
                OutputBackendKind.Wasapi => new WasapiBackend().EnumerateDevices(),
                _ => new DirectSoundBackend().EnumerateDevices()
            };
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "枚举 {Kind} 设备失败", kind);
            return Array.Empty<OutputDeviceInfo>();
        }
    }

    public bool ApplyOutputSettings(OutputSettings settings)
    {
        var wasPlaying = State == PlayerState.Playing;
        var position = Position;

        lock (_control)
        {
            _settings = settings.Clone();

            if (_backend.Kind != settings.Backend)
                SwapBackend(CreateBackend(settings.Backend));

            // 还没开始放就先不碰设备：ASIO 驱动通常单客户端独占，
            // 空闲时占着它会让其它程序打不开声卡。等第一次 Open 再真正启动。
            if (CurrentTrack is null && _mixer == 0)
            {
                Log.Information("输出设置已记下（{Backend}），将在开始播放时生效", settings.Backend);
                OutputChanged?.Invoke(this, EventArgs.Empty);
                return true;
            }

            BuildChain(ResolveTargetRate(CurrentTrack?.SampleRate ?? _mixerRate));
        }

        if (CurrentTrack is not null)
        {
            Seek(position);
            if (wasPlaying) Play();
        }

        OutputChanged?.Invoke(this, EventArgs.Empty);
        Log.Information("输出已切换：{Description}", _backend.Description);
        return _backend.IsRunning;
    }

    private static IOutputBackend CreateBackend(OutputBackendKind kind) => kind switch
    {
        OutputBackendKind.Asio => new AsioBackend(),
        OutputBackendKind.Wasapi => new WasapiBackend(),
        _ => new DirectSoundBackend()
    };

    private void AttachBackendEvents(IOutputBackend backend)
    {
        backend.DeviceLost += OnDeviceLost;
        backend.FormatChanged += OnBackendFormatChanged;
    }

    private void SwapBackend(IOutputBackend backend)
    {
        var previous = _backend;
        // 先切换活动身份，让旧后端在 Dispose 期间发出的迟到通知立即失效。
        _backend = backend;
        _backendRecovery.Activate(backend);

        try
        {
            previous.DeviceLost -= OnDeviceLost;
            previous.FormatChanged -= OnBackendFormatChanged;
            previous.Dispose();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "释放旧后端失败");
        }

        AttachBackendEvents(_backend);
    }

    /// <summary>设备掉了（拔线 / 被别的程序抢走）。只排队，恢复由单一控制工作项执行。</summary>
    private void OnDeviceLost(object? sender, string reason)
    {
        QueueBackendRecovery(sender, BackendRecoveryKind.DeviceLost, reason);
    }

    /// <summary>设备侧格式变化。只排队，恢复由单一控制工作项执行。</summary>
    private void OnBackendFormatChanged(object? sender, string reason)
    {
        QueueBackendRecovery(sender, BackendRecoveryKind.FormatChanged, reason);
    }

    private void QueueBackendRecovery(object? sender, BackendRecoveryKind kind, string reason)
    {
        if (_disposed || sender is null) return;

        if (!_backendRecovery.TryEnqueue(sender, kind, reason, out var shouldSchedule) ||
            !shouldSchedule)
            return;

        // 后端通知线程只会触发一个合并后的控制工作项；重复通知不会叠加 Task.Run。
        _ = Task.Run(ProcessBackendRecovery);
    }

    private void ProcessBackendRecovery()
    {
        while (!_disposed && _backendRecovery.TryTake(out var request))
        {
            try
            {
                RecoverBackend(request);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "输出后端恢复失败");
            }
        }
    }

    private void RecoverBackend(in BackendRecoveryRequest request)
    {
        bool wasPlaying;
        TimeSpan position;
        string? errorMessage = null;

        lock (_control)
        {
            if (_disposed || !_backendRecovery.IsCurrent(request) ||
                !ReferenceEquals(request.Backend, _backend))
                return;

            wasPlaying = State == PlayerState.Playing;
            position = Position;

            if ((request.Kind & BackendRecoveryKind.DeviceLost) != 0)
            {
                Log.Warning("输出设备异常：{Reason}，回退到系统输出", request.Reason);
                SwapBackend(new DirectSoundBackend());
                _settings.Backend = OutputBackendKind.DirectSound;
                BuildChain(ResolveTargetRate(CurrentTrack?.SampleRate ?? 44100));
                errorMessage = request.Reason + "，已自动切回系统输出";
            }
            else
            {
                Log.Information("输出格式变化：{Reason}，重建链路", request.Reason);
                if (CurrentTrack is null) return;
                BuildChain(ResolveTargetRate(CurrentTrack.SampleRate));
            }

            // 与重建保持在同一控制临界区，避免用户切歌夹在重建和恢复位置之间。
            if (CurrentTrack is not null)
            {
                Seek(position);
                if (wasPlaying) Play();
            }
        }

        if (errorMessage is not null)
            ErrorOccurred?.Invoke(this, errorMessage);
        OutputChanged?.Invoke(this, EventArgs.Empty);
    }

    private int ResolveTargetRate(int trackRate) => SeamlessPolicy.ResolveOutputRate(trackRate, _settings);

    /// <summary>建 mixer + 启后端。当前后端起不来就回退到系统输出，绝不让播放直接死掉。</summary>
    private void BuildChain(int targetRate)
    {
        // 采样率变了，按旧采样率预载的下一曲就作废了（否则会被 BASSmix 悄悄重采样）
        if (_mixerRate != 0 && _mixerRate != targetRate) DropPreload();

        TeardownChain();

        if (TryBuildChain(targetRate, out var error)) return;

        if (_backend.Kind != OutputBackendKind.DirectSound)
        {
            Log.Warning("{Kind} 启动失败：{Error}，回退到系统输出", _backend.Kind, error);
            ErrorOccurred?.Invoke(this, $"{error}，已回退到系统输出");

            SwapBackend(new DirectSoundBackend());
            _settings.Backend = OutputBackendKind.DirectSound;

            TeardownChain();
            if (TryBuildChain(targetRate, out error)) return;
        }

        // DirectSound is the final fallback. If it also failed, release its mixer and any DSP
        // registration immediately instead of leaving a half-built chain until the next action.
        TeardownChain();
        Log.Error("输出链路建立失败：{Error}", error);
        ErrorOccurred?.Invoke(this, "音频输出无法启动：" + error);
    }

    private bool TryBuildChain(int targetRate, out string error)
    {
        error = string.Empty;

        var flags = BassFlags.Float | BassFlags.MixerNonStop;
        if (_backend.RequiresDecodingSource) flags |= BassFlags.Decode;

        // 固定 2 声道输出：多声道源在挂进 mixer 时下混，立体声源则一路不动
        _mixer = BassMix.CreateMixerStream(targetRate, 2, flags);
        if (_mixer == 0)
        {
            error = $"创建混音器失败（{Bass.LastError}）";
            return false;
        }

        _mixerGeneration = unchecked(_mixerGeneration + 1);
        if (_mixerGeneration == 0) _mixerGeneration = 1;
        _mixerRate = targetRate;
        Bass.ChannelSetAttribute(_mixer, ChannelAttribute.Volume, _volume);

        if (_current != 0)
        {
            // 重建后必须重新挂源**和 END sync**：sync 是跟着 mixer 通道走的，
            // mixer 一换就没了，漏挂会导致这首放完之后再也没有任何回调（既不无缝也不续播）
            AttachSource(_current, paused: true);
            SetEndSync(_current);
        }

        // Attach before starting a pull backend: ASIO/WASAPI may request the first block inside Start.
        if (_spectrumConsumers > 0)
        {
            _spectrumAnalyzer.Restart(_mixerRate);
            AttachSpectrumDspLocked();
        }

        try
        {
            // 同一后端重启也算新会话，丢弃旧 Poll/驱动通知，避免旧回调重建新链路。
            _backendRecovery.AdvanceSession(_backend);
            _backend.Start(_mixer, _mixerRate, 2, _settings);
        }
        catch (OutputBackendException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "后端启动时抛出未预期的异常");
            error = ex.Message;
            return false;
        }

        UpdateResamplingFlag();

        return true;
    }

    private void TeardownChain()
    {
        // 先停设备：停完之后混音线程一定不在跑，后面释放句柄才是安全的
        try { _backend.Stop(); }
        catch (Exception ex) { Log.Debug(ex, "停止后端失败"); }

        DetachSpectrumDspLocked();

        if (_mixer == 0) return;

        lock (_swap)
        {
            _currentEndSyncGeneration = 0;
            AdvanceControlRevisionLocked();
        }

        if (_current != 0)
        {
            try { BassMix.MixerRemoveChannel(_current); }
            catch (Exception ex) { Log.Debug(ex, "摘除当前源失败"); }
        }

        try { Bass.StreamFree(_mixer); }
        catch (Exception ex) { Log.Debug(ex, "释放混音器失败"); }

        _mixer = 0;
        ForgetSpectrumDspRegistration();
    }

    private void UpdateResamplingFlag()
    {
        var trackRate = CurrentTrack?.SampleRate ?? 0;
        var deviceRate = _backend.IsRunning && _backend.SampleRate > 0 ? _backend.SampleRate : _mixerRate;
        _resampling = trackRate > 0 && deviceRate > 0 && trackRate != deviceRate;
    }

    // ================= 播放 =================

    public double Volume
    {
        get => _volume;
        set
        {
            var clamped = Math.Clamp(value, 0.0, 1.0);
            _volume = clamped;

            var mixer = _mixer;
            if (mixer != 0) Bass.ChannelSetAttribute(mixer, ChannelAttribute.Volume, clamped);
        }
    }

    public TimeSpan Duration => CurrentTrack?.Duration ?? TimeSpan.Zero;

    public TimeSpan Position
    {
        get
        {
            int current, mixer;
            lock (_swap)
            {
                current = _current;
                mixer = _mixer;
            }

            if (current == 0) return TimeSpan.Zero;

            var bytes = mixer != 0
                ? BassMix.ChannelGetPosition(current)
                : Bass.ChannelGetPosition(current);

            if (bytes < 0) return TimeSpan.Zero;

            var seconds = Bass.ChannelBytes2Seconds(current, bytes);
            return seconds <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(seconds);
        }
    }

    public bool Open(string path) => OpenInternal(path, isUrl: false);

    /// <summary>
    /// 打开网络流（P4 在线试听）：BASS 直连 URL，不落盘、不进任何队列。
    /// 直链有时效，用完即弃；URL 流时长/格式信息随网络源可能不完整。
    /// </summary>
    public bool OpenUrl(string url) => OpenInternal(url, isUrl: true);

    private bool OpenInternal(string source, bool isUrl)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (string.IsNullOrWhiteSpace(source)) return false;

        if (!isUrl && !File.Exists(source))
        {
            Log.Warning("文件不存在：{File}", source);
            ErrorOccurred?.Invoke(this, $"文件不存在：{Path.GetFileName(source)}");
            return false;
        }

        if (!BassRuntime.IsInitialized)
        {
            ErrorOccurred?.Invoke(this, "音频引擎尚未初始化");
            return false;
        }

        // 建流放在所有锁之外：读文件头/建 URL 流可能耗时
        var handle = isUrl ? CreateUrlStream(source) : CreateDecodeStream(source);
        if (handle == 0)
        {
            var error = Bass.LastError;
            Log.Error("打开" + (isUrl ? "网络流" : "文件") + "失败 {Source}：{Error}", source, error);
            ErrorOccurred?.Invoke(this,
                $"无法播放 {(isUrl ? "音频流" : Path.GetFileName(source))}（BASS 错误：{error}）");

            ReleaseCurrent();
            SetState(PlayerState.Stopped, force: true);
            return false;
        }

        var path = isUrl ? source : source;
        var track = BuildTrackInfo(path, handle);

        lock (_control)
        {
            // 指针搬运在 _swap 里做，旧句柄收集出来到锁外释放
            List<int> toFree;
            lock (_swap)
            {
                toFree = new List<int>();
                if (_current != 0) toFree.Add(_current);
                if (_next != 0) toFree.Add(_next);

                _preloadGeneration++;
                _current = handle;
                CurrentTrack = track;
                AdvanceTrackIdentityLocked();
                _next = 0;
                _nextPath = null;
                _nextInfo = null;
                _rejectedPreloadPath = null;
            }

            DetachAndFree(toFree);

            var targetRate = ResolveTargetRate(track.SampleRate);
            if (_mixer == 0 || _mixerRate != targetRate || !_backend.IsRunning)
            {
                BuildChain(targetRate);   // 内部会挂源与 sync
            }
            else
            {
                AttachSource(_current, paused: true);
                SetEndSync(_current);
            }

            UpdateResamplingFlag();
        }

        FreePendingHandles();

        Log.Information("已打开 {File}（{Info}，{Duration}），输出 {Output}",
            Path.GetFileName(path), track.TechnicalSummary, track.Duration, _backend.Description);

        TrackOpened?.Invoke(this, track);
        SetState(PlayerState.Stopped, force: true);
        return true;
    }

    private static int CreateDecodeStream(string path)
    {
        // Decode：交给 mixer 混音；Float：全程 32 位浮点，避免中间截断
        var flags = BassFlags.Decode | BassFlags.Float;

        // 没有 Xing 头的 VBR MP3 靠 Prescan 才能拿到准确时长与精确 seek
        if (string.Equals(Path.GetExtension(path), ".mp3", StringComparison.OrdinalIgnoreCase))
            flags |= BassFlags.Prescan;

        var handle = Bass.CreateStream(path, 0, 0, flags);
        if (handle == 0)
            handle = Bass.CreateStream(path, 0, 0, BassFlags.Decode);

        return handle;
    }

    /// <summary>网络流（P4 试听）：Decode + Float，交给同一套 mixer 链路输出。
    /// 注意必须用 5 参重载（带 DownloadProcedure）：4 参重载把 URL 当本地文件路径，必然 FileOpen 失败（实测修正）。</summary>
    private static int CreateUrlStream(string url)
    {
        var flags = BassFlags.Decode | BassFlags.Float;
        var handle = Bass.CreateStream(url, 0, flags, null, IntPtr.Zero);
        if (handle == 0)
            handle = Bass.CreateStream(url, 0, BassFlags.Decode, null, IntPtr.Zero);

        return handle;
    }

    private void AttachSource(int handle, bool paused)
    {
        if (_mixer == 0 || handle == 0) return;

        var flags = BassFlags.MixerChanNoRampin;

        // 多声道源下混到立体声；立体声源不会被动到
        if (Bass.ChannelGetInfo(handle, out var info) && info.Channels > 2)
            flags |= BassFlags.MixerChanDownMix;

        if (paused) flags |= BassFlags.MixerChanPause;

        if (!BassMix.MixerAddChannel(_mixer, handle, flags))
            Log.Error("把解码流挂到混音器失败：{Error}", Bass.LastError);
    }

    private void SetEndSync(int handle)
    {
        if (handle == 0 || _mixer == 0) return;

        int generation;
        lock (_swap)
        {
            if (_disposed || handle != _current || _mixer == 0) return;
            generation = NextEndSyncGenerationLocked();
            _currentEndSyncGeneration = generation;
        }

        // Mixtime：回调发生在混音线程、样本边界上，是做无缝衔接的唯一正确时机
        if (BassMix.ChannelSetSync(handle, SyncFlags.End | SyncFlags.Mixtime | SyncFlags.Onetime, 0,
                _endSyncProcedure, new IntPtr(generation)) == 0)
        {
            lock (_swap)
            {
                if (_current == handle && _currentEndSyncGeneration == generation)
                    _currentEndSyncGeneration = 0;
            }

            Log.Warning("设置混音时结束回调失败：{Error}", Bass.LastError);
        }
    }

    private int NextEndSyncGenerationLocked()
    {
        var generation = unchecked(++_endSyncGeneration);
        return generation == 0 ? unchecked(++_endSyncGeneration) : generation;
    }

    private int AdvanceTrackIdentityLocked()
    {
        var identity = NextNonZero(ref _trackIdentityGeneration);
        Volatile.Write(ref _currentTrackIdentityGeneration, identity);

        // A new current track also invalidates every control-sensitive END
        // completion belonging to the previous identity.
        AdvanceControlRevisionLocked();
        return identity;
    }

    private int AdvanceControlRevisionLocked()
    {
        var revision = NextNonZero(ref _controlRevision);
        Volatile.Write(ref _currentControlRevision, revision);
        return revision;
    }

    private static int NextNonZero(ref int counter)
    {
        var value = unchecked(++counter);
        return value == 0 ? unchecked(++counter) : value;
    }

    private void AdvanceControlRevisionForControlIntent()
    {
        lock (_swap)
        {
            if (_current != 0) AdvanceControlRevisionLocked();
        }
    }

    internal static bool IsCurrentEndSync(int currentChannel, int currentGeneration,
        int callbackChannel, IntPtr user)
    {
        return currentChannel != 0 && currentGeneration != 0 &&
               callbackChannel == currentChannel &&
               unchecked((int)user.ToInt64()) == currentGeneration;
    }

    public bool Play()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_control)
        {
            if (_current == 0) return false;

            if (_mixer == 0 || !_backend.IsRunning)
            {
                BuildChain(ResolveTargetRate(CurrentTrack?.SampleRate ?? _mixerRate));
                if (!_backend.IsRunning) return false;
            }

            // 曲目播完后 BASSmix 会把源摘掉，这时要重新挂回去才放得出声
            if (BassMix.ChannelGetMixer(_current) == 0)
            {
                AttachSource(_current, paused: true);
                SetEndSync(_current);
                BassMix.ChannelSetPosition(_current, 0, PositionFlags.Bytes);
            }

            BassMix.ChannelFlags(_current, 0, BassFlags.MixerChanPause);
            _backend.Resume();
            AdvanceControlRevisionForControlIntent();
        }

        SetState(PlayerState.Playing);
        return true;
    }

    public void Pause()
    {
        lock (_control)
        {
            if (_current == 0 || State != PlayerState.Playing) return;

            BassMix.ChannelFlags(_current, BassFlags.MixerChanPause, BassFlags.MixerChanPause);
            _backend.Pause();
            AdvanceControlRevisionForControlIntent();
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
        lock (_control)
        {
            if (_current == 0) return;

            EnsureAttached(paused: true);

            BassMix.ChannelFlags(_current, BassFlags.MixerChanPause, BassFlags.MixerChanPause);
            BassMix.ChannelSetPosition(_current, 0, PositionFlags.Bytes);
            _backend.Pause();
            AdvanceControlRevisionForControlIntent();
        }

        SetState(PlayerState.Stopped);
    }

    public void Seek(TimeSpan position)
    {
        lock (_control)
        {
            if (_current == 0) return;

            // If a natural END completion is still queued, the source has already
            // been detached while the logical state is still Playing. Reattach it
            // unpaused so this seek both cancels the stale completion and keeps
            // playback moving.
            EnsureAttached(paused: ShouldPauseReattachedSource(State));

            var total = CurrentTrack?.Duration.TotalSeconds ?? 0;
            var seconds = total > 0
                ? Math.Clamp(position.TotalSeconds, 0, Math.Max(0, total - 0.05))
                : Math.Max(0, position.TotalSeconds);

            var bytes = Bass.ChannelSeconds2Bytes(_current, seconds);
            if (bytes < 0) return;

            if (!BassMix.ChannelSetPosition(_current, bytes, PositionFlags.Bytes))
                Log.Debug("Seek 到 {Seconds}s 失败：{Error}", seconds, Bass.LastError);
            else
                AdvanceControlRevisionForControlIntent();
        }
    }

    /// <summary>曲目自然播完后会被 BASSmix 摘出 mixer，Stop / Seek 之前要先确认它还挂着。</summary>
    internal static bool ShouldPauseReattachedSource(PlayerState state) =>
        state != PlayerState.Playing;

    private void EnsureAttached(bool paused)
    {
        if (_current == 0 || _mixer == 0) return;
        if (BassMix.ChannelGetMixer(_current) != 0) return;

        AttachSource(_current, paused);
        SetEndSync(_current);
    }

    // ================= 预载与无缝 =================

    public bool IsNextSeamless
    {
        get { lock (_swap) { return _next != 0; } }
    }

    public void PreloadNext(string path)
    {
        if (_disposed || string.IsNullOrWhiteSpace(path)) return;

        int generation;
        lock (_swap)
        {
            if (_next != 0 && string.Equals(_nextPath, path, StringComparison.OrdinalIgnoreCase)) return;

            // 已经判定过"接不上"的同一首，不要每个 tick 都重开一次文件
            if (string.Equals(_rejectedPreloadPath, path, StringComparison.OrdinalIgnoreCase)) return;

            generation = _preloadGeneration;
        }

        // 建流可能读盘、MP3 还要 Prescan，放后台线程，绝不能卡住 UI。
        // 相同代际与路径只允许一个任务；Dispose 会在 BASS Shutdown 前等它们全部退出。
        _preloadWork.TryStart(generation, path, () => RunPreload(path, generation));
    }

    private void RunPreload(string path, int generation)
    {
        var handle = 0;
        try
        {
            // PreloadNext 的指针检查与 tracker 接纳之间允许上一项恰好完成；
            // 后台真正读盘前再核验一次，避免这个窄窗口多做一次 MP3 Prescan。
            lock (_swap)
            {
                if (_disposed || generation != _preloadGeneration || _next != 0 ||
                    string.Equals(_rejectedPreloadPath, path, StringComparison.OrdinalIgnoreCase))
                    return;
            }

            if (!File.Exists(path)) return;

            var mixerRate = Volatile.Read(ref _mixerRate);
            handle = CreateDecodeStream(path);
            if (handle == 0)
            {
                Log.Debug("预载失败 {File}：{Error}", Path.GetFileName(path), Bass.LastError);
                MarkPreloadRejected(path, generation);
                return;
            }

            var info = BuildTrackInfo(path, handle);
            var targetRate = ResolveTargetRate(info.SampleRate);

            if (targetRate != mixerRate)
            {
                // 采样率不一致就得重建链路，注定有间隙，不预载；记下来别再试
                Log.Information("下一曲采样率 {Next} Hz 与当前输出 {Current} Hz 不同，切歌时会有极短间隙",
                    info.SampleRate, mixerRate);
                MarkPreloadRejected(path, generation);
                return;
            }

            var installed = false;
            lock (_swap)
            {
                if (!_disposed && generation == _preloadGeneration && _next == 0)
                {
                    _next = handle;
                    _nextPath = path;
                    _nextInfo = info;
                    _rejectedPreloadPath = null;
                    handle = 0; // 所有权已交给 _next；finally 不再释放
                    installed = true;
                }
            }

            if (installed)
                Log.Debug("已预载下一曲：{File}", Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "预载下一曲失败：{File}", Path.GetFileName(path));
        }
        finally
        {
            if (handle != 0)
            {
                try { Bass.StreamFree(handle); }
                catch (Exception ex) { Log.Debug(ex, "释放未采用的预载流失败"); }
            }
        }
    }

    private void MarkPreloadRejected(string path, int generation)
    {
        lock (_swap)
        {
            if (!_disposed && generation == _preloadGeneration)
                _rejectedPreloadPath = path;
        }
    }

    public void ClearPreload() => DropPreload();

    private void DropPreload()
    {
        int handle;
        lock (_swap)
        {
            _preloadGeneration++;
            handle = _next;
            _next = 0;
            _nextPath = null;
            _nextInfo = null;
            _rejectedPreloadPath = null;
        }

        if (handle == 0) return;

        try { Bass.StreamFree(handle); }
        catch (Exception ex) { Log.Debug(ex, "释放预载流失败"); }
    }

    /// <summary>混音线程只提交指针交接，END 通知由单 worker 按控制锁序列化。</summary>
    private void OnSourceEnded(int handle, int channel, int data, IntPtr user)
    {
        try
        {
            PlaybackEndCompletion completion = default;
            var transitionedChannel = 0;
            TrackInfo? transitionedTrack = null;
            string? transitionedPath = null;

            lock (_swap)
            {
                if (_disposed || !IsCurrentEndSync(_current, _currentEndSyncGeneration, channel, user))
                    return;

                if (_next != 0 && _mixer != 0)
                {
                    // 样本边界上把下一曲接进来，NoRampin 保证没有淡入
                    if (BassMix.MixerAddChannel(_mixer, _next, BassFlags.MixerChanNoRampin))
                    {
                        if (_current != 0)
                            _pendingFree.Add(_current);

                        _current = _next;
                        CurrentTrack = _nextInfo;
                        transitionedChannel = _current;
                        transitionedTrack = CurrentTrack;
                        transitionedPath = _nextPath;
                        _currentEndSyncGeneration = 0;
                        AdvanceTrackIdentityLocked();

                        _preloadGeneration++;
                        _next = 0;
                        _nextPath = null;
                        _nextInfo = null;
                    }
                }

                if (transitionedChannel != 0)
                {
                    // Native sync registration and ThreadPool scheduling stay out
                    // of the pointer lock. The identity is revalidated below.
                }
                else
                {
                    // 无下一曲或挂载失败后，当前 token 失效，迟到的重复 END 不得再次通知 UI。
                    _currentEndSyncGeneration = 0;
                    var controlRevision = AdvanceControlRevisionLocked();
                    if (CurrentTrack is null) return;
                    completion = new PlaybackEndCompletion(
                        PlaybackRevision,
                        controlRevision,
                        _current,
                        CurrentTrack,
                        PlaybackEndKind.Ended,
                        CurrentTrack.Path);
                }
            }

            if (transitionedChannel != 0)
            {
                // 新的当前曲也要挂 sync，否则它播完就没人接手了。
                SetEndSync(transitionedChannel);
                lock (_swap)
                {
                    if (_disposed || transitionedTrack is null ||
                        _current != transitionedChannel ||
                        !ReferenceEquals(CurrentTrack, transitionedTrack))
                        return;

                    completion = new PlaybackEndCompletion(
                        PlaybackRevision,
                        Volatile.Read(ref _currentControlRevision),
                        transitionedChannel,
                        transitionedTrack,
                        PlaybackEndKind.Transitioned,
                        transitionedPath ?? transitionedTrack.Path);
                }
            }

            QueueEndCompletion(completion);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理播放结束事件时异常");
        }
    }

    private void QueueEndCompletion(in PlaybackEndCompletion completion)
    {
        _endNotifications.Enqueue(completion, out var shouldSchedule);
        if (shouldSchedule)
            ThreadPool.UnsafeQueueUserWorkItem(EndNotificationCallback, this);
    }

    private void ProcessEndNotifications()
    {
        while (!_disposed)
        {
            while (!_disposed && _endNotifications.TryTake(out var completion))
            {
                try
                {
                    PublishEndCompletion(completion);
                }
                catch (Exception ex)
                {
                    // Subscriber failures must not strand worker ownership and silence
                    // every later END notification. Other engine events are synchronous,
                    // but this queue has to preserve its own drain invariant.
                    Log.Error(ex, "播放结束通知订阅者异常");
                }
            }

            // Do not reclaim worker ownership after disposal. A producer cannot
            // legally enqueue once _disposed is set, and leaving the ownership bit
            // set is harmless because the engine is no longer reachable by audio
            // callbacks. This guard also prevents a shutdown-time spin when a
            // queued completion raced with Dispose().
            if (_disposed) return;
            if (!_endNotifications.TryRetainWorker()) return;
        }
    }

    private void PublishEndCompletion(in PlaybackEndCompletion completion)
    {
        if (_disposed) return;

        var notifyStopped = false;
        lock (_control)
        {
            if (_disposed) return;

            lock (_swap)
            {
                if (!ShouldPublishEnd(
                        completion.Kind,
                        completion.IdentityRevision,
                        completion.ControlRevision,
                        completion.Channel,
                        completion.Track,
                        PlaybackRevision,
                        Volatile.Read(ref _currentControlRevision),
                        _current,
                        CurrentTrack))
                    return;

                // 身份校验与状态提交必须在同一个指针临界区内；音频回调不拿
                // _control，若先放开 _swap，它可能在两者之间完成下一次交接。
                if (completion.Kind == PlaybackEndKind.Ended)
                {
                    notifyStopped = State != PlayerState.Stopped;
                    State = PlayerState.Stopped;
                }
            }
        }

        var args = new PlaybackTrackEventArgs(
            completion.IdentityRevision,
            completion.ControlRevision,
            completion.Channel,
            completion.Track,
            completion.Path,
            completion.Kind == PlaybackEndKind.Ended);

        if (completion.Kind == PlaybackEndKind.Transitioned)
        {
            // 外部订阅者可能同步切歌、Seek 或退出；绝不能在 _control / _swap 内调用。
            if (!IsEndCompletionCurrent(completion)) return;
            TrackTransitioned?.Invoke(this, args);
            return;
        }

        // State=Stopped was committed under _control. A later Seek/output rebuild
        // may legitimately cancel auto-advance without changing that state, so a
        // second completion check must not swallow its matching state notification.
        // If Play already superseded it, the current-state check suppresses the
        // stale Stopped notification instead.
        if (notifyStopped && State == PlayerState.Stopped)
            StateChanged?.Invoke(this, PlayerState.Stopped);

        // Stopped remains ordered before TrackEnded. A control issued after the
        // state commit may still cancel only the automatic queue advance.
        if (!IsEndCompletionCurrent(completion)) return;
        TrackEnded?.Invoke(this, args);
    }

    private bool IsEndCompletionCurrent(in PlaybackEndCompletion completion)
    {
        if (_disposed) return false;

        lock (_swap)
        {
            return !_disposed && ShouldPublishEnd(
                completion.Kind,
                completion.IdentityRevision,
                completion.ControlRevision,
                completion.Channel,
                completion.Track,
                PlaybackRevision,
                Volatile.Read(ref _currentControlRevision),
                _current,
                CurrentTrack);
        }
    }

    internal static bool ShouldPublishEnd(
        PlaybackEndKind kind,
        int completionIdentityRevision,
        int completionControlRevision,
        int completionChannel,
        TrackInfo? completionTrack,
        int currentIdentityRevision,
        int currentControlRevision,
        int currentChannel,
        TrackInfo? currentTrack)
    {
        if (completionIdentityRevision == 0 ||
            completionIdentityRevision != currentIdentityRevision ||
            completionChannel == 0 || completionChannel != currentChannel ||
            completionTrack is null || !ReferenceEquals(completionTrack, currentTrack))
            return false;

        // A seamless hand-off is already an audio fact: later Pause/Seek/Stop or
        // output-chain rebuilds must not prevent the UI from learning that the
        // current track changed. Natural END is different: a later user control
        // intentionally cancels the queued auto-advance.
        return kind == PlaybackEndKind.Transitioned ||
               (completionControlRevision != 0 &&
                completionControlRevision == currentControlRevision);
    }

    /// <summary>回收无缝交接攒下的旧流。由看门狗每秒调用一次。</summary>
    private void FreePendingHandles()
    {
        List<int> toFree;
        lock (_swap)
        {
            if (_pendingFree.Count == 0) return;
            toFree = new List<int>(_pendingFree);
            _pendingFree.Clear();
        }

        DetachAndFree(toFree);
    }

    private void DetachAndFree(IEnumerable<int> handles)
    {
        foreach (var handle in handles)
        {
            if (handle == 0) continue;

            try
            {
                BassMix.MixerRemoveChannel(handle);
                Bass.StreamFree(handle);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "释放解码流失败");
            }

        }
    }

    private void ReleaseCurrent()
    {
        int handle;
        lock (_swap)
        {
            handle = _current;
            _current = 0;
            _currentEndSyncGeneration = 0;
            // Invalidate completions already handed to the worker before clearing
            // the identity. This is required even when no mixer currently exists.
            AdvanceTrackIdentityLocked();
            CurrentTrack = null;
        }

        if (handle != 0) DetachAndFree(new[] { handle });
    }

    // ================= 杂项 =================

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
            Bitrate = seconds > 0 && fileSize > 0 ? (int)(fileSize * 8.0 / seconds / 1000.0) : 0,
            Format = Path.GetExtension(path).TrimStart('.').ToUpperInvariant(),
            FileSize = fileSize
        };
    }

    private static (string Artist, string Title) SplitFileName(string fileName)
    {
        const string separator = " - ";
        var index = fileName.IndexOf(separator, StringComparison.Ordinal);
        if (index > 0 && index + separator.Length < fileName.Length)
            return (fileName[..index].Trim(), fileName[(index + separator.Length)..].Trim());
        return (string.Empty, fileName);
    }

    private void SetState(PlayerState state, bool force = false)
    {
        if (!force && State == state) return;

        State = state;
        StateChanged?.Invoke(this, state);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 先封住新请求，并在任何音频锁之外等待 Prescan / 建流工作结束。
        // App 会在本方法返回后立刻 BassRuntime.Shutdown，不能留下后台 BASS 调用。
        _preloadWork.CloseAndWait();

        // END callbacks can only observe _disposed and stop enqueueing after this
        // point; drop any metadata still owned by the worker before releasing the
        // remaining native handles.
        _endNotifications.Clear();

        _watchdog.Dispose();

        lock (_control)
        {
            FreePendingHandles();
            DropPreload();
            ReleaseCurrent();
            TeardownChain();

            try
            {
                _backend.DeviceLost -= OnDeviceLost;
                _backend.FormatChanged -= OnBackendFormatChanged;
                _backend.Dispose();
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "释放输出后端失败");
            }

            _backendRecovery.Cancel();

            _legacySpectrumEnabled = false;
            _spectrumConsumers = 0;
            _spectrumTap.Stop();
            _spectrumAnalyzer.Dispose();
        }

        Log.Debug("PlaybackEngine 已释放");
    }

    // ================= L3.2 频谱（mixer DSP 只复制 PCM；FFT 永远在后台线程） =================

    public int SpectrumBinCount => SpectrumAnalyzer.BarCount;

    public IDisposable AcquireSpectrum()
    {
        lock (_control)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            BeginSpectrumConsumerLocked();
            return new SpectrumLease(this);
        }
    }

    public bool TryCopySpectrum(Span<float> destination) => _spectrumAnalyzer.TryCopyLevels(destination);

    /// <summary>旧 UI 的幂等兼容层。true 只占一个 consumer，不会因重复调用叠加 DSP。</summary>
    public void EnableSpectrum(bool enabled)
    {
        lock (_control)
        {
            if (_disposed || _legacySpectrumEnabled == enabled) return;
            _legacySpectrumEnabled = enabled;

            if (enabled) BeginSpectrumConsumerLocked();
            else EndSpectrumConsumerLocked();
        }
    }

    /// <summary>旧 UI 的分配式兼容层。新渲染路径应复用缓冲并调用 TryCopySpectrum。</summary>
    public float[] GetSpectrumLevels(int bins = SpectrumAnalyzer.BarCount)
    {
        bins = Math.Max(0, bins);
        var result = new float[bins];
        if (bins == 0) return result;

        Span<float> levels = stackalloc float[SpectrumAnalyzer.BarCount];
        if (!TryCopySpectrum(levels)) return result;

        if (bins == SpectrumAnalyzer.BarCount)
        {
            levels.CopyTo(result);
            return result;
        }

        for (var i = 0; i < bins; i++)
        {
            var source = Math.Min(SpectrumAnalyzer.BarCount - 1,
                (int)((long)i * SpectrumAnalyzer.BarCount / bins));
            result[i] = levels[source];
        }

        return result;
    }

    private void BeginSpectrumConsumerLocked()
    {
        _spectrumConsumers++;
        if (_spectrumConsumers == 1)
            _spectrumAnalyzer.Restart(_mixerRate);

        AttachSpectrumDspLocked();
    }

    private void EndSpectrumConsumerLocked()
    {
        if (_spectrumConsumers == 0) return;
        _spectrumConsumers--;
        if (_spectrumConsumers != 0) return;

        DetachSpectrumDspLocked();
        _spectrumAnalyzer.Stop();
    }

    private void AttachSpectrumDspLocked()
    {
        if (_spectrumConsumers == 0 || _mixer == 0) return;

        var registeredDsp = Volatile.Read(ref _spectrumDspHandle);
        if (registeredDsp != 0)
        {
            if (Volatile.Read(ref _spectrumDspMixer) == _mixer &&
                Volatile.Read(ref _spectrumDspGeneration) == _mixerGeneration)
            {
                _spectrumTap.Start();
                return;
            }

            ForgetSpectrumDspRegistration();
        }

        Volatile.Write(ref _spectrumDspMixer, _mixer);
        Volatile.Write(ref _spectrumDspGeneration, _mixerGeneration);
        _spectrumTap.Start();

        var dsp = Bass.ChannelSetDSP(
            _mixer,
            _spectrumDspProcedure,
            new IntPtr(_mixerGeneration),
            0);

        if (dsp == 0)
        {
            _spectrumTap.Stop();
            ForgetSpectrumDspRegistration();
            Log.Warning("挂频谱 DSP 失败：{Error}", Bass.LastError);
            return;
        }

        Volatile.Write(ref _spectrumDspHandle, dsp);
    }

    private void DetachSpectrumDspLocked()
    {
        _spectrumTap.Stop();

        var dsp = Volatile.Read(ref _spectrumDspHandle);
        var mixer = Volatile.Read(ref _spectrumDspMixer);
        if (dsp == 0 || mixer == 0)
        {
            ForgetSpectrumDspRegistration();
            return;
        }

        if (Bass.ChannelRemoveDSP(mixer, dsp))
        {
            ForgetSpectrumDspRegistration();
            return;
        }

        // Keep the exact pair so a later teardown can retry. The inactive tap makes a failed
        // removal inert, and re-enable reuses it instead of stacking a second DSP.
        Log.Warning("卸载频谱 DSP 失败（mixer={Mixer}, HDSP={Dsp}）：{Error}", mixer, dsp, Bass.LastError);
    }

    private void ForgetSpectrumDspRegistration()
    {
        Volatile.Write(ref _spectrumDspHandle, 0);
        Volatile.Write(ref _spectrumDspMixer, 0);
        Volatile.Write(ref _spectrumDspGeneration, 0);
    }

    /// <summary>音频线程：校验当前注册后，只把交错 float PCM 复制到预分配 SPSC ring。</summary>
    private void OnSpectrumDsp(int handle, int channel, IntPtr buffer, int length, IntPtr user)
    {
        // Capture the tap epoch before registration validation. If teardown/rebuild happens
        // between validation and copy, the old callback cannot join the new tap session.
        var tapEpoch = _spectrumTap.SessionEpoch;
        if (handle != Volatile.Read(ref _spectrumDspHandle) ||
            channel != Volatile.Read(ref _spectrumDspMixer) ||
            user.ToInt64() != Volatile.Read(ref _spectrumDspGeneration))
            return;

        _spectrumTap.CopyInterleaved(buffer, length, tapEpoch);
    }

    private void ReleaseSpectrumLease()
    {
        lock (_control)
        {
            if (_disposed) return;
            EndSpectrumConsumerLocked();
        }
    }

    internal int SpectrumConsumerCount
    {
        get { lock (_control) return _spectrumConsumers; }
    }

    internal int SpectrumDspHandle => Volatile.Read(ref _spectrumDspHandle);

    internal int SpectrumMixerGeneration => _mixerGeneration;

    private sealed class SpectrumLease : IDisposable
    {
        private PlaybackEngine? _owner;

        public SpectrumLease(PlaybackEngine owner) => _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.ReleaseSpectrumLease();
    }
}

/// <summary>
/// 预载后台工作的单飞与关闭屏障。键由播放代际和路径组成，路径比较遵循 Windows
/// 文件语义且忽略大小写；关闭后拒绝新工作，并可同步等到已接纳的任务全部完成。
/// </summary>
internal sealed class PreloadWorkTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<WorkKey, Task> _active = new(WorkKeyComparer.Instance);
    private bool _closed;

    internal bool IsClosed
    {
        get { lock (_gate) return _closed; }
    }

    internal int ActiveCount
    {
        get { lock (_gate) return _active.Count; }
    }

    internal bool TryStart(int generation, string path, Action work)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(work);

        var key = new WorkKey(generation, path);
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_gate)
        {
            if (_closed || _active.ContainsKey(key)) return false;

            // start 屏障保证任务在写入字典、挂好完成回调之前不会执行用户工作。
            var task = Task.Run(() =>
            {
                start.Task.GetAwaiter().GetResult();
                work();
            });

            _active.Add(key, task);
            _ = task.ContinueWith(
                static (completed, state) =>
                {
                    var completion = (Completion)state!;
                    _ = completed.Exception; // 观察异常，避免未观察任务异常
                    completion.Owner.RemoveCompleted(completion.Key, completed);
                },
                new Completion(this, key),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            start.SetResult(true);
            return true;
        }
    }

    internal void CloseAndWait()
    {
        Task[] tasks;
        lock (_gate)
        {
            _closed = true;
            tasks = _active.Values.ToArray();
        }

        try
        {
            Task.WhenAll(tasks).GetAwaiter().GetResult();
        }
        catch
        {
            // 工作异常由任务完成回调观察；关闭屏障的职责是排空，不能阻断音频资源释放。
        }

        lock (_gate)
        {
            // 已关闭后不会再加入新任务，快照中的任务也都已经完成。
            _active.Clear();
        }
    }

    private void RemoveCompleted(WorkKey key, Task completed)
    {
        lock (_gate)
        {
            if (_active.TryGetValue(key, out var tracked) && ReferenceEquals(tracked, completed))
                _active.Remove(key);
        }
    }

    private readonly record struct WorkKey(int Generation, string Path);

    private sealed class WorkKeyComparer : IEqualityComparer<WorkKey>
    {
        internal static readonly WorkKeyComparer Instance = new();

        public bool Equals(WorkKey x, WorkKey y) =>
            x.Generation == y.Generation &&
            StringComparer.OrdinalIgnoreCase.Equals(x.Path, y.Path);

        public int GetHashCode(WorkKey obj) =>
            HashCode.Combine(obj.Generation, StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Path));
    }

    private readonly record struct Completion(PreloadWorkTracker Owner, WorkKey Key);
}
