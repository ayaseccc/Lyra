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
    /// <summary>P2 起可选 asio / wasapi / directsound；P1 只有 directsound。</summary>
    public string Backend { get; set; } = "directsound";

    public string Device { get; set; } = string.Empty;

    public bool Exclusive { get; set; } = true;

    public string RateStrategy { get; set; } = "follow";
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
