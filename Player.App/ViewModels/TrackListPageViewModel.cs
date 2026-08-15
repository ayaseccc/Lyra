using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
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

    /// <summary>分组模式：该行是组内第一行（封面不降透明、专辑行显示年份）。</summary>
    public bool ShowCover { get; init; }

    /// <summary>所属分组键（分组模式折叠过滤用；平铺为空）。</summary>
    public string? GroupKey { get; init; }

    /// <summary>组年份（仅组首行有值，显示在专辑行右端）。</summary>
    public string YearText { get; init; } = string.Empty;

    public bool IsGroupHeader => false;
}

/// <summary>专辑分组头（UI-R2）：专辑名 | 艺术家 ———— 年份（年份右对齐）。L3.1：封面/折叠。</summary>
public sealed class GroupHeaderItem
{
    public required string AlbumText { get; init; }

    public required string ArtistText { get; init; }

    public required string YearText { get; init; }

    /// <summary>分组键（专辑+艺术家），折叠状态按它记。</summary>
    public required string GroupKey { get; init; }

    /// <summary>组内第一首的封面（组头封面缩略图，L3.1 开关控制显示）。</summary>
    public string? CoverHash { get; init; }

    public bool IsGroupHeader => true;

    /// <summary>折叠指示（▸ 折叠 / ▾ 展开）。</summary>
    public bool IsCollapsed { get; init; }

    /// <summary>供「当前播放」MultiBinding 空安全使用（组头没有曲目）。</summary>
    public TrackRecord? Track => null;
}

/// <summary>曲目列表列定义（L3.1 列自定义：显示/隐藏+顺序+列宽持久化）。</summary>
public sealed record TrackColumnDef(string Key, string Name, double DefaultWidth);

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

    /// <summary>折叠的分组键（会话内记住；初始按配置默认展开/折叠）。</summary>
    private readonly HashSet<string> _collapsedGroups = new(StringComparer.Ordinal);
    private bool _collapsedDefaultApplied;

    // ================= L3.1 列自定义 =================

    public static IReadOnlyList<TrackColumnDef> TrackColumns { get; } = new[]
    {
        new TrackColumnDef("Title", "标题", 500),
        new TrackColumnDef("Artist", "歌手", 150),
        new TrackColumnDef("Album", "专辑", 170),
        new TrackColumnDef("Duration", "时长", 64),
        new TrackColumnDef("Format", "格式", 60),
        new TrackColumnDef("SampleRate", "采样率", 80),
        new TrackColumnDef("BitDepth", "位深", 60),
        new TrackColumnDef("Bitrate", "码率", 80),
    };

    private double ColWidth(string key)
        => ConfigService.Current.Ui.ColumnWidths.TryGetValue(key, out var w) && w > 0 ? w : DefaultWidthOf(key);

    private int ColIndex(string key)
    {
        var cols = ConfigService.Current.Ui.Columns;
        var idx = cols.IndexOf(key);
        // 列 0 是封面列，数据列从 1 开始（实测：从 0 开始会堆到封面列，平铺宽 0 全不可见）
        return idx < 0 ? -1 : idx + 1;
    }

    private static double DefaultWidthOf(string key)
        => TrackColumns.FirstOrDefault(c => c.Key == key)?.DefaultWidth ?? 100;

    /// <summary>列可见性（顺序列表里出现 = 可见）。</summary>
    public bool IsColumnVisible(string key) => ConfigService.Current.Ui.Columns.Contains(key);

    /// <summary>列配置变化后刷新全部列绑定。</summary>
    public void RefreshColumns()
    {
        foreach (var col in TrackColumns)
        {
            OnPropertyChanged($"ColWidth{col.Key}");
            OnPropertyChanged($"ColIndex{col.Key}");
        }
    }

    /// <summary>显示/隐藏列。</summary>
    public void SetColumnVisible(string key, bool visible)
    {
        var cols = ConfigService.Current.Ui.Columns;
        if (visible)
        {
            if (!cols.Contains(key))
            {
                var defs = TrackColumns.Select(c => c.Key).ToList();
                var insertAt = cols.Count;
                for (var i = 0; i < defs.Count; i++)
                {
                    if (defs[i] == key) { insertAt = i; break; }
                    if (cols.Contains(defs[i]) && defs.IndexOf(key) < defs.IndexOf(defs[i]))
                    {
                        // 保持在默认顺序中的相对位置
                    }
                }
                cols.Insert(Math.Min(insertAt, cols.Count), key);
            }
        }
        else
        {
            cols.Remove(key);
        }
        ConfigService.Save();
        RefreshColumns();
    }

    /// <summary>列顺序上移/下移（delta = -1/+1）。</summary>
    public void MoveColumn(string key, int delta)
    {
        var cols = ConfigService.Current.Ui.Columns;
        var idx = cols.IndexOf(key);
        if (idx < 0) return;
        var target = idx + delta;
        if (target < 0 || target >= cols.Count) return;
        cols.RemoveAt(idx);
        cols.Insert(target, key);
        ConfigService.Save();
        RefreshColumns();
    }

    /// <summary>列宽（0 清空回默认）。</summary>
    public void SetColumnWidth(string key, double width)
    {
        if (width <= 0) ConfigService.Current.Ui.ColumnWidths.Remove(key);
        else ConfigService.Current.Ui.ColumnWidths[key] = width;
        ConfigService.Save();
        RefreshColumns();
    }

    /// <summary>分组标题封面开关（L3.1）。</summary>
    public bool GroupCoverVisible => ConfigService.Current.Ui.GroupCoverVisible;

    /// <summary>组头封面列宽（开关关闭 = 0 隐藏）。</summary>
    public double GroupCoverWidth => ConfigService.Current.Ui.GroupCoverVisible ? 36 : 0;

    /// <summary>标题 列宽（L3.1 列自定义；绑定源，缺失时列定义宽 0 导致整列不可见）。</summary>
    public double ColWidthTitle => ColWidth("Title");

    /// <summary>歌手 列宽（L3.1 列自定义；绑定源，缺失时列定义宽 0 导致整列不可见）。</summary>
    public double ColWidthArtist => ColWidth("Artist");

    /// <summary>专辑 列宽（L3.1 列自定义；绑定源，缺失时列定义宽 0 导致整列不可见）。</summary>
    public double ColWidthAlbum => ColWidth("Album");

    /// <summary>时长 列宽（L3.1 列自定义；绑定源，缺失时列定义宽 0 导致整列不可见）。</summary>
    public double ColWidthDuration => ColWidth("Duration");

    /// <summary>格式 列宽（L3.1 列自定义；绑定源，缺失时列定义宽 0 导致整列不可见）。</summary>
    public double ColWidthFormat => ColWidth("Format");

    /// <summary>采样率 列宽（L3.1 列自定义；绑定源，缺失时列定义宽 0 导致整列不可见）。</summary>
    public double ColWidthSampleRate => ColWidth("SampleRate");

    /// <summary>位深 列宽（L3.1 列自定义；绑定源，缺失时列定义宽 0 导致整列不可见）。</summary>
    public double ColWidthBitDepth => ColWidth("BitDepth");

    /// <summary>码率 列宽（L3.1 列自定义；绑定源，缺失时列定义宽 0 导致整列不可见）。</summary>
    public double ColWidthBitrate => ColWidth("Bitrate");

    /// <summary>标题 列索引（封面列=0，数据列从 1 起；缺失时元素堆到封面列）。</summary>
    public int ColIndexTitle => ColIndex("Title");

    /// <summary>歌手 列索引（封面列=0，数据列从 1 起；缺失时元素堆到封面列）。</summary>
    public int ColIndexArtist => ColIndex("Artist");

    /// <summary>专辑 列索引（封面列=0，数据列从 1 起；缺失时元素堆到封面列）。</summary>
    public int ColIndexAlbum => ColIndex("Album");

    /// <summary>时长 列索引（封面列=0，数据列从 1 起；缺失时元素堆到封面列）。</summary>
    public int ColIndexDuration => ColIndex("Duration");

    /// <summary>格式 列索引（封面列=0，数据列从 1 起；缺失时元素堆到封面列）。</summary>
    public int ColIndexFormat => ColIndex("Format");

    /// <summary>采样率 列索引（封面列=0，数据列从 1 起；缺失时元素堆到封面列）。</summary>
    public int ColIndexSampleRate => ColIndex("SampleRate");

    /// <summary>位深 列索引（封面列=0，数据列从 1 起；缺失时元素堆到封面列）。</summary>
    public int ColIndexBitDepth => ColIndex("BitDepth");

    /// <summary>码率 列索引（封面列=0，数据列从 1 起；缺失时元素堆到封面列）。</summary>
    public int ColIndexBitrate => ColIndex("Bitrate");

    /// <summary>折叠/展开一个分组（组头点击）。</summary>
    public void ToggleGroup(string groupKey)
    {
        if (!_collapsedGroups.Add(groupKey))
            _collapsedGroups.Remove(groupKey);
        RebuildDisplay();
    }

    /// <summary>配置变化后重建显示（L3.1 组头封面开关等）。</summary>
    public void ReloadConfigDependentDisplay()
    {
        OnPropertyChanged(nameof(GroupCoverWidth));
        OnPropertyChanged(nameof(GroupCoverVisible));
        RebuildDisplay();
    }

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

    /// <summary>分组键（与 TrackGrouper 一致：专辑 + 艺术家）。</summary>
    private static string GroupKeyOf(TrackGroup group)
        => group.Album + "\u0001" + group.Artist;

    /// <summary>按当前模式重建显示行。分组用 TrackGrouper（纯函数），平铺用过滤/排序后的 View。
    /// L3.1：折叠组只留组头；组头带封面与折叠状态。</summary>
    private void RebuildDisplay()
    {
        DisplayItems.Clear();
        if (IsGrouped)
        {
            var grouped = TrackGrouper.Group(View.Cast<TrackRecord>()).ToList();

            // 初始默认：配置要求默认折叠时，把所有组标记折叠（只一次；审查：不能用 Count==0 判据，
            // 用户手动展开所有组后会重复折叠）
            if (!ConfigService.Current.Ui.GroupsExpandedByDefault && !_collapsedDefaultApplied)
            {
                _collapsedDefaultApplied = true;
                foreach (var g in grouped) _collapsedGroups.Add(GroupKeyOf(g));
            }

            foreach (var group in grouped)
            {
                var key = GroupKeyOf(group);
                var collapsed = _collapsedGroups.Contains(key);
                DisplayItems.Add(new GroupHeaderItem
                {
                    AlbumText = group.Album,
                    ArtistText = group.Artist,
                    YearText = group.Year,
                    GroupKey = key,
                    CoverHash = group.Tracks.FirstOrDefault()?.CoverHash,
                    IsCollapsed = collapsed
                });
                if (collapsed) continue;
                for (var i = 0; i < group.Tracks.Count; i++)
                {
                    DisplayItems.Add(new TrackRowItem
                    {
                        Track = group.Tracks[i],
                        ShowCover = i == 0,
                        GroupKey = key
                    });
                }
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

    // ---------------- 右键菜单命令（2026-08-16 用户要求） ----------------

    /// <summary>「下一首播放」回调（Shell 转发 PlayerViewModel.PlayNextTracks）。</summary>
    public Action<IReadOnlyList<TrackRecord>, string>? PlayNextRequested { get; set; }

    /// <summary>「移出歌单」回调（Shell 从歌单移除选中曲目并刷新）。</summary>
    public Action? RemoveFromPlaylistRequested { get; set; }

    /// <summary>文件操作（移动/重命名/删除）后通知 Shell 重扫刷新。</summary>
    public Action? FilesChangedRequested { get; set; }

    /// <summary>歌词来源偏好（Shell 注入：按曲目持久化 + 当前播放曲目即时重载）。</summary>
    public Action<Player.Core.Lyrics.LyricPreference, IReadOnlyList<TrackRecord>>? LyricPreferenceRequested { get; set; }

    [RelayCommand]
    private void SetLyricPreference(Player.Core.Lyrics.LyricPreference preference)
    {
        var tracks = SelectedTracks;
        if (tracks.Count == 0) return;
        LyricPreferenceRequested?.Invoke(preference, tracks);
    }

    [RelayCommand]
    private void PlayNextSelected()
    {
        var tracks = SelectedTracks;
        if (tracks.Count == 0) return;
        PlayNextRequested?.Invoke(tracks, SourceName);
    }

    [RelayCommand]
    private void OpenContainingFolder()
    {
        var first = SelectedTracks.FirstOrDefault();
        if (first is null) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{first.Path}\""));
        }
        catch { /* 资源管理器打不开时静默 */ }
    }

    [RelayCommand]
    private void CopyFiles()
    {
        var files = SelectedTracks.Select(t => t.Path).Where(File.Exists).ToList();
        if (files.Count == 0) return;
        var col = new System.Collections.Specialized.StringCollection();
        col.AddRange(files.ToArray());
        System.Windows.Clipboard.SetFileDropList(col);
    }

    [RelayCommand]
    private void MoveFiles()
    {
        var files = SelectedTracks.Select(t => t.Path).Where(File.Exists).ToList();
        if (files.Count == 0) return;
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "选择目标文件夹（移动曲目文件）" };
        if (dlg.ShowDialog() != true) return;
        var errors = 0;
        foreach (var f in files)
        {
            try { File.Move(f, Path.Combine(dlg.FolderName, Path.GetFileName(f))); }
            catch { errors++; }
        }
        if (errors > 0)
            System.Windows.MessageBox.Show($"有 {errors} 个文件移动失败", "移动文件",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        else FilesChangedRequested?.Invoke();
    }

    [RelayCommand]
    private void RenameFile()
    {
        var first = SelectedTracks.FirstOrDefault();
        if (first is null || !File.Exists(first.Path)) return;
        var dir = Path.GetDirectoryName(first.Path);
        var ext = Path.GetExtension(first.Path);
        var input = Player.App.Views.InputDialog.Show("重命名文件", "新文件名（不含扩展名）",
            Path.GetFileNameWithoutExtension(first.Path));
        if (string.IsNullOrWhiteSpace(input)) return;
        var target = Path.Combine(dir ?? string.Empty, input.Trim() + ext);
        if (string.Equals(target, first.Path, StringComparison.OrdinalIgnoreCase)) return;
        try
        {
            File.Move(first.Path, target);
            FilesChangedRequested?.Invoke();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"重命名失败：{ex.Message}", "重命名",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    [RelayCommand]
    private void DeleteFiles()
    {
        var files = SelectedTracks.Select(t => t.Path).Where(File.Exists).ToList();
        if (files.Count == 0) return;
        var msg = files.Count == 1
            ? $"确定删除文件？\n{files[0]}"
            : $"确定删除这 {files.Count} 个文件？";
        if (System.Windows.MessageBox.Show(msg, "删除文件", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        var errors = 0;
        foreach (var f in files)
        {
            try { File.Delete(f); }
            catch { errors++; }
        }
        if (errors > 0)
            System.Windows.MessageBox.Show($"有 {errors} 个文件删除失败", "删除文件",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        else FilesChangedRequested?.Invoke();
    }

    [RelayCommand]
    private void ShowProperties()
    {
        var first = SelectedTracks.FirstOrDefault();
        if (first is null)
        {
            Serilog.Log.Debug("属性：无选中曲目");
            return;
        }
        Serilog.Log.Debug("属性：显示 {Path}", first.Path);
        // 自绘属性窗（用户反馈：系统 SHObjectProperties 在 Win11 不弹窗）
        var owner = System.Windows.Application.Current?.MainWindow;
        Player.App.Views.TrackPropertiesDialog.Show(first, owner);
        Serilog.Log.Debug("属性：对话框已关闭");
    }

    [RelayCommand]
    private void RemoveFromPlaylist()
    {
        if (!IsPlaylistPage) return;
        RemoveFromPlaylistRequested?.Invoke();
    }


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
