using System.Globalization;

namespace Player.Core.Audio;

/// <summary>
/// 当前曲目的运行时信息。P0 的标题/艺术家由文件名推断，
/// P1 接入 TagLibSharp 后改为读真实标签（PLAN 第 5 节）。
/// </summary>
public sealed record TrackInfo
{
    public required string Path { get; init; }

    public string Title { get; init; } = string.Empty;

    public string Artist { get; init; } = string.Empty;

    public TimeSpan Duration { get; init; }

    public int SampleRate { get; init; }

    public int Channels { get; init; }

    /// <summary>源文件的原始位深；BASS 拿不到时为 0。</summary>
    public int BitDepth { get; init; }

    /// <summary>大写扩展名，如 FLAC。</summary>
    public string Format { get; init; } = string.Empty;

    public long FileSize { get; init; }

    /// <summary>形如 "FLAC · 44.1 kHz · 16 bit · 立体声"，给播放条显示用。</summary>
    public string TechnicalSummary
    {
        get
        {
            var parts = new List<string>(4);
            if (!string.IsNullOrEmpty(Format)) parts.Add(Format);
            if (SampleRate > 0)
                parts.Add((SampleRate / 1000.0).ToString("0.#", CultureInfo.InvariantCulture) + " kHz");
            if (BitDepth > 0) parts.Add(BitDepth + " bit");
            parts.Add(Channels switch
            {
                1 => "单声道",
                2 => "立体声",
                > 2 => Channels + " 声道",
                _ => "未知声道"
            });
            return string.Join(" · ", parts);
        }
    }

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title)
        ? System.IO.Path.GetFileNameWithoutExtension(Path)
        : Title;
}
