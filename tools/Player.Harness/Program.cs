using Player.Core.Audio;
using Player.Core.Infra;
using Player.Core.Library;
using Player.Core.Lyrics;
using Player.Core.Online;

namespace Player.Harness;

/// <summary>
/// 离线自测工具。跑的是 Player.Core 里真正在用的那份逻辑，不是复制品。
///
///   dotnet run --project tools/Player.Harness -- seamless
///       无缝衔接与播放模式的决策逻辑（纯函数，不需要声卡，任何平台都能跑）
///
///   dotnet run --project tools/Player.Harness -- library &lt;曲库目录&gt; [库外文件目录]
///       扫描 / 歌单 / 持久化的端到端验证（要真实音频文件，不需要声卡）
///
///   dotnet run --project tools/Player.Harness -- lyrics
///       P3 歌词与在线层：LRC 解析 / 令牌桶 / 额度头 / 匹配算法 / 缓存存储（纯逻辑 + 临时库，无网络）
///
/// 注意：ASIO / WASAPI 的出声效果没法离线验证，只能在装了声卡驱动的 Windows 上实听，
/// 见 docs/ASIO-验收指引.md。
/// </summary>
public static class Program
{
    private static int _passed;
    private static int _failed;

    public static async Task<int> Main(string[] args)
    {
        var mode = args.Length > 0 ? args[0].ToLowerInvariant() : "seamless";

        switch (mode)
        {
            case "seamless":
                RunSeamlessChecks();
                break;

            case "library":
                if (args.Length < 2)
                {
                    Console.WriteLine("用法：library <曲库目录> [库外文件目录]");
                    return 2;
                }
                await RunLibraryChecksAsync(args[1], args.Length > 2 ? args[2] : null);
                break;

            case "lyrics":
                await RunLyricsChecksAsync();
                break;

            default:
                Console.WriteLine($"未知模式：{mode}（可用：seamless / library / lyrics）");
                return 2;
        }

        Console.WriteLine();
        Console.WriteLine($"===== 通过 {_passed} 项，失败 {_failed} 项 =====");
        return _failed == 0 ? 0 : 1;
    }

    // ================= 无缝衔接决策（P2） =================

    private static void RunSeamlessChecks()
    {
        Console.WriteLine("=== 采样率解析 ===");
        var follow = new OutputSettings { RateMode = SampleRateMode.Follow };
        var fixed48 = new OutputSettings { RateMode = SampleRateMode.Fixed, FixedSampleRate = 48000 };

        Check("跟随模式：输出跟着源文件走", SeamlessPolicy.ResolveOutputRate(96000, follow) == 96000);
        Check("跟随模式：拿不到采样率时退回 44100", SeamlessPolicy.ResolveOutputRate(0, follow) == 44100);
        Check("固定模式：无视源文件采样率", SeamlessPolicy.ResolveOutputRate(96000, fixed48) == 48000);

        Console.WriteLine();
        Console.WriteLine("=== 能否无缝衔接 ===");
        Check("跟随模式 · 同采样率 → 无缝",
            SeamlessPolicy.CanTransitionSeamlessly(44100, 44100, follow));
        Check("跟随模式 · 44.1k→48k → 不无缝（要重建链路）",
            !SeamlessPolicy.CanTransitionSeamlessly(44100, 48000, follow));
        Check("跟随模式 · 44.1k→88.2k → 不无缝",
            !SeamlessPolicy.CanTransitionSeamlessly(44100, 88200, follow));
        Check("固定 48k · 44.1k→96k 也能无缝（都被重采样到 48k）",
            SeamlessPolicy.CanTransitionSeamlessly(44100, 96000, fixed48));
        Check("下一曲采样率未知 → 不无缝",
            !SeamlessPolicy.CanTransitionSeamlessly(44100, 0, follow));

        Console.WriteLine();
        Console.WriteLine("=== 预载时机（提前 5 秒） ===");
        Check("剩 4 秒 → 该预载了", SeamlessPolicy.ShouldPreload(196, 200, false));
        Check("剩 10 秒 → 还不用", !SeamlessPolicy.ShouldPreload(190, 200, false));
        Check("正好剩 5 秒 → 该预载了", SeamlessPolicy.ShouldPreload(195, 200, false));
        Check("已经预载过 → 不重复", !SeamlessPolicy.ShouldPreload(196, 200, true));
        Check("时长未知 → 不预载", !SeamlessPolicy.ShouldPreload(10, 0, false));
        Check("还没开始播 → 不预载", !SeamlessPolicy.ShouldPreload(0, 200, false));

        Console.WriteLine();
        Console.WriteLine("=== 播放模式下的下一曲预测 ===");
        var tracks = Enumerable.Range(1, 5)
            .Select(i => new TrackRecord { Id = i, Path = $"C:/m/{i}.flac", Title = $"T{i}" })
            .ToList();

        var list = new PlaybackList();
        list.Replace(tracks, "测试", 0);

        list.Mode = PlayMode.Sequential;
        Check("顺序：第 1 首的下一曲是第 2 首", list.PeekNext()?.Id == 2);
        list.MoveTo(4);
        Check("顺序：最后一首没有下一曲", list.PeekNext() is null);

        list.Mode = PlayMode.RepeatAll;
        Check("列表循环：最后一首的下一曲绕回第 1 首", list.PeekNext()?.Id == 1);

        list.Mode = PlayMode.RepeatOne;
        Check("单曲循环：下一曲就是自己", list.PeekNext()?.Id == 5);

        list.Mode = PlayMode.Shuffle;
        var peeked = list.PeekNext();
        var actual = list.MoveNext(userInitiated: false);
        Check("随机：预测到的下一曲与真正切过去的一致",
            peeked is null || (actual is not null && peeked.Id == actual.Id));

        list.Mode = PlayMode.Sequential;
        list.Replace(tracks, "测试", 0);
        Check("换列表后预测重新生效", list.PeekNext()?.Id == 2);

        Console.WriteLine();
        Console.WriteLine("=== 输出设置往返 ===");
        var config = new OutputConfig();
        var settings = new OutputSettings
        {
            Backend = OutputBackendKind.Asio,
            DeviceName = "TOPPING E1x2 OTG",
            Exclusive = true,
            RateMode = SampleRateMode.Follow,
            FixedSampleRate = 96000,
            AsioBufferSamples = 256,
            AsioFirstChannel = 2,
            WasapiBufferMs = 100
        };
        config.CopyFrom(settings);
        var restored = config.ToSettings();

        Check("后端往返", restored.Backend == OutputBackendKind.Asio);
        Check("设备名往返", restored.DeviceName == "TOPPING E1x2 OTG");
        Check("采样率策略往返", restored.RateMode == SampleRateMode.Follow);
        Check("ASIO 缓冲往返", restored.AsioBufferSamples == 256);
        Check("ASIO 起始声道往返", restored.AsioFirstChannel == 2);
        Check("WASAPI 缓冲往返", restored.WasapiBufferMs == 100);
    }

    // ================= 媒体库端到端（P1，P2 继续沿用） =================

    private static async Task RunLibraryChecksAsync(string root, string? outsideDir)
    {
        LogSetup.Initialize();
        Db.Initialize();

        ConfigService.Current.Library.Folders.Clear();
        ConfigService.Current.Library.Folders.Add(root);

        using var library = new LibraryService();

        var full = await library.ScanAsync(fullRescan: true);
        Console.WriteLine($"【全量扫描】{full}");
        Console.WriteLine($"  曲库 {library.Tracks.Count} 首 · 专辑 {library.GetAlbums().Count} · " +
                          $"艺术家 {library.GetArtists().Count} · 文件夹歌单 {library.GetFolderPlaylists().Count}");
        Check("扫描到了曲目", library.Tracks.Count > 0);
        Check("全量扫描在 2 分钟内完成（PLAN P1 验收线）", full.Elapsed < TimeSpan.FromMinutes(2));

        var incremental = await library.ScanAsync(fullRescan: false);
        Console.WriteLine($"【增量扫描】{incremental}");
        Check("无变化时增量扫描不写库", incremental.AddedOrUpdated == 0);

        var playlists = new PlaylistService(library);
        playlists.Load();
        foreach (var old in playlists.Playlists.ToList()) playlists.Delete(old.Id);

        var id = playlists.Create("harness 测试歌单");
        playlists.AddTracks(id, library.Tracks.Take(10));
        Check("歌单加入 10 首", playlists.GetTracks(id).Count == 10);

        var items = playlists.GetTracks(id).ToList();
        playlists.InsertTracks(id, 0, new[] { items[^1] });
        Check("按落点插入：末尾那首被移到首位", playlists.GetTracks(id)[0].Id == items[^1].Id);
        Check("按落点插入不改变总数", playlists.GetTracks(id).Count == 10);

        if (!string.IsNullOrWhiteSpace(outsideDir) && Directory.Exists(outsideDir))
        {
            var outsideFiles = Directory.GetFiles(outsideDir).Take(3).ToArray();
            var imported = await library.ImportFilesAsync(outsideFiles);
            Check("库外文件能并入曲库", imported.Count > 0);

            playlists.AddTracks(id, imported);
            var before = playlists.GetTracks(id).Count;

            await library.ScanAsync(fullRescan: true);
            var after = playlists.GetTracks(id).Count;

            Check("全量扫描不会删掉库外手动加入的曲目（歌单条目数不变）", before == after);
        }

        var m3u = Path.Combine(Path.GetTempPath(), "harness.m3u8");
        playlists.ExportM3u(playlists.GetTracks(id), m3u);
        var (importedId, matched, _) = playlists.ImportM3u(m3u);
        Check("m3u8 导出再导入条目数一致", matched == playlists.GetTracks(id).Count);
        playlists.Delete(importedId);

        using var reopened = new LibraryService();
        reopened.Load();
        var playlists2 = new PlaylistService(reopened);
        playlists2.Load();
        Check("重启后曲库还在", reopened.Tracks.Count == library.Tracks.Count);
        Check("重启后歌单还在", playlists2.GetTracks(id).Count == playlists.GetTracks(id).Count);
        Check("重启后文件夹虚拟歌单能重建", reopened.GetFolderPlaylists().Count == library.GetFolderPlaylists().Count);

        LogSetup.Shutdown();
    }


    // ================= P3：歌词与在线层（纯逻辑 + 临时库，无网络） =================

    private static async Task RunLyricsChecksAsync()
    {
        RunLrcChecks();
        RunBucketChecks();
        RunQuotaHeaderChecks();
        RunMatcherChecks();
        RunClientPureFunctionChecks();
        RunLyricLayoutChecks();

        // 存储层需要临时库
        // 内嵌歌词（P3.1-③）：用 format-test 的 FLAC 样本 + TagLibSharp 写入后读回
        var sampleDir = Path.GetFullPath(Path.Combine("publish", "format-test", "hires"));
        RunEmbeddedLyricsChecks(sampleDir);

        var dbPath = Path.Combine(Path.GetTempPath(), "harness-lyrics-" + Guid.NewGuid().ToString("N")[..8] + ".db");
        try
        {
            Db.Initialize(dbPath);
            RunCacheStoreChecks();
            await RunLyricsServiceFallbackChecksAsync(sampleDir);
        }
        finally
        {
            try { File.Delete(dbPath); File.Delete(dbPath + "-wal"); File.Delete(dbPath + "-shm"); }
            catch { /* 忽略清理失败 */ }
        }
    }

    // ---------------- LRC 解析 ----------------

    private static void RunLrcChecks()
    {
        Console.WriteLine("=== LRC 解析 ===");

        var doc = LrcParser.Parse("""
            [ti:测试]
            [ar:某歌手]
            [offset:+500]
            [00:01.00]第一句
            [00:03.00]第二句
            [00:05.00]第三句
            """);
        Check("元数据标签被跳过、offset 标签被解析", doc.TagOffset == TimeSpan.FromMilliseconds(500));
        Check("正常解析出行", doc.Lines.Count == 3 && doc.HasTimeline);
        Check("时间轴按时间排序", doc.Lines[0].Time == TimeSpan.FromSeconds(1) && doc.Lines[2].Time == TimeSpan.FromSeconds(5));

        var multi = LrcParser.Parse("[00:01.00][00:03.50]同一句重复");
        Check("一行多时间标签展开成多行", multi.Lines.Count == 2);

        var fractions = LrcParser.Parse("[00:01.2]点两位小数\n[00:02.345]点三位小数");
        Check("小数位补零正确（.2 → 200ms）", fractions.Lines[0].Time == TimeSpan.FromMilliseconds(1200));
        Check("小数位三位正确（.345 → 345ms）", fractions.Lines[1].Time == TimeSpan.FromMilliseconds(2345));

        var plain = LrcParser.Parse("没有时间标签的第一行\n第二行");
        Check("无时间轴降级为整篇静态", !plain.HasTimeline && plain.Lines.Count == 2);

        Check("空内容返回 Empty", LrcParser.Parse(null).IsEmpty && LrcParser.Parse("").IsEmpty);

        // FindIndexAt：位置在第一行前 → -1
        Check("位置在第一行之前返回 -1", doc.FindIndexAt(TimeSpan.FromSeconds(0.5)) == -1);
        Check("正好命中第二行", doc.FindIndexAt(TimeSpan.FromSeconds(3)) == 1);
        Check("两行之间取上一行", doc.FindIndexAt(TimeSpan.FromSeconds(4.2)) == 1);
        Check("超出最后一行取末行", doc.FindIndexAt(TimeSpan.FromSeconds(99)) == 2);

        // Merge：翻译并轨（容差 500ms）
        var original = LrcParser.Parse("[00:01.00]原文1\n[00:03.00]原文2");
        var translation = LrcParser.Parse("[00:01.20]译1\n[00:03.30]译2");
        var merged = LrcParser.Merge(original, translation, null);
        Check("翻译在容差内并轨", merged.Lines[0].Translation == "译1");
        Check("第二行 300ms 差也在容差内并轨", merged.Lines[1].Translation == "译2");

        var far = LrcParser.Merge(
            LrcParser.Parse("[00:01.00]A"), LrcParser.Parse("[00:05.00]B"), null);
        Check("翻译距离超过容差 → 不并轨", far.Lines[0].Translation is null or "");

        Console.WriteLine();
    }

    // ---------------- 令牌桶 ----------------

    private static void RunBucketChecks()
    {
        Console.WriteLine("=== 令牌桶（18 次/分） ===");

        var bucket = new TokenBucket(18, TimeSpan.FromMinutes(1));
        var now = DateTime.UtcNow;

        for (var i = 0; i < 18; i++)
        {
            var ok = bucket.TryTake(now, out _);
            if (!ok) { Check($"第 {i + 1} 次应成功", false); break; }
        }
        Check("窗口内 18 次全部可取", bucket.AvailableNow == 0);

        var blocked = bucket.TryTake(now, out var retryAfter);
        Check("第 19 次被拒绝", !blocked);
        Check("拒绝时给出等待时间", retryAfter > TimeSpan.Zero && retryAfter <= TimeSpan.FromMinutes(1));

        var afterSlide = bucket.TryTake(now + TimeSpan.FromMinutes(1) + TimeSpan.FromSeconds(1), out _);
        Check("窗口滑出后可再取", afterSlide);

        var capacity = new TokenBucket(2, TimeSpan.FromMinutes(1));
        capacity.TryTake(now, out _);
        capacity.TryTake(now, out _);
        var third = capacity.TryTake(now + TimeSpan.FromSeconds(59), out _);
        Check("未滑出窗口前仍被拒", !third);

        Console.WriteLine();
    }

    // ---------------- 额度响应头 ----------------

    private static void RunQuotaHeaderChecks()
    {
        Console.WriteLine("=== 额度响应头解析 ===");

        Check("正常数字", QuotaTracker.ParseHeader("358") == 358);
        Check("带空白", QuotaTracker.ParseHeader(" 400 ") == 400);
        Check("空头返回 null（不是 0）", QuotaTracker.ParseHeader(null) is null);
        Check("非数字返回 null", QuotaTracker.ParseHeader("abc") is null);
        Check("空字符串返回 null", QuotaTracker.ParseHeader("") is null);

        var tracker = new QuotaTracker();
        tracker.Update(name => name switch
        {
            "X-Quota-Free-Remaining" => "399",
            "X-Quota-Paid-Remaining" => "0",
            _ => null
        });
        Check("更新后 FreeRemaining=399", tracker.FreeRemaining == 399);
        Check("更新后 PaidRemaining=0", tracker.PaidRemaining == 0);
        Check("展示文案", tracker.DisplayText == "API 剩 399");

        var exhausted = new QuotaTracker();
        exhausted.Update(name => name == "X-Quota-Free-Remaining" ? "0" : "0");
        Check("免费+付费都为 0 时视为额度用尽", exhausted.IsExhausted);

        var unknown = new QuotaTracker();
        Check("从未收到响应头时不算用尽", !unknown.IsExhausted);

        Console.WriteLine();
    }

    // ---------------- 匹配算法 ----------------

    private static void RunMatcherChecks()
    {
        Console.WriteLine("=== 网易云 ID 匹配 ===");

        Check("全角转半角", LyricMatcher.Normalize("ＡＢＣ１２３") == "abc123");
        Check("去括号及内容", LyricMatcher.Normalize("晴天 (Live)") == "晴天");
        Check("去方括号内容", LyricMatcher.Normalize("晴天[翻唱]") == "晴天");
        Check("空白压缩", LyricMatcher.Normalize("  晴  天  ") == "晴天");
        Check("标点与空白直接丢弃（中文匹配更稳）", LyricMatcher.Normalize("Hello, World!") == "helloworld");
        Check("大小写统一", LyricMatcher.Normalize("HELLO") == "hello");

        Check("完全相同 = 1.0", LyricMatcher.TextSimilarity("晴天", "晴天") == 1.0);
        Check("包含关系高分", LyricMatcher.TextSimilarity("晴天", "晴天 钢琴版") >= 0.55);
        Check("完全不同 = 0", LyricMatcher.TextSimilarity("abc", "xyz") == 0);
        Check("编辑距离相似度", LyricMatcher.TextSimilarity("晴天", "晴天2") > 0.8);

        Check("时长差 3 秒内算命中", LyricMatcher.DurationInTolerance(252000, 253000));
        Check("时长差超过 3 秒不算", !LyricMatcher.DurationInTolerance(260000, 253000));
        Check("未知时长不算命中", !LyricMatcher.DurationInTolerance(0, 253000));

        var search = new SearchResult
        {
            Songs = new List<SearchSong>
            {
                new() { Id = 1, Name = "晴天", Artists = "周杰伦", Duration = 269000 },
                new() { Id = 2, Name = "晴天", Artists = "翻唱者", Duration = 120000 },   // 时长差太远
                new() { Id = 3, Name = "晴天 (Live)", Artists = "周杰伦", Duration = 271000 }  // 相似但稍低
            }
        };

        var best = LyricMatcher.PickBest(search, "晴天", "周杰伦", 269000);
        Check("时长过滤 + 相似度择优 → 原版", best?.Id == 1);

        var allWrong = new SearchResult
        {
            Songs = new List<SearchSong>
            {
                new() { Id = 9, Name = "阴天", Artists = "李四", Duration = 269000 }
            }
        };
        Check("相似度过低 → 未匹配（宁可空）", LyricMatcher.PickBest(allWrong, "晴天", "周杰伦", 269000) is null);

        var ranked = LyricMatcher.RankCandidates(search, "晴天", "周杰伦", 269000);
        Check("候选排序第一位是原版", ranked[0].Id == 1);

        Console.WriteLine();
    }

    // ---------------- ChkszClient 纯函数 ----------------

    private static void RunClientPureFunctionChecks()
    {
        Console.WriteLine("=== ChkszClient 脱敏与错误映射 ===");

        var redacted = ChkszClient.Redact("https://api.chksz.com/api/163_search?apikey=chksz_secret123&keyword=x");
        Check("URL 脱敏 apikey", redacted.Contains("apikey=***") && !redacted.Contains("secret123"));

        Check("400 → 参数错误", ChkszClient.MapError<int>(400, null).Error.Contains("参数"));
        Check("401 → Key 无效", ChkszClient.MapError<int>(401, null).AuthFailed);
        Check("402 → 额度用尽", ChkszClient.MapError<int>(402, null).QuotaExhausted);
        Check("404 → 资源不存在", ChkszClient.MapError<int>(404, null).NotFound);
        Check("429 → 频繁", ChkszClient.MapError<int>(429, null).Error.Contains("频繁"));
        Check("503 → 稍后再试", ChkszClient.MapError<int>(503, null).Error.Contains("稍后"));
        Check("200 未映射 → 通用错误", ChkszClient.MapError<int>(500, null).Error.Contains("500"));

        Console.WriteLine();
    }


    // ---------------- 自绘歌词布局（UI-R0 纯函数） ----------------

    private static void RunLyricLayoutChecks()
    {
        Console.WriteLine("=== 自绘歌词布局（UI-R0） ===");

        // 目标偏移：当前行居中
        Check("第 0 行目标 = 0", LyricLayout.TargetOffsetFor(0, 100, 500) == 0);
        Check("第 5 行目标居中", LyricLayout.TargetOffsetFor(5, 100, 500) == 5 * 52 + 26 - 250);
        Check("末行钳制到最大偏移", LyricLayout.TargetOffsetFor(99, 100, 500) == 100 * 52 - 500);
        Check("负索引 = 0", LyricLayout.TargetOffsetFor(-1, 100, 500) == 0);
        Check("空列表 = 0", LyricLayout.TargetOffsetFor(3, 0, 500) == 0);

        // 可见范围
        var (first, last) = LyricLayout.VisibleRange(0, 500, 100);
        Check("offset=0 可见 0..9", first == 0 && last == 9);
        var (f2, l2) = LyricLayout.VisibleRange(520, 500, 100);
        Check("offset=520 可见 10..19", f2 == 10 && l2 == 19);
        var (f3, l3) = LyricLayout.VisibleRange(0, 500, 0);
        Check("空列表 (-1,-1)", f3 == -1 && l3 == -1);

        // 淡出
        Check("当前行不透明", LyricLayout.LineFade(0) == 1.0);
        Check("相邻行渐淡", LyricLayout.LineFade(1) > LyricLayout.LineFade(3));
        Check("远处收敛", Math.Abs(LyricLayout.LineFade(6) - LyricLayout.LineFade(9)) < 0.001);

        // 缓动
        var (o1, s1) = LyricLayout.EaseTowards(0, 100, 0.1);
        Check("缓动朝目标收敛", o1 > 0 && o1 < 100 && !s1);
        var (o2, s2) = LyricLayout.EaseTowards(0, 0.2, 0.1);
        Check("到位判定（<0.5px）", o2 == 0.2 && s2);

        // 命中测试
        Check("y=26 命中第 0 行", LyricLayout.HitTest(26, 0, 10) == 0);
        Check("offset 后命中正确行", LyricLayout.HitTest(26, 520, 20) == 10);
        Check("越界返回 -1", LyricLayout.HitTest(9999, 0, 10) == -1);

        // 滚轮步进方向
        Check("滚轮向上=内容上移", LyricLayout.WheelStep(120) < 0);
        Check("滚轮向下=内容下移", LyricLayout.WheelStep(-120) > 0);

        Console.WriteLine();
    }

    // ---------------- 内嵌标签歌词（P3.1-③） ----------------

    private static void RunEmbeddedLyricsChecks(string sampleDir)
    {
        Console.WriteLine("=== 内嵌标签歌词（TagLibSharp 读 USLT/LYRICS） ===");

        var sample = Path.Combine(sampleDir, "flac_44k_440Hz.flac");
        if (!File.Exists(sample))
        {
            Console.WriteLine("  跳过：找不到 format-test 样本（publish/format-test/hires/flac_44k_440Hz.flac）");
            return;
        }

        var target = Path.Combine(Path.GetTempPath(), "embedded-" + Guid.NewGuid().ToString("N")[..8] + ".flac");
        File.Copy(sample, target);

        try
        {
            // 形态一：带 LRC 时间轴
            using (var file = TagLib.File.Create(target))
            {
                file.Tag.Lyrics = "[00:01.00]内嵌第一句\n[00:05.00]内嵌第二句";
                file.Save();
            }

            var lyrics = TagReader.ReadLyrics(target);
            Check("内嵌歌词读回", lyrics is not null && lyrics.Contains("内嵌第一句"));

            var doc = LrcParser.Parse(lyrics);
            Check("内嵌带时间轴 → 滚动形态", doc.HasTimeline && doc.Lines.Count == 2);

            // 形态二：纯文本
            using (var file2 = TagLib.File.Create(target))
            {
                file2.Tag.Lyrics = "没有时间轴的纯文本歌词";
                file2.Save();
            }

            var plain = LrcParser.Parse(TagReader.ReadLyrics(target));
            Check("内嵌纯文本 → 静态形态", !plain.HasTimeline && plain.Lines.Count == 1 && plain.PlainText.Contains("纯文本"));

            // 没有内嵌歌词的文件 → null
            File.Delete(target);
            File.Copy(sample, target);
            Check("无内嵌歌词返回 null", TagReader.ReadLyrics(target) is null);
        }
        finally
        {
            try { File.Delete(target); }
            catch { /* 忽略清理失败 */ }
        }

        Console.WriteLine();
    }

    // ---------------- 缓存存储（临时库） ----------------

    private static void RunCacheStoreChecks()
    {
        Console.WriteLine("=== 歌词缓存存储 ===");

        LyricsCacheStore.SaveCached("163:12345", new CachedLyric
        {
            Lrc = "[00:01.00]测试",
            TranslatedLrc = "",
            RomajiLrc = ""
        });
        var cached = LyricsCacheStore.GetCached("163:12345");
        Check("歌词缓存往返", cached is not null && cached.Lrc == "[00:01.00]测试");
        Check("不存在的缓存返回 null", LyricsCacheStore.GetCached("163:99999") is null);

        // netease_id 挂在 tracks 行上：先造一行再写
        LibraryDb.UpsertTracks(new[]
        {
            new TrackRecord { Path = @"C:\music\a.flac", Title = "a", DurationMs = 1000 }
        });
        LyricsCacheStore.SaveNeteaseId(@"C:\music\a.flac", 12345);
        var map = LyricsCacheStore.LoadNeteaseIds();
        Check("netease_id 持久化并可读回", map.TryGetValue(@"C:\music\a.flac", out var id) && id == 12345);

        LyricsCacheStore.SaveManualOffset(@"C:\music\a.flac", TimeSpan.FromMilliseconds(300));
        var offset = LyricsCacheStore.GetManualOffset(@"C:\music\a.flac");
        Check("手动偏移存取", offset == TimeSpan.FromMilliseconds(300));
        Check("未设置偏移返回 null", LyricsCacheStore.GetManualOffset(@"C:\music\b.flac") is null);

        Console.WriteLine();
    }

    // ---------------- LyricsService 离线降级 ----------------

    private static async Task RunLyricsServiceFallbackChecksAsync(string sampleDir)
    {
        Console.WriteLine("=== LyricsService 离线降级与优先级 ===");

        using var client = new ChkszClient();
        using var service = new LyricsService(client);

        // 没有 Key 时在线能力应整体降级
        Check("无 Key → IsOnlineAvailable 为 false", !service.IsOnlineAvailable);

        // P3.1-⑤：未设置偏好的歌默认走网易云（用户实测结论）
        Check("默认来源偏好 = 网易云", service.GetPreference(@"C:\never-set\a.flac") == LyricPreference.Online);

        // .lrc 文件优先级：同目录同名
        var dir = Path.Combine(Path.GetTempPath(), "harness-lrc-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        try
        {
            var audioPath = Path.Combine(dir, "song.flac");
            await File.WriteAllTextAsync(audioPath, "fake");
            await File.WriteAllTextAsync(Path.Combine(dir, "song.lrc"), "[00:01.00]本地歌词第一句\n[00:05.00]本地歌词第二句");

            var track = new TrackRecord
            {
                Id = 1,
                Path = audioPath,
                Title = "song",
                Artist = "test",
                DurationMs = 300000
            };

            // 默认偏好是网易云；这里显式切回自动，验证 Auto 链下的 .lrc 行为
            service.SetPreference(audioPath, LyricPreference.Auto);

            var result = await service.LoadForTrackAsync(track);
            Check(".lrc 文件被优先加载", result.Source == LyricSource.LocalFile);
            Check(".lrc 内容正确", result.Document.Lines.Count == 2 && result.Document.Lines[0].Text == "本地歌词第一句");
            Check("无 Key 时没有在线歌词也不抛异常", result.Document.Lines.Count == 2);

            // 没有 .lrc、没有 Key → 安静返回 Empty（不弹窗不崩溃）
            var noLrc = new TrackRecord { Id = 2, Path = Path.Combine(dir, "no-lrc.flac"), Title = "x", DurationMs = 1000 };
            var empty = await service.LoadForTrackAsync(noLrc);
            Check("无 .lrc 无 Key → Empty（优雅降级）", empty.IsEmpty);

            // 同一首歌第二次加载不再尝试匹配（会话记忆，不烧额度）
            var again = await service.LoadForTrackAsync(noLrc);
            Check("会话内重复加载稳定返回 Empty", again.IsEmpty);

            // ---- P3.1-③ 内嵌歌词与优先级链 ----
            var sample = Path.Combine(sampleDir, "flac_44k_440Hz.flac");
            if (File.Exists(sample))
            {
                var embeddedPath = Path.Combine(dir, "embedded.flac");
                File.Copy(sample, embeddedPath);
                using (var file = TagLib.File.Create(embeddedPath))
                {
                    file.Tag.Lyrics = "[00:01.00]内嵌第一句\n[00:05.00]内嵌第二句";
                    file.Save();
                }

                var embeddedTrack = new TrackRecord
                {
                    Id = 3,
                    Path = embeddedPath,
                    Title = "embedded",
                    Artist = "test",
                    DurationMs = 300000
                };

                // 默认偏好是网易云；这里显式切回自动，验证 Auto 链下的内嵌行为
                service.SetPreference(embeddedPath, LyricPreference.Auto);

                // 无 .lrc 有内嵌 → Embedded，且不碰 API（无 Key 环境下也验证来源标记）
                var embeddedResult = await service.LoadForTrackAsync(embeddedTrack);
                Check("无 .lrc 有内嵌 → 内嵌标签来源", embeddedResult.Source == LyricSource.Embedded);
                Check("内嵌带时间轴 → 滚动", embeddedResult.Document.HasTimeline && embeddedResult.Document.Lines.Count == 2);
                Check("有内嵌时不尝试在线匹配（会话内不烧额度）",
                    service.GetNeteaseId(embeddedPath) is null);

                // 同目录 .lrc 存在 → .lrc 优先于内嵌
                await File.WriteAllTextAsync(Path.Combine(dir, "embedded.lrc"),
                    "[00:01.00]本地歌词优先\n[00:05.00]第二句");
                var lrcFirst = await service.LoadForTrackAsync(embeddedTrack);
                Check(".lrc 存在时优先于内嵌标签", lrcFirst.Source == LyricSource.LocalFile);
                Check(".lrc 内容确实来自文件", lrcFirst.Document.Lines[0].Text == "本地歌词优先");

                // ---- 来源偏好（右键菜单「歌词来源」，P3.1-④） ----
                service.SetPreference(embeddedPath, LyricPreference.Embedded);
                var prefEmbedded = await service.LoadForTrackAsync(embeddedTrack);
                Check("偏好「内嵌标签」→ 跳过 .lrc 用内嵌", prefEmbedded.Source == LyricSource.Embedded);
                Check("偏好持久化可读回", service.GetPreference(embeddedPath) == LyricPreference.Embedded);

                service.SetPreference(embeddedPath, LyricPreference.Online);
                var prefOnline = await service.LoadForTrackAsync(embeddedTrack);
                Check("偏好「网易云」→ 跳过 .lrc 与内嵌（无 Key 时 Empty）", prefOnline.IsEmpty);

                service.SetPreference(embeddedPath, LyricPreference.LrcFile);
                var prefLrc = await service.LoadForTrackAsync(embeddedTrack);
                Check("偏好「本地 .lrc」→ 用 .lrc", prefLrc.Source == LyricSource.LocalFile);

                service.SetPreference(embeddedPath, LyricPreference.Auto);
                var prefAuto = await service.LoadForTrackAsync(embeddedTrack);
                Check("恢复「自动」→ 默认链（.lrc 优先）", prefAuto.Source == LyricSource.LocalFile);
            }
            else
            {
                Console.WriteLine("  跳过：内嵌优先级断言（找不到 format-test 样本）");
            }
        }
        finally
        {
            try { Directory.Delete(dir, true); }
            catch { /* 忽略清理失败 */ }
        }

        Console.WriteLine();
    }

    private static void Check(string what, bool ok)
    {
        if (ok) _passed++; else _failed++;
        Console.WriteLine($"  {(ok ? "✓" : "✗ 失败")}  {what}");
    }
}