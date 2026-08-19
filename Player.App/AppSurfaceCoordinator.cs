using System.Windows;
using Player.App.Controls;
using Player.App.ViewModels;
using Player.Core.Infra;

namespace Player.App;

internal enum PrimarySurface
{
    Main,
    Mini,
    Background
}

internal enum DesktopLyricsSurface
{
    Hidden,
    VisibleUnlocked,
    VisibleLocked
}

internal enum AppLifecycle
{
    Running,
    Exiting
}

/// <summary>
/// Owns the visibility and shutdown transitions for every top-level player surface.
/// Windows may request a transition, but they never show another window directly.
/// </summary>
internal sealed class AppSurfaceCoordinator : IDisposable
{
    private readonly MainWindow _mainWindow;
    private readonly PlayerViewModel _player;
    private readonly Func<DesktopLyricsWindow> _createDesktopLyrics;
    private readonly Action _refreshDesktopLyrics;

    private MiniPlayerWindow? _miniWindow;
    private DesktopLyricsWindow? _desktopLyricsWindow;
    private bool _disposed;

    public AppSurfaceCoordinator(
        MainWindow mainWindow,
        PlayerViewModel player,
        Func<DesktopLyricsWindow> createDesktopLyrics,
        Action refreshDesktopLyrics)
    {
        _mainWindow = mainWindow;
        _player = player;
        _createDesktopLyrics = createDesktopLyrics;
        _refreshDesktopLyrics = refreshDesktopLyrics;
    }

    public PrimarySurface PrimarySurface { get; private set; } = PrimarySurface.Main;

    public DesktopLyricsSurface DesktopLyricsSurface =>
        _desktopLyricsWindow is not { IsVisible: true }
            ? DesktopLyricsSurface.Hidden
            : _desktopLyricsWindow.IsLocked
                ? DesktopLyricsSurface.VisibleLocked
                : DesktopLyricsSurface.VisibleUnlocked;

    public AppLifecycle Lifecycle { get; private set; } = AppLifecycle.Running;

    public bool TrayReady { get; private set; }

    public bool IsExiting => Lifecycle == AppLifecycle.Exiting;

    public bool IsMiniVisible => PrimarySurface == PrimarySurface.Mini && _miniWindow is { IsVisible: true };

    public bool IsDesktopLyricsVisible => _desktopLyricsWindow is { IsVisible: true };

    public DesktopLyricsWindow? DesktopLyricsWindow => _desktopLyricsWindow;

    public event EventHandler? StateChanged;

    public void SetTrayReady(bool ready)
    {
        TrayReady = ready;
        NotifyStateChanged();
    }

    public void RestoreConfiguredDesktopLyrics()
    {
        if (ConfigService.Current.Ui.DesktopLyricsEnabled)
            ShowDesktopLyrics(forceUnlocked: false);
    }

    public void ShowMain()
    {
        if (!CanTransition()) return;

        HideMiniSurface();
        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();

        PrimarySurface = PrimarySurface.Main;
        NotifyStateChanged();
    }

    public void ShowMini()
    {
        if (!CanTransition()) return;

        var mini = EnsureMiniWindow();
        try
        {
            if (!mini.IsVisible)
                mini.Show();
            mini.ActivateSurface();
            _mainWindow.Hide();
            mini.Activate();

            PrimarySurface = PrimarySurface.Mini;
            NotifyStateChanged();
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "迷你悬浮窗显示失败，保留主窗口");
            mini.DeactivateSurface();
            mini.Hide();
            _mainWindow.Show();
            PrimarySurface = PrimarySurface.Main;
            NotifyStateChanged();
        }
    }

    public void MovePrimaryToBackground()
    {
        if (!CanTransition() || !TrayReady) return;

        HideMiniSurface();
        _mainWindow.Hide();
        PrimarySurface = PrimarySurface.Background;
        NotifyStateChanged();
    }

    public void ToggleDesktopLyrics()
    {
        if (!CanTransition()) return;

        if (IsDesktopLyricsVisible)
            HideDesktopLyrics();
        else
            ShowDesktopLyrics(forceUnlocked: true);
    }

    public void ApplyDesktopLyricsSettings() => _desktopLyricsWindow?.ApplySettings();

    public void PrepareForExit()
    {
        if (Lifecycle == AppLifecycle.Exiting) return;

        Lifecycle = AppLifecycle.Exiting;
        TrayReady = false;

        if (_miniWindow is not null)
        {
            _miniWindow.RestoreRequested -= ShowMain;
            _miniWindow.ExitRequested -= RequestExit;
            _miniWindow.CloseForExit();
            _miniWindow = null;
        }

        if (_desktopLyricsWindow is not null)
        {
            _desktopLyricsWindow.DismissRequested -= HideDesktopLyrics;
            _desktopLyricsWindow.Close();
            _desktopLyricsWindow = null;
        }

        NotifyStateChanged();
    }

    public void RequestExit()
    {
        PrepareForExit();
        Application.Current.Shutdown();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        PrepareForExit();
    }

    private bool CanTransition() => !_disposed && Lifecycle == AppLifecycle.Running;

    private MiniPlayerWindow EnsureMiniWindow()
    {
        if (_miniWindow is not null) return _miniWindow;

        _miniWindow = new MiniPlayerWindow(_player) { Owner = null };
        _miniWindow.RestoreRequested += ShowMain;
        _miniWindow.ExitRequested += RequestExit;
        return _miniWindow;
    }

    private DesktopLyricsWindow EnsureDesktopLyricsWindow()
    {
        if (_desktopLyricsWindow is not null) return _desktopLyricsWindow;

        _desktopLyricsWindow = _createDesktopLyrics();
        _desktopLyricsWindow.DismissRequested += HideDesktopLyrics;
        return _desktopLyricsWindow;
    }

    private void HideMiniSurface()
    {
        if (_miniWindow is null) return;
        _miniWindow.DeactivateSurface();
        if (_miniWindow.IsVisible)
            _miniWindow.Hide();
    }

    private void ShowDesktopLyrics(bool forceUnlocked)
    {
        var lyrics = EnsureDesktopLyricsWindow();
        if (forceUnlocked)
            lyrics.ForceUnlocked();
        if (!lyrics.IsVisible)
            lyrics.Show();
        lyrics.ApplySettings();
        _refreshDesktopLyrics();

        ConfigService.Current.Ui.DesktopLyricsEnabled = true;
        ConfigService.Save();
        NotifyStateChanged();
    }

    private void HideDesktopLyrics()
    {
        if (!CanTransition()) return;

        _desktopLyricsWindow?.Hide();
        ConfigService.Current.Ui.DesktopLyricsEnabled = false;
        ConfigService.Save();
        NotifyStateChanged();
    }

    private void NotifyStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);
}
