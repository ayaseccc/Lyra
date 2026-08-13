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

    private Point _dragStartPoint;
    private TrackRecord? _draggingTrack;

    public MainWindow()
    {
        InitializeComponent();

        SeekSlider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(OnSeekDragStarted));
        SeekSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnSeekDragCompleted));
    }

    private ShellViewModel? Shell => DataContext as ShellViewModel;

    private PlayerViewModel? Player => Shell?.Player;

    private TrackListPageViewModel? CurrentTrackPage => Shell?.CurrentPage as TrackListPageViewModel;

    // ---------------- 拖放（窗口级：入库并开播） ----------------

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

    // ---------------- 曲目列表 ----------------

    private void OnTrackHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader header) return;
        if (header.Role == GridViewColumnHeaderRole.Padding) return;
        if (header.Content is not string text) return;
        if (!SortProperties.TryGetValue(text, out var property)) return;

        CurrentTrackPage?.SortBy(property);
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
    }

    private void OnTrackListMouseMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton != MouseButtonState.Pressed || _draggingTrack is null) return;
        // 只有手工歌单、且没有列排序/过滤时才允许拖拽排序（否则可见顺序与底层顺序对不上）
        if (CurrentTrackPage?.CanReorderNow != true) return;

        var position = e.GetPosition(null);
        if (Math.Abs(position.X - _dragStartPoint.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _dragStartPoint.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var data = new DataObject(typeof(TrackRecord), _draggingTrack);
        DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Move);
        _draggingTrack = null;
    }

    /// <summary>WPF 的 ListBox 右键不会改变选中项，这里手动选中命中的那一行，右键菜单才不会作用错对象。</summary>
    private void OnNavListPreviewRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list) return;

        var source = e.OriginalSource as DependencyObject;
        while (source is not null && source is not ListBoxItem)
        {
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }

        if (source is ListBoxItem { DataContext: NavItemViewModel nav } && !nav.IsHeader)
            list.SelectedItem = nav;
    }

    private void OnTrackListDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = ContainsUsablePaths(e) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = e.Data.GetDataPresent(typeof(TrackRecord)) && CurrentTrackPage?.CanReorderNow == true
            ? DragDropEffects.Move
            : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnTrackListDrop(object sender, DragEventArgs e)
    {
        // 外部文件拖到列表上，等同于拖到窗口上
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Handled = true;
            if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0 && Shell is not null)
                await Shell.HandleDroppedPathsAsync(paths);
            return;
        }

        if (e.Data.GetData(typeof(TrackRecord)) is not TrackRecord dragged) return;

        var page = CurrentTrackPage;
        if (page?.CanReorderNow != true) return;

        var target = FindDataContext<TrackRecord>(e.OriginalSource as DependencyObject);
        if (target is null || ReferenceEquals(target, dragged)) return;

        page.MoveItem(page.Items.IndexOf(dragged), page.Items.IndexOf(target));
        e.Handled = true;
    }

    // ---------------- 专辑 / 艺术家 ----------------

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

    // ---------------- 进度条（P0.1 的时序，勿动） ----------------

    private void OnSeekDragStarted(object sender, DragStartedEventArgs e) => Player?.BeginSeek();

    private void OnSeekDragCompleted(object sender, DragCompletedEventArgs e) => Player?.EndSeek(SeekSlider.Value);

    private void SeekSlider_OnPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e) => Player?.BeginSeek();

    private void SeekSlider_OnPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        => Player?.EndSeek(SeekSlider.Value);

    // ---------------- 工具 ----------------

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
