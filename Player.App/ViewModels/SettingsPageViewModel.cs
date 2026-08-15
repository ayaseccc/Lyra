using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Player.Core.Audio;
using Player.Core.Hotkeys;
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

        // P4 实机反馈：在线页补全（下载位置 / 音质档 / GD 与网易云 API 地址，改即生效）
        _downloadDir = ConfigService.Current.Online.DownloadDir;
        _previewBr = BrOptions.FirstOrDefault(o => o.Value == ConfigService.Current.Online.PreviewBr) ?? BrOptions[0];
        _gdApiUrl = ConfigService.Current.Online.GdApiUrl;
        _chkszApiUrl = ConfigService.Current.Online.ChkszApiUrl;

        // L2 行为组：关闭到托盘（默认关闭）
        _closeToTray = ConfigService.Current.Ui.CloseToTray;

        // L2 行为组：启动恢复策略（默认都恢复）
        _restoreLastTrack = ConfigService.Current.Ui.RestoreLastTrack;
        _restoreLastNav = ConfigService.Current.Ui.RestoreLastNav;

        // L2 全局热键（默认全关）
        _globalHotkeysEnabled = ConfigService.Current.Ui.GlobalHotkeysEnabled;

        _loading = false;
    }

    public string Title => "设置";

    public string Subtitle => string.Empty;

    // ================= 关于（L2 设置页补全） =================

    /// <summary>应用版本（assembly 信息版本，去掉 +hash 尾巴）。</summary>
    public string AppVersion { get; } = GetAppVersion();

    private static string GetAppVersion()
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        var attrs = asm.GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false);
        var info = attrs.Length > 0
            ? ((System.Reflection.AssemblyInformationalVersionAttribute)attrs[0]).InformationalVersion
            : null;
        var version = string.IsNullOrWhiteSpace(info) ? null : info;
        if (version is not null && version.IndexOf('+') is var plus && plus > 0) version = version[..plus];
        return string.IsNullOrWhiteSpace(version)
            ? asm.GetName().Version?.ToString(3) ?? "1.0.0"
            : version;
    }

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

    [ObservableProperty]
    private bool _isAboutTab;

    // ================= 行为（L2：关闭行为 + 启动恢复策略） =================

    /// <summary>关闭主窗时最小化到托盘（默认关闭 = 关窗即退出；开启后退出走托盘菜单）。</summary>
    [ObservableProperty]
    private bool _closeToTray;

    partial void OnCloseToTrayChanged(bool value)
    {
        if (_loading) return;
        ConfigService.Current.Ui.CloseToTray = value;
        ConfigService.Save();
    }

    /// <summary>启动时恢复上次播放的曲目（只恢复信息与歌词，不自动播放）。</summary>
    [ObservableProperty]
    private bool _restoreLastTrack;

    partial void OnRestoreLastTrackChanged(bool value)
    {
        if (_loading) return;
        ConfigService.Current.Ui.RestoreLastTrack = value;
        ConfigService.Save();
    }

    /// <summary>启动时回到上次停留的页面。</summary>
    [ObservableProperty]
    private bool _restoreLastNav;

    partial void OnRestoreLastNavChanged(bool value)
    {
        if (_loading) return;
        ConfigService.Current.Ui.RestoreLastNav = value;
        ConfigService.Save();
    }

    // ================= 快捷键（L2：可改绑清单 + 全局热键） =================

    /// <summary>全局热键（RegisterHotKey）：默认全关；开启后任何窗口下 Ctrl+Alt+P/←/→ 控制播放。</summary>
    [ObservableProperty]
    private bool _globalHotkeysEnabled;

    partial void OnGlobalHotkeysEnabledChanged(bool value)
    {
        if (_loading) return;
        ConfigService.Current.Ui.GlobalHotkeysEnabled = value;
        ConfigService.Save();
    }

    public sealed record ShortcutItem(string Keys, string Description, bool CanRebind, string ActionName);

    public sealed record GlobalHotkeyItem(string Name, string Keys, string Description);

    /// <summary>改绑状态提示（"按新组合…"/"已改绑…"/"已取消…"）。</summary>
    public string RebindStatus { get; private set; } = string.Empty;

    public bool IsRebinding { get; private set; }

    /// <summary>正在捕获的动作名（应用内动作名或全局热键名）；该行按键显示"按新组合…"。</summary>
    private string? _rebindingActionName;

    public event Action<ShortcutKey>? RebindRequested;

    public event Action<string>? RebindGlobalRequested;

    [RelayCommand]
    private void BeginRebind(string? actionName)
    {
        if (!Enum.TryParse<ShortcutKey>(actionName, out var action)) return;
        IsRebinding = true;
        _rebindingActionName = actionName;
        RebindStatus = "按新组合…（Esc 取消）";
        OnPropertyChanged(nameof(IsRebinding));
        OnPropertyChanged(nameof(RebindStatus));
        OnPropertyChanged(nameof(ShortcutItems));
        RebindRequested?.Invoke(action);
    }

    [RelayCommand]
    private void BeginRebindGlobal(string? name)
    {
        if (string.IsNullOrEmpty(name)) return;
        IsRebinding = true;
        _rebindingActionName = name;
        RebindStatus = "按新组合…（Esc 取消）";
        OnPropertyChanged(nameof(IsRebinding));
        OnPropertyChanged(nameof(RebindStatus));
        OnPropertyChanged(nameof(GlobalHotkeyItems));
        RebindGlobalRequested?.Invoke(name);
    }

    public void SetRebindStatus(string message)
    {
        RebindStatus = message;
        OnPropertyChanged(nameof(RebindStatus));
    }

    public void EndRebind(bool ok, string message)
    {
        IsRebinding = false;
        _rebindingActionName = null;
        RebindStatus = message;
        OnPropertyChanged(nameof(IsRebinding));
        OnPropertyChanged(nameof(RebindStatus));
        OnPropertyChanged(nameof(ShortcutItems));
        OnPropertyChanged(nameof(GlobalHotkeyItems));
    }

    /// <summary>应用内快捷键清单（动态：配置改绑后立即反映；Tab/Esc/大歌词页为固定项）。</summary>
    public IReadOnlyList<ShortcutItem> ShortcutItems
    {
        get
        {
            var map = new ShortcutMap(ConfigService.Current.Ui.ShortcutBindings);
            var items = Enum.GetValues<ShortcutKey>()
                .Select(k => new ShortcutItem(
                    _rebindingActionName == k.ToString() ? "按新组合…" : map.GetCombo(k),
                    ShortcutMap.Describe(k), true, k.ToString()))
                .ToList();
            items.Add(new ShortcutItem("Tab", "平铺 / 专辑分组切换（固定）", false, string.Empty));
            items.Add(new ShortcutItem("Esc", "退出大歌词页 / 设置页（固定）", false, string.Empty));
            items.Add(new ShortcutItem("大歌词页", "双击或 Esc 退出；空格暂停；滚轮/拖动浏览；点歌词行跳转；点空白左/右半切曲", false, string.Empty));
            return items;
        }
    }

    /// <summary>全局热键清单（名字 → 当前组合；配置改绑后立即反映）。</summary>
    public IReadOnlyList<GlobalHotkeyItem> GlobalHotkeyItems
    {
        get
        {
            var over = ConfigService.Current.Ui.GlobalHotkeyCombos;
            return GlobalHotkeys.GlobalHotkeyService.DefaultCombos
                .Select(c => new GlobalHotkeyItem(c.Name,
                    _rebindingActionName == c.Name ? "按新组合…" : (over.TryGetValue(c.Name, out var o) ? o : c.Combo),
                    c.Name switch
                    {
                        "PlayPause" => "播放 / 暂停",
                        "PrevTrack" => "上一曲",
                        "NextTrack" => "下一曲",
                        _ => string.Empty
                    }))
                .ToList();
        }
    }

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

    /// <summary>L2：Key 输入框密文显示。默认只回显尾 4 位；眼睛按钮临时明文（可编辑）。</summary>
    [ObservableProperty]
    private bool _isKeyRevealed;

    partial void OnApiKeyChanged(string value) => OnPropertyChanged(nameof(ApiKeyDisplay));

    partial void OnIsKeyRevealedChanged(bool value)
    {
        OnPropertyChanged(nameof(ApiKeyDisplay));
        OnPropertyChanged(nameof(IsKeyReadOnly));
    }

    /// <summary>输入框内容：隐藏态 = 圆点 + 尾 4 位（短 Key 只显圆点）；显示态 = 完整 Key（可编辑）。</summary>
    public string ApiKeyDisplay
    {
        get => IsKeyRevealed
            ? ApiKey
            : MaskTail4(ApiKey);
        set
        {
            if (IsKeyRevealed && ApiKey != value) ApiKey = value;
        }
    }

    /// <summary>隐藏态只读（避免把掩码前缀当 Key 编辑）。</summary>
    public bool IsKeyReadOnly => !IsKeyRevealed;

    /// <summary>掩码回显只保留尾 4 位（状态行等处共用；短 Key 一律只显圆点，审查修复）。</summary>
    public string KeyMasked => MaskTail4(ApiKey);

    private static string MaskTail4(string key) => string.IsNullOrEmpty(key)
        ? string.Empty
        : "••••" + (key.Length >= 4 ? key[^4..] : string.Empty);

    /// <summary>额度展示（P3.1-④ 从播放条迁到设置页）：免费/付费余量 + UTC+8 重置时间。</summary>
    public string QuotaDisplay
    {
        get
        {
            var quota = _client.Quota;

            if (quota.FreeRemaining is null)
                return "尚未收到额度信息";

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
            ? "未填写 API Key"
            : $"已配置（{KeyMasked}）";
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

    // ================= 在线（P4 实机反馈补全：下载位置 / 音质档 / API 地址） =================

    /// <summary>音质档下拉（与在线搜索页同一套标签）。</summary>
    public sealed record BrSettingOption(int Value, string Label)
    {
        public override string ToString() => Label;
    }

    public IReadOnlyList<BrSettingOption> BrOptions { get; } = new[]
    {
        new BrSettingOption(999, Player.Core.Online.QualityFormat.Br(999)),
        new BrSettingOption(740, Player.Core.Online.QualityFormat.Br(740)),
        new BrSettingOption(320, Player.Core.Online.QualityFormat.Br(320)),
        new BrSettingOption(128, Player.Core.Online.QualityFormat.Br(128)),
    };

    /// <summary>下载目录（空 = 未设置，下载时提示）。改即生效。</summary>
    [ObservableProperty]
    private string _downloadDir = string.Empty;

    partial void OnDownloadDirChanged(string value)
    {
        if (_loading) return;
        ConfigService.Current.Online.DownloadDir = value.Trim();
        ConfigService.Save();
        OnPropertyChanged(nameof(DownloadDirHint));
    }

    public string DownloadDirHint => string.Empty;

    /// <summary>在线搜索默认音质档。改即生效（下次打开搜索页起用）。</summary>
    [ObservableProperty]
    private BrSettingOption _previewBr = null!;

    partial void OnPreviewBrChanged(BrSettingOption value)
    {
        if (_loading || value is null) return;
        ConfigService.Current.Online.PreviewBr = value.Value;
        ConfigService.Save();
    }

    /// <summary>GD 音源 API 地址（空 = 官方默认）。改即生效。</summary>
    [ObservableProperty]
    private string _gdApiUrl = string.Empty;

    partial void OnGdApiUrlChanged(string value)
    {
        if (_loading) return;
        if (!Player.Core.Online.OnlineUrl.IsHttp(value.Trim()) && !string.IsNullOrWhiteSpace(value))
        {
            // 非法地址不落盘，状态行提示；界面值保留让用户改
            KeyStatus = "GD 地址必须以 http:// 或 https:// 开头（当前未保存）。";
            return;
        }
        ConfigService.Current.Online.GdApiUrl = value.Trim();
        ConfigService.Save();
    }

    /// <summary>网易云（ChKSz）API 地址（空 = 官方默认）。改即生效。</summary>
    [ObservableProperty]
    private string _chkszApiUrl = string.Empty;

    partial void OnChkszApiUrlChanged(string value)
    {
        if (_loading) return;
        if (!Player.Core.Online.OnlineUrl.IsHttp(value.Trim()) && !string.IsNullOrWhiteSpace(value))
        {
            KeyStatus = "网易云地址必须以 http:// 或 https:// 开头（当前未保存）。";
            return;
        }
        ConfigService.Current.Online.ChkszApiUrl = value.Trim();
        ConfigService.Save();
    }

    /// <summary>选择下载目录（.NET 8 WPF 自带 OpenFolderDialog）。</summary>
    [RelayCommand]
    private void BrowseDownloadDir()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择下载目录",
            InitialDirectory = string.IsNullOrWhiteSpace(DownloadDir)
                ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
                : DownloadDir
        };
        if (dialog.ShowDialog() == true)
            DownloadDir = dialog.FolderName;
    }
}
