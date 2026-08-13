using System.Globalization;
using System.Text;
using Player.Core.Online;

namespace Player.Core.Lyrics;

/// <summary>
/// 本地曲目 → 网易云 ID 的匹配（PLAN 第 7.2 节）。全部做成纯函数，harness 可离线断言。
///
/// 实测修正（docs/api-samples/README.md）：网易云搜索结果以 UGC/翻唱为主，
/// 「晴天 周杰伦」前 5 条全是翻唱，所以**时长差 &lt; 3 秒是硬条件**，相似度只用来
/// 在时长合格的候选中择优 —— 只靠标题+歌手会大量配到翻唱版本。
/// </summary>
public static class LyricMatcher
{
    /// <summary>时长差硬条件（PLAN 第 7.2 节）。</summary>
    public const double DurationToleranceMs = 3000;

    /// <summary>低于这个相似度视为没配上（宁可"未找到"也不乱配）。</summary>
    public const double MinSimilarity = 0.45;

    /// <summary>
    /// 标题/歌手规范化：全角→半角、去括号及括号内容（(Live) / [翻唱] / feat. 之类）、
    /// 去空白与常见标点、转小写。繁简归一是 P5 歌单匹配才需要的，这里不做。
    /// </summary>
    /// <summary>
    /// 标题/歌手规范化：全角→半角、**丢弃括号及其内容**（(Live) / [翻唱] / feat. 之类）、
    /// 丢弃空白与常见标点、转小写。不做词间分隔 —— 中文歌词匹配时 "晴 天" 与 "晴天"
    /// 应当等价；英文 "hello world" 两侧都去掉空格后依然一致，包含匹配不受影响。
    /// 繁简归一是 P5 歌单匹配才需要的，这里不做。
    /// </summary>
    public static string Normalize(string text)
    {
        if (string.IsNullOrEmpty(text)) return string.Empty;

        var builder = new StringBuilder(text.Length);
        var bracketDepth = 0;

        foreach (var ch in text)
        {
            var c = ch;

            // 全角 → 半角
            if (c >= '！' && c <= '～')
            {
                c = (char)(c - 0xFEE0);
            }
            else if (c == '　') // 全角空格
            {
                continue;
            }

            // 括号深度：进入括号后内容全部丢弃，直到配对的右括号
            if (c is '(' or '（' or '[' or '【' or '{' or '《' or '「')
            {
                bracketDepth++;
                continue;
            }

            if (c is ')' or '）' or ']' or '】' or '}' or '》' or '」')
            {
                if (bracketDepth > 0) bracketDepth--;
                continue;
            }

            if (bracketDepth > 0) continue;

            // 字母/数字/中日韩字符保留；空白和标点直接丢弃（不保留分隔）
            if (char.IsLetterOrDigit(c) || c > 0x2E80 || c is '.' or '-' or '_')
            {
                builder.Append(c);
            }
        }

        return builder.ToString().ToLowerInvariant();
    }

    /// <summary>Levenshtein 编辑距离（大小写不敏感），供相似度计算用。</summary>
    public static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];

        for (var j = 0; j <= b.Length; j++) previous[j] = j;

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;

            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(
                    Math.Min(current[j - 1] + 1, previous[j] + 1),
                    previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    /// <summary>0.0 ~ 1.0。完全一致为 1；一方包含另一方给高分但不满分。</summary>
    public static double TextSimilarity(string normalizedA, string normalizedB)
    {
        if (normalizedA.Length == 0 || normalizedB.Length == 0) return 0;

        if (string.Equals(normalizedA, normalizedB, StringComparison.Ordinal)) return 1.0;

        var longer = normalizedA.Length >= normalizedB.Length ? normalizedA : normalizedB;
        var shorter = normalizedA.Length >= normalizedB.Length ? normalizedB : normalizedA;

        // 一方完整包含另一方（"晴天" vs "晴天 钢琴版"）：给 0.85 起，按长度差递减
        if (longer.Contains(shorter, StringComparison.Ordinal))
        {
            var ratio = (double)shorter.Length / longer.Length;
            return 0.55 + 0.45 * ratio;
        }

        var distance = Levenshtein(normalizedA, normalizedB);
        return 1.0 - (double)distance / Math.Max(longer.Length, 1);
    }

    /// <summary>
    /// 综合相似度：标题为主（0.65），歌手为辅（0.35）。歌手为空时标题全权决定。
    /// </summary>
    public static double CombinedSimilarity(
        string normalizedTitleA, string normalizedArtistA,
        string normalizedTitleB, string normalizedArtistB)
    {
        var titleSim = TextSimilarity(normalizedTitleA, normalizedTitleB);

        if (normalizedArtistA.Length == 0 || normalizedArtistB.Length == 0)
            return titleSim;

        var artistSim = TextSimilarity(normalizedArtistA, normalizedArtistB);
        return 0.65 * titleSim + 0.35 * artistSim;
    }

    /// <summary>时长差（毫秒）是否在容差内。未知时长（≤0）不算命中。</summary>
    public static bool DurationInTolerance(long candidateMs, long expectedMs) =>
        expectedMs > 0 && candidateMs > 0 &&
        Math.Abs(candidateMs - expectedMs) <= DurationToleranceMs;

    /// <summary>
    /// 从搜索结果里挑最优匹配：先按时长差硬条件过滤，再按综合相似度排序取最高。
    /// 全部候选都不达标时返回 null（安静显示"未找到"，不打扰用户 —— P3 约定）。
    /// </summary>
    public static SearchSong? PickBest(
        SearchResult searchResult,
        string title, string artist, long durationMs)
    {
        if (searchResult.Songs.Count == 0) return null;

        var normalizedTitle = Normalize(title);
        var normalizedArtist = Normalize(artist);

        SearchSong? best = null;
        var bestScore = 0.0;

        foreach (var song in searchResult.Songs)
        {
            if (!DurationInTolerance(song.Duration, durationMs)) continue;

            var score = CombinedSimilarity(
                normalizedTitle, normalizedArtist,
                Normalize(song.Name), Normalize(song.Artists));

            if (score <= bestScore) continue;

            best = song;
            bestScore = score;
        }

        return best is not null && bestScore >= MinSimilarity ? best : null;
    }

    /// <summary>手动"重新匹配"用的候选排序：全部结果按综合相似度降序，时长差做次要排序。</summary>
    public static IReadOnlyList<SearchSong> RankCandidates(
        SearchResult searchResult,
        string title, string artist, long durationMs)
    {
        var normalizedTitle = Normalize(title);
        var normalizedArtist = Normalize(artist);

        return searchResult.Songs
            .Select(song => (
                Song: song,
                Score: CombinedSimilarity(
                    normalizedTitle, normalizedArtist,
                    Normalize(song.Name), Normalize(song.Artists)),
                DurationDelta: durationMs > 0 ? Math.Abs(song.Duration - durationMs) : 0))
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.DurationDelta)
            .Select(x => x.Song)
            .ToList();
    }
}
