using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Player.Core.Online;

/// <summary>
/// ChKSz 收编为网易云兜底源（P4 修订 v2）：把 ChkszClient 的 163_search/music/lyric
/// 适配到 IOnlineSource。Key / 额度 / 脱敏纪律全部沿用 ChkszClient 不变；
/// 无 Key 时整体不可用（IsAvailable=false，下拉灰显）。
/// </summary>
public sealed class ChkszSource : IOnlineSource
{
    private readonly ChkszClient _client;
    private bool _disposed;

    public ChkszSource(ChkszClient client)
    {
        _client = client;
        IsAvailable = ChkszClient.HasApiKey;
    }

    public string Key => "netease";

    public string DisplayName => "网易云";

    public bool IsFree => false;

    public bool IsAvailable { get; private set; }

    public async Task<OnlineResult<IReadOnlyList<OnlineTrack>>> SearchAsync(
        string keyword, int limit, int page, CancellationToken ct)
    {
        if (!ChkszClient.HasApiKey)
            return OnlineResult<IReadOnlyList<OnlineTrack>>.Fail("未配置 API Key");

        var result = await _client.SearchAsync(keyword, limit: limit, offset: (page - 1) * limit, cancellationToken: ct)
            .ConfigureAwait(false);
        if (!result.Success)
            return OnlineResult<IReadOnlyList<OnlineTrack>>.Fail(result.Error, notFound: result.NotFound);

        return OnlineResult<IReadOnlyList<OnlineTrack>>.Ok(result.Data!.Songs
            .Select(s => new OnlineTrack(
                s.Id.ToString(),
                s.Name,
                s.ArtistList,
                s.Album,
                s.PicUrl ?? string.Empty,
                s.Id.ToString(),
                Key,
                s.Duration))
            .ToList());
    }

    /// <summary>ChKSz 没有 [source]_album 模式，明确不支持（UI 提示）。</summary>
    public Task<OnlineResult<IReadOnlyList<OnlineTrack>>> SearchAlbumAsync(
        string keyword, int limit, int page, CancellationToken ct)
        => Task.FromResult(OnlineResult<IReadOnlyList<OnlineTrack>>.Fail("该音源不支持整张专辑拉取"));

    public async Task<OnlineResult<OnlineStream>> GetStreamAsync(
        OnlineTrack track, int preferredBr, CancellationToken ct)
    {
        if (!long.TryParse(track.Id, out var songId))
            return OnlineResult<OnlineStream>.Fail("曲目 ID 无效");

        var level = preferredBr switch
        {
            >= 999 => "jymaster",
            >= 740 => "lossless",
            >= 320 => "exhigh",
            _ => "standard"
        };

        var result = await _client.GetSongUrlAsync(songId, level, ct).ConfigureAwait(false);
        if (!result.Success)
            return OnlineResult<OnlineStream>.Fail(result.Error, notFound: result.NotFound);

        if (string.IsNullOrWhiteSpace(result.Data?.Url))
            return OnlineResult<OnlineStream>.Fail("该音质没有资源", notFound: true);

        return OnlineResult<OnlineStream>.Ok(new OnlineStream(
            result.Data!.Url, result.Data.Bitrate, result.Data.Size));
    }

    public async Task<OnlineResult<OnlineLyric>> GetLyricAsync(OnlineTrack track, CancellationToken ct)
    {
        if (!long.TryParse(track.LyricId, out var songId))
            return OnlineResult<OnlineLyric>.Fail("曲目 ID 无效");

        var result = await _client.GetLyricAsync(songId, ct).ConfigureAwait(false);
        if (!result.Success)
            return OnlineResult<OnlineLyric>.Fail(result.Error, notFound: result.NotFound);

        var lyric = result.Data!;
        if (string.IsNullOrWhiteSpace(lyric.Lrc))
            return OnlineResult<OnlineLyric>.Fail("该曲目没有歌词", notFound: true);

        return OnlineResult<OnlineLyric>.Ok(new OnlineLyric(lyric.Lrc, lyric.TranslatedLrc));
    }

    public Task<OnlineResult<string>> GetPicUrlAsync(OnlineTrack track, int size, CancellationToken ct)
        // 网易云搜索直接带 picUrl 直链；适配层把 PicId 当直链用（OnlineTrack 映射时填入）
        => Task.FromResult(string.IsNullOrWhiteSpace(track.PicId)
            ? OnlineResult<string>.Fail("没有封面", notFound: true)
            : OnlineResult<string>.Ok(track.PicId));

    public async Task ProbeAsync(CancellationToken ct)
    {
        if (!ChkszClient.HasApiKey)
        {
            IsAvailable = false;
            return;
        }

        var result = await _client.SearchAsync("test", limit: 1, cancellationToken: ct).ConfigureAwait(false);
        IsAvailable = result.Success;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // ChkszClient 由外部统一持有/释放（App.OnExit），这里不 Dispose
    }
}
