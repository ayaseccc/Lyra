using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Player.Core.Infra;
using Serilog;

namespace Player.Core.Online;

/// <summary>一次调用的结果。失败一律带可读原因，调用方直接拿去显示。</summary>
public sealed class ChkszResult<T>
{
    public bool Success { get; private init; }

    public T? Data { get; private init; }

    public string Error { get; private init; } = string.Empty;

    public int StatusCode { get; private init; }

    /// <summary>402：额度用尽。在线功能应整体降级，不要继续请求。</summary>
    public bool QuotaExhausted { get; private init; }

    /// <summary>401：Key 无效或没填。</summary>
    public bool AuthFailed { get; private init; }

    /// <summary>404：资源不存在（灰色歌曲、该音质无资源）。</summary>
    public bool NotFound { get; private init; }

    public static ChkszResult<T> Ok(T data) => new() { Success = true, Data = data, StatusCode = 200 };

    public static ChkszResult<T> Fail(string error, int status = 0, bool quota = false, bool auth = false, bool notFound = false)
        => new() { Success = false, Error = error, StatusCode = status, QuotaExhausted = quota, AuthFailed = auth, NotFound = notFound };
}

/// <summary>
/// ChKSz API 客户端（PLAN 第 6 节）。四个端点、全局令牌桶、额度头解析、错误映射、URL 脱敏。
///
/// 三条铁律：
/// ① apikey **只**从 data/config.json 读，永不写进日志、异常消息、样本；
/// ② 任何在线失败都不能影响本地播放，调用方拿到的永远是 <see cref="ChkszResult{T}"/> 而不是异常；
/// ③ 额度数字以响应头为准，不写死。
/// </summary>
public sealed partial class ChkszClient : IDisposable
{
    /// <summary>官方默认地址；设置页可改（空/非法回落默认）。</summary>
    private const string DefaultBaseAddress = "https://api.chksz.com";

    private readonly HttpClient _http;
    private readonly TokenBucket _bucket = new(18, TimeSpan.FromMinutes(1));
    private bool _disposed;

    public ChkszClient(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(20);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Player/1.0");
    }

    public QuotaTracker Quota { get; } = new();

    public TokenBucket Bucket => _bucket;

    /// <summary>API 列表里有没有带 Key 的条目。没有时在线功能整体降级，不发任何请求。</summary>
    public static bool HasApiKey => ConfigService.ChkszEndpoint() is not null;

    // ================= 四个端点 =================

    /// <summary>搜索歌名/歌手/专辑。</summary>
    public Task<ChkszResult<SearchResult>> SearchAsync(
        string keyword, int limit = 30, int offset = 0, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(keyword))
            return Task.FromResult(ChkszResult<SearchResult>.Fail("搜索关键词是空的"));

        var query = $"keyword={Uri.EscapeDataString(keyword)}&limit={limit}&offset={offset}";
        return SendAsync<SearchResult>("/api/163_search", query, cancellationToken);
    }

    /// <summary>解析播放/下载直链。直链有时效，**不要缓存**。</summary>
    public Task<ChkszResult<SongUrlResult>> GetSongUrlAsync(
        long songId, string level = "jymaster", CancellationToken cancellationToken = default)
    {
        var query = $"id={songId}&level={Uri.EscapeDataString(level)}&type=json";
        return SendAsync<SongUrlResult>("/api/163_music", query, cancellationToken);
    }

    /// <summary>取歌词（原文 / 翻译 / 罗马音，后两者常为空）。</summary>
    public Task<ChkszResult<LyricResult>> GetLyricAsync(long songId, CancellationToken cancellationToken = default)
        => SendAsync<LyricResult>("/api/163_lyric", $"id={songId}", cancellationToken);

    /// <summary>取歌单详情。大歌单慢，单独给长超时（PLAN 第 6 节）。</summary>
    public Task<ChkszResult<PlaylistResult>> GetPlaylistAsync(long playlistId, CancellationToken cancellationToken = default)
        => SendAsync<PlaylistResult>("/api/163_playlist", $"id={playlistId}", cancellationToken,
            timeout: TimeSpan.FromSeconds(60));

    // ================= 发送与错误映射 =================

    private async Task<ChkszResult<T>> SendAsync<T>(
        string path, string query, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        if (_disposed) return ChkszResult<T>.Fail("客户端已释放");

        var endpoint = ConfigService.ChkszEndpoint();
        if (endpoint is null)
            return ChkszResult<T>.Fail("还没有填 API Key，请到设置页的在线功能里填写", auth: true);

        var baseUrl = endpoint.Url;
        if (!OnlineUrl.IsHttp(baseUrl)) baseUrl = DefaultBaseAddress;
        var url = $"{baseUrl.Trim().TrimEnd('/')}{path}?apikey={Uri.EscapeDataString(endpoint.Key)}&{query}";
        var safeUrl = Redact(url);

        try
        {
            // 所有调用都要排队过令牌桶
            await _bucket.WaitAsync(cancellationToken).ConfigureAwait(false);

            var result = await SendOnceAsync<T>(url, safeUrl, timeout, cancellationToken).ConfigureAwait(false);

            // 429：按 Retry-After 等一次，最多重试一次（PLAN 第 6 节）
            if (result.StatusCode == 429 && result.RetryAfter is { } wait)
            {
                var delay = wait > TimeSpan.FromSeconds(60) ? TimeSpan.FromSeconds(60) : wait;
                Log.Warning("触发限流，{Delay} 后重试一次：{Url}", delay, safeUrl);

                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                await _bucket.WaitAsync(cancellationToken).ConfigureAwait(false);

                result = await SendOnceAsync<T>(url, safeUrl, timeout, cancellationToken).ConfigureAwait(false);
            }

            return result.Result;
        }
        catch (OperationCanceledException)
        {
            return ChkszResult<T>.Fail("请求已取消");
        }
        catch (Exception ex)
        {
            // 连异常消息都要脱敏：HttpRequestException 有时会带上请求 URL
            Log.Error(ex, "请求失败：{Url}", safeUrl);
            return ChkszResult<T>.Fail("网络请求失败：" + Redact(ex.Message));
        }
    }

    private async Task<(ChkszResult<T> Result, int StatusCode, TimeSpan? RetryAfter)> SendOnceAsync<T>(
        string url, string safeUrl, TimeSpan? timeout, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var timeoutCts = new CancellationTokenSource(timeout ?? _http.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, linked.Token)
            .ConfigureAwait(false);

        Quota.Update(name => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null);

        var status = (int)response.StatusCode;
        var body = await response.Content.ReadAsStringAsync(linked.Token).ConfigureAwait(false);

        Log.Debug("{Status} {Url}（{Bytes} 字节，剩余额度 {Quota}）",
            status, safeUrl, body.Length, Quota.FreeRemaining);

        TimeSpan? retryAfter = null;
        if (status == 429)
        {
            retryAfter = response.Headers.RetryAfter?.Delta
                         ?? (response.Headers.RetryAfter?.Date is { } date
                             ? date - DateTimeOffset.Now
                             : TimeSpan.FromSeconds(5));

            if (retryAfter < TimeSpan.Zero) retryAfter = TimeSpan.FromSeconds(1);
        }

        string? msg = null;
        try
        {
            var envelope = JsonSerializer.Deserialize<ChkszEnvelope<T>>(body);
            msg = envelope?.Msg;

            // 判断成功以 HTTP 状态 + msg 为准，不能只看有没有 data
            if (response.IsSuccessStatusCode && envelope is not null && envelope.Data is not null)
                return (ChkszResult<T>.Ok(envelope.Data), status, retryAfter);
        }
        catch (JsonException ex)
        {
            Log.Warning("响应不是预期的 JSON：{Url}，{Message}", safeUrl, ex.Message);

            if (response.IsSuccessStatusCode)
                return (ChkszResult<T>.Fail("服务端返回了无法解析的内容"), status, retryAfter);
        }

        return (MapError<T>(status, msg), status, retryAfter);
    }

    /// <summary>把 HTTP 状态映射成用户能看懂的话（PLAN 第 6 节的错误处理表）。纯函数，可离线断言。</summary>
    public static ChkszResult<T> MapError<T>(int status, string? msg)
    {
        var detail = string.IsNullOrWhiteSpace(msg) ? string.Empty : $"（{msg.Trim()}）";

        return status switch
        {
            400 => ChkszResult<T>.Fail("请求参数有误" + detail, status),
            401 => ChkszResult<T>.Fail("API Key 无效，请到设置页检查" + detail, status, auth: true),
            402 => ChkszResult<T>.Fail("今日额度已用尽，等次日重置或去后台兑换 LDC" + detail, status, quota: true),
            403 => ChkszResult<T>.Fail("请求被拒绝" + detail, status),
            404 => ChkszResult<T>.Fail("没有找到对应资源，可能是灰色歌曲或该音质无资源" + detail, status, notFound: true),
            429 => ChkszResult<T>.Fail("请求过于频繁，请稍后再试" + detail, status),
            503 => ChkszResult<T>.Fail("服务端暂时不可用，请稍后再试" + detail, status),
            _ => ChkszResult<T>.Fail($"请求失败（HTTP {status}）{detail}", status)
        };
    }

    /// <summary>日志/异常里出现的 URL 一律脱敏 apikey。纯函数，可离线断言。</summary>
    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return ApiKeyPattern().Replace(text, "apikey=***");
    }

    [GeneratedRegex(@"apikey=[^&\s""']+", RegexOptions.IgnoreCase)]
    private static partial Regex ApiKeyPattern();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }
}
