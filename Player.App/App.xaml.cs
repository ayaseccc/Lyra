using System.IO;
using System.Windows;
using System.Windows.Threading;
using Player.App.ViewModels;
using Player.Core.Audio;
using Player.Core.Infra;
using Player.Core.Library;
using Player.Core.Lyrics;
using Player.Core.Online;
using Serilog;

namespace Player.App;

public partial class App : Application
{
    private PlaybackEngine? _engine;
    private LibraryService? _library;
    private PlaylistService? _playlists;
    private PlayerViewModel? _player;
    private ShellViewModel? _shell;
    private ChkszClient? _client;
    private LyricsService? _lyrics;
    private Player.Core.Online.OnlineSources? _onlineSources;
    private Player.Core.Downloads.DownloadService? _downloads;
    private Mutex? _instanceMutex;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 先挂异常处理器，再做任何可能抛异常的初始化（P6 三层兜底）
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // P6 单实例：第二实例把文件（双击/多选/拖到 exe）转交运行实例后退出
        if (!SingleInstance.TryAcquire(out _instanceMutex))
        {
            var files = e.Args.Select(a => a.Trim('"')).Where(a => File.Exists(a)).ToList();
            SingleInstance.ForwardFiles(files);
            Shutdown(0);
            return;
        }
        SingleInstance.StartServer(HandleIncomingFiles);

        try
        {
            LogSetup.Initialize();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "无法创建 data 目录或日志文件：\n" + ex.Message +
                "\n\n请把程序放在有写入权限的目录（例如不要放在 Program Files 下）。",
                "Player", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        // P6 文件关联：幂等注册（放在日志初始化后，失败原因可查）
        try { FileAssociation.Register(); }
        catch (Exception ex) { Log.Warning(ex, "文件关联注册失败（不影响运行）"); }

        try
        {
            Db.Initialize();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "媒体库数据库初始化失败");
            MessageBox.Show("媒体库数据库打开失败：\n" + ex.Message + "\n\n详见 data/logs 下的日志。",
                "Player", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        try
        {
            BassRuntime.Initialize();
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "BASS 初始化失败，程序退出");
            MessageBox.Show(
                "音频引擎初始化失败：\n" + ex.Message +
                "\n\n请确认 bass.dll（x64）与 Player.exe 在同一目录。详细信息见 data/logs 下的日志文件。",
                "Player", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        _engine = new PlaybackEngine();
        _library = new LibraryService();
        _playlists = new PlaylistService(_library);

        // Apply the fixed mini-surface palette before any window is shown. This keeps a
        // no-track startup from briefly exposing the XAML fallback resources.
        Theming.ThemeService.Initialize();

        // L3.1 个性化：行高/全局字体/字号缩放写入 Application 资源（XAML DynamicResource 引用）
        Theming.ThemeService.ApplyUiPersonalization();

        // P3：ChKSz 客户端与歌词服务。Key 只在 ConfigService 里读，任何在线失败都不影响本地播放
        _client = new ChkszClient();

        // P4：在线源注册表（GD 默认零 Key + 网易云兜底），后台探测可用性（下拉灰显用）
        _onlineSources = new Player.Core.Online.OnlineSources(_client);
        _ = _onlineSources.ProbeAllAsync(CancellationToken.None);

        // P4-6：歌词链插入 GD（零额度优先；未注入时保持原链）
        _lyrics = new LyricsService(_client, _onlineSources.Default as Player.Core.Online.GdSource);

        // P4-5：下载服务（串行队列）；下载完成后触发媒体库增量扫描自动入库
        _downloads = new Player.Core.Downloads.DownloadService(_onlineSources, _library);
        _downloads.BatchCompleted += OnDownloadBatchCompleted;

        _player = new PlayerViewModel(_engine, _lyrics, _client);
        _player.SetOnlineSources(_onlineSources);
        _shell = new ShellViewModel(_library, _playlists, _player, _engine, _client, _onlineSources, _downloads);

        var window = new MainWindow { DataContext = _shell };
        MainWindow = window;
        window.Show();

        try
        {
            // 载入曲库 + 启动增量扫描，全程不阻塞 UI
            await _shell.InitializeAsync();

            if (e.Args.Length > 0)
                await _shell.HandleDroppedPathsAsync(e.Args);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动初始化失败");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 顺序很重要：先停 UI 侧的订阅与计时器，再停监听与扫描，最后放流与 BASS
        _shell?.Dispose();
        _player?.Dispose();
        _lyrics?.Dispose();
        _downloads?.Dispose();
        _onlineSources?.Dispose();
        _client?.Dispose();
        _library?.StopWatching();
        _library?.Dispose();
        _engine?.Dispose();
        BassRuntime.Shutdown();
        ConfigService.Save();
        LogSetup.Shutdown();

        base.OnExit(e);
    }

    /// <summary>P4-5：一批下载完成后触发增量扫描自动入库。</summary>
    private void OnDownloadBatchCompleted()
    {
        _ = _shell?.ScanAsync(fullRescan: false);
    }

    /// <summary>异常弹窗节流：同一条异常在 10 秒内反复出现时不重复弹窗（只记日志），
    /// 连续 5 次直接退出——防止布局循环异常导致弹窗风暴耗尽资源（2026-08-15 实机崩溃教训）。</summary>
    private string? _lastExceptionMessage;
    private DateTime _lastExceptionAt;
    private int _exceptionRepeatCount;

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "UI 线程未处理异常");

        var now = DateTime.UtcNow;
        var message = e.Exception.Message ?? string.Empty;
        if (string.Equals(message, _lastExceptionMessage, StringComparison.Ordinal)
            && (now - _lastExceptionAt) < TimeSpan.FromSeconds(10))
        {
            _exceptionRepeatCount++;
            if (_exceptionRepeatCount >= 5)
            {
                Log.Fatal("同一异常连续出现 {Count} 次，为避免弹窗风暴直接退出", _exceptionRepeatCount);
                Environment.Exit(1);
            }
            e.Handled = true;
            return;
        }

        _lastExceptionMessage = message;
        _lastExceptionAt = now;
        _exceptionRepeatCount = 1;

        MessageBox.Show("发生了一个错误：\n" + e.Exception.Message + "\n\n详细信息已写入 data/logs。",
            "Player", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
            Log.Fatal(ex, "非 UI 线程未处理异常");
        else
            Log.Fatal("非 UI 线程未处理异常：{Object}", e.ExceptionObject);
    }

    /// <summary>P6：外部打开文件（双击/多选/拖到 exe）→ 导入曲库并播放；主窗从任意表面恢复。</summary>
    private async Task HandleIncomingFilesAsync(IReadOnlyList<string> files)
    {
        try
        {
            if (_library is null) return;
            var tracks = await _library.ImportFilesAsync(files);
            Log.Information("外部文件导入完成 {Count} 首", tracks.Count);
            if (tracks.Count == 0) return;

            if (Application.Current.MainWindow is MainWindow main && main.Shell is { } shell)
            {
                shell.Player.PlayTracks(tracks, 0, "文件打开");
                main.ShowFromExternalOpen();
                Log.Information("外部文件已入队播放");
            }
        }
        catch (Exception ex)
        {
            Log.Error(ex, "处理外部打开文件失败");
        }
    }

    private void HandleIncomingFiles(IReadOnlyList<string> files)
    {
        Log.Information("收到外部打开文件 {Count} 个：{First}", files.Count, files.FirstOrDefault());
        _ = HandleIncomingFilesAsync(files);
    }

    /// <summary>P6：Task 未观察异常兜底（async void 之外的 fire-and-forget 任务）——记录并标记已处理。</summary>
    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error(e.Exception, "Task 未观察异常");
        e.SetObserved();
    }
}
