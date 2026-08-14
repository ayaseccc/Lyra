using System;
using System.ComponentModel;
using System.Windows.Forms;
using Player.App.ViewModels;
using Player.Core.Infra;

namespace Player.App.SystemTray;

/// <summary>
/// L2 托盘（PLAN：托盘菜单 播放控制/桌面歌词开关/显示主窗/退出，双击还原）。
/// WinForms NotifyIcon（UseWindowsForms 随 SDK 自带，无新增 NuGet 依赖）。
/// 与 B4 ShutdownMode 语义兼容：开启"关闭到托盘"后主窗关闭被拦截为隐藏、进程存活，
/// 显式退出路径 = 托盘菜单-退出（MainWindow.OnClosing 负责拦截，这里只管菜单行为）。
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly PlayerViewModel _player;
    private readonly Action _toggleDesktopLyrics;
    private readonly Action _showWindow;
    private readonly Action _exit;
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _playPauseItem;
    private readonly ToolStripMenuItem _desktopLyricsItem;
    private readonly System.Drawing.Icon _iconResource;
    private bool _disposed;

    public TrayService(PlayerViewModel player, Action toggleDesktopLyrics, Action showWindow, Action exit)
    {
        _player = player;
        _toggleDesktopLyrics = toggleDesktopLyrics;
        _showWindow = showWindow;
        _exit = exit;

        _playPauseItem = new ToolStripMenuItem("播放 / 暂停");
        var previousItem = new ToolStripMenuItem("上一曲");
        var nextItem = new ToolStripMenuItem("下一曲");
        _desktopLyricsItem = new ToolStripMenuItem("桌面歌词") { CheckOnClick = true };
        var showItem = new ToolStripMenuItem("显示主窗");
        var exitItem = new ToolStripMenuItem("退出");

        var menu = new ContextMenuStrip();
        menu.Items.AddRange(new ToolStripItem[]
        {
            _playPauseItem, previousItem, nextItem,
            new ToolStripSeparator(),
            _desktopLyricsItem,
            new ToolStripSeparator(),
            showItem, exitItem
        });

        _iconResource = LoadIcon();
        _icon = new NotifyIcon
        {
            Text = "Player",
            Icon = _iconResource,
            Visible = true,
            ContextMenuStrip = menu
        };

        _playPauseItem.Click += (_, _) => _player.PlayPauseCommand.Execute(null);
        previousItem.Click += (_, _) => _player.PreviousCommand.Execute(null);
        nextItem.Click += (_, _) => _player.NextCommand.Execute(null);
        _desktopLyricsItem.Click += (_, _) =>
        {
            _toggleDesktopLyrics();
            RefreshDesktopLyricsCheck();
        };
        showItem.Click += (_, _) => _showWindow();
        exitItem.Click += (_, _) => _exit();
        _icon.DoubleClick += (_, _) => _showWindow();

        _player.PropertyChanged += OnPlayerPropertyChanged;
        RefreshPlayerState();
        RefreshDesktopLyricsCheck();
    }

    private static System.Drawing.Icon LoadIcon()
    {
        try
        {
            using var stream = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Assets/app.ico"))?.Stream;
            if (stream is not null) return new System.Drawing.Icon(stream);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "托盘图标资源加载失败，退回系统图标");
        }
        return System.Drawing.SystemIcons.Application;
    }

    /// <summary>桌面歌词开关被其他入口（播放条按钮等）改过之后，同步托盘菜单勾选。</summary>
    public void RefreshDesktopLyricsCheck()
    {
        if (_disposed) return;
        _desktopLyricsItem.Checked = ConfigService.Current.Ui.DesktopLyricsEnabled;
    }

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PlayerViewModel.Title) or nameof(PlayerViewModel.Artist)
            or nameof(PlayerViewModel.CurrentTrack) or nameof(PlayerViewModel.IsPlaying))
        {
            RefreshPlayerState();
        }
    }

    private void RefreshPlayerState()
    {
        if (_disposed) return;
        _playPauseItem.Text = _player.IsPlaying ? "暂停" : "播放";

        var title = string.IsNullOrWhiteSpace(_player.Title) ? "Player" : _player.Title.Trim();
        if (!string.IsNullOrWhiteSpace(_player.Artist)) title += " - " + _player.Artist.Trim();
        // NotifyIcon.Text 上限 63 字符，超长截断
        if (title.Length > 60) title = title[..60] + "…";
        _icon.Text = title;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _player.PropertyChanged -= OnPlayerPropertyChanged;
        _icon.Visible = false;
        _icon.Dispose();
        _iconResource.Dispose();
    }
}
