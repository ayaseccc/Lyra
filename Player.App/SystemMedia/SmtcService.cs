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
    private readonly DispatcherTimer _metadataDebounceTimer;
    private readonly object _metadataGate = new();
    private CancellationTokenSource? _metadataCts;
    private long _metadataGeneration;
    private bool _disposed;

    /// <summary>
    /// 绑定窗口句柄创建 SMTC。失败直接抛出（带 HResult），由调用方决定重试时机；
    /// 关键点：GetForWindow 在窗口**显示前**调用会失败，必须等窗口真正可见后初始化
    /// （MainWindow 在 Loaded 里补建，失败则保持 null 以便重试）。
    /// </summary>
    public SmtcService(IntPtr hwnd, PlayerViewModel player)
    {
        _player = player;

        // GetForWindow is the expected failure point while the HWND is still
        // settling. Resolve and configure it before creating a DispatcherTimer;
        // otherwise each constructor retry would leave a timer holding the failed
        // partial instance alive.
        _smtc = SystemMediaTransportControlsInterop.GetForWindow(hwnd);
        _smtc.IsEnabled = true;
        _smtc.IsPlayEnabled = true;
        _smtc.IsPauseEnabled = true;
        _smtc.IsNextEnabled = true;
        _smtc.IsPreviousEnabled = player.CanGoPrevious;
        _smtc.ButtonPressed += OnButtonPressed;

        _metadataDebounceTimer = new DispatcherTimer(
            DispatcherPriority.Background,
            Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(40)
        };
        _metadataDebounceTimer.Tick += OnMetadataDebounceTick;

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
            case nameof(PlayerViewModel.IsOnlinePreview):
                if (_player.HasTrack) RequestMetadataPush();
                break;
            case nameof(PlayerViewModel.IsPlaying):
                PushState();
                break;
            case nameof(PlayerViewModel.CanGoPrevious):
                PushCapabilities();
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
        if (!_player.HasTrack)
        {
            PushStopped();
            return;
        }

        RequestMetadataPush();
        PushCapabilities();
        PushState();
        PushPosition();
    }

    private void PushCapabilities()
    {
        if (_smtc is null || _disposed) return;
        try
        {
            _smtc.IsPreviousEnabled = _player.CanGoPrevious;
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "SMTC 控制能力推送失败");
        }
    }

    private void RequestMetadataPush()
    {
        if (_smtc is null || _disposed || !_player.HasTrack) return;
        lock (_metadataGate) _metadataGeneration++;
        _metadataDebounceTimer.Stop();
        _metadataDebounceTimer.Start();
    }

    private void OnMetadataDebounceTick(object? sender, EventArgs e)
    {
        _metadataDebounceTimer.Stop();
        StartMetadataPush();
    }

    private void StartMetadataPush()
    {
        if (_smtc is null || _disposed || !_player.HasTrack) return;

        CancellationTokenSource? previous;
        CancellationTokenSource current;
        long generation;
        lock (_metadataGate)
        {
            generation = _metadataGeneration;
            previous = _metadataCts;
            current = _metadataCts = new CancellationTokenSource();
        }

        try { previous?.Cancel(); }
        catch (ObjectDisposedException) { }

        _ = PushMetadataAsync(generation, current);
    }

    private async Task PushMetadataAsync(long generation, CancellationTokenSource requestCts)
    {
        var cancellationToken = requestCts.Token;
        try
        {
            if (_smtc is null || !IsMetadataCurrent(generation)) return;

            // Snapshot all scalar metadata before the asynchronous cover lookup.
            // The commit below is guarded by the same generation, so a slow old
            // cover can never update SMTC after a newer track has arrived.
            var title = string.IsNullOrWhiteSpace(_player.Title) || _player.Title == "未在播放"
                ? "Lyra"
                : _player.Title;
            var artist = _player.Artist ?? string.Empty;
            var album = _player.Album ?? string.Empty;
            RandomAccessStreamReference? thumbnail = null;

            // Online preview metadata does not belong to PlaybackList.Current;
            // reusing that local row's hash would show the previous song's cover.
            var hash = _player.IsOnlinePreview ? null : _player.CurrentTrack?.CoverHash;
            if (!string.IsNullOrEmpty(hash))
            {
                var path = Path.Combine(AppPaths.CoversDir, hash + ".jpg");
                if (File.Exists(path))
                {
                    var file = await StorageFile.GetFileFromPathAsync(path);
                    cancellationToken.ThrowIfCancellationRequested();
                    thumbnail = RandomAccessStreamReference.CreateFromFile(file);
                }
            }

            if (!IsMetadataCurrent(generation)) return;

            var updater = _smtc.DisplayUpdater;
            updater.Type = MediaPlaybackType.Music;
            updater.MusicProperties.Title = title;
            updater.MusicProperties.Artist = artist;
            updater.MusicProperties.AlbumTitle = album;
            updater.Thumbnail = thumbnail;
            updater.Update();
        }
        catch (OperationCanceledException)
        {
            // A newer metadata request superseded this one.
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "SMTC 元数据推送失败");
        }
        finally
        {
            lock (_metadataGate)
            {
                if (ReferenceEquals(_metadataCts, requestCts))
                    _metadataCts = null;
            }
            requestCts.Dispose();
        }
    }

    private bool IsMetadataCurrent(long generation)
    {
        lock (_metadataGate)
            return !_disposed && _player.HasTrack && generation == _metadataGeneration;
    }

    private void CancelMetadataPush()
    {
        CancellationTokenSource? pending;
        lock (_metadataGate)
        {
            _metadataGeneration++;
            pending = _metadataCts;
            _metadataCts = null;
        }

        try { pending?.Cancel(); }
        catch (ObjectDisposedException) { }
        // PushMetadataAsync owns disposal after the pending WinRT call unwinds.
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
        _metadataDebounceTimer.Stop();
        CancelMetadataPush();
        try
        {
            // 停止时清掉陈旧元数据，避免浮窗残留上一首（审查修复）
            var updater = _smtc.DisplayUpdater;
            updater.Type = MediaPlaybackType.Music;
            updater.MusicProperties.Title = string.Empty;
            updater.MusicProperties.Artist = string.Empty;
            updater.MusicProperties.AlbumTitle = string.Empty;
            updater.Thumbnail = null;
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
        _metadataDebounceTimer.Stop();
        _metadataDebounceTimer.Tick -= OnMetadataDebounceTick;
        CancelMetadataPush();
        if (_smtc is not null)
        {
            _smtc.ButtonPressed -= OnButtonPressed;
            _smtc.IsEnabled = false;
        }
        _player.PropertyChanged -= OnPlayerPropertyChanged;
    }
}
