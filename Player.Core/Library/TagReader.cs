using System.Security.Cryptography;
using Player.Core.Infra;
using Serilog;

namespace Player.Core.Library;

/// <summary>
/// 用 TagLibSharp 读标签（PLAN 第 2 节）。标题/艺术家一律**标签优先、文件名兜底**，
/// 内嵌封面按内容 hash 落盘到 data/cache/covers/{hash}.jpg，重复封面只存一份。
/// </summary>
public static partial class TagReader
{
    /// <summary>封面 hash 只取前 64KB + 长度：整图做摘要在万级曲库上纯属浪费 CPU。</summary>
    private const int CoverHashSampleBytes = 64 * 1024;

    public static TrackRecord? Read(string path, bool extractCover = true)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists) return null;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "无法访问文件：{File}", path);
            return null;
        }

        var record = new TrackRecord
        {
            Path = path,
            FileSize = info.Length,
            Mtime = new DateTimeOffset(info.LastWriteTimeUtc).ToUnixTimeSeconds(),
            AddedAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds()
        };

        try
        {
            using var file = TagLib.File.Create(path);

            var tag = file.Tag;
            if (tag is not null)
            {
                record.Title = Clean(tag.Title);
                record.Artist = Clean(tag.FirstPerformer) is { Length: > 0 } performer
                    ? performer
                    : Clean(tag.JoinedPerformers);
                record.Album = Clean(tag.Album);
                record.AlbumArtist = Clean(tag.FirstAlbumArtist) is { Length: > 0 } albumArtist
                    ? albumArtist
                    : Clean(tag.JoinedAlbumArtists);
                record.TrackNo = (int)Math.Min(tag.Track, int.MaxValue);
                record.DiscNo = (int)Math.Min(tag.Disc, int.MaxValue);
            }

            var properties = file.Properties;
            if (properties is not null)
            {
                record.DurationMs = (long)properties.Duration.TotalMilliseconds;
                record.SampleRate = properties.AudioSampleRate;
                record.BitDepth = properties.BitsPerSample;
                record.Bitrate = properties.AudioBitrate;
            }

            if (extractCover && tag is not null)
                record.CoverHash = TrySaveCover(tag);
        }
        catch (Exception ex)
        {
            // 标签坏了不代表放不了，退回文件名兜底，曲目照样入库
            Log.Debug(ex, "读取标签失败，改用文件名兜底：{File}", path);
        }

        FillFromFileName(record);
        record.RebuildSearchKey();
        return record;
    }

    /// <summary>
    /// 读取内嵌歌词（P3.1-③ 新需求）：ID3v2 USLT / APE Lyrics / VorbisComment LYRICS /
    /// MP4 ©lyr 等。TagLibSharp 的 <see cref="TagLib.Tag.Lyrics"/> 已做聚合；返回 null
    /// 表示文件没有内嵌歌词（也可能是标签读不出来）。内容可能是纯文本或带 LRC 时间轴，
    /// 由调用方按 LRC 解析决定展示形态。
    /// </summary>
    public static string? ReadLyrics(string path)
    {
        try
        {
            using var file = TagLib.File.Create(path);
            var lyrics = file.Tag?.Lyrics;
            return string.IsNullOrWhiteSpace(lyrics) ? null : lyrics.Trim();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "读取内嵌歌词失败：{File}", path);
            return null;
        }
    }

    /// <summary>
    /// 标签缺失时用文件名兜底。先剥掉开头的曲号（"01 - "、"01."、"01_"），
    /// 再按「歌手 - 标题」惯例拆分——否则 "01 - 周杰伦 - 晴天.flac" 会把 01 当成歌手。
    /// </summary>
    private static void FillFromFileName(TrackRecord record)
    {
        if (!string.IsNullOrWhiteSpace(record.Title) && !string.IsNullOrWhiteSpace(record.Artist))
            return;

        var name = System.IO.Path.GetFileNameWithoutExtension(record.Path).Trim();

        var trackNumberPrefix = TrackNumberPrefix().Match(name);
        if (trackNumberPrefix.Success)
        {
            if (record.TrackNo <= 0 && int.TryParse(trackNumberPrefix.Groups[1].Value, out var number))
                record.TrackNo = number;

            name = name[trackNumberPrefix.Length..].Trim();
        }

        const string separator = " - ";
        var index = name.IndexOf(separator, StringComparison.Ordinal);

        if (index > 0 && index + separator.Length < name.Length)
        {
            if (string.IsNullOrWhiteSpace(record.Artist))
                record.Artist = name[..index].Trim();
            if (string.IsNullOrWhiteSpace(record.Title))
                record.Title = name[(index + separator.Length)..].Trim();
        }
        else if (string.IsNullOrWhiteSpace(record.Title))
        {
            record.Title = name;
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^(\d{1,3})\s*[-._]\s*")]
    private static partial System.Text.RegularExpressions.Regex TrackNumberPrefix();

    private static string? TrySaveCover(TagLib.Tag tag)
    {
        try
        {
            var pictures = tag.Pictures;
            if (pictures is null || pictures.Length == 0) return null;

            var data = pictures[0].Data?.Data;
            if (data is null || data.Length < 128) return null;

            var hash = ComputeHash(data);
            var target = System.IO.Path.Combine(AppPaths.CoversDir, hash + ".jpg");

            if (!File.Exists(target))
            {
                Directory.CreateDirectory(AppPaths.CoversDir);
                // 同一张专辑的多条曲目会并发写同一个文件，写临时文件再原子替换
                var temp = target + "." + Environment.CurrentManagedThreadId + ".tmp";
                File.WriteAllBytes(temp, data);
                try { File.Move(temp, target, overwrite: true); }
                catch { try { File.Delete(temp); } catch { /* 忽略 */ } }
            }

            return hash;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "提取封面失败");
            return null;
        }
    }

    private static string ComputeHash(byte[] data)
    {
        var length = Math.Min(data.Length, CoverHashSampleBytes);
        Span<byte> digest = stackalloc byte[32];
        SHA256.HashData(data.AsSpan(0, length), digest);

        // 把总长度拌进去，进一步降低"前 64KB 相同"的碰撞概率
        return Convert.ToHexString(digest)[..24] + "-" + data.Length.ToString("x");
    }

    private static string Clean(string? value) => value?.Trim() ?? string.Empty;
}
