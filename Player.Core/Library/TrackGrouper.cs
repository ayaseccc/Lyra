namespace Player.Core.Library;

/// <summary>专辑分组（UI-R2 定稿）：分组键 = 专辑 + 专辑艺术家，组内按碟号/曲号排序。</summary>
public sealed record TrackGroup(string Album, string Artist, string Year, IReadOnlyList<TrackRecord> Tracks);

/// <summary>
/// 纯函数分组器（R2 定稿）：
/// ① 有专辑的曲目按 专辑+专辑艺术家 分组（专辑艺术家缺失时退回曲目艺术家）；
/// ② 无专辑的散曲归「单曲 | 艺术家」组；
/// ③ 组内按 碟号→曲号 排序，无编号的排最后、按路径兜底；
/// ④ 组排序：艺术家 → 专辑名；组年份取组内最早年份（0 = 未知，不显示）。
/// 全部逻辑纯内存、无副作用，harness 可测。
/// </summary>
public static class TrackGrouper
{
    private sealed class GroupAccumulator
    {
        public string Album = string.Empty;
        public string Artist = string.Empty;
        public int Year;
        public readonly List<TrackRecord> Tracks = new();
    }

    public static IReadOnlyList<TrackGroup> Group(IEnumerable<TrackRecord> tracks)
    {
        var buckets = new Dictionary<string, GroupAccumulator>(StringComparer.Ordinal);

        foreach (var track in tracks)
        {
            var isSingle = string.IsNullOrWhiteSpace(track.Album);
            var album = isSingle ? "单曲" : track.DisplayAlbum;
            var artist = isSingle ? track.DisplayArtist : track.DisplayAlbumArtist;

            var key = album + "\u0001" + artist;
            if (!buckets.TryGetValue(key, out var bucket))
            {
                bucket = new GroupAccumulator { Album = album, Artist = artist };
                buckets[key] = bucket;
            }

            bucket.Tracks.Add(track);
            if (track.Year > 0 && (bucket.Year == 0 || track.Year < bucket.Year))
                bucket.Year = track.Year;
        }

        var groups = new List<TrackGroup>(buckets.Count);
        foreach (var bucket in buckets.Values)
        {
            var sorted = bucket.Tracks
                .OrderBy(t => t.DiscNo <= 0 ? int.MaxValue : t.DiscNo)
                .ThenBy(t => t.TrackNo <= 0 ? int.MaxValue : t.TrackNo)
                .ThenBy(t => t.Path, StringComparer.OrdinalIgnoreCase)
                .ToList();

            groups.Add(new TrackGroup(
                bucket.Album,
                bucket.Artist,
                bucket.Year > 0 ? bucket.Year.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty,
                sorted));
        }

        return groups
            .OrderBy(g => g.Artist, StringComparer.OrdinalIgnoreCase)
            .ThenBy(g => g.Album, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

