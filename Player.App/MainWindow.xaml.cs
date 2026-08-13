using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Player.App.ViewModels;
using Player.Core.Audio;
using Player.Core.Library;
using Wpf.Ui.Controls;

namespace Player.App;

public partial class MainWindow : FluentWindow
{
    /// <summary>列头文案 → 排序用的属性名。时长/采样率/位深要按数值排，不能按显示文本排。</summary>
    private static readonly Dictionary<string, string> SortProperties = new()
    {
        ["标题"] = "DisplayTitle",
        ["歌手"] = "DisplayArtist",
        ["专辑"] = "DisplayAlbum",
        ["时长"] = "DurationMs",
        ["格式"] = "Format",
        ["采样率"] = "SampleRate",
        ["位深"] = "BitDepth"
    };

    /// <summary>拖动曲目行时用的自定义剪贴板格式。</summary>
    private const string TrackDragFormat = "Player.TrackRecords";

    private Point _dragStartPoint;
    private TrackRecord? _draggingTrack;
    private TrackRecord[] _dragPayload = Array.Empty<TrackRecord>();

    public MainWindow()
    {
        InitializeComponent();

        SeekSlider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(OnSeekDragStarted));
        SeekSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnSeekDragCompleted));
    }

    private ShellViewModel? Shell => DataContext as ShellViewModel;

    private PlayerViewModel? Player => Shell?.Player;

    private TrackListPageViewModel? CurrentTrackPage => Shell?.CurrentPage as TrackListPageViewModel;

    // ================= 窗口级拖放：入库并开播 =================
    // 只有落在"非歌单目标"上的拖放才走这里；落到侧边栏歌单或歌单详情页的，
    // 会在下面各自的处理器里被标记为已处理，不会冒泡到这里。

    private void Window_OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = ContainsUsablePaths(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_OnDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;
        if (Shell is null) return;

        await Shell.HandleDroppedPathsAsync(paths);
    }

    private static bool ContainsUsablePaths(DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return false;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return false;
        return paths.Any(p => Directory.Exists(p) || AudioFormats.IsSupported(p));
    }

    private static string[] GetDroppedPaths(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) as string[] ?? Array.Empty<string>();

    private static TrackRecord[] GetDraggedTracks(DragEventArgs e) =>
        e.Data.GetData(TrackDragFormat) as TrackRecord[] ?? Array.Empty<TrackRecord>();

    // ================= 侧边栏：拖到歌单上 = 加入该歌单 =================

    private void OnNavDragOver(object sender, DragEventArgs e)
    {
        var nav = FindDataContext<NavItemViewModel>(e.OriginalSource as DependencyObject);
        var isPlaylistTarget = nav is { Kind: NavKind.Playlist };
        var hasPayload = e.Data.GetDataPresent(TrackDragFormat) || ContainsUsablePaths(e);

        if (isPlaylistTarget && hasPayload)
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;   // 拦下来，别让窗口把它当成"入库并开播"
            return;
        }

        e.Effects = DragDropEffects.None;   // 非歌单目标：不处理，冒泡给窗口
    }

    private async void OnNavDrop(object sender, DragEventArgs e)
    {
        var nav = FindDataContext<NavItemViewModel>(e.OriginalSource as DependencyObject);
        if (nav is not { Kind: NavKind.Playlist } || Shell is null) return;

        e.Handled = true;
        await Shell.DropOnPlaylistAsync(nav.PlaylistId, GetDroppedPaths(e), GetDraggedTracks(e));
    }

    /// <summary>WPF 的 ListBox 右键不会改变选中项，这里手动选中命中的那一行，右键菜单才不会作用错对象。</summary>
    private void OnNavListPreviewRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list) return;

        var nav = FindDataContext<NavItemViewModel>(e.OriginalSource as DependencyObject);
        if (nav is not null && !nav.IsHeader) list.SelectedItem = nav;
    }

    // ================= 曲目列表 =================

    private void OnTrackHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader header) return;
        if (header.Role == GridViewColumnHeaderRole.Padding) return;
        if (header.Content is not string text) return;
        if (!SortProperties.TryGetValue(text, out var property)) return;

        CurrentTrackPage?.SortBy(property);
    }

    private void OnTrackListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // WPF-UI 也有一个同名的 ListView，这里要的是标准控件
        if (sender is not System.Windows.Controls.ListView list) return;
        CurrentTrackPage?.SetSelection(list.SelectedItems.OfType<TrackRecord>());
    }

    private void OnTrackListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var track = FindDataContext<TrackRecord>(e.OriginalSource as DependencyObject);
        if (track is null) return;

        CurrentTrackPage?.Play(track);
    }

    private void OnTrackListPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
        _draggingTrack = FindDataContext<TrackRecord>(e.OriginalSource as DependencyObject);

        // 负载要在这一刻取：鼠标按下之后 WPF 会把多选收敛成单选，那时就拿不到整个选区了
        _dragPayload = _draggingTrack is null
            ? Array.Empty<TrackRecord>()
            : CurrentTrackPage?.GetDragPayload(_draggingTrack).ToArray() ?? new[] { _draggingTrack };
    }

    private void OnTrackListMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggingTrack is null) return;
        if (_dragPayload.Length == 0) return;

        var position = e.GetPosition(null);
        if (Math.Abs(position.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        // 任何列表的行都可以拖（拖到侧边栏歌单 = 加入），是否允许落回本列表由落点决定
        var data = new DataObject(TrackDragFormat, _dragPayload);
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy | DragDropEffects.Move);

        _draggingTrack = null;
        _dragPayload = Array.Empty<TrackRecord>();
    }

    private void OnTrackListDragOver(object sender, DragEventArgs e)
    {
        var isPlaylistPage = CurrentTrackPage?.CanEdit == true;

        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            if (isPlaylistPage && ContainsUsablePaths(e))
            {
                e.Effects = DragDropEffects.Copy;   // 歌单页：按落点插入
                e.Handled = true;
                return;
            }

            e.Effects = DragDropEffects.None;       // 其它页：冒泡给窗口做"入库并开播"
            return;
        }

        if (e.Data.GetDataPresent(TrackDragFormat) && isPlaylistPage)
        {
            e.Effects = DragDropEffects.Move;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnTrackListDrop(object sender, DragEventArgs e)
    {
        var page = CurrentTrackPage;
        if (page is null || Shell is null) return;

        // 歌单页之外的落点一律不处理，交给窗口维持"入库并开播"
        if (!page.CanEdit) return;

        var targetRow = FindDataContext<TrackRecord>(e.OriginalSource as DependencyObject);
        var insertIndex = page.IndexOfRow(targetRow);

        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Handled = true;
            await Shell.InsertIntoPlaylistAsync(page, insertIndex, GetDroppedPaths(e), Array.Empty<TrackRecord>());
            return;
        }

        var dragged = GetDraggedTracks(e);
        if (dragged.Length == 0) return;

        e.Handled = true;
        await Shell.InsertIntoPlaylistAsync(page, insertIndex, Array.Empty<string>(), dragged);
    }

    // ================= 专辑 / 艺术家 =================

    private void OnAlbumListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var album = FindDataContext<AlbumGroup>(e.OriginalSource as DependencyObject);
        if (album is null) return;

        (Shell?.CurrentPage as AlbumPageViewModel)?.Open(album);
    }

    private void OnArtistListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var artist = FindDataContext<ArtistGroup>(e.OriginalSource as DependencyObject);
        if (artist is null) return;

        (Shell?.CurrentPage as ArtistPageViewModel)?.Open(artist);
    }

    // ================= 进度条（P0.1 的时序） =================

    private void OnSeekDragStarted(object sender, DragStartedEventArgs e) => Player?.BeginSeek();

    private void OnSeekDragCompleted(object sender, DragCompletedEventArgs e) => Player?.EndSeek(SeekSlider.Value);

    private void SeekSlider_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Player?.BeginSeek();

    private void SeekSlider_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        => Player?.EndSeek(SeekSlider.Value);

    // ================= 工具 =================

    /// <summary>从被点中的元素往上找到列表行，取它的数据对象。</summary>
    private static T? FindDataContext<T>(DependencyObject? source) where T : class
    {
        while (source is not null)
        {
            if (source is ListBoxItem item)
                return item.DataContext as T;

            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }

        return null;
    }
}
