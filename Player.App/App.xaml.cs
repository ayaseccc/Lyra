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
    private readonly object _incomingFilesGate = new();
    private readonly Queue<IReadOnlyList<string>> _pendingIncomingFiles = new();
    private readonly SemaphoreSlim _incomingFilesSerial = new(1, 1);
    private bool _startupReady;
    private bool _incomingDrainScheduled;
    private bool _startupCompleted;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 先挂异常处理器，再做任何可能抛异常的初始化（P6 三层兜底）
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        var startupFiles = e.Args
            .Select(a => a.Trim('"'))
            .Where(a => File.Exists(a) && AudioFormats.IsSupported(a))
            .ToList();

        // P6 单实例：第二实例把文件（双击/多选/拖到 exe）转交运行实例后退出。
        // 这一段发生在主窗建立前，任何异常都必须主动退出，不能交给通用 UI
        // 异常处理器吞掉后留下一个没有窗口的进程。
        try
        {
            if (!SingleInstance.TryAcquire(out _instanceMutex))
            {
                var forwarded = SingleInstance.ForwardFiles(startupFiles);
                if (startupFiles.Count > 0 && !forwarded)
                {
                    MessageBox.Show(
                        "Lyra 已在运行，但未能把所选音频文件交给现有窗口。\n\n请关闭正在运行的旧版 Player 或 Lyra 后重试。",
                        "Lyra", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                Shutdown(startupFiles.Count == 0 || forwarded ? 0 : 2);
                return;
            }

            SingleInstance.StartServer(HandleIncomingFiles);
        }
        catch (Exception ex)
        {
            ShowFatalStartupError("单实例服务初始化失败", ex);
            return;
        }

        try
        {
            LogSetup.Initialize();
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "无法创建 data 目录或日志文件：\n" + ex.Message +
                "\n\n请把程序放在有写入权限的目录（例如不要放在 Program Files 下）。",
                "Lyra", MessageBoxButton.OK, MessageBoxImage.Error);
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
                "Lyra", MessageBoxButton.OK, MessageBoxImage.Error);
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
                "\n\n请确认 bass.dll（x64）与 Lyra.exe 在同一目录。详细信息见 data/logs 下的日志文件。",
                "Lyra", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        try
        {
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

            // P4：在线源注册表（GD 默认零 Key + 网易云兜底）。可用性探测延迟到
            // 首次进入在线搜索页，避免每次启动都产生与本地播放无关的网络请求。
            _onlineSources = new Player.Core.Online.OnlineSources(_client);

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
        }
        catch (Exception ex)
        {
            ShowFatalStartupError("播放器界面初始化失败", ex);
            return;
        }

        try
        {
            // 载入曲库 + 启动增量扫描，全程不阻塞 UI
            await _shell.InitializeAsync(restoreLastTrack: startupFiles.Count == 0);

            if (startupFiles.Count > 0)
                await _shell.OpenExternalFilesAsync(startupFiles);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "启动初始化失败");
        }

        // 管道从进程一开始就监听，避免第二次双击在初始化窗口期丢失；
        // 这里才放行并按到达顺序处理积压文件。
        MarkStartupReady();
        _startupCompleted = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 顺序很重要：先停 UI 侧的订阅与计时器，再停监听与扫描，最后放流与 BASS
        try
        {
            RunExitStep("停止单实例服务", SingleInstance.StopServer);
            if (_downloads is not null)
                _downloads.BatchCompleted -= OnDownloadBatchCompleted;
            RunExitStep("释放主界面模型", () => _shell?.Dispose());
            RunExitStep("释放播放界面模型", () => _player?.Dispose());
            RunExitStep("释放歌词服务", () => _lyrics?.Dispose());
            RunExitStep("释放下载服务", () => _downloads?.Dispose());
            RunExitStep("释放在线源", () => _onlineSources?.Dispose());
            RunExitStep("释放在线客户端", () => _client?.Dispose());
            RunExitStep("停止曲库监听", () => _library?.StopWatching());
            RunExitStep("释放曲库", () => _library?.Dispose());
            RunExitStep("释放播放引擎", () => _engine?.Dispose());
            RunExitStep("关闭 BASS", BassRuntime.Shutdown);
            RunExitStep("保存配置", ConfigService.Save);
        }
        finally
        {
            RunExitStep("释放旧版兼容锁", SingleInstance.ReleaseLegacyMutex);
            if (_instanceMutex is not null)
            {
                var mutex = _instanceMutex;
                RunExitStep("释放 Lyra 实例锁", () =>
                {
                    try { mutex.ReleaseMutex(); }
                    catch (ApplicationException) { }
                    finally { mutex.Dispose(); }
                });
                _instanceMutex = null;
            }

            RunExitStep("关闭日志", LogSetup.Shutdown, logFailure: false);
            DispatcherUnhandledException -= OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException -= OnDomainUnhandledException;
            TaskScheduler.UnobservedTaskException -= OnUnobservedTaskException;
            base.OnExit(e);
        }
    }

    /// <summary>P4-5：一批下载完成后触发增量扫描自动入库。</summary>
    private void OnDownloadBatchCompleted()
    {
        var shell = _shell;
        if (shell is null || Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
        _ = Dispatcher.BeginInvoke(
            () => _ = shell.ScanAsync(fullRescan: false),
            DispatcherPriority.Background);
    }

    /// <summary>异常弹窗节流：同一条异常在 10 秒内反复出现时不重复弹窗（只记日志），
    /// 连续 5 次直接退出——防止布局循环异常导致弹窗风暴耗尽资源（2026-08-15 实机崩溃教训）。</summary>
    private string? _lastExceptionMessage;
    private DateTime _lastExceptionAt;
    private int _exceptionRepeatCount;
    private int _backgroundExceptionDialogPending;

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("UI 线程未处理异常：{Exception}", ChkszClient.Redact(e.Exception.ToString()));

        if (!_startupCompleted && MainWindow is null)
        {
            e.Handled = true;
            ShowFatalStartupError("播放器启动失败", e.Exception);
            return;
        }

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

        MessageBox.Show("发生了一个错误：\n" + ChkszClient.Redact(e.Exception.Message ?? string.Empty)
                        + "\n\n日志：" + AppPaths.LogsDir,
            "Lyra", MessageBoxButton.OK, MessageBoxImage.Warning);
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        string detail;
        if (e.ExceptionObject is Exception ex)
        {
            Log.Fatal("非 UI 线程未处理异常：{Exception}", ChkszClient.Redact(ex.ToString()));
            detail = ChkszClient.Redact(ex.Message);
        }
        else
        {
            detail = "未知后台错误";
            Log.Fatal("非 UI 线程未处理异常：{Object}",
                ChkszClient.Redact(e.ExceptionObject?.ToString() ?? detail));
        }

        try
        {
            MessageBox.Show(
                "播放器发生了无法恢复的后台错误，即将退出：\n" + detail
                + "\n\n日志：" + AppPaths.LogsDir,
                "Lyra", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch
        {
            // 进程已在终止路径上，弹窗失败时仍保留上面的日志。
        }
    }

    /// <summary>P6：外部打开文件（双击/多选/拖到 exe）→ 临时入队播放；主窗从任意表面恢复。</summary>
    private async Task HandleIncomingFilesAsync(IReadOnlyList<string> files)
    {
        try
        {
            if (Application.Current.MainWindow is MainWindow main && main.Shell is { } shell)
            {
                await shell.OpenExternalFilesAsync(files);
                main.ShowFromExternalOpen();
                Log.Information("外部文件已交给播放队列（{Count} 个）", files.Count);
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

        var scheduleDrain = false;
        lock (_incomingFilesGate)
        {
            _pendingIncomingFiles.Enqueue(files.ToArray());
            if (!_startupReady)
            {
                Log.Information("播放器仍在启动，已暂存外部打开文件 {Count} 个", files.Count);
                return;
            }

            if (!_incomingDrainScheduled)
            {
                _incomingDrainScheduled = true;
                scheduleDrain = true;
            }
        }

        if (scheduleDrain)
            _ = DrainIncomingFilesAsync();
    }

    private void MarkStartupReady()
    {
        var scheduleDrain = false;
        lock (_incomingFilesGate)
        {
            _startupReady = true;
            if (_pendingIncomingFiles.Count > 0 && !_incomingDrainScheduled)
            {
                _incomingDrainScheduled = true;
                scheduleDrain = true;
            }
        }

        if (scheduleDrain)
            _ = DrainIncomingFilesAsync();
    }

    private async Task DrainIncomingFilesAsync()
    {
        while (true)
        {
            IReadOnlyList<string>? batch;
            lock (_incomingFilesGate)
            {
                if (_pendingIncomingFiles.Count == 0)
                {
                    _incomingDrainScheduled = false;
                    return;
                }

                batch = _pendingIncomingFiles.Dequeue();
            }

            await ProcessIncomingFilesAsync(batch);
        }
    }

    private async Task ProcessIncomingFilesAsync(IReadOnlyList<string> files)
    {
        await _incomingFilesSerial.WaitAsync();
        try
        {
            await HandleIncomingFilesAsync(files);
        }
        finally
        {
            _incomingFilesSerial.Release();
        }
    }

    /// <summary>P6：Task 未观察异常兜底（async void 之外的 fire-and-forget 任务）——记录并标记已处理。</summary>
    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error("Task 未观察异常：{Exception}", ChkszClient.Redact(e.Exception.ToString()));
        e.SetObserved();
        QueueBackgroundExceptionDialog(e.Exception.GetBaseException().Message);
    }

    private void QueueBackgroundExceptionDialog(string message)
    {
        if (Interlocked.Exchange(ref _backgroundExceptionDialogPending, 1) != 0)
            return;

        void ShowDialog()
        {
            try
            {
                MessageBox.Show(
                    "后台任务发生了一个错误，播放器已继续运行：\n"
                    + ChkszClient.Redact(message) + "\n\n日志：" + AppPaths.LogsDir,
                    "Lyra", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                Interlocked.Exchange(ref _backgroundExceptionDialogPending, 0);
            }
        }

        try
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
            {
                Interlocked.Exchange(ref _backgroundExceptionDialogPending, 0);
                return;
            }

            Dispatcher.BeginInvoke(ShowDialog, DispatcherPriority.Normal);
        }
        catch
        {
            Interlocked.Exchange(ref _backgroundExceptionDialogPending, 0);
        }
    }

    private void ShowFatalStartupError(string title, Exception exception)
    {
        Log.Fatal(exception, "{Title}", title);
        try
        {
            var logHint = Directory.Exists(AppPaths.LogsDir)
                ? "\n\n日志：" + AppPaths.LogsDir
                : string.Empty;
            MessageBox.Show(
                title + "：\n" + ChkszClient.Redact(exception.Message)
                + "\n\nLyra 将退出。" + logHint,
                "Lyra", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch
        {
        }

        if (!Dispatcher.HasShutdownStarted && !Dispatcher.HasShutdownFinished)
            Shutdown(1);
    }

    private static void RunExitStep(string name, Action action, bool logFailure = true)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            if (logFailure)
                Log.Warning(ex, "退出清理失败：{Step}", name);
        }
    }
}
