using System.Text.RegularExpressions;

namespace Player.Core.Lyrics;

/// <summary>
/// 自绘歌词控件的布局与滚动计算（UI-R5 重写，对照 foobar 歌词栏）。
/// 全部纯函数，harness 可离线断言；与 WPF 无关（不引用任何 UI 类型）。
///
/// R5 规则：成对单元布局（原文+翻译=一个单元，无翻译不留空位）、按栏宽折行
/// （CJK 逐字符 / 拉丁按词）、全部水平居中、滚动目标=当前单元几何中心、
/// 单元高度动态、栏宽变化即时重排。
/// </summary>
public static class LyricLayout
{
    /// <summary>主文本行高（px）。</summary>
    public const double PrimaryLineHeight = 30;

    /// <summary>副文本（翻译/罗马音）行高（px）。</summary>
    public const double SecondaryLineHeight = 20;

    /// <summary>单元内 原文↔翻译 间距（px）。</summary>
    public const double InnerGap = 3;

    /// <summary>单元间距（px）。</summary>
    public const double UnitGap = 16;

    /// <summary>主文本字号。</summary>
    public const double PrimaryFontSize = 17;

    /// <summary>翻译字号 ≈ 原文 0.85。</summary>
    public const double SecondaryFontSize = 14.5;

    // ================= 元数据识别（R5 ①：从时间流剥离） =================

    private static readonly Regex MetadataRegex = new(
        @"^\s*(?<key>作词人|作曲人|编曲人|作詞|作曲者|作词|作曲|编曲|制作人|監製|監制|OP/ED|OP|ED|Mixed|母带|混音|词|曲|编)\s*[:：]?\s*(?<value>.+?)\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>识别头部元数据行（作词：X / 作曲:X / OP：X / 带前后空格与全角半角冒号变体）。</summary>
    public static (string Key, string Value)? TryParseMetadata(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var match = MetadataRegex.Match(text);
        if (!match.Success) return null;
        var value = match.Groups["value"].Value.Trim();
        if (string.IsNullOrWhiteSpace(value)) return null;
        return (match.Groups["key"].Value.Trim(), value);
    }

    /// <summary>是否是元数据行（不参与歌词时间流与当前行判定）。</summary>
    public static bool IsMetadataLine(string text) => TryParseMetadata(text) is not null;

    /// <summary>元数据键归一（作词/词/作词人 → 作词 等），供并入制作信息区。</summary>
    public static string NormalizeMetadataKey(string key) => key switch
    {
        "词" or "作词人" or "作詞" => "作词",
        "曲" or "作曲人" or "作曲者" => "作曲",
        "编" or "编曲人" => "编曲",
        "母带" or "混音" or "Mixed" or "制作人" or "監製" or "監制" => "制作",
        "OP/ED" => "OP/ED",
        _ => key
    };

    // ================= 折行（R5 ④：CJK 逐字符 / 拉丁按词，禁止省略号） =================

    /// <summary>
    /// 按栏宽折行：CJK 逐字符，拉丁词在空格处断行；任何情况不截断（返回的行都是完整文本）。
    /// measureWidth 由宿主提供（WPF 侧用 FormattedText 量，harness 用合成量法）。
    /// </summary>
    public static IReadOnlyList<string> WrapText(string text, double maxWidth, Func<string, double> measureWidth)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
        if (maxWidth <= 0) return new[] { text };

        var lines = new List<string>();
        var current = string.Empty;

        foreach (var ch in text)
        {
            var candidate = current + ch;
            if (measureWidth(candidate) <= maxWidth)
            {
                current = candidate;
                continue;
            }

            if (current.Length == 0)
            {
                // 单个字符都放不下：仍然显示（宁可贴边也不截断）
                lines.Add(ch.ToString());
                continue;
            }

            // 拉丁按词：在最后一个空格处断
            var space = current.LastIndexOf(' ');
            if (space > 0 && space < current.Length - 1)
            {
                lines.Add(current[..space]);
                current = current[(space + 1)..] + ch;
            }
            else
            {
                // CJK 或词内超宽：逐字符断
                lines.Add(current);
                current = ch.ToString();
            }
        }

        if (current.Length > 0) lines.Add(current);
        return lines;
    }

    // ================= 单元布局（R5 ③：原文+翻译=一个单元） =================

    /// <summary>一个布局单元的高度信息。</summary>
    public sealed record UnitLayout(int SourceIndex, double Height, int PrimaryLineCount, int SecondaryLineCount);

    /// <summary>
    /// 按各单元折行行数算单元高度：主行×主行高 + (有副文本? 副行×副行高 + 单元内间距 : 0)。
    /// 无翻译的单元不保留空位（动态高度）。isSecondaryShown=false 时副文本不占位。
    /// </summary>
    public static IReadOnlyList<UnitLayout> BuildUnitLayout(
        IReadOnlyList<int> primaryLineCounts,
        IReadOnlyList<int> secondaryLineCounts,
        bool isSecondaryShown,
        double primaryLineHeight = PrimaryLineHeight,
        double secondaryLineHeight = SecondaryLineHeight,
        double innerGap = InnerGap)
    {
        var result = new List<UnitLayout>(primaryLineCounts.Count);
        for (var i = 0; i < primaryLineCounts.Count; i++)
        {
            var primary = Math.Max(1, primaryLineCounts[i]);
            var secondary = isSecondaryShown ? Math.Max(0, secondaryLineCounts[i]) : 0;
            var height = primary * primaryLineHeight
                         + (secondary > 0 ? secondary * secondaryLineHeight + innerGap : 0);
            result.Add(new UnitLayout(i, height, primary, secondary));
        }
        return result;
    }

    /// <summary>各单元顶部的滚动偏移（px）：top[i] = Σ高度[0..i-1] + i×单元间距。</summary>
    public static double[] ComputeUnitTops(IReadOnlyList<double> unitHeights)
    {
        var tops = new double[unitHeights.Count];
        var y = 0.0;
        for (var i = 0; i < unitHeights.Count; i++)
        {
            tops[i] = y;
            y += unitHeights[i] + UnitGap;
        }
        return tops;
    }

    /// <summary>全部单元的总高度（含单元间距）。</summary>
    public static double TotalHeight(IReadOnlyList<double> unitHeights) =>
        unitHeights.Count == 0 ? 0
            : unitHeights.Sum() + UnitGap * (unitHeights.Count - 1);

    // ================= 滚动目标与可见范围（R5 ⑥：几何中心） =================

    /// <summary>当前单元居中的目标滚动偏移：目标 = 单元中心 - 视口中心；首/末自动钳制。</summary>
    public static double TargetOffsetForUnit(int unitIndex, IReadOnlyList<double> unitHeights, double viewportHeight)
    {
        if (unitIndex < 0 || unitHeights.Count == 0 || viewportHeight <= 0) return 0;
        if (unitIndex >= unitHeights.Count) unitIndex = unitHeights.Count - 1;

        var tops = ComputeUnitTops(unitHeights);
        var center = tops[unitIndex] + unitHeights[unitIndex] / 2;
        var target = center - viewportHeight / 2;
        return Math.Clamp(target, 0, Math.Max(0, TotalHeight(unitHeights) - viewportHeight));
    }

    /// <summary>当前可见单元范围 [first, last]（含），空返回 (-1,-1)。</summary>
    public static (int First, int Last) VisibleUnits(double offset, double viewportHeight, IReadOnlyList<double> unitHeights)
    {
        if (unitHeights.Count == 0 || viewportHeight <= 0) return (-1, -1);

        var tops = ComputeUnitTops(unitHeights);
        var first = -1;
        var last = -1;
        for (var i = 0; i < unitHeights.Count; i++)
        {
            var top = tops[i] - offset;
            var bottom = top + unitHeights[i];
            if (bottom >= 0 && top <= viewportHeight)
            {
                if (first < 0) first = i;
                last = i;
            }
        }
        return first < 0 ? (-1, -1) : (first, last);
    }

    /// <summary>命中测试：视口内 y → 单元号（点击=跳转）。越界返回 -1。</summary>
    public static int HitTestUnit(double y, double offset, IReadOnlyList<double> unitHeights)
    {
        if (unitHeights.Count == 0) return -1;
        var tops = ComputeUnitTops(unitHeights);
        var contentY = y + offset;
        for (var i = 0; i < unitHeights.Count; i++)
        {
            if (contentY >= tops[i] && contentY < tops[i] + unitHeights[i])
                return i;
        }
        return -1;
    }

    /// <summary>缓动一步（沿用 UI-R0）：offset 向 target 收敛，dt 秒、k 越大越快。</summary>
    public static (double Offset, bool Settled) EaseTowards(double offset, double target, double dt, double k = 10.0)
    {
        if (Math.Abs(target - offset) < 0.5) return (target, true);
        var factor = 1.0 - Math.Exp(-k * dt);
        var next = offset + (target - offset) * factor;
        return (next, Math.Abs(target - next) < 0.5);
    }

    /// <summary>滚轮一步的滚动量（px）。按约 2.5 个主行滚动，方向与滚轮一致。</summary>
    public static double WheelStep(double delta) => delta > 0 ? -PrimaryLineHeight * 2.5 : PrimaryLineHeight * 2.5;
}
