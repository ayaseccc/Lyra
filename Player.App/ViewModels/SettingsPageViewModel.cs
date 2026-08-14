using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Player.Core.Audio;
using Player.Core.Infra;
using Player.Core.Library;
using Player.App.Controls;
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
    private readonly Action? _importM3u;

    /// <summary>初始化期间不要把界面上的默认值当成用户改动去应用。</summary>
    private bool _loading = true;

    public SettingsPageViewModel(LibraryService library, IPlaybackEngine engine, Func<bool, Task> requestScan,
        ChkszClient? client = null, Action? importM3u = null)
    {
        _library = library;
        _engine = engine;
        _client = client ?? new ChkszClient();
        _requestScan = requestScan;
        _importM3u = importM3u;

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

        // UI-R3 主题组：底色（深/浅）× 染色（开/关）
        _selectedThemeBase = ThemeBases.FirstOrDefault(t =>
            t.Key.Equals(ConfigService.Current.Ui.ThemeBase, StringComparison.OrdinalIgnoreCase)) ?? ThemeBases[0];
        _themeTint = ConfigService.Current.Ui.ThemeTint;

        // L1 第三步 + L1.1：歌词组（字体/字重/桌面歌词个性化）
        var fs = (int)ConfigService.Current.Ui.DesktopLyricsFontSize;
        _selectedLyricFontSize = Array.IndexOf((LyricFontSizes as int[])!, fs) >= 0 ? fs : 20;
        _desktopLyricsTwoLines = ConfigService.Current.Ui.DesktopLyricsTwoLines;

        var ui = ConfigService.Current.Ui;
        _selectedLyricFontFamily = LyricFonts.Contains(ui.LyricFontFamily)
            ? ui.LyricFontFamily
            : (LyricFonts.Contains("Microsoft YaHei UI") ? "Microsoft YaHei UI" : LyricFonts[0]);
        _selectedLyricFontWeight = Weights.FirstOrDefault(w => w.Key == ui.LyricFontWeight) ?? Weights[0];
        _desktopLyricsShowBackground = ui.DesktopLyricsShowBackground;
        _selectedDesktopLyricsBgOpacity = BgOpacities.FirstOrDefault(o => Math.Abs(o.Value - ui.DesktopLyricsBgOpacity) < 0.01) ?? BgOpacities[2];
        _selectedDesktopLyricsTextColor = TextColors.FirstOrDefault(c =>
            (c.Key == "Theme" && ui.DesktopLyricsTextColorMode == "Theme")
            || (c.Key != "Theme" && ui.DesktopLyricsTextColorMode == "Custom" && string.Equals(c.Key, ui.DesktopLyricsTextColor, StringComparison.OrdinalIgnoreCase)))
            ?? TextColors[0];

        // P3 在线组：Key 只从 data/config.json 读
        ApiKey = ConfigService.Current.ApiKey;
        RefreshKeyStatus();

        // L2 行为组：关闭到托盘（默认关闭）
        _closeToTray = ConfigService.Current.Ui.CloseToTray;

        // L2 全局热键（默认全关）
        _globalHotkeysEnabled = ConfigService.Current.Ui.GlobalHotkeysEnabled;

        _loading = false;
    }

    public string Title => "设置";

    public string Subtitle => "输出与媒体库";

    // ================= 分页（UI-R3 反馈） =================

    [ObservableProperty]
    private bool _isThemeTab = true;

    [ObservableProperty]
    private bool _isOutputTab;

    [ObservableProperty]
    private bool _isLibraryTab;

    [ObservableProperty]
    private bool _isOnlineTab;

    [ObservableProperty]
    private bool _isLyricTab;

    [ObservableProperty]
    private bool _isShortcutTab;

    [ObservableProperty]
    private bool _isBehaviorTab;

    // ================= 行为（L2：关闭行为；启动恢复策略随 L2 设置页补全） =================

    /// <summary>关闭主窗时最小化到托盘（默认关闭 = 关窗即退出；开启后退出走托盘菜单）。</summary>
    [ObservableProperty]
    private bool _closeToTray;

    partial void OnCloseToTrayChanged(bool value)
    {
        if (_loading) return;
        ConfigService.Current.Ui.CloseToTray = value;
        ConfigService.Save();
    }

    // ================= 快捷键（L2：只读清单 + 全局热键开关） =================

    /// <summary>全局热键（RegisterHotKey）：默认全关；开启后任何窗口下 Ctrl+Alt+P/←/→ 控制播放。</summary>
    [ObservableProperty]
    private bool _globalHotkeysEnabled;

    partial void OnGlobalHotkeysEnabledChanged(bool value)
    {
        if (_loading) return;
        ConfigService.Current.Ui.GlobalHotkeysEnabled = value;
        ConfigService.Save();
    }

    public sealed record ShortcutItem(string Keys, string Description);

    public IReadOnlyList<ShortcutItem> ShortcutItems { get; } = new[]
    {
        new ShortcutItem("Space", "播放 / 暂停（全局；大歌词页内同样生效）"),
        new ShortcutItem("← / →", "后退 / 前进 5 秒"),
        new ShortcutItem("Ctrl+← / Ctrl+→", "上一曲 / 下一曲"),
        new ShortcutItem("Ctrl+F", "聚焦搜索框"),
        new ShortcutItem("Enter", "播放选中曲目（列表聚焦时）"),
        new ShortcutItem("Delete", "从歌单移除（歌单页）"),
        new ShortcutItem("Ctrl+L", "定位正在播放的曲目"),
        new ShortcutItem("F5", "重扫媒体库"),
        new ShortcutItem("Tab", "平铺 / 专辑分组切换（L1 已占用）"),
        new ShortcutItem("Esc", "退出大歌词页 / 设置页"),
        new ShortcutItem("大歌词页", "双击或 Esc 退出；空格暂停；滚轮/拖动浏览；点歌词行跳转；点空白左/右半切曲")
    };

    // ================= 歌词（L1 第三步 + L1.1 打磨） =================

    /// <summary>L1.1-②：系统字体（置顶中日文友好项）+ 字重，作用于右栏/大歌词页/桌面歌词。</summary>
    public IReadOnlyList<string> LyricFonts => LyricUiOptions.FontFamilies;

    public IReadOnlyList<FontWeightOption> Weights => LyricUiOptions.Weights;

    public IReadOnlyList<int> LyricFontSizes { get; } = new[] { 16, 20, 24 };

    [ObservableProperty]
    private string _selectedLyricFontFamily = string.Empty;

    [ObservableProperty]
    private FontWeightOption? _selectedLyricFontWeight;

    partial void OnSelectedLyricFontFamilyChanged(string value)
    {
        if (_loading || string.IsNullOrEmpty(value)) return;
        ConfigService.Current.Ui.LyricFontFamily = value;
        ConfigService.Save();
    }

    partial void OnSelectedLyricFontWeightChanged(FontWeightOption? value)
    {
        if (_loading || value is null) return;
        ConfigService.Current.Ui.LyricFontWeight = value.Key;
        ConfigService.Save();
    }

    [ObservableProperty]
    private int _selectedLyricFontSize;

    [ObservableProperty]
    private bool _desktopLyricsTwoLines;

    partial void OnSelectedLyricFontSizeChanged(int value)
    {
        if (_loading) return;
        ConfigService.Current.Ui.DesktopLyricsFontSize = value;
        ConfigService.Save();
    }

    partial void OnDesktopLyricsTwoLinesChanged(bool value)
    {
        if (_loading) return;
        ConfigService.Current.Ui.DesktopLyricsTwoLines = value;
        ConfigService.Save();
    }

    // ---- L1.1-③：桌面歌词个性化 ----

    public IReadOnlyList<BgOpacityOption> BgOpacities => LyricUiOptions.BgOpacities;

    public IReadOnlyList<DesktopLyricsColorOption> TextColors => LyricUiOptions.TextColors;

    [ObservableProperty]
    private bool _desktopLyricsShowBackground;

    [ObservableProperty]
    private BgOpacityOption? _selectedDesktopLyricsBgOpacity;

    [ObservableProperty]
    private DesktopLyricsColorOption? _selectedDesktopLyricsTextColor;

    partial void OnDesktopLyricsShowBackgroundChanged(bool value)
    {
        if (_loading) return;
        ConfigService.Current.Ui.DesktopLyricsShowBackground = value;
        ConfigService.Save();
    }

    partial void OnSelectedDesktopLyricsBgOpacityChanged(BgOpacityOption? value)
    {
        if (_loading || value is null) return;
        ConfigService.Current.Ui.DesktopLyricsBgOpacity = value.Value;
        ConfigService.Save();
    }

    partial void OnSelectedDesktopLyricsTextColorChanged(DesktopLyricsColorOption? value)
    {
        if (_loading || value is null) return;
        if (value.Key == "Theme")
        {
            ConfigService.Current.Ui.DesktopLyricsTextColorMode = "Theme";
        }
        else
        {
            ConfigService.Current.Ui.DesktopLyricsTextColorMode = "Custom";
            ConfigService.Current.Ui.DesktopLyricsTextColor = value.Key;
        }
        ConfigService.Save();
    }

    // ================= 主题（UI-R3） =================

    public sealed record ThemeBaseOption(string Key, string Name)
    {
        public override string ToString() => Name;
    }

    public IReadOnlyList<ThemeBaseOption> ThemeBases { get; } = new[]
    {
        new ThemeBaseOption("Light", "浅色"),
        new ThemeBaseOption("Dark", "深色")
    };

    [ObservableProperty]
    private ThemeBaseOption _selectedThemeBase;

    [ObservableProperty]
    private bool _themeTint;

    partial void OnSelectedThemeBaseChanged(ThemeBaseOption value)
    {
        if (_loading) return;
        ApplyTheme();
    }

    partial void OnThemeTintChanged(bool value)
    {
        if (_loading) return;
        ApplyTheme();
    }

    private void ApplyTheme()
    {
        var dark = SelectedThemeBase.Key.Equals("Dark", StringComparison.OrdinalIgnoreCase);
        Player.App.Theming.ThemeService.SetMode(dark, ThemeTint);
    }

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

    /// <summary>导入 m3u8 播放列表（UI-R1.5 反馈：入口从侧边栏移进设置页）。</summary>
    [RelayCommand]
    private void ImportM3u() => _importM3u?.Invoke();

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

    /// <summary>额度展示（P3.1-④ 从播放条迁到设置页）：免费/付费余量 + UTC+8 重置时间。</summary>
    public string QuotaDisplay
    {
        get
        {
            var quota = _client.Quota;

            if (quota.FreeRemaining is null)
                return "尚未收到额度信息（点「测试连接」后显示，数字以服务端响应头为准）";

            var text = $"今日免费剩余 {quota.FreeRemaining}";
            if (quota.PaidRemaining is > 0) text += $" · 付费 {quota.PaidRemaining}";
            text += $" · {NextResetUtc8():HH:mm}（UTC+8）重置";

            return text;
        }
    }

    /// <summary>下一个 UTC+8 零点（额度重置时刻）。</summary>
    private static DateTimeOffset NextResetUtc8()
    {
        TimeZoneInfo cst;
        try { cst = TimeZoneInfo.FindSystemTimeZoneById("China Standard Time"); }
        catch { cst = TimeZoneInfo.CreateCustomTimeZone("CST", TimeSpan.FromHours(8), "CST", "CST"); }

        var nowCst = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, cst);
        return TimeZoneInfo.ConvertTime(new DateTimeOffset(nowCst.Date.AddDays(1), cst.GetUtcOffset(nowCst)), TimeZoneInfo.Utc);
    }

    private void RefreshKeyStatus()
    {
        KeyStatus = string.IsNullOrWhiteSpace(ConfigService.Current.ApiKey)
            ? "还没有填写 API Key。在线搜索 / 歌词 / 歌单同步都依赖它（只保存在本地 data/config.json）。"
            : $"已配置（{KeyMasked}）。额度与限流以服务端响应头为准，见下方今日额度。";
        OnPropertyChanged(nameof(KeyMasked));
        OnPropertyChanged(nameof(QuotaDisplay));
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
                RefreshKeyStatus();
                OnPropertyChanged(nameof(QuotaDisplay));
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
