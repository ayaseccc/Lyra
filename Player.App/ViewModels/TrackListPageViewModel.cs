using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Player.Core.Infra;
using Player.Core.Library;

namespace Player.App.ViewModels;

/// <summary>右键「添加到歌单」子菜单的一项。</summary>
public sealed record PlaylistMenuItem(string Name, IRelayCommand<PlaylistRecord?> Command, PlaylistRecord Playlist);

/// <summary>曲目列表的一行（UI-R2）：平铺与分组共用；分组模式下 ShowCover 仅首行 true。</summary>
public sealed class TrackRowItem
{
    public required TrackRecord Track { get; init; }

    /// <summary>分组模式：该行是组内第一行，左侧显示整组封面。</summary>
    public bool ShowCover { get; init; }

    public bool IsGroupHeader => false;
}

/// <summary>专辑分组头（UI-R2）：专辑名 | 艺术家 ———— 年份（年份右对齐）。</summary>
public sealed class GroupHeaderItem
{
    public required string AlbumText { get; init; }

    public required string ArtistText { get; init; }

    public required string YearText { get; init; }

    public bool IsGroupHeader => true;

    /// <summary>供「当前播放」MultiBinding 空安全使用（组头没有曲目）。</summary>
    public TrackRecord? Track => null;
}

/// <summary>
/// 曲目列表页：全部歌曲 / 某个歌单 / 某个文件夹虚拟歌单 / 某张专辑 / 某位艺术家 都用它。
/// 过滤与排序全在内存里做（万级无压力），过滤输入做 200ms 去抖避免每敲一下就重算。
///
/// 注意：页面上所有命令都必须挂在**本 VM 自己**身上。DataTemplate 里用
/// {RelativeSource AncestorType=Window} 去够 ShellViewModel 的命令并不可靠，
/// 右键菜单更是独立的弹出视觉树、根本够不到主窗口 —— P1.1 的"点击无反应"就是这么来的。
/// </summary>
public sealed partial class TrackListPageViewModel : ObservableObject
{
    private readonly Action<IReadOnlyList<TrackRecord>, int, string> _playRequested;
    private readonly DispatcherTimer _filterDebounce;

    private IReadOnlyList<TrackRecord> _selectedTracks = Array.Empty<TrackRecord>();
    private string _appliedFilter = string.Empty;
    private string? _sortProperty;
    private ListSortDirection _sortDirection = ListSortDirection.Ascending;

    public TrackListPageViewModel(
        string title,
        IEnumerable<TrackRecord> tracks,
        string sourceName,
        Action<IReadOnlyList<TrackRecord>, int, string> playRequested)
    {
        Title = title;
        SourceName = sourceName;
        _playRequested = playRequested;

        Items = new ObservableCollection<TrackRecord>(tracks);
        View = CollectionViewSource.GetDefaultView(Items);
        View.Filter = FilterPredicate;

        _isGrouped = ConfigService.Current.Ui.ListGrouped;

        _filterDebounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _filterDebounce.Tick += OnFilterDebounceTick;

        RebuildDisplay();
    }

    public string Title { get; }

    public string SourceName { get; }

    public ObservableCollection<TrackRecord> Items { get; }

    public ICollectionView View { get; }

    /// <summary>列表实际显示的行（UI-R2）：平铺 = 全部 TrackRowItem；分组 = 组头 + 曲目行。保持扁平结构以维持虚拟化。</summary>
    public ObservableCollection<object> DisplayItems { get; } = new();

    /// <summary>显示模式：true = 专辑分组，false = 平铺（UI-R2，选择持久化）。</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ViewModeToolTip))]
    private bool _isGrouped;

    public string ViewModeToolTip => IsGrouped ? "切换为平铺列表" : "切换为专辑分组";

    /// <summary>当前显示顺序的曲目（双击播放、定位用）。</summary>
    public IReadOnlyList<TrackRecord> DisplayTracks =>
        DisplayItems.OfType<TrackRowItem>().Select(i => i.Track).ToList();

    /// <summary>平铺/分组切换（UI-R2）。</summary>
    [RelayCommand]
    private void ToggleViewMode()
    {
        IsGrouped = !IsGrouped;
        ConfigService.Current.Ui.ListGrouped = IsGrouped;
        ConfigService.Save();
        RebuildDisplay();
    }

    /// <summary>按当前模式重建显示行。分组用 TrackGrouper（纯函数），平铺用过滤/排序后的 View。</summary>
    private void RebuildDisplay()
    {
        DisplayItems.Clear();
        if (IsGrouped)
        {
            foreach (var group in TrackGrouper.Group(View.Cast<TrackRecord>()))
            {
                DisplayItems.Add(new GroupHeaderItem
                {
                    AlbumText = group.Album,
                    ArtistText = group.Artist,
                    YearText = group.Year
                });
                for (var i = 0; i < group.Tracks.Count; i++)
                    DisplayItems.Add(new TrackRowItem { Track = group.Tracks[i], ShowCover = i == 0 });
            }
        }
        else
        {
            foreach (var track in View.Cast<TrackRecord>())
                DisplayItems.Add(new TrackRowItem { Track = track });
        }
    }

    /// <summary>手工歌单 id；非歌单页面为 null。</summary>
    public long? PlaylistId { get; init; }

    public bool CanEdit => PlaylistId is not null;

    public bool IsPlaylistPage => PlaylistId is not null;

    public bool IsLibraryPage => PlaylistId is null;

    /// <summary>右键「添加到歌单」的候选歌单，由 Shell 在建页时传进来。</summary>
    public IReadOnlyList<PlaylistRecord> PlaylistTargets { get; init; } = Array.Empty<PlaylistRecord>();

    /// <summary>
    /// 右键菜单直接用的条目：命令与参数都已经绑在实例上，
    /// 菜单里因此**不需要任何 RelativeSource 查找**（右键菜单是独立弹出树，查找不可靠）。
    /// </summary>
    public IReadOnlyList<PlaylistMenuItem> PlaylistMenuItems =>
        _playlistMenuItems ??= PlaylistTargets
            .Select(p => new PlaylistMenuItem(p.Name, AddToPlaylistCommand, p))
            .ToList();

    public bool HasPlaylistTargets => PlaylistTargets.Count > 0;

    private IReadOnlyList<PlaylistMenuItem>? _playlistMenuItems;

    /// <summary>从专辑/艺术家页钻进来时显示的返回按钮文案。</summary>
    public string? BackTitle { get; init; }

    public IRelayCommand? BackCommand { get; init; }

    public bool HasBack => BackCommand is not null;

    // ---------------- Shell 注入的回调 ----------------

    /// <summary>歌单内容变了要回写数据库（拖拽排序、移除条目）。</summary>
    public Action<IReadOnlyList<TrackRecord>>? ItemsReordered { get; init; }

    /// <summary>按落点插入（页内拖动 / 从别处拖进来）。</summary>
    public Action<int, IReadOnlyList<TrackRecord>>? InsertRequested { get; init; }

    public Action<PlaylistRecord, IReadOnlyList<TrackRecord>>? AddToPlaylistRequested { get; init; }

    public Action<TrackListPageViewModel>? ExportRequested { get; init; }

    /// <summary>空曲库时的「添加音乐文件夹」。</summary>
    public Action? AddLibraryFolderRequested { get; init; }

    /// <summary>空歌单时的「添加文件到歌单」。</summary>
    public Action<TrackListPageViewModel>? AddFilesRequested { get; init; }

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private TrackRecord? _selectedTrack;

    public IReadOnlyList<TrackRecord> SelectedTracks => _selectedTracks;

    public string Subtitle
    {
        get
        {
            var totalMs = Items.Sum(t => t.DurationMs);
            var span = TimeSpan.FromMilliseconds(totalMs);
            var duration = span.TotalHours >= 1
                ? $"{(int)span.TotalHours} 小时 {span.Minutes} 分"
                : $"{span.Minutes} 分 {span.Seconds} 秒";
            return $"{Items.Count} 首 · {duration}";
        }
    }

    public bool IsEmpty => Items.Count == 0;

    public bool ShowLibraryEmptyState => IsEmpty && IsLibraryPage;

    public bool ShowPlaylistEmptyState => IsEmpty && IsPlaylistPage;

    // ---------------- 过滤与排序 ----------------

    partial void OnFilterTextChanged(string value)
    {
        _filterDebounce.Stop();
        _filterDebounce.Start();
    }

    private void OnFilterDebounceTick(object? sender, EventArgs e)
    {
        _filterDebounce.Stop();
        _appliedFilter = FilterText.Trim().ToLowerInvariant();
        View.Refresh();
        RebuildDisplay();
    }

    private bool FilterPredicate(object item)
    {
        if (_appliedFilter.Length == 0) return true;
        return item is TrackRecord track && track.SearchKey.Contains(_appliedFilter, StringComparison.Ordinal);
    }

    /// <summary>点列头排序：同一列再点一次换升降序。</summary>
    public void SortBy(string propertyName)
    {
        if (string.IsNullOrEmpty(propertyName)) return;

        _sortDirection = _sortProperty == propertyName && _sortDirection == ListSortDirection.Ascending
            ? ListSortDirection.Descending
            : ListSortDirection.Ascending;
        _sortProperty = propertyName;

        using (View.DeferRefresh())
        {
            View.SortDescriptions.Clear();
            View.SortDescriptions.Add(new SortDescription(propertyName, _sortDirection));
        }
        RebuildDisplay();
    }

    // ---------------- 选择与播放 ----------------

    /// <summary>由列表控件同步当前多选状态（WPF 的 SelectedItems 不能直接绑定）。</summary>
    public void SetSelection(IEnumerable<TrackRecord> tracks)
    {
        _selectedTracks = tracks.ToList();
        AddToPlaylistCommand.NotifyCanExecuteChanged();
    }

    /// <summary>拖动时的负载：拖的行在选区里就带上整个选区，否则只带这一行。</summary>
    public IReadOnlyList<TrackRecord> GetDragPayload(TrackRecord row)
    {
        if (_selectedTracks.Any(t => ReferenceEquals(t, row)))
            return _selectedTracks;
        return new[] { row };
    }

    [RelayCommand]
    private void PlayAll()
    {
        var ordered = DisplayTracks;
        if (ordered.Count == 0) return;
        _playRequested(ordered, 0, SourceName);
    }

    [RelayCommand]
    private void PlaySelected()
    {
        if (SelectedTrack is null) return;
        Play(SelectedTrack);
    }

    /// <summary>双击某行：以当前可见顺序（分组模式下即碟号/曲号顺序）作为播放列表，从这一首开始。</summary>
    public void Play(TrackRecord track)
    {
        var ordered = DisplayTracks.ToList();
        var index = ordered.FindIndex(t => ReferenceEquals(t, track));
        if (index < 0) index = 0;
        _playRequested(ordered, index, SourceName);
    }

    /// <summary>定位正在播放的曲目：清掉过滤、按路径找到同一条并选中（UI-R1.5 ⑪）。</summary>
    public void LocateTrack(TrackRecord track)
    {
        _filterDebounce.Stop();
        if (_appliedFilter.Length > 0)
        {
            _appliedFilter = string.Empty;
            FilterText = string.Empty;
            View.Refresh();
        }

        var match = DisplayTracks.FirstOrDefault(t =>
            string.Equals(t.Path, track.Path, StringComparison.OrdinalIgnoreCase));
        if (match is null) return;

        SelectedTrack = match;
    }

    // ---------------- 歌单编辑 ----------------

    [RelayCommand]
    private void AddToPlaylist(PlaylistRecord? playlist)
    {
        if (playlist is null) return;

        var tracks = _selectedTracks.Count > 0
            ? _selectedTracks
            : SelectedTrack is null ? Array.Empty<TrackRecord>() : new[] { SelectedTrack };

        // 一首都没选时也照样回调，让 Shell 在状态栏提示"先选中歌曲"，
        // 而不是静默无反应 —— 静默正是这一轮要消灭的观感
        AddToPlaylistRequested?.Invoke(playlist, tracks);
    }

    [RelayCommand]
    private void RemoveSelected()
    {
        if (!CanEdit) return;

        var removing = _selectedTracks.Count > 0
            ? _selectedTracks.ToList()
            : SelectedTrack is null ? new List<TrackRecord>() : new List<TrackRecord> { SelectedTrack };

        if (removing.Count == 0) return;

        foreach (var track in removing)
            Items.Remove(track);

        ItemsReordered?.Invoke(Items.ToList());
        NotifyItemsChanged();
        RebuildDisplay();
    }

    [RelayCommand]
    private void ExportM3u() => ExportRequested?.Invoke(this);

    [RelayCommand]
    private void AddLibraryFolder() => AddLibraryFolderRequested?.Invoke();

    [RelayCommand]
    private void AddFiles() => AddFilesRequested?.Invoke(this);

    /// <summary>按落点插入一批曲目（页内拖动、从别的列表拖入、从资源管理器拖入）。</summary>
    public void RequestInsert(int index, IReadOnlyList<TrackRecord> tracks)
    {
        if (!CanEdit || tracks.Count == 0) return;
        InsertRequested?.Invoke(index, tracks);
    }

    /// <summary>算出落点索引：落在某一行上就用它的位置，落在空白处（或找不到）就追加到末尾。</summary>
    public int IndexOfRow(TrackRecord? row)
    {
        if (row is null) return Items.Count;

        var index = Items.IndexOf(row);
        return index < 0 ? Items.Count : index;
    }

    /// <summary>视图正被排序/过滤/分组时，可见顺序与底层顺序对不上，落点索引没有意义（分组模式一律追加）。</summary>
    public bool IsViewSortedOrFiltered =>
        IsGrouped || View.SortDescriptions.Count > 0 || _appliedFilter.Length > 0;

    private void NotifyItemsChanged()
    {
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(ShowLibraryEmptyState));
        OnPropertyChanged(nameof(ShowPlaylistEmptyState));
    }
}
