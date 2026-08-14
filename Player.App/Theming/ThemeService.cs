using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Player.Core.Infra;
using Player.Core.Theming;

namespace Player.App.Theming;

/// <summary>
/// UI-R3 封面取色主题引擎（应用侧）：切歌 → 取封面主色 → 派生调色板 →
/// 300ms 平滑过渡到界面。设置页两挡：跟随封面（默认）/ 固定深色（逃生口）。
/// 主题色通过 Application 资源的 DynamicResource 刷子即时生效。
/// </summary>
public static class ThemeService
{
    private const int SampleSize = 32;             // 缩样尺寸（定稿：~32×32）
    private const double TransitionMs = 300;       // 切歌过渡时长（定稿）
    private const double TickMs = 30;              // 过渡步长

    // 窗口背景为 Mica 材质（系统主题跟随），面板/侧栏/播放条用 Surface 系浅 tint；
    // 窗口内容区不设独立背景刷子，Mica 本身就是“浅 tint 背景”。
    private static DispatcherTimer? _timer;
    private static ThemePalette _current = ThemePalette.FixedDark;
    private static ThemePalette _target = ThemePalette.FixedDark;
    private static double _progress = 1.0;
    private static bool _initialized;
    private static string? _lastCoverHash;

    public static bool FollowCover { get; private set; } = true;

    /// <summary>启动时调用：按配置应用主题（跟随封面时先维持深色，取到封面后再过渡）。</summary>
    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        FollowCover = !string.Equals(ConfigService.Current.Ui.ThemeMode, "FixedDark", StringComparison.OrdinalIgnoreCase);
        if (!FollowCover)
        {
            ApplyPalette(ThemePalette.FixedDark);
            _current = ThemePalette.FixedDark;
            _target = ThemePalette.FixedDark;
        }
    }

    /// <summary>设置页切换主题模式（跟随封面 / 固定深色）。</summary>
    public static void SetMode(bool followCover)
    {
        FollowCover = followCover;
        ConfigService.Current.Ui.ThemeMode = followCover ? "FollowCover" : "FixedDark";
        ConfigService.Save();

        if (followCover)
        {
            // 跟随封面：用当前曲目封面重新取色
            var palette = _lastCoverHash is null ? ThemeDeriver.NeutralFallback() : DeriveFromCover(_lastCoverHash);
            AnimateTo(palette);
        }
        else
        {
            AnimateTo(ThemePalette.FixedDark);
        }
    }

    /// <summary>切歌钩子（PlayerViewModel.ApplyTrackDisplay 调用）。</summary>
    public static void OnTrackChanged(string? coverHash)
    {
        if (!_initialized) Initialize();
        _lastCoverHash = coverHash;
        if (!FollowCover) return;

        var palette = coverHash is null ? ThemeDeriver.NeutralFallback() : DeriveFromCover(coverHash);
        Serilog.Log.Information("主题：封面 {Hash} → 背景 {Bg} 表面 {Surface} 强调 {Accent} 文字 {Text}",
            coverHash ?? "(无)", palette.Background, palette.Surface, palette.Accent, palette.TextPrimary);
        AnimateTo(palette);
    }

    private static ThemePalette DeriveFromCover(string coverHash)
    {
        try
        {
            var pixels = LoadSamplePixels(coverHash);
            if (pixels.Count == 0) return ThemeDeriver.NeutralFallback();
            var colors = CoverColorExtractor.Extract(pixels);
            return ThemeDeriver.Derive(colors.Main);
        }
        catch
        {
            return ThemeDeriver.NeutralFallback();
        }
    }

    /// <summary>封面文件 → 32×32 Bgra32 像素 → 纯函数取色。</summary>
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

    /// <summary>300ms 缓动过渡到目标调色板。</summary>
    public static void AnimateTo(ThemePalette target)
    {
        if (!_initialized) Initialize();
        _target = target;
        _progress = 0.0;

        _timer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TickMs) };
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
            _timer!.Stop();
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
    }

    private static void Set(ResourceDictionary resources, string key, RgbColor c)
    {
        var brush = new SolidColorBrush(Color.FromArgb(c.A, c.R, c.G, c.B));
        brush.Freeze();
        resources[key] = brush;
    }

    /// <summary>静态构造挂上计时器。</summary>
    static ThemeService()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(TickMs) };
        timer.Tick += OnTick;
        _timer = timer;
    }
}
