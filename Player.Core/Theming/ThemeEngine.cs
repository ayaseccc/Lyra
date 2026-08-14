using System.Globalization;

namespace Player.Core.Theming;

/// <summary>纯数据 RGBA 颜色（harness 可测，不依赖 WPF）。Alpha 默认不透明。</summary>
public readonly record struct RgbColor(byte R, byte G, byte B, byte A = 255)
{
    public override string ToString() =>
        A == 255 ? $"#{R:X2}{G:X2}{B:X2}" : $"#{A:X2}{R:X2}{G:X2}{B:X2}";
}

/// <summary>封面提取结果：主色 + 强调色。</summary>
public sealed record CoverColors(RgbColor Main, RgbColor Accent);

/// <summary>
/// 派生主题调色板（UI-R3 定稿：浅 tint 背景 + 深色文字 + 高饱和强调色）。
/// 跟随封面模式全部不透明；固定深色模式保留原半透明刷子（Mica 透出）。
/// </summary>
public sealed record ThemePalette(
    RgbColor Background,
    RgbColor Surface,
    RgbColor SurfaceStrong,
    RgbColor TextPrimary,
    RgbColor TextSecondary,
    RgbColor TextTertiary,
    RgbColor Accent,
    RgbColor Hover,
    RgbColor Selected,
    RgbColor BorderSoft,
    RgbColor TrackEmpty,
    RgbColor VolumeReached,
    RgbColor VolumeSlot)
{
    /// <summary>固定深色（逃生口）：即现有深色主题的原色。</summary>
    public static readonly ThemePalette FixedDark = new(
        new RgbColor(0x20, 0x20, 0x20),
        new RgbColor(0x20, 0x20, 0x20, 0xB3),
        new RgbColor(0x1C, 0x1C, 0x1C, 0xE6),
        new RgbColor(0xFF, 0xFF, 0xFF),
        new RgbColor(0xB6, 0xB6, 0xB6),
        new RgbColor(0x8B, 0x8B, 0x8B),
        new RgbColor(0x60, 0xCD, 0xFF),
        new RgbColor(0xFF, 0xFF, 0xFF, 0x1A),
        new RgbColor(0xFF, 0xFF, 0xFF, 0x2E),
        new RgbColor(0xFF, 0xFF, 0xFF, 0x26),
        new RgbColor(0xFF, 0xFF, 0xFF, 0x40),
        new RgbColor(0x3A, 0x3A, 0x3A),
        new RgbColor(0x8B, 0x8B, 0x8B));
}

/// <summary>
/// 封面主色提取（UI-R3）：输入为缩小后的像素数组（约 32×32），
/// 量化直方图 + 饱和度加权找主色，强调色取数量前 10 桶中饱和度最高者。
/// </summary>
public static class CoverColorExtractor
{
    private const int Shift = 3;               // 量化到 5bit/通道（32 级）
    private const int BucketCount = 1 << 15;

    public static CoverColors Extract(IReadOnlyList<RgbColor> pixels) =>
        new(ExtractDominant(pixels), ExtractAccent(pixels));

    /// <summary>主色：量化桶按 数量×(0.5+饱和度) 加权取最大。</summary>
    public static RgbColor ExtractDominant(IReadOnlyList<RgbColor> pixels)
    {
        if (pixels.Count == 0) return new RgbColor(0x40, 0x40, 0x40);

        var counts = new int[BucketCount];
        var sumsR = new long[BucketCount];
        var sumsG = new long[BucketCount];
        var sumsB = new long[BucketCount];

        foreach (var px in pixels)
        {
            var idx = ((px.R >> Shift) << 10) | ((px.G >> Shift) << 5) | (px.B >> Shift);
            counts[idx]++;
            sumsR[idx] += px.R;
            sumsG[idx] += px.G;
            sumsB[idx] += px.B;
        }

        double bestScore = -1;
        var best = new RgbColor(0x40, 0x40, 0x40);
        for (var i = 0; i < BucketCount; i++)
        {
            if (counts[i] == 0) continue;
            var r = (byte)(sumsR[i] / counts[i]);
            var g = (byte)(sumsG[i] / counts[i]);
            var b = (byte)(sumsB[i] / counts[i]);
            var sat = Saturation(r, g, b);
            // 饱和度加权：彩色桶胜过灰/黑/白桶（避免封面边框/纯白占主导）
            var score = counts[i] * (0.5 + sat);
            if (score > bestScore)
            {
                bestScore = score;
                best = new RgbColor(r, g, b);
            }
        }
        return best;
    }

    /// <summary>强调色：数量前 10 的桶里挑饱和度最高的。</summary>
    public static RgbColor ExtractAccent(IReadOnlyList<RgbColor> pixels)
    {
        if (pixels.Count == 0) return new RgbColor(0x60, 0xCD, 0xFF);

        var counts = new int[BucketCount];
        var sumsR = new long[BucketCount];
        var sumsG = new long[BucketCount];
        var sumsB = new long[BucketCount];

        foreach (var px in pixels)
        {
            var idx = ((px.R >> Shift) << 10) | ((px.G >> Shift) << 5) | (px.B >> Shift);
            counts[idx]++;
            sumsR[idx] += px.R;
            sumsG[idx] += px.G;
            sumsB[idx] += px.B;
        }

        var top = new List<(int Count, byte R, byte G, byte B)>(10);
        for (var i = 0; i < BucketCount; i++)
        {
            if (counts[i] == 0) continue;
            top.Add((counts[i], (byte)(sumsR[i] / counts[i]), (byte)(sumsG[i] / counts[i]), (byte)(sumsB[i] / counts[i])));
        }
        top.Sort((a, b) => b.Count.CompareTo(a.Count));
        if (top.Count == 0) return new RgbColor(0x60, 0xCD, 0xFF);

        var best = top[0];
        var bestSat = -1.0;
        foreach (var item in top.Take(Math.Min(10, top.Count)))
        {
            var sat = Saturation(item.R, item.G, item.B);
            if (sat > bestSat)
            {
                bestSat = sat;
                best = item;
            }
        }
        return new RgbColor(best.R, best.G, best.B);
    }

    private static double Saturation(byte r, byte g, byte b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        if (max == 0) return 0;
        return (max - min) / (double)max;
    }
}

/// <summary>
/// 主题派生（UI-R3）：浅色/深色两套基底，各自可随封面染色。
/// 浅色：浅 tint 背景 + 深色文字；深色：深 tint 背景 + 浅色文字。
/// 对比度保底（WCAG）：主/次/三级文字 vs 背景 ≥ 7/4.5/3，强调色 vs 背景 ≥ 3。
/// 过暗或过灰的主色 → 各自的中性回退（浅→中性浅灰，深→固定深色）。
/// </summary>
public static class ThemeDeriver
{
    public const double MinTextPrimaryContrast = 7.0;
    public const double MinTextSecondaryContrast = 4.5;
    public const double MinTextTertiaryContrast = 3.0;
    public const double MinAccentContrast = 3.0;

    private const double DarkFallbackLuminance = 0.30;
    private const double SaturationFallback = 0.10;
    private const double MinBackgroundLuminance = 0.78;

    /// <summary>过暗/过灰回退用的中性基色（浅暖灰）。</summary>
    private static readonly RgbColor NeutralBase = new(0xC9, 0xC4, 0xBA);

    /// <summary>浅色基底 + 封面染色。</summary>
    public static ThemePalette DeriveLight(RgbColor main)
    {
        var (_, s, l) = Hsl(main);
        if (l < DarkFallbackLuminance || s < SaturationFallback)
            return NeutralFallback();
        return DeriveLightCore(main);
    }

    /// <summary>深色基底 + 封面染色。</summary>
    public static ThemePalette DeriveDark(RgbColor main)
    {
        var (_, s, l) = Hsl(main);
        if (l < 0.18 || s < SaturationFallback)
            return ThemePalette.FixedDark;
        return DeriveDarkCore(main);
    }

    /// <summary>浅色不染色的中性浅灰（固定浅色）。</summary>
    public static ThemePalette NeutralFallback() => DeriveLightCore(NeutralBase);

    /// <summary>深色不染色的固定深色（固定深色）。</summary>
    public static ThemePalette FixedDarkPalette() => ThemePalette.FixedDark;

    /// <summary>旧 API 兼容：默认浅色派生。</summary>
    public static ThemePalette Derive(RgbColor main) => DeriveLight(main);

    private static ThemePalette DeriveLightCore(RgbColor main)
    {
        // 背景：主色向白混合 82%，保证足够浅（对比度保底的前提）
        var background = Mix(main, White, 0.82);
        while (RelativeLuminance(background) < MinBackgroundLuminance)
            background = Mix(background, White, 0.5);

        var surface = Mix(main, White, 0.90);
        var surfaceStrong = Mix(main, White, 0.95);

        // 强调色：主色同色相、最大饱和度，再向黑混合直到 vs 背景 ≥ 3.0
        var accent = Saturate(main);
        while (ContrastRatio(accent, background) < MinAccentContrast)
            accent = Mix(accent, Black, 0.15);

        // 文字：深色；逐档保证对比度
        var textPrimary = new RgbColor(0x14, 0x14, 0x12);
        while (ContrastRatio(textPrimary, background) < MinTextPrimaryContrast)
            textPrimary = Mix(textPrimary, Black, 0.5);

        var textSecondary = Mix(textPrimary, background, 0.55);
        while (ContrastRatio(textSecondary, background) < MinTextSecondaryContrast)
            textSecondary = Mix(textSecondary, textPrimary, 0.25);

        var textTertiary = Mix(textPrimary, background, 0.70);
        while (ContrastRatio(textTertiary, background) < MinTextTertiaryContrast)
            textTertiary = Mix(textTertiary, textPrimary, 0.25);

        return new ThemePalette(
            background,
            surface,
            surfaceStrong,
            textPrimary,
            textSecondary,
            textTertiary,
            accent,
            Mix(background, Black, 0.05),
            Mix(background, Black, 0.10),
            Mix(background, Black, 0.14),
            Mix(background, Black, 0.28),
            Mix(background, Black, 0.72),
            Mix(background, Black, 0.26));
    }

    /// <summary>深色基底 + 封面染色：深 tint 背景 + 浅色文字 + 高亮强调色。</summary>
    private static ThemePalette DeriveDarkCore(RgbColor main)
    {
        // 背景：主色向黑混合 78%，保留明显色相又保证足够深（对比度保底的前提）
        var background = Mix(main, Black, 0.78);
        while (RelativeLuminance(background) > 0.12)
            background = Mix(background, Black, 0.5);

        var surface = Mix(main, Black, 0.70);
        var surfaceStrong = Mix(main, Black, 0.72);

        // 强调色：同色相最大饱和度，再向白混合直到 vs 背景 ≥ 3.0
        var accent = Saturate(main);
        while (ContrastRatio(accent, background) < MinAccentContrast)
            accent = Mix(accent, White, 0.15);

        // 文字：浅色；逐档保证对比度
        var textPrimary = new RgbColor(0xF2, 0xF2, 0xF0);
        while (ContrastRatio(textPrimary, background) < MinTextPrimaryContrast)
            textPrimary = Mix(textPrimary, White, 0.5);

        var textSecondary = Mix(textPrimary, background, 0.45);
        while (ContrastRatio(textSecondary, background) < MinTextSecondaryContrast)
            textSecondary = Mix(textSecondary, textPrimary, 0.25);

        var textTertiary = Mix(textPrimary, background, 0.62);
        while (ContrastRatio(textTertiary, background) < MinTextTertiaryContrast)
            textTertiary = Mix(textTertiary, textPrimary, 0.25);

        return new ThemePalette(
            background,
            surface,
            surfaceStrong,
            textPrimary,
            textSecondary,
            textTertiary,
            accent,
            Mix(background, White, 0.08),
            Mix(background, White, 0.14),
            Mix(background, White, 0.18),
            Mix(background, White, 0.30),
            Mix(background, White, 0.20),
            Mix(background, White, 0.45));
    }

    // ---------- 颜色工具 ----------

    private static readonly RgbColor White = new(0xFF, 0xFF, 0xFF);
    private static readonly RgbColor Black = new(0x00, 0x00, 0x00);

    public static RgbColor Mix(RgbColor a, RgbColor b, double bWeight)
    {
        var w = Math.Clamp(bWeight, 0, 1);
        return new RgbColor(
            (byte)Math.Round(a.R + (b.R - a.R) * w),
            (byte)Math.Round(a.G + (b.G - a.G) * w),
            (byte)Math.Round(a.B + (b.B - a.B) * w));
    }

    /// <summary>同色相最大饱和度（HSL S=1, L=0.5 转 RGB）。</summary>
    public static RgbColor Saturate(RgbColor c)
    {
        var (h, _, _) = Hsl(c);
        return HslToRgb(h, 1.0, 0.5);
    }

    public static (double H, double S, double L) Hsl(RgbColor c)
    {
        var r = c.R / 255.0;
        var g = c.G / 255.0;
        var b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2.0;
        double h = 0, s = 0;
        if (Math.Abs(max - min) > 1e-9)
        {
            var d = max - min;
            s = l > 0.5 ? d / (2.0 - max - min) : d / (max + min);
            if (max == r) h = (g - b) / d + (g < b ? 6 : 0);
            else if (max == g) h = (b - r) / d + 2;
            else h = (r - g) / d + 4;
            h /= 6.0;
        }
        return (h, s, l);
    }

    public static RgbColor HslToRgb(double h, double s, double l)
    {
        h = h - Math.Floor(h);
        if (s <= 1e-9) { var v = (byte)Math.Round(l * 255); return new RgbColor(v, v, v); }
        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        double R(double t)
        {
            t = t - Math.Floor(t);
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 0.5) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }
        return new RgbColor(
            (byte)Math.Round(R(h + 1.0 / 3) * 255),
            (byte)Math.Round(R(h) * 255),
            (byte)Math.Round(R(h - 1.0 / 3) * 255));
    }

    /// <summary>WCAG 相对亮度。</summary>
    public static double RelativeLuminance(RgbColor c)
    {
        double Lin(double v)
        {
            v /= 255.0;
            return v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    /// <summary>WCAG 对比度（1..21）。</summary>
    public static double ContrastRatio(RgbColor a, RgbColor b)
    {
        var la = RelativeLuminance(a);
        var lb = RelativeLuminance(b);
        var hi = Math.Max(la, lb);
        var lo = Math.Min(la, lb);
        return (hi + 0.05) / (lo + 0.05);
    }
}
