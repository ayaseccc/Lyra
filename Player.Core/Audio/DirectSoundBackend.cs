using ManagedBass;
using Serilog;

namespace Player.Core.Audio;

/// <summary>
/// 兜底后端：BASS 默认输出（DirectSound / 系统共享混音）。
/// 它不"拉"数据，而是由 BASS 直接播放 mixer，因此 mixer 必须是可播放流。
/// </summary>
public sealed class DirectSoundBackend : IOutputBackend, IOutputDeviceEnumerator
{
    private int _source;
    private string _deviceName = "系统默认";
    private bool _paused;

    public OutputBackendKind Kind => OutputBackendKind.DirectSound;

    public bool RequiresDecodingSource => false;

    public bool IsRunning { get; private set; }

    public int SampleRate { get; private set; }

    public string Description =>
        IsRunning ? $"系统输出 · {_deviceName} · {SampleRate} Hz" : "系统输出（未启动）";

    public event EventHandler<string>? DeviceLost;

#pragma warning disable CS0067 // 系统输出没有格式变化通知，此事件永不触发，仅为满足接口
    public event EventHandler<string>? FormatChanged;
#pragma warning restore CS0067

    public IReadOnlyList<OutputDeviceInfo> EnumerateDevices()
    {
        var list = new List<OutputDeviceInfo>();

        // 0 号是 "No sound"，从 1 开始枚举
        for (var i = 1; Bass.GetDeviceInfo(i, out var info); i++)
        {
            if (!info.IsEnabled) continue;
            list.Add(new OutputDeviceInfo(Kind, i, info.Name ?? $"设备 {i}", info.IsDefault, info.Driver));
        }

        return list;
    }

    public void Start(int sourceHandle, int sampleRate, int channels, OutputSettings settings)
    {
        _source = sourceHandle;
        SampleRate = sampleRate;

        var device = ResolveDevice(settings.DeviceName);
        var deviceLabel = string.IsNullOrWhiteSpace(settings.DeviceName) ? "系统默认" : settings.DeviceName;
        if (device > 0)
        {
            if (!Bass.GetDeviceInfo(device, out var info) || !info.IsInitialized)
            {
                // 已经初始化过会返回 false，这里无害，真正的判据是下面的 ChannelSetDevice
                Bass.Init(device, sampleRate, DeviceInitFlags.Default, IntPtr.Zero);
            }

            if (!Bass.ChannelSetDevice(sourceHandle, device))
                throw new OutputBackendException($"无法切换到输出设备「{deviceLabel}」（{Bass.LastError}）");

            _deviceName = Bass.GetDeviceInfo(device, out var d) ? d.Name ?? _deviceName : _deviceName;
        }

        if (!Bass.ChannelPlay(sourceHandle, false))
            throw new OutputBackendException($"系统输出启动失败：{Bass.LastError}");

        IsRunning = true;
        _paused = false;
        Log.Information("已启动系统输出：{Device}，{Rate} Hz / {Channels} ch", _deviceName, sampleRate, channels);
    }

    private int ResolveDevice(string deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) return -1;

        foreach (var device in EnumerateDevices())
        {
            if (string.Equals(device.Name, deviceName, StringComparison.OrdinalIgnoreCase))
                return device.Index;
        }

        Log.Information("配置里的输出设备 {Name} 不在了，改用系统默认", deviceName);
        return -1;
    }

    public void Stop()
    {
        if (_source != 0)
        {
            try { Bass.ChannelStop(_source); }
            catch (Exception ex) { Log.Debug(ex, "停止系统输出时出错"); }
        }

        _source = 0;      // 句柄归引擎所有，停了就不要再拿着它，避免误伤复用的新句柄
        IsRunning = false;
    }

    public void Pause()
    {
        _paused = true;
        if (_source != 0) Bass.ChannelPause(_source);
    }

    public void Resume()
    {
        _paused = false;
        if (_source != 0) Bass.ChannelPlay(_source, false);
    }

    /// <summary>默认设备被拔掉时 BASS 会把通道停掉，这里据此发现并上报。</summary>
    public void Poll()
    {
        if (!IsRunning || _source == 0 || _paused) return;

        try
        {
            if (Bass.ChannelIsActive(_source) != PlaybackState.Stopped) return;

            Log.Warning("系统输出通道已停止，设备可能被拔出或禁用");
            IsRunning = false;
            DeviceLost?.Invoke(this, "系统输出设备已断开");
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "系统输出自检失败");
        }
    }

    public void Dispose()
    {
        Stop();
        DeviceLost = null;
    }
}
