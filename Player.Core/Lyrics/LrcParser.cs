using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Player.Core.Lyrics;

/// <summary>歌词的一行。翻译/罗马音是并行轨，可能为空。</summary>
public sealed class LyricLine
{
    public TimeSpan Time { get; init; }

    public string Text { get; init; } = string.Empty;

    public string? Translation { get; set; }

    public string? Romaji { get; set; }

    public bool IsEmpty => string.IsNullOrWhiteSpace(Text);
}

/// <summary>一整篇歌词。没有时间轴时降级为整篇静态文本（PLAN 第 7.2 节）。</summary>
public sealed class LyricDocument
{
    public static readonly LyricDocument Empty = new();

    public IReadOnlyList<LyricLine> Lines { get; init; } = Array.Empty<LyricLine>();

    /// <summary>头部元数据（[ti:]/[ar:]/[词:]/[曲:]/[编曲:] 等，键小写）。</summary>
    public IReadOnlyDictionary<string, string> Header { get; init; } = new Dictionary<string, string>();

    /// <summary>来自 [offset:] 标签的整体偏移（正数表示歌词提前）。</summary>
    public TimeSpan TagOffset { get; init; }

    public bool HasTimeline { get; init; }

    public bool HasTranslation => Lines.Any(l => !string.IsNullOrWhiteSpace(l.Translation));

    public bool HasRomaji => Lines.Any(l => !string.IsNullOrWhiteSpace(l.Romaji));

    public bool IsEmpty => Lines.Count == 0;

    public string PlainText => string.Join(Environment.NewLine, Lines.Select(l => l.Text));

    /// <summary>
    /// 找出播放位置对应的行号（最后一个时间 &lt;= position 的行）。位置在第一行之前返回 -1。
    /// 二分查找，万行歌词也不吃力。
    /// </summary>
    public int FindIndexAt(TimeSpan position)
    {
        if (!HasTimeline || Lines.Count == 0) return -1;

        var low = 0;
        var high = Lines.Count - 1;
        var found = -1;

        while (low <= high)
        {
            var mid = (low + high) / 2;
            if (Lines[mid].Time <= position)
            {
                found = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return found;
    }
}

/// <summary>
/// LRC 解析（PLAN 第 7.2 节）：支持一行多时间标签、<c>[offset:]</c>、元数据标签，
/// 没有任何时间标签时整篇按纯文本处理。做成纯函数，harness 可离线断言。
/// </summary>
public static partial class LrcParser
{
    public static LyricDocument Parse(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return LyricDocument.Empty;

        var lines = new List<LyricLine>();
        var plain = new List<string>();
        var offset = TimeSpan.Zero;
        var header = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in content.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;

            // [offset:+500] / [offset:-200]，单位毫秒
            var offsetMatch = OffsetTag().Match(line);
            if (offsetMatch.Success &&
                int.TryParse(offsetMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ms))
            {
                offset = TimeSpan.FromMilliseconds(ms);
                continue;
            }

            // 元数据标签（ti/ar/al/by/词/曲/编曲 等）记录进 Header 并跳过
            var metaLine = MetadataLine().Match(line);
            if (metaLine.Success)
            {
                foreach (Match m in MetadataPair().Matches(metaLine.Groups[1].Value))
                    header[m.Groups[1].Value.Trim().ToLowerInvariant()] = m.Groups[2].Value.Trim();
                continue;
            }

            var timeMatches = TimeTag().Matches(line);
            if (timeMatches.Count == 0)
            {
                plain.Add(line.Trim());
                continue;
            }

            // 一行可能挂多个时间标签：[00:12.00][01:20.00]同一句歌词
            var text = TimeTag().Replace(line, string.Empty).Trim();

            foreach (Match match in timeMatches)
            {
                if (!TryParseTime(match, out var time)) continue;
                lines.Add(new LyricLine { Time = time, Text = text });
            }
        }

        if (lines.Count > 0)
        {
            return new LyricDocument
            {
                Lines = lines.OrderBy(l => l.Time).ToList(),
                Header = header,
                TagOffset = offset,
                HasTimeline = true
            };
        }

        // 一个时间标签都没有 → 整篇静态显示
        if (plain.Count == 0) return LyricDocument.Empty;

        return new LyricDocument
        {
            Lines = plain.Select(t => new LyricLine { Time = TimeSpan.Zero, Text = t }).ToList(),
            Header = header,
            TagOffset = offset,
            HasTimeline = false
        };
    }

    /// <summary>把翻译、罗马音按时间轴并进原文（网易云的三条轨时间戳基本一致，容差 500ms）。</summary>
    public static LyricDocument Merge(LyricDocument original, LyricDocument? translation, LyricDocument? romaji)
    {
        if (original.IsEmpty || !original.HasTimeline) return original;

        foreach (var line in original.Lines)
        {
            line.Translation = FindNearestText(translation, line.Time);
            line.Romaji = FindNearestText(romaji, line.Time);
        }

        return original;
    }

    private static string? FindNearestText(LyricDocument? doc, TimeSpan time)
    {
        if (doc is null || doc.IsEmpty || !doc.HasTimeline) return null;

        const double toleranceMs = 500;
        LyricLine? best = null;
        var bestDelta = double.MaxValue;

        foreach (var line in doc.Lines)
        {
            var delta = Math.Abs((line.Time - time).TotalMilliseconds);
            if (delta > toleranceMs || delta >= bestDelta) continue;

            best = line;
            bestDelta = delta;
        }

        return string.IsNullOrWhiteSpace(best?.Text) ? null : best!.Text;
    }

    private static bool TryParseTime(Match match, out TimeSpan time)
    {
        time = TimeSpan.Zero;

        if (!int.TryParse(match.Groups[1].Value, out var minutes)) return false;
        if (!int.TryParse(match.Groups[2].Value, out var seconds)) return false;

        var fractionText = match.Groups[3].Success ? match.Groups[3].Value : string.Empty;
        var milliseconds = 0;

        if (fractionText.Length > 0)
        {
            // ".23" 是 230ms，".234" 是 234ms
            var normalized = fractionText.PadRight(3, '0')[..3];
            int.TryParse(normalized, out milliseconds);
        }

        time = new TimeSpan(0, 0, minutes, seconds, milliseconds);
        return true;
    }

    /// <summary>把整篇歌词转成便于人读的纯文本（无时间轴时用）。</summary>
    public static string ToPlainText(LyricDocument document)
    {
        var builder = new StringBuilder();
        foreach (var line in document.Lines)
        {
            builder.AppendLine(line.Text);
            if (!string.IsNullOrWhiteSpace(line.Translation)) builder.AppendLine(line.Translation);
        }
        return builder.ToString().TrimEnd();
    }

    [GeneratedRegex(@"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]")]
    private static partial Regex TimeTag();

    [GeneratedRegex(@"^\s*\[offset:\s*([+-]?\d+)\s*\]", RegexOptions.IgnoreCase)]
    private static partial Regex OffsetTag();

    [GeneratedRegex(@"^((?:\[[a-zA-Z\u4e00-\u9fff]+\s*:\s*[^\]]*\]\s*)+)")]
    private static partial Regex MetadataLine();

    [GeneratedRegex(@"\[([a-zA-Z\u4e00-\u9fff]+)\s*:\s*([^\]]*)\]", RegexOptions.IgnoreCase)]
    private static partial Regex MetadataPair();
}
