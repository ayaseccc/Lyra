using System.Text.Json;
using System.Text.Json.Serialization;
using Serilog;

namespace Player.Core.Infra;

/// <summary>data/config.json 的结构（PLAN 第 9 节）。apiKey 到 P3 才用，但字段先占好位。</summary>
public sealed class AppConfig
{
    public string ApiKey { get; set; } = string.Empty;

    public OutputConfig Output { get; set; } = new();

    public LibraryConfig Library { get; set; } = new();

    public OnlineConfig Online { get; set; } = new();

    public UiConfig Ui { get; set; } = new();
}

public sealed class OutputConfig
{
    /// <summary>directsound / wasapi / asio</summary>
    public string Backend { get; set; } = "directsound";

    /// <summary>设备名（按名字记，插拔后序号会变）。</summary>
    public string Device { get; set; } = string.Empty;

    public bool Exclusive { get; set; } = true;

    /// <summary>follow / fixed</summary>
    public string RateStrategy { get; set; } = "follow";

    public int FixedSampleRate { get; set; } = 48000;

    /// <summary>ASIO 缓冲区（采样点），0 = 驱动首选。</summary>
    public int AsioBufferSamples { get; set; }

    /// <summary>ASIO 起始输出声道，0 = 设备的第一对。</summary>
    public int AsioFirstChannel { get; set; }

    public int WasapiBufferMs { get; set; } = 50;

    public Audio.OutputSettings ToSettings() => new()
    {
        Backend = Backend?.ToLowerInvariant() switch
        {
            "asio" => Audio.OutputBackendKind.Asio,
            "wasapi" => Audio.OutputBackendKind.Wasapi,
            _ => Audio.OutputBackendKind.DirectSound
        },
        DeviceName = Device ?? string.Empty,
        Exclusive = Exclusive,
        RateMode = string.Equals(RateStrategy, "fixed", StringComparison.OrdinalIgnoreCase)
            ? Audio.SampleRateMode.Fixed
            : Audio.SampleRateMode.Follow,
        FixedSampleRate = FixedSampleRate > 0 ? FixedSampleRate : 48000,
        AsioBufferSamples = AsioBufferSamples,
        AsioFirstChannel = AsioFirstChannel,
        WasapiBufferMs = WasapiBufferMs > 0 ? WasapiBufferMs : 50
    };

    public void CopyFrom(Audio.OutputSettings settings)
    {
        Backend = settings.Backend switch
        {
            Audio.OutputBackendKind.Asio => "asio",
            Audio.OutputBackendKind.Wasapi => "wasapi",
            _ => "directsound"
        };
        Device = settings.DeviceName;
        Exclusive = settings.Exclusive;
        RateStrategy = settings.RateMode == Audio.SampleRateMode.Fixed ? "fixed" : "follow";
        FixedSampleRate = settings.FixedSampleRate;
        AsioBufferSamples = settings.AsioBufferSamples;
        AsioFirstChannel = settings.AsioFirstChannel;
        WasapiBufferMs = settings.WasapiBufferMs;
    }
}

public sealed class LibraryConfig
{
    /// <summary>媒体库根目录，可多个。</summary>
    public List<string> Folders { get; set; } = new();
}

public sealed class OnlineConfig
{
    public string PlayLevel { get; set; } = "lossless";

    public string DownloadLevel { get; set; } = "hires";

    /// <summary>上次下载使用的目录（下载时弹窗选择并记住）。</summary>
    public string DownloadDir { get; set; } = string.Empty;

    public string NamingTemplate { get; set; } = "{AlbumArtist}/{Album}/{TrackNo} - {Title}";

    /// <summary>通用 API 端点列表（实机反馈：可自行增删，每条可选 Key）。
    /// 运行规则：第 1 条（http 地址）供 GD 源搜索/试听/下载；第一条带 Key 的条目供网易云（ChKSz）源。
    /// 旧的 gdApiUrl / chkszApiUrl / apiKey 字段在加载时迁移进本列表。</summary>
    public List<ApiEndpointConfig> ApiEndpoints { get; set; } = new();

    /// <summary>在线搜索默认音质档（999/740/320/128）。</summary>
    public int PreviewBr { get; set; } = 999;

    /// <summary>（已废弃，仅迁移用）GD 音源地址。见 ApiEndpoints。</summary>
    public string GdApiUrl { get; set; } = "https://music-api.gdstudio.xyz/api.php";

    /// <summary>（已废弃，仅迁移用）网易云地址。见 ApiEndpoints。</summary>
    public string ChkszApiUrl { get; set; } = "https://api.chksz.com";
}

/// <summary>
/// 一个可配置的在线 API 端点（类型 + 地址 + 可选 Key）。
/// Kind 决定运行时用途，为未来扩展新 API 类型预留：新增类型只需注册新的 Kind 并让对应源读取。
/// </summary>
public sealed class ApiEndpointConfig
{
    /// <summary>类型：gd（GD 兼容：搜索/试听/下载/歌词）| chksz（网易云 ChKSz 兼容）。</summary>
    public string Kind { get; set; } = "gd";

    public string Url { get; set; } = string.Empty;

    public string Key { get; set; } = string.Empty;
}

public sealed class UiConfig
{
    public double Volume { get; set; } = 0.6;

    /// <summary>Sequential / RepeatAll / RepeatOne / Shuffle</summary>
    public string PlayMode { get; set; } = "RepeatAll";

    /// <summary>上次停留的导航目标（"{Kind}|{PlaylistId}|{FolderPath}"），启动时恢复（UI-R1.5 反馈）。</summary>
    public string LastNav { get; set; } = string.Empty;

    /// <summary>上次播放的曲目路径，启动时静默恢复信息与歌词。</summary>
    public string LastTrackPath { get; set; } = string.Empty;

    /// <summary>窗口尺寸记忆（UI-R1.5 反馈）。0 表示未记录。</summary>
    public double WindowWidth { get; set; }

    public double WindowHeight { get; set; }

    /// <summary>曲目列表显示模式（UI-R2）：true = 专辑分组，false = 平铺。</summary>
    public bool ListGrouped { get; set; }

    /// <summary>旧主题模式字段（UI-R3 迁移用）：FollowCover / FixedDark。新配置见 ThemeBase + ThemeTint。</summary>
    public string ThemeMode { get; set; } = "FollowCover";

    /// <summary>主题底色（UI-R3 反馈）：Dark = 深色，Light = 浅色。默认浅色。</summary>
    public string ThemeBase { get; set; } = "Light";

    /// <summary>是否随封面染色（UI-R3 反馈）：true = 染色（默认），false = 不染色。</summary>
    public bool ThemeTint { get; set; } = true;

    /// <summary>右侧信息栏折叠状态（UI-R4）：true = 展开（默认）。</summary>
    public bool SidePaneOpen { get; set; } = true;

    /// <summary>桌面歌词（L1 第三步 + L1.1 个性化）：开关 / 锁定 / 单双行 / 字号 / 宽度 / 背景 / 文字颜色；歌词字体见下。</summary>
    public bool DesktopLyricsEnabled { get; set; }

    public bool DesktopLyricsLocked { get; set; } = true;

    public bool DesktopLyricsTwoLines { get; set; } = true;

    public double DesktopLyricsFontSize { get; set; } = 20;

    public double DesktopLyricsWidth { get; set; } = 560;

    /// <summary>L1.1-③：背景卡片显示开关（纯文字模式靠描边/阴影保可读）。</summary>
    public bool DesktopLyricsShowBackground { get; set; } = true;

    /// <summary>L1.1-③：背景卡片不透明度（0.3–0.9）。</summary>
    public double DesktopLyricsBgOpacity { get; set; } = 0.82;

    /// <summary>L1.1-③：文字颜色模式 Theme=跟随取色主题 / Custom=自定义纯色。</summary>
    public string DesktopLyricsTextColorMode { get; set; } = "Theme";

    /// <summary>L1.1-③：自定义文字颜色（#RRGGBB）。</summary>
    public string DesktopLyricsTextColor { get; set; } = "#FFFFFF";

    /// <summary>L1.1-②：歌词字体（右栏/大歌词页/桌面歌词共用；桌面歌词字号独立）。</summary>
    public string LyricFontFamily { get; set; } = "Microsoft YaHei UI";

    /// <summary>L1.1-②：歌词字重 Normal / Medium / Bold。</summary>
    public string LyricFontWeight { get; set; } = "Normal";

    // ================= L3.1 个性化 =================

    /// <summary>曲目行高（紧凑 56 / 标准 72 / 舒适 110，平铺与分组共用）。</summary>
    public int RowHeight { get; set; } = 72;

    /// <summary>专辑分组默认展开（false = 默认折叠，手动切换每组状态会话内记住）。</summary>
    public bool GroupsExpandedByDefault { get; set; } = true;

    /// <summary>分组标题行显示专辑封面。</summary>
    public bool GroupCoverVisible { get; set; } = true;

    /// <summary>全局 UI 字体（空 = 跟随系统）。</summary>
    public string UiFontFamily { get; set; } = string.Empty;

    /// <summary>全局 UI 字号缩放（0.90–1.25）。</summary>
    public double UiFontScale { get; set; } = 1.0;

    /// <summary>自定义强调色（#RRGGBB，空 = 跟随封面取色/主题默认）。</summary>
    public string CustomAccent { get; set; } = string.Empty;

    /// <summary>选中行高亮透明度（0–1，默认 0.12）。</summary>
    public double SelectedOpacity { get; set; } = 0.12;

    /// <summary>悬停高亮透明度（0–1，默认 0.07）。</summary>
    public double HoverOpacity { get; set; } = 0.07;

    /// <summary>曲目列表可见列顺序（Key 列表：Title/Artist/Album/Duration/Format/SampleRate/BitDepth/Bitrate）。</summary>
    public List<string> Columns { get; set; } = new() { "Title", "Artist", "Album", "Duration", "Format", "SampleRate", "BitDepth", "Bitrate" };

    /// <summary>L3.2 迷你窗位置记忆（"x,y"）。</summary>
    public string MiniPos { get; set; } = string.Empty;

    /// <summary>L3.2 迷你窗频谱（mixer DSP tap，设置可关）。</summary>
    public bool MiniSpectrum { get; set; }

    /// <summary>右键菜单风格：true = 复古原生（Win32 质感），false = WPF-UI 现代。</summary>
    public bool ClassicMenus { get; set; }

    /// <summary>未单独设置来源的曲目默认歌词来源（Auto/LrcFile/Embedded/Online，默认 Online=网易云）。</summary>
    public string LyricDefaultPreference { get; set; } = "Online";

    /// <summary>各列宽度（Key → 像素，缺省用默认宽度）。</summary>
    public Dictionary<string, double> ColumnWidths { get; set; } = new();

    /// <summary>L2 托盘：关闭主窗时最小化到托盘而不是退出（默认关闭 = 关窗即退出）。</summary>
    public bool CloseToTray { get; set; }

    /// <summary>L2 全局热键：默认全关；开启后 Ctrl+Alt+P 播放/暂停、Ctrl+Alt+←/→ 上下曲。</summary>
    public bool GlobalHotkeysEnabled { get; set; }

    /// <summary>L2 快捷键自定义：动作名 → 组合串（如 "NextTrack" → "Ctrl+Right"）。空 = 用默认。</summary>
    public Dictionary<string, string> ShortcutBindings { get; set; } = new();

    /// <summary>L2 全局热键自定义：名字 → 组合串（"PlayPause"/"PrevTrack"/"NextTrack"）。空 = 用默认。</summary>
    public Dictionary<string, string> GlobalHotkeyCombos { get; set; } = new();

    /// <summary>L2 行为页-启动恢复策略：启动时恢复上次播放的曲目信息（不自动播放）。</summary>
    public bool RestoreLastTrack { get; set; } = true;

    /// <summary>L2 行为页-启动恢复策略：启动时回到上次停留的页面。</summary>
    public bool RestoreLastNav { get; set; } = true;
}

/// <summary>
/// 配置读写。API Key 只从这里读，且**永不写入日志**（本类任何日志都不打印配置内容）。
/// </summary>
public static class ConfigService
{
    private static readonly object Gate = new();

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    private static AppConfig? _current;

    public static AppConfig Current
    {
        get
        {
            lock (Gate)
            {
                return _current ??= Load();
            }
        }
    }

    private static AppConfig Load()
    {
        try
        {
            AppPaths.EnsureCreated();

            if (!File.Exists(AppPaths.ConfigFile))
            {
                Log.Information("未找到配置文件，使用默认配置");
                return new AppConfig();
            }

            var json = File.ReadAllText(AppPaths.ConfigFile);
            var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions);

            if (config is null)
            {
                Log.Warning("配置文件解析结果为空，使用默认配置");
                return new AppConfig();
            }

            MigrateLegacyOnlineFields(config);

            Log.Information("配置已加载：媒体库根目录 {Count} 个", config.Library.Folders.Count);
            return config;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "读取配置失败，改用默认配置");
            return new AppConfig();
        }
    }

    /// <summary>把旧的 gdApiUrl / chkszApiUrl / apiKey 配置迁移进通用 API 列表（2026-08-15 实机反馈）。</summary>
    private static void MigrateLegacyOnlineFields(AppConfig config)
    {
        var online = config.Online;
        if (online.ApiEndpoints is { Count: > 0 }) return;

        const string defaultGd = "https://music-api.gdstudio.xyz/api.php";
        const string defaultChksz = "https://api.chksz.com";

        // 始终生成 GD + 网易云两条打底，再用旧字段覆盖（保证设置页列表可见、GD 默认源可用）
        online.ApiEndpoints.Add(new ApiEndpointConfig { Kind = "gd", Url = defaultGd });
        online.ApiEndpoints.Add(new ApiEndpointConfig { Kind = "chksz", Url = defaultChksz });

        if (!string.IsNullOrWhiteSpace(online.GdApiUrl)
            && !string.Equals(online.GdApiUrl.Trim(), defaultGd, StringComparison.OrdinalIgnoreCase))
            online.ApiEndpoints[0].Url = online.GdApiUrl.Trim();

        if (!string.IsNullOrWhiteSpace(online.ChkszApiUrl)
            && !string.Equals(online.ChkszApiUrl.Trim(), defaultChksz, StringComparison.OrdinalIgnoreCase))
            online.ApiEndpoints[1].Url = online.ChkszApiUrl.Trim();

        if (!string.IsNullOrWhiteSpace(config.ApiKey))
            online.ApiEndpoints[1].Key = config.ApiKey.Trim();
    }

    /// <summary>GD 源端点：Kind=gd 的第一条合法 http 地址，没有则官方默认。</summary>
    public static string GdEndpointUrl()
    {
        var ep = Current.Online.ApiEndpoints?.FirstOrDefault(
            e => e.Kind == "gd" && Player.Core.Online.OnlineUrl.IsHttp(e.Url));
        return ep is null ? "https://music-api.gdstudio.xyz/api.php" : ep.Url.Trim().TrimEnd('?');
    }

    /// <summary>网易云（ChKSz）端点：Kind=chksz 的第一条带 Key 条目；没有返回 null（在线源按无 Key 降级）。</summary>
    public static ApiEndpointConfig? ChkszEndpoint()
        => Current.Online.ApiEndpoints?.FirstOrDefault(
            e => e.Kind == "chksz" && !string.IsNullOrWhiteSpace(e.Key));

    public static void Save()
    {
        lock (Gate)
        {
            if (_current is null) return;

            try
            {
                AppPaths.EnsureCreated();
                var json = JsonSerializer.Serialize(_current, JsonOptions);
                // 先写临时文件再替换，避免写一半掉电把配置写坏
                var temp = AppPaths.ConfigFile + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, AppPaths.ConfigFile, overwrite: true);
                Log.Debug("配置已保存");
            }
            catch (Exception ex)
            {
                Log.Error(ex, "保存配置失败");
            }
        }
    }
}
