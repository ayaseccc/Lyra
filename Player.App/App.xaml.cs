using System.Windows;
using System.Windows.Threading;
using Player.App.ViewModels;
using Player.Core.Audio;
using Player.Core.Infra;
using Serilog;

namespace Player.App;

public partial class App : Application
{
    private PlaybackEngine? _engine;
    private PlayerViewModel? _viewModel;

    protected override void OnStartup(StartupEventArgs e)
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
        _viewModel = new PlayerViewModel(_engine);

        var window = new MainWindow { DataContext = _viewModel };
        MainWindow = window;
        window.Show();

        // 支持命令行传入文件（"用 Player 打开"）
        if (e.Args.Length > 0)
            _viewModel.LoadPaths(e.Args);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // 顺序很重要：先停 UI 计时器与事件订阅，再放流，最后放 BASS，保证退出无残留线程
        _viewModel?.Dispose();
        _engine?.Dispose();
        BassRuntime.Shutdown();
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
