using System.Collections.Concurrent;
using System.Diagnostics;
using Player.Core.Audio;
using Serilog;

namespace Player.Core.Library;

public sealed record ScanProgress(string Phase, int Processed, int Total)
{
    public double Percent => Total <= 0 ? 0 : Math.Min(100, Processed * 100.0 / Total);
}

public sealed class ScanResult
{
    public int Scanned { get; init; }

    public int AddedOrUpdated { get; init; }

    public int Removed { get; init; }

    public TimeSpan Elapsed { get; init; }

    public bool Cancelled { get; init; }

    public override string ToString() =>
        $"共 {Scanned} 个文件，写入 {AddedOrUpdated}，移除 {Removed}，耗时 {Elapsed.TotalSeconds:0.0}s";
}

/// <summary>
/// 目录扫描器（PLAN 第 5 节）：全量 + 增量。
/// 增量判据是 mtime + 文件大小；标签读取并行、写库分批走事务，万级曲库目标 ≤ 2 分钟。
/// 整个过程跑在后台线程，绝不碰 UI。
/// </summary>
public static class LibraryScanner
{
    private const int WriteBatchSize = 2000;

    public static Task<ScanResult> ScanAsync(
        IReadOnlyList<string> roots,
        bool fullRescan,
        IProgress<ScanProgress>? progress = null,
        CancellationToken cancellationToken = default)
        => Task.Run(() => Scan(roots, fullRescan, progress, cancellationToken), cancellationToken);

    private static ScanResult Scan(
        IReadOnlyList<string> roots,
        bool fullRescan,
        IProgress<ScanProgress>? progress,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        Log.Information("开始{Mode}扫描，根目录 {Count} 个", fullRescan ? "全量" : "增量", roots.Count);

        // ---- 1. 枚举文件 ----
        progress?.Report(new ScanProgress("正在枚举文件", 0, 0));
        var (files, completeRoots) = EnumerateAudioFiles(roots, cancellationToken);
        Log.Information("枚举到音频文件 {Count} 个", files.Count);

        if (cancellationToken.IsCancellationRequested)
            return new ScanResult { Cancelled = true, Elapsed = stopwatch.Elapsed };

        // ---- 2. 与库内现状比对 ----
        var index = LibraryDb.GetPathIndex();
        var present = new HashSet<string>(files.Count, StringComparer.OrdinalIgnoreCase);
        var needRead = new List<FileEntry>();

        foreach (var file in files)
        {
            present.Add(file.Path);

            if (!fullRescan &&
                index.TryGetValue(file.Path, out var existing) &&
                existing.Mtime == file.Mtime &&
                existing.FileSize == file.Size)
            {
                continue; // 没变，跳过
            }

            needRead.Add(file);
        }

        // 只清理"位于本次成功扫描过的根目录之下、但文件已经不在了"的曲目。
        // 两类曲目因此被保护：① 根目录本次不可访问（U 盘拔了、网络盘断了）——误删会连带
        // 清空 playlist_items，盘回来后 id 变化导致歌单永久丢失；② 用户手动拖进歌单、
        // 位于任何根目录之外的曲目——它们不归扫描管。
        var normalizedReachable = completeRoots
            .Select(r => r.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .ToList();

        var unreachableCount = roots.Count(r => !string.IsNullOrWhiteSpace(r)) - normalizedReachable.Count;
        if (unreachableCount > 0)
            Log.Warning("有 {Count} 个根目录本次不可访问，其下曲目一律保留", unreachableCount);

        var removedIds = FindMissingTrackIds(index, present, normalizedReachable);

        Log.Information("需读取标签 {Read} 个，需移除 {Removed} 个", needRead.Count, removedIds.Count);

        // ---- 3. 并行读标签 ----
        var records = new ConcurrentBag<TrackRecord>();
        var processed = 0;
        var reported = 0;

        if (needRead.Count > 0)
        {
            progress?.Report(new ScanProgress("正在读取标签", 0, needRead.Count));

            var options = new ParallelOptions
            {
                CancellationToken = cancellationToken,
                MaxDegreeOfParallelism = Math.Max(2, Environment.ProcessorCount)
            };

            try
            {
                Parallel.ForEach(needRead, options, file =>
                {
                    var record = TagReader.Read(file.Path);
                    if (record is not null)
                    {
                        // TagReader is also used by manual imports. Scans overwrite
                        // its legacy seconds stamp with the precise enumeration stamp.
                        record.Mtime = file.Mtime;
                        record.FileSize = file.Size;
                        records.Add(record);
                    }

                    var done = Interlocked.Increment(ref processed);
                    if (done % 25 != 0 && done != needRead.Count) return;

                    // 多线程下上报顺序可能倒挂，只让进度单调递增，进度条才不会回跳
                    var previous = Volatile.Read(ref reported);
                    if (done > previous && Interlocked.CompareExchange(ref reported, done, previous) == previous)
                        progress?.Report(new ScanProgress("正在读取标签", done, needRead.Count));
                });
            }
            catch (OperationCanceledException)
            {
                Log.Information("扫描被取消");
                return new ScanResult { Cancelled = true, Elapsed = stopwatch.Elapsed, Scanned = files.Count };
            }
        }

        // ---- 4. 写库（分批事务） ----
        var all = records.ToList();
        if (all.Count > 0)
        {
            progress?.Report(new ScanProgress("正在写入媒体库", 0, all.Count));

            for (var offset = 0; offset < all.Count; offset += WriteBatchSize)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var batch = all.GetRange(offset, Math.Min(WriteBatchSize, all.Count - offset));
                LibraryDb.UpsertTracks(batch);
                progress?.Report(new ScanProgress("正在写入媒体库", offset + batch.Count, all.Count));
            }
        }

        // ---- 5. 清理已消失的文件 ----
        if (removedIds.Count > 0)
            LibraryDb.DeleteTracks(removedIds);

        stopwatch.Stop();

        var result = new ScanResult
        {
            Scanned = files.Count,
            AddedOrUpdated = all.Count,
            Removed = removedIds.Count,
            Elapsed = stopwatch.Elapsed
        };

        Log.Information("扫描完成：{Result}", result);
        return result;
    }

    private readonly record struct FileEntry(string Path, long Mtime, long Size);

    internal static long ToMtimeStamp(DateTime lastWriteTimeUtc) =>
        lastWriteTimeUtc.ToUniversalTime().Ticks;

    internal static List<long> FindMissingTrackIds(
        IReadOnlyDictionary<string, (long Id, long Mtime, long FileSize)> index,
        IReadOnlySet<string> present,
        IReadOnlyList<string> completeRoots)
    {
        return index
            .Where(kv => !present.Contains(kv.Key) && IsUnderAnyRoot(kv.Key, completeRoots))
            .Select(kv => kv.Value.Id)
            .ToList();
    }

    /// <summary>路径是否位于给定的某个根目录之下（按目录分隔符对齐，避免 D:\Music 命中 D:\MusicVideos）。</summary>
    public static bool IsUnderAnyRoot(string path, IReadOnlyList<string> roots)
    {
        foreach (var root in roots)
        {
            if (root.Length == 0) continue;
            if (!path.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
            if (path.Length == root.Length) return true;

            var next = path[root.Length];
            if (next == Path.DirectorySeparatorChar || next == Path.AltDirectorySeparatorChar)
                return true;
        }

        return false;
    }

    internal static EnumerationOptions CreateEnumerationOptions() => new()
    {
        RecurseSubdirectories = true,
        // Deletion is allowed only after a complete traversal. Silently skipping
        // an inaccessible child would make its existing tracks look deleted.
        IgnoreInaccessible = false,
        AttributesToSkip = FileAttributes.System
    };

    /// <returns>枚举到的文件，以及本次**完整枚举**的根目录列表。</returns>
    private static (List<FileEntry> Files, List<string> ReachableRoots) EnumerateAudioFiles(
        IReadOnlyList<string> roots, CancellationToken cancellationToken)
    {
        var list = new List<FileEntry>(capacity: 4096);
        var reachable = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var options = CreateEnumerationOptions();

        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                Log.Warning("媒体库根目录不存在或不可访问，跳过：{Root}", root);
                continue;
            }

            try
            {
                foreach (var info in new DirectoryInfo(root).EnumerateFiles("*", options))
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!AudioFormats.IsSupported(info.Name)) continue;
                    if (!seen.Add(info.FullName)) continue; // 根目录互相嵌套时去重

                    list.Add(new FileEntry(
                        info.FullName,
                        ToMtimeStamp(info.LastWriteTimeUtc),
                        info.Length));
                }

                reachable.Add(root);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "枚举根目录失败：{Root}", root);
            }
        }

        return (list, reachable);
    }
}
