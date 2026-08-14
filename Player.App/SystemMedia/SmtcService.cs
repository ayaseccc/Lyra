using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Windows.Media;
using Windows.Media.Core;
using Windows.Storage;
using Windows.Storage.Streams;
using Player.App.ViewModels;
using Player.Core.Infra;

namespace Player.App.SystemMedia;

/// <summary>
/// L2 SMTC 接入（探针 SmtcProbe 选型：SystemMediaTransportControlsInterop.GetForWindow 桌面直连）。
/// 媒体键控制播放（Play/Pause/Next/Previous）；锁屏/音量浮窗显示 标题/艺术家/专辑/封面；
/// 播放状态与进度实时同步（进度每秒节流）。
/// </summary>
public sealed class SmtcService : IDisposable
{
    private readonly PlayerViewModel _player;
    private readonly SystemMediaTransportControls? _smtc;
    private DateTime _lastPositionPush = DateTime.MinValue;
    private bool _disposed;

    /// <summary>
    /// 绑定窗口句柄创建 SMTC。失败直接抛出（带 HResult），由调用方决定重试时机；
    /// 关键点：GetForWindow 在窗口**显示前**调用会失败，必须等窗口真正可见后初始化
    /// （MainWindow 在 Loaded 里补建，失败则保持 null 以便重试）。
    /// </summary>
    public SmtcService(IntPtr hwnd, PlayerViewModel player)
    {
        _player = player;

        _smtc = SystemMediaTransportControlsInterop.GetForWindow(hwnd);
        _smtc.IsEnabled = true;
        _smtc.IsPlayEnabled = true;
        _smtc.IsPauseEnabled = true;
        _smtc.IsNextEnabled = true;
        _smtc.IsPreviousEnabled = true;
        _smtc.ButtonPressed += OnButtonPressed;

        player.PropertyChanged += OnPlayerPropertyChanged;
        PushAll();
    }

    private void OnPlayerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(PlayerViewModel.Title):
            case nameof(PlayerViewModel.Artist):
            case nameof(PlayerViewModel.Album):
            case nameof(PlayerViewModel.CurrentTrack):
                PushMetadata();
                break;
            case nameof(PlayerViewModel.IsPlaying):
                PushState();
                break;
            case nameof(PlayerViewModel.HasTrack):
                if (_player.HasTrack) PushAll();
                else PushStopped();
                break;
            case nameof(PlayerViewModel.PositionSeconds):
                // 进度节流：最多每秒推一次（PositionSeconds 每 250ms 刷新）
                if (DateTime.UtcNow - _lastPositionPush >= TimeSpan.FromSeconds(1))
                    PushPosition();
                break;
        }
    }

    private void PushAll()
    {
        PushMetadata();
        PushState();
        PushPosition();
    }

    private async void PushMetadata()
    {
        if (_smtc is null) return;
        try
        {
            var updater = _smtc.DisplayUpdater;
            updater.Type = MediaPlaybackType.Music;
            updater.MusicProperties.Title = string.IsNullOrWhiteSpace(_player.Title) || _player.Title == "未在播放"
                ? "Player"
                : _player.Title;
            updater.MusicProperties.Artist = _player.Artist ?? string.Empty;
            updater.MusicProperties.AlbumTitle = _player.Album ?? string.Empty;

            var hash = _player.CurrentTrack?.CoverHash;
            if (!string.IsNullOrEmpty(hash))
            {
                var path = Path.Combine(AppPaths.CoversDir, hash + ".jpg");
                if (File.Exists(path))
                {
                    var file = await StorageFile.GetFileFromPathAsync(path);
                    updater.Thumbnail = RandomAccessStreamReference.CreateFromFile(file);
                }
            }

            updater.Update();
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "SMTC 元数据推送失败");
        }
    }

    private void PushState()
    {
        if (_smtc is null) return;
        try
        {
            _smtc.PlaybackStatus = _player.IsPlaying ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "SMTC 状态推送失败");
        }
    }

    private void PushPosition()
    {
        if (_smtc is null || !_player.HasTrack) return;
        try
        {
            var duration = Math.Max(1.0, _player.DurationSeconds);
            _smtc.UpdateTimelineProperties(new SystemMediaTransportControlsTimelineProperties
            {
                Position = TimeSpan.FromSeconds(Math.Min(_player.PositionSeconds, duration)),
                EndTime = TimeSpan.FromSeconds(duration),
                MinSeekTime = TimeSpan.Zero,
                MaxSeekTime = TimeSpan.FromSeconds(duration)
            });
            _lastPositionPush = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "SMTC 进度推送失败");
        }
    }

    private void PushStopped()
    {
        if (_smtc is null) return;
        try
        {
            // 停止时清掉陈旧元数据，避免浮窗残留上一首（审查修复）
            var updater = _smtc.DisplayUpdater;
            updater.Type = MediaPlaybackType.Music;
            updater.MusicProperties.Title = string.Empty;
            updater.MusicProperties.Artist = string.Empty;
            updater.MusicProperties.AlbumTitle = string.Empty;
            updater.Update();
            _smtc.PlaybackStatus = MediaPlaybackStatus.Stopped;
        }
        catch { }
    }

    private void OnButtonPressed(SystemMediaTransportControls sender, SystemMediaTransportControlsButtonPressedEventArgs args)
    {
        if (_disposed) return;
        var dispatcher = Application.Current?.Dispatcher;
        switch (args.Button)
        {
            // 语义区分（审查修复）：Play 只在暂停时起播，Pause 只在播放中暂停，
            // 避免播放中再按 Play 被当成切换
            case SystemMediaTransportControlsButton.Play:
                dispatcher?.BeginInvoke(() =>
                {
                    if (!_player.IsPlaying) _player.PlayPauseCommand.Execute(null);
                });
                break;
            case SystemMediaTransportControlsButton.Pause:
                dispatcher?.BeginInvoke(() =>
                {
                    if (_player.IsPlaying) _player.PlayPauseCommand.Execute(null);
                });
                break;
            case SystemMediaTransportControlsButton.Next:
                dispatcher?.BeginInvoke(() => _player.NextCommand.Execute(null));
                break;
            case SystemMediaTransportControlsButton.Previous:
                dispatcher?.BeginInvoke(() => _player.PreviousCommand.Execute(null));
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_smtc is not null)
        {
            _smtc.ButtonPressed -= OnButtonPressed;
            _smtc.IsEnabled = false;
        }
        _player.PropertyChanged -= OnPlayerPropertyChanged;
    }
}
