using Player.Core.Infra;
using Serilog;

namespace Player.Core.Library;

/// <summary>
/// 媒体库门面：内存快照 + 扫描调度 + 目录监听 + 三种聚合视图（专辑 / 艺术家 / 文件夹虚拟歌单）。
/// 万级曲库的过滤、排序、分组全部在内存里完成，SQL 只负责持久化。
/// </summary>
public sealed class LibraryService : IDisposable
{
    private readonly LibraryWatcher _watcher = new();
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly object _cacheGate = new();

    private List<TrackRecord> _tracks = new();
    private Dictionary<long, TrackRecord> _byId = new();
    private Dictionary<string, TrackRecord> _byPath = new(StringComparer.OrdinalIgnoreCase);

    private List<AlbumGroup>? _albumCache;
    private List<ArtistGroup>? _artistCache;
    private List<FolderPlaylist>? _folderCache;

    private CancellationTokenSource? _scanCts;
    private volatile bool _rescanPending;
    private bool _disposed;

    public LibraryService()
    {
        _watcher.ChangesSettled += OnWatcherChangesSettled;
    }

    /// <summary>曲库快照。扫描结束后整体替换，界面拿到的永远是一致的一份。</summary>
    public IReadOnlyList<TrackRecord> Tracks => _tracks;

    public IReadOnlyList<string> Roots => ConfigService.Current.Library.Folders;

    public bool IsScanning { get; private set; }

    /// <summary>曲库内容变了（载入完成 / 扫描完成）。可能在后台线程触发。</summary>
    public event EventHandler? LibraryChanged;

    /// <summary>扫描开始（含目录监听自动触发的那次）。可能在后台线程触发。</summary>
    public event EventHandler? ScanStarted;

    public event EventHandler<ScanProgress>? ScanProgressChanged;

    public event EventHandler<ScanResult>? ScanCompleted;

    // ---------------- 载入与扫描 ----------------

    /// <summary>从数据库载入曲库快照（启动时调用，10000 曲约百毫秒级）。</summary>
    public void Load()
    {
        try
        {
            var tracks = LibraryDb.GetAllTracks();
            SwapSnapshot(tracks);
            Log.Information("曲库已载入：{Count} 首", tracks.Count);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "载入曲库失败");
        }
    }

    public async Task<ScanResult> ScanAsync(bool fullRescan, CancellationToken cancellationToken = default)
    {
        var roots = Roots.ToList();
        if (roots.Count == 0)
        {
            Log.Information("没有配置媒体库根目录，跳过扫描");
            return new ScanResult();
        }

        await _scanLock.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            IsScanning = true;
            ScanStarted?.Invoke(this, EventArgs.Empty);

            _scanCts?.Dispose();
            _scanCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            var progress = new Progress<ScanProgress>(p => ScanProgressChanged?.Invoke(this, p));

            var result = await LibraryScanner
                .ScanAsync(roots, fullRescan, progress, _scanCts.Token)
                .ConfigureAwait(false);

            if (!result.Cancelled)
            {
                // 重新载入快照，让专辑 / 艺术家 / 文件夹歌单一起刷新
                var tracks = LibraryDb.GetAllTracks();
                SwapSnapshot(tracks);
            }

            ScanCompleted?.Invoke(this, result);
            return result;
        }
        catch (OperationCanceledException)
        {
            return new ScanResult { Cancelled = true };
        }
        catch (Exception ex)
        {
            Log.Error(ex, "扫描失败");
            return new ScanResult();
        }
        finally
        {
            IsScanning = false;
            _scanLock.Release();

            // 扫描期间攒下的目录变化，扫完补跑一次，免得被永久漏掉
            if (_rescanPending && !_disposed)
            {
                _rescanPending = false;
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await Task.Delay(500).ConfigureAwait(false);
                        await ScanAsync(fullRescan: false).ConfigureAwait(false);
                    }
                    catch (Exception ex) { Log.Debug(ex, "补跑增量扫描失败"); }
                });
            }
        }
    }

    public void CancelScan()
    {
        try { _scanCts?.Cancel(); }
        catch (Exception ex) { Log.Debug(ex, "取消扫描失败"); }
    }

    public void StartWatching() => _watcher.Start(Roots);

    public void StopWatching() => _watcher.Stop();

    private async void OnWatcherChangesSettled(object? sender, EventArgs e)
    {
        if (_disposed) return;

        if (IsScanning)
        {
            // 正在扫描，先记下来，扫完统一补一次
            _rescanPending = true;
            return;
        }

        Log.Information("检测到曲库目录变化，执行增量扫描");
        try { await ScanAsync(fullRescan: false).ConfigureAwait(false); }
        catch (Exception ex) { Log.Error(ex, "目录变化触发的增量扫描失败"); }
    }

    private void SwapSnapshot(List<TrackRecord> tracks)
    {
        var byId = new Dictionary<long, TrackRecord>(tracks.Count);
        var byPath = new Dictionary<string, TrackRecord>(tracks.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var track in tracks)
        {
            byId[track.Id] = track;
            byPath[track.Path] = track;
        }

        // 三份聚合在当前线程（扫描时即后台线程）先算好，
        // 免得界面刷新时在 UI 线程上对万级曲库做 GroupBy 造成卡顿
        var albums = BuildAlbums(tracks);
        var artists = BuildArtists(tracks);
        var folders = BuildFolderPlaylists(tracks, Roots);

        lock (_cacheGate)
        {
            _tracks = tracks;
            _byId = byId;
            _byPath = byPath;
            _albumCache = albums;
            _artistCache = artists;
            _folderCache = folders;
        }

        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---------------- 查询 ----------------

    public TrackRecord? GetById(long id) => _byId.TryGetValue(id, out var track) ? track : null;

    public TrackRecord? GetByPath(string path) => _byPath.TryGetValue(path, out var track) ? track : null;

    public IReadOnlyList<TrackRecord> GetTracksByIds(IEnumerable<long> ids)
    {
        var result = new List<TrackRecord>();
        foreach (var id in ids)
        {
            if (_byId.TryGetValue(id, out var track))
                result.Add(track);
        }
        return result;
    }

    public IReadOnlyList<AlbumGroup> GetAlbums()
    {
        lock (_cacheGate)
        {
            return _albumCache ??= BuildAlbums(_tracks);
        }
    }

    public IReadOnlyList<ArtistGroup> GetArtists()
    {
        lock (_cacheGate)
        {
            return _artistCache ??= BuildArtists(_tracks);
        }
    }

    public IReadOnlyList<FolderPlaylist> GetFolderPlaylists()
    {
        lock (_cacheGate)
        {
            return _folderCache ??= BuildFolderPlaylists(_tracks, Roots);
        }
    }

    // ---------------- 聚合 ----------------

    private static List<AlbumGroup> BuildAlbums(List<TrackRecord> tracks)
    {
        return tracks
            .GroupBy(t => t.DisplayAlbum + "\u0001" + t.DisplayAlbumArtist, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var ordered = group
                    .OrderBy(t => t.DiscNo)
                    .ThenBy(t => t.TrackNo)
                    .ThenBy(t => t.DisplayTitle, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();

                var first = ordered[0];
                return new AlbumGroup
                {
                    Key = group.Key,
                    Album = first.DisplayAlbum,
                    AlbumArtist = first.DisplayAlbumArtist,
                    CoverHash = ordered.FirstOrDefault(t => t.CoverHash is not null)?.CoverHash,
                    TotalDurationMs = ordered.Sum(t => t.DurationMs),
                    Tracks = ordered
                };
            })
            .OrderBy(a => a.Album, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static List<ArtistGroup> BuildArtists(List<TrackRecord> tracks)
    {
        return tracks
            .GroupBy(t => t.DisplayArtist, StringComparer.CurrentCultureIgnoreCase)
            .Select(group =>
            {
                var ordered = group
                    .OrderBy(t => t.DisplayAlbum, StringComparer.CurrentCultureIgnoreCase)
                    .ThenBy(t => t.DiscNo)
                    .ThenBy(t => t.TrackNo)
                    .ToList();

                return new ArtistGroup
                {
                    Name = group.Key,
                    AlbumCount = ordered.Select(t => t.DisplayAlbum)
                        .Distinct(StringComparer.CurrentCultureIgnoreCase).Count(),
                    CoverHash = ordered.FirstOrDefault(t => t.CoverHash is not null)?.CoverHash,
                    Tracks = ordered
                };
            })
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 文件夹虚拟歌单：把每首曲目归到「所属根目录下的顶层子文件夹」。
    /// 直接躺在根目录下的散曲不产生歌单（它们仍然在「全部歌曲」里）。
    /// </summary>
    private static List<FolderPlaylist> BuildFolderPlaylists(List<TrackRecord> tracks, IReadOnlyList<string> roots)
    {
        if (roots.Count == 0 || tracks.Count == 0) return new List<FolderPlaylist>();

        var normalizedRoots = roots
            .Where(r => !string.IsNullOrWhiteSpace(r))
            .Select(r => r.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            // 先长后短：嵌套根目录时优先归到更具体的那个
            .OrderByDescending(r => r.Length)
            .ToList();

        var buckets = new Dictionary<string, (string Name, string Root, List<TrackRecord> Tracks)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var track in tracks)
        {
            var root = normalizedRoots.FirstOrDefault(r =>
                track.Path.Length > r.Length + 1 &&
                track.Path.StartsWith(r, StringComparison.OrdinalIgnoreCase) &&
                (track.Path[r.Length] == Path.DirectorySeparatorChar ||
                 track.Path[r.Length] == Path.AltDirectorySeparatorChar));

            if (root is null) continue;

            var relative = track.Path[(root.Length + 1)..];
            var separatorIndex = relative.IndexOfAny(new[]
                { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });

            if (separatorIndex <= 0) continue; // 散在根目录下的文件不算歌单

            var topFolder = relative[..separatorIndex];
            var fullPath = Path.Combine(root, topFolder);

            if (!buckets.TryGetValue(fullPath, out var bucket))
            {
                bucket = (topFolder, root, new List<TrackRecord>());
                buckets[fullPath] = bucket;
            }

            bucket.Tracks.Add(track);
        }

        var playlists = buckets
            .Select(kv => new
            {
                FullPath = kv.Key,
                kv.Value.Name,
                kv.Value.Root,
                Tracks = (IReadOnlyList<TrackRecord>)kv.Value.Tracks
                    .OrderBy(t => t.Path, StringComparer.CurrentCultureIgnoreCase)
                    .ToList()
            })
            .ToList();

        // 不同根目录下出现同名文件夹时，补上根目录名以便区分
        var duplicateNames = playlists
            .GroupBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.CurrentCultureIgnoreCase);

        return playlists
            .Select(p => new FolderPlaylist
            {
                Name = duplicateNames.Contains(p.Name)
                    ? $"{p.Name}（{RootDisplayName(p.Root)}）"
                    : p.Name,
                FullPath = p.FullPath,
                Root = p.Root,
                CoverHash = p.Tracks.FirstOrDefault(t => t.CoverHash is not null)?.CoverHash,
                Tracks = p.Tracks
            })
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>根目录的显示名。盘符根（D:\）取不到文件夹名，退回盘符本身。</summary>
    public static string RootDisplayName(string root)
    {
        var trimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _watcher.ChangesSettled -= OnWatcherChangesSettled;
        _watcher.Dispose();

        try { _scanCts?.Cancel(); } catch { /* 忽略 */ }
        _scanCts?.Dispose();
        _scanLock.Dispose();
    }
}
