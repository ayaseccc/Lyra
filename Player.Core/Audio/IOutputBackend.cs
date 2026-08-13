namespace Player.Core.Audio;

/// <summary>
/// 输出后端抽象（PLAN 第 4 节）。播放链路固定为「解码流 → mixer → 后端」，
/// 后端只负责把 mixer 里的音频送到设备上，不关心曲目、歌单、播放模式。
/// </summary>
public interface IOutputBackend : IDisposable
{
    OutputBackendKind Kind { get; }

    /// <summary>给界面看的一行描述，如「ASIO · TOPPING E1x2 · 48000 Hz · 缓冲 256 samples」。</summary>
    string Description { get; }

    /// <summary>
    /// true 表示后端自己从解码流里"拉"数据（ASIO / WASAPI），mixer 必须建成 decode 流；
    /// false 表示由 BASS 直接播放 mixer（DirectSound），mixer 必须是可播放流。
    /// </summary>
    bool RequiresDecodingSource { get; }

    bool IsRunning { get; }

    /// <summary>实际生效的输出采样率（ASIO 面板上显示的那个数）。</summary>
    int SampleRate { get; }

    /// <summary>启动输出。失败抛 <see cref="OutputBackendException"/>，由引擎回退到 DirectSound。</summary>
    void Start(int sourceHandle, int sampleRate, int channels, OutputSettings settings);

    void Stop();

    void Pause();

    void Resume();

    /// <summary>
    /// 周期性自检（引擎每秒调一次）。ASIO 有驱动通知，WASAPI / DirectSound 没有，
    /// 只能靠轮询发现"设备没了"，发现后触发 <see cref="DeviceLost"/>。
    /// </summary>
    void Poll();

    /// <summary>设备掉了 / 被别的程序占用 / 驱动复位。<b>可能在驱动回调线程触发。</b></summary>
    event EventHandler<string>? DeviceLost;

    /// <summary>设备侧格式变了（例如用户在 ASIO 面板上改了采样率）。引擎会用同一后端重建链路。</summary>
    event EventHandler<string>? FormatChanged;
}

/// <summary>设备枚举。做成静态是因为枚举不需要先启动后端。</summary>
public interface IOutputDeviceEnumerator
{
    IReadOnlyList<OutputDeviceInfo> EnumerateDevices();
}
