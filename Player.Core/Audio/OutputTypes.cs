namespace Player.Core.Audio;

/// <summary>输出后端类型（PLAN 第 4 节）。</summary>
public enum OutputBackendKind
{
    /// <summary>BASS 默认输出，兜底用，任何机器开箱能响。</summary>
    DirectSound,

    /// <summary>WASAPI 共享 / 独占。</summary>
    Wasapi,

    /// <summary>ASIO，本项目的主场景。</summary>
    Asio
}

/// <summary>采样率策略。</summary>
public enum SampleRateMode
{
    /// <summary>跟随源文件（设备支持即位完美）。</summary>
    Follow,

    /// <summary>固定输出采样率，源与之不同时由 BASS 重采样。</summary>
    Fixed
}

/// <summary>一个可选的输出设备。</summary>
public sealed record OutputDeviceInfo(
    OutputBackendKind Kind,
    int Index,
    string Name,
    bool IsDefault = false,
    string? Driver = null)
{
    public override string ToString() => Name;
}

/// <summary>输出相关的设置（对应 config.json 的 output 段）。</summary>
public sealed class OutputSettings
{
    public OutputBackendKind Backend { get; set; } = OutputBackendKind.DirectSound;

    /// <summary>设备名。按名字匹配比按序号稳，插拔后序号会变。</summary>
    public string DeviceName { get; set; } = string.Empty;

    /// <summary>WASAPI 独占模式。ASIO 天然独占，DirectSound 忽略此项。</summary>
    public bool Exclusive { get; set; } = true;

    public SampleRateMode RateMode { get; set; } = SampleRateMode.Follow;

    /// <summary>RateMode = Fixed 时的输出采样率。</summary>
    public int FixedSampleRate { get; set; } = 48000;

    /// <summary>ASIO 缓冲区（采样点）。0 = 用驱动的首选值。</summary>
    public int AsioBufferSamples { get; set; }

    /// <summary>ASIO 输出起始声道。0 = 设备的第一对（面板上的 Playback 1/2）。</summary>
    public int AsioFirstChannel { get; set; }

    /// <summary>WASAPI 缓冲区（毫秒）。</summary>
    public int WasapiBufferMs { get; set; } = 50;

    public OutputSettings Clone() => new()
    {
        Backend = Backend,
        DeviceName = DeviceName,
        Exclusive = Exclusive,
        RateMode = RateMode,
        FixedSampleRate = FixedSampleRate,
        AsioBufferSamples = AsioBufferSamples,
        AsioFirstChannel = AsioFirstChannel,
        WasapiBufferMs = WasapiBufferMs
    };
}

/// <summary>输出后端启动失败。</summary>
public sealed class OutputBackendException : Exception
{
    public OutputBackendException(string message) : base(message) { }
}
