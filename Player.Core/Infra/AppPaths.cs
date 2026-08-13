namespace Player.Core.Infra;

/// <summary>
/// 便携式目录布局（PLAN 第 9 节）：所有可变数据都放在 exe 同级的 data/ 下，
/// 整个程序文件夹拷走即完成迁移。data/ 已在 .gitignore 中，永不入仓库。
/// </summary>
public static class AppPaths
{
    public static string BaseDir { get; } = AppContext.BaseDirectory;

    public static string DataDir { get; } = Path.Combine(AppContext.BaseDirectory, "data");

    public static string LogsDir => Path.Combine(DataDir, "logs");

    public static string CacheDir => Path.Combine(DataDir, "cache");

    public static string CoversDir => Path.Combine(CacheDir, "covers");

    public static string LyricsCacheDir => Path.Combine(CacheDir, "lyrics");

    /// <summary>配置文件；P3 起承载 apikey，只能从这里读取。</summary>
    public static string ConfigFile => Path.Combine(DataDir, "config.json");

    /// <summary>媒体库数据库（P1 起使用）。</summary>
    public static string DatabaseFile => Path.Combine(DataDir, "library.db");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(LogsDir);
        Directory.CreateDirectory(CoversDir);
        Directory.CreateDirectory(LyricsCacheDir);
    }
}
