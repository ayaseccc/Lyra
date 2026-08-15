namespace Player.Core.Audio;

/// <summary>
/// 播放引擎接口。P2 起播放链路固定为「解码流 → bassmix → IOutputBackend」，
/// 输出后端可在运行时切换（ASIO / WASAPI 独占共享 / 系统输出）。
/// </summary>
public interface IPlaybackEngine : IDisposable
{
    TrackInfo? CurrentTrack { get; }

    PlayerState State { get; }

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

    /// <summary>开启/关闭频谱取样（mixer 挂 DSP tap 复制样本，不消费播放数据）。</summary>
    void EnableSpectrum(bool enabled);

    /// <summary>取归一化频谱柱（0~1，bins 个；无信号返回全 0）。30fps 由 UI 侧定时拉取。</summary>
    float[] GetSpectrumLevels(int bins = 16);

    void ClearPreload();

    /// <summary>下一曲是否已经预载好，且能与当前曲目无缝衔接。</summary>
    bool IsNextSeamless { get; }

    // ---------- 事件 ----------

    /// <summary>状态变化。可能在非 UI 线程触发。</summary>
    event EventHandler<PlayerState>? StateChanged;

    /// <summary>成功打开新曲目。</summary>
    event EventHandler<TrackInfo>? TrackOpened;

    /// <summary>播放自然结束且没有可无缝衔接的下一曲。<b>在 BASS 回调线程触发。</b></summary>
    event EventHandler? TrackEnded;

    /// <summary>已无缝切到预载的下一曲（参数是它的路径）。<b>在 BASS 混音线程触发。</b></summary>
    event EventHandler<string>? TrackTransitioned;

    /// <summary>输出链路变了（切后端、设备回退、采样率跟随切换）。</summary>
    event EventHandler? OutputChanged;

    /// <summary>可展示给用户的错误信息。</summary>
    event EventHandler<string>? ErrorOccurred;
}
