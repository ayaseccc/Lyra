using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Player.App.Infra;
using Player.Core.Audio;
using Player.Core.Infra;
using Player.Core.Library;
using Serilog;
using Wpf.Ui.Controls;

namespace Player.App.ViewModels;

/// <summary>
/// 底部播放条。P1 起播放列表由 <see cref="PlaybackList"/> 承载（取代 P0 的 PlaybackQueue），
/// 曲目信息优先用媒体库里的标签，技术参数仍取自 BASS 打开流后的真实值。
/// </summary>
public sealed partial class PlayerViewModel : ObservableObject, IDisposable
{
    private readonly IPlaybackEngine _engine;
    private readonly PlaybackList _list = new();
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;

    private bool _isSeeking;
    private double? _pendingSeekTarget;
    private DateTime _seekGuardUntil;
    private DateTime _lastTrackStartedAt;
    private int _consecutiveQuickEnds;
    private bool _disposed;

    /// <summary>连续跳过坏文件的上限。曲库所在盘掉线时不能拿一万首在 UI 线程上硬试。</summary>
    private const int MaxSkipAttempts = 10;

    public PlayerViewModel(IPlaybackEngine engine)
    {
        _engine = engine;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        Volume = Math.Clamp(ConfigService.Current.Ui.Volume, 0, 1);
        _engine.Volume = Volume;

        PlayMode = Enum.TryParse<PlayMode>(ConfigService.Current.Ui.PlayMode, out var mode)
            ? mode
            : PlayMode.RepeatAll;
        _list.Mode = PlayMode;

        _engine.TrackOpened += OnTrackOpened;
        _engine.StateChanged += OnStateChanged;
        _engine.TrackEnded += OnTrackEnded;
        _engine.ErrorOccurred += OnErrorOccurred;

        _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _timer.Tick += OnTimerTick;
        _timer.Start();
    }

    public PlaybackList List => _list;

    /// <summary>当前播放的曲目，供列表页高亮用。</summary>
    public TrackRecord? CurrentTrack => _list.Current;

    [ObservableProperty]
    private string _title = "未在播放";

    [ObservableProperty]
    private string _artist = string.Empty;

    [ObservableProperty]
    private string _technicalInfo = "拖入音频文件或先在设置里添加音乐文件夹";

    [ObservableProperty]
    private ImageSource? _coverImage;

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionText))]
    [NotifyPropertyChangedFor(nameof(DurationText))]
    private bool _hasTrack;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionText))]
    private double _positionSeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationText))]
    private double _durationSeconds = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumePercentText))]
    private double _volume = 0.6;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayModeIcon))]
    [NotifyPropertyChangedFor(nameof(PlayModeText))]
    private PlayMode _playMode = PlayMode.RepeatAll;

    public string PositionText => FormatTime(HasTrack ? PositionSeconds : 0);

    public string DurationText => FormatTime(HasTrack ? DurationSeconds : 0);

    public string VolumePercentText => ((int)Math.Round(Volume * 100)) + "%";

    public SymbolRegular PlayModeIcon => PlayMode switch
    {
        PlayMode.Sequential => SymbolRegular.ArrowRight24,
        PlayMode.RepeatAll => SymbolRegular.ArrowRepeatAll24,
        PlayMode.RepeatOne => SymbolRegular.ArrowRepeat124,
        PlayMode.Shuffle => SymbolRegular.ArrowShuffle24,
        _ => SymbolRegular.ArrowRepeatAll24
    };

    public string PlayModeText => PlayMode switch
    {
        PlayMode.Sequential => "顺序播放",
        PlayMode.RepeatAll => "列表循环",
        PlayMode.RepeatOne => "单曲循环",
        PlayMode.Shuffle => "随机播放",
        _ => "列表循环"
    };

    /// <summary>音量是 UI → 引擎的单向写入，定时器不回写（P0.1）。</summary>
    partial void OnVolumeChanged(double value)
    {
        _engine.Volume = value;
        ConfigService.Current.Ui.Volume = value;
    }

    partial void OnPlayModeChanged(PlayMode value)
    {
        _list.Mode = value;
        ConfigService.Current.Ui.PlayMode = value.ToString();
    }

    // ---------------- 对外播放入口 ----------------

    /// <summary>用一批曲目替换播放列表并从指定位置开始播放。</summary>
    public void PlayTracks(IReadOnlyList<TrackRecord> tracks, int startIndex, string sourceName)
    {
        if (tracks.Count == 0)
        {
            StatusText = "这个列表是空的";
            return;
        }

        _list.Replace(tracks, sourceName, startIndex);
        PlayCurrentOrSkip();
    }

    public void PlayTrack(TrackRecord track, IReadOnlyList<TrackRecord> context, string sourceName)
    {
        var index = context.ToList().FindIndex(t => ReferenceEquals(t, track));
        PlayTracks(context, index < 0 ? 0 : index, sourceName);
    }

    // ---------------- 命令 ----------------

    [RelayCommand]
    private void PlayPause()
    {
        if (!HasTrack)
        {
            if (_list.Count == 0)
            {
                StatusText = "还没有可播放的内容";
                return;
            }
            PlayCurrentOrSkip();
            return;
        }

        _engine.TogglePlayPause();
    }

    [RelayCommand]
    private void Stop()
    {
        _engine.Stop();
        PositionSeconds = 0;
        _pendingSeekTarget = null;
    }

    [RelayCommand]
    private void Next()
    {
        if (_list.Count == 0)
        {
            StatusText = "还没有可播放的内容";
            return;
        }

        if (_list.MoveNext(userInitiated: true) is null)
        {
            StatusText = "已经是最后一首";
            return;
        }

        PlayCurrentOrSkip();
    }

    [RelayCommand]
    private void Previous()
    {
        // 播放超过 3 秒时先回到本曲开头，这是常见播放器的习惯
        if (HasTrack && PositionSeconds > 3)
        {
            _engine.Seek(TimeSpan.Zero);
            PositionSeconds = 0;
            return;
        }

        if (_list.MovePrevious() is null)
        {
            _engine.Seek(TimeSpan.Zero);
            PositionSeconds = 0;
            return;
        }

        PlayCurrentOrSkip();
    }

    [RelayCommand]
    private void CyclePlayMode()
    {
        PlayMode = PlayMode switch
        {
            PlayMode.Sequential => PlayMode.RepeatAll,
            PlayMode.RepeatAll => PlayMode.RepeatOne,
            PlayMode.RepeatOne => PlayMode.Shuffle,
            _ => PlayMode.Sequential
        };
        StatusText = PlayModeText;
    }

    // ---------------- 进度条（P0.1 修复后的时序） ----------------

    /// <summary>
    /// 用户开始操作进度条（鼠标按下 / 开始拖动）。可重复调用。
    /// 从这一刻起到 <see cref="EndSeek"/> 为止，定时器不再回写进度条。
    /// </summary>
    public void BeginSeek() => _isSeeking = true;

    /// <summary>
    /// 松手：**无论点击还是拖动，释放时必然执行一次 seek**（P1.1-②）。
    /// 之前这里用 _isSeeking 当门禁，而点击路径下 Slider 会吞掉 PreviewMouseLeftButtonDown，
    /// BeginSeek 从未执行 → EndSeek 直接返回 → 700ms 静默窗口过期后滑块被拉回旧位置。
    /// 现在按下/松开都用 handledEventsToo 的处理器接管，这里不再设门禁；
    /// 同一次操作可能被"鼠标松开"和"拖动结束"各调一次，seek 到同一位置是幂等的。
    /// </summary>
    public void EndSeek(double seconds)
    {
        _isSeeking = false;

        if (!HasTrack) return;

        _engine.Seek(TimeSpan.FromSeconds(seconds));

        // 乐观更新：立刻按目标值显示，不等下一个 tick 从引擎读回
        PositionSeconds = seconds;
        _pendingSeekTarget = seconds;
        _seekGuardUntil = DateTime.UtcNow.AddMilliseconds(700);
    }

    // ---------------- 内部 ----------------

    private void PlayCurrentOrSkip()
    {
        var attempts = Math.Min(MaxSkipAttempts, Math.Max(1, _list.Count));

        for (var i = 0; i < attempts; i++)
        {
            var track = _list.Current;
            if (track is null) return;

            if (_engine.Open(track.Path))
            {
                _engine.Play();
                ApplyTrackDisplay(track);
                BumpPlayCount(track);
                _lastTrackStartedAt = DateTime.UtcNow;
                OnPropertyChanged(nameof(CurrentTrack));
                return;
            }

            // 打开失败（文件被删/格式插件缺失）——跳到下一首继续试
            if (_list.MoveNext(userInitiated: true) is null) break;
        }

        StatusText = "连续多个文件都打不开，已停止（详见 data/logs）";
        OnPropertyChanged(nameof(CurrentTrack));
    }

    private void ApplyTrackDisplay(TrackRecord track)
    {
        Title = track.DisplayTitle;
        Artist = track.DisplayArtist;
        CoverImage = CoverImageCache.Get(track.CoverHash);
    }

    private static void BumpPlayCount(TrackRecord track)
    {
        if (track.Id <= 0) return;

        Task.Run(() =>
        {
            try { LibraryDb.IncrementPlayCount(track.Id); }
            catch (Exception ex) { Log.Debug(ex, "更新播放次数失败"); }
        });
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (_isSeeking || !HasTrack) return;
        if (_engine.State != PlayerState.Playing) return;

        var enginePosition = _engine.Position.TotalSeconds;

        if (_pendingSeekTarget is { } target)
        {
            var caughtUp = Math.Abs(enginePosition - target) <= 1.0;
            if (!caughtUp && DateTime.UtcNow < _seekGuardUntil) return;
            _pendingSeekTarget = null;
        }

        PositionSeconds = enginePosition;
    }

    // 引擎只提供技术参数与时长；标题/艺术家一律以媒体库标签为准
    private void OnTrackOpened(object? sender, TrackInfo info) => _dispatcher.BeginInvoke(() =>
    {
        TechnicalInfo = info.TechnicalSummary;
        DurationSeconds = info.Duration.TotalSeconds > 0 ? info.Duration.TotalSeconds : 1;
        PositionSeconds = 0;
        HasTrack = true;
        _pendingSeekTarget = null;
    });

    private void OnStateChanged(object? sender, PlayerState state) => _dispatcher.BeginInvoke(() =>
    {
        IsPlaying = state == PlayerState.Playing;

        if (state == PlayerState.Stopped && _engine.CurrentTrack is null)
        {
            HasTrack = false;
            IsPlaying = false;
            PositionSeconds = 0;
            DurationSeconds = 1;
            Title = "未在播放";
            Artist = string.Empty;
            TechnicalInfo = string.Empty;
            CoverImage = null;
        }
    });

    // 来自 BASS 回调线程，必须切回 UI 线程再换曲（换曲会释放旧流）
    private void OnTrackEnded(object? sender, EventArgs e) => _dispatcher.BeginInvoke(() =>
    {
        // 0 长度或立刻结束的文件会让"结束→下一首"在消息队列里空转，连着几首就停下来
        if (DateTime.UtcNow - _lastTrackStartedAt < TimeSpan.FromMilliseconds(600))
        {
            if (++_consecutiveQuickEnds >= 5)
            {
                _consecutiveQuickEnds = 0;
                _engine.Stop();
                IsPlaying = false;
                StatusText = "连续多首文件无法正常播放，已停止";
                return;
            }
        }
        else
        {
            _consecutiveQuickEnds = 0;
        }

        if (_list.MoveNext(userInitiated: false) is null)
        {
            _engine.Stop();
            IsPlaying = false;
            PositionSeconds = 0;
            StatusText = "播放结束";
            return;
        }

        PlayCurrentOrSkip();
    });

    private void OnErrorOccurred(object? sender, string message) =>
        _dispatcher.BeginInvoke(() => StatusText = message);

    private static string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) seconds = 0;
        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _timer.Stop();
        _timer.Tick -= OnTimerTick;

        _engine.TrackOpened -= OnTrackOpened;
        _engine.StateChanged -= OnStateChanged;
        _engine.TrackEnded -= OnTrackEnded;
        _engine.ErrorOccurred -= OnErrorOccurred;

        ConfigService.Save();
    }
}
