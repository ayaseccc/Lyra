using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Player.Core.Infra;
using Serilog;

namespace Player.Core.Online;

/// <summary>
/// GD Studio 源（P4 修订 v2 默认源）：零 Key 零额度；搜索/专辑/取流/歌词/封面。
/// 打样结论（docs/api-samples/gd/README.md）：子源状态会变（netease 搜索空、kuwo 取流空、
/// joox 全链路可用），且错误是 200 + JSON 形态（detail / 空数组 / br=-1），不是 HTTP 错误码。
/// 策略：保守 10 次/分令牌桶；网络失败指数退避重试（1s/2s/4s，最多 3 次）；
/// 逐子源可用性探测，搜索自动落回可用子源；取流失败（br=-1）报 NotFound。
/// </summary>
public sealed class GdSource : IOnlineSource
{
    /// <summary>官方默认地址；设置页可改（空/非法回落默认）。</summary>
    private const string DefaultBaseAddress = "https://music-api.gdstudio.xyz/api.php";

    /// <summary>子源探测顺序（2026-08-15 打样：netease 搜索空、kuwo/joox 可用）。</summary>
    private static readonly string[] SubSources = { "netease", "kuwo", "joox", "bilibili" };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _http;
    private readonly TokenBucket _bucket = new(10, TimeSpan.FromMinutes(1));

    /// <summary>歌词专用桶（审查：P4-6 歌词链与搜索共享 10/min 会互相拖累，独立配额）。</summary>
    private readonly TokenBucket _lyricBucket = new(10, TimeSpan.FromMinutes(1));

    /// <summary>子源可用性：并发读写（探测线程 + 搜索线程），用 ConcurrentDictionary（审查修复）。</summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> _subSourceOk =
        new(SubSources.ToDictionary(s => s, _ => true));
    private bool _disposed;

    public GdSource(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(20);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Player/1.0");
    }

    public string Key => "gd";

    public string DisplayName => "GD Studio";

    public bool IsFree => true;

    public bool IsAvailable { get; private set; } = true;

    /// <summary>源内部的可达子源（取流用条目自带 source，不在这选）。</summary>
    public IReadOnlyList<string> AvailableSubSources =>
        _subSourceOk.Where(kv => kv.Value).Select(kv => kv.Key).ToList();

    // ================= IOnlineSource =================

    public Task<OnlineResult<IReadOnlyList<OnlineTrack>>> SearchAsync(
        string keyword, int limit, int page, CancellationToken ct)
        => SearchCoreAsync(keyword, limit, page, ct, albumMode: false);

    public Task<OnlineResult<IReadOnlyList<OnlineTrack>>> SearchAlbumAsync(
        string keyword, int limit, int page, CancellationToken ct)
        => SearchCoreAsync(keyword, limit, page, ct, albumMode: true);

    private async Task<OnlineResult<IReadOnlyList<OnlineTrack>>> SearchCoreAsync(
        string keyword, int limit, int page, CancellationToken ct, bool albumMode)
    {
        if (string.IsNullOrWhiteSpace(keyword))
            return OnlineResult<IReadOnlyList<OnlineTrack>>.Fail("搜索关键词是空的");

        // 逐子源探测：第一个返回非空列表的子源作为结果；空结果/不支持标记该子源不可用
        OnlineResult<IReadOnlyList<OnlineTrack>>? lastFailure = null;
        var anyReached = false;   // 是否有子源正常返回（网络通、只是没结果）

        foreach (var source in SubSources)
        {
            if (!_subSourceOk.TryGetValue(source, out var ok) || !ok) continue;

            var result = await SearchSubSourceAsync(source, keyword, limit, page, ct, albumMode).ConfigureAwait(false);
            if (!result.Success)
            {
                lastFailure ??= result;
                Mark(source, available: false, $"搜索失败：{result.Error}");
                continue;
            }

            anyReached = true;
            if (result.Data is { Count: > 0 })
            {
                Mark(source, available: true, null);
                return result;
            }

            // 空数组：该子源当前搜不到（可能临时不可用，不立即拉黑，只记录）
            Log.Debug("GD 子源 {Source} 搜索为空", source);
        }

        // 全部子源没搜到：若全部是网络/接口失败 → 报失败原因（断网降级）；有源可达则报"没找到"
        return anyReached
            ? OnlineResult<IReadOnlyList<OnlineTrack>>.Ok(Array.Empty<OnlineTrack>())
            : (lastFailure ?? OnlineResult<IReadOnlyList<OnlineTrack>>.Fail("所有音源都不可用"));
    }

    public async Task<OnlineResult<OnlineStream>> GetStreamAsync(
        OnlineTrack track, int preferredBr, CancellationToken ct)
    {
        // ① 同源音质降级链：999 → 740 → 320 → 128，返回实际值（PLAN：拿不到逐级降并标注）
        OnlineResult<OnlineStream>? lastNotFound = null;
        foreach (var br in QualityChain(preferredBr))
        {
            var result = await GetStreamFromSourceAsync(track, br, ct).ConfigureAwait(false);
            if (result.Success) return result;
            if (!result.NotFound) return result;   // 网络/解析错误直接返回，不降级
            lastNotFound = result;
        }

        // ② 跨子源自动重试：该子源取流不可用（如 kuwo 恒空）时，重搜同名曲目找可播子源
        var fallback = await TryOtherSubSourcesAsync(track, preferredBr, ct).ConfigureAwait(false);
        if (fallback.Success) return fallback;
        if (fallback.NotFound) return lastNotFound ?? fallback;

        return fallback;
    }

    /// <summary>音质降级链（请求 br → 依次尝试的档位）。</summary>
    private static int[] QualityChain(int preferredBr) => preferredBr switch
    {
        >= 999 => new[] { 999, 740, 320, 128 },
        >= 740 => new[] { 740, 320, 128 },
        >= 320 => new[] { 320, 128 },
        _ => new[] { 128 }
    };

    private async Task<OnlineResult<OnlineStream>> GetStreamFromSourceAsync(
        OnlineTrack track, int br, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(track.Id) || string.IsNullOrWhiteSpace(track.Source))
            return OnlineResult<OnlineStream>.Fail("曲目信息不完整", notFound: true);

        var query = $"types=url&source={Uri.EscapeDataString(track.Source)}&id={Uri.EscapeDataString(track.Id)}&br={br}";
        var result = await GetJsonAsync<GdModels.Url>(query, ct).ConfigureAwait(false);
        if (!result.Success)
            return OnlineResult<OnlineStream>.Fail(result.Error);

        var url = result.Data!;
        if (string.IsNullOrWhiteSpace(url.Address) || url.Br <= 0)
        {
            Log.Debug("GD 取流失败（{Source}/{Id}，请求 br={Br}）", track.Source, track.Id, br);
            return OnlineResult<OnlineStream>.Fail("该源没有此音质的资源", notFound: true);
        }

        return OnlineResult<OnlineStream>.Ok(new OnlineStream(url.Address, url.Br, url.Size));
    }

    /// <summary>按子源逐个重搜同名曲目（跳过原不可用子源），找到能取流的候选（最多试 3 个子源）。</summary>
    private async Task<OnlineResult<OnlineStream>> TryOtherSubSourcesAsync(
        OnlineTrack track, int preferredBr, CancellationToken ct)
    {
        var tried = 0;
        foreach (var source in SubSources)
        {
            if (string.Equals(source, track.Source, StringComparison.OrdinalIgnoreCase)) continue;
            if (!_subSourceOk.TryGetValue(source, out var ok) || !ok) continue;

            var candidates = await SearchSubSourceAsync(source, track.Name, limit: 10, page: 1, ct, albumMode: false)
                .ConfigureAwait(false);
            if (!candidates.Success || candidates.Data is not { Count: > 0 }) continue;

            foreach (var cand in candidates.Data)
            {
                foreach (var br in QualityChain(preferredBr))
                {
                    var result = await GetStreamFromSourceAsync(cand, br, ct).ConfigureAwait(false);
                    if (result.Success)
                    {
                        Log.Information("GD 跨子源取流成功：{Source}/{Id} → {NewSource}/{NewId}（br={Br}）",
                            track.Source, track.Id, cand.Source, cand.Id, result.Data!.ActualBr);
                        return result;
                    }
                }
            }

            if (++tried >= 3) break;
        }

        return OnlineResult<OnlineStream>.Fail("各音源都没有可用的资源", notFound: true);
    }

    public async Task<OnlineResult<OnlineLyric>> GetLyricAsync(OnlineTrack track, CancellationToken ct)
    {
        var query = $"types=lyric&source={Uri.EscapeDataString(track.Source)}&id={Uri.EscapeDataString(track.LyricId)}";
        var result = await GetJsonAsync<GdModels.Lyric>(query, ct).ConfigureAwait(false);
        if (!result.Success)
            return OnlineResult<OnlineLyric>.Fail(result.Error);

        var lyric = result.Data!;
        if (string.IsNullOrWhiteSpace(lyric.Text))
            return OnlineResult<OnlineLyric>.Fail("该曲目没有歌词", notFound: true);

        return OnlineResult<OnlineLyric>.Ok(new OnlineLyric(lyric.Text, lyric.Translation));
    }

    /// <summary>P4-6：按网易云曲目 id 直接拉 GD(netease) 歌词——本地歌词链复用已匹配 id，零额度优先于 ChKSz。</summary>
    public async Task<OnlineResult<OnlineLyric>> GetLyricByNeteaseIdAsync(long neteaseId, CancellationToken ct)
    {
        await _lyricBucket.WaitAsync(ct).ConfigureAwait(false);   // 独立桶：不拖累在线搜索
        var query = $"types=lyric&source=netease&id={neteaseId}";
        var result = await GetJsonCoreAsync<GdModels.Lyric>(query, ct).ConfigureAwait(false);
        if (!result.Success)
            return OnlineResult<OnlineLyric>.Fail(result.Error);

        var lyric = result.Data!;
        if (string.IsNullOrWhiteSpace(lyric.Text))
            return OnlineResult<OnlineLyric>.Fail("该曲目没有歌词", notFound: true);

        return OnlineResult<OnlineLyric>.Ok(new OnlineLyric(lyric.Text, lyric.Translation));
    }

    public async Task<OnlineResult<string>> GetPicUrlAsync(OnlineTrack track, int size, CancellationToken ct)
    {
        var query = $"types=pic&source={Uri.EscapeDataString(track.Source)}&id={Uri.EscapeDataString(track.PicId)}&size={(size is 300 or 500 ? size : 300)}";
        var result = await GetJsonAsync<GdModels.Pic>(query, ct).ConfigureAwait(false);
        if (!result.Success)
            return OnlineResult<string>.Fail(result.Error);

        return string.IsNullOrWhiteSpace(result.Data!.Url)
            ? OnlineResult<string>.Fail("没有封面", notFound: true)
            : OnlineResult<string>.Ok(result.Data.Url);
    }

    public async Task ProbeAsync(CancellationToken ct)
    {
        // 打样：search name=test 任一子源非空即视为可用（不拉黑整个源）
        foreach (var source in SubSources)
        {
            var result = await SearchSubSourceAsync(source, "test", 1, 1, ct).ConfigureAwait(false);
            if (result.Success)
            {
                if (result.Data is { Count: > 0 })
                {
                    Mark(source, available: true, null);
                    IsAvailable = true;
                    return;
                }
            }
            else
            {
                Mark(source, available: false, $"探测失败：{result.Error}");
            }
        }

        IsAvailable = false;
    }

    // ================= 内部 =================

    private async Task<OnlineResult<IReadOnlyList<OnlineTrack>>> SearchSubSourceAsync(
        string source, string keyword, int limit, int page, CancellationToken ct, bool albumMode = false)
    {
        var type = albumMode ? "search" : "search";
        var src = albumMode ? source + "_album" : source;
        var query = $"types={type}&source={Uri.EscapeDataString(src)}&name={Uri.EscapeDataString(keyword)}&count={limit}&pages={page}";
        var result = await GetJsonAsync<List<GdModels.Track>>(query, ct).ConfigureAwait(false);
        if (!result.Success)
            return OnlineResult<IReadOnlyList<OnlineTrack>>.Fail(result.Error);

        var list = result.Data ?? new List<GdModels.Track>();
        return OnlineResult<IReadOnlyList<OnlineTrack>>.Ok(list
            .Select(t => new OnlineTrack(
                t.Id, t.Name ?? string.Empty, t.Artist ?? Array.Empty<string>(), t.Album ?? string.Empty,
                t.PicId ?? string.Empty, t.LyricId ?? t.Id, t.Source ?? source))
            .ToList());
    }

    private async Task<OnlineResult<T>> GetJsonAsync<T>(string query, CancellationToken ct)
    {
        await _bucket.WaitAsync(ct).ConfigureAwait(false);
        return await GetJsonCoreAsync<T>(query, ct).ConfigureAwait(false);
    }

    /// <summary>不带令牌桶的请求核心（歌词走独立桶时用）。</summary>
    private async Task<OnlineResult<T>> GetJsonCoreAsync<T>(string query, CancellationToken ct)
    {
        var url = ConfigService.GdEndpointUrl() + "?" + query;
        var safeUrl = "api.php?" + RedactQuery(query);

        // 指数退避：网络层失败重试最多 3 次（1s/2s/4s）；内容层错误（detail/空）不重试
        var delay = TimeSpan.FromSeconds(1);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
                var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                Log.Debug("{Status} {Url}（{Bytes} 字节）", (int)response.StatusCode, safeUrl, body.Length);

                if (!response.IsSuccessStatusCode)
                {
                    if (attempt < 3)
                    {
                        await Task.Delay(delay, ct).ConfigureAwait(false);
                        delay *= 2;
                        continue;
                    }
                    return OnlineResult<T>.Fail($"请求失败（HTTP {(int)response.StatusCode}）");
                }

                // 打样：detail 字段 = 源不支持/参数错误（200 但不是数据）
                var trimmed = body.TrimStart();
                if (trimmed.StartsWith("{", StringComparison.Ordinal)
                    && trimmed.Contains("detail", StringComparison.OrdinalIgnoreCase))
                    return OnlineResult<T>.Fail("该音源暂不支持此操作");

                // 响应是 camelCase，模型是 PascalCase：大小写不敏感匹配（打样模型验证修正）
                var data = JsonSerializer.Deserialize<T>(body, JsonOptions);
                return data is not null
                    ? OnlineResult<T>.Ok(data)
                    : OnlineResult<T>.Fail("响应解析失败");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                return OnlineResult<T>.Fail("请求超时，请稍后重试");
            }
            catch (Exception ex)
            {
                Log.Debug(ex, "GD 请求失败：{Url}", safeUrl);
                if (attempt < 3)
                {
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    delay *= 2;
                    continue;
                }
                return OnlineResult<T>.Fail("网络请求失败：" + ex.Message);
            }
        }
    }

    private static string RedactQuery(string query) =>
        // GD 无 Key；query 里没有敏感字段，原样返回（只防未来加参时误打日志）
        query;

    private void Mark(string source, bool available, string? reason)
    {
        if (available && _subSourceOk.TryGetValue(source, out var old) && old) return;
        _subSourceOk[source] = available;
        if (!available)
            Log.Warning("GD 子源 {Source} 不可用：{Reason}", source, reason ?? "未知");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }
}
