using System.Windows;
using System.Windows.Threading;
using Player.App.ViewModels;
using Player.Core.Audio;
using Player.Core.Infra;
using Player.Core.Library;
using Serilog;

namespace Player.App;

public partial class App : Application
{
    private PlaybackEngine? _engine;
    private LibraryService? _library;
    private PlaylistService? _playlists;
    private PlayerViewModel? _player;
    private ShellViewModel? _shell;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 先挂异常处理器，再做任何可能抛异常的初始化
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

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
        _player = new PlayerViewModel(_engine);
        _shell = new ShellViewModel(_library, _playlists, _player);

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
        _library?.StopWatching();
        _library?.Dispose();
        _engine?.Dispose();
        BassRuntime.Shutdown();
        ConfigService.Save();
        LogSetup.Shutdown();

        base.OnExit(e);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error(e.Exception, "UI 线程未处理异常");
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
}
