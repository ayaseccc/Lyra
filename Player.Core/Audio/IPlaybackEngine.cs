namespace Player.Core.Audio;

/// <summary>Identity carried by asynchronous natural-end and seamless-transition events.</summary>
public sealed class PlaybackTrackEventArgs : EventArgs
{
    internal PlaybackTrackEventArgs(
        int identityRevision,
        int controlRevision,
        int channel,
        TrackInfo track,
        string path,
        bool requiresControlMatch)
    {
        Revision = identityRevision;
        ControlRevision = controlRevision;
        Channel = channel;
        Track = track;
        Path = path;
        RequiresControlMatch = requiresControlMatch;
    }

    /// <summary>Current-track identity generation. Pause/seek/output rebuilds do not change it.</summary>
    public int Revision { get; }

    public TrackInfo Track { get; }

    public string Path { get; }

    /// <summary>Whether UI state created for another track generation belongs to this event.</summary>
    public bool HasTrackIdentity(int revision) => Revision != 0 && Revision == revision;

    internal int ControlRevision { get; }

    internal int Channel { get; }

    internal bool RequiresControlMatch { get; }
}

/// <summary>
/// 播放引擎接口。P2 起播放链路固定为「解码流 → bassmix → IOutputBackend」，
/// 输出后端可在运行时切换（ASIO / WASAPI 独占共享 / 系统输出）。
/// </summary>
public interface IPlaybackEngine : IDisposable
{
    TrackInfo? CurrentTrack { get; }

    PlayerState State { get; }

    /// <summary>Changes only when the current track identity changes.</summary>
    int PlaybackRevision { get; }

    /// <summary>
    /// Atomically validates an asynchronous natural-end or seamless-transition event.
    /// Call this immediately before applying the event after switching to another thread.
    /// </summary>
    bool IsPlaybackEventCurrent(PlaybackTrackEventArgs playbackEvent);

    /// <summary>0.0 ~ 1.0 的软件音量。衰减发生在 mixer 上，1.0 时不做任何处理（位完美）。</summary>
    double Volume { get; set; }

    TimeSpan Position { get; }

    TimeSpan Duration { get; }

    // ---------- 输出（P2） ----------

    /// <summary>当前生效的输出设置。改设置请走 <see cref="ApplyOutputSettings"/>。</summary>
    OutputSettings OutputSettings { get; }

    OutputBackendKind ActiveBackend { get; }

    /// <summary>形如「ASIO · TOPPING E1x2 · 96000 Hz · 缓冲 256 samples」。</summary>
    string OutputDescription { get; }

    /// <summary>实际输出采样率。位完美时应等于当前曲目的采样率。</summary>
    int OutputSampleRate { get; }

    /// <summary>音量为 1.0 且没有重采样时为 true，界面据此提示"当前为位完美输出"。</summary>
    bool IsBitPerfect { get; }

    IReadOnlyList<OutputDeviceInfo> EnumerateDevices(OutputBackendKind kind);

    /// <summary>运行时切换后端/设备/缓冲/采样率策略，不需要重启程序。失败会自动回退到系统输出。</summary>
    bool ApplyOutputSettings(OutputSettings settings);

    // ---------- 播放 ----------

    bool Open(string path);

    /// <summary>P4：打开网络流（在线试听）。BASS 直连 URL，不落盘。</summary>
    bool OpenUrl(string url);

    bool Play();

    void Pause();

    void TogglePlayPause();

    void Stop();

    void Seek(TimeSpan position);

    /// <summary>预载下一曲（PLAN 第 4 节：提前 5 秒）。采样率一致时可做到样本级无缝。</summary>
    void PreloadNext(string path);

    // ---------- 频谱（L3.2） ----------

    /// <summary>固定输出柱数。当前分析器发布 16 柱快照。</summary>
    int SpectrumBinCount { get; }

    /// <summary>
    /// 获取一个频谱使用租约。第一个租约挂载 mixer DSP 并启动后台 FFT；最后一个租约释放时真实卸载 DSP。
    /// </summary>
    IDisposable AcquireSpectrum();

    /// <summary>把最新频谱快照复制到调用方缓冲区。缓冲区至少需要 <see cref="SpectrumBinCount"/> 个元素。</summary>
    bool TryCopySpectrum(Span<float> destination);

    /// <summary>开启/关闭频谱取样（mixer 挂 DSP tap 复制样本，不消费播放数据）。</summary>
    /// <remarks>兼容旧调用；新代码应持有 <see cref="AcquireSpectrum"/> 返回的租约。</remarks>
    void EnableSpectrum(bool enabled);

    /// <summary>取归一化频谱柱（0~1，bins 个；无信号返回全 0）。30fps 由 UI 侧定时拉取。</summary>
    /// <remarks>兼容旧调用，会分配数组；逐帧渲染请改用 <see cref="TryCopySpectrum"/>。</remarks>
    float[] GetSpectrumLevels(int bins = 16);

    void ClearPreload();

    /// <summary>下一曲是否已经预载好，且能与当前曲目无缝衔接。</summary>
    bool IsNextSeamless { get; }

    // ---------- 事件 ----------

    /// <summary>状态变化。可能在非 UI 线程触发。</summary>
    event EventHandler<PlayerState>? StateChanged;

    /// <summary>成功打开新曲目。</summary>
    event EventHandler<TrackInfo>? TrackOpened;

    /// <summary>播放自然结束且没有可无缝衔接的下一曲。由 END worker 发布，非 BASS 回调线程。</summary>
    event EventHandler<PlaybackTrackEventArgs>? TrackEnded;

    /// <summary>已无缝切到预载的下一曲。由 END worker 发布，参数携带曲目身份。</summary>
    event EventHandler<PlaybackTrackEventArgs>? TrackTransitioned;

    /// <summary>输出链路变了（切后端、设备回退、采样率跟随切换）。</summary>
    event EventHandler? OutputChanged;

    /// <summary>可展示给用户的错误信息。</summary>
    event EventHandler<string>? ErrorOccurred;
}
