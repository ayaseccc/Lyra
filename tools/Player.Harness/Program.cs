using Player.Core.Audio;
using Player.Core.Downloads;
using Player.Core.Hotkeys;
using Player.Core.Infra;
using Player.Core.Library;
using Player.Core.Lyrics;
using Player.Core.Online;
using Player.Core.Theming;

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
    private static int _skipped;

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

            case "grouping":
                RunGroupingChecks();
                break;

            case "theme":
                RunThemeChecks();
                break;

            case "shortcuts":
                RunShortcutChecks();
                break;

            case "gdprobe":
                await RunGdProbeAsync();
                break;

            case "downloads":
                RunDownloadTemplateChecks();
                RunL31ConfigRoundtrip();
                break;

            case "dlprobe":
                await RunDownloadProbeAsync();
                break;

            case "netfail":
                RunNetFailChecks();
                break;

            case "urlprobe":
                if (args.Length < 2) { Console.WriteLine("用法：urlprobe <url>"); return 2; }
                RunUrlProbe(args[1]);
                break;

            case "dsp":
                RunDspProbe();
                break;

            default:
                Console.WriteLine($"未知模式：{mode}（可用：seamless / library / lyrics / grouping / theme / shortcuts / gdprobe）");
                return 2;
        }

        Console.WriteLine();
        Console.WriteLine($"===== 通过 {_passed} 项，失败 {_failed} 项，跳过 {_skipped} 项 =====");
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

        // 「下一首播放」插队（2026-08-16 右键菜单）
        list.Replace(tracks, "测试", 0);   // 当前 T1
        var insert = new List<TrackRecord> { tracks[3], tracks[4] };   // T4, T5
        var at = list.InsertAfterCurrent(insert);
        Check("插队：插入位置在当前之后", at == 1);
        Check("插队：当前曲目不变", list.Current?.Id == 1);
        list.MoveTo(at);
        Check("插队：切到插入的第一首", list.Current?.Id == 4);
        Check("插队：下一曲是插入的第二首", list.PeekNext()?.Id == 5);
        list.MoveNext(userInitiated: true);
        Check("插队：播完插队回到原队列", list.PeekNext()?.Id == 2);

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
        Check("头部元数据捕获（ti/ar）", doc.Header.TryGetValue("ti", out var ti) && ti == "测试"
            && doc.Header.TryGetValue("ar", out var ar) && ar == "某歌手");

        var credit = LrcParser.Parse("[ti:歌][作词:林夕][曲:陈辉阳][编曲:王双骏]\n[00:01.00]词曲示例");
        Check("制作信息头部捕获（作词/曲/编曲）",
            credit.Header.TryGetValue("作词", out var lyr) && lyr == "林夕"
            && credit.Header.TryGetValue("曲", out var comp) && comp == "陈辉阳"
            && credit.Header.TryGetValue("编曲", out var arr) && arr == "王双骏");
        Check("带制作信息头部的歌词仍正常解析", credit.Lines.Count == 1 && credit.HasTimeline);

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

        // 脱敏夹具用假 Key（铁律：代码里不出现 chksz_ 字样，审查修复）
        var redacted = ChkszClient.Redact("https://api.chksz.com/api/163_search?apikey=sk-test-redact-fake&keyword=x");
        Check("URL 脱敏 apikey", redacted.Contains("apikey=***") && !redacted.Contains("redact-fake"));

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
        Console.WriteLine("=== 自绘歌词布局（UI-R5 单元化） ===");

        // ---- 元数据识别（R5 ①：从时间流剥离） ----
        Check("元数据识别 作词：X", LyricLayout.TryParseMetadata("作词：林夕") is { Key: "作词" } m1 && m1.Value == "林夕");
        Check("元数据识别 作曲:X（半角冒号）", LyricLayout.TryParseMetadata("作曲:X") is { Key: "作曲" });
        Check("元数据识别 编曲 X（空格变体）", LyricLayout.TryParseMetadata("编曲 王双骏") is { Key: "编曲" });
        Check("元数据识别 OP：X", LyricLayout.TryParseMetadata("OP：シグナル") is { Key: "OP" });
        Check("元数据识别 作词人：X", LyricLayout.TryParseMetadata("作词人：林夕") is { Key: "作词人" });
        Check("元数据识别 前导空格+全角冒号", LyricLayout.TryParseMetadata("  作词　：　林夕  ") is { Key: "作词" });
        Check("元数据识别 不误判普通歌词", !LyricLayout.IsMetadataLine("春风十里不如你"));
        Check("键归一 词→作词", LyricLayout.NormalizeMetadataKey("词") == "作词");

        // ---- 折行（R5 ④：CJK 逐字符 / 拉丁按词，禁止截断） ----
        // 合成量法：CJK 每字符 17px，拉丁半宽 8.5px，空格 4px
        double Measure(string s) => s.Sum(c => c >= 0x2E80 ? 17.0 : c == ' ' ? 4.0 : 8.5);

        var cjk = LyricLayout.WrapText("春风吹又生", 51, Measure);
        Check("CJK 按字符折行（51px = 3 字符）", cjk.Count == 2 && cjk[0] == "春风吹" && cjk[1] == "又生");

        var latin = LyricLayout.WrapText("hello world foo", 60, Measure);
        Check("拉丁按词折行（词界空格断）", latin.Count == 3 && latin[0] == "hello" && latin[1] == "world" && latin[2] == "foo");

        var noTrunc = LyricLayout.WrapText("超长单字符", 10, Measure);
        Check("单字符超宽不截断（完整显示）", noTrunc.Count == 5 && string.Concat(noTrunc) == "超长单字符");

        var empty = LyricLayout.WrapText("", 100, Measure);
        Check("空文本折行返回空", empty.Count == 0);

        // ---- 单元布局（R5 ③：动态高度，无翻译不留空位） ----
        var layout = LyricLayout.BuildUnitLayout(new[] { 1, 2, 1 }, new[] { 1, 0, 0 }, isSecondaryShown: true);
        Check("有翻译单元 = 主行高+副行高+内距", layout[0].Height == LyricLayout.PrimaryLineHeight + LyricLayout.SecondaryLineHeight + LyricLayout.InnerGap);
        Check("无翻译单元不保留空位（动态高度）", layout[1].Height == 2 * LyricLayout.PrimaryLineHeight);
        Check("副文本隐藏时不占位", LyricLayout.BuildUnitLayout(new[] { 1 }, new[] { 2 }, isSecondaryShown: false)[0].Height == LyricLayout.PrimaryLineHeight);

        var tops = LyricLayout.ComputeUnitTops(new[] { 30.0, 40.0, 30.0 });
        Check("单元顶部偏移累加（含间距）", tops[1] == 30 + LyricLayout.UnitGap && tops[2] == 30 + LyricLayout.UnitGap + 40 + LyricLayout.UnitGap);

        // ---- 滚动目标 = 当前单元几何中心（R5 ⑥） ----
        var heights = new[] { 30.0, 60.0, 30.0, 30.0 };
        Check("单元 0 目标 = 0", LyricLayout.TargetOffsetForUnit(0, heights, 120) == 0);
        Check("单元 1 目标 = 几何中心", LyricLayout.TargetOffsetForUnit(1, heights, 120) == 46 + 30 - 60);
        Check("单元 2 目标 = 几何中心", LyricLayout.TargetOffsetForUnit(2, heights, 120) == 122 + 15 - 60);
        Check("末单元钳制到最大偏移", LyricLayout.TargetOffsetForUnit(3, heights, 120) == LyricLayout.TotalHeight(heights) - 120);
        Check("负索引 = 0", LyricLayout.TargetOffsetForUnit(-1, heights, 120) == 0);

        // ---- 可见范围 / 命中（按单元） ----
        var (first, last) = LyricLayout.VisibleUnits(0, 120, heights);
        Check("offset=0 可见 0..1", first == 0 && last == 1);
        var (f2, l2) = LyricLayout.VisibleUnits(LyricLayout.TotalHeight(heights) - 120, 120, heights);
        Check("offset=最大 可见 1..3", f2 == 1 && l2 == 3);
        Check("空列表 (-1,-1)", LyricLayout.VisibleUnits(0, 120, Array.Empty<double>()) == (-1, -1));
        Check("单元 1 中部命中", LyricLayout.HitTestUnit(46 + 20, 0, heights) == 1);
        Check("命中越界 -1", LyricLayout.HitTestUnit(9999, 0, heights) == -1);

        // ---- 缓动（沿用） ----
        var (o1, s1) = LyricLayout.EaseTowards(0, 100, 0.1);
        Check("缓动朝目标收敛", o1 > 0 && o1 < 100 && !s1);
        var (o2, s2) = LyricLayout.EaseTowards(0, 0.2, 0.1);
        Check("到位判定（<0.5px）", o2 == 0.2 && s2);

        // ---- 滚轮步进方向 ----
        Check("滚轮向上=内容上移", LyricLayout.WheelStep(120) < 0);
        Check("滚轮向下=内容下移", LyricLayout.WheelStep(-120) > 0);

    }

    // ---------------- 内嵌标签歌词（P3.1-③） ----------------

    private static void RunEmbeddedLyricsChecks(string sampleDir)
    {
        Console.WriteLine("=== 内嵌标签歌词（TagLibSharp 读 USLT/LYRICS） ===");

        var sample = Path.Combine(sampleDir, "flac_44k_440Hz.flac");
        if (!File.Exists(sample))
        {
            Skip("内嵌标签歌词全部断言", "找不到 format-test 样本（publish/format-test/hires/flac_44k_440Hz.flac）");
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
                Skip("内嵌优先级断言（LRC/内嵌/自动 3 项）", "找不到 format-test 样本");
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

    /// <summary>环境不具备条件时显式记账跳过（审计：跳过项要打印数量与原因，结尾汇总）。</summary>
    private static void Skip(string what, string reason)
    {
        _skipped++;
        Console.WriteLine($"  ⏭ 跳过  {what}（原因：{reason}）");
    }

    // ================= GD 源真实链路探测（P4-2；联网，非主线断言） =================

    private static async Task RunGdProbeAsync()
    {
        Console.WriteLine();
        Console.WriteLine("=== GD 源真实链路（P4-2 打样模型验证，需联网） ===");

        using var gd = new GdSource();
        await gd.ProbeAsync(CancellationToken.None);
        Console.WriteLine($"可用子源：{string.Join(", ", gd.AvailableSubSources)}");

        // 搜索
        var search = await gd.SearchAsync("晴天", limit: 5, page: 1, CancellationToken.None);
        Console.WriteLine($"搜索「晴天」：success={search.Success} count={search.Data?.Count ?? 0} err={search.Error}");
        if (search.Data is { Count: > 0 })
        {
            var t = search.Data[0];
            Console.WriteLine($"  第 1 条：id={t.Id} name={t.Name} artist={t.ArtistLine} album={t.Album} source={t.Source}");

            // 取流（条目自带 source）
            var stream = await gd.GetStreamAsync(t, preferredBr: 999, CancellationToken.None);
            Console.WriteLine($"  取流 br999：success={stream.Success} actualBr={stream.Data?.ActualBr} size={stream.Data?.SizeBytes} err={stream.Error}");
            if (stream.Success)
            {
                Console.WriteLine($"    直链前 80 字符：{stream.Data!.Url[..Math.Min(80, stream.Data.Url.Length)]}");
                var down = await TryHeadAsync(stream.Data.Url);
                Console.WriteLine($"    直链 HEAD：{down}");
            }

            // 降级验证：999 失败时试 320
            if (!stream.Success)
            {
                var s320 = await gd.GetStreamAsync(t, preferredBr: 320, CancellationToken.None);
                Console.WriteLine($"  降级 br320：success={s320.Success} actualBr={s320.Data?.ActualBr} err={s320.Error}");
            }

            // 歌词
            var lyric = await gd.GetLyricAsync(t, CancellationToken.None);
            Console.WriteLine($"  歌词：success={lyric.Success} len={lyric.Data?.Lrc?.Length ?? 0} 翻译={lyric.Data?.Translation is { Length: > 0 }} err={lyric.Error}");

            // 封面
            var pic = await gd.GetPicUrlAsync(t, 300, CancellationToken.None);
            Console.WriteLine($"  封面：success={pic.Success} url={pic.Data} err={pic.Error}");
        }
        else
        {
            Console.WriteLine("  搜索为空（子源全部不可用？）——真实链路验证跳过");
            Skip("GD 真实链路（搜索/取流/歌词/封面）", "所有子源搜索为空或网络不可达");
        }

        // 专辑
        var album = await gd.SearchAlbumAsync("叶惠美", limit: 5, page: 1, CancellationToken.None);
        Console.WriteLine($"专辑拉取「叶惠美」：success={album.Success} count={album.Data?.Count ?? 0} err={album.Error}");

        Console.WriteLine();
    }

    // ================= L3.1 个性化配置往返（JSON 序列化→反序列化） =================

    private static void RunL31ConfigRoundtrip()
    {
        Console.WriteLine();
        Console.WriteLine("=== L3.1 个性化配置往返 ===");

        var ui = ConfigService.Current.Ui;
        var backup = new Player.Core.Infra.UiConfig
        {
            RowHeight = ui.RowHeight,
            GroupsExpandedByDefault = ui.GroupsExpandedByDefault,
            GroupCoverVisible = ui.GroupCoverVisible,
            UiFontFamily = ui.UiFontFamily,
            UiFontScale = ui.UiFontScale,
            CustomAccent = ui.CustomAccent,
            SelectedOpacity = ui.SelectedOpacity,
            HoverOpacity = ui.HoverOpacity,
            Columns = new List<string>(ui.Columns),
            ColumnWidths = new Dictionary<string, double>(ui.ColumnWidths)
        };

        try
        {
            ui.RowHeight = 110;
            ui.GroupsExpandedByDefault = false;
            ui.GroupCoverVisible = false;
            ui.UiFontFamily = "Segoe UI";
            ui.UiFontScale = 1.15;
            ui.CustomAccent = "#E91E63";
            ui.SelectedOpacity = 0.22;
            ui.HoverOpacity = 0.05;
            ui.Columns = new List<string> { "Title", "Duration", "Format" };
            ui.ColumnWidths["Title"] = 430;
            ConfigService.Save();

            var json = System.IO.File.ReadAllText(Player.Core.Infra.AppPaths.ConfigFile);
            var reloaded = System.Text.Json.JsonSerializer.Deserialize<Player.Core.Infra.AppConfig>(json,
                new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
                    PropertyNameCaseInsensitive = true
                });

            Check("行高往返", reloaded?.Ui.RowHeight == 110);
            Check("分组默认折叠往返", reloaded?.Ui.GroupsExpandedByDefault == false);
            Check("组头封面开关往返", reloaded?.Ui.GroupCoverVisible == false);
            Check("界面字体往返", reloaded?.Ui.UiFontFamily == "Segoe UI");
            Check("字号缩放往返", reloaded is not null && Math.Abs(reloaded.Ui.UiFontScale - 1.15) < 0.001);
            Check("自定义强调色往返", reloaded?.Ui.CustomAccent == "#E91E63");
            Check("选中透明度往返", reloaded is not null && Math.Abs(reloaded.Ui.SelectedOpacity - 0.22) < 0.001);
            Check("悬停透明度往返", reloaded is not null && Math.Abs(reloaded.Ui.HoverOpacity - 0.05) < 0.001);
            Check("列顺序往返", reloaded is not null && string.Join(",", reloaded.Ui.Columns) == "Title,Duration,Format");
            Check("列宽往返", reloaded is not null && reloaded.Ui.ColumnWidths.TryGetValue("Title", out var w) && Math.Abs(w - 430) < 0.001);
        }
        finally
        {
            ui.RowHeight = backup.RowHeight;
            ui.GroupsExpandedByDefault = backup.GroupsExpandedByDefault;
            ui.GroupCoverVisible = backup.GroupCoverVisible;
            ui.UiFontFamily = backup.UiFontFamily;
            ui.UiFontScale = backup.UiFontScale;
            ui.CustomAccent = backup.CustomAccent;
            ui.SelectedOpacity = backup.SelectedOpacity;
            ui.HoverOpacity = backup.HoverOpacity;
            ui.Columns = backup.Columns;
            ui.ColumnWidths = backup.ColumnWidths;
            ConfigService.Save();
        }
    }

    // ================= 下载命名模板（P4-5） =================

    private static void RunDownloadTemplateChecks()
    {
        Console.WriteLine();
        Console.WriteLine("=== 下载命名模板（DownloadTemplater） ===");

        var values = new Dictionary<string, string>
        {
            ["AlbumArtist"] = "周杰伦",
            ["Album"] = "叶惠美",
            ["TrackNo"] = "01",
            ["Title"] = "晴天: 完整版?"
        };
        var path = DownloadTemplater.Render("{AlbumArtist}/{Album}/{TrackNo} - {Title}", values);
        Check("模板渲染替换占位符", path == "周杰伦/叶惠美/01 - 晴天_ 完整版_");
        Check("未知占位符原样保留", DownloadTemplater.Render("x {Unknown} y", values).Contains("{Unknown}"));
        Check("空 TrackNo 时去掉 - 段", DownloadTemplater.Render(
            "{AlbumArtist}/{Album}/{TrackNo} - {Title}",
            new Dictionary<string, string> { ["AlbumArtist"] = "YOASOBI", ["Album"] = "夜に駆ける", ["TrackNo"] = "", ["Title"] = "夜に駆ける" })
            == "YOASOBI/夜に駆ける/夜に駆ける");

        var sanitized = DownloadTemplater.SanitizeComponent("a/b:c*d?" + (char)34 + "f<g>h|i");
        Console.WriteLine($"  debug sanitized='{sanitized}'");
        Check("非法文件名字符替换为下划线", sanitized == "a_b_c_d__f_g_h_i");   // ? 与 " 各一个下划线
        Console.WriteLine($"  debug CON.='{DownloadTemplater.SanitizeComponent("CON.")}'");
        Check("保留名与尾点处理", DownloadTemplater.SanitizeComponent("CON.") == "CON_");
        Console.WriteLine($"  debug blank='{DownloadTemplater.SanitizeComponent("   ")}'");
        Check("空白组件回退下划线", DownloadTemplater.SanitizeComponent("   ") == "_");

        Check("扩展名从 URL 推断", DownloadTemplater.ExtensionFromUrl("https://x.com/a/b.flac?v=1") == ".flac");
        Check("未知扩展名回退 bin", DownloadTemplater.ExtensionFromUrl("https://x.com/a") == ".bin");
        Check("URL 带路径保留最后扩展", DownloadTemplater.ExtensionFromUrl("http://host/abc.MP3?k=1") == ".mp3");

        Console.WriteLine();
    }

    /// <summary>下载全链路探测（P4-5；需联网；GD 零 Key）。</summary>
    private static async Task RunDownloadProbeAsync()
    {
        Console.WriteLine();
        Console.WriteLine("=== 下载全链路探测（P4-5） ===");

        Db.Initialize();
        var lib = new LibraryService();
        using var sources = new OnlineSources(new ChkszClient());
        using var dl = new DownloadService(sources, lib);

        var dir = Path.Combine(Path.GetTempPath(), "player-dl-probe");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        ConfigService.Current.Online.DownloadDir = dir;

        var gd = (GdSource)sources.Default;
        var search = await gd.SearchAsync("夜に駆ける", limit: 5, page: 1, CancellationToken.None);
        var track = search.Data?.FirstOrDefault();
        if (track is null)
        {
            Console.WriteLine("搜索为空，跳过");
            Skip("下载全链路（取流/落盘/标签/lrc）", "GD 搜索为空或网络不可达");
            return;
        }

        Console.WriteLine($"目标曲目：{track.Name} / {track.ArtistLine}（{track.Source}）");
        var item = dl.Enqueue(track, gd.Key, 999);

        for (var i = 0; i < 120 && !item.IsDone; i++) await Task.Delay(1000);

        Console.WriteLine($"状态={item.Status} 错误={item.Error} 实际音质={item.ActualBr}");
        if (item.TargetPath is not null && File.Exists(item.TargetPath))
        {
            var info = new FileInfo(item.TargetPath);
            Console.WriteLine($"文件：{item.TargetPath}（{info.Length} bytes）");
            var lrc = Path.ChangeExtension(item.TargetPath, ".lrc");
            Console.WriteLine($"lrc 存在：{File.Exists(lrc)}（{new FileInfo(lrc).Length} bytes）");
            using var tf = TagLib.File.Create(item.TargetPath);
            Console.WriteLine($"标签：标题={tf.Tag.Title} 歌手={string.Join("/", tf.Tag.Performers)} 专辑={tf.Tag.Album} 封面={tf.Tag.Pictures.Length} 张");
            Check("下载完成且文件存在", item.Status == DownloadStatus.Completed && File.Exists(item.TargetPath));
            Check("标签标题正确", tf.Tag.Title == track.Name);
            Check("有歌词时 lrc 落盘", !File.Exists(lrc) || new FileInfo(lrc).Length > 0);
        }
        else
        {
            Check("下载完成且文件存在", false);
        }

        // 重复检测：刚下载未入库的曲目再入队 → 不误拦（Duplicate 只对库内已有曲目）
        var dup = dl.Enqueue(track, gd.Key, 999);
        Check("未入库曲目再入队不误标记重复", dup.Status == DownloadStatus.Queued);

        try { Directory.Delete(dir, true); } catch { /* 忽略 */ }
        Console.WriteLine();
    }

    /// <summary>模拟断网：网络层失败必须降级为 OnlineResult.Fail，绝不能抛异常（P4 验收：断网本地零影响）。</summary>
    private static void RunNetFailChecks()
    {
        Console.WriteLine();
        Console.WriteLine("=== 网络失败降级（断网模拟） ===");

        var failing = new FailingHandler();
        using var gd = new GdSource(failing);
        var search = gd.SearchAsync("test", 5, 1, CancellationToken.None).GetAwaiter().GetResult();
        Check("搜索网络失败 → Fail 不抛异常", !search.Success);
        Check("失败带可读原因", !string.IsNullOrWhiteSpace(search.Error));

        var track = new OnlineTrack("1", "test", new[] { "a" }, "album", "", "1", "netease");
        var stream = gd.GetStreamAsync(track, 999, CancellationToken.None).GetAwaiter().GetResult();
        Check("取流网络失败 → Fail 不抛异常", !stream.Success);

        var lyric = gd.GetLyricByNeteaseIdAsync(1, CancellationToken.None).GetAwaiter().GetResult();
        Check("GD 歌词网络失败 → Fail 不抛异常", !lyric.Success);

        Console.WriteLine();
    }

    /// <summary>立即抛网络异常的 handler（模拟断网）。</summary>
    private sealed class FailingHandler : System.Net.Http.HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            System.Net.Http.HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new System.Net.Http.HttpRequestException("模拟断网：网络不可达");
    }

    /// <summary>BASS URL 流 flags 诊断（P4-4；需联网 + BASS 初始化）。</summary>
    private static void RunUrlProbe(string url)
    {
        Console.WriteLine();
        Console.WriteLine("=== BASS URL 流 flags 诊断（P4-4） ===");

        BassRuntime.Initialize();

        // 疑似根因：CDN 拒默认 UA（curl 打样都带 Mozilla）。先配 NetAgent 再试
        var uaPtr = System.Runtime.InteropServices.Marshal.StringToHGlobalUni(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        ManagedBass.Bass.Configure(ManagedBass.Configuration.NetAgent, uaPtr);
        System.Runtime.InteropServices.Marshal.FreeHGlobal(uaPtr);
        Console.WriteLine("NetAgent 已设为 Mozilla UA");

        var combos = new (string Name, ManagedBass.BassFlags Flags)[]
        {
            ("Decode|Float", ManagedBass.BassFlags.Decode | ManagedBass.BassFlags.Float),
            ("Decode|Float|StreamStatus", ManagedBass.BassFlags.Decode | ManagedBass.BassFlags.Float | ManagedBass.BassFlags.StreamStatus),
            ("StreamStatus", ManagedBass.BassFlags.StreamStatus),
            ("无 flags", 0),
            ("Decode|Float|AsyncFile", ManagedBass.BassFlags.Decode | ManagedBass.BassFlags.Float | ManagedBass.BassFlags.AsyncFile)
        };

        foreach (var (name, flags) in combos)
        {
            // 5 参重载（带 DownloadProcedure）才是 URL 流；4 参是本地文件
            var h = ManagedBass.Bass.CreateStream(url, 0, flags, null, IntPtr.Zero);
            Console.WriteLine($"{name}: handle={h} error={ManagedBass.Bass.LastError}");
            if (h != 0) ManagedBass.Bass.StreamFree(h);
        }

        Console.WriteLine();
    }

    private static async Task<string> TryHeadAsync(string url)
    {
        try
        {
            using var client = new HttpClient();
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
            using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            return $"HTTP {(int)resp.StatusCode}";
        }
        catch (Exception ex)
        {
            return "失败：" + ex.Message;
        }
    }

    // ================= 专辑分组（UI-R2） =================

    private static void RunGroupingChecks()
    {
        Console.WriteLine();
        Console.WriteLine("=== 专辑分组（TrackGrouper） ===");

        static TrackRecord T(string path, string title, string artist, string album,
            string albumArtist = "", int disc = 0, int track = 0, int year = 0) => new()
        {
            Path = path,
            Title = title,
            Artist = artist,
            Album = album,
            AlbumArtist = albumArtist,
            DiscNo = disc,
            TrackNo = track,
            Year = year
        };

        // ① 分组键 = 专辑 + 专辑艺术家：同名专辑不同艺术家要分开
        var tracks = new[]
        {
            T(@"C:\a\01.flac", "曲1", "乐队A", "同名专辑", "乐队A", 1, 1, 2020),
            T(@"C:\a\02.flac", "曲2", "乐队A", "同名专辑", "乐队A", 1, 2, 2020),
            T(@"C:\b\01.flac", "曲1", "乐队B", "同名专辑", "乐队B", 1, 1, 2019),
            T(@"C:\c\01.flac", "散曲1", "歌手C", ""),
            T(@"C:\c\02.flac", "散曲2", "歌手C", ""),
            T(@"C:\d\01.flac", "散曲3", "歌手D", ""),
            T(@"C:\e\01.flac", "跨碟1", "乐队A", "多碟专辑", "乐队A", 1, 1, 2021),
            T(@"C:\e\02.flac", "跨碟2", "乐队A", "多碟专辑", "乐队A", 2, 1, 2021),
            T(@"C:\e\03.flac", "跨碟3", "乐队A", "多碟专辑", "乐队A", 2, 3, 2021),
            T(@"C:\f\01.flac", "无号1", "乐队A", "多碟专辑", "乐队A"),
            T(@"C:\f\02.flac", "无号2", "乐队A", "多碟专辑", "乐队A"),
            T(@"C:\g\01.flac", "艺术家兜底", "独唱者", "个人专辑")
        };
        var groups = TrackGrouper.Group(tracks);

        Check("同名专辑不同艺术家分成两组", groups.Count(g => g.Album == "同名专辑") == 2);
        Check("散曲归「单曲 | 艺术家」组", groups.Any(g => g.Album == "单曲" && g.Artist == "歌手C" && g.Tracks.Count == 2));
        Check("单曲组按艺术家独立", groups.Any(g => g.Album == "单曲" && g.Artist == "歌手D" && g.Tracks.Count == 1));

        var multi = groups.First(g => g.Album == "多碟专辑");
        Check("组内按 碟号→曲号 排序", multi.Tracks.Select(t => t.Title).SequenceEqual(
            new[] { "跨碟1", "跨碟2", "跨碟3", "无号1", "无号2" }));
        Check("组年份取组内最早年份", multi.Year == "2021");
        Check("同名专辑组年份正确", groups.First(g => g.Album == "同名专辑" && g.Artist == "乐队A").Year == "2020");
        Check("无年份的组年份为空", groups.First(g => g.Album == "个人专辑").Year == string.Empty);
        Check("专辑艺术家缺失时退回曲目艺术家", groups.Any(g => g.Album == "个人专辑" && g.Artist == "独唱者"));

        // ② 组排序：艺术家 → 专辑名
        var orderedArtists = groups.Select(g => g.Artist).ToList();
        Check("组按艺术家排序（乐队A 在 乐队B 前）",
            orderedArtists.IndexOf("乐队A") < orderedArtists.IndexOf("乐队B"));
        // 排序固定 OrdinalIgnoreCase（审计：任何环境断言必须过）——
        // 码位序："多"(U+591A) > "同"(U+540C)，故同名专辑在前（拼音序只在 zh-CN 文化下成立，不可依赖）
        Check("组排序稳定（同一艺术家按专辑名，Ordinal 序）",
            groups.Where(g => g.Artist == "乐队A").Select(g => g.Album).SequenceEqual(new[] { "同名专辑", "多碟专辑" }));

        // ③ 空输入与退化
        Check("空输入返回空列表", TrackGrouper.Group(Array.Empty<TrackRecord>()).Count == 0);
        var noMeta = new[] { T(@"C:\x\01.flac", "无标签", "", "") };
        var noMetaGroups = TrackGrouper.Group(noMeta);
        Check("全空标签归入单曲组（未知艺术家）",
            noMetaGroups.Count == 1 && noMetaGroups[0].Album == "单曲" && noMetaGroups[0].Artist == "未知艺术家");

        // ④ 输入顺序不影响组内顺序（组内始终按碟/曲号重排）
        var shuffled = new[]
        {
            T(@"C:\z\03.flac", "三", "乐队A", "排序专辑", "乐队A", 1, 3, 2000),
            T(@"C:\z\01.flac", "一", "乐队A", "排序专辑", "乐队A", 1, 1, 2000),
            T(@"C:\z\02.flac", "二", "乐队A", "排序专辑", "乐队A", 1, 2, 2000)
        };
        Check("组内顺序与输入顺序无关（按曲号重排）",
            TrackGrouper.Group(shuffled)[0].Tracks.Select(t => t.Title).SequenceEqual(new[] { "一", "二", "三" }));
    }

    // ================= 封面取色主题引擎（UI-R3） =================

    private static void RunThemeChecks()
    {
        Console.WriteLine();
        Console.WriteLine("=== 封面取色与主题派生（ThemeEngine） ===");

        // ---- 取色 ----
        var solidRed = Enumerable.Repeat(new RgbColor(0xD0, 0x20, 0x18), 1024).ToList();
        Check("纯色封面取主色 = 该色",
            CoverColorExtractor.ExtractDominant(solidRed) == new RgbColor(0xD0, 0x20, 0x18));

        var mixed = new List<RgbColor>(1024);
        for (var i = 0; i < 1024; i++) mixed.Add(i % 4 == 0 ? new RgbColor(0x10, 0x30, 0x90) : new RgbColor(0xE0, 0x40, 0x30));
        var dom = CoverColorExtractor.ExtractDominant(mixed);
        Check("红蓝混合封面主色偏红（多数桶）", dom.R > dom.B && dom.R > 0xC0);

        var accent = CoverColorExtractor.ExtractAccent(mixed);
        Check("强调色取高饱和桶", ThemeDeriver.Hsl(accent).S > 0.5);

        var darkCover = Enumerable.Repeat(new RgbColor(0x18, 0x1A, 0x1C), 1024).ToList();
        Check("暗色封面可提取", CoverColorExtractor.ExtractDominant(darkCover) == new RgbColor(0x18, 0x1A, 0x1C));

        Check("空输入取色有兜底", CoverColorExtractor.ExtractDominant(Array.Empty<RgbColor>()) == new RgbColor(0x40, 0x40, 0x40));

        // ---- 派生与对比度保底 ----
        Check("纯红封面 → 浅 tint 背景（亮度 ≥ 0.78）",
            ThemeDeriver.RelativeLuminance(ThemeDeriver.Derive(new RgbColor(0xD0, 0x20, 0x18)).Background) >= 0.78);
        Check("纯蓝封面 → 浅 tint 背景（亮度 ≥ 0.78）",
            ThemeDeriver.RelativeLuminance(ThemeDeriver.Derive(new RgbColor(0x20, 0x50, 0xD0)).Background) >= 0.78);
        Check("黄色封面 → 浅 tint 背景（亮度 ≥ 0.78）",
            ThemeDeriver.RelativeLuminance(ThemeDeriver.Derive(new RgbColor(0xE0, 0xC0, 0x20)).Background) >= 0.78);

        // 对比度断言：一组代表性输入（亮/中/暗/灰）
        RgbColor[] inputs =
        {
            new(0xD0, 0x20, 0x18), new(0x20, 0x50, 0xD0), new(0x2E, 0xA0, 0x3A),
            new(0xE0, 0xC0, 0x20), new(0xC0, 0x50, 0xA0), new(0x70, 0x80, 0x90),
            new(0x10, 0x10, 0x10), new(0xF0, 0xF0, 0xF0), new(0x88, 0x88, 0x88),
            new(0x20, 0x20, 0x20), new(0x40, 0x60, 0x80)
        };
        var allContrastsOk = true;
        foreach (var input in inputs)
        {
            var palette = ThemeDeriver.Derive(input);
            var bg = palette.Background;
            if (ThemeDeriver.ContrastRatio(palette.TextPrimary, bg) < ThemeDeriver.MinTextPrimaryContrast) allContrastsOk = false;
            if (ThemeDeriver.ContrastRatio(palette.TextSecondary, bg) < ThemeDeriver.MinTextSecondaryContrast) allContrastsOk = false;
            if (ThemeDeriver.ContrastRatio(palette.TextTertiary, bg) < ThemeDeriver.MinTextTertiaryContrast) allContrastsOk = false;
            if (ThemeDeriver.ContrastRatio(palette.Accent, bg) < ThemeDeriver.MinAccentContrast) allContrastsOk = false;
        }
        Check($"对比度保底（{inputs.Length} 组输入：主/次/三级文字 ≥ 7/4.5/3、强调色 ≥ 3）", allContrastsOk);

        // 过暗/过灰 → 回退中性浅灰
        var darkPalette = ThemeDeriver.Derive(new RgbColor(0x10, 0x10, 0x10));
        Check("过暗封面回退中性浅灰（背景为浅色）",
            ThemeDeriver.RelativeLuminance(darkPalette.Background) >= 0.78);
        Check("过灰封面回退中性浅灰（背景为浅色）",
            ThemeDeriver.RelativeLuminance(ThemeDeriver.Derive(new RgbColor(0x88, 0x88, 0x88)).Background) >= 0.78);

        // 强调色饱和度：彩色输入下强调色应保持高饱和
        var redAccent = ThemeDeriver.Derive(new RgbColor(0xD0, 0x20, 0x18)).Accent;
        Check("强调色高饱和（红色输入）", ThemeDeriver.Hsl(redAccent).S > 0.7);

        // 中性回退与固定深色均为有效调色板
        Check("固定深色调色板存在", ThemePalette.FixedDark.Background == new RgbColor(0x20, 0x20, 0x20));
        Check("中性回退与固定深色不同", ThemeDeriver.NeutralFallback().Background != ThemePalette.FixedDark.Background);

        // ---- 深色基底派生（UI-R3 反馈：深色/浅色分别适配） ----
        var allDarkContrastsOk = true;
        foreach (var input in inputs)
        {
            var palette = ThemeDeriver.DeriveDark(input);
            var bg = palette.Background;
            if (ThemeDeriver.RelativeLuminance(bg) > 0.14) allDarkContrastsOk = false;
            if (ThemeDeriver.ContrastRatio(palette.TextPrimary, bg) < ThemeDeriver.MinTextPrimaryContrast) allDarkContrastsOk = false;
            if (ThemeDeriver.ContrastRatio(palette.TextSecondary, bg) < ThemeDeriver.MinTextSecondaryContrast) allDarkContrastsOk = false;
            if (ThemeDeriver.ContrastRatio(palette.TextTertiary, bg) < ThemeDeriver.MinTextTertiaryContrast) allDarkContrastsOk = false;
            if (ThemeDeriver.ContrastRatio(palette.Accent, bg) < ThemeDeriver.MinAccentContrast) allDarkContrastsOk = false;
        }
        Check($"深色基底对比度保底（{inputs.Length} 组输入：背景亮度≤0.14、文字≥7/4.5/3、强调色≥3）", allDarkContrastsOk);
        Check("深色基底染红色 → 深红背景（带色相）",
            ThemeDeriver.Hsl(ThemeDeriver.DeriveDark(new RgbColor(0xD0, 0x20, 0x18)).Background).H < 0.1);
        Check("过暗封面深色派生回退固定深色",
            ThemeDeriver.DeriveDark(new RgbColor(0x10, 0x10, 0x10)) == ThemePalette.FixedDark);

        // ---- 音量格语义色（L1.1-①：已到达=前景强调，未到达=弱化，两挡主题都成立） ----
        var lightVol = ThemeDeriver.Derive(new RgbColor(0xD0, 0x20, 0x18));
        Check("浅色：已到达格 = 前景文字色（深/醒目）", lightVol.VolumeReached == lightVol.TextPrimary);
        Check("浅色：未到达格明显浅于已到达格（弱化）",
            ThemeDeriver.RelativeLuminance(lightVol.VolumeSlot) > ThemeDeriver.RelativeLuminance(lightVol.VolumeReached));
        var darkVol = ThemeDeriver.DeriveDark(new RgbColor(0xD0, 0x20, 0x18));
        Check("深色：已到达格 = 前景文字色（亮/醒目）", darkVol.VolumeReached == darkVol.TextPrimary);
        Check("深色：未到达格明显暗于已到达格（弱化）",
            ThemeDeriver.RelativeLuminance(darkVol.VolumeSlot) < ThemeDeriver.RelativeLuminance(darkVol.VolumeReached));
        Check("固定深色：已到达=白、未到达=深灰",
            ThemePalette.FixedDark.VolumeReached == new RgbColor(0xFF, 0xFF, 0xFF)
            && ThemePalette.FixedDark.VolumeSlot == new RgbColor(0x3A, 0x3A, 0x3A));

        Console.WriteLine();
    }

    // ================= 应用内快捷键策略（L2） =================

    private static void RunShortcutChecks()
    {
        Console.WriteLine();
        Console.WriteLine("=== 快捷键响应策略（ShortcutPolicy） ===");

        var keys = Enum.GetValues<ShortcutKey>();
        var allKeys = keys.ToArray();

        // ---- 文本输入聚焦：一律不响应（L2 约束②核心规则） ----
        var textInputBlocked = allKeys.All(k => !ShortcutPolicy.ShouldHandle(FocusKind.TextInput, k));
        Check("文本输入框聚焦：全部快捷键不响应", textInputBlocked);
        Check("下拉框聚焦：全部快捷键不响应", allKeys.All(k => !ShortcutPolicy.ShouldHandle(FocusKind.ComboBox, k)));

        // ---- 按钮聚焦：Space 归按钮 ----
        Check("按钮聚焦：Space 不抢（归按钮激活）", !ShortcutPolicy.ShouldHandle(FocusKind.ButtonBase, ShortcutKey.Space));
        Check("按钮聚焦：F5 重扫仍响应", ShortcutPolicy.ShouldHandle(FocusKind.ButtonBase, ShortcutKey.Rescan));
        Check("按钮聚焦：Ctrl+F 仍响应", ShortcutPolicy.ShouldHandle(FocusKind.ButtonBase, ShortcutKey.FocusSearch));

        // ---- 滑条聚焦：方向键归滑条 ----
        Check("滑条聚焦：←/→ 不抢（归滑条）", !ShortcutPolicy.ShouldHandle(FocusKind.Slider, ShortcutKey.SeekBack)
              && !ShortcutPolicy.ShouldHandle(FocusKind.Slider, ShortcutKey.SeekForward));
        Check("滑条聚焦：Ctrl+←/→ 不抢", !ShortcutPolicy.ShouldHandle(FocusKind.Slider, ShortcutKey.PrevTrack)
              && !ShortcutPolicy.ShouldHandle(FocusKind.Slider, ShortcutKey.NextTrack));
        Check("滑条聚焦：Space 仍响应", ShortcutPolicy.ShouldHandle(FocusKind.Slider, ShortcutKey.Space));

        // ---- 列表聚焦：Enter/Delete 生效 ----
        Check("列表聚焦：Enter 播放选中", ShortcutPolicy.ShouldHandle(FocusKind.ListBox, ShortcutKey.Enter));
        Check("列表聚焦：Delete 歌单移除", ShortcutPolicy.ShouldHandle(FocusKind.ListBox, ShortcutKey.Delete));
        Check("列表聚焦：Space 仍响应", ShortcutPolicy.ShouldHandle(FocusKind.ListBox, ShortcutKey.Space));

        // ---- 普通区域：全部响应 ----
        Check("无焦点/普通区：全部快捷键响应", allKeys.All(k => ShortcutPolicy.ShouldHandle(FocusKind.None, k)));
        Check("无焦点/普通区：Space 响应", ShortcutPolicy.ShouldHandle(FocusKind.None, ShortcutKey.Space));

        // ================= 快捷键映射（ShortcutMap，L2 自定义改绑） =================
        Console.WriteLine();
        Console.WriteLine("=== 快捷键映射（ShortcutMap 改绑） ===");

        var map = new ShortcutMap();
        Check("默认 Space → 播放暂停", map.TryResolve("Space", ModifierMask.None, FocusKind.None, out var a) && a == ShortcutKey.Space);
        Check("默认 Ctrl+Left → 上一曲", map.TryResolve("Left", ModifierMask.Ctrl, FocusKind.None, out a) && a == ShortcutKey.PrevTrack);
        Check("默认 Ctrl+F → 聚焦搜索", map.TryResolve("F", ModifierMask.Ctrl, FocusKind.None, out a) && a == ShortcutKey.FocusSearch);
        Check("默认 F5 → 重扫", map.TryResolve("F5", ModifierMask.None, FocusKind.None, out a) && a == ShortcutKey.Rescan);
        Check("未绑定的组合不命中", !map.TryResolve("X", ModifierMask.Ctrl, FocusKind.None, out _));
        Check("Enter 规范化（WPF Key.Enter 是 Return）", map.TryResolve("Return", ModifierMask.None, FocusKind.None, out a) && a == ShortcutKey.Enter);

        // 改绑
        var overridden = new Dictionary<string, string> { ["NextTrack"] = "Ctrl+Right", ["Rescan"] = "F6" };
        var map2 = new ShortcutMap(overridden);
        Check("改绑后 Ctrl+Right → 下一曲", map2.TryResolve("Right", ModifierMask.Ctrl, FocusKind.None, out a) && a == ShortcutKey.NextTrack);
        Check("改绑后 Ctrl+Left 仍是上一曲", map2.TryResolve("Left", ModifierMask.Ctrl, FocusKind.None, out a) && a == ShortcutKey.PrevTrack);
        Check("改绑后 F6 → 重扫", map2.TryResolve("F6", ModifierMask.None, FocusKind.None, out a) && a == ShortcutKey.Rescan);
        Check("未改绑的默认仍在", map2.TryResolve("Space", ModifierMask.None, FocusKind.None, out a) && a == ShortcutKey.Space);

        // 覆盖校验：非法组合/冲突/字母键无修饰 → 回退默认
        var bad = new Dictionary<string, string>
        {
            ["NextTrack"] = "??bad??",
            ["Locate"] = "Ctrl+L",
            ["Rescan"] = "Q",
        };
        var map3 = new ShortcutMap(bad);
        Check("非法组合回退默认", map3.GetCombo(ShortcutKey.NextTrack) == "Ctrl+Right");
        Check("冲突组合回退默认", map3.GetCombo(ShortcutKey.Locate) == "Ctrl+L");
        Check("字母键无修饰回退默认", map3.GetCombo(ShortcutKey.Rescan) == "F5");
        Check("冲突的默认组合不受影响", map3.TryResolve("F", ModifierMask.Ctrl, FocusKind.None, out a) && a == ShortcutKey.FocusSearch);

        // 解析/格式化往返
        Check("Format 顺序 Ctrl+Shift+Alt", ShortcutMap.Format(ModifierMask.Ctrl | ModifierMask.Alt | ModifierMask.Shift, "K") == "Ctrl+Shift+Alt+K");
        Check("Parse 往返一致", ShortcutMap.TryParse("Ctrl+Shift+Alt+K", out var mods, out var key) && mods == (ModifierMask.Ctrl | ModifierMask.Shift | ModifierMask.Alt) && key == "K");
        Check("Parse 拒绝未知修饰", !ShortcutMap.TryParse("Super+K", out _, out _));
        Check("Parse 拒绝非法键", !ShortcutMap.TryParse("Ctrl+Banana", out _, out _));

        // 焦点规则在改绑后依然生效（文本输入/按钮 Space）
        var map4 = new ShortcutMap(new Dictionary<string, string> { ["Space"] = "Ctrl+Space" });
        Check("改绑后 Space 仍归按钮（文本输入不响应）", !map4.TryResolve("Space", ModifierMask.Ctrl, FocusKind.TextInput, out _)
              && !ShortcutPolicy.ShouldHandle(FocusKind.ButtonBase, ShortcutKey.Space));
        Check("改绑后 Ctrl+Space 命中播放暂停", map4.TryResolve("Space", ModifierMask.Ctrl, FocusKind.None, out a) && a == ShortcutKey.Space);

        Console.WriteLine();
    }

    // ================= L3.2 频谱 DSP 探针（进主工程前验证：mixer 挂 DSP 复制样本不抢播放数据） =================

    /// <summary>探针：生成正弦波 → mixer → 挂 DSP 回调计数 → 播放 1.5 秒 → 验证 DSP 收到样本且播放未中断。</summary>
    private static void RunDspProbe()
    {
        Console.WriteLine("=== L3.2 频谱 DSP 探针 ===");

        Player.Core.Audio.BassRuntime.Initialize();
        try
        {
            const int rate = 44100;
            var phase = 0.0;

            // push 模式流：BASS 需要数据时回调往 buffer 里填（写数据进给定缓冲）
            var wave = new ManagedBass.StreamProcedure((_, buffer, length, _) =>
            {
                var count = length / 4;
                var buf = new float[count];
                for (var i = 0; i < count; i++)
                {
                    buf[i] = (float)Math.Sin(2 * Math.PI * 440 * phase / rate) * 0.3f;
                    phase += 1;
                }
                System.Runtime.InteropServices.Marshal.Copy(buf, 0, buffer, count);
                return length;
            });

            var source = ManagedBass.Bass.CreateStream(rate, 1, ManagedBass.BassFlags.Decode, wave, IntPtr.Zero);
            if (source == 0) { Console.WriteLine($"创建源流失败：{ManagedBass.Bass.LastError}"); return; }

            var mixer = ManagedBass.Mix.BassMix.CreateMixerStream(rate, 1, ManagedBass.BassFlags.Default);
            if (mixer == 0) { Console.WriteLine($"创建 mixer 失败：{ManagedBass.Bass.LastError}"); return; }
            if (!ManagedBass.Mix.BassMix.MixerAddChannel(mixer, source, 0))
            { Console.WriteLine($"挂源失败：{ManagedBass.Bass.LastError}"); return; }

            long dspSamples = 0;
            ManagedBass.DSPProcedure dsp = (_, _, _, len, _) =>
            {
                // 只复制不修改：DSP 回调直接拿到混音输出样本指针，不用 ChannelGetData，不消费播放数据
                Interlocked.Add(ref dspSamples, len / 4);
            };
            if (ManagedBass.Bass.ChannelSetDSP(mixer, dsp, IntPtr.Zero, 0) == 0)
            { Console.WriteLine($"挂 DSP 失败：{ManagedBass.Bass.LastError}"); return; }

            if (!ManagedBass.Bass.ChannelPlay(mixer, false))
            { Console.WriteLine($"播放失败：{ManagedBass.Bass.LastError}"); return; }

            Thread.Sleep(1500);

            var active = ManagedBass.Bass.ChannelIsActive(mixer);
            Console.WriteLine($"DSP 收到样本：{dspSamples}（期望 >{rate}）");
            Console.WriteLine($"播放状态：{active}（期望 Playing）");
            Check("DSP 收到样本", dspSamples > rate / 2);
            Check("播放未中断", active == ManagedBass.PlaybackState.Playing);

            ManagedBass.Bass.StreamFree(mixer);
            ManagedBass.Bass.StreamFree(source);
        }
        finally
        {
            Player.Core.Audio.BassRuntime.Shutdown();
        }
    }
}