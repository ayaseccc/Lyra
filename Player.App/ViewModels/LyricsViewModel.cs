using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Player.App.Views;
using Player.Core.Library;
using Player.Core.Lyrics;
using Player.Core.Online;
using Serilog;

namespace Player.App.ViewModels;

/// <summary>歌词页显示的一行（原文 + 可选的翻译/罗马音副行）。</summary>
public sealed class LyricDisplayLine : System.ComponentModel.INotifyPropertyChanged
{
    public required TimeSpan Time { get; init; }

    public required string PrimaryText { get; init; }

    private string _secondaryText = string.Empty;

    /// <summary>翻译/罗马音副行。切换显示模式时原地更新（不重建集合，滚动位置不受影响）。</summary>
    public string SecondaryText
    {
        get => _secondaryText;
        set
        {
            if (_secondaryText == value) return;
            _secondaryText = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(SecondaryText)));
        }
    }

    public string TimeText => Time.TotalHours >= 1
        ? Time.ToString(@"h:mm:ss")
        : Time.ToString(@"m:ss");

    private bool _isCurrent;

    /// <summary>当前播放行（高亮）。由 LyricsViewModel 按播放位置更新。</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value) return;
            _isCurrent = value;
            PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(IsCurrent)));
        }
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
}

public enum LyricDisplayMode
{
    Original,
    Bilingual,
    Romaji
}

/// <summary>
/// 歌词覆盖层（PLAN 第 8 节）：点击底部封面展开。大封面 + 滚动歌词 + 当前行高亮居中、
/// 点击某行跳转、原文/双语/罗马音切换、偏移微调（±0.1s，持久化）、手动重新匹配。
/// 加载与刷新全部异步，任何在线失败都只是"未找到"，绝不影响播放。
/// </summary>
public sealed partial class LyricsViewModel : ObservableObject, IDisposable
{
    private readonly LyricsService _lyrics;
    private readonly PlayerViewModel _player;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _timer;

    private int _loadVersion;
    private TrackRecord? _track;
    private LyricDocument _document = LyricDocument.Empty;
    private TimeSpan _effectiveOffset;
    private int _highlightedIndex = -1;
    private bool _disposed;

    public LyricsViewModel(LyricsService lyrics, PlayerViewModel player)
    {
        _lyrics = lyrics;
        _player = player;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(400)
        };
        _timer.Tick += OnTimerTick;
        _timer.Start();
    }

    public ObservableCollection<LyricDisplayLine> Lines { get; } = new();

    [ObservableProperty]
    private bool _isOpen;

    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>顶部来源描述，如「在线 · 已匹配网易云」。空串表示还没有内容。</summary>
    [ObservableProperty]
    private string _sourceText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowTranslation))]
    [NotifyPropertyChangedFor(nameof(ShowRomaji))]
    [NotifyPropertyChangedFor(nameof(ModeText))]
    [NotifyPropertyChangedFor(nameof(IsOriginalMode))]
    [NotifyPropertyChangedFor(nameof(IsBilingualMode))]
    [NotifyPropertyChangedFor(nameof(IsRomajiMode))]
    private LyricDisplayMode _displayMode = LyricDisplayMode.Bilingual;

    [ObservableProperty]
    private int _currentLineIndex = -1;

    [ObservableProperty]
    private string _offsetText = "偏移 0.0 秒";

    public bool ShowTranslation => DisplayMode == LyricDisplayMode.Bilingual;

    public bool ShowRomaji => DisplayMode == LyricDisplayMode.Romaji;

    /// <summary>右键菜单单选标记（P3.1-③ 交互改版）。</summary>
    public bool IsOriginalMode => DisplayMode == LyricDisplayMode.Original;

    public bool IsBilingualMode => DisplayMode == LyricDisplayMode.Bilingual;

    public bool IsRomajiMode => DisplayMode == LyricDisplayMode.Romaji;

    // ---------------- 歌词来源偏好（右键菜单「歌词来源」，按曲目持久化） ----------------

    public LyricPreference SourcePreference =>
        _track is null ? LyricPreference.Auto : _lyrics.GetPreference(_track.Path);

    public bool IsPrefAuto => SourcePreference == LyricPreference.Auto;

    public bool IsPrefLrcFile => SourcePreference == LyricPreference.LrcFile;

    public bool IsPrefEmbedded => SourcePreference == LyricPreference.Embedded;

    public bool IsPrefOnline => SourcePreference == LyricPreference.Online;

    /// <summary>右键菜单选择来源：保存偏好并立即按新来源重新加载。</summary>
    [RelayCommand]
    private void SetSourcePreference(LyricPreference preference)
    {
        if (_track is null) return;

        _lyrics.SetPreference(_track.Path, preference);
        NotifyPreferenceChanged();
        _ = LoadForTrackAsync(_track);
    }

    private void NotifyPreferenceChanged()
    {
        OnPropertyChanged(nameof(SourcePreference));
        OnPropertyChanged(nameof(IsPrefAuto));
        OnPropertyChanged(nameof(IsPrefLrcFile));
        OnPropertyChanged(nameof(IsPrefEmbedded));
        OnPropertyChanged(nameof(IsPrefOnline));
    }

    public string ModeText => DisplayMode switch
    {
        LyricDisplayMode.Original => "原文",
        LyricDisplayMode.Bilingual => "双语",
        _ => "罗马音"
    };

    public bool HasTimeline => _document.HasTimeline;

    /// <summary>有没有任何可显示的歌词内容（空状态提示用）。</summary>
    public bool HasLyrics => Lines.Count > 0 || !string.IsNullOrEmpty(PlainText);

    /// <summary>无时间轴歌词的整篇静态文本（PLAN 第 7.2 节降级路径）。</summary>
    public string PlainText { get; private set; } = string.Empty;

    /// <summary>本地 .lrc 优先于在线歌词，显示"重新获取"入口前要判断一下。</summary>
    public bool CanRefreshOnline => ChkszClient.HasApiKey && _track is not null;

    // ---------------- 打开/关闭 ----------------

    [RelayCommand]
    private void ToggleOpen() => IsOpen = !IsOpen;

    [RelayCommand]
    private void Close() => IsOpen = false;

    // ---------------- 加载 ----------------

    /// <summary>切歌时由 PlayerViewModel 调用。内部按版本号丢弃过期的加载结果。</summary>
    public async Task LoadForTrackAsync(TrackRecord track)
    {
        var version = ++_loadVersion;

        // 立即绑定新曲目并刷新菜单勾选（P3.1-④：否则切歌后菜单还显示上一首的来源偏好）
        _track = track;
        NotifyPreferenceChanged();

        StatusText = "加载歌词…";

        var result = await _lyrics.LoadForTrackAsync(track).ConfigureAwait(true);
        if (version != _loadVersion) return;

        ApplyResult(result);
    }

    /// <summary>手动「重新获取」：跳过缓存直接走 API（本地 .lrc 仍优先）。</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (_track is null) return;

        var version = ++_loadVersion;
        StatusText = "重新获取中…";

        var result = await _lyrics.RefreshFromOnlineAsync(_track).ConfigureAwait(true);
        if (version != _loadVersion) return;

        ApplyResult(result);
    }

    /// <summary>播放停止/无曲目时清空。</summary>
    public void Reset()
    {
        _loadVersion++;
        _track = null;
        _document = LyricDocument.Empty;
        Lines.Clear();
        StatusText = string.Empty;
        SourceText = string.Empty;
        OffsetText = "偏移 0.0 秒";
        CurrentLineIndex = -1;
        PlainText = string.Empty;
        OnPropertyChanged(nameof(PlainText));
        OnPropertyChanged(nameof(HasLyrics));
        _highlightedIndex = -1;
    }

    private void ApplyResult(LyricsLoadResult result)
    {
        _document = result.Document;
        _effectiveOffset = result.EffectiveOffset;
        PlainText = _document.PlainText;
        OnPropertyChanged(nameof(PlainText));
        _highlightedIndex = -1;
        RebuildLines();
        UpdateOffsetText();
        RefreshSourceText(result);
        UpdateCurrentLine();
        NotifyPreferenceChanged();
    }

    private void RefreshSourceText(LyricsLoadResult result)
    {
        if (_track is null) return;

        var matched = _lyrics.GetNeteaseId(_track.Path);
        var matchText = matched is { } id ? $" · 已匹配网易云 {id}" : string.Empty;

        SourceText = result.IsEmpty
            ? string.Empty
            : $"{result.SourceText}{matchText}";
    }

    // ---------------- 显示模式与行重建 ----------------

    [RelayCommand]
    private void CycleDisplayMode()
    {
        DisplayMode = DisplayMode switch
        {
            LyricDisplayMode.Original => LyricDisplayMode.Bilingual,
            LyricDisplayMode.Bilingual => LyricDisplayMode.Romaji,
            _ => LyricDisplayMode.Original
        };

        ApplyDisplayMode();
    }

    /// <summary>右键菜单直接选模式（P3.1-③）。</summary>
    [RelayCommand]
    private void SetDisplayMode(LyricDisplayMode mode)
    {
        if (DisplayMode == mode) return;

        DisplayMode = mode;
        ApplyDisplayMode();
    }

    /// <summary>
    /// 切换显示模式：**原地更新每行副文本，不重建集合**。
    /// 集合不变 → ListBox 滚动位置纹丝不动（修掉"切模式滑到底部"）。
    /// </summary>
    private void ApplyDisplayMode()
    {
        for (var i = 0; i < Lines.Count; i++)
        {
            var line = Lines[i];
            var document = _document.Lines;
            var secondary = i < document.Count ? SubTextFor(document[i]) : string.Empty;
            line.SecondaryText = secondary;
        }
    }

    private string SubTextFor(LyricLine line) => DisplayMode switch
    {
        LyricDisplayMode.Bilingual => line.Translation ?? string.Empty,
        LyricDisplayMode.Romaji => line.Romaji ?? string.Empty,
        _ => string.Empty
    };

    private void RebuildLines()
    {
        Lines.Clear();

        // 无时间轴的纯文本歌词也按行生成列表项（P3.1-④：排版与带时间轴统一，
        // 不再是一大段 PlainText；高亮跟随只在有时间轴时启用）
        foreach (var line in _document.Lines)
        {
            Lines.Add(new LyricDisplayLine
            {
                Time = line.Time,
                PrimaryText = line.Text,
                SecondaryText = SubTextFor(line)
            });
        }

        OnPropertyChanged(nameof(HasTimeline));
        OnPropertyChanged(nameof(HasLyrics));
    }

    // ---------------- 当前行跟随播放进度 ----------------

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (!IsOpen || !_document.HasTimeline || !_player.HasTrack) return;
        UpdateCurrentLine();
    }

    private void UpdateCurrentLine()
    {
        if (!_document.HasTimeline || Lines.Count == 0) return;

        // 正偏移 = 歌词提前：显示时把播放位置往后推
        var position = _player.PositionSeconds + _effectiveOffset.TotalSeconds;
        var index = _document.FindIndexAt(TimeSpan.FromSeconds(Math.Max(0, position)));
        if (index == CurrentLineIndex) return;

        CurrentLineIndex = index;

        // 高亮只动新旧两行，别在每 tick 里刷整个集合
        if (_highlightedIndex >= 0 && _highlightedIndex < Lines.Count)
            Lines[_highlightedIndex].IsCurrent = false;

        _highlightedIndex = index;
        if (index >= 0 && index < Lines.Count)
            Lines[index].IsCurrent = true;
    }

    /// <summary>点击某一行：跳到对应时间点。</summary>
    public void SeekToLine(int index)
    {
        if (index < 0 || index >= Lines.Count) return;
        _player.EndSeek(Lines[index].Time.TotalSeconds);
    }

    // ---------------- 偏移微调 ----------------

    [RelayCommand]
    private void OffsetEarlier() => AdjustOffset(-0.1);

    [RelayCommand]
    private void OffsetLater() => AdjustOffset(0.1);

    private void AdjustOffset(double seconds)
    {
        if (_track is null) return;

        var current = _lyrics.GetManualOffset(_track.Path) ?? TimeSpan.Zero;
        var next = current + TimeSpan.FromSeconds(seconds);
        next = TimeSpan.FromMilliseconds(Math.Round(next.TotalMilliseconds / 100.0) * 100.0);

        _lyrics.SetManualOffset(_track.Path, next);
        _effectiveOffset = _document.TagOffset + next;

        UpdateOffsetText();
        UpdateCurrentLine();
    }

    private void UpdateOffsetText()
    {
        var total = _effectiveOffset.TotalSeconds;
        OffsetText = Math.Abs(total) < 0.001
            ? "偏移 0.0 秒"
            : $"偏移 {(total > 0 ? "+" : string.Empty)}{total:0.0} 秒";
    }

    // ---------------- 手动重新匹配 ----------------

    [RelayCommand]
    private async Task RematchAsync()
    {
        if (_track is null) return;
        if (!ChkszClient.HasApiKey)
        {
            StatusText = "还没有填 API Key，无法在线匹配（设置页 → 在线）";
            return;
        }

        StatusText = "搜索候选…";

        var candidates = await _lyrics.FindCandidatesAsync(_track).ConfigureAwait(true);
        if (_track is null) return;

        if (candidates.Count == 0)
        {
            StatusText = "没有搜索到候选结果";
            return;
        }

        var choice = RematchDialog.Show(candidates, _track.DisplayTitle);
        if (choice is null) return;   // 取消

        if (choice.Id <= 0)
        {
            // 用户选择"清除匹配"
            _lyrics.ClearMatch(_track.Path);
            StatusText = "已清除匹配，将不再使用在线歌词";
            var result = await _lyrics.LoadForTrackAsync(_track).ConfigureAwait(true);
            ApplyResult(result);
            return;
        }

        StatusText = "应用匹配…";
        var applied = await _lyrics.ApplyMatchAsync(_track, choice.Id).ConfigureAwait(true);
        ApplyResult(applied);

        if (!applied.IsEmpty)
            StatusText = $"已匹配：{choice.Name}";
        else
            StatusText = "已保存匹配，但这首歌暂时没有歌词";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
    }
}
