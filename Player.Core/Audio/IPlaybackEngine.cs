namespace Player.Core.Audio;

/// <summary>
/// 播放引擎接口。P0 只有一个基于 BASS 默认输出的实现；
/// P2 会在其内部把输出替换为可切换的 IOutputBackend（ASIO / WASAPI / DirectSound）。
/// </summary>
public interface IPlaybackEngine : IDisposable
{
    TrackInfo? CurrentTrack { get; }

    PlayerState State { get; }

    /// <summary>0.0 ~ 1.0 的软件音量。</summary>
    double Volume { get; set; }

    TimeSpan Position { get; }

    TimeSpan Duration { get; }

    bool Open(string path);

    bool Play();

    void Pause();

    void TogglePlayPause();

    void Stop();

    void Seek(TimeSpan position);

    /// <summary>状态变化。可能在非 UI 线程触发。</summary>
    event EventHandler<PlayerState>? StateChanged;

    /// <summary>成功打开新曲目。</summary>
    event EventHandler<TrackInfo>? TrackOpened;

    /// <summary>播放自然结束。<b>在 BASS 回调线程触发</b>，订阅方必须自行切回 UI 线程。</summary>
    event EventHandler? TrackEnded;

    /// <summary>可展示给用户的错误信息。</summary>
    event EventHandler<string>? ErrorOccurred;
}
