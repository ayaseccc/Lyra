using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Player.Core.Audio;
using Serilog;

namespace Player.App.ViewModels;

/// <summary>队列里的一行。</summary>
public sealed partial class QueueItemViewModel : ObservableObject
{
    public QueueItemViewModel(string path)
    {
        Path = path;
        Display = System.IO.Path.GetFileName(path);
    }

    public string Path { get; }

    public string Display { get; }

    [ObservableProperty]
    private bool _isCurrent;
}

/// <summary>
/// P0 主视图模型：把 Player.Core 的播放引擎与队列包装成界面可绑定的状态。
/// 引擎事件可能来自 BASS 线程，这里统一切回 UI 线程再更新。
/// </summary>
public sealed partial class PlayerViewModel : ObservableObject, IDisposable
{
    private readonly IPlaybackEngine _engine;
    private readonly PlaybackQueue _queue = new();
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;

    private bool _isSeeking;
    private bool _disposed;

    public PlayerViewModel(IPlaybackEngine engine)
    {
        _engine = engine;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        _engine.Volume = Volume;
        _engine.TrackOpened += OnTrackOpened;
        _engine.StateChanged += OnStateChanged;
        _engine.TrackEnded += OnTrackEnded;
        _engine.ErrorOccurred += OnErrorOccurred;

        EngineInfo = $"BASS {BassRuntime.BassVersion} · 输出 {BassRuntime.OutputDeviceName} · " +
                     $"格式插件 {BassRuntime.LoadedPlugins.Count} 个";

        _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _timer.Tick += OnTimerTick;
        _timer.Start();
    }

    public ObservableCollection<QueueItemViewModel> QueueItems { get; } = new();

    [ObservableProperty]
    private string _title = "未在播放";

    [ObservableProperty]
    private string _artist = string.Empty;

    [ObservableProperty]
    private string _technicalInfo = "拖入音频文件即可播放";

    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionText))]
    [NotifyPropertyChangedFor(nameof(DurationText))]
    private bool _hasTrack;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasQueue))]
    private bool _isQueueEmpty = true;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _engineInfo = string.Empty;

    [ObservableProperty]
    private int _selectedIndex = -1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionText))]
    private double _positionSeconds;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DurationText))]
    private double _durationSeconds = 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(VolumePercentText))]
    private double _volume = 0.6;

    public bool HasQueue => !IsQueueEmpty;

    public string PositionText => FormatTime(HasTrack ? PositionSeconds : 0);

    public string DurationText => FormatTime(HasTrack ? DurationSeconds : 0);

    public string VolumePercentText => ((int)Math.Round(Volume * 100)) + "%";

    partial void OnVolumeChanged(double value) => _engine.Volume = value;

    // ---------------- 命令 ----------------

    [RelayCommand]
    private void PlayPause()
    {
        if (!HasTrack)
        {
            if (_queue.Count == 0)
            {
                StatusText = "队列为空，先拖入或打开音频文件";
                return;
            }
            PlayIndex(Math.Max(0, _queue.CurrentIndex));
            return;
        }

        _engine.TogglePlayPause();
    }

    [RelayCommand]
    private void Stop()
    {
        _engine.Stop();
        PositionSeconds = 0;
    }

    [RelayCommand]
    private void Next()
    {
        if (_queue.Count == 0)
        {
            StatusText = "队列为空，先拖入或打开音频文件";
            return;
        }

        if (!_queue.HasNext)
        {
            StatusText = "已经是最后一首";
            return;
        }
        PlayIndex(_queue.CurrentIndex + 1);
    }

    [RelayCommand]
    private void Previous()
    {
        // 播放超过 3 秒时，"上一首"先回到本曲开头（常见播放器行为）
        if (HasTrack && PositionSeconds > 3)
        {
            _engine.Seek(TimeSpan.Zero);
            PositionSeconds = 0;
            return;
        }

        if (!_queue.HasPrevious)
        {
            _engine.Seek(TimeSpan.Zero);
            PositionSeconds = 0;
            return;
        }
        PlayIndex(_queue.CurrentIndex - 1);
    }

    [RelayCommand]
    private void OpenFiles()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择音频文件",
            Multiselect = true,
            Filter = AudioFormats.DialogFilter
        };

        if (dialog.ShowDialog() == true)
            LoadPaths(dialog.FileNames);
    }

    // ---------------- 外部调用（拖放 / 双击） ----------------

    /// <summary>拖入或打开一批路径：整批替换队列并从第一首开始播放。</summary>
    public void LoadPaths(IEnumerable<string> paths)
    {
        var files = ExpandPaths(paths);
        if (files.Count == 0)
        {
            StatusText = "没有找到可播放的音频文件";
            return;
        }

        _queue.Replace(files);
        RebuildQueueItems();
        StatusText = $"已加入 {files.Count} 个文件";
        PlayIndex(0);
    }

    public void PlayQueueItem(int index)
    {
        if (index < 0 || index >= _queue.Count) return;
        PlayIndex(index);
    }

    public void BeginSeek() => _isSeeking = true;

    public void EndSeek(double seconds)
    {
        _isSeeking = false;
        if (!HasTrack) return;
        _engine.Seek(TimeSpan.FromSeconds(seconds));
        PositionSeconds = _engine.Position.TotalSeconds;
    }

    // ---------------- 内部 ----------------

    private void PlayIndex(int index)
    {
        // 坏文件（损坏 / 缺插件）自动往后跳；用循环而不是递归，
        // 拖入一堆坏文件时才不会把调用栈越堆越深
        var target = index;

        while (true)
        {
            var path = _queue.MoveTo(target);
            if (path is null) return;

            if (_engine.Open(path))
            {
                _engine.Play();
                UpdateQueueHighlight();
                return;
            }

            if (!_queue.HasNext)
            {
                UpdateQueueHighlight();
                return;
            }

            target++;
        }
    }

    private void RebuildQueueItems()
    {
        QueueItems.Clear();
        foreach (var path in _queue.Items)
            QueueItems.Add(new QueueItemViewModel(path));
        IsQueueEmpty = QueueItems.Count == 0;
    }

    private void UpdateQueueHighlight()
    {
        for (var i = 0; i < QueueItems.Count; i++)
            QueueItems[i].IsCurrent = i == _queue.CurrentIndex;
        SelectedIndex = _queue.CurrentIndex;
    }

    private static List<string> ExpandPaths(IEnumerable<string> paths)
    {
        var result = new List<string>();

        foreach (var path in paths)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    // IgnoreInaccessible 必须开：拖入盘符根目录会遇到 System Volume Information
                    // 之类的无权限目录，默认设置直接抛异常，会导致整批文件被丢弃
                    var options = new EnumerationOptions
                    {
                        RecurseSubdirectories = true,
                        IgnoreInaccessible = true
                    };

                    result.AddRange(Directory
                        .EnumerateFiles(path, "*", options)
                        .Where(AudioFormats.IsSupported)
                        .OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
                }
                else if (File.Exists(path) && AudioFormats.IsSupported(path))
                {
                    result.Add(path);
                }
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "展开拖入路径失败：{Path}", path);
            }
        }

        return result;
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (_isSeeking || !HasTrack) return;
        if (_engine.State != PlayerState.Playing) return;
        PositionSeconds = _engine.Position.TotalSeconds;
    }

    private void OnTrackOpened(object? sender, TrackInfo track) => _dispatcher.BeginInvoke(() =>
    {
        Title = track.DisplayTitle;
        Artist = string.IsNullOrWhiteSpace(track.Artist) ? "未知艺术家" : track.Artist;
        TechnicalInfo = track.TechnicalSummary;
        DurationSeconds = track.Duration.TotalSeconds > 0 ? track.Duration.TotalSeconds : 1;
        PositionSeconds = 0;
        HasTrack = true;
        // 这里不清空 StatusText：跳过坏文件的提示要留给用户看见
    });

    private void OnStateChanged(object? sender, PlayerState state) => _dispatcher.BeginInvoke(() =>
    {
        IsPlaying = state == PlayerState.Playing;

        if (state == PlayerState.Stopped && _engine.CurrentTrack is null)
        {
            // 打开失败导致没有任何流：把播放条整体复位，
            // 否则会停留在上一首的标题上、而按钮已经无流可操作
            HasTrack = false;
            IsPlaying = false;
            PositionSeconds = 0;
            DurationSeconds = 1;
            Title = "未在播放";
            Artist = string.Empty;
            TechnicalInfo = "拖入音频文件即可播放";
        }
    });

    // 该事件来自 BASS 回调线程，必须切回 UI 线程后再操作流（释放/换曲）
    private void OnTrackEnded(object? sender, EventArgs e) => _dispatcher.BeginInvoke(() =>
    {
        if (_queue.HasNext)
        {
            PlayIndex(_queue.CurrentIndex + 1);
        }
        else
        {
            // 回卷到开头，保证界面显示的 0:00 与流的真实位置一致，再点播放才有声
            _engine.Stop();
            IsPlaying = false;
            PositionSeconds = 0;
            StatusText = "播放结束";
        }
    });

    private void OnErrorOccurred(object? sender, string message) =>
        _dispatcher.BeginInvoke(() => StatusText = message);

    private static string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) seconds = 0;
        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss")
            : time.ToString(@"m\:ss");
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
    }
}
