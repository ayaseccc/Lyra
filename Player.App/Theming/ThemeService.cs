using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Player.Core.Infra;
using Player.Core.Theming;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;

namespace Player.App.Theming;

/// <summary>
/// UI-R3 封面取色主题引擎（应用侧）：
/// 底色两挡（深色/浅色）× 染色开关（是否随封面），四组合统一适配全部页面；
/// WPF-UI 控件主题（Light/Dark）与 Accent 强调色随模式联动，保证整窗统一。
/// 切歌/切模式 300ms 缓动过渡。
/// </summary>
public static class ThemeService
{
    private const int SampleSize = 32;
    private const double TransitionMs = 300;
    private const double TickMs = 30;

    // WPF-UI 控件强调色资源键（染色时覆盖，让按钮/选中态跟随封面强调色）
    private static readonly string[] AccentKeys =
    {
        "AccentFillColorDefaultBrush",
        "AccentFillColorSecondaryBrush",
        "AccentFillColorTertiaryBrush",
        "AccentTextFillColorPrimaryBrush"
    };

    private static DispatcherTimer _timer = null!;
    private static ThemePalette _current = ThemePalette.FixedDark;
    private static ThemePalette _target = ThemePalette.FixedDark;
    private static double _progress = 1.0;
    private static bool _initialized;
    private static string? _lastCoverHash;
    private static long _paletteRequestVersion;

    // 取色只读 32x32 像素，但仍会触发磁盘读取和大数组量化；按封面+底色缓存，
    // 并合并同一 key 的并发请求，避免快速切歌时重复工作。
    private static readonly ConcurrentDictionary<string, ThemePalette> PaletteCache =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, Task<ThemePalette>> PaletteLoads =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, SolidColorBrush> MutableBrushes =
        new(StringComparer.Ordinal);
    private const int PaletteCacheLimit = 256;

    /// <summary>深色 / 浅色基底。</summary>
    public static bool DarkBase { get; private set; }

    /// <summary>是否随封面染色。</summary>
    public static bool Tint { get; private set; }

    static ThemeService()
    {
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TickMs) };
        _timer.Tick += OnTick;
    }

    /// <summary>启动时调用：按配置应用主题。跟随染色时先维持默认，取到封面后再过渡。</summary>
    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        var ui = ConfigService.Current.Ui;
        // 迁移：旧 ThemeMode（FollowCover/FixedDark）→ 新双字段
        if (string.IsNullOrEmpty(ui.ThemeBase))
        {
            if (string.Equals(ui.ThemeMode, "FixedDark", StringComparison.OrdinalIgnoreCase))
            {
                ui.ThemeBase = "Dark";
                ui.ThemeTint = false;
            }
            else
            {
                ui.ThemeBase = "Light";
                ui.ThemeTint = true;
            }
        }

        DarkBase = string.Equals(ui.ThemeBase, "Dark", StringComparison.OrdinalIgnoreCase);
        Tint = ui.ThemeTint;

        ApplicationThemeManager.Apply(
            DarkBase ? ApplicationTheme.Dark : ApplicationTheme.Light,
            WindowBackdropType.Mica,
            updateAccent: false);

        ApplyMiniPlayerResources();

        // Player.xaml intentionally contains a dark emergency fallback so XAML can
        // load before configuration. Always replace it with the configured base at
        // startup, including cover-tint mode where no cover may ever arrive.
        var palette = NoCoverPalette(DarkBase, Tint);
        ApplyPalette(palette);
        _current = palette;
        _target = palette;
        _progress = 1.0;
    }

    /// <summary>L3.1 个性化应用到 Application 资源：行高/全局字体/字号缩放。
    /// 启动与设置变化时调用；XAML 样式用 DynamicResource 引用。</summary>
    public static void ApplyUiPersonalization()
    {
        if (Application.Current is not { } app) return;
        var ui = ConfigService.Current.Ui;
        var res = app.Resources;

        res["RowHeightFlat"] = (double)Math.Clamp(ui.RowHeight, 40, 160);
        res["RowHeightGrouped"] = (double)Math.Clamp(ui.RowHeight, 40, 160);

        var scale = Math.Clamp(ui.UiFontScale, 0.9, 1.25);
        res["UiFontFamily"] = string.IsNullOrWhiteSpace(ui.UiFontFamily)
            ? SystemFonts.MessageFontFamily
            : new FontFamily(ui.UiFontFamily);
        res["UiFontSizePage"] = 20 * scale;
        res["UiFontSizeTitle"] = 14 * scale;
        res["UiFontSizeBody"] = 12 * scale;
        res["UiFontSizeSmall"] = 11 * scale;

        // 右键菜单风格：经典（keyed 样式）或 null（回退 WPF-UI 现代隐式样式）
        res["MenuStyleOverride"] = ui.ClassicMenus
            ? (Style?)app.Resources["ClassicContextMenuStyle"]
            : null;
        res["SeparatorStyleOverride"] = ui.ClassicMenus
            ? (Style?)app.Resources["ClassicSeparatorStyle"]
            : null;
    }

    /// <summary>按当前配置重新应用主题（L3.1 自定义强调色/透明度变化后调用）。</summary>
    public static void ApplyModeFromConfig()
    {
        Initialize();
        // 个性化参数已经写入内存配置。这里只刷新当前视觉帧，不重复保存配置、
        // 重跑封面取色或重置 300ms 主题动画。
        var palette = _timer.IsEnabled
            ? Lerp(_current, _target, EaseOut(_progress))
            : _current;
        if (!_timer.IsEnabled && Tint && _lastCoverHash is null)
        {
            // 染色模式尚未有当前曲目时，_current 仍可能是静态初始化值；
            // 用与 SetMode 相同的无封面回退，避免设置页滑块把浅色底误刷成深色。
            palette = NoCoverPalette(DarkBase, tint: true);
            _current = _target = palette;
            _progress = 1.0;
        }
        ApplyPalette(palette);
    }

    /// <summary>设置页切换：底色（深/浅）× 染色（开/关）。</summary>
    public static void SetMode(bool darkBase, bool tint)
    {
        Initialize();

        DarkBase = darkBase;
        Tint = tint;
        var requestVersion = Interlocked.Increment(ref _paletteRequestVersion);
        ConfigService.Current.Ui.ThemeBase = darkBase ? "Dark" : "Light";
        ConfigService.Current.Ui.ThemeTint = tint;
        ConfigService.Save();

        ApplicationThemeManager.Apply(
            darkBase ? ApplicationTheme.Dark : ApplicationTheme.Light,
            WindowBackdropType.Mica,
            updateAccent: false);

        ApplyMiniPlayerResources();

        if (!tint)
        {
            // 关闭染色是一个明确的逃生路径：停止旧动画、取消旧的异步提交，
            // 不读取当前封面，也不等待任何后台任务。
            _timer.Stop();
            var fixedPalette = NoCoverPalette(darkBase, tint: false);
            _current = fixedPalette;
            _target = fixedPalette;
            _progress = 1.0;
            ApplyPalette(fixedPalette);
            return;
        }

        var coverHash = _lastCoverHash;
        if (string.IsNullOrWhiteSpace(coverHash))
        {
            AnimateTo(NoCoverPalette(darkBase, tint: true));
            return;
        }

        RequestCoverPalette(coverHash, darkBase, requestVersion);
    }

    /// <summary>切歌钩子（PlayerViewModel.ApplyTrackDisplay 调用）。</summary>
    public static void OnTrackChanged(string? coverHash)
    {
        if (!_initialized) Initialize();
        _lastCoverHash = coverHash;

        // 关闭染色时不应为每次切歌读取/解码封面；SetMode 已经应用固定调色板。
        if (!Tint) return;

        var requestVersion = Interlocked.Increment(ref _paletteRequestVersion);
        if (string.IsNullOrWhiteSpace(coverHash))
        {
            var fallback = NoCoverPalette(DarkBase, tint: true);
            AnimateTo(fallback);
            return;
        }

        RequestCoverPalette(coverHash, DarkBase, requestVersion);
    }

    private static void RequestCoverPalette(string coverHash, bool darkBase, long requestVersion)
    {
        var key = CacheKey(coverHash, darkBase);
        if (PaletteCache.TryGetValue(key, out var cached))
        {
            ApplyCoverPaletteIfCurrent(coverHash, darkBase, requestVersion, cached);
            return;
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        var load = PaletteLoads.GetOrAdd(key, _ => Task.Run(() => DeriveFromCover(coverHash, darkBase)));
        _ = ApplyLoadedPaletteAsync(load, key, coverHash, darkBase, requestVersion, dispatcher);
    }

    private static async Task ApplyLoadedPaletteAsync(
        Task<ThemePalette> load,
        string key,
        string coverHash,
        bool darkBase,
        long requestVersion,
        System.Windows.Threading.Dispatcher dispatcher)
    {
        try
        {
            var palette = await load.ConfigureAwait(false);
            AddPaletteCache(key, palette);
            if (dispatcher.HasShutdownStarted || dispatcher.HasShutdownFinished) return;

            _ = dispatcher.BeginInvoke(new Action(() =>
                ApplyCoverPaletteIfCurrent(coverHash, darkBase, requestVersion, palette)));
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "封面取色失败，使用固定回退色（Hash 已省略）");
        }
        finally
        {
            PaletteLoads.TryRemove(key, out _);
        }
    }

    private static void ApplyCoverPaletteIfCurrent(
        string coverHash,
        bool darkBase,
        long requestVersion,
        ThemePalette palette)
    {
        if (requestVersion != Volatile.Read(ref _paletteRequestVersion)
            || !Tint
            || DarkBase != darkBase
            || !string.Equals(_lastCoverHash, coverHash, StringComparison.OrdinalIgnoreCase))
            return;

        Serilog.Log.Information("主题：深色={Dark} 封面已应用 → 背景 {Bg} 表面 {Surface} 强调 {Accent} 文字 {Text}",
            darkBase, palette.Background, palette.Surface, palette.Accent, palette.TextPrimary);
        AnimateTo(palette);
    }

    private static string CacheKey(string coverHash, bool darkBase) =>
        (darkBase ? "D:" : "L:") + coverHash;

    private static void AddPaletteCache(string key, ThemePalette palette)
    {
        PaletteCache[key] = palette;
        if (PaletteCache.Count <= PaletteCacheLimit) return;

        // 主题缓存不是音频数据，偶尔移除一个任意旧项即可；不在 UI 线程做全量 Clear。
        foreach (var old in PaletteCache.Keys)
        {
            if (!string.Equals(old, key, StringComparison.OrdinalIgnoreCase))
            {
                PaletteCache.TryRemove(old, out _);
                break;
            }
        }
    }

    private static ThemePalette DeriveFromCover(string coverHash, bool darkBase)
    {
        try
        {
            var pixels = LoadSamplePixels(coverHash);
            if (pixels.Count == 0) return NoCoverPalette(darkBase, tint: true);
            var colors = CoverColorExtractor.Extract(pixels);
            return darkBase ? ThemeDeriver.DeriveDark(colors.Main) : ThemeDeriver.DeriveLight(colors.Main);
        }
        catch
        {
            return NoCoverPalette(darkBase, tint: true);
        }
    }

    private static ThemePalette NoCoverPalette(bool darkBase, bool tint)
    {
        if (!darkBase) return ThemeDeriver.NeutralFallback();
        return tint
            ? ThemeDeriver.DeriveDark(new RgbColor(0x30, 0x40, 0x60))
            : ThemePalette.FixedDark;
    }

    private static IReadOnlyList<RgbColor> LoadSamplePixels(string coverHash)
    {
        var path = Path.Combine(AppPaths.CoversDir, coverHash + ".jpg");
        if (!File.Exists(path)) return Array.Empty<RgbColor>();

        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = new Uri(path);
        image.DecodePixelWidth = SampleSize;
        image.DecodePixelHeight = SampleSize;
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        image.EndInit();

        var converted = new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        var stride = SampleSize * 4;
        var bytes = new byte[stride * SampleSize];
        converted.CopyPixels(bytes, stride, 0);

        var pixels = new List<RgbColor>(SampleSize * SampleSize);
        for (var i = 0; i < SampleSize * SampleSize; i++)
        {
            var o = i * 4;
            pixels.Add(new RgbColor(bytes[o + 2], bytes[o + 1], bytes[o]));
        }
        return pixels;
    }

    public static void AnimateTo(ThemePalette target)
    {
        _target = target;
        _progress = 0.0;
        if (!_timer.IsEnabled) _timer.Start();
    }

    private static void OnTick(object? sender, EventArgs e)
    {
        _progress = Math.Min(1.0, _progress + TickMs / TransitionMs);
        var t = EaseOut(_progress);
        ApplyPalette(Lerp(_current, _target, t));

        if (_progress >= 1.0)
        {
            _current = _target;
            _timer.Stop();
        }
    }

    private static double EaseOut(double t) => 1.0 - (1.0 - t) * (1.0 - t);

    private static ThemePalette Lerp(ThemePalette from, ThemePalette to, double t) => new(
        Lerp(from.Background, to.Background, t),
        Lerp(from.Surface, to.Surface, t),
        Lerp(from.SurfaceStrong, to.SurfaceStrong, t),
        Lerp(from.TextPrimary, to.TextPrimary, t),
        Lerp(from.TextSecondary, to.TextSecondary, t),
        Lerp(from.TextTertiary, to.TextTertiary, t),
        Lerp(from.Accent, to.Accent, t),
        Lerp(from.Hover, to.Hover, t),
        Lerp(from.Selected, to.Selected, t),
        Lerp(from.BorderSoft, to.BorderSoft, t),
        Lerp(from.TrackEmpty, to.TrackEmpty, t),
        Lerp(from.VolumeReached, to.VolumeReached, t),
        Lerp(from.VolumeSlot, to.VolumeSlot, t));

    private static RgbColor Lerp(RgbColor a, RgbColor b, double t) => new(
        (byte)Math.Round(a.R + (b.R - a.R) * t),
        (byte)Math.Round(a.G + (b.G - a.G) * t),
        (byte)Math.Round(a.B + (b.B - a.B) * t),
        (byte)Math.Round(a.A + (b.A - a.A) * t));

    /// <summary>L3.1 个性化：自定义强调色 + 选中/悬停透明度（读配置，每帧应用动画中间帧也一致）。</summary>
    private static ThemePalette Personalize(ThemePalette p)
    {
        var ui = ConfigService.Current.Ui;

        if (!string.IsNullOrWhiteSpace(ui.CustomAccent)
            && TryParseHex(ui.CustomAccent, out var accent))
        {
            p = p with { Accent = accent };
        }

        var hoverA = (byte)Math.Clamp((int)Math.Round(255 * ui.HoverOpacity), 0, 255);
        var selectedA = (byte)Math.Clamp((int)Math.Round(255 * ui.SelectedOpacity), 0, 255);
        p = p with
        {
            Hover = p.Hover with { A = hoverA },
            Selected = p.Selected with { A = selectedA }
        };
        return p;
    }

    private static bool TryParseHex(string text, out RgbColor color)
    {
        color = default;
        var hex = text.Trim().TrimStart('#');
        if (hex.Length != 6 || !byte.TryParse(hex[..2], System.Globalization.NumberStyles.HexNumber, null, out var r)
            || !byte.TryParse(hex[2..4], System.Globalization.NumberStyles.HexNumber, null, out var g)
            || !byte.TryParse(hex[4..6], System.Globalization.NumberStyles.HexNumber, null, out var b))
            return false;
        color = new RgbColor(r, g, b);
        return true;
    }

    /// <summary>把调色板写进 Application 资源（XAML 全部用 DynamicResource 引用）。</summary>
    private static void ApplyPalette(ThemePalette p)
    {
        p = Personalize(p);
        var resources = Application.Current.Resources;
        Set(resources, "SurfaceBrush", p.Surface);
        Set(resources, "SurfaceStrongBrush", p.SurfaceStrong);
        resources["SurfaceStrongColor"] = Color.FromArgb(p.SurfaceStrong.A, p.SurfaceStrong.R, p.SurfaceStrong.G, p.SurfaceStrong.B);
        Set(resources, "TextPrimaryBrush", p.TextPrimary);
        Set(resources, "TextSecondaryBrush", p.TextSecondary);
        Set(resources, "TextTertiaryBrush", p.TextTertiary);
        Set(resources, "AccentBrush", p.Accent);
        Set(resources, "HoverBrush", p.Hover);
        Set(resources, "SelectedBrush", p.Selected);
        Set(resources, "BorderSoftBrush", p.BorderSoft);
        Set(resources, "TrackEmptyBrush", p.TrackEmpty);
        Set(resources, "VolumeReachedBrush", p.VolumeReached);
        Set(resources, "VolumeSlotBrush", p.VolumeSlot);

        // 染色时覆盖 WPF-UI 控件强调色，保证按钮/选中态与整体一致
        if (Tint)
        {
            Set(resources, "AccentFillColorDefaultBrush", p.Accent);
            Set(resources, "AccentFillColorSecondaryBrush", p.Accent);
            Set(resources, "AccentFillColorTertiaryBrush", p.Accent);
            Set(resources, "AccentTextFillColorPrimaryBrush", p.TextPrimary);
        }
        else
        {
            foreach (var key in AccentKeys)
                resources.Remove(key);
        }
    }

    private static void ApplyMiniPlayerResources()
    {
        if (Application.Current is not { } app) return;
        ApplyMiniPlayerResources(app.Resources);
    }

    private static void ApplyMiniPlayerResources(ResourceDictionary resources)
    {
        // 迷你窗只跟随明暗基底；封面取色与主界面自定义强调色都不会进入这些资源。
        var surface = DarkBase
            ? new RgbColor(0x20, 0x25, 0x22)
            : new RgbColor(0xF7, 0xF8, 0xF5);
        var coverSurface = DarkBase
            ? new RgbColor(0x18, 0x22, 0x1D)
            : new RgbColor(0xE5, 0xEB, 0xE6);
        var text = DarkBase
            ? new RgbColor(0xF2, 0xF5, 0xF2)
            : new RgbColor(0x17, 0x1B, 0x18);
        var secondary = DarkBase
            ? new RgbColor(0xB7, 0xC0, 0xBA)
            : new RgbColor(0x62, 0x6B, 0x65);
        var hover = DarkBase
            ? new RgbColor(0x30, 0x3A, 0x34)
            : new RgbColor(0xE8, 0xEC, 0xE8);
        var border = DarkBase
            ? new RgbColor(0x3B, 0x46, 0x3F)
            : new RgbColor(0xD6, 0xDD, 0xD7);
        var accent = DarkBase
            ? new RgbColor(0xF0, 0xB4, 0x29)
            : new RgbColor(0xA9, 0x76, 0x00);
        var accentForeground = DarkBase
            ? new RgbColor(0x17, 0x1B, 0x18)
            : new RgbColor(0xFF, 0xFF, 0xFF);

        Set(resources, "MiniPlayerSurfaceBrush", surface);
        Set(resources, "MiniPlayerCoverSurfaceBrush", coverSurface);
        Set(resources, "MiniPlayerTextBrush", text);
        Set(resources, "MiniPlayerSecondaryBrush", secondary);
        Set(resources, "MiniPlayerHoverBrush", hover);
        Set(resources, "MiniPlayerBorderBrush", border);
        Set(resources, "MiniPlayerAccentBrush", accent);
        Set(resources, "MiniPlayerAccentForegroundBrush", accentForeground);
    }

    private static void Set(ResourceDictionary resources, string key, RgbColor c)
    {
        var color = Color.FromArgb(c.A, c.R, c.G, c.B);
        if (!MutableBrushes.TryGetValue(key, out var brush) || brush.IsFrozen)
        {
            brush = new SolidColorBrush(color);
            MutableBrushes[key] = brush;
            resources[key] = brush;
            return;
        }

        if (brush.Color != color) brush.Color = color;

        // 染色关闭时会移除 WPF-UI 的 Accent 覆盖。再次开启只需把自有画刷
        // 放回本地资源；绝不修改合并字典里的 WPF-UI 回退画刷。
        if (!resources.Contains(key) || !ReferenceEquals(resources[key], brush))
            resources[key] = brush;
    }
}
