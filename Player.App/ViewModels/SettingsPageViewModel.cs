using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows.Threading;
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

public sealed record BackendOption(OutputBackendKind Kind, string Name)
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
public sealed partial class SettingsPageViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan SaveDebounceDelay = TimeSpan.FromMilliseconds(200);

    private readonly LibraryService _library;
    private readonly IPlaybackEngine _engine;
    private readonly ChkszClient _client;
    private readonly Func<bool, Task> _requestScan;
    private readonly Action? _importM3u;
    private readonly DispatcherTimer _saveDebounceTimer;
    private bool _savePending;
    private bool _disposed;

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
        _saveDebounceTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher.CurrentDispatcher)
        {
            Interval = SaveDebounceDelay
        };
        _saveDebounceTimer.Tick += OnSaveDebounceTick;

        Folders = new ObservableCollection<string>(ConfigService.Current.Library.Folders);

        Backends = new[]
        {
            new BackendOption(OutputBackendKind.Asio, "ASIO"),
            new BackendOption(OutputBackendKind.Wasapi, "WASAPI"),
            new BackendOption(OutputBackendKind.DirectSound, "系统输出")
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

        // P4 实机反馈：在线页 = 通用 API 列表（类型 + 地址 + 可选 Key，可增删，改即生效）
        foreach (var ep in ConfigService.Current.Online.ApiEndpoints)
        {
            var row = new ApiEndpointRow
            {
                Kind = ApiKinds.FirstOrDefault(k => k.Value == ep.Kind) ?? ApiKinds[0],
                Url = ep.Url,
                Key = ep.Key,
                IsKeyRevealed = string.IsNullOrEmpty(ep.Key)
            };
            row.PropertyChanged += OnApiRowChanged;
            ApiEndpoints.Add(row);
        }
        _previewBr = BrOptions.FirstOrDefault(o => o.Value == ConfigService.Current.Online.PreviewBr) ?? BrOptions[0];
        _selectedSection = Sections[0];

        // L3.1 个性化初始值（ui 已在上面歌词组声明）
        _selectedRowHeight = RowHeights.FirstOrDefault(r => r.Value == ui.RowHeight) ?? RowHeights[1];
        _groupsExpandedByDefault = ui.GroupsExpandedByDefault;
        _groupCoverVisible = ui.GroupCoverVisible;
        _selectedUiFont = UiFontOptions.FirstOrDefault(f =>
            string.Equals(f.Family, ui.UiFontFamily, StringComparison.OrdinalIgnoreCase)) ?? UiFontOptions[0];
        _selectedFontScale = FontScales.FirstOrDefault(s => Math.Abs(s.Value - ui.UiFontScale) < 0.001) ?? FontScales[2];
        _selectedAccent = AccentColors.FirstOrDefault(a =>
            string.Equals(a.Color, ui.CustomAccent, StringComparison.OrdinalIgnoreCase)) ?? AccentColors[0];
        _selectedOpacity = ui.SelectedOpacity;
        _hoverOpacity = ui.HoverOpacity;
        _miniOpacity = ui.MiniOpacity;
        _classicMenus = ui.ClassicMenus;
        _selectedLyricSource = LyricSourceOptions.FirstOrDefault(o =>
            string.Equals(o.Value, ui.LyricDefaultPreference, StringComparison.OrdinalIgnoreCase))
            ?? LyricSourceOptions[3];
        RefreshColumnRows();

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

    // ================= 分区导航（L3.0-3：左侧竖排窄导航，五大区） =================

    public sealed record SettingsSection(string Key, string Name)
    {
        public override string ToString() => Name;
    }

    public IReadOnlyList<SettingsSection> Sections { get; } = new[]
    {
        new SettingsSection("appearance", "外观"),
        new SettingsSection("lyrics", "歌词"),
        new SettingsSection("playback", "播放"),
        new SettingsSection("library", "媒体库"),
        new SettingsSection("online", "在线"),
        new SettingsSection("shortcuts", "快捷键"),
        new SettingsSection("system", "系统"),
    };

    [ObservableProperty]
    private SettingsSection _selectedSection = null!;

    public bool IsAppearanceSec => SelectedSection?.Key == "appearance";

    public bool IsLyricsSec => SelectedSection?.Key == "lyrics";

    public bool IsPlaybackSec => SelectedSection?.Key == "playback";

    public bool IsLibrarySec => SelectedSection?.Key == "library";

    public bool IsOnlineSec => SelectedSection?.Key == "online";

    public bool IsShortcutsSec => SelectedSection?.Key == "shortcuts";

    public bool IsSystemSec => SelectedSection?.Key == "system";

    partial void OnSelectedSectionChanged(SettingsSection value)
    {
        OnPropertyChanged(nameof(IsAppearanceSec));
        OnPropertyChanged(nameof(IsLyricsSec));
        OnPropertyChanged(nameof(IsPlaybackSec));
        OnPropertyChanged(nameof(IsLibrarySec));
        OnPropertyChanged(nameof(IsOnlineSec));
        OnPropertyChanged(nameof(IsShortcutsSec));
        OnPropertyChanged(nameof(IsSystemSec));
    }

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

    /// <summary>歌词来源默认值（未单独设置来源的曲目生效）。</summary>
    public sealed record LyricSourceOption(string Value, string Name)
    {
        public override string ToString() => Name;
    }

    public IReadOnlyList<LyricSourceOption> LyricSourceOptions { get; } = new[]
    {
        new LyricSourceOption("Auto", "自动（.lrc＞内嵌＞缓存＞在线）"),
        new LyricSourceOption("LrcFile", "优先本地 .lrc"),
        new LyricSourceOption("Embedded", "优先内嵌标签"),
        new LyricSourceOption("Online", "仅在线（网易云）"),
    };

    [ObservableProperty]
    private LyricSourceOption _selectedLyricSource = null!;

    partial void OnSelectedLyricSourceChanged(LyricSourceOption value)
    {
        if (_loading || value is null) return;
        ConfigService.Current.Ui.LyricDefaultPreference = value.Value;
        ConfigService.Save();
    }

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

    // ================= 在线（P4 实机反馈：通用 API 列表，兼容各种 API、为扩展打基础） =================

    /// <summary>API 类型（运行时用途），新增 API 类型只需在此注册并让对应源读取。</summary>
    public sealed record ApiKindOption(string Value, string Name)
    {
        public override string ToString() => Name;
    }

    public IReadOnlyList<ApiKindOption> ApiKinds { get; } = new[]
    {
        new ApiKindOption("gd", "GD 兼容"),
        new ApiKindOption("chksz", "网易云兼容"),
    };

    /// <summary>一行可编辑的 API 端点（类型 + 地址 + 可选 Key），改动即落盘。</summary>
    public sealed partial class ApiEndpointRow : ObservableObject
    {
        [ObservableProperty]
        private ApiKindOption _kind = null!;

        [ObservableProperty]
        private string _url = string.Empty;

        [ObservableProperty]
        private string _key = string.Empty;

        [ObservableProperty]
        private bool _isKeyRevealed;

        public string MaskedKey => SecretMask.ForDisplay(Key);

        partial void OnKeyChanged(string value) => OnPropertyChanged(nameof(MaskedKey));

        [RelayCommand]
        private void ToggleKeyReveal() => IsKeyRevealed = !IsKeyRevealed;

        public void HideKey() => IsKeyRevealed = false;
    }

    public ObservableCollection<ApiEndpointRow> ApiEndpoints { get; } = new();

    private void OnApiRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(ApiEndpointRow.Kind)
            or nameof(ApiEndpointRow.Url)
            or nameof(ApiEndpointRow.Key)))
            return;

        var debounce = e.PropertyName is nameof(ApiEndpointRow.Url) or nameof(ApiEndpointRow.Key);
        PersistEndpoints(debounce);
    }

    [RelayCommand]
    private void AddApiEndpoint()
    {
        var row = new ApiEndpointRow { Kind = ApiKinds[0], IsKeyRevealed = true };
        row.PropertyChanged += OnApiRowChanged;
        ApiEndpoints.Add(row);
        PersistEndpoints(debounce: false);
    }

    [RelayCommand]
    private void RemoveApiEndpoint(ApiEndpointRow row)
    {
        row.PropertyChanged -= OnApiRowChanged;
        ApiEndpoints.Remove(row);
        PersistEndpoints(debounce: false);
    }

    private void PersistEndpoints(bool debounce)
    {
        if (_loading) return;
        ConfigService.Current.Online.ApiEndpoints = ApiEndpoints
            .Select(r => new Player.Core.Infra.ApiEndpointConfig
            {
                Kind = r.Kind?.Value ?? "gd",
                Url = r.Url.Trim(),
                Key = r.Key.Trim()
            })
            .ToList();
        if (debounce) ScheduleConfigSave();
        else SaveConfigImmediately();
        OnPropertyChanged(nameof(QuotaDisplay));
    }

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

    /// <summary>右键菜单风格：true = 复古原生（Win32 质感），false = WPF-UI 现代。</summary>
    [ObservableProperty]
    private bool _classicMenus;

    partial void OnClassicMenusChanged(bool value)
    {
        if (_loading) return;
        ConfigService.Current.Ui.ClassicMenus = value;
        ConfigService.Save();
        Theming.ThemeService.ApplyUiPersonalization();
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

    // ================= 在线（P4 实机反馈补全：音质档） =================

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

    /// <summary>在线搜索默认音质档。改即生效（下次打开搜索页起用）。</summary>
    [ObservableProperty]
    private BrSettingOption _previewBr = null!;

    partial void OnPreviewBrChanged(BrSettingOption value)
    {
        if (_loading || value is null) return;
        ConfigService.Current.Online.PreviewBr = value.Value;
        ConfigService.Save();
    }

    // ================= L3.1 个性化（外观区：列表 / 列 / 字体 / 颜色） =================

    // ---- 列表 ----

    public sealed record RowHeightOption(int Value, string Name)
    {
        public override string ToString() => Name;
    }

    public IReadOnlyList<RowHeightOption> RowHeights { get; } = new[]
    {
        new RowHeightOption(56, "紧凑"),
        new RowHeightOption(72, "标准"),
        new RowHeightOption(110, "舒适"),
    };

    [ObservableProperty]
    private RowHeightOption _selectedRowHeight = null!;

    partial void OnSelectedRowHeightChanged(RowHeightOption value)
    {
        if (_loading || value is null) return;
        ConfigService.Current.Ui.RowHeight = value.Value;
        ConfigService.Save();
        Theming.ThemeService.ApplyUiPersonalization();
    }

    [ObservableProperty]
    private bool _groupsExpandedByDefault;

    partial void OnGroupsExpandedByDefaultChanged(bool value)
    {
        if (_loading) return;
        ConfigService.Current.Ui.GroupsExpandedByDefault = value;
        ConfigService.Save();
    }

    [ObservableProperty]
    private bool _groupCoverVisible;

    partial void OnGroupCoverVisibleChanged(bool value)
    {
        if (_loading) return;
        ConfigService.Current.Ui.GroupCoverVisible = value;
        ConfigService.Save();
    }

    // ---- 列 ----

    public sealed partial class ColumnSettingRow : ObservableObject
    {
        public required string Key { get; init; }

        public required string Name { get; init; }

        [ObservableProperty]
        private bool _visible;

        [ObservableProperty]
        private double _width;
    }

    public ObservableCollection<ColumnSettingRow> ColumnRows { get; } = new();

    public void RefreshColumnRows()
    {
        foreach (var oldRow in ColumnRows)
            oldRow.PropertyChanged -= OnColumnRowChanged;
        ColumnRows.Clear();
        var cols = ConfigService.Current.Ui.Columns;
        foreach (var col in TrackListPageViewModel.TrackColumns)
        {
            var row = new ColumnSettingRow
            {
                Key = col.Key,
                Name = col.Name,
                Visible = cols.Contains(col.Key),
                Width = ConfigService.Current.Ui.ColumnWidths.TryGetValue(col.Key, out var w) ? w : col.DefaultWidth
            };
            row.PropertyChanged += OnColumnRowChanged;
            ColumnRows.Add(row);
        }
    }

    private void OnColumnRowChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not ColumnSettingRow row) return;
        ApplyColumnRow(row, debounce: e.PropertyName == nameof(ColumnSettingRow.Width));
    }

    /// <summary>列的显示/宽度/顺序变化 → 落盘并刷新当前列表页。</summary>
    private void ApplyColumnRow(ColumnSettingRow row, bool debounce)
    {
        if (_loading) return;
        ConfigService.Current.Ui.ColumnWidths[row.Key] = row.Width;
        if (!row.Visible) ConfigService.Current.Ui.Columns.Remove(row.Key);
        else if (!ConfigService.Current.Ui.Columns.Contains(row.Key))
        {
            // 按默认顺序插回
            var defs = TrackListPageViewModel.TrackColumns.Select(c => c.Key).ToList();
            var insertAt = defs.Count;
            for (var i = 0; i < defs.Count; i++)
            {
                if (defs[i] == row.Key) { insertAt = i; break; }
            }
            ConfigService.Current.Ui.Columns.Insert(Math.Min(insertAt, ConfigService.Current.Ui.Columns.Count), row.Key);
        }
        if (debounce) ScheduleConfigSave();
        else SaveConfigImmediately();
    }

    /// <summary>列上移/下移（设置页按钮）。</summary>
    [RelayCommand]
    private void MoveColumnUp(ColumnSettingRow row)
    {
        MoveColumnRow(row, -1);
    }

    [RelayCommand]
    private void MoveColumnDown(ColumnSettingRow row)
    {
        MoveColumnRow(row, +1);
    }

    private void MoveColumnRow(ColumnSettingRow row, int delta)
    {
        var cols = ConfigService.Current.Ui.Columns;
        var idx = cols.IndexOf(row.Key);
        if (idx < 0) return;
        var target = idx + delta;
        if (target < 0 || target >= cols.Count) return;
        cols.RemoveAt(idx);
        cols.Insert(target, row.Key);
        SaveConfigImmediately();
        RefreshColumnRows();
    }

    // ---- 恢复默认外观（L3.1；预设已按用户意见删除，留待以后版本做主题） ----

    /// <summary>恢复全部外观设置到 P4 收官时的默认样子（行高 72/分组展开/无组头封面/
    /// 字体默认/字号 100%/强调色跟随封面/默认透明度/8 列默认顺序与默认列宽）。
    /// 2026-08 实机反馈：窗口尺寸也一并回默认 1400×900（此前没有恢复入口）。</summary>
    [RelayCommand]
    private void ResetAppearance()
    {
        if (_loading) return;
        var ui = ConfigService.Current.Ui;
        ui.RowHeight = 72;
        ui.GroupsExpandedByDefault = true;
        ui.GroupCoverVisible = false;   // P4 时代组头就是细行，无封面
        ui.UiFontFamily = string.Empty;
        ui.UiFontScale = 1.0;
        ui.CustomAccent = string.Empty;
        ui.SelectedOpacity = 0.12;
        ui.HoverOpacity = 0.07;
        ui.MiniOpacity = 1.0;
        ui.Columns = TrackListPageViewModel.TrackColumns.Select(c => c.Key).ToList();
        ui.ColumnWidths.Clear();

        // 窗口几何回默认：解除最大化并回 1400×900。位置不动，只重置尺寸与状态；
        // MainWindow 内部已把新值写进 ui.WindowWidth/Height/Maximized，随后统一落盘。
        if (System.Windows.Application.Current?.MainWindow is MainWindow mw)
            mw.ResetWindowGeometryToDefault();

        SaveConfigImmediately();
        Theming.ThemeService.ApplyUiPersonalization();
        Theming.ThemeService.ApplyModeFromConfig();
        ReloadUiProperties();
    }

    /// <summary>重读配置刷新全部外观属性（预设/恢复默认后设置页控件同步）。</summary>
    private void ReloadUiProperties()
    {
        _loading = true;
        try
        {
            var ui = ConfigService.Current.Ui;
            SelectedRowHeight = RowHeights.FirstOrDefault(r => r.Value == ui.RowHeight) ?? RowHeights[1];
            GroupsExpandedByDefault = ui.GroupsExpandedByDefault;
            GroupCoverVisible = ui.GroupCoverVisible;
            SelectedUiFont = UiFontOptions.FirstOrDefault(f =>
                string.Equals(f.Family, ui.UiFontFamily, StringComparison.OrdinalIgnoreCase)) ?? UiFontOptions[0];
            SelectedFontScale = FontScales.FirstOrDefault(s => Math.Abs(s.Value - ui.UiFontScale) < 0.001) ?? FontScales[2];
            SelectedAccent = AccentColors.FirstOrDefault(a =>
                string.Equals(a.Color, ui.CustomAccent, StringComparison.OrdinalIgnoreCase)) ?? AccentColors[0];
            SelectedOpacity = ui.SelectedOpacity;
            HoverOpacity = ui.HoverOpacity;
            MiniOpacity = ui.MiniOpacity;
            ClassicMenus = ui.ClassicMenus;
            SelectedLyricSource = LyricSourceOptions.FirstOrDefault(o =>
                string.Equals(o.Value, ui.LyricDefaultPreference, StringComparison.OrdinalIgnoreCase))
                ?? LyricSourceOptions[3];
            RefreshColumnRows();
        }
        finally
        {
            _loading = false;
        }
    }


    // ---- 字体 ----

    public sealed record FontOption(string Name, string Family)
    {
        public override string ToString() => Name;
    }

    public IReadOnlyList<FontOption> UiFontOptions { get; } = new[]
    {
        new FontOption("默认（跟随系统）", string.Empty),
        new FontOption("微软雅黑", "Microsoft YaHei UI"),
        new FontOption("Segoe UI", "Segoe UI"),
        new FontOption("思源黑体", "Source Han Sans SC"),
        new FontOption("Noto Sans SC", "Noto Sans SC"),
        new FontOption("宋体", "SimSun"),
    };

    [ObservableProperty]
    private FontOption _selectedUiFont = null!;

    partial void OnSelectedUiFontChanged(FontOption value)
    {
        if (_loading || value is null) return;
        ConfigService.Current.Ui.UiFontFamily = value.Family;
        ConfigService.Save();
        Theming.ThemeService.ApplyUiPersonalization();
    }

    public sealed record ScaleOption(double Value, string Name)
    {
        public override string ToString() => Name;
    }

    public IReadOnlyList<ScaleOption> FontScales { get; } = new[]
    {
        new ScaleOption(0.90, "90%"), new ScaleOption(0.95, "95%"), new ScaleOption(1.00, "100%"),
        new ScaleOption(1.05, "105%"), new ScaleOption(1.10, "110%"), new ScaleOption(1.15, "115%"),
        new ScaleOption(1.20, "120%"), new ScaleOption(1.25, "125%"),
    };

    [ObservableProperty]
    private ScaleOption _selectedFontScale = null!;

    partial void OnSelectedFontScaleChanged(ScaleOption value)
    {
        if (_loading || value is null) return;
        ConfigService.Current.Ui.UiFontScale = value.Value;
        ConfigService.Save();
        Theming.ThemeService.ApplyUiPersonalization();
    }

    // ---- 颜色 ----

    public sealed record AccentOption(string Color, string Name)
    {
        public override string ToString() => Name;
    }

    public IReadOnlyList<AccentOption> AccentColors { get; } = new[]
    {
        new AccentOption(string.Empty, "跟随封面/默认"),
        new AccentOption("#B87A00", "琥珀金"),
        new AccentOption("#D35400", "落日橙"),
        new AccentOption("#E91E63", "樱粉"),
        new AccentOption("#9C27B0", "紫罗兰"),
        new AccentOption("#3F51B5", "靛蓝"),
        new AccentOption("#0288D1", "天蓝"),
        new AccentOption("#00897B", "青绿"),
        new AccentOption("#43A047", "草绿"),
        new AccentOption("#757575", "中性灰"),
    };

    [ObservableProperty]
    private AccentOption _selectedAccent = null!;

    partial void OnSelectedAccentChanged(AccentOption value)
    {
        if (_loading || value is null) return;
        ConfigService.Current.Ui.CustomAccent = value.Color;
        ConfigService.Save();
        Theming.ThemeService.ApplyModeFromConfig();
    }

    [ObservableProperty]
    private double _selectedOpacity;

    partial void OnSelectedOpacityChanged(double value)
    {
        if (_loading) return;
        ConfigService.Current.Ui.SelectedOpacity = value;
        ScheduleConfigSave();
        Theming.ThemeService.ApplyModeFromConfig();
    }

    [ObservableProperty]
    private double _hoverOpacity;

    partial void OnHoverOpacityChanged(double value)
    {
        if (_loading) return;
        ConfigService.Current.Ui.HoverOpacity = value;
        ScheduleConfigSave();
        Theming.ThemeService.ApplyModeFromConfig();
    }

    /// <summary>迷你悬浮窗整体不透明度。不进主题管线，由悬浮窗自己应用。</summary>
    [ObservableProperty]
    private double _miniOpacity;

    partial void OnMiniOpacityChanged(double value)
    {
        if (_loading) return;
        ConfigService.Current.Ui.MiniOpacity = Math.Clamp(value, 0.35, 1.0);
        ScheduleConfigSave();
    }

    private void ScheduleConfigSave()
    {
        if (_loading || _disposed) return;
        _savePending = true;
        _saveDebounceTimer.Stop();
        _saveDebounceTimer.Start();
    }

    private void OnSaveDebounceTick(object? sender, EventArgs e) => FlushPendingSave();

    private void SaveConfigImmediately()
    {
        _saveDebounceTimer.Stop();
        _savePending = false;
        ConfigService.Save();
    }

    private void FlushPendingSave()
    {
        _saveDebounceTimer.Stop();
        if (!_savePending) return;
        _savePending = false;
        ConfigService.Save();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _saveDebounceTimer.Tick -= OnSaveDebounceTick;
        FlushPendingSave();

        foreach (var row in ApiEndpoints)
        {
            row.HideKey();
            row.PropertyChanged -= OnApiRowChanged;
        }
        foreach (var row in ColumnRows)
            row.PropertyChanged -= OnColumnRowChanged;
    }
}
