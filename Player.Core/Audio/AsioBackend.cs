using ManagedBass;
using ManagedBass.Asio;
using Serilog;

namespace Player.Core.Audio;

/// <summary>
/// ASIO 后端（PLAN 第 4 节的主场景）。用 BASS_ASIO_ChannelEnableBASS 把 mixer 的解码流
/// 直接接到 ASIO 通道上，驱动线程按需拉数据，中间不经过系统混音。
///
/// 位完美的三个前提：① 设备采样率等于源采样率（RateMode=Follow 时由本类设置）；
/// ② 音量为 100%（音量衰减发生在 mixer 上）；③ 驱动没有自己做重采样。
/// </summary>
public sealed class AsioBackend : IOutputBackend, IOutputDeviceEnumerator
{
    private readonly object _gate = new();

    /// <summary>必须用字段持有：驱动回调只拿到函数指针，委托被 GC 就会崩。</summary>
    private readonly AsioNotifyProcedure _notifyProcedure;
    private readonly AsioNotificationMailbox _notifyMailbox = new();

    private int _device = -1;
    private int _firstChannel;
    private int _bufferSamples;
    private bool _initialized;
    private string _deviceName = string.Empty;
    private volatile bool _paused;

    public OutputBackendKind Kind => OutputBackendKind.Asio;

    public bool RequiresDecodingSource => true;

    public bool IsRunning { get; private set; }

    public int SampleRate { get; private set; }

    public string Description => IsRunning
        ? $"ASIO · {_deviceName} · {SampleRate} Hz · 缓冲 {_bufferSamples} samples"
        : "ASIO（未启动）";

    public event EventHandler<string>? DeviceLost;

    public event EventHandler<string>? FormatChanged;

    public AsioBackend() => _notifyProcedure = OnAsioNotify;

    public IReadOnlyList<OutputDeviceInfo> EnumerateDevices()
    {
        var list = new List<OutputDeviceInfo>();

        for (var i = 0; BassAsio.GetDeviceInfo(i, out var info); i++)
            list.Add(new OutputDeviceInfo(Kind, i, info.Name ?? $"ASIO 设备 {i}", i == 0, info.Driver));

        return list;
    }

    public void Start(int sourceHandle, int sampleRate, int channels, OutputSettings settings)
    {
        lock (_gate)
        {
            Stop();

            _device = ResolveDevice(settings.DeviceName);
            if (_device < 0)
                throw new OutputBackendException("没有找到可用的 ASIO 设备（驱动没装，或者被别的程序独占了）");

            if (!BassAsio.Init(_device, AsioInitFlags.Thread))
            {
                var error = BassAsio.LastError;
                Log.Error("BassAsio.Init 失败，设备 {Device}：{Error}", _device, error);
                throw new OutputBackendException(DescribeInitError(error));
            }

            _initialized = true;
            _deviceName = BassAsio.GetDeviceInfo(_device, out var info) ? info.Name ?? "ASIO" : "ASIO";

            // 设备采样率：跟随源文件（位完美）或用固定值
            if (!BassAsio.CheckRate(sampleRate))
            {
                var supported = ProbeSupportedRates();
                Free();
                throw new OutputBackendException(
                    $"设备不支持 {sampleRate} Hz" +
                    (supported.Count > 0 ? $"（它支持 {string.Join(" / ", supported)} Hz，可在设置里改用固定采样率）" : ""));
            }

            BassAsio.Rate = sampleRate;
            SampleRate = (int)Math.Round(BassAsio.Rate);
            if (SampleRate != sampleRate)
                Log.Warning("ASIO 采样率设为 {Want} Hz，驱动实际报告 {Actual} Hz", sampleRate, SampleRate);

            _firstChannel = Math.Max(0, settings.AsioFirstChannel);

            var outputs = 0;
            try { outputs = BassAsio.Info.Outputs; } catch { /* 拿不到就不校验 */ }
            if (outputs > 0 && _firstChannel + 1 >= outputs)
            {
                Free();
                throw new OutputBackendException(
                    $"设备只有 {outputs} 个输出通道，选不了 Playback {_firstChannel + 1}/{_firstChannel + 2}");
            }

            // join: true → 后续声道自动跟第一个声道成组，立体声只要一次调用
            if (!BassAsio.ChannelEnableBass(false, _firstChannel, sourceHandle, true))
            {
                var error = BassAsio.LastError;
                Free();
                Log.Error("ChannelEnableBass 失败，通道 {Channel}：{Error}", _firstChannel, error);
                throw new OutputBackendException($"无法把音频接到 ASIO 通道 {_firstChannel + 1}/{_firstChannel + 2}（{error}）");
            }

            _bufferSamples = ResolveBuffer(settings.AsioBufferSamples);

            // 在启动前注册通知，避免驱动在首个启动块里发出的变化被漏掉。
            var notifyGeneration = _notifyMailbox.BeginSession();
            if (!BassAsio.SetNotify(_notifyProcedure, new IntPtr(notifyGeneration)))
                Log.Warning("注册 ASIO 驱动通知失败：{Error}；仍保留 Poll 掉线检测", BassAsio.LastError);

            if (!BassAsio.Start(_bufferSamples, 0))
            {
                var firstError = BassAsio.LastError;
                Log.Warning("ASIO 以 {Buffer} samples 启动失败（{Error}），改用驱动首选缓冲重试",
                    _bufferSamples, firstError);

                _bufferSamples = 0;   // 0 = 交给驱动决定
                if (!BassAsio.Start(0, 0))
                {
                    var error = BassAsio.LastError;
                    Free();
                    Log.Error("BassAsio.Start 失败：{Error}", error);
                    throw new OutputBackendException($"ASIO 启动失败（{error}）");
                }
            }

            IsRunning = true;
            _paused = false;

            Log.Information(
                "ASIO 已启动：{Device}，{Rate} Hz，起始通道 {Channel}，缓冲 {Buffer} samples，延迟 {Latency} 采样",
                _deviceName, SampleRate, _firstChannel, _bufferSamples, SafeLatency());
        }
    }

    private int ResolveBuffer(int requested)
    {
        var preferred = 0;
        var min = 0;
        var max = 0;

        try
        {
            var info = BassAsio.Info;
            preferred = info.PreferredBufferLength;
            min = info.MinBufferLength;
            max = info.MaxBufferLength;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "读取 ASIO 缓冲区范围失败");
        }

        if (requested <= 0)
            return preferred > 0 ? preferred : 0;

        if (min > 0 && requested < min) return min;
        if (max > 0 && requested > max) return max;
        return requested;
    }

    private static double SafeLatency()
    {
        try { return BassAsio.GetLatency(false); }
        catch { return 0; }
    }

    private static List<int> ProbeSupportedRates()
    {
        var rates = new List<int>();
        foreach (var rate in new[] { 44100, 48000, 88200, 96000, 176400, 192000, 352800, 384000 })
        {
            try { if (BassAsio.CheckRate(rate)) rates.Add(rate); }
            catch { /* 忽略 */ }
        }
        return rates;
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

            Log.Information("配置里的 ASIO 设备 {Name} 不在了，改用第一个可用设备", deviceName);
        }

        return devices[0].Index;
    }

    private static string DescribeInitError(Errors error) => error switch
    {
        Errors.Device => "ASIO 设备不可用（可能已拔出）",
        Errors.Already => "该 ASIO 设备已被初始化",
        Errors.Driver => "ASIO 驱动打不开：多数是被别的程序占用了（ASIO 驱动通常只允许一个客户端）",
        Errors.Busy => "ASIO 设备正被别的程序占用",
        _ => $"ASIO 初始化失败（{error}）"
    };

    /// <summary>
    /// 驱动回调线程！这里只写无分配 mailbox。读取采样率、日志和事件派发统一在 Poll 完成。
    /// </summary>
    private void OnAsioNotify(AsioNotify notify, IntPtr user)
    {
        var flags = notify == AsioNotify.Rate
            ? AsioNotificationFlags.Rate
            : AsioNotificationFlags.Reset;
        var generation = unchecked((int)user.ToInt64());
        _notifyMailbox.Post(generation, flags);
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (!IsRunning) return;
            _paused = true;
            SetCurrentDevice();
            try { BassAsio.ChannelPause(false, _firstChannel); }
            catch (Exception ex) { Log.Debug(ex, "ASIO 暂停失败"); }
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (!IsRunning) return;
            _paused = false;
            SetCurrentDevice();
            // 清掉 Pause 位
            try { BassAsio.ChannelReset(false, _firstChannel, AsioChannelResetFlags.Pause); }
            catch (Exception ex) { Log.Debug(ex, "ASIO 恢复失败"); }
        }
    }

    private void SetCurrentDevice()
    {
        if (_device < 0) return;
        try { BassAsio.CurrentDevice = _device; }
        catch (Exception ex) { Log.Debug(ex, "设置 ASIO 当前设备失败"); }
    }

    /// <summary>驱动通知之外的兜底：ASIO 停了但我们以为还在放，就是设备出事了。</summary>
    public void Poll()
    {
        if (!IsRunning || _paused) return;

        try
        {
            SetCurrentDevice();
            if (!BassAsio.IsStarted)
            {
                Log.Warning("ASIO 已停止但播放器仍在播放状态，判定为设备异常");
                IsRunning = false;
                _notifyMailbox.EndSession();
                DeviceLost?.Invoke(this, "ASIO 设备已停止");
                return;
            }

            var generation = _notifyMailbox.CurrentGeneration;
            var flags = _notifyMailbox.Drain(generation);
            if (flags == AsioNotificationFlags.None) return;

            if ((flags & AsioNotificationFlags.Rate) != 0)
            {
                var rate = (int)Math.Round(BassAsio.Rate);
                SampleRate = rate;
                Log.Information("ASIO 面板把采样率改成了 {Rate} Hz，需要按新格式重建链路", rate);
                FormatChanged?.Invoke(this, $"ASIO 设备采样率变为 {rate} Hz");
                return;
            }

            // Reset 按 ASIO 文档表示驱动请求重新初始化；交给同一后端重建，失败时由引擎回退。
            Log.Information("ASIO 驱动请求重新初始化输出，需要重建链路");
            FormatChanged?.Invoke(this, "ASIO 驱动请求重新初始化输出");
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "ASIO 自检失败");
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (!_initialized) return;
            SetCurrentDevice();

            try { if (BassAsio.IsStarted) BassAsio.Stop(); }
            catch (Exception ex) { Log.Debug(ex, "BassAsio.Stop 失败"); }

            Free();
        }
    }

    private void Free()
    {
        // 先失效当前代际并移除回调，防止 Free 期间迟到通知污染下一次 Start。
        _notifyMailbox.EndSession();
        try { BassAsio.SetNotify(null!, IntPtr.Zero); }
        catch (Exception ex) { Log.Debug(ex, "移除 ASIO 通知回调失败"); }

        try { BassAsio.Free(); }
        catch (Exception ex) { Log.Debug(ex, "BassAsio.Free 失败"); }

        _initialized = false;
        IsRunning = false;
    }

    public void Dispose()
    {
        Stop();
        DeviceLost = null;
        FormatChanged = null;
    }
}
