using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Player.App.Infra;
using Player.App.Theming;
using Player.Core.Audio;
using Player.Core.Infra;
using Player.Core.Library;
using Player.Core.Lyrics;
using Player.Core.Online;
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
    private readonly LyricsService _lyrics;
    private readonly ChkszClient _client;

    private bool _isSeeking;
    private int _activeSeekRevision;
    private double? _pendingSeekTarget;
    private int _pendingSeekRevision;
    private DateTime _seekGuardUntil;
    private DateTime _lastTrackStartedAt;
    private int _consecutiveQuickEnds;
    private bool _disposed;
    private CancellationTokenSource? _previewCts;
    private long _previewGeneration;
    private CancellationTokenSource? _visualCts;
    private long _visualGeneration;

    /// <summary>连续跳过坏文件的上限。曲库所在盘掉线时不能拿一万首在 UI 线程上硬试。</summary>
    private const int MaxSkipAttempts = 10;

    public PlayerViewModel(IPlaybackEngine engine, LyricsService lyrics, ChkszClient client)
    {
        _engine = engine;
        _lyrics = lyrics;
        _client = client;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        Volume = Math.Clamp(ConfigService.Current.Ui.Volume, 0, 1);
        _engine.Volume = Volume;

        // UI-R4：侧栏折叠状态持久化
        IsSidePaneVisible = ConfigService.Current.Ui.SidePaneOpen;

        PlayMode = Enum.TryParse<PlayMode>(ConfigService.Current.Ui.PlayMode, out var mode)
            ? mode
            : PlayMode.RepeatAll;
        _list.Mode = PlayMode;

        _engine.TrackOpened += OnTrackOpened;
        _engine.StateChanged += OnStateChanged;
        _engine.TrackEnded += OnTrackEnded;
        _engine.TrackTransitioned += OnTrackTransitioned;
        _engine.OutputChanged += OnOutputChanged;
        _engine.ErrorOccurred += OnErrorOccurred;

        Lyrics = new LyricsViewModel(_lyrics, this);

        // 输出设置在这里只是"记下"，真正开设备要等第一次播放（见 PlaybackEngine 注释）
        _engine.ApplyOutputSettings(ConfigService.Current.Output.ToSettings());
        RefreshOutputState();

        _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _timer.Tick += OnTimerTick;
        _timer.Start();
    }

    public PlaybackList List => _list;

    /// <summary>歌词覆盖层（点击底部封面展开）。</summary>
    public LyricsViewModel Lyrics { get; private set; } = null!;

    /// <summary>右侧信息栏/歌词栏可见（UI-R1 可折叠）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SidePaneWidth))]
    private bool _isSidePaneVisible = true;

    [RelayCommand]
    private void ToggleSidePane()
    {
        IsSidePaneVisible = !IsSidePaneVisible;
        // UI-R4：折叠状态持久化
        ConfigService.Current.Ui.SidePaneOpen = IsSidePaneVisible;
        ConfigService.Save();
    }

    /// <summary>右侧栏列宽：折叠时归零（UI-R1）。</summary>
    public System.Windows.GridLength SidePaneWidth =>
        IsSidePaneVisible ? new System.Windows.GridLength(280) : new System.Windows.GridLength(0);

    /// <summary>当前播放的曲目，供列表页高亮用。</summary>
    public TrackRecord? CurrentTrack => _list.Current;

    /// <summary>专辑名（UI-R4：右侧信息栏 艺术家 | 专辑）。</summary>
    [ObservableProperty]
    private string _album = string.Empty;

    /// <summary>艺术家 | 专辑（UI-R4）。</summary>
    public string ArtistAndAlbum =>
        string.IsNullOrWhiteSpace(Album) ? Artist : $"{Artist} | {Album}";

    /// <summary>制作信息一行（作词 · 作曲 · 编曲，UI-R4；空 = 标签里没有）。</summary>
    [ObservableProperty]
    private string _credits = string.Empty;

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
    [NotifyPropertyChangedFor(nameof(ProgressPercent))]
    private bool _hasTrack;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionText))]
    [NotifyPropertyChangedFor(nameof(ProgressPercent))]
    private double _positionSeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationText))]
    [NotifyPropertyChangedFor(nameof(ProgressPercent))]
    private double _durationSeconds = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumePercentText))]
    [NotifyPropertyChangedFor(nameof(VolumeLevel))]
    [NotifyPropertyChangedFor(nameof(VolumeDbText))]
    private double _volume = 0.6;

    /// <summary>P4 在线试听：源注册表（按条目所属源取流）；试听不写歌单/队列，切走即结束。</summary>
    private Player.Core.Online.OnlineSources? _onlineSources;

    /// <summary>当前是否处于在线试听（临时播放态）。</summary>
    private bool _isOnlinePreview;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayModeIcon))]
    [NotifyPropertyChangedFor(nameof(PlayModeText))]
    [NotifyPropertyChangedFor(nameof(IsSequentialMode))]
    [NotifyPropertyChangedFor(nameof(IsRepeatAllMode))]
    [NotifyPropertyChangedFor(nameof(IsRepeatOneMode))]
    [NotifyPropertyChangedFor(nameof(IsShuffleMode))]
    private PlayMode _playMode = PlayMode.RepeatAll;

    public string PositionText => FormatTime(HasTrack ? PositionSeconds : 0);

    public string DurationText => FormatTime(HasTrack ? DurationSeconds : 0);

    public string VolumePercentText => ((int)Math.Round(Volume * 100)) + "%";

    /// <summary>播放进度百分比（迷你窗进度线用）。</summary>
    public double ProgressPercent
    {
        get
        {
            return HasTrack && DurationSeconds > 0
                ? Math.Clamp(PositionSeconds * 100 / DurationSeconds, 0, 100)
                : 0;
        }
    }

    /// <summary>Acquire one reference-counted mixer DSP tap lease for the visible mini surface.</summary>
    public IDisposable AcquireSpectrum() => _engine.AcquireSpectrum();

    /// <summary>Copy the latest fixed 16 spectrum levels without allocating.</summary>
    public bool TryCopySpectrum(Span<float> destination) => _engine.TryCopySpectrum(destination);

    /// <summary>音量方块的亮起个数（0..10，UI-R1.5 反馈）。</summary>
    public int VolumeLevel => (int)Math.Round(Volume * 10);

    /// <summary>拖动音量时短暂显示的 dB 值：0dB=100%，-100dB=静音。</summary>
    public string VolumeDbText
    {
        get
        {
            if (Volume <= 0.0001) return "-100 dB";
            return Math.Max(-100, 20 * Math.Log10(Volume)).ToString("0") + " dB";
        }
    }

    /// <summary>拖动音量期间显示 dB 反馈，松开 1 秒后自动隐藏。</summary>
    [ObservableProperty]
    private bool _isVolumeFeedbackVisible;

    private DispatcherTimer? _volumeFeedbackTimer;

    /// <summary>拖动音量方块（点击/滑动）：连续设音量并显示 dB 文字。</summary>
    public void SetVolumeFromDrag(double fraction)
    {
        Volume = Math.Clamp(fraction, 0, 1);
        IsVolumeFeedbackVisible = true;
    }

    /// <summary>松手：dB 文字 1 秒后消失。</summary>
    public void EndVolumeDrag()
    {
        _volumeFeedbackTimer?.Stop();
        _volumeFeedbackTimer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _volumeFeedbackTimer.Tick += (_, _) =>
        {
            _volumeFeedbackTimer.Stop();
            IsVolumeFeedbackVisible = false;
        };
        _volumeFeedbackTimer.Start();
    }

    /// <summary>播放条上的输出指示，如「ASIO · TOPPING E1x2 · 96000 Hz · 缓冲 256 samples」。</summary>
    [ObservableProperty]
    private string _outputDescription = string.Empty;

    /// <summary>音量 100% 且没有重采样时为真，界面提示"位完美"。</summary>
    [ObservableProperty]
    private bool _isBitPerfect;

    /// <summary>窗口标题栏（UI-R1）：「标题 - 艺术家 | 格式 | 位深 | 码率 | 采样率」。</summary>
    [ObservableProperty]
    private string _windowTitle = "Lyra";

    public string OutputHint => IsBitPerfect
        ? "位完美输出（音量 100%，未重采样）"
        : Math.Abs(Volume - 1.0) > 0.0001
            ? "音量不是 100%，输出经过了软件衰减"
            : "输出经过重采样（采样率与源文件不一致）";

    /// <summary>输出徽章悬停提示：当前输出 + 位完美状态。</summary>
    public string OutputToolTip => $"{OutputBadgeText}\n{OutputHint}";

    /// <summary>播放条上的小徽章文案，如「WASAPI 96k」（UI-R1.5 ⑫）。</summary>
    public string OutputBadgeText
    {
        get
        {
            var backend = _engine.ActiveBackend switch
            {
                OutputBackendKind.Asio => "ASIO",
                OutputBackendKind.Wasapi => "WASAPI",
                _ => "系统输出"
            };
            var rate = _engine.OutputSampleRate;
            return rate > 0
                ? $"{backend} {(rate / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)}k"
                : backend;
        }
    }

    /// <summary>输出设备切换菜单的条目（UI-R1.5 ⑫ 扩展：列出所有后端的设备）。</summary>
    public sealed record OutputDeviceItem(OutputBackendKind Kind, string Name, bool IsCurrent)
    {
        public string DisplayName => (Kind switch
        {
            OutputBackendKind.Asio => "ASIO",
            OutputBackendKind.Wasapi => "WASAPI",
            _ => "系统输出"
        }) + " · " + Name;
    }

    public ObservableCollection<OutputDeviceItem> OutputDevices { get; } = new();

    private void RefreshOutputState()
    {
        RefreshOutputInfo();
        RefreshOutputDevices();
    }

    private void RefreshOutputInfo()
    {
        OutputDescription = _engine.OutputDescription;
        IsBitPerfect = _engine.IsBitPerfect;
        OnPropertyChanged(nameof(OutputHint));
        OnPropertyChanged(nameof(OutputBadgeText));
        OnPropertyChanged(nameof(OutputToolTip));
    }

    private void RefreshOutputDevices()
    {
        var current = _engine.OutputSettings.DeviceName;
        var active = _engine.ActiveBackend;
        OutputDevices.Clear();

        foreach (var kind in new[]
                 {
                     OutputBackendKind.Asio, OutputBackendKind.Wasapi, OutputBackendKind.DirectSound
                 })
        {
            foreach (var device in _engine.EnumerateDevices(kind))
            {
                OutputDevices.Add(new OutputDeviceItem(
                    kind,
                    device.Name,
                    kind == active &&
                    string.Equals(device.Name, current, StringComparison.OrdinalIgnoreCase)));
            }
        }
    }

    /// <summary>从徽章菜单直接切换输出后端/设备（UI-R1.5 ⑫ / R2 修复：失败也刷新重同步勾选）。</summary>
    [RelayCommand]
    private void SwitchOutputDevice(OutputDeviceItem? device)
    {
        if (device is null) return;

        Log.Information("尝试切换输出：{Kind} · {Name}", device.Kind, device.Name);

        var settings = _engine.OutputSettings.Clone();
        settings.Backend = device.Kind;
        settings.DeviceName = device.Name;

        var ok = _engine.ApplyOutputSettings(settings);
        ConfigService.Current.Output.CopyFrom(settings);
        ConfigService.Save();

        // 无论成败都重建设备列表：菜单勾选以引擎实际状态为准（修多选）
        RefreshOutputState();

        StatusText = ok
            ? "输出：" + device.DisplayName
            : "切换输出失败，已保持当前输出（详见日志）";
    }

    private void OnOutputChanged(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(RefreshOutputState);

    /// <summary>无缝衔接已经发生：引擎自己换到了预载好的下一曲，这里把列表游标和界面追上去。</summary>
    private void OnTrackTransitioned(object? sender, PlaybackTrackEventArgs e) => _dispatcher.BeginInvoke(() =>
    {
        // END notifications are asynchronous. A user seek/stop/open may have made
        // this completion obsolete while it waited for the UI dispatcher.
        if (!IsCurrentPlaybackEvent(e)) return;

        var path = e.Path;
        if (string.IsNullOrWhiteSpace(path)) return;

        var next = _list.PeekNext();
        if (next is not null && string.Equals(next.Path, path, StringComparison.OrdinalIgnoreCase))
        {
            _list.MoveNext(userInitiated: false);
        }
        else
        {
            // 预载之后用户可能改了列表/模式，游标对不上就按路径找回来，
            // 否则后续 PeekNext 会一直基于错误位置，界面和实际播放永久错位
            var matched = _list.Items.FirstOrDefault(t =>
                string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase));

            if (matched is not null) _list.MoveToTrack(matched);
            else Log.Warning("无缝切到了不在当前列表里的曲目：{Path}", path);
        }

        var track = _list.Current;
        if (track is not null && string.Equals(track.Path, path, StringComparison.OrdinalIgnoreCase))
        {
            ApplyTrackDisplay(track);
            BumpPlayCount(track);
        }

        // 无缝路径不会触发 TrackOpened，技术信息要在这里自己刷新
        if (_engine.CurrentTrack is { } info) TechnicalInfo = info.TechnicalSummary;

        _lastTrackStartedAt = DateTime.UtcNow;
        _consecutiveQuickEnds = 0;
        DurationSeconds = _engine.Duration.TotalSeconds > 0 ? _engine.Duration.TotalSeconds : 1;

        // The audio hand-off precedes this dispatcher callback. A seek started or
        // completed on B during that delay belongs to B and must not be erased by
        // the late transition UI reset. Pending state from A is discarded.
        var activeSeekIsForThisTrack = _isSeeking && e.HasTrackIdentity(_activeSeekRevision);
        var pendingSeekIsForThisTrack = _pendingSeekTarget.HasValue &&
                                        e.HasTrackIdentity(_pendingSeekRevision);
        if (!pendingSeekIsForThisTrack) ClearPendingSeek();
        if (!activeSeekIsForThisTrack && !pendingSeekIsForThisTrack)
            PositionSeconds = Math.Clamp(_engine.Position.TotalSeconds, 0, DurationSeconds);

        RefreshOutputInfo();
        RefreshWindowTitle();
        OnPropertyChanged(nameof(CurrentTrack));
        StatusText = "无缝衔接：" + (track?.DisplayTitle ?? Path.GetFileNameWithoutExtension(path));
    });

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

    // ---------------- 播放模式图标组（UI-R1：四键直接选） ----------------

    public bool IsSequentialMode => PlayMode == PlayMode.Sequential;

    public bool IsRepeatAllMode => PlayMode == PlayMode.RepeatAll;

    public bool IsRepeatOneMode => PlayMode == PlayMode.RepeatOne;

    public bool IsShuffleMode => PlayMode == PlayMode.Shuffle;

    [RelayCommand]
    private void SetPlayMode(PlayMode mode)
    {
        // UI-R1.5 反馈：模式切换不再在状态栏刷文字，界面只看按钮图标变化
        PlayMode = mode;
        // 重复点击当前项时 PlayMode 不会变化，强制通知一次，让菜单勾选与按钮图标重新同步
        OnPropertyChanged(nameof(PlayMode));
    }

    /// <summary>左键点击：按 顺序 → 列表循环 → 单曲循环 → 随机 循环切换（UI-R1.5 反馈）。</summary>
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
        OnPropertyChanged(nameof(PlayMode));
    }

    /// <summary>音量是 UI → 引擎的单向写入，定时器不回写（P0.1）。</summary>
    partial void OnVolumeChanged(double value)
    {
        _engine.Volume = value;
        ConfigService.Current.Ui.Volume = value;
        IsBitPerfect = _engine.IsBitPerfect;
        OnPropertyChanged(nameof(OutputHint));
    }

    partial void OnPlayModeChanged(PlayMode value)
    {
        _list.Mode = value;
        ConfigService.Current.Ui.PlayMode = value.ToString();

        // 换了模式，之前预载的"下一曲"可能已经不是下一曲了
        _engine.ClearPreload();
    }

    // ---------------- P4 在线试听 ----------------

    /// <summary>注入在线源注册表（App 启动时设置）。</summary>
    public void SetOnlineSources(Player.Core.Online.OnlineSources sources) => _onlineSources = sources;

    /// <summary>是否正在在线试听（UI 显示「试听」角标用）。</summary>
    public bool IsOnlinePreview => _isOnlinePreview;

    /// <summary>
    /// 在线试听：取流 → URL 临时流 → 播放。不写任何歌单/队列；
    /// 切到本地曲目（PlayTracks/PlayTrack）即结束临时态。
    /// </summary>
    public async Task<bool> PlayOnlinePreviewAsync(Player.Core.Online.OnlineTrack track, string sourceKey, int preferredBr)
    {
        var previewCts = BeginPreviewRequest();
        var generation = Volatile.Read(ref _previewGeneration);
        var source = _onlineSources?.Get(sourceKey);
        if (source is null)
        {
            Interlocked.CompareExchange(ref _previewCts, null, previewCts);
            previewCts.Dispose();
            StatusText = "在线源不可用";
            return false;
        }

        try
        {
            var stream = await source.GetStreamAsync(track, preferredBr, previewCts.Token).ConfigureAwait(true);
            if (previewCts.IsCancellationRequested || generation != Volatile.Read(ref _previewGeneration) || _disposed)
                return false;
            if (!stream.Success)
            {
                StatusText = "试听失败：" + stream.Error;
                return false;
            }

            _isOnlinePreview = true;
            OnPropertyChanged(nameof(IsOnlinePreview));
            if (!_engine.OpenUrl(stream.Data!.Url))
            {
                _isOnlinePreview = false;
                OnPropertyChanged(nameof(IsOnlinePreview));
                StatusText = "试听失败：无法打开音频流";
                return false;
            }

            // URL 流没有本地标签：覆盖展示元数据（OnTrackOpened 之后执行）
            Title = track.Name;
            Artist = track.ArtistLine;
            Album = track.Album;
            OnPropertyChanged(nameof(ArtistAndAlbum));
            TechnicalInfo = $"在线试听 · 实际 {QualityFormat.Br(stream.Data.ActualBr)}";
            CancelVisualLoad();
            CoverImage = null;
            Lyrics.Reset();
            RefreshWindowTitle();

            _engine.Play();
            StatusText = $"试听：{track.Name} · {track.ArtistLine}（{QualityFormat.Br(stream.Data.ActualBr)}）";
            return true;
        }
        catch (OperationCanceledException) when (previewCts.IsCancellationRequested || _disposed)
        {
            return false;
        }
        finally
        {
            if (ReferenceEquals(Volatile.Read(ref _previewCts), previewCts))
                Interlocked.CompareExchange(ref _previewCts, null, previewCts);
            previewCts.Dispose();
        }
    }

    /// <summary>切换到本地曲目时退出临时试听态（在 PlayTracks 开头调用）。</summary>
    private void ExitOnlinePreview()
    {
        Interlocked.Increment(ref _previewGeneration);
        var pending = Interlocked.Exchange(ref _previewCts, null);
        if (pending is not null)
        {
            try { pending.Cancel(); } catch (ObjectDisposedException) { }
        }

        if (!_isOnlinePreview) return;
        _isOnlinePreview = false;
        OnPropertyChanged(nameof(IsOnlinePreview));
        _engine.ClearPreload();
        Lyrics.Reset();
    }

    private CancellationTokenSource BeginPreviewRequest()
    {
        var next = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _previewCts, next);
        Interlocked.Increment(ref _previewGeneration);
        if (previous is not null)
        {
            try { previous.Cancel(); } catch (ObjectDisposedException) { }
        }
        return next;
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

        ExitOnlinePreview();   // P4：切到本地曲目即结束在线试听
        _list.Replace(tracks, sourceName, startIndex);
        PlayCurrentOrSkip();
    }

    public void PlayTrack(TrackRecord track, IReadOnlyList<TrackRecord> context, string sourceName)
    {
        var index = context.ToList().FindIndex(t => ReferenceEquals(t, track));
        PlayTracks(context, index < 0 ? 0 : index, sourceName);
    }

    /// <summary>「下一首播放」：选中曲目插到当前之后，**不打断当前播放**（播完当前曲自然轮到）；
    /// 队列空则直接起播。用户反馈：立即播放=「播放」，插队=「下一首播放」。</summary>
    public void PlayNextTracks(IReadOnlyList<TrackRecord> tracks, string sourceName)
    {
        if (tracks.Count == 0) return;
        ExitOnlinePreview();
        if (_list.Count == 0)
        {
            _list.Replace(tracks, sourceName, 0);
            PlayCurrentOrSkip();
            return;
        }
        _list.InsertAfterCurrent(tracks);

        // 当前曲最后 5 秒内可能已经预载了旧的下一曲。插队后必须撤销，
        // 让下一次进度 tick 按更新后的队列重新预载，否则自然续播会跳过插队曲。
        _engine.ClearPreload();
        StatusText = $"已将 {tracks.Count} 首插入「下一首播放」";
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
        // Stop is also the cancellation boundary for an in-flight preview URL
        // lookup. Otherwise a late network response can restart playback after the
        // user explicitly stopped it.
        ExitOnlinePreview();
        _engine.Stop();
        PositionSeconds = 0;
        ClearPendingSeek();
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

    // ---------------- 定位正在播放（UI-R1.5 ⑪） ----------------

    /// <summary>请求当前曲目列表滚动到正在播放的曲目（Shell 转发给页面 VM）。</summary>
    public event Action? LocateRequested;

    [RelayCommand]
    private void LocateCurrentTrack() => LocateRequested?.Invoke();

    // ---------------- 进度条（P0.1 修复后的时序） ----------------

    /// <summary>
    /// 用户开始操作进度条（鼠标按下 / 开始拖动）。可重复调用。
    /// 从这一刻起到 <see cref="EndSeek"/> 为止，定时器不再回写进度条。
    /// </summary>
    public void BeginSeek()
    {
        if (!_isSeeking)
            _activeSeekRevision = _engine.PlaybackRevision;
        _isSeeking = true;
    }

    /// <summary>
    /// 松手：**无论点击还是拖动，释放时必然执行一次 seek**（P1.1-②）。
    /// 之前这里用 _isSeeking 当门禁，而点击路径下 Slider 会吞掉 PreviewMouseLeftButtonDown，
    /// BeginSeek 从未执行 → EndSeek 直接返回 → 700ms 静默窗口过期后滑块被拉回旧位置。
    /// 现在按下/松开都用 handledEventsToo 的处理器接管，这里不再设门禁；
    /// 同一次操作可能被"鼠标松开"和"拖动结束"各调一次，seek 到同一位置是幂等的。
    /// </summary>
    public void EndSeek(double seconds)
    {
        var seekRevision = _activeSeekRevision != 0
            ? _activeSeekRevision
            : _engine.PlaybackRevision;
        _isSeeking = false;
        _activeSeekRevision = 0;

        if (!HasTrack) return;

        // A drag that began on A must not seek B if a sample-boundary transition
        // happened before mouse-up. A click path that did not call BeginSeek uses
        // the current revision and remains supported.
        if (seekRevision == 0 || seekRevision != _engine.PlaybackRevision)
        {
            ClearPendingSeek();
            PositionSeconds = Math.Clamp(_engine.Position.TotalSeconds, 0, DurationSeconds);
            return;
        }

        // 一次拖动会被"鼠标松开"和"拖动结束"各调一次，同一位置只真正 seek 一次，
        // 免得 BASS 多冲一次缓冲（可能有极轻微爆音）
        if (_pendingSeekTarget is { } pending &&
            _pendingSeekRevision == seekRevision &&
            Math.Abs(pending - seconds) < 0.05 &&
            DateTime.UtcNow < _seekGuardUntil)
            return;

        _engine.Seek(TimeSpan.FromSeconds(seconds));

        if (seekRevision != _engine.PlaybackRevision)
        {
            ClearPendingSeek();
            PositionSeconds = Math.Clamp(_engine.Position.TotalSeconds, 0, DurationSeconds);
            return;
        }

        // 乐观更新：立刻按目标值显示，不等下一个 tick 从引擎读回
        PositionSeconds = seconds;
        _pendingSeekTarget = seconds;
        _pendingSeekRevision = seekRevision;
        _seekGuardUntil = DateTime.UtcNow.AddMilliseconds(700);
    }

    private void ClearPendingSeek()
    {
        _pendingSeekTarget = null;
        _pendingSeekRevision = 0;
    }

    // ---------------- 内部 ----------------

    private void PlayCurrentOrSkip()
    {
        // 审查修复：Next/Prev/PlayPause 等所有切本地曲入口统一先退出在线试听态
        ExitOnlinePreview();
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
        Album = string.IsNullOrWhiteSpace(track.DisplayAlbum) ? string.Empty : track.DisplayAlbum;
        OnPropertyChanged(nameof(ArtistAndAlbum));
        CancelVisualLoad();
        CoverImage = null;
        Credits = string.Empty;
        ThemeService.OnTrackChanged(track.CoverHash);   // UI-R3：封面取色整体染色
        RefreshWindowTitle();

        // 记住上次播放的曲目（退出时随配置落盘，下次启动恢复，UI-R1.5 反馈）
        ConfigService.Current.Ui.LastTrackPath = track.Path;

        // P3：切歌即异步加载歌词（.lrc > 缓存 > 在线匹配），失败不影响播放
        _ = LoadTrackVisualsAsync(track);
        _ = Lyrics.LoadForTrackAsync(track);
    }

    private async Task LoadTrackVisualsAsync(TrackRecord track)
    {
        var cts = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _visualCts, cts);
        if (previous is not null)
        {
            try { previous.Cancel(); } catch (ObjectDisposedException) { }
        }
        var generation = Interlocked.Increment(ref _visualGeneration);

        try
        {
            var visuals = await Task.Run(() =>
            {
                cts.Token.ThrowIfCancellationRequested();
                var cover = CoverImageCache.GetLarge(track.CoverHash);
                cts.Token.ThrowIfCancellationRequested();
                var credits = Player.Core.Library.CreditReader.Read(track.Path).ToLine();
                return (cover, credits);
            }, cts.Token).ConfigureAwait(false);

            if (cts.IsCancellationRequested || _disposed || generation != Volatile.Read(ref _visualGeneration))
                return;

            await _dispatcher.InvokeAsync(() =>
            {
                if (_disposed || cts.IsCancellationRequested || generation != Volatile.Read(ref _visualGeneration)) return;
                if (!ReferenceEquals(_list.Current, track)
                    && !string.Equals(_list.Current?.Path, track.Path, StringComparison.OrdinalIgnoreCase)) return;
                CoverImage = visuals.cover;
                Credits = visuals.credits;
            });
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested || _disposed)
        {
        }
        finally
        {
            Interlocked.CompareExchange(ref _visualCts, null, cts);
            cts.Dispose();
        }
    }

    private void CancelVisualLoad()
    {
        Interlocked.Increment(ref _visualGeneration);
        var cts = Interlocked.Exchange(ref _visualCts, null);
        if (cts is null) return;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    /// <summary>启动时静默恢复上次播放的曲目：只加载信息与歌词，不发声（UI-R1.5 反馈）。</summary>
    public void RestoreTrack(TrackRecord track)
    {
        try
        {
            if (!_engine.Open(track.Path)) return;

            _list.Replace(new[] { track }, "上次播放", 0);
            ApplyTrackDisplay(track);
            OnPropertyChanged(nameof(CurrentTrack));
            StatusText = "已恢复上次播放的曲目";
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "恢复上次曲目失败：{Path}", track.Path);
        }
    }

    /// <summary>码率格式化（UI-R1.5）：小于 1000 显示 "3072 kbps"，更大显示 "3.0 Mbps"。</summary>
    private static string FormatBitrate(int kbps)
    {
        if (kbps < 1000) return kbps + " kbps";
        return (kbps / 1000.0).ToString("0.0") + " Mbps";
    }

    /// <summary>窗口标题（UI-R1）：标题 - 艺术家 | 格式 | 位深 | 码率 | 采样率。</summary>
    private void RefreshWindowTitle()
    {
        if (!HasTrack)
        {
            WindowTitle = "Lyra";
            return;
        }

        var info = _engine.CurrentTrack;
        var parts = new List<string>(4);
        if (!string.IsNullOrEmpty(info?.Format)) parts.Add(info.Format);
        if (info?.BitDepth is > 0) parts.Add(info.BitDepth + "bit");
        if (info?.Bitrate is > 0) parts.Add(FormatBitrate(info.Bitrate));
        if (info?.SampleRate is > 0) parts.Add((info.SampleRate / 1000.0).ToString("0.#") + "kHz");

        var suffix = parts.Count > 0 ? " | " + string.Join(" | ", parts) : string.Empty;
        WindowTitle = Artist.Length > 0
            ? $"{Title} - {Artist}{suffix}"
            : $"{Title}{suffix}";
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
        // 自愈：IsMoveToPointEnabled 的点击路径下 Slider 不做鼠标捕获，
        // 用户若在滑条上按下、把鼠标移开再松手，释放事件收不到，
        // _isSeeking 会一直挂着导致进度条永久冻结 —— 发现左键已松开就解除接管。
        if (_isSeeking && System.Windows.Input.Mouse.LeftButton == System.Windows.Input.MouseButtonState.Released)
        {
            _isSeeking = false;
            _activeSeekRevision = 0;
        }

        if (_isSeeking || !HasTrack) return;
        if (_engine.State != PlayerState.Playing) return;

        var enginePosition = _engine.Position.TotalSeconds;

        if (_pendingSeekTarget is { } target)
        {
            if (_pendingSeekRevision == _engine.PlaybackRevision)
            {
                var caughtUp = Math.Abs(enginePosition - target) <= 1.0;
                if (!caughtUp && DateTime.UtcNow < _seekGuardUntil) return;
            }

            ClearPendingSeek();
        }

        PositionSeconds = enginePosition;

        TryPreloadNext();
    }

    /// <summary>PLAN 第 4 节：下一曲提前 5 秒预创建解码流，采样率一致时可做到样本级无缝。</summary>
    private void TryPreloadNext()
    {
        if (!SeamlessPolicy.ShouldPreload(PositionSeconds, DurationSeconds, _engine.IsNextSeamless)) return;

        var next = _list.PeekNext();
        if (next is null) return;

        _engine.PreloadNext(next.Path);
    }

    // 引擎只提供技术参数与时长；标题/艺术家一律以媒体库标签为准
    private void OnTrackOpened(object? sender, TrackInfo info)
    {
        var revision = _engine.PlaybackRevision;
        _dispatcher.BeginInvoke(() =>
        {
            if (revision != _engine.PlaybackRevision ||
                !ReferenceEquals(info, _engine.CurrentTrack))
                return;

            TechnicalInfo = info.TechnicalSummary;
            DurationSeconds = info.Duration.TotalSeconds > 0 ? info.Duration.TotalSeconds : 1;
            HasTrack = true;

            var activeSeekIsForThisTrack = _isSeeking && _activeSeekRevision == revision;
            var pendingSeekIsForThisTrack = _pendingSeekTarget.HasValue &&
                                            _pendingSeekRevision == revision;
            if (!pendingSeekIsForThisTrack) ClearPendingSeek();
            if (!activeSeekIsForThisTrack && !pendingSeekIsForThisTrack)
                PositionSeconds = Math.Clamp(_engine.Position.TotalSeconds, 0, DurationSeconds);

            RefreshOutputInfo();
            RefreshWindowTitle();
        });
    }

    private void OnStateChanged(object? sender, PlayerState _) => _dispatcher.BeginInvoke(() =>
    {
        // State notifications may cross on the dispatcher when a user command
        // supersedes a queued natural END. Always project the engine's current
        // state instead of replaying a stale captured value.
        var currentState = _engine.State;
        IsPlaying = currentState == PlayerState.Playing;

        if (currentState == PlayerState.Stopped && _engine.CurrentTrack is null)
        {
            HasTrack = false;
            IsPlaying = false;
            PositionSeconds = 0;
            DurationSeconds = 1;
            Title = "未在播放";
            Artist = string.Empty;
            Album = string.Empty;
            Credits = string.Empty;
            OnPropertyChanged(nameof(ArtistAndAlbum));
            TechnicalInfo = string.Empty;
            CancelVisualLoad();
            CoverImage = null;
            RefreshWindowTitle();
            Lyrics.Reset();
        }
    });

    // 来自 BASS 回调线程，必须切回 UI 线程再换曲（换曲会释放旧流）
    private void OnTrackEnded(object? sender, PlaybackTrackEventArgs e) => _dispatcher.BeginInvoke(() =>
    {
        // Do this check on the dispatcher, immediately before mutating the queue.
        // The engine performs the same check on its worker; this second check covers
        // controls issued after publication but before the dispatcher callback runs.
        if (!IsCurrentPlaybackEvent(e)) return;

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

    private bool IsCurrentPlaybackEvent(PlaybackTrackEventArgs e)
    {
        return !_disposed && _engine.IsPlaybackEventCurrent(e);
    }

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

        Interlocked.Increment(ref _previewGeneration);
        var previewCts = Interlocked.Exchange(ref _previewCts, null);
        if (previewCts is not null)
        {
            try { previewCts.Cancel(); } catch (ObjectDisposedException) { }
            // The in-flight preview owns disposal in its finally block.
        }
        CancelVisualLoad();
        _timer.Stop();
        _timer.Tick -= OnTimerTick;

        _engine.TrackOpened -= OnTrackOpened;
        _engine.StateChanged -= OnStateChanged;
        _engine.TrackEnded -= OnTrackEnded;
        _engine.TrackTransitioned -= OnTrackTransitioned;
        _engine.OutputChanged -= OnOutputChanged;
        _engine.ErrorOccurred -= OnErrorOccurred;

        Lyrics.Dispose();
        ConfigService.Save();
    }
}
