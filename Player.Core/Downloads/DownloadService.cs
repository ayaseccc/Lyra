using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Player.Core.Infra;
using Player.Core.Library;
using Player.Core.Online;
using Serilog;

namespace Player.Core.Downloads;

public enum DownloadStatus
{
    Queued,
    Downloading,
    Completed,
    Failed,
    /// <summary>与库内已有曲目重复（标题+歌手同且时长差 &lt;2s），等待用户确认。</summary>
    Duplicate,
    /// <summary>用户取消（排队中或下载中被取消）。</summary>
    Cancelled
}

/// <summary>一个下载任务（在线曲目 → 落盘）。</summary>
public sealed class DownloadItem
{
    public required OnlineTrack Track { get; init; }

    public required string SourceKey { get; init; }

    public required int PreferredBr { get; init; }

    public required string FileName { get; init; }

    public DownloadStatus Status { get; internal set; } = DownloadStatus.Queued;

    public int ProgressPercent { get; internal set; }

    public string? TargetPath { get; internal set; }

    public string? Error { get; internal set; }

    /// <summary>实际下载音质（服务端返回）。</summary>
    public int ActualBr { get; internal set; }

    /// <summary>入队时确定的目标目录（下载时弹窗选择；空则回落配置值）。</summary>
    internal string? DownloadDir { get; set; }

    /// <summary>单任务取消令牌（取消当前任务不影响队列后续任务）。</summary>
    internal CancellationTokenSource CancelCts { get; } = new();

    public string DisplayTitle => Track.Name;

    public string DisplayArtist => Track.ArtistLine;

    public bool IsDone => Status is DownloadStatus.Completed or DownloadStatus.Failed or DownloadStatus.Cancelled;

    public bool IsCancellable => Status is DownloadStatus.Queued or DownloadStatus.Downloading;
}

/// <summary>
/// P4 下载服务：串行队列（任务间隔 ≥4s）、失败自动重试 1 次、
/// TagLibSharp 写标签+封面、有词写同名 .lrc、命名模板落下载目录、完成后触发入库扫描。
/// 重复检测（标题+歌手同且时长差 &lt;2s）标记 Duplicate 等待确认。
/// </summary>
public sealed class DownloadService : IDisposable
{
    private static readonly TimeSpan TaskGap = TimeSpan.FromSeconds(4);

    private readonly OnlineSources _sources;
    private readonly LibraryService _library;
    private readonly HttpClient _http;
    private readonly ConcurrentQueue<DownloadItem> _queue = new();

    /// <summary>已结束的任务（完成/失败/取消/重复等待确认），保留最近若干条。
    /// 页面重建（下载完成触发扫描 → 下载管理页刷新）后仍能显示历史任务（实机反馈：取消后行消失）。</summary>
    private readonly List<DownloadItem> _history = new();
    private const int HistoryLimit = 50;

    private readonly object _gate = new();
    private DownloadItem? _current;
    private CancellationTokenSource? _cts;
    private bool _disposed;

    public DownloadService(OnlineSources sources, LibraryService library)
    {
        _sources = sources;
        _library = library;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Player/1.0");
    }

    /// <summary>任一任务状态/进度变化（UI 绑定用）。</summary>
    public event Action<DownloadItem>? ItemChanged;

    /// <summary>全部完成后由调用方触发媒体库增量扫描（App 层接）。</summary>
    public event Action? BatchCompleted;

    public DownloadItem? Current => _current;

    public IReadOnlyList<DownloadItem> Snapshot()
    {
        lock (_gate)
        {
            var list = new List<DownloadItem>();
            if (_current is not null) list.Add(_current);
            list.AddRange(_queue);
            list.AddRange(_history);   // 历史（最新在前）
            return list;
        }
    }

    /// <summary>任务进入终态时归档（历史列表，超出上限丢最旧）。</summary>
    private void AddHistory(DownloadItem item)
    {
        lock (_gate)
        {
            if (_history.Contains(item)) return;
            _history.Insert(0, item);
            if (_history.Count > HistoryLimit)
                _history.RemoveAt(_history.Count - 1);
        }
    }

    /// <summary>从历史里摘除（重复确认后重新入队时用）。</summary>
    private void RemoveFromHistory(DownloadItem item)
    {
        lock (_gate)
        {
            _history.Remove(item);
        }
    }

    /// <summary>入队（先查库内重复，命中则标记 Duplicate 等待确认）。服务已释放时返回 Failed 项（不抛）。
    /// downloadDir：下载时弹窗选择的目标目录（不传则回落配置值）。</summary>
    public DownloadItem Enqueue(OnlineTrack track, string sourceKey, int preferredBr,
        Func<DownloadItem, bool>? confirmDuplicate = null, string? downloadDir = null)
    {
        if (_disposed)
        {
            return new DownloadItem
            {
                Track = track,
                SourceKey = sourceKey,
                PreferredBr = preferredBr,
                FileName = BuildFileName(track, sourceKey, preferredBr),
                Status = DownloadStatus.Failed,
                Error = "下载服务已释放"
            };
        }

        var item = new DownloadItem
        {
            Track = track,
            SourceKey = sourceKey,
            PreferredBr = preferredBr,
            FileName = BuildFileName(track, sourceKey, preferredBr),
            DownloadDir = string.IsNullOrWhiteSpace(downloadDir) ? null : downloadDir
        };

        if (IsDuplicateInLibrary(track))
        {
            item.Status = DownloadStatus.Duplicate;
            item.Error = "与媒体库中已有曲目重复";
            if (confirmDuplicate is null || !confirmDuplicate(item))
            {
                AddHistory(item);   // 等待确认也算终态：切页后还能看到
                Raise(item);
                return item;
            }
            RemoveFromHistory(item);
            item.Status = DownloadStatus.Queued;
            item.Error = null;
        }

        _queue.Enqueue(item);
        Raise(item);
        _ = Task.Run(RunAsync);
        return item;
    }

    /// <summary>确认重复后强制入队下载。</summary>
    public void ConfirmDuplicate(DownloadItem item)
    {
        if (item.Status != DownloadStatus.Duplicate) return;
        RemoveFromHistory(item);
        item.Status = DownloadStatus.Queued;
        item.Error = null;
        Raise(item);
        _queue.Enqueue(item);
        _ = Task.Run(RunAsync);
    }

    /// <summary>取消重复项（丢弃）。</summary>
    public void CancelDuplicate(DownloadItem item)
    {
        if (item.Status != DownloadStatus.Duplicate) return;
        item.Status = DownloadStatus.Failed;
        item.Error = "已取消（库内重复）";
        Raise(item);
    }

    /// <summary>取消任务：下载中 → 取消当前下载（队列继续）；排队中 → 移出队列。</summary>
    public void Cancel(DownloadItem item)
    {
        lock (_gate)
        {
            if (ReferenceEquals(item, _current))
            {
                item.CancelCts.Cancel();
                return;
            }
        }

        // 排队中的任务：从队列摘除并标记（worker 并发出队时可能已被取走，届时走 _current 分支）
        if (item.Status == DownloadStatus.Queued)
        {
            var rest = new ConcurrentQueue<DownloadItem>();
            DownloadItem? removed = null;
            while (_queue.TryDequeue(out var q))
            {
                if (ReferenceEquals(q, item)) removed = q;
                else rest.Enqueue(q);
            }
            while (rest.TryDequeue(out var r)) _queue.Enqueue(r);

            if (removed is not null)
            {
                item.Status = DownloadStatus.Cancelled;
                item.Error = null;
                Raise(item);
            }
        }
    }

    private async Task RunAsync()
    {
        lock (_gate)
        {
            if (_current is not null || _cts is not null) return;   // 已在跑
            _cts = new CancellationTokenSource();
        }

        var ct = _cts.Token;
        try
        {
            while (true)
            {
                DownloadItem? item;
                lock (_gate)
                {
                    // 锁内取任务；取不到=队列确定空，原子退出并清 _cts（修复：此前
                    // "空队列退出"与"清 _cts"分两把锁，中间入队的新任务会被新 worker 误判
                    // 已在跑而丢弃——最后一条下载被静默吞掉，审查发现）
                    if (!_queue.TryDequeue(out item))
                    {
                        _current = null;
                        _cts = null;
                        break;
                    }
                    _current = item;
                }

                try
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, item.CancelCts.Token);
                    await ProcessItemAsync(item, linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (item.CancelCts.IsCancellationRequested)
                {
                    // 用户取消当前任务：标记后继续下一个（不中断队列）
                    item.Status = DownloadStatus.Cancelled;
                    item.Error = null;
                    Raise(item);
                    AddHistory(item);
                }

                // 任务间隔 ≥4s（PLAN：串行队列；锁外延时）
                if (!_queue.IsEmpty)
                    await Task.Delay(TaskGap, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            lock (_gate) { _current = null; _cts = null; }
        }

        BatchCompleted?.Invoke();   // 后台线程触发；订阅方（媒体库扫描）也是后台，安全
    }

    private async Task ProcessItemAsync(DownloadItem item, CancellationToken ct)
    {
        item.Status = DownloadStatus.Downloading;
        item.ProgressPercent = 0;
        Raise(item);

        // ① 取流（音质降级链在源内部处理），失败重试 1 次
        var source = _sources.Get(item.SourceKey);
        if (source is null)
        {
            Fail(item, "音源不可用");
            return;
        }

        var stream = await source.GetStreamAsync(item.Track, item.PreferredBr, ct).ConfigureAwait(false);
        if (!stream.Success)
        {
            stream = await source.GetStreamAsync(item.Track, item.PreferredBr, ct).ConfigureAwait(false);
            if (!stream.Success)
            {
                Fail(item, "取流失败：" + stream.Error);
                return;
            }
        }

        item.ActualBr = stream.Data!.ActualBr;

        // ② 下载到临时文件（目录：入队时弹窗选择的优先，回落配置值）
        var dir = item.DownloadDir ?? ConfigService.Current.Online.DownloadDir;
        if (string.IsNullOrWhiteSpace(dir))
        {
            Fail(item, "未设置下载目录");
            return;
        }

        var ext = DownloadTemplater.ExtensionFromUrl(stream.Data.Url);
        var tempPath = Path.Combine(Path.GetTempPath(), "player-dl-" + Guid.NewGuid().ToString("N")[..8] + ext);
        try
        {
            var ok = await DownloadToFileAsync(stream.Data.Url, tempPath, item, ct).ConfigureAwait(false);
            if (!ok) return;   // Fail 已在下载函数内处理
        }
        catch (OperationCanceledException)
        {
            TryDelete(tempPath);
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(tempPath);
            Log.Warning(ex, "下载文件失败：{Url}", stream.Data.Url);
            Fail(item, "下载失败：" + ex.Message);
            return;
        }

        // ③ 目标路径（模板）并写标签/封面/lrc
        try
        {
            var relative = DownloadTemplater.Render(ConfigService.Current.Online.NamingTemplate,
                new Dictionary<string, string>
                {
                    ["AlbumArtist"] = item.Track.Artists.FirstOrDefault() ?? string.Empty,
                    ["Album"] = item.Track.Album,
                    ["TrackNo"] = string.Empty,
                    ["Title"] = item.Track.Name
                });
            var target = Path.Combine(dir, relative.Trim(Path.DirectorySeparatorChar, ' ') + ext);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);

            await WriteTagsAsync(tempPath, item, source, ct).ConfigureAwait(false);

            if (File.Exists(target)) TryDelete(target);
            File.Move(tempPath, target);
            item.TargetPath = target;

            // 音频落盘成功后再写 .lrc（审查：先写 lrc 会让 File.Move 失败时留下孤立歌词文件）
            await WriteLyricIfAnyAsync(target, item, source, ct).ConfigureAwait(false);
            item.Status = DownloadStatus.Completed;
            item.ProgressPercent = 100;
            Raise(item);
            AddHistory(item);
            Log.Information("下载完成：{Target}（实际 {Br}）", target, item.ActualBr);
        }
        catch (OperationCanceledException)
        {
            TryDelete(tempPath);
            throw;
        }
        catch (Exception ex)
        {
            TryDelete(tempPath);
            Log.Warning(ex, "写标签/落盘失败：{Name}", item.Track.Name);
            Fail(item, "写入失败：" + ex.Message);
        }
    }

    private async Task<bool> DownloadToFileAsync(string url, string tempPath, DownloadItem item, CancellationToken ct)
    {
        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            Fail(item, $"下载失败（HTTP {(int)response.StatusCode}）");
            return false;
        }

        var total = response.Content.Headers.ContentLength ?? 0;
        await using var src = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024);
        var buffer = new byte[64 * 1024];
        long written = 0;
        while (true)
        {
            var n = await src.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (n == 0) break;
            await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            written += n;
            if (total > 0) item.ProgressPercent = (int)(written * 100 / total);
            Raise(item);
        }

        return true;
    }

    /// <summary>TagLibSharp 写标签（标题/歌手/专辑/曲号）+ 封面（在线取图下载）。</summary>
    private static async Task WriteTagsAsync(string path, DownloadItem item, IOnlineSource source, CancellationToken ct)
    {
        TagLib.File tagFile = TagLib.File.Create(path);
        try
        {
            tagFile.Tag.Title = item.Track.Name;
            tagFile.Tag.Performers = item.Track.Artists.ToArray();
            tagFile.Tag.Album = item.Track.Album;
            tagFile.Tag.Track = 0;

            // 封面：在线取图（GD 需要 pic 端点；网易云直链）
            var pic = await source.GetPicUrlAsync(item.Track, 500, ct).ConfigureAwait(false);
            // OnlineResult<T>.Data is nullable even when Success=true (the result
            // wrapper intentionally does not encode that invariant in its type).
            // Capture it locally and validate before handing it to APIs that require
            // a non-null URL.  This also keeps a malformed provider response from
            // turning an otherwise successful download into a NullReference/argument
            // failure while preserving the existing best-effort cover behaviour.
            var picUrl = pic.Data;
            if (pic.Success && !string.IsNullOrWhiteSpace(picUrl))
            {
                try
                {
                    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
                    var bytes = await client.GetByteArrayAsync(picUrl, ct).ConfigureAwait(false);
                    if (bytes.Length > 0)
                    {
                        var ext2 = DownloadTemplater.ExtensionFromUrl(picUrl);
                        var picture = new TagLib.Picture(new TagLib.ByteVector(bytes))
                        {
                            MimeType = ext2 is ".png" ? "image/png" : "image/jpeg"
                        };
                        tagFile.Tag.Pictures = new[] { picture };
                    }
                }
                catch (Exception ex)
                {
                    Log.Debug(ex, "封面下载失败：{Url}", pic.Data);
                }
            }

            tagFile.Save();
        }
        finally
        {
            tagFile.Dispose();
        }
    }

    private static async Task WriteLyricIfAnyAsync(string targetPath, DownloadItem item, IOnlineSource source, CancellationToken ct)
    {
        var lyric = await source.GetLyricAsync(item.Track, ct).ConfigureAwait(false);
        if (!lyric.Success || string.IsNullOrWhiteSpace(lyric.Data?.Lrc)) return;

        var lrcPath = Path.ChangeExtension(targetPath, ".lrc");
        await File.WriteAllTextAsync(lrcPath, lyric.Data.Lrc, System.Text.Encoding.UTF8, ct).ConfigureAwait(false);
    }

    private bool IsDuplicateInLibrary(OnlineTrack track)
    {
        // 标题+歌手同且时长差<2s（PLAN：库内重复提示）
        var title = track.Name?.Trim() ?? string.Empty;
        if (title.Length == 0) return false;
        var artist = track.Artists.FirstOrDefault()?.Trim() ?? string.Empty;

        return _library.Tracks.Any(t =>
            string.Equals(t.DisplayTitle?.Trim(), title, StringComparison.OrdinalIgnoreCase)
            && (artist.Length == 0 || string.Equals(t.DisplayArtist?.Trim(), artist, StringComparison.OrdinalIgnoreCase))
            && (track.DurationMs <= 0 || Math.Abs(t.Duration.TotalMilliseconds - track.DurationMs) < 2000));
    }

    private string BuildFileName(OnlineTrack track, string sourceKey, int preferredBr) =>
        $"{DownloadTemplater.SanitizeComponent(track.Name)}.bin";

    private void Fail(DownloadItem item, string error)
    {
        item.Status = DownloadStatus.Failed;
        item.Error = error;
        Raise(item);
        AddHistory(item);
        Log.Warning("下载失败：{Name}（{Error}）", item.Track.Name, error);
    }

    private void Raise(DownloadItem item) => ItemChanged?.Invoke(item);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* 忽略清理失败 */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts?.Cancel();
        lock (_gate)
        {
            _queue.Clear();
            _current = null;
            _cts = null;
        }
        _http.Dispose();
    }
}
