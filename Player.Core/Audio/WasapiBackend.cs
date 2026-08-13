using ManagedBass;
using ManagedBass.Wasapi;
using Serilog;

namespace Player.Core.Audio;

/// <summary>
/// WASAPI 后端（独占 / 共享）。独占模式同样能位完美，是没有 ASIO 驱动时的次选。
/// 输出由 WASAPI 的回调线程从 mixer 解码流里拉数据。
/// </summary>
public sealed class WasapiBackend : IOutputBackend, IOutputDeviceEnumerator
{
    /// <summary>回调里连续拉不到数据多少次就判定设备异常。</summary>
    private const int FeedFailureThreshold = 50;

    private readonly object _gate = new();

    /// <summary>必须用字段持有：回调只拿函数指针，委托被 GC 就会崩。</summary>
    private WasapiProcedure? _procedure;

    private int _source;
    private int _device = -1;
    private bool _initialized;
    private bool _exclusive;
    private volatile bool _paused;
    private int _feedFailures;
    private string _deviceName = string.Empty;

    public OutputBackendKind Kind => OutputBackendKind.Wasapi;

    public bool RequiresDecodingSource => true;

    public bool IsRunning { get; private set; }

    public int SampleRate { get; private set; }

    public string Description => IsRunning
        ? $"WASAPI {(_exclusive ? "独占" : "共享")} · {_deviceName} · {SampleRate} Hz"
        : "WASAPI（未启动）";

    public event EventHandler<string>? DeviceLost;

#pragma warning disable CS0067 // WASAPI 没有格式变化通知，此事件永不触发，仅为满足接口
    public event EventHandler<string>? FormatChanged;
#pragma warning restore CS0067

    public IReadOnlyList<OutputDeviceInfo> EnumerateDevices()
    {
        var list = new List<OutputDeviceInfo>();

        for (var i = 0; BassWasapi.GetDeviceInfo(i, out var info); i++)
        {
            if (!info.IsEnabled) continue;
            if (info.IsInput || info.IsLoopback) continue;   // 只要真正的输出端点

            list.Add(new OutputDeviceInfo(Kind, i, info.Name ?? $"WASAPI 设备 {i}", info.IsDefault));
        }

        return list;
    }

    public void Start(int sourceHandle, int sampleRate, int channels, OutputSettings settings)
    {
        lock (_gate)
        {
            Stop();

            _source = sourceHandle;
            _exclusive = settings.Exclusive;

            _device = ResolveDevice(settings.DeviceName);
            if (_device < 0)
                throw new OutputBackendException("没有找到可用的 WASAPI 输出设备");

            _deviceName = BassWasapi.GetDeviceInfo(_device, out var info) ? info.Name ?? "WASAPI" : "WASAPI";

            var flags = _exclusive ? WasapiInitFlags.Exclusive : WasapiInitFlags.Shared;

            // 独占模式下设备未必吃这个格式，先问一句，问不过就直接给出可读的原因
            var format = BassWasapi.CheckFormat(_device, sampleRate, channels, flags);
            if (format == WasapiFormat.Unknown)
            {
                var hint = _exclusive
                    ? $"设备在独占模式下不支持 {sampleRate} Hz / {channels} 声道，可改用共享模式或固定采样率"
                    : $"设备不支持 {sampleRate} Hz / {channels} 声道";
                throw new OutputBackendException(hint);
            }

            _procedure = WasapiFeed;

            var bufferSeconds = Math.Clamp(settings.WasapiBufferMs, 5, 500) / 1000f;

            if (!BassWasapi.Init(_device, sampleRate, channels, flags, bufferSeconds, 0f, _procedure, IntPtr.Zero))
            {
                var error = Bass.LastError;
                Log.Error("BassWasapi.Init 失败，设备 {Device}：{Error}", _device, error);
                _procedure = null;
                throw new OutputBackendException(DescribeInitError(error, _exclusive));
            }

            _initialized = true;

            if (BassWasapi.GetInfo(out var wasapiInfo))
            {
                SampleRate = wasapiInfo.Frequency;
                Log.Information("WASAPI 实际格式：{Rate} Hz / {Channels} ch / {Format}，独占 {Exclusive}",
                    wasapiInfo.Frequency, wasapiInfo.Channels, wasapiInfo.Format, wasapiInfo.IsExclusive);
            }
            else
            {
                SampleRate = sampleRate;
            }

            if (!BassWasapi.Start())
            {
                var error = Bass.LastError;
                FreeDevice();
                Log.Error("BassWasapi.Start 失败：{Error}", error);
                throw new OutputBackendException($"WASAPI 启动失败（{error}）");
            }

            IsRunning = true;
            _paused = false;
            Log.Information("WASAPI 已启动：{Device}，{Rate} Hz，{Mode}", _deviceName, SampleRate,
                _exclusive ? "独占" : "共享");
        }
    }

    /// <summary>
    /// WASAPI 回调线程：只做一件事——从 mixer 拉数据。
    /// <b>这里绝不能写日志</b>（Serilog 文件 sink 是同步写盘，每个缓冲一次 IO 会直接爆音），
    /// 也不能碰 UI。连续失败只累加计数，交给 Poll 去上报。
    /// </summary>
    private int WasapiFeed(IntPtr buffer, int length, IntPtr user)
    {
        var source = _source;
        if (source == 0) return 0;

        var read = Bass.ChannelGetData(source, buffer, length);

        if (read < 0)
        {
            // 返回 0 = 输出静音；连续失败说明源或设备真出事了，由 Poll 触发回退
            Interlocked.Increment(ref _feedFailures);
            return 0;
        }

        if (_feedFailures != 0) Interlocked.Exchange(ref _feedFailures, 0);
        return read;
    }

    private int ResolveDevice(string deviceName)
    {
        var devices = EnumerateDevices();
        if (devices.Count == 0) return -1;

        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            foreach (var device in devices)
            {
                if (string.Equals(device.Name, deviceName, StringComparison.OrdinalIgnoreCase))
                    return device.Index;
            }

            Log.Information("配置里的 WASAPI 设备 {Name} 不在了，改用默认设备", deviceName);
        }

        foreach (var device in devices)
        {
            if (device.IsDefault) return device.Index;
        }

        return devices[0].Index;
    }

    private static string DescribeInitError(Errors error, bool exclusive) => error switch
    {
        Errors.Busy => exclusive
            ? "设备已被别的程序以独占方式占用（先关掉它，或改用共享模式）"
            : "设备正忙",
        Errors.Device => "WASAPI 设备不可用（可能已拔出）",
        Errors.Already => "该设备已经初始化过了",
        Errors.SampleFormat => "设备不支持这个采样格式，试试共享模式或固定采样率",
        _ => $"WASAPI 初始化失败（{error}）"
    };

    /// <summary>WASAPI 的"当前设备"是**线程局部**的，任何跨线程调用前都要先设一次。</summary>
    private void SetCurrentDevice()
    {
        if (_device < 0) return;
        try { BassWasapi.CurrentDevice = _device; }
        catch (Exception ex) { Log.Debug(ex, "设置 WASAPI 当前设备失败"); }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (!IsRunning) return;
            _paused = true;
            SetCurrentDevice();
            try { BassWasapi.Stop(false); }   // false = 不清缓冲，恢复时接着放
            catch (Exception ex) { Log.Debug(ex, "WASAPI 暂停失败"); }
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (!_initialized) return;
            _paused = false;
            SetCurrentDevice();
            try { BassWasapi.Start(); }
            catch (Exception ex) { Log.Debug(ex, "WASAPI 恢复失败"); }
        }
    }

    /// <summary>WASAPI 没有掉线通知，只能轮询：设备没了 BassWasapi 会自己停下来。</summary>
    public void Poll()
    {
        if (!IsRunning || _paused) return;

        try
        {
            if (Volatile.Read(ref _feedFailures) > FeedFailureThreshold)
            {
                Log.Warning("WASAPI 连续 {Count} 次拉不到数据，判定为设备异常", _feedFailures);
                Interlocked.Exchange(ref _feedFailures, 0);
                IsRunning = false;
                DeviceLost?.Invoke(this, "WASAPI 输出中断");
                return;
            }

            SetCurrentDevice();
            if (BassWasapi.IsStarted) return;

            Log.Warning("WASAPI 输出已停止，设备可能被拔出或被独占抢走");
            IsRunning = false;
            DeviceLost?.Invoke(this, "WASAPI 设备已断开或被其它程序独占");
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "WASAPI 自检失败");
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_initialized) return;

            SetCurrentDevice();
            try { if (BassWasapi.IsStarted) BassWasapi.Stop(true); }
            catch (Exception ex) { Log.Debug(ex, "BassWasapi.Stop 失败"); }

            FreeDevice();
        }
    }

    private void FreeDevice()
    {
        try
        {
            BassWasapi.CurrentDevice = _device;
            BassWasapi.Free();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "BassWasapi.Free 失败");
        }

        _initialized = false;
        IsRunning = false;
        _procedure = null;
        _source = 0;
    }

    public void Dispose()
    {
        Stop();
        DeviceLost = null;
    }
}
