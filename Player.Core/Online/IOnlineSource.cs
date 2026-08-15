using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Player.Core.Online;

/// <summary>统一在线曲目（跨源：GD Studio / ChKSz-网易云）。DurationMs=0 表示未知。</summary>
public sealed record OnlineTrack(
    string Id,
    string Name,
    IReadOnlyList<string> Artists,
    string Album,
    string PicId,
    string LyricId,
    string Source,
    long DurationMs = 0)
{
    public string ArtistLine => Artists.Count == 0 ? string.Empty : string.Join(" / ", Artists);

    public string ArtistAlbumLine => string.IsNullOrWhiteSpace(Album)
        ? ArtistLine
        : $"{ArtistLine}（{Album}）";
}

/// <summary>统一取流结果。ActualBr &lt;= 0 且 Url 为空 = 取流失败（NotFound）。</summary>
public sealed record OnlineStream(string Url, int ActualBr, long SizeBytes);

/// <summary>统一歌词结果（LRC 原文 + 可选翻译）。</summary>
public sealed record OnlineLyric(string? Lrc, string? Translation);

/// <summary>统一在线结果包装：成功带数据；失败带可读原因；NotFound = 无资源（搜不到/取流失败）。</summary>
public sealed class OnlineResult<T>
{
    public bool Success { get; }

    public T? Data { get; }

    public string Error { get; }

    /// <summary>无资源（搜不到 / 该源无此音质 / 灰色歌曲）。UI 提示"没找到"而不是报错。</summary>
    public bool NotFound { get; }

    private OnlineResult(bool success, T? data, string error, bool notFound)
    {
        Success = success;
        Data = data;
        Error = error;
        NotFound = notFound;
    }

    public static OnlineResult<T> Ok(T data) => new(true, data, string.Empty, false);

    public static OnlineResult<T> Fail(string error, bool notFound = false) => new(false, default, error, notFound);
}

/// <summary>
/// 在线源统一抽象（P4 修订 v2）：搜索 / 专辑拉取 / 取流 / 歌词 / 封面。
/// 实现约定：失败一律返回 OnlineResult 而不是抛异常；IsAvailable 供下拉灰显。
/// </summary>
public interface IOnlineSource : IDisposable
{
    /// <summary>稳定键（配置/日志用）。</summary>
    string Key { get; }

    /// <summary>下拉展示名。</summary>
    string DisplayName { get; }

    /// <summary>是否零 Key 零额度（GD=true；网易云=false，需 Key 且消耗额度）。</summary>
    bool IsFree { get; }

    /// <summary>可用性（探测结果，不可用灰显）。</summary>
    bool IsAvailable { get; }

    /// <summary>按关键词搜索（翻页 page 从 1 开始）。</summary>
    Task<OnlineResult<IReadOnlyList<OnlineTrack>>> SearchAsync(string keyword, int limit, int page, CancellationToken ct);

    /// <summary>按专辑名拉整张专辑曲目（[source]_album）；不支持的源返回 Fail。</summary>
    Task<OnlineResult<IReadOnlyList<OnlineTrack>>> SearchAlbumAsync(string keyword, int limit, int page, CancellationToken ct);

    /// <summary>取流（preferredBr 越大越好，服务端可能降级；结果标注实际码率）。直链有时效不缓存。</summary>
    Task<OnlineResult<OnlineStream>> GetStreamAsync(OnlineTrack track, int preferredBr, CancellationToken ct);

    /// <summary>取歌词（LRC 原文 + 可选翻译）。</summary>
    Task<OnlineResult<OnlineLyric>> GetLyricAsync(OnlineTrack track, CancellationToken ct);

    /// <summary>取封面 URL（size 为偏好尺寸；有些源直接返回直链）。</summary>
    Task<OnlineResult<string>> GetPicUrlAsync(OnlineTrack track, int size, CancellationToken ct);

    /// <summary>异步探测可用性（失败置 IsAvailable=false）。</summary>
    Task ProbeAsync(CancellationToken ct);
}
