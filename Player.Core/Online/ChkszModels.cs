using System.Text.Json.Serialization;

namespace Player.Core.Online;

/// <summary>
/// ChKSz 响应的统一外壳：<c>{code, msg, data}</c>。
/// 判断成功以 HTTP 状态 + msg 为准，不能只看有没有 data（PLAN 第 6 节）。
/// </summary>
public sealed class ChkszEnvelope<T>
{
    [JsonPropertyName("code")]
    public int Code { get; set; }

    [JsonPropertyName("msg")]
    public string? Msg { get; set; }

    [JsonPropertyName("data")]
    public T? Data { get; set; }
}

// ---------------- 163_search ----------------

public sealed class SearchResult
{
    [JsonPropertyName("songs")]
    public List<SearchSong> Songs { get; set; } = new();

    [JsonPropertyName("total")]
    public int Total { get; set; }
}

public sealed class SearchSong
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>实测是用 "/" 连接的字符串，不是数组，例如 "周杰伦-/A-LNK"。</summary>
    [JsonPropertyName("artists")]
    public string Artists { get; set; } = string.Empty;

    [JsonPropertyName("album")]
    public string Album { get; set; } = string.Empty;

    [JsonPropertyName("picUrl")]
    public string? PicUrl { get; set; }

    /// <summary>毫秒。</summary>
    [JsonPropertyName("duration")]
    public long Duration { get; set; }

    public TimeSpan DurationSpan => TimeSpan.FromMilliseconds(Duration);

    /// <summary>把 "/" 连接的艺术家串拆开。</summary>
    public IReadOnlyList<string> ArtistList =>
        Artists.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>展示用："艺术家（专辑）"，专辑为空时省略括号。</summary>
    public string ArtistLine => string.IsNullOrWhiteSpace(Album)
        ? Artists
        : $"{Artists}（{Album}）";

    /// <summary>展示用：mm:ss 或 h:mm:ss。</summary>
    public string DurationText => Duration <= 0
        ? "-"
        : DurationSpan.TotalHours >= 1
            ? DurationSpan.ToString(@"h:mm:ss")
            : DurationSpan.ToString(@"m:ss");
}

// ---------------- 163_music ----------------

public sealed class SongUrlResult
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    /// <summary>有时效的直链，**绝不缓存**，每次播放现取（PLAN 第 6 节）。</summary>
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    /// <summary>实际码率（bps）。</summary>
    [JsonPropertyName("br")]
    public int Bitrate { get; set; }

    /// <summary>服务端回显的实际音质档位。</summary>
    [JsonPropertyName("level")]
    public string? Level { get; set; }

    [JsonPropertyName("size")]
    public long Size { get; set; }

    [JsonPropertyName("md5")]
    public string? Md5 { get; set; }

    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("artist")]
    public string? Artist { get; set; }

    [JsonPropertyName("album")]
    public string? Album { get; set; }

    [JsonPropertyName("picUrl")]
    public string? PicUrl { get; set; }
}

// ---------------- 163_lyric ----------------

/// <summary>四个字段恒存在，但翻译/罗马音/逐字经常是空串。</summary>
public sealed class LyricResult
{
    [JsonPropertyName("lrc")]
    public string? Lrc { get; set; }

    [JsonPropertyName("tlyric")]
    public string? TranslatedLrc { get; set; }

    [JsonPropertyName("romalrc")]
    public string? RomajiLrc { get; set; }

    [JsonPropertyName("klyric")]
    public string? KaraokeLrc { get; set; }

    public bool HasAnything =>
        !string.IsNullOrWhiteSpace(Lrc) ||
        !string.IsNullOrWhiteSpace(TranslatedLrc) ||
        !string.IsNullOrWhiteSpace(RomajiLrc);
}

// ---------------- 163_playlist ----------------

public sealed class PlaylistResult
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("coverImgUrl")]
    public string? CoverImgUrl { get; set; }

    [JsonPropertyName("creator")]
    public PlaylistCreator? Creator { get; set; }

    [JsonPropertyName("trackCount")]
    public int TrackCount { get; set; }

    [JsonPropertyName("tracks")]
    public List<PlaylistTrack> Tracks { get; set; } = new();
}

public sealed class PlaylistCreator
{
    [JsonPropertyName("nickname")]
    public string? Nickname { get; set; }
}

/// <summary>实测**没有时长字段**，P5 做本地匹配时只能靠标题 + 歌手。</summary>
public sealed class PlaylistTrack
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("ar")]
    public List<PlaylistArtist> Artists { get; set; } = new();

    [JsonPropertyName("al")]
    public PlaylistAlbum? Album { get; set; }

    public string ArtistText => string.Join(" / ", Artists.Select(a => a.Name).Where(n => !string.IsNullOrEmpty(n)));
}

public sealed class PlaylistArtist
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }
}

public sealed class PlaylistAlbum
{
    [JsonPropertyName("name")]
    public string? Name { get; set; }

    [JsonPropertyName("picUrl")]
    public string? PicUrl { get; set; }
}
