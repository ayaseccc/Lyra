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

    public string DownloadDir { get; set; } = string.Empty;

    public string NamingTemplate { get; set; } = "{AlbumArtist}/{Album}/{TrackNo} - {Title}";
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

    /// <summary>主题模式（UI-R3）：FollowCover = 跟随封面整体染色（默认），FixedDark = 固定深色（逃生口）。</summary>
    public string ThemeMode { get; set; } = "FollowCover";
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

            Log.Information("配置已加载：媒体库根目录 {Count} 个", config.Library.Folders.Count);
            return config;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "读取配置失败，改用默认配置");
            return new AppConfig();
        }
    }

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
