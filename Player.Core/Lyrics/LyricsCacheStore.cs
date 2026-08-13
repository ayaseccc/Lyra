using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Player.Core.Infra;
using Serilog;

namespace Player.Core.Lyrics;

/// <summary>歌词缓存的持久化形状（lyrics_cache 表 content_json）。</summary>
public sealed class CachedLyric
{
    [JsonPropertyName("lrc")]
    public string Lrc { get; set; } = string.Empty;

    [JsonPropertyName("tlyric")]
    public string TranslatedLrc { get; set; } = string.Empty;

    [JsonPropertyName("romalrc")]
    public string RomajiLrc { get; set; } = string.Empty;
}

/// <summary>
/// 歌词相关的小型持久化（手写 SQL，走 Db.Open）：
/// ① lyrics_cache 表 —— 网易云歌词永久缓存（PLAN 第 6 节：宁可多缓存）；
/// ② tracks.netease_id —— 本地曲目匹配到的网易云 ID（重扫不覆盖，见 Db 迁移注释）；
/// ③ 用户手动偏移 —— 按曲目路径存，来源无关（.lrc 文件或在线歌词都适用）。
/// </summary>
public static class LyricsCacheStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    // ---------------- 歌词内容缓存 ----------------

    public static CachedLyric? GetCached(string cacheKey)
    {
        try
        {
            using var connection = Db.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT content_json FROM lyrics_cache WHERE cache_key = @key;";
            command.Parameters.AddWithValue("@key", cacheKey);

            var json = command.ExecuteScalar() as string;
            if (string.IsNullOrWhiteSpace(json)) return null;

            return JsonSerializer.Deserialize<CachedLyric>(json, JsonOptions);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "读取歌词缓存失败：{Key}", cacheKey);
            return null;
        }
    }

    public static void SaveCached(string cacheKey, CachedLyric lyric)
    {
        try
        {
            using var connection = Db.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO lyrics_cache(cache_key, content_json, fetched_at)
                VALUES(@key, @json, @at)
                ON CONFLICT(cache_key) DO UPDATE SET
                    content_json = excluded.content_json, fetched_at = excluded.fetched_at;
                """;
            command.Parameters.AddWithValue("@key", cacheKey);
            command.Parameters.AddWithValue("@json", JsonSerializer.Serialize(lyric, JsonOptions));
            command.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "写入歌词缓存失败：{Key}", cacheKey);
        }
    }

    // ---------------- 网易云 ID 匹配结果 ----------------

    /// <summary>path（不区分大小写）→ netease_id。启动时全量载入内存，避免每次查库。</summary>
    public static Dictionary<string, long> LoadNeteaseIds()
    {
        var map = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var connection = Db.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT path, netease_id FROM tracks WHERE netease_id IS NOT NULL;";

            using var reader = command.ExecuteReader();
            while (reader.Read())
                map[reader.GetString(0)] = reader.GetInt64(1);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "读取网易云 ID 映射失败");
        }

        return map;
    }

    public static void SaveNeteaseId(string path, long neteaseId)
    {
        try
        {
            using var connection = Db.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE tracks SET netease_id = @id WHERE path = @path;";
            command.Parameters.AddWithValue("@id", neteaseId);
            command.Parameters.AddWithValue("@path", path);
            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "保存网易云 ID 失败：{Path}", path);
        }
    }

    public static void ClearNeteaseId(string path)
    {
        try
        {
            using var connection = Db.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE tracks SET netease_id = NULL WHERE path = @path;";
            command.Parameters.AddWithValue("@path", path);
            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "清除网易云 ID 失败：{Path}", path);
        }
    }

    // ---------------- 用户手动偏移 ----------------

    /// <summary>来源偏好 key（按曲目路径散列，同 offset 方案）。</summary>
    public static string PreferenceKey(string path)
    {
        var bytes = System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(path.ToLowerInvariant()));
        return "pref:" + Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    public static string? GetLyricPreference(string path)
    {
        try
        {
            using var connection = Db.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT content_json FROM lyrics_cache WHERE cache_key = @key;";
            command.Parameters.AddWithValue("@key", PreferenceKey(path));
            return command.ExecuteScalar() as string;
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "读取来源偏好失败：{Path}", path);
            return null;
        }
    }

    public static void SaveLyricPreference(string path, string preference)
    {
        try
        {
            using var connection = Db.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO lyrics_cache(cache_key, content_json, fetched_at)
                VALUES(@key, @json, @at)
                ON CONFLICT(cache_key) DO UPDATE SET content_json = excluded.content_json;
                """;
            command.Parameters.AddWithValue("@key", PreferenceKey(path));
            command.Parameters.AddWithValue("@json", preference);
            command.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "保存来源偏好失败：{Path}", path);
        }
    }

    /// <summary>偏移 key 与来源无关，按曲目路径散列，避免把路径写进缓存表。</summary>
    public static string OffsetKey(string path)
    {
        var bytes = System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(path.ToLowerInvariant()));
        return "offset:" + Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    public static TimeSpan? GetManualOffset(string path)
    {
        try
        {
            using var connection = Db.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT content_json FROM lyrics_cache WHERE cache_key = @key;";
            command.Parameters.AddWithValue("@key", OffsetKey(path));

            var raw = command.ExecuteScalar() as string;
            if (raw is null || !int.TryParse(raw, out var ms)) return null;
            return TimeSpan.FromMilliseconds(ms);
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "读取手动偏移失败：{Path}", path);
            return null;
        }
    }

    public static void SaveManualOffset(string path, TimeSpan offset)
    {
        try
        {
            using var connection = Db.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO lyrics_cache(cache_key, content_json, fetched_at)
                VALUES(@key, @json, @at)
                ON CONFLICT(cache_key) DO UPDATE SET content_json = excluded.content_json;
                """;
            command.Parameters.AddWithValue("@key", OffsetKey(path));
            command.Parameters.AddWithValue("@json", ((int)Math.Round(offset.TotalMilliseconds)).ToString());
            command.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "保存手动偏移失败：{Path}", path);
        }
    }
}
