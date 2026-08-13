namespace Player.Core.Library;

/// <summary>
/// tracks 表的一行（PLAN 第 5 节 schema）。扫描后整表载入内存，
/// 万级曲库的过滤/排序全在内存里做，不走 SQL。
/// </summary>
public sealed class TrackRecord
{
    public long Id { get; set; }

    public string Path { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Artist { get; set; } = string.Empty;

    public string Album { get; set; } = string.Empty;

    public string AlbumArtist { get; set; } = string.Empty;

    public int TrackNo { get; set; }

    public int DiscNo { get; set; }

    public long DurationMs { get; set; }

    public int SampleRate { get; set; }

    public int BitDepth { get; set; }

    public int Bitrate { get; set; }

    public long FileSize { get; set; }

    /// <summary>文件最后写入时间（Unix 秒），增量扫描靠它 + 文件大小比对。</summary>
    public long Mtime { get; set; }

    public long AddedAt { get; set; }

    public int PlayCount { get; set; }

    public long? LastPlayed { get; set; }

    /// <summary>内嵌封面的内容 hash，对应 data/cache/covers/{hash}.jpg；无封面为 null。</summary>
    public string? CoverHash { get; set; }

    // ---------- 派生显示属性 ----------

    public TimeSpan Duration => TimeSpan.FromMilliseconds(DurationMs);

    public string DurationText => DurationMs <= 0
        ? "-"
        : Duration.TotalHours >= 1
            ? Duration.ToString(@"h\:mm\:ss")
            : Duration.ToString(@"m\:ss");

    public string Format => System.IO.Path.GetExtension(Path).TrimStart('.').ToUpperInvariant();

    public string SampleRateText => SampleRate > 0
        ? (SampleRate / 1000.0).ToString("0.#") + " kHz"
        : "-";

    public string BitDepthText => BitDepth > 0 ? BitDepth + " bit" : "-";

    public string DisplayTitle => string.IsNullOrWhiteSpace(Title)
        ? System.IO.Path.GetFileNameWithoutExtension(Path)
        : Title;

    public string DisplayArtist => string.IsNullOrWhiteSpace(Artist) ? "未知艺术家" : Artist;

    public string DisplayAlbum => string.IsNullOrWhiteSpace(Album) ? "未知专辑" : Album;

    public string DisplayAlbumArtist => string.IsNullOrWhiteSpace(AlbumArtist) ? DisplayArtist : AlbumArtist;

    /// <summary>小写的「标题 + 歌手 + 专辑」，即时过滤时只比这一个字符串，万级也不卡。</summary>
    public string SearchKey { get; private set; } = string.Empty;

    public void RebuildSearchKey() =>
        SearchKey = string.Join(' ', Title, Artist, Album,
            System.IO.Path.GetFileNameWithoutExtension(Path)).ToLowerInvariant();
}
