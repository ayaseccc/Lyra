using System.Collections.Frozen;
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
    private readonly object _watcherLifecycleGate = new();
    private readonly SemaphoreSlim _scanLock = new(1, 1);
    private readonly LibraryRescanQueue _rescanQueue = new();
    private LibrarySnapshot _snapshot = LibrarySnapshot.Empty;

    // The lifetime token cancels both an active scan and callers waiting for the
    // single-flight gate.  The gate itself is intentionally kept alive after
    // Dispose: a waiter may still be unwinding and calling Release in finally.
    private readonly CancellationTokenSource _lifetimeCts = new();
    private CancellationTokenSource? _scanCts;
    private readonly object _scanCtsGate = new();
    private int _isScanning;
    private int _disposed;

    public LibraryService()
    {
        _watcher.ChangesSettled += OnWatcherChangesSettled;
    }

    /// <summary>曲库快照。扫描结束后整体替换，界面拿到的永远是一致的一份。</summary>
    public IReadOnlyList<TrackRecord> Tracks
    {
        get
        {
            var snapshot = Volatile.Read(ref _snapshot);
            return snapshot.Tracks;
        }
    }

    public IReadOnlyList<string> Roots => ConfigService.Current.Library.Folders;

    public bool IsScanning => Volatile.Read(ref _isScanning) != 0;

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

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _lifetimeCts.Token);

        try
        {
            await _scanLock.WaitAsync(linkedCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new ScanResult { Cancelled = true };
        }

        if (IsDisposed)
        {
            _scanLock.Release();
            return new ScanResult { Cancelled = true };
        }

        try
        {
            Volatile.Write(ref _isScanning, 1);
            ScanStarted?.Invoke(this, EventArgs.Empty);

            lock (_scanCtsGate)
            {
                // ScanAsync is single-flight, so the previous source has already
                // completed.  Do not dispose it from a new caller while its
                // finally block may still be observing the token.
                _scanCts = linkedCts;
            }

            var progress = new Progress<ScanProgress>(p => ScanProgressChanged?.Invoke(this, p));

            var result = await LibraryScanner
                .ScanAsync(roots, fullRescan, progress, linkedCts.Token)
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
            Volatile.Write(ref _isScanning, 0);
            lock (_scanCtsGate)
            {
                if (ReferenceEquals(_scanCts, linkedCts))
                    _scanCts = null;
            }
            _scanLock.Release();
        }
    }

    public void CancelScan()
    {
        CancellationTokenSource? scan;
        lock (_scanCtsGate) scan = _scanCts;

        try { scan?.Cancel(); }
        catch (Exception ex) { Log.Debug(ex, "取消扫描失败"); }
    }

    public void StartWatching()
    {
        lock (_watcherLifecycleGate)
        {
            if (!IsDisposed) _watcher.Start(Roots);
        }
    }

    public void StopWatching()
    {
        lock (_watcherLifecycleGate)
        {
            if (!IsDisposed) _watcher.Stop();
        }
    }

    /// <summary>
    /// 把一批文件/文件夹并入曲库并返回对应曲目（拖到歌单上、或"添加文件"用）。
    /// 已在库的直接复用，不在库的读标签后入库——**即使它们不在任何媒体库根目录下**，
    /// 扫描器不会删除根目录之外的曲目，所以这类手动加入的歌不会莫名消失。
    /// </summary>
    public async Task<IReadOnlyList<TrackRecord>> ImportFilesAsync(
        IEnumerable<string> paths, CancellationToken cancellationToken = default)
    {
        var files = await Task.Run(() => ExpandToAudioFiles(paths), cancellationToken).ConfigureAwait(false);
        if (files.Count == 0) return Array.Empty<TrackRecord>();

        var missing = files.Where(f => GetByPath(f) is null).ToList();

        if (missing.Count > 0)
        {
            await Task.Run(() =>
            {
                var records = new List<TrackRecord>(missing.Count);
                foreach (var path in missing)
                {
                    var record = TagReader.Read(path);
                    if (record is not null) records.Add(record);
                }

                if (records.Count > 0)
                {
                    LibraryDb.UpsertTracks(records);
                    SwapSnapshot(LibraryDb.GetAllTracks());   // 重新载入才能拿到自增 id
                }
            }, cancellationToken).ConfigureAwait(false);

            Log.Information("手动导入 {Count} 个文件进曲库", missing.Count);
        }

        // 按用户给的顺序返回
        var result = new List<TrackRecord>(files.Count);
        foreach (var file in files)
        {
            var track = GetByPath(file);
            if (track is not null) result.Add(track);
        }

        return result;
    }

    /// <summary>移除某个根目录时，把它下面的曲目一并清出曲库（扫描器已不再负责这件事）。</summary>
    public void RemoveTracksUnderRoot(string root)
    {
        var normalized = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var snapshot = Volatile.Read(ref _snapshot);
        var ids = snapshot.Tracks
            .Where(t => LibraryScanner.IsUnderAnyRoot(t.Path, new[] { normalized }))
            .Select(t => t.Id)
            .ToList();

        if (ids.Count == 0) return;

        LibraryDb.DeleteTracks(ids);
        SwapSnapshot(LibraryDb.GetAllTracks());
        Log.Information("已移除根目录 {Root} 下的 {Count} 首曲目", normalized, ids.Count);
    }

    private static List<string> ExpandToAudioFiles(IEnumerable<string> paths)
    {
        var result = new List<string>();
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };

        foreach (var path in paths)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    result.AddRange(Directory.EnumerateFiles(path, "*", options)
                        .Where(Audio.AudioFormats.IsSupported)
                        .OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
                }
                else if (File.Exists(path) && Audio.AudioFormats.IsSupported(path))
                {
                    result.Add(path);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "展开路径失败：{Path}", path);
            }
        }

        return result;
    }

    private void OnWatcherChangesSettled(object? sender, EventArgs e)
    {
        RequestRescan();
    }

    private void RequestRescan()
    {
        if (!_rescanQueue.Request(out var shouldSchedule) || !shouldSchedule)
            return;

        _ = Task.Run(ProcessPendingRescansAsync);
    }

    private async Task ProcessPendingRescansAsync()
    {
        while (_rescanQueue.TryTake())
        {
            try
            {
                Log.Information("检测到曲库目录变化，执行增量扫描");
                await ScanAsync(fullRescan: false).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // ScanAsync normally converts failures to ScanResult.  Keep this
                // guard so an unexpected pre-scan failure cannot strand worker ownership.
                Log.Error(ex, "目录变化触发的增量扫描失败");
            }
        }
    }

    private void SwapSnapshot(List<TrackRecord> tracks)
    {
        var snapshot = BuildSnapshot(tracks, Roots.ToArray());
        Volatile.Write(ref _snapshot, snapshot);
        LibraryChanged?.Invoke(this, EventArgs.Empty);
    }

    private static LibrarySnapshot BuildSnapshot(
        IReadOnlyList<TrackRecord> tracks, IReadOnlyList<string> roots)
    {
        // 先固定曲目序列，再以同一批对象构建全部索引与聚合；发布前没有任何读者可见。
        var stableTracks = tracks.ToArray();
        var byId = new Dictionary<long, TrackRecord>(tracks.Count);
        var byPath = new Dictionary<string, TrackRecord>(tracks.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var track in stableTracks)
        {
            byId[track.Id] = track;
            byPath[track.Path] = track;
        }

        // 三份聚合在当前线程（扫描时即后台线程）先算好，
        // 免得界面刷新时在 UI 线程上对万级曲库做 GroupBy 造成卡顿
        var albums = BuildAlbums(stableTracks);
        var artists = BuildArtists(stableTracks);
        var folders = BuildFolderPlaylists(stableTracks, roots);

        return new LibrarySnapshot(
            Array.AsReadOnly(stableTracks),
            byId.ToFrozenDictionary(),
            byPath.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            albums.AsReadOnly(),
            artists.AsReadOnly(),
            folders.AsReadOnly());
    }

    /// <summary>Harness 专用：不触碰数据库或配置，按生产路径构建并原子发布测试快照。</summary>
    internal void ReplaceSnapshotForTest(
        IReadOnlyList<TrackRecord> tracks, IReadOnlyList<string>? roots = null)
    {
        ArgumentNullException.ThrowIfNull(tracks);
        var snapshot = BuildSnapshot(tracks, roots ?? Array.Empty<string>());
        Volatile.Write(ref _snapshot, snapshot);
    }

    // ---------------- 查询 ----------------

    public TrackRecord? GetById(long id)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        return snapshot.ById.TryGetValue(id, out var track) ? track : null;
    }

    public TrackRecord? GetByPath(string path)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        return snapshot.ByPath.TryGetValue(path, out var track) ? track : null;
    }

    public IReadOnlyList<TrackRecord> GetTracksByIds(IEnumerable<long> ids)
    {
        var snapshot = Volatile.Read(ref _snapshot);
        var result = new List<TrackRecord>();
        foreach (var id in ids)
        {
            if (snapshot.ById.TryGetValue(id, out var track))
                result.Add(track);
        }
        return result;
    }

    public IReadOnlyList<AlbumGroup> GetAlbums()
    {
        var snapshot = Volatile.Read(ref _snapshot);
        return snapshot.Albums;
    }

    public IReadOnlyList<ArtistGroup> GetArtists()
    {
        var snapshot = Volatile.Read(ref _snapshot);
        return snapshot.Artists;
    }

    public IReadOnlyList<FolderPlaylist> GetFolderPlaylists()
    {
        var snapshot = Volatile.Read(ref _snapshot);
        return snapshot.Folders;
    }

    // ---------------- 聚合 ----------------

    private static List<AlbumGroup> BuildAlbums(IReadOnlyList<TrackRecord> tracks)
    {
        return tracks
            .GroupBy(t => t.DisplayAlbum + "\u0001" + t.DisplayAlbumArtist, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var ordered = group
                    .OrderBy(t => t.DiscNo)
                    .ThenBy(t => t.TrackNo)
                    .ThenBy(t => t.DisplayTitle, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                var first = ordered[0];
                return new AlbumGroup
                {
                    Key = group.Key,
                    Album = first.DisplayAlbum,
                    AlbumArtist = first.DisplayAlbumArtist,
                    CoverHash = ordered.FirstOrDefault(t => t.CoverHash is not null)?.CoverHash,
                    TotalDurationMs = ordered.Sum(t => t.DurationMs),
                    Tracks = ordered.AsReadOnly()
                };
            })
            .OrderBy(a => a.Album, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static List<ArtistGroup> BuildArtists(IReadOnlyList<TrackRecord> tracks)
    {
        return tracks
            .GroupBy(t => t.DisplayArtist, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                var ordered = group
                    .OrderBy(t => t.DisplayAlbum, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(t => t.DiscNo)
                    .ThenBy(t => t.TrackNo)
                    .ToList();

                return new ArtistGroup
                {
                    Name = group.Key,
                    AlbumCount = ordered.Select(t => t.DisplayAlbum)
                        .Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                    CoverHash = ordered.FirstOrDefault(t => t.CoverHash is not null)?.CoverHash,
                    Tracks = ordered.AsReadOnly()
                };
            })
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 文件夹虚拟歌单：把每首曲目归到「所属根目录下的顶层子文件夹」。
    /// 直接躺在根目录下的散曲不产生歌单（它们仍然在「全部歌曲」里）。
    /// </summary>
    private static List<FolderPlaylist> BuildFolderPlaylists(
        IReadOnlyList<TrackRecord> tracks, IReadOnlyList<string> roots)
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
                    .OrderBy(t => t.Path, StringComparer.OrdinalIgnoreCase)
                    .ToList()
                    .AsReadOnly()
            })
            .ToList();

        // 不同根目录下出现同名文件夹时，补上根目录名以便区分
        var duplicateNames = playlists
            .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

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
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>根目录的显示名。盘符根（D:\）取不到文件夹名，退回盘符本身。</summary>
    public static string RootDisplayName(string root)
    {
        var trimmed = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }

    /// <summary>一次发布的完整只读视图；构造完成后不再改变。</summary>
    private sealed class LibrarySnapshot
    {
        internal static readonly LibrarySnapshot Empty = new(
            Array.Empty<TrackRecord>(),
            new Dictionary<long, TrackRecord>().ToFrozenDictionary(),
            new Dictionary<string, TrackRecord>(StringComparer.OrdinalIgnoreCase)
                .ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            Array.Empty<AlbumGroup>(),
            Array.Empty<ArtistGroup>(),
            Array.Empty<FolderPlaylist>());

        internal LibrarySnapshot(
            IReadOnlyList<TrackRecord> tracks,
            FrozenDictionary<long, TrackRecord> byId,
            FrozenDictionary<string, TrackRecord> byPath,
            IReadOnlyList<AlbumGroup> albums,
            IReadOnlyList<ArtistGroup> artists,
            IReadOnlyList<FolderPlaylist> folders)
        {
            Tracks = tracks;
            ById = byId;
            ByPath = byPath;
            Albums = albums;
            Artists = artists;
            Folders = folders;
        }

        internal IReadOnlyList<TrackRecord> Tracks { get; }
        internal FrozenDictionary<long, TrackRecord> ById { get; }
        internal FrozenDictionary<string, TrackRecord> ByPath { get; }
        internal IReadOnlyList<AlbumGroup> Albums { get; }
        internal IReadOnlyList<ArtistGroup> Artists { get; }
        internal IReadOnlyList<FolderPlaylist> Folders { get; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // Close scheduling first.  A watcher callback already in flight either
        // records work before this point (then Close drops it) or is rejected.
        _rescanQueue.Close();

        lock (_watcherLifecycleGate)
        {
            _watcher.ChangesSettled -= OnWatcherChangesSettled;
            _watcher.Dispose();
        }

        try { _lifetimeCts.Cancel(); } catch { /* 忽略 */ }
        CancelScan();

        // Do not dispose _scanLock or _lifetimeCts here.  Dispose is synchronous
        // while ScanAsync is deliberately awaitable; releasing either primitive
        // before the active scan reaches its finally block reintroduces the
        // shutdown race this service is meant to avoid.  Both are owned by this
        // short-lived service and become collectible once the scan unwinds.
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;
}

