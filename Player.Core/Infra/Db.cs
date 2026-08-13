using Microsoft.Data.Sqlite;
using Serilog;

namespace Player.Core.Infra;

/// <summary>
/// SQLite 连接与建表（PLAN 第 5 节 schema）。手写轻量 DAL，不引 EF。
/// 库文件固定在 data/library.db，随程序目录一起搬走。
/// </summary>
public static class Db
{
    private const int SchemaVersion = 2;

    private static string? _connectionString;

    public static string DatabasePath { get; private set; } = string.Empty;

    public static bool IsInitialized => _connectionString is not null;

    public static void Initialize(string? databasePath = null)
    {
        AppPaths.EnsureCreated();

        DatabasePath = databasePath ?? AppPaths.DatabaseFile;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true
        }.ToString();

        EnsureSchema();
        Log.Information("媒体库数据库就绪：{Path}", DatabasePath);
    }

    public static SqliteConnection Open()
    {
        if (_connectionString is null)
            throw new InvalidOperationException("Db 尚未初始化，请先调用 Db.Initialize()");

        var connection = new SqliteConnection(_connectionString);
        connection.Open();

        using var pragma = connection.CreateCommand();
        // WAL：扫描写入与界面读取可以并行，避免扫描时列表卡住
        // busy_timeout：万一撞上写事务，等一会儿而不是直接抛 SQLITE_BUSY
        pragma.CommandText =
            "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; " +
            "PRAGMA temp_store=MEMORY; PRAGMA busy_timeout=5000;";
        pragma.ExecuteNonQuery();

        return connection;
    }

    private static void EnsureSchema()
    {
        using var connection = Open();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;

        command.CommandText = """
            CREATE TABLE IF NOT EXISTS tracks(
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                path         TEXT NOT NULL UNIQUE,
                title        TEXT NOT NULL DEFAULT '',
                artist       TEXT NOT NULL DEFAULT '',
                album        TEXT NOT NULL DEFAULT '',
                album_artist TEXT NOT NULL DEFAULT '',
                track_no     INTEGER NOT NULL DEFAULT 0,
                disc_no      INTEGER NOT NULL DEFAULT 0,
                duration_ms  INTEGER NOT NULL DEFAULT 0,
                sample_rate  INTEGER NOT NULL DEFAULT 0,
                bit_depth    INTEGER NOT NULL DEFAULT 0,
                bitrate      INTEGER NOT NULL DEFAULT 0,
                file_size    INTEGER NOT NULL DEFAULT 0,
                mtime        INTEGER NOT NULL DEFAULT 0,
                added_at     INTEGER NOT NULL DEFAULT 0,
                play_count   INTEGER NOT NULL DEFAULT 0,
                last_played  INTEGER,
                cover_hash   TEXT,
                netease_id   INTEGER
            );

            CREATE INDEX IF NOT EXISTS idx_tracks_album  ON tracks(album);
            CREATE INDEX IF NOT EXISTS idx_tracks_artist ON tracks(artist);

            CREATE TABLE IF NOT EXISTS playlists(
                id         INTEGER PRIMARY KEY AUTOINCREMENT,
                name       TEXT NOT NULL,
                sort_order INTEGER NOT NULL DEFAULT 0,
                source     TEXT NOT NULL DEFAULT 'local',
                source_id  TEXT,
                synced_at  INTEGER
            );

            CREATE TABLE IF NOT EXISTS playlist_items(
                playlist_id        INTEGER NOT NULL,
                position           INTEGER NOT NULL,
                track_id           INTEGER,
                online_id          TEXT,
                online_title       TEXT,
                online_artist      TEXT,
                online_album       TEXT,
                online_duration_ms INTEGER,
                PRIMARY KEY(playlist_id, position)
            );

            CREATE INDEX IF NOT EXISTS idx_playlist_items_track ON playlist_items(track_id);

            CREATE TABLE IF NOT EXISTS lyrics_cache(
                cache_key    TEXT PRIMARY KEY,
                content_json TEXT,
                fetched_at   INTEGER
            );

            CREATE TABLE IF NOT EXISTS settings(
                key   TEXT PRIMARY KEY,
                value TEXT
            );
            """;
        command.ExecuteNonQuery();

        // 旧库迁移（幂等）：tracks 表补 netease_id 列，歌词匹配结果持久化用。
        // 扫描 Upsert 不碰这一列，只有 LyricsService 显式写入，重扫不会冲掉用户的匹配。
        // 注意：索引必须等列加好之后再建，否则旧库上 CREATE INDEX 会整批失败。
        EnsureTrackColumns(connection);

        using (var index = connection.CreateCommand())
        {
            index.Transaction = transaction;
            index.CommandText = "CREATE INDEX IF NOT EXISTS idx_tracks_netease ON tracks(netease_id);";
            index.ExecuteNonQuery();
        }

        command.CommandText = "INSERT INTO settings(key, value) VALUES('schema_version', @v) " +
                              "ON CONFLICT(key) DO UPDATE SET value = excluded.value;";
        command.Parameters.AddWithValue("@v", SchemaVersion.ToString());
        command.ExecuteNonQuery();

        transaction.Commit();
    }

    private static void EnsureTrackColumns(SqliteConnection connection)
    {
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(tracks);";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                if (string.Equals(reader.GetString(1), "netease_id", StringComparison.OrdinalIgnoreCase))
                    return;
            }
        }

        using var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE tracks ADD COLUMN netease_id INTEGER;";
        alter.ExecuteNonQuery();
        Log.Information("tracks 表已迁移：新增 netease_id 列");
    }
}
