using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Player.Core.Audio;
using Player.Core.Infra;
using Player.Core.Library;
using Player.Core.Online;
using Serilog;

namespace Player.App.ViewModels;

public sealed record BackendOption(OutputBackendKind Kind, string Name, string Hint)
{
    public override string ToString() => Name;
}

public sealed record RateModeOption(SampleRateMode Mode, string Name)
{
    public override string ToString() => Name;
}

public sealed record BufferOption(int Value, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// 设置页。P1 做了媒体库组，P2 加上输出组（PLAN 第 8 节）：
/// 后端 / 设备 / 独占 / 缓冲 / 采样率策略，改动即时生效，不需要重启程序。
/// </summary>
public sealed partial class SettingsPageViewModel : ObservableObject
{
    private readonly LibraryService _library;
    private readonly IPlaybackEngine _engine;
    private readonly ChkszClient _client;
    private readonly Func<bool, Task> _requestScan;

    /// <summary>初始化期间不要把界面上的默认值当成用户改动去应用。</summary>
    private bool _loading = true;

    public SettingsPageViewModel(LibraryService library, IPlaybackEngine engine, Func<bool, Task> requestScan,
        ChkszClient? client = null)
    {
        _library = library;
        _engine = engine;
        _client = client ?? new ChkszClient();
        _requestScan = requestScan;

        Folders = new ObservableCollection<string>(ConfigService.Current.Library.Folders);

        Backends = new[]
        {
            new BackendOption(OutputBackendKind.Asio, "ASIO", "首选。绕过系统混音，采样率跟随源文件，可做到位完美"),
            new BackendOption(OutputBackendKind.Wasapi, "WASAPI", "没有 ASIO 驱动时的次选，独占模式同样能位完美"),
            new BackendOption(OutputBackendKind.DirectSound, "系统输出", "兜底，任何机器都能出声，经过系统混音")
        };

        RateModes = new[]
        {
            new RateModeOption(SampleRateMode.Follow, "跟随源文件（位完美）"),
            new RateModeOption(SampleRateMode.Fixed, "固定采样率 + 重采样")
        };

        FixedRates = new[] { 44100, 48000, 88200, 96000, 176400, 192000 };

        AsioBuffers = new[]
        {
            new BufferOption(0, "驱动首选"),
            new BufferOption(64, "64 samples"),
            new BufferOption(128, "128 samples"),
            new BufferOption(256, "256 samples"),
            new BufferOption(512, "512 samples"),
            new BufferOption(1024, "1024 samples")
        };

        AsioChannels = new[]
        {
            new BufferOption(0, "Playback 1/2"),
            new BufferOption(2, "Playback 3/4"),
            new BufferOption(4, "Playback 5/6"),
            new BufferOption(6, "Playback 7/8")
        };

        WasapiBuffers = new[]
        {
            new BufferOption(20, "20 ms"),
            new BufferOption(50, "50 ms"),
            new BufferOption(100, "100 ms"),
            new BufferOption(200, "200 ms")
        };

        var settings = _engine.OutputSettings;
        _selectedBackend = Backends.FirstOrDefault(b => b.Kind == settings.Backend) ?? Backends[^1];
        _selectedRateMode = RateModes.First(r => r.Mode == settings.RateMode);
        _selectedFixedRate = FixedRates.Contains(settings.FixedSampleRate) ? settings.FixedSampleRate : 48000;
        _selectedAsioBuffer = AsioBuffers.FirstOrDefault(b => b.Value == settings.AsioBufferSamples) ?? AsioBuffers[0];
        _selectedWasapiBuffer = WasapiBuffers.FirstOrDefault(b => b.Value == settings.WasapiBufferMs) ?? WasapiBuffers[1];
        _exclusive = settings.Exclusive;
        _selectedAsioChannel = AsioChannels.FirstOrDefault(c => c.Value == settings.AsioFirstChannel) ?? AsioChannels[0];

        Devices = new ObservableCollection<OutputDeviceInfo>();
        LoadDevices(settings.DeviceName);

        OutputStatus = _engine.OutputDescription;

        // P3 在线组：Key 只从 data/config.json 读
        ApiKey = ConfigService.Current.ApiKey;
        RefreshKeyStatus();

        _loading = false;
    }

    public string Title => "设置";

    public string Subtitle => "输出与媒体库";

    // ================= 输出 =================

    public IReadOnlyList<BackendOption> Backends { get; }

    public IReadOnlyList<RateModeOption> RateModes { get; }

    public IReadOnlyList<int> FixedRates { get; }

    public IReadOnlyList<BufferOption> AsioBuffers { get; }

    public IReadOnlyList<BufferOption> WasapiBuffers { get; }

    /// <summary>ASIO 输出声道对。TOPPING E1x2 这类多路回放设备默认走 Playback 1/2。</summary>
    public IReadOnlyList<BufferOption> AsioChannels { get; }

    public ObservableCollection<OutputDeviceInfo> Devices { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAsio))]
    [NotifyPropertyChangedFor(nameof(IsWasapi))]
    [NotifyPropertyChangedFor(nameof(BackendHint))]
    private BackendOption _selectedBackend;

    [ObservableProperty]
    private OutputDeviceInfo? _selectedDevice;

    [ObservableProperty]
    private bool _exclusive;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFixedRate))]
    private RateModeOption _selectedRateMode;

    [ObservableProperty]
    private int _selectedFixedRate;

    [ObservableProperty]
    private BufferOption _selectedAsioBuffer;

    [ObservableProperty]
    private BufferOption _selectedWasapiBuffer;

    [ObservableProperty]
    private BufferOption _selectedAsioChannel;

    [ObservableProperty]
    private string _outputStatus = string.Empty;

    public bool IsAsio => SelectedBackend.Kind == OutputBackendKind.Asio;

    public bool IsWasapi => SelectedBackend.Kind == OutputBackendKind.Wasapi;

    public bool IsFixedRate => SelectedRateMode.Mode == SampleRateMode.Fixed;

    public string BackendHint => SelectedBackend.Hint;

    partial void OnSelectedBackendChanged(BackendOption value)
    {
        if (_loading) return;

        // 换设备列表会连带触发 OnSelectedDeviceChanged，
        // 不挡住的话会 apply 两次 —— ASIO 被开-关-开，第二次很容易撞上驱动还没释放
        _loading = true;
        try { LoadDevices(string.Empty); }
        finally { _loading = false; }

        ApplyOutput();
    }

    partial void OnSelectedDeviceChanged(OutputDeviceInfo? value)
    {
        if (_loading) return;
        ApplyOutput();
    }

    partial void OnExclusiveChanged(bool value) => ApplyOutput();

    partial void OnSelectedRateModeChanged(RateModeOption value) => ApplyOutput();

    partial void OnSelectedFixedRateChanged(int value) => ApplyOutput();

    partial void OnSelectedAsioBufferChanged(BufferOption value) => ApplyOutput();

    partial void OnSelectedWasapiBufferChanged(BufferOption value) => ApplyOutput();

    partial void OnSelectedAsioChannelChanged(BufferOption value) => ApplyOutput();

    partial void OnOutputStatusChanged(string value) { /* 仅用于界面显示 */ }

    private void LoadDevices(string preferredName)
    {
        Devices.Clear();

        foreach (var device in _engine.EnumerateDevices(SelectedBackend.Kind))
            Devices.Add(device);

        if (Devices.Count == 0)
        {
            SelectedDevice = null;
            OutputStatus = SelectedBackend.Kind == OutputBackendKind.Asio
                ? "没有检测到 ASIO 设备：确认声卡驱动已安装，且没有别的程序占着它"
                : "没有检测到可用设备";
            return;
        }

        var wanted = Devices.FirstOrDefault(d =>
                         string.Equals(d.Name, preferredName, StringComparison.OrdinalIgnoreCase))
                     ?? Devices.FirstOrDefault(d => d.IsDefault)
                     ?? Devices[0];

        SelectedDevice = wanted;
    }

    [RelayCommand]
    private void RefreshDevices()
    {
        var current = SelectedDevice?.Name ?? string.Empty;
        LoadDevices(current);
        OutputStatus = $"已刷新设备列表，共 {Devices.Count} 个";
    }

    [RelayCommand]
    private void ApplyOutput()
    {
        if (_loading) return;

        var settings = new OutputSettings
        {
            Backend = SelectedBackend.Kind,
            DeviceName = SelectedDevice?.Name ?? string.Empty,
            Exclusive = Exclusive,
            RateMode = SelectedRateMode.Mode,
            FixedSampleRate = SelectedFixedRate,
            AsioBufferSamples = SelectedAsioBuffer.Value,
            AsioFirstChannel = Math.Max(0, SelectedAsioChannel.Value),
            WasapiBufferMs = SelectedWasapiBuffer.Value
        };

        _engine.ApplyOutputSettings(settings);

        ConfigService.Current.Output.CopyFrom(settings);
        ConfigService.Save();

        // 引擎可能因为设备起不来回退到了系统输出，这里显示的是"实际生效"的结果
        OutputStatus = _engine.OutputDescription;

        if (_engine.ActiveBackend != settings.Backend)
        {
            // 起不来被回退了：界面要跟着回到实际生效的后端，设备列表也要一起换
            _loading = true;
            try
            {
                SelectedBackend = Backends.First(b => b.Kind == _engine.ActiveBackend);
                LoadDevices(string.Empty);
            }
            finally { _loading = false; }
        }
    }

    // ================= 媒体库 =================

    public ObservableCollection<string> Folders { get; }

    [ObservableProperty]
    private string? _selectedFolder;

    public bool IsScanning => _library.IsScanning;

    [RelayCommand]
    private async Task AddFolderAsync()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择音乐文件夹",
            Multiselect = false
        };

        if (dialog.ShowDialog() != true) return;

        var folder = dialog.FolderName;
        if (string.IsNullOrWhiteSpace(folder)) return;

        if (Folders.Any(f => string.Equals(f, folder, StringComparison.OrdinalIgnoreCase)))
            return;

        Folders.Add(folder);
        PersistFolders();

        await _requestScan(false);
    }

    [RelayCommand]
    private async Task RemoveFolderAsync()
    {
        if (SelectedFolder is null) return;

        var folder = SelectedFolder;
        Folders.Remove(folder);
        SelectedFolder = null;
        PersistFolders();

        // 扫描器只负责"根目录之内"的曲目，移除根目录时要显式把它下面的曲目清出去
        await Task.Run(() => _library.RemoveTracksUnderRoot(folder));
        await _requestScan(false);
    }

    [RelayCommand]
    private Task RescanAsync() => _requestScan(true);

    [RelayCommand]
    private Task IncrementalScanAsync() => _requestScan(false);

    [RelayCommand]
    private void CancelScan() => _library.CancelScan();

    private void PersistFolders()
    {
        // 设置页开着的时候用户可能又拖了文件夹进来，先合并再写回，别把它抹掉
        var merged = ConfigService.Current.Library.Folders
            .Where(existing => Folders.Any(f => string.Equals(f, existing, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        foreach (var folder in Folders)
        {
            if (!merged.Any(m => string.Equals(m, folder, StringComparison.OrdinalIgnoreCase)))
                merged.Add(folder);
        }

        ConfigService.Current.Library.Folders = merged;
        ConfigService.Save();
        _library.StartWatching();   // 根目录变了，监听也要跟着重建
    }

    // ================= 在线（P3） =================

    [ObservableProperty]
    private string _apiKey = string.Empty;

    /// <summary>Key 是否已配置（用于界面提示，不显示 Key 本身）。</summary>
    [ObservableProperty]
    private string _keyStatus = string.Empty;

    [ObservableProperty]
    private bool _isTestingKey;

    public string KeyMasked => string.IsNullOrEmpty(ApiKey)
        ? string.Empty
        : ApiKey.Length <= 10
            ? ApiKey[..Math.Min(4, ApiKey.Length)] + "…"
            : ApiKey[..6] + "…" + ApiKey[^4..];

    private void RefreshKeyStatus()
    {
        KeyStatus = string.IsNullOrWhiteSpace(ConfigService.Current.ApiKey)
            ? "还没有填写 API Key。在线搜索 / 歌词 / 歌单同步都依赖它（只保存在本地 data/config.json）。"
            : $"已配置（{KeyMasked}）。今日免费额度以服务端响应头为准，见播放条右侧指示。";
        OnPropertyChanged(nameof(KeyMasked));
    }

    /// <summary>把 Key 写入 config.json。空输入 = 清除。</summary>
    [RelayCommand]
    private void SaveApiKey()
    {
        var key = ApiKey.Trim();

        ConfigService.Current.ApiKey = key;
        ConfigService.Save();
        RefreshKeyStatus();
    }

    /// <summary>发一次真实搜索校验 Key。消耗 1 次额度，失败提示不弹窗（写在状态行）。</summary>
    [RelayCommand]
    private async Task TestApiKeyAsync()
    {
        if (string.IsNullOrWhiteSpace(ApiKey))
        {
            KeyStatus = "先粘贴 API Key 再测试。";
            return;
        }

        if (!string.Equals(ApiKey.Trim(), ConfigService.Current.ApiKey, StringComparison.Ordinal))
        {
            // 测试前先落盘，ChkszClient 从配置里读 Key
            ConfigService.Current.ApiKey = ApiKey.Trim();
            ConfigService.Save();
        }

        IsTestingKey = true;
        KeyStatus = "正在测试…";

        try
        {
            var result = await _client.SearchAsync("测试", limit: 1);

            if (result.Success)
            {
                KeyStatus = $"连接正常：搜索到 {result.Data?.Total ?? 0} 条结果，剩余额度 {_client.Quota.FreeRemaining?.ToString() ?? "未知"}。";
                RefreshKeyStatus();
                KeyStatus = $"连接正常（剩余额度 {_client.Quota.FreeRemaining?.ToString() ?? "未知"}）。";
            }
            else if (result.AuthFailed)
            {
                KeyStatus = "Key 无效或未填写，请检查设置。";
            }
            else if (result.QuotaExhausted)
            {
                KeyStatus = "今日额度已用尽，等次日重置或去后台兑换 LDC。";
            }
            else
            {
                KeyStatus = "测试失败：" + result.Error;
            }
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "测试 API Key 失败");
            KeyStatus = "测试失败：" + ex.Message;
        }
        finally
        {
            IsTestingKey = false;
        }
    }
}
