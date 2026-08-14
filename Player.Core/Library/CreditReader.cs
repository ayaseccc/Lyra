using Serilog;
using TagLib;

namespace Player.Core.Library;

/// <summary>制作信息（UI-R4）：作词/作曲/编曲，只取标签里真实存在的。</summary>
public sealed record TrackCredits(string Lyricist, string Composer, string Arranger)
{
    public static readonly TrackCredits Empty = new(string.Empty, string.Empty, string.Empty);

    public bool IsEmpty => string.IsNullOrWhiteSpace(Lyricist) && string.IsNullOrWhiteSpace(Composer) && string.IsNullOrWhiteSpace(Arranger);

    /// <summary>格式化为一行（只含存在的项）。</summary>
    public string ToLine()
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(Lyricist)) parts.Add("作词 " + Lyricist);
        if (!string.IsNullOrWhiteSpace(Composer)) parts.Add("作曲 " + Composer);
        if (!string.IsNullOrWhiteSpace(Arranger)) parts.Add("编曲 " + Arranger);
        return string.Join(" · ", parts);
    }
}

/// <summary>
/// 从音频文件标签读取制作信息（UI-R4 定稿：来源为标签或 LRC 头部，有才显示）。
/// 作曲走标准 Composers；作词/编曲走 Vorbis 注释（FLAC）或 ID3v2 TXXX（MP3）的常见键。
/// </summary>
public static class CreditReader
{
    private static readonly string[] LyricistKeys = { "LYRICIST", "作词", "词作者", "歌词作者", "词" };
    private static readonly string[] ArrangerKeys = { "ARRANGER", "编曲", "编曲者" };
    private static readonly string[] TxxxLyricist = { "Lyricist", "作词", "词", "Lyrics By" };
    private static readonly string[] TxxxArranger = { "Arranger", "编曲", "编" };

    public static TrackCredits Read(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path);

            var composer = Clean(file.Tag?.Composers);

            var lyricist = string.Empty;
            var arranger = string.Empty;

            // Vorbis 注释（FLAC/Ogg）：按原始键读
            if (file.GetTag(TagTypes.Xiph) is TagLib.Ogg.XiphComment xiph)
            {
                lyricist = FirstNonEmpty(xiph, LyricistKeys);
                arranger = FirstNonEmpty(xiph, ArrangerKeys);
            }
            // ID3v2（MP3）：TXXX 描述匹配
            else if (file.GetTag(TagTypes.Id3v2) is TagLib.Id3v2.Tag id3)
            {
                foreach (var frame in id3.GetFrames<TagLib.Id3v2.UserTextInformationFrame>())
                {
                    var desc = frame.Description?.Trim() ?? string.Empty;
                    var value = string.Join(" / ", frame.Text ?? Array.Empty<string>()).Trim();
                    if (string.IsNullOrEmpty(value)) continue;
                    if (string.IsNullOrEmpty(lyricist) && TxxxLyricist.Contains(desc, StringComparer.OrdinalIgnoreCase))
                        lyricist = Clean(value);
                    if (string.IsNullOrEmpty(arranger) && TxxxArranger.Contains(desc, StringComparer.OrdinalIgnoreCase))
                        arranger = Clean(value);
                }
            }

            if (string.IsNullOrWhiteSpace(composer) && string.IsNullOrWhiteSpace(lyricist) && string.IsNullOrWhiteSpace(arranger))
                return TrackCredits.Empty;

            return new TrackCredits(lyricist, composer, arranger);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "读取制作信息失败：{Path}", path);
            return TrackCredits.Empty;
        }
    }

    private static string FirstNonEmpty(TagLib.Ogg.XiphComment xiph, IReadOnlyList<string> keys)
    {
        foreach (var key in keys)
        {
            var v = xiph.GetFirstField(key);
            if (!string.IsNullOrWhiteSpace(v)) return Clean(v);
        }
        return string.Empty;
    }

    private static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var cleaned = value.Trim();
        // 常见标签工具会写 "作词：某某" 或 "词：某某" 前缀，剥掉
        foreach (var prefix in new[] { "作词：", "作词:", "作曲：", "作曲:", "编曲：", "编曲:", "词：", "词:", "曲：", "曲:" })
        {
            if (cleaned.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                cleaned = cleaned[prefix.Length..].Trim();
                break;
            }
        }
        return cleaned;
    }

    private static string Clean(IEnumerable<string>? values)
    {
        if (values is null) return string.Empty;
        return string.Join(" / ", values.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => Clean(v)));
    }
}
