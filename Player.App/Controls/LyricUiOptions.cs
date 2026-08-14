using System.Windows;
using System.Windows.Media;

namespace Player.App.Controls;

/// <summary>字重选项（设置页下拉 + 桌面歌词菜单共用）。</summary>
public sealed record FontWeightOption(string Key, string Name)
{
    public override string ToString() => Name;
}

/// <summary>桌面歌词文字颜色选项（Key="Theme" = 跟随取色主题，否则为 #RRGGBB）。</summary>
public sealed record DesktopLyricsColorOption(string Key, string Name)
{
    public override string ToString() => Name;
}

/// <summary>背景透明度选项。</summary>
public sealed record BgOpacityOption(double Value, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// 歌词字体相关选项（L1.1-②）：系统字体枚举，置顶常见中日文友好项
/// （微软雅黑 / 思源黑体 / Noto Sans CJK / 等线 / 宋体 等），其余按字母序。
/// 右栏、大歌词页、桌面歌词共用同一份列表。
/// </summary>
public static class LyricUiOptions
{
    /// <summary>置顶的中日文友好字体（按优先级；未安装的自动跳过）。</summary>
    private static readonly string[] PreferredFonts =
    {
        "Microsoft YaHei UI", "微软雅黑", "Microsoft YaHei",
        "Source Han Sans SC", "Source Han Sans CN", "思源黑体", "思源黑体 CN",
        "Noto Sans CJK SC", "Noto Sans SC",
        "DengXian", "等线",
        "SimSun", "宋体",
        "Segoe UI", "Arial"
    };

    /// <summary>全部候选字体（置顶项在前，其余按名称排序），静态缓存。</summary>
    public static IReadOnlyList<string> FontFamilies { get; } = BuildFontFamilies();

    public static IReadOnlyList<FontWeightOption> Weights { get; } = new[]
    {
        new FontWeightOption("Normal", "常规"),
        new FontWeightOption("Medium", "中等"),
        new FontWeightOption("Bold", "加粗")
    };

    public static IReadOnlyList<BgOpacityOption> BgOpacities { get; } = new[]
    {
        new BgOpacityOption(0.3, "30%"),
        new BgOpacityOption(0.5, "50%"),
        new BgOpacityOption(0.7, "70%"),
        new BgOpacityOption(0.9, "90%")
    };

    public static IReadOnlyList<DesktopLyricsColorOption> TextColors { get; } = new[]
    {
        new DesktopLyricsColorOption("Theme", "跟随主题"),
        new DesktopLyricsColorOption("#FFFFFF", "纯白"),
        new DesktopLyricsColorOption("#000000", "纯黑"),
        new DesktopLyricsColorOption("#FF5252", "亮红"),
        new DesktopLyricsColorOption("#69F0AE", "亮绿"),
        new DesktopLyricsColorOption("#40C4FF", "亮蓝"),
        new DesktopLyricsColorOption("#FFEE58", "亮黄"),
        new DesktopLyricsColorOption("#18FFFF", "亮青"),
        new DesktopLyricsColorOption("#FF4081", "亮粉"),
        new DesktopLyricsColorOption("#FF9100", "橙色")
    };

    private static IReadOnlyList<string> BuildFontFamilies()
    {
        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in Fonts.SystemFontFamilies) installed.Add(f.Source);

        var list = new List<string>(installed.Count);
        foreach (var preferred in PreferredFonts)
            if (installed.Remove(preferred)) list.Add(preferred);

        foreach (var name in installed.OrderBy(s => s, StringComparer.CurrentCultureIgnoreCase))
            list.Add(name);
        return list;
    }

    /// <summary>字重 Key → FontWeight（未知值回退常规）。</summary>
    public static FontWeight ParseWeight(string? key) => key switch
    {
        "Medium" => FontWeights.Medium,
        "Bold" => FontWeights.Bold,
        _ => FontWeights.Normal
    };

    /// <summary>当前行字重 = 基准字重的下一档（常规→中等偏粗 SemiBold、中等→加粗、加粗→加粗）。</summary>
    public static FontWeight CurrentLineWeight(string? key) => key switch
    {
        "Medium" => FontWeights.Bold,
        "Bold" => FontWeights.Bold,
        _ => FontWeights.SemiBold
    };

    /// <summary>配置里的字体名 → FontFamily（空值回退雅黑）。</summary>
    public static FontFamily ResolveFontFamily(string? familyName) =>
        string.IsNullOrWhiteSpace(familyName)
            ? new FontFamily("Microsoft YaHei UI, Segoe UI")
            : new FontFamily(familyName);
}
