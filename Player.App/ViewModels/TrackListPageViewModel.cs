using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Player.Core.Library;

namespace Player.App.ViewModels;

/// <summary>
/// 曲目列表页：全部歌曲 / 某个歌单 / 某个文件夹虚拟歌单 / 某张专辑 / 某位艺术家 都用它。
/// 过滤与排序全在内存里做（万级无压力），过滤输入做 200ms 去抖避免每敲一下就重算。
/// </summary>
public sealed partial class TrackListPageViewModel : ObservableObject
{
    private readonly Action<IReadOnlyList<TrackRecord>, int, string> _playRequested;
    private readonly DispatcherTimer _filterDebounce;

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

        _filterDebounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _filterDebounce.Tick += OnFilterDebounceTick;
    }

    public string Title { get; }

    public string SourceName { get; }

    public ObservableCollection<TrackRecord> Items { get; }

    public ICollectionView View { get; }

    /// <summary>只有手工歌单允许拖拽排序与移除条目。</summary>
    public long? PlaylistId { get; init; }

    public bool CanEdit => PlaylistId is not null;

    /// <summary>
    /// 只有手工歌单、且当前既没有列排序也没有过滤时才允许拖拽排序 ——
    /// 否则用户看到的顺序和底层顺序对不上，拖完位置会错乱。
    /// </summary>
    public bool CanReorderNow =>
        CanEdit && View.SortDescriptions.Count == 0 && _appliedFilter.Length == 0;

    /// <summary>从专辑/艺术家页钻进来时显示的返回按钮文案。</summary>
    public string? BackTitle { get; init; }

    public IRelayCommand? BackCommand { get; init; }

    public bool HasBack => BackCommand is not null;

    /// <summary>歌单内容变了要回写数据库（拖拽排序、移除条目）。</summary>
    public Action<IReadOnlyList<TrackRecord>>? ItemsReordered { get; init; }

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private TrackRecord? _selectedTrack;

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
    }

    [RelayCommand]
    private void PlayAll()
    {
        var ordered = View.Cast<TrackRecord>().ToList();
        if (ordered.Count == 0) return;
        _playRequested(ordered, 0, SourceName);
    }

    [RelayCommand]
    private void PlaySelected()
    {
        if (SelectedTrack is null) return;
        Play(SelectedTrack);
    }

    /// <summary>双击某行：以当前可见顺序作为播放列表，从这一首开始。</summary>
    public void Play(TrackRecord track)
    {
        var ordered = View.Cast<TrackRecord>().ToList();
        var index = ordered.FindIndex(t => ReferenceEquals(t, track));
        if (index < 0) index = 0;
        _playRequested(ordered, index, SourceName);
    }

    [RelayCommand]
    private void RemoveSelected()
    {
        if (!CanEdit || SelectedTrack is null) return;

        Items.Remove(SelectedTrack);
        ItemsReordered?.Invoke(Items.ToList());
        OnPropertyChanged(nameof(Subtitle));
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>拖拽排序（仅手工歌单）。</summary>
    public void MoveItem(int fromIndex, int toIndex)
    {
        if (!CanEdit) return;
        if (fromIndex < 0 || fromIndex >= Items.Count) return;
        if (toIndex < 0 || toIndex >= Items.Count || fromIndex == toIndex) return;

        // 手工排序与列排序互斥，先清掉排序规则，否则拖完位置会被排序打回去
        if (View.SortDescriptions.Count > 0)
        {
            View.SortDescriptions.Clear();
            _sortProperty = null;
        }

        Items.Move(fromIndex, toIndex);
        ItemsReordered?.Invoke(Items.ToList());
    }
}
