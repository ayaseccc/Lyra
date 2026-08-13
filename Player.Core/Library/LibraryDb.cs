using Microsoft.Data.Sqlite;
using Player.Core.Infra;
using Serilog;

namespace Player.Core.Library;

/// <summary>歌单（手工 / 未来的网易云同步歌单）。</summary>
public sealed class PlaylistRecord
{
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public int SortOrder { get; set; }

    /// <summary>local | netease（netease 到 P5 才用）。</summary>
    public string Source { get; set; } = "local";

    public string? SourceId { get; set; }

    public long? SyncedAt { get; set; }
}

/// <summary>tracks / playlists / settings 三张表的数据访问，手写 SQL。</summary>
public static class LibraryDb
{
    private const string TrackColumns =
        "id, path, title, artist, album, album_artist, track_no, disc_no, duration_ms, " +
        "sample_rate, bit_depth, bitrate, file_size, mtime, added_at, play_count, last_played, cover_hash";

    // ---------------- tracks ----------------

    public static List<TrackRecord> GetAllTracks()
    {
        var list = new List<TrackRecord>(capacity: 4096);

        using var connection = Db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT {TrackColumns} FROM tracks;";

        using var reader = command.ExecuteReader();
        while (reader.Read())
            list.Add(ReadTrack(reader));

        return list;
    }

    /// <summary>path → (id, mtime, fileSize)，增量扫描用来判断哪些文件变了。</summary>
    public static Dictionary<string, (long Id, long Mtime, long FileSize)> GetPathIndex()
    {
        var map = new Dictionary<string, (long, long, long)>(StringComparer.OrdinalIgnoreCase);

        using var connection = Db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, path, mtime, file_size FROM tracks;";

        using var reader = command.ExecuteReader();
        while (reader.Read())
            map[reader.GetString(1)] = (reader.GetInt64(0), reader.GetInt64(2), reader.GetInt64(3));

        return map;
    }

    /// <summary>批量写入（新增或更新）。一次事务 + 预编译语句，万级也是秒级。</summary>
    public static void UpsertTracks(IReadOnlyCollection<TrackRecord> tracks)
    {
        if (tracks.Count == 0) return;

        using var connection = Db.Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO tracks(path, title, artist, album, album_artist, track_no, disc_no,
                               duration_ms, sample_rate, bit_depth, bitrate, file_size, mtime,
                               added_at, cover_hash)
            VALUES(@path, @title, @artist, @album, @albumArtist, @trackNo, @discNo,
                   @durationMs, @sampleRate, @bitDepth, @bitrate, @fileSize, @mtime,
                   @addedAt, @coverHash)
            ON CONFLICT(path) DO UPDATE SET
                title = excluded.title, artist = excluded.artist, album = excluded.album,
                album_artist = excluded.album_artist, track_no = excluded.track_no,
                disc_no = excluded.disc_no, duration_ms = excluded.duration_ms,
                sample_rate = excluded.sample_rate, bit_depth = excluded.bit_depth,
                bitrate = excluded.bitrate, file_size = excluded.file_size,
                mtime = excluded.mtime, cover_hash = excluded.cover_hash;
            """;

        var pPath = command.Parameters.Add("@path", SqliteType.Text);
        var pTitle = command.Parameters.Add("@title", SqliteType.Text);
        var pArtist = command.Parameters.Add("@artist", SqliteType.Text);
        var pAlbum = command.Parameters.Add("@album", SqliteType.Text);
        var pAlbumArtist = command.Parameters.Add("@albumArtist", SqliteType.Text);
        var pTrackNo = command.Parameters.Add("@trackNo", SqliteType.Integer);
        var pDiscNo = command.Parameters.Add("@discNo", SqliteType.Integer);
        var pDuration = command.Parameters.Add("@durationMs", SqliteType.Integer);
        var pSampleRate = command.Parameters.Add("@sampleRate", SqliteType.Integer);
        var pBitDepth = command.Parameters.Add("@bitDepth", SqliteType.Integer);
        var pBitrate = command.Parameters.Add("@bitrate", SqliteType.Integer);
        var pFileSize = command.Parameters.Add("@fileSize", SqliteType.Integer);
        var pMtime = command.Parameters.Add("@mtime", SqliteType.Integer);
        var pAddedAt = command.Parameters.Add("@addedAt", SqliteType.Integer);
        var pCoverHash = command.Parameters.Add("@coverHash", SqliteType.Text);

        foreach (var track in tracks)
        {
            pPath.Value = track.Path;
            pTitle.Value = track.Title;
            pArtist.Value = track.Artist;
            pAlbum.Value = track.Album;
            pAlbumArtist.Value = track.AlbumArtist;
            pTrackNo.Value = track.TrackNo;
            pDiscNo.Value = track.DiscNo;
            pDuration.Value = track.DurationMs;
            pSampleRate.Value = track.SampleRate;
            pBitDepth.Value = track.BitDepth;
            pBitrate.Value = track.Bitrate;
            pFileSize.Value = track.FileSize;
            pMtime.Value = track.Mtime;
            pAddedAt.Value = track.AddedAt;
            pCoverHash.Value = (object?)track.CoverHash ?? DBNull.Value;
            command.ExecuteNonQuery();
        }

        transaction.Commit();
        Log.Debug("写入曲目 {Count} 条", tracks.Count);
    }

    public static void DeleteTracks(IReadOnlyCollection<long> trackIds)
    {
        if (trackIds.Count == 0) return;

        using var connection = Db.Open();
        using var transaction = connection.BeginTransaction();

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM tracks WHERE id = @id;";
            var pId = command.Parameters.Add("@id", SqliteType.Integer);

            using var cleanup = connection.CreateCommand();
            cleanup.Transaction = transaction;
            cleanup.CommandText = "DELETE FROM playlist_items WHERE track_id = @id;";
            var pCleanupId = cleanup.Parameters.Add("@id", SqliteType.Integer);

            foreach (var id in trackIds)
            {
                pId.Value = id;
                command.ExecuteNonQuery();
                pCleanupId.Value = id;
                cleanup.ExecuteNonQuery();
            }
        }

        transaction.Commit();
        Log.Debug("删除曲目 {Count} 条", trackIds.Count);
    }

    public static void IncrementPlayCount(long trackId)
    {
        try
        {
            using var connection = Db.Open();
            using var command = connection.CreateCommand();
            command.CommandText =
                "UPDATE tracks SET play_count = play_count + 1, last_played = @now WHERE id = @id;";
            command.Parameters.AddWithValue("@now", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            command.Parameters.AddWithValue("@id", trackId);
            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "更新播放次数失败 trackId={Id}", trackId);
        }
    }

    private static TrackRecord ReadTrack(SqliteDataReader reader)
    {
        var track = new TrackRecord
        {
            Id = reader.GetInt64(0),
            Path = reader.GetString(1),
            Title = reader.GetString(2),
            Artist = reader.GetString(3),
            Album = reader.GetString(4),
            AlbumArtist = reader.GetString(5),
            TrackNo = reader.GetInt32(6),
            DiscNo = reader.GetInt32(7),
            DurationMs = reader.GetInt64(8),
            SampleRate = reader.GetInt32(9),
            BitDepth = reader.GetInt32(10),
            Bitrate = reader.GetInt32(11),
            FileSize = reader.GetInt64(12),
            Mtime = reader.GetInt64(13),
            AddedAt = reader.GetInt64(14),
            PlayCount = reader.GetInt32(15),
            LastPlayed = reader.IsDBNull(16) ? null : reader.GetInt64(16),
            CoverHash = reader.IsDBNull(17) ? null : reader.GetString(17)
        };

        track.RebuildSearchKey();
        return track;
    }

    // ---------------- playlists ----------------

    public static List<PlaylistRecord> GetPlaylists()
    {
        var list = new List<PlaylistRecord>();

        using var connection = Db.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, name, sort_order, source, source_id, synced_at FROM playlists ORDER BY sort_order, id;";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            list.Add(new PlaylistRecord
            {
                Id = reader.GetInt64(0),
                Name = reader.GetString(1),
                SortOrder = reader.GetInt32(2),
                Source = reader.GetString(3),
                SourceId = reader.IsDBNull(4) ? null : reader.GetString(4),
                SyncedAt = reader.IsDBNull(5) ? null : reader.GetInt64(5)
            });
        }

        return list;
    }

    public static long CreatePlaylist(string name, string source = "local", string? sourceId = null)
    {
        using var connection = Db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO playlists(name, sort_order, source, source_id)
            VALUES(@name, COALESCE((SELECT MAX(sort_order) + 1 FROM playlists), 0), @source, @sourceId);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("@name", name);
        command.Parameters.AddWithValue("@source", source);
        command.Parameters.AddWithValue("@sourceId", (object?)sourceId ?? DBNull.Value);

        return (long)(command.ExecuteScalar() ?? 0L);
    }

    public static void RenamePlaylist(long playlistId, string newName)
    {
        using var connection = Db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "UPDATE playlists SET name = @name WHERE id = @id;";
        command.Parameters.AddWithValue("@name", newName);
        command.Parameters.AddWithValue("@id", playlistId);
        command.ExecuteNonQuery();
    }

    public static void DeletePlaylist(long playlistId)
    {
        using var connection = Db.Open();
        using var transaction = connection.BeginTransaction();

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText =
                "DELETE FROM playlist_items WHERE playlist_id = @id; DELETE FROM playlists WHERE id = @id;";
            command.Parameters.AddWithValue("@id", playlistId);
            command.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    /// <summary>按 position 顺序取歌单里的本地曲目 id（P1 只有本地条目，在线条目 P5 再说）。</summary>
    public static List<long> GetPlaylistTrackIds(long playlistId)
    {
        var ids = new List<long>();

        using var connection = Db.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT track_id FROM playlist_items WHERE playlist_id = @id AND track_id IS NOT NULL ORDER BY position;";
        command.Parameters.AddWithValue("@id", playlistId);

        using var reader = command.ExecuteReader();
        while (reader.Read())
            ids.Add(reader.GetInt64(0));

        return ids;
    }

    /// <summary>整表替换歌单内容（新增、删除、拖拽排序统一走这里，简单可靠）。</summary>
    public static void ReplacePlaylistItems(long playlistId, IReadOnlyList<long> trackIds)
    {
        using var connection = Db.Open();
        using var transaction = connection.BeginTransaction();

        using (var delete = connection.CreateCommand())
        {
            delete.Transaction = transaction;
            delete.CommandText = "DELETE FROM playlist_items WHERE playlist_id = @id;";
            delete.Parameters.AddWithValue("@id", playlistId);
            delete.ExecuteNonQuery();
        }

        using (var insert = connection.CreateCommand())
        {
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO playlist_items(playlist_id, position, track_id) VALUES(@pid, @pos, @tid);";
            var pPid = insert.Parameters.Add("@pid", SqliteType.Integer);
            var pPos = insert.Parameters.Add("@pos", SqliteType.Integer);
            var pTid = insert.Parameters.Add("@tid", SqliteType.Integer);
            pPid.Value = playlistId;

            for (var i = 0; i < trackIds.Count; i++)
            {
                pPos.Value = i;
                pTid.Value = trackIds[i];
                insert.ExecuteNonQuery();
            }
        }

        transaction.Commit();
    }

    // ---------------- settings ----------------

    public static string? GetSetting(string key)
    {
        using var connection = Db.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key = @key;";
        command.Parameters.AddWithValue("@key", key);
        return command.ExecuteScalar() as string;
    }

    public static void SetSetting(string key, string value)
    {
        using var connection = Db.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            "INSERT INTO settings(key, value) VALUES(@key, @value) " +
            "ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@value", value);
        command.ExecuteNonQuery();
    }
}
