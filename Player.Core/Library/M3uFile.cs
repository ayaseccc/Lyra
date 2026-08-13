using Serilog;

namespace Player.Core.Library;

/// <summary>m3u8 播放列表的读写（PLAN 第 5 节）。统一 UTF-8，写出时优先用相对路径便于整包搬走。</summary>
public static class M3uFile
{
    public static IReadOnlyList<string> Read(string filePath)
    {
        var result = new List<string>();
        var baseDir = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? string.Empty;

        foreach (var raw in File.ReadAllLines(filePath))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;

            try
            {
                var path = Path.IsPathRooted(line) ? line : Path.GetFullPath(Path.Combine(baseDir, line));
                result.Add(path);
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "m3u 里的路径无法解析：{Line}", line);
            }
        }

        return result;
    }

    public static void Write(string filePath, IEnumerable<TrackRecord> tracks)
    {
        var baseDir = Path.GetDirectoryName(Path.GetFullPath(filePath)) ?? string.Empty;
        var lines = new List<string> { "#EXTM3U" };

        foreach (var track in tracks)
        {
            var seconds = (int)Math.Round(track.DurationMs / 1000.0);
            lines.Add($"#EXTINF:{seconds},{track.DisplayArtist} - {track.DisplayTitle}");
            lines.Add(MakeRelativeIfPossible(baseDir, track.Path));
        }

        File.WriteAllLines(filePath, lines, new System.Text.UTF8Encoding(false));
    }

    private static string MakeRelativeIfPossible(string baseDir, string fullPath)
    {
        try
        {
            if (string.IsNullOrEmpty(baseDir)) return fullPath;

            var relative = Path.GetRelativePath(baseDir, fullPath);
            // 只在确实位于播放列表所在目录之下时才用相对路径
            return relative.StartsWith("..", StringComparison.Ordinal) ? fullPath : relative;
        }
        catch
        {
            return fullPath;
        }
    }
}
