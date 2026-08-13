using Player.Core.Audio;
using Player.Core.Infra;
using Player.Core.Library;

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

            default:
                Console.WriteLine($"未知模式：{mode}（可用：seamless / library）");
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

    private static void Check(string what, bool ok)
    {
        if (ok) _passed++; else _failed++;
        Console.WriteLine($"  {(ok ? "✓" : "✗ 失败")}  {what}");
    }
}
