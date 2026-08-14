using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Player.App.Controls;
using Player.App.Views;
using Player.Core.Library;
using Player.Core.Lyrics;
using Player.Core.Online;
using Serilog;

namespace Player.App.ViewModels;

public enum LyricDisplayMode
{
    Original,
    Bilingual,
    Romaji
}

/// <summary>
/// 歌词数据与交互（UI-R0 起展示交给自绘控件 <see cref="LyricCanvas"/>）。
/// 数据层保留 P3 全部能力：三级来源（.lrc > 内嵌 > 缓存 > API）、网易云 ID 匹配、
/// 手动重新匹配、按曲目来源偏好、偏移微调；本类只输出渲染数据
/// （RenderLines / CurrentIndex / IsStatic），不碰任何 ItemsControl/模板/滚动控件。
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
    private bool _disposed;

    public LyricsViewModel(LyricsService lyrics, PlayerViewModel player)
    {
        _lyrics = lyrics;
        _player = player;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        // 播放进度 → 当前行（两个展示位置都跟随）
        _timer = new DispatcherTimer(DispatcherPriority.Background, _dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _timer.Tick += OnTimerTick;
        _timer.Start();
    }

    // ================= 渲染输出（自绘控件直接绑定） =================

    /// <summary>按当前显示模式生成的渲染行（Primary 主文本 + Secondary 副文本 + 时间点）。</summary>
    public IReadOnlyList<LyricRenderLine> RenderLines { get; private set; } = Array.Empty<LyricRenderLine>();

    /// <summary>当前播放行（-1 = 无）。自绘控件据此居中跟随并高亮。</summary>
    [ObservableProperty]
    private int _currentIndex = -1;

    /// <summary>无时间轴歌词：整篇静态显示（自绘控件不跟随不淡出）。</summary>
    public bool IsStatic => !_document.HasTimeline;

    /// <summary>有没有任何可显示的歌词内容（空状态提示用）。</summary>
    public bool HasLyrics => RenderLines.Count > 0;

    [ObservableProperty]
    private string _statusText = string.Empty;

    /// <summary>当前实际来源描述（右键菜单禁用项显示），如「在线 · 已匹配网易云 12345」。</summary>
    [ObservableProperty]
    private string _sourceText = string.Empty;

    [ObservableProperty]
    private string _offsetText = "偏移 0.0 秒";

    // ================= 显示模式 =================

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ModeText))]
    [NotifyPropertyChangedFor(nameof(IsOriginalMode))]
    [NotifyPropertyChangedFor(nameof(IsBilingualMode))]
    [NotifyPropertyChangedFor(nameof(IsRomajiMode))]
    private LyricDisplayMode _displayMode = LyricDisplayMode.Bilingual;

    public bool IsOriginalMode => DisplayMode == LyricDisplayMode.Original;

    public bool IsBilingualMode => DisplayMode == LyricDisplayMode.Bilingual;

    public bool IsRomajiMode => DisplayMode == LyricDisplayMode.Romaji;

    public string ModeText => DisplayMode switch
    {
        LyricDisplayMode.Original => "原文",
        LyricDisplayMode.Bilingual => "双语",
        _ => "罗马音"
    };

    /// <summary>右键菜单直接选模式：重建渲染行（数据小，重建无成本）。</summary>
    [RelayCommand]
    private void SetDisplayMode(LyricDisplayMode mode)
    {
        if (DisplayMode == mode) return;
        DisplayMode = mode;
        RebuildRenderLines();
    }

    [RelayCommand]
    private void CycleDisplayMode()
    {
        DisplayMode = DisplayMode switch
        {
            LyricDisplayMode.Original => LyricDisplayMode.Bilingual,
            LyricDisplayMode.Bilingual => LyricDisplayMode.Romaji,
            _ => LyricDisplayMode.Original
        };
        RebuildRenderLines();
    }

    // ================= 歌词来源偏好（右键菜单，按曲目持久化） =================

    public LyricPreference SourcePreference =>
        _track is null ? LyricPreference.Online : _lyrics.GetPreference(_track.Path);

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

    // ================= 加载 =================

    /// <summary>切歌时由 PlayerViewModel 调用。内部按版本号丢弃过期的加载结果。</summary>
    public async Task LoadForTrackAsync(TrackRecord track)
    {
        var version = ++_loadVersion;

        // 立即绑定新曲目并刷新菜单勾选（否则切歌后菜单还显示上一首的来源偏好）
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
        RenderLines = Array.Empty<LyricRenderLine>();
        CurrentIndex = -1;
        StatusText = string.Empty;
        SourceText = string.Empty;
        OffsetText = "偏移 0.0 秒";
        OnPropertyChanged(nameof(RenderLines));
        OnPropertyChanged(nameof(IsStatic));
        OnPropertyChanged(nameof(HasLyrics));
        OnPropertyChanged(nameof(LyricCreditsText));
    }

    private void ApplyResult(LyricsLoadResult result)
    {
        _document = result.Document;
        _effectiveOffset = result.EffectiveOffset;
        RebuildRenderLines();
        UpdateOffsetText();
        RefreshSourceText(result);
        UpdateCurrentIndex();
        NotifyPreferenceChanged();
        OnPropertyChanged(nameof(LyricCreditsText));
    }

    /// <summary>
    /// 歌词侧制作信息（UI-R5 ①）：流内剥离的元数据行 + LRC 头部元数据，合并去重。
    /// 作词/作曲/编曲/OP/ED，有才显示；标签里没有时才值得看这里。
    /// </summary>
    public string LyricCreditsText
    {
        get
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var parts = new List<string>(4);

            void Add(string key, string value)
            {
                var norm = LyricLayout.NormalizeMetadataKey(key);
                if (!seen.Add(norm + "\u0001" + value)) return;
                parts.Add(norm + " " + value);
            }

            // 流内剥离的元数据优先（更可靠）
            foreach (var (k, v) in _flowCredits) Add(k, v);

            // LRC 头部补位
            foreach (var key in new[] { "作词", "词", "lyricist", "作曲", "曲", "composer", "编曲", "编", "arranger", "OP", "ED", "制作", "混音", "母带" })
                if (_document.Header.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v))
                    Add(key, v);

            return string.Join(" · ", parts);
        }
    }

    private void RefreshSourceText(LyricsLoadResult result)
    {
        if (_track is null) return;

        _lastSource = result.Source;
        OnPropertyChanged(nameof(SourceDisplayText));
        SourceText = result.SourceText;
    }

    private LyricSource _lastSource = LyricSource.None;

    /// <summary>右键菜单里的只读来源行（UI-R1.5 ⑥）：「来源：网易云 · 缓存」，不带数字 ID。</summary>
    public string SourceDisplayText => _lastSource switch
    {
        LyricSource.LocalFile => "来源：本地 .lrc",
        LyricSource.Embedded => "来源：内嵌标签",
        LyricSource.Cache => "来源：网易云 · 缓存",
        LyricSource.Online => "来源：网易云",
        _ => string.Empty
    };

    private string SubTextFor(LyricLine line) => DisplayMode switch
    {
        LyricDisplayMode.Bilingual => line.Translation ?? string.Empty,
        LyricDisplayMode.Romaji => line.Romaji ?? string.Empty,
        _ => string.Empty
    };

    /// <summary>流内剥离出的元数据（作词/作曲/编曲/OP/ED 等），并入制作信息。</summary>
    private readonly List<(string Key, string Value)> _flowCredits = new();

    /// <summary>重建渲染行（加载完成 / 切换显示模式）。R5 ①：元数据行从时间流剥离。</summary>
    private void RebuildRenderLines()
    {
        _flowCredits.Clear();

        var units = new List<LyricRenderLine>(_document.Lines.Count);
        foreach (var line in _document.Lines)
        {
            // R5 ①：作词/作曲/编曲/OP/ED 等头部行不进歌词流、不参与当前行判定
            if (LyricLayout.TryParseMetadata(line.Text) is { } meta)
            {
                _flowCredits.Add((LyricLayout.NormalizeMetadataKey(meta.Key), meta.Value));
                continue;
            }

            units.Add(new LyricRenderLine
            {
                Time = line.Time,
                Primary = line.Text,
                Secondary = SubTextFor(line)
            });
        }

        RenderLines = units;

        OnPropertyChanged(nameof(RenderLines));
        OnPropertyChanged(nameof(IsStatic));
        OnPropertyChanged(nameof(HasLyrics));
        OnPropertyChanged(nameof(LyricCreditsText));
    }

    // ================= 当前行跟随播放进度 =================

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (!_document.HasTimeline || !_player.HasTrack) return;
        UpdateCurrentIndex();
    }

    private void UpdateCurrentIndex()
    {
        if (!_document.HasTimeline || RenderLines.Count == 0) return;

        // 正偏移 = 歌词提前：显示时把播放位置往后推。
        // R5 ①：元数据行已剥离，当前行索引在过滤后的单元上二分（开播第一句真实歌词前无高亮）。
        var position = _player.PositionSeconds + _effectiveOffset.TotalSeconds;
        var pos = TimeSpan.FromSeconds(Math.Max(0, position));
        var lines = RenderLines;
        var lo = 0;
        var hi = lines.Count - 1;
        var found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (lines[mid].Time <= pos)
            {
                found = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        if (found == CurrentIndex) return;
        CurrentIndex = found;
    }

    /// <summary>自绘控件点击某一行：跳到对应时间点。</summary>
    public void SeekToLine(int index)
    {
        if (index < 0 || index >= RenderLines.Count) return;
        _player.EndSeek(RenderLines[index].Time.TotalSeconds);
    }

    // ================= 偏移微调 =================

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
        UpdateCurrentIndex();
    }

    private void UpdateOffsetText()
    {
        var total = _effectiveOffset.TotalSeconds;
        OffsetText = Math.Abs(total) < 0.001
            ? "偏移 0.0 秒"
            : $"偏移 {(total > 0 ? "+" : string.Empty)}{total:0.0} 秒";
    }

    // ================= 手动重新匹配 =================

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
