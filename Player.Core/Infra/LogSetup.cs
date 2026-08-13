using Serilog;

namespace Player.Core.Infra;

/// <summary>
/// Serilog 滚动文件日志（PLAN 第 2 节）。排查 BASS / ASIO 设备问题主要靠它。
/// 注意：日志里永远不允许出现 API Key，P3 打印 URL 时必须把 apikey 参数脱敏。
/// </summary>
public static class LogSetup
{
    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized) return;

        AppPaths.EnsureCreated();

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(
                path: Path.Combine(AppPaths.LogsDir, "player-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        _initialized = true;
        Log.Information("================ Player 启动 ================");
    }

    public static void Shutdown()
    {
        if (!_initialized) return;
        Log.Information("================ Player 退出 ================");
        Log.CloseAndFlush();
        _initialized = false;
    }
}
