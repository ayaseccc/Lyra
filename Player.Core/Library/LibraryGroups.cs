namespace Player.Core.Library;

/// <summary>专辑聚合（左侧栏「专辑」视图）。</summary>
public sealed class AlbumGroup
{
    public required string Key { get; init; }

    public required string Album { get; init; }

    public required string AlbumArtist { get; init; }

    public string? CoverHash { get; init; }

    public required IReadOnlyList<TrackRecord> Tracks { get; init; }

    public int TrackCount => Tracks.Count;

    public long TotalDurationMs { get; init; }

    public string SubText => $"{AlbumArtist} · {TrackCount} 首";
}

/// <summary>艺术家聚合（左侧栏「艺术家」视图）。</summary>
public sealed class ArtistGroup
{
    public required string Name { get; init; }

    public required IReadOnlyList<TrackRecord> Tracks { get; init; }

    public int TrackCount => Tracks.Count;

    public int AlbumCount { get; init; }

    public string? CoverHash { get; init; }

    public string SubText => $"{AlbumCount} 张专辑 · {TrackCount} 首";
}

/// <summary>
/// 文件夹虚拟歌单（2026-08-13 用户新需求）：媒体库根目录下的每个顶层子文件夹
/// 自动成为一个**只读**歌单，内容包含其所有子目录，随扫描自动更新。
/// </summary>
public sealed class FolderPlaylist
{
    public required string Name { get; init; }

    /// <summary>该顶层子文件夹的完整路径，同时用作唯一标识。</summary>
    public required string FullPath { get; init; }

    public required string Root { get; init; }

    public required IReadOnlyList<TrackRecord> Tracks { get; init; }

    public int TrackCount => Tracks.Count;

    public string? CoverHash { get; init; }
}
