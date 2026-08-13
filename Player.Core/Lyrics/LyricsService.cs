using Player.Core.Library;
using Player.Core.Online;
using Serilog;

namespace Player.Core.Lyrics;

/// <summary>歌词来源（P3.1-③ 起为四级优先级：.lrc > 内嵌标签 > 缓存 > API）。</summary>
public enum LyricSource
{
    /// <summary>没找到歌词。</summary>
    None,

    /// <summary>同目录同名 .lrc 文件。</summary>
    LocalFile,

    /// <summary>文件内嵌标签歌词（USLT / LYRICS 等，P3.1-③ 新需求）。</summary>
    Embedded,

    /// <summary>本地缓存（lyrics_cache 表）。</summary>
    Cache,

    /// <summary>ChKSz API 刚拉的。</summary>
    Online
}

/// <summary>
/// 用户手动指定的歌词来源偏好（右键菜单「歌词来源」，按曲目持久化）。
/// 偏好来源找不到内容时自动回退到默认链的其余部分，不会让歌词变空。
/// </summary>
public enum LyricPreference
{
    /// <summary>默认链：.lrc &gt; 内嵌 &gt; 缓存 &gt; API。</summary>
    Auto,

    /// <summary>优先同目录 .lrc（跳过内嵌标签）。</summary>
    LrcFile,

    /// <summary>优先内嵌标签（.lrc 次之，仍跳过缓存/API 前的顺序调整）。</summary>
    Embedded,

    /// <summary>只用网易云（跳过 .lrc 与内嵌）。</summary>
    Online
}

/// <summary>一次歌词加载的结果。UI 直接拿来显示。</summary>
public sealed class LyricsLoadResult
{
    public static readonly LyricsLoadResult Empty = new();

    public LyricDocument Document { get; init; } = LyricDocument.Empty;

    public LyricSource Source { get; init; } = LyricSource.None;

    /// <summary>歌词页要展示的来源描述（"本地 .lrc" / "缓存" / "在线"）。</summary>
    public string SourceText => Source switch
    {
        LyricSource.LocalFile => "本地 .lrc",
        LyricSource.Embedded => "内嵌标签",
        LyricSource.Cache => "缓存",
        LyricSource.Online => "在线",
        _ => string.Empty
    };

    /// <summary>整体生效偏移 = 歌词 [offset:] 标签 + 用户手动微调。正数表示歌词提前。</summary>
    public TimeSpan EffectiveOffset { get; init; }

    public bool IsEmpty => Document.IsEmpty;
}

/// <summary>
/// 歌词服务（PLAN 第 7.2 节 + P3.1-③）：
///
/// 优先级：同目录同名 .lrc 文件 ＞ 文件内嵌标签歌词（USLT/LYRICS，有内嵌不碰 API）＞
/// 本地缓存 ＞ ChKSz API（取歌词前先按 标题+歌手+时长匹配网易云 ID，结果持久化到
/// tracks.netease_id，支持手动重新匹配）。
///
/// 铁律：
/// ① 任何在线失败都**不影响本地播放**，也不弹窗 —— 歌词页显示"未找到"即可；
/// ② 匹配失败要安静，同一首歌会话内不反复搜索烧额度；
/// ③ 402（额度用尽）后在线能力整体降级一段时间，额度恢复自动解除。
/// </summary>
public sealed class LyricsService : IDisposable
{
    private readonly ChkszClient _client;
    private readonly object _gate = new();

    /// <summary>path（不区分大小写）→ 网易云 ID。启动时从库加载，匹配后即时更新。</summary>
    private readonly Dictionary<string, long> _neteaseIds;

    /// <summary>会话内已经尝试过自动匹配（且失败）的 path。避免每切一次歌就搜一次。</summary>
    private readonly HashSet<string> _matchAttempted = new(StringComparer.OrdinalIgnoreCase);


    private DateTime _quotaExhaustedUntil;
    private int _loadVersion;
    private bool _disposed;

    public LyricsService(ChkszClient client)
    {
        _client = client;
        _neteaseIds = LyricsCacheStore.LoadNeteaseIds();
        _client.Quota.Changed += OnQuotaChanged;
    }

    /// <summary>在线能力当前是否可用（有 Key 且没撞上额度用尽）。</summary>
    public bool IsOnlineAvailable =>
        ChkszClient.HasApiKey && DateTime.UtcNow >= _quotaExhaustedUntil;

    /// <summary>path → 网易云 ID；没匹配过返回 null。</summary>
    public long? GetNeteaseId(string path)
    {
        lock (_gate)
        {
            return _neteaseIds.TryGetValue(path, out var id) ? id : null;
        }
    }

    // ================= 用户来源偏好（右键菜单「歌词来源」，按曲目持久化） =================

    /// <summary>
    /// 读取来源偏好；未设置过返回 <see cref="LyricPreference.Online"/>（用户实测结论：
    /// 网易云歌词排版统一且支持双语/罗马音，默认走网易云；想要本地来源在右键菜单里改）。
    /// </summary>
    public LyricPreference GetPreference(string path)
    {
        var raw = LyricsCacheStore.GetLyricPreference(path);
        return Enum.TryParse<LyricPreference>(raw, out var preference) ? preference : LyricPreference.Online;
    }

    /// <summary>设置来源偏好并持久化（按曲目）。</summary>
    public void SetPreference(string path, LyricPreference preference)
        => LyricsCacheStore.SaveLyricPreference(path, preference.ToString());

    // ================= 加载歌词 =================

    /// <summary>
    /// 按三级优先级取一首歌的歌词。可并发调用（快速切歌），内部串行化 +
    /// 版本号：只有"最新一次调用"的结果会被返回，旧请求的结果直接丢弃。
    /// </summary>
    public async Task<LyricsLoadResult> LoadForTrackAsync(TrackRecord track, CancellationToken cancellationToken = default)
    {
        if (track is null || track.Path.Length == 0) return LyricsLoadResult.Empty;

        var version = Interlocked.Increment(ref _loadVersion);
        var result = await LoadCoreAsync(track, useCache: true, cancellationToken).ConfigureAwait(false);

        // 中间被更新的请求覆盖了就丢弃（竞态：快速切歌时旧歌的结果不能覆盖新歌）
        if (version != Volatile.Read(ref _loadVersion)) return LyricsLoadResult.Empty;
        return result;
    }

    /// <summary>手动"重新获取"：跳过缓存，直接走 API（本地 .lrc 仍优先）。</summary>
    public Task<LyricsLoadResult> RefreshFromOnlineAsync(TrackRecord track, CancellationToken cancellationToken = default)
    {
        if (track is null || track.Path.Length == 0) return Task.FromResult(LyricsLoadResult.Empty);

        var version = Interlocked.Increment(ref _loadVersion);
        return LoadCoreAsync(track, useCache: false, cancellationToken).ContinueWith(t =>
        {
            var result = t.IsCompletedSuccessfully ? t.Result : LyricsLoadResult.Empty;
            return version == Volatile.Read(ref _loadVersion) ? result : LyricsLoadResult.Empty;
        }, cancellationToken);
    }

    private async Task<LyricsLoadResult> LoadCoreAsync(
        TrackRecord track, bool useCache, CancellationToken cancellationToken)
    {
        // 用户手动来源偏好（右键菜单「歌词来源」，按曲目持久化）；
        // 偏好来源没有内容时回退到默认链的其余部分，绝不让歌词变空。
        var preference = GetPreference(track.Path);

        // Embedded 偏好：内嵌优先，.lrc 次之
        if (preference == LyricPreference.Embedded)
        {
            var embedded = await ReadEmbeddedAsync(track).ConfigureAwait(false);
            if (embedded is not null) return embedded;

            var lrc = await TryReadLrcAsync(track, cancellationToken).ConfigureAwait(false);
            if (lrc is not null) return lrc;

            return await LoadOnlineAsync(track, useCache, cancellationToken).ConfigureAwait(false);
        }

        // 其余偏好（Auto / LrcFile）：.lrc 在默认链最前；Online 完全跳过本地来源
        if (preference != LyricPreference.Online)
        {
            var lrc = await TryReadLrcAsync(track, cancellationToken).ConfigureAwait(false);
            if (lrc is not null) return lrc;

            // LrcFile 偏好：只想要 .lrc，跳过内嵌标签直接去在线
            if (preference == LyricPreference.LrcFile)
                return await LoadOnlineAsync(track, useCache, cancellationToken).ConfigureAwait(false);

            var embedded = await ReadEmbeddedAsync(track).ConfigureAwait(false);
            if (embedded is not null) return embedded;
        }

        // ---- 网易云（匹配 ID → 缓存 → API） ----
        return await LoadOnlineAsync(track, useCache, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>同目录同名 .lrc。文件损坏时返回 null（降级，不抛给播放链路）。</summary>
    private async Task<LyricsLoadResult?> TryReadLrcAsync(TrackRecord track, CancellationToken cancellationToken)
    {
        var lrcPath = Path.ChangeExtension(track.Path, ".lrc");
        if (!File.Exists(lrcPath)) return null;

        try
        {
            var content = await File.ReadAllTextAsync(lrcPath, cancellationToken).ConfigureAwait(false);
            return BuildResult(LrcParser.Parse(content), LyricSource.LocalFile, track.Path);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "读取本地 .lrc 失败：{Path}", lrcPath);
            return null;
        }
    }

    /// <summary>内嵌标签歌词（USLT/LYRICS 等）。没有则返回 null。</summary>
    private async Task<LyricsLoadResult?> ReadEmbeddedAsync(TrackRecord track)
    {
        var embedded = await Task.Run(() => TagReader.ReadLyrics(track.Path)).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(embedded)) return null;

        return BuildResult(LrcParser.Parse(embedded), LyricSource.Embedded, track.Path);
    }

    private async Task<LyricsLoadResult> LoadOnlineAsync(
        TrackRecord track, bool useCache, CancellationToken cancellationToken)
    {
        var neteaseId = GetNeteaseId(track.Path);

        // 没有 ID：尝试自动匹配（一次/会话）
        if (neteaseId is null)
        {
            lock (_gate)
            {
                if (_matchAttempted.Contains(track.Path)) return LyricsLoadResult.Empty;
                _matchAttempted.Add(track.Path);
            }

            if (!IsOnlineAvailable) return LyricsLoadResult.Empty;

            var matched = await TryAutoMatchAsync(track, cancellationToken).ConfigureAwait(false);
            if (matched is null) return LyricsLoadResult.Empty;

            neteaseId = matched;
        }

        // 本地缓存
        var cacheKey = "163:" + neteaseId;
        if (useCache)
        {
            var cached = LyricsCacheStore.GetCached(cacheKey);
            if (cached is not null && !string.IsNullOrWhiteSpace(cached.Lrc))
            {
                var document = MergeLyrics(cached);
                return BuildResult(document, LyricSource.Cache, track.Path);
            }
        }

        // API
        if (!IsOnlineAvailable) return LyricsLoadResult.Empty;

        var lyricResult = await _client.GetLyricAsync(neteaseId.Value, cancellationToken).ConfigureAwait(false);
        if (!lyricResult.Success)
        {
            if (lyricResult.QuotaExhausted) MarkQuotaExhausted();
            return LyricsLoadResult.Empty;
        }

        var fresh = new CachedLyric
        {
            Lrc = lyricResult.Data?.Lrc ?? string.Empty,
            TranslatedLrc = lyricResult.Data?.TranslatedLrc ?? string.Empty,
            RomajiLrc = lyricResult.Data?.RomajiLrc ?? string.Empty
        };

        if (!string.IsNullOrWhiteSpace(fresh.Lrc))
        {
            LyricsCacheStore.SaveCached(cacheKey, fresh);
            return BuildResult(MergeLyrics(fresh), LyricSource.Online, track.Path);
        }

        return LyricsLoadResult.Empty;
    }

    /// <summary>歌词 + 翻译 + 罗马音并轨，应用手动偏移。</summary>
    private static LyricDocument MergeLyrics(CachedLyric cached)
    {
        var original = LrcParser.Parse(cached.Lrc);
        var translation = LrcParser.Parse(cached.TranslatedLrc);
        var romaji = LrcParser.Parse(cached.RomajiLrc);
        return LrcParser.Merge(original, translation, romaji);
    }

    private LyricsLoadResult BuildResult(LyricDocument document, LyricSource source, string path)
    {
        var manualOffset = LyricsCacheStore.GetManualOffset(path) ?? TimeSpan.Zero;
        return new LyricsLoadResult
        {
            Document = document,
            Source = source,
            EffectiveOffset = document.TagOffset + manualOffset
        };
    }

    // ================= 自动匹配（标题 + 歌手 + 时长差 < 3s） =================

    private async Task<long?> TryAutoMatchAsync(TrackRecord track, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(track.Title)) return null;

        var keyword = track.Title;
        if (!string.IsNullOrWhiteSpace(track.Artist)) keyword += " " + track.Artist;

        var result = await _client.SearchAsync(keyword, limit: 30, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            if (result.QuotaExhausted) MarkQuotaExhausted();
            return null;
        }

        var best = LyricMatcher.PickBest(result.Data!, track.Title, track.Artist, track.DurationMs);
        if (best is null)
        {
            Log.Information("歌词匹配未命中：{Title} - {Artist}", track.Title, track.Artist);
            return null;
        }

        Log.Information("歌词匹配：{Title} → 网易云 {Id}（{Name}）",
            track.Title, best.Id, best.Name);
        SaveMatch(track.Path, best.Id);
        return best.Id;
    }

    private void SaveMatch(string path, long neteaseId)
    {
        lock (_gate)
        {
            _neteaseIds[path] = neteaseId;
        }

        LyricsCacheStore.SaveNeteaseId(path, neteaseId);
    }

    // ================= 手动重新匹配 =================

    /// <summary>按 标题+歌手 搜索，返回按相似度排序的候选（供"重新匹配"对话框选择）。</summary>
    public async Task<IReadOnlyList<SearchSong>> FindCandidatesAsync(
        TrackRecord track, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(track.Title)) return Array.Empty<SearchSong>();

        var keyword = track.Title;
        if (!string.IsNullOrWhiteSpace(track.Artist)) keyword += " " + track.Artist;

        var result = await _client.SearchAsync(keyword, limit: 30, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (!result.Success)
        {
            if (result.QuotaExhausted) MarkQuotaExhausted();
            return Array.Empty<SearchSong>();
        }

        return LyricMatcher.RankCandidates(result.Data!, track.Title, track.Artist, track.DurationMs);
    }

    /// <summary>用户从候选里选了一个：持久化 ID 并立即拉歌词（跳过缓存）。</summary>
    public async Task<LyricsLoadResult> ApplyMatchAsync(
        TrackRecord track, long neteaseId, CancellationToken cancellationToken = default)
    {
        if (track is null || neteaseId <= 0) return LyricsLoadResult.Empty;

        SaveMatch(track.Path, neteaseId);
        lock (_gate)
        {
            _matchAttempted.Remove(track.Path);
        }

        return await RefreshFromOnlineAsync(track, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>清除已匹配的 ID（"重新匹配"里选"取消匹配"时用）。</summary>
    public void ClearMatch(string path)
    {
        lock (_gate)
        {
            _neteaseIds.Remove(path);
            _matchAttempted.Remove(path);
        }

        LyricsCacheStore.ClearNeteaseId(path);
    }

    // ================= 手动偏移 =================

    public TimeSpan? GetManualOffset(string path) => LyricsCacheStore.GetManualOffset(path);

    public void SetManualOffset(string path, TimeSpan offset)
    {
        LyricsCacheStore.SaveManualOffset(path, offset);
    }

    // ================= 内部 =================

    private void OnQuotaChanged(object? sender, EventArgs e)
    {
        // 额度恢复（比如第二天重置、手动兑换 LDC）后解除降级
        if (_client.Quota.FreeRemaining is > 0)
            _quotaExhaustedUntil = default;
    }

    private void MarkQuotaExhausted()
    {
        _quotaExhaustedUntil = DateTime.UtcNow.AddMinutes(10);
        Log.Information("额度用尽，在线歌词功能暂停 10 分钟");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client.Quota.Changed -= OnQuotaChanged;
    }
}