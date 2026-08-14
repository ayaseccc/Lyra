using System.IO;
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

        if (!Tint)
        {
            var palette = DarkBase ? ThemePalette.FixedDark : ThemeDeriver.NeutralFallback();
            ApplyPalette(palette);
            _current = palette;
            _target = palette;
        }
    }

    /// <summary>设置页切换：底色（深/浅）× 染色（开/关）。</summary>
    public static void SetMode(bool darkBase, bool tint)
    {
        Initialize();

        DarkBase = darkBase;
        Tint = tint;
        ConfigService.Current.Ui.ThemeBase = darkBase ? "Dark" : "Light";
        ConfigService.Current.Ui.ThemeTint = tint;
        ConfigService.Save();

        ApplicationThemeManager.Apply(
            darkBase ? ApplicationTheme.Dark : ApplicationTheme.Light,
            WindowBackdropType.Mica,
            updateAccent: false);

        var palette = tint
            ? (_lastCoverHash is null
                ? (darkBase ? ThemeDeriver.DeriveDark(new RgbColor(0x30, 0x40, 0x60)) : ThemeDeriver.NeutralFallback())
                : DeriveFromCover(_lastCoverHash))
            : (darkBase ? ThemePalette.FixedDark : ThemeDeriver.NeutralFallback());

        AnimateTo(palette);
    }

    /// <summary>切歌钩子（PlayerViewModel.ApplyTrackDisplay 调用）。</summary>
    public static void OnTrackChanged(string? coverHash)
    {
        if (!_initialized) Initialize();
        _lastCoverHash = coverHash;
        if (!Tint) return;

        AnimateTo(coverHash is null
            ? (DarkBase ? ThemePalette.FixedDark : ThemeDeriver.NeutralFallback())
            : DeriveFromCover(coverHash));
    }

    private static ThemePalette DeriveFromCover(string coverHash)
    {
        try
        {
            var pixels = LoadSamplePixels(coverHash);
            if (pixels.Count == 0) return DarkBase ? ThemePalette.FixedDark : ThemeDeriver.NeutralFallback();
            var colors = CoverColorExtractor.Extract(pixels);
            return DarkBase ? ThemeDeriver.DeriveDark(colors.Main) : ThemeDeriver.DeriveLight(colors.Main);
        }
        catch
        {
            return DarkBase ? ThemePalette.FixedDark : ThemeDeriver.NeutralFallback();
        }
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

    /// <summary>把调色板写进 Application 资源（XAML 全部用 DynamicResource 引用）。</summary>
    private static void ApplyPalette(ThemePalette p)
    {
        var resources = Application.Current.Resources;
        Set(resources, "SurfaceBrush", p.Surface);
        Set(resources, "SurfaceStrongBrush", p.SurfaceStrong);
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
            var accentBrush = new SolidColorBrush(Color.FromArgb(p.Accent.A, p.Accent.R, p.Accent.G, p.Accent.B));
            accentBrush.Freeze();
            var accentTextBrush = new SolidColorBrush(Color.FromArgb(p.TextPrimary.A, p.TextPrimary.R, p.TextPrimary.G, p.TextPrimary.B));
            accentTextBrush.Freeze();
            resources["AccentFillColorDefaultBrush"] = accentBrush;
            resources["AccentFillColorSecondaryBrush"] = accentBrush;
            resources["AccentFillColorTertiaryBrush"] = accentBrush;
            resources["AccentTextFillColorPrimaryBrush"] = accentTextBrush;
        }
        else
        {
            foreach (var key in AccentKeys)
                resources.Remove(key);
        }
    }

    private static void Set(ResourceDictionary resources, string key, RgbColor c)
    {
        var brush = new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B));
        brush.Freeze();
        resources[key] = brush;
    }
}
