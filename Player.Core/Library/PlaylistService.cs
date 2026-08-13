using Serilog;

namespace Player.Core.Library;

/// <summary>
/// 歌单服务（PLAN 第 5 节 + P1 新需求）。P1 覆盖三类歌单能力：
/// ① 手工歌单：增删改、成员增删、拖拽排序；② m3u8 导入导出；
/// ③ 文件夹虚拟歌单：只读，由 <see cref="LibraryService"/> 从曲库路径推导，不落库。
/// 它同时取代 P0 的 PlaybackQueue 成为播放列表的来源。
/// </summary>
public sealed class PlaylistService
{
    private readonly LibraryService _library;

    private List<PlaylistRecord> _playlists = new();

    public PlaylistService(LibraryService library)
    {
        _library = library;
    }

    /// <summary>手工歌单（source=local）。文件夹虚拟歌单不在这里，见 <see cref="GetFolderPlaylists"/>。</summary>
    public IReadOnlyList<PlaylistRecord> Playlists => _playlists;

    public event EventHandler? PlaylistsChanged;

    public void Load()
    {
        try
        {
            _playlists = LibraryDb.GetPlaylists();
            Log.Information("歌单已载入：{Count} 个", _playlists.Count);
            PlaylistsChanged?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "载入歌单失败");
        }
    }

    public IReadOnlyList<FolderPlaylist> GetFolderPlaylists() => _library.GetFolderPlaylists();

    // ---------------- 手工歌单 ----------------

    public long Create(string name)
    {
        var id = LibraryDb.CreatePlaylist(string.IsNullOrWhiteSpace(name) ? "新建歌单" : name.Trim());
        Load();
        return id;
    }

    public void Rename(long playlistId, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName)) return;
        LibraryDb.RenamePlaylist(playlistId, newName.Trim());
        Load();
    }

    public void Delete(long playlistId)
    {
        LibraryDb.DeletePlaylist(playlistId);
        Load();
    }

    public IReadOnlyList<TrackRecord> GetTracks(long playlistId)
    {
        var ids = LibraryDb.GetPlaylistTrackIds(playlistId);
        return _library.GetTracksByIds(ids);
    }

    /// <summary>整表替换歌单内容——新增、移除、拖拽排序都走它。</summary>
    public void SetTracks(long playlistId, IEnumerable<TrackRecord> tracks)
    {
        LibraryDb.ReplacePlaylistItems(playlistId, tracks.Select(t => t.Id).ToList());
        PlaylistsChanged?.Invoke(this, EventArgs.Empty);
    }

    public void AddTracks(long playlistId, IEnumerable<TrackRecord> tracks)
    {
        var existing = GetTracks(playlistId).ToList();
        var existingIds = existing.Select(t => t.Id).ToHashSet();

        foreach (var track in tracks)
        {
            if (existingIds.Add(track.Id))
                existing.Add(track);
        }

        SetTracks(playlistId, existing);
    }

    public void RemoveTracks(long playlistId, IEnumerable<TrackRecord> tracks)
    {
        var removing = tracks.Select(t => t.Id).ToHashSet();
        var remaining = GetTracks(playlistId).Where(t => !removing.Contains(t.Id));
        SetTracks(playlistId, remaining);
    }

    // ---------------- m3u8 ----------------

    /// <summary>导入 m3u8：按路径匹配库内曲目，库外的条目跳过（数量在返回值里）。</summary>
    public (long PlaylistId, int Matched, int Skipped) ImportM3u(string filePath)
    {
        var paths = M3uFile.Read(filePath);

        var matched = new List<TrackRecord>();
        var skipped = 0;

        foreach (var path in paths)
        {
            var track = _library.GetByPath(path);
            if (track is null) skipped++;
            else matched.Add(track);
        }

        var name = Path.GetFileNameWithoutExtension(filePath);
        var id = Create(string.IsNullOrWhiteSpace(name) ? "导入的歌单" : name);
        SetTracks(id, matched);

        Log.Information("导入 m3u8 完成：匹配 {Matched} 首，跳过 {Skipped} 首", matched.Count, skipped);
        return (id, matched.Count, skipped);
    }

    public void ExportM3u(IEnumerable<TrackRecord> tracks, string filePath)
    {
        M3uFile.Write(filePath, tracks);
        Log.Information("已导出 m3u8：{File}", filePath);
    }
}
