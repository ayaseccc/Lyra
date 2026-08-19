using System.Text.Json;
using Player.Core.Library;
using Serilog;

namespace Player.Core.Infra;

/// <summary>可恢复的真实播放上下文。当前曲目继续沿用 Ui.LastTrackPath。</summary>
public sealed class PersistedPlaybackContext
{
    public int Version { get; set; } = PlaybackContextStore.CurrentVersion;

    public string SourceName { get; set; } = string.Empty;

    public List<string> TrackPaths { get; set; } = new();
}

/// <summary>
/// 播放上下文侧车存储。队列变化时才写入；常规 config.json 保存不会触碰此文件。
/// </summary>
public static class PlaybackContextStore
{
    public const int CurrentVersion = 1;
    internal const int MaxTrackCount = 100_000;
    private const long MaxFileBytes = 32L * 1024 * 1024;
    private const int MaxSourceNameLength = 512;
    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public static PersistedPlaybackContext Capture(string sourceName, IEnumerable<TrackRecord> tracks)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        return Normalize(new PersistedPlaybackContext
        {
            SourceName = sourceName ?? string.Empty,
            TrackPaths = tracks
                .Select(track => track.Path)
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Take(MaxTrackCount)
                .ToList()
        });
    }

    /// <summary>按持久化顺序解析现有曲库；保留歌单中有意出现的重复曲目。</summary>
    public static IReadOnlyList<TrackRecord> Resolve(
        PersistedPlaybackContext? context,
        IEnumerable<TrackRecord> availableTracks)
    {
        ArgumentNullException.ThrowIfNull(availableTracks);
        if (context is null || context.Version != CurrentVersion || context.TrackPaths.Count == 0)
            return Array.Empty<TrackRecord>();

        var byPath = availableTracks
            .Where(track => !string.IsNullOrWhiteSpace(track.Path))
            .GroupBy(track => track.Path, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var resolved = new List<TrackRecord>(Math.Min(context.TrackPaths.Count, MaxTrackCount));
        foreach (var path in context.TrackPaths.Take(MaxTrackCount))
        {
            if (!string.IsNullOrWhiteSpace(path) && byPath.TryGetValue(path, out var track))
                resolved.Add(track);
        }
        return resolved;
    }

    public static PersistedPlaybackContext? Load(string? filePath = null)
    {
        var path = Path.GetFullPath(filePath ?? AppPaths.PlaybackContextFile);
        lock (Gate)
        {
            try
            {
                if (!File.Exists(path)) return null;
                if (new FileInfo(path).Length > MaxFileBytes)
                {
                    Log.Warning("播放上下文文件过大，已忽略");
                    return null;
                }

                var context = JsonSerializer.Deserialize<PersistedPlaybackContext>(
                    File.ReadAllText(path), JsonOptions);
                return context is null || context.Version != CurrentVersion
                    ? null
                    : Normalize(context);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "读取播放上下文失败，回退兼容恢复");
                return null;
            }
        }
    }

    public static bool Save(PersistedPlaybackContext context, string? filePath = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        var path = Path.GetFullPath(filePath ?? AppPaths.PlaybackContextFile);
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var json = JsonSerializer.Serialize(Normalize(context), JsonOptions);
                var temp = path + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, path, overwrite: true);
                Log.Debug("播放上下文已保存：{Count} 首", context.TrackPaths.Count);
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "保存播放上下文失败");
                return false;
            }
        }
    }

    private static PersistedPlaybackContext Normalize(PersistedPlaybackContext context)
    {
        var sourceName = context.SourceName?.Trim() ?? string.Empty;
        if (sourceName.Length > MaxSourceNameLength)
            sourceName = sourceName[..MaxSourceNameLength];

        return new PersistedPlaybackContext
        {
            Version = CurrentVersion,
            SourceName = sourceName,
            TrackPaths = (context.TrackPaths ?? new List<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Take(MaxTrackCount)
                .ToList()
        };
    }
}
