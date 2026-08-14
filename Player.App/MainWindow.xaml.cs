using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Player.App.ViewModels;
using Player.Core.Audio;
using Player.Core.Infra;
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

    private readonly System.Windows.Threading.Dispatcher _dispatcher =
        Application.Current?.Dispatcher ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;

    private Point _dragStartPoint;
    private TrackRecord? _draggingTrack;
    private TrackRecord[] _dragPayload = Array.Empty<TrackRecord>();
    private bool _seekPressedOnSlider;
    private bool _volumeDragging;

    public MainWindow()
    {
        InitializeComponent();

        // 进度条的接管必须用 AddHandler(..., handledEventsToo: true)：
        // Slider 的类处理器会在 IsMoveToPointEnabled 时把 PreviewMouseLeftButtonDown 标记为已处理，
        // XAML 上挂的实例处理器默认收不到已处理事件 —— 这正是 P1.1-② 里"点击后弹回"的根因
        // （BeginSeek 没跑 → EndSeek 被门禁挡掉 → 定时器把位置拉回去）。
        SeekSlider.AddHandler(PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(OnSeekPressed), handledEventsToo: true);
        SeekSlider.AddHandler(PreviewMouseLeftButtonUpEvent,
            new MouseButtonEventHandler(OnSeekReleased), handledEventsToo: true);

        // 拖动结束也走同一条释放路径（鼠标在窗口外松开时靠它兜底）
        SeekSlider.AddHandler(Thumb.DragStartedEvent, new DragStartedEventHandler(OnSeekDragStarted));
        SeekSlider.AddHandler(Thumb.DragCompletedEvent, new DragCompletedEventHandler(OnSeekDragCompleted));

        // 播放条输出徽章：左键单击也弹出设备切换菜单（UI-R1.5 ⑫）
        OutputBadgeButton.Click += (_, _) =>
        {
            if (OutputBadgeButton.ContextMenu is not { } menu) return;
            menu.PlacementTarget = OutputBadgeButton;
            menu.IsOpen = true;
        };

        // 播放模式按钮：左键单击展开模式选择菜单
        ModeButton.Click += (_, _) =>
        {
            if (ModeButton.ContextMenu is not { } menu) return;
            menu.PlacementTarget = ModeButton;
            menu.IsOpen = true;
        };

        // DataContext 是构造后由 App 赋值的，定位事件在这里挂才挂得上
        DataContextChanged += (_, _) =>
        {
            if (Shell is not null)
                Shell.TrackLocateRequested += ScrollToCurrentTrack;
        };

        // 音量方块：点击/拖动设置音量（UI-R1.5 反馈）
        VolumeSquares.AddHandler(PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(OnVolumeMouseDown), handledEventsToo: true);
        VolumeSquares.AddHandler(PreviewMouseMoveEvent,
            new MouseEventHandler(OnVolumeMouseMove), handledEventsToo: true);
        VolumeSquares.AddHandler(PreviewMouseLeftButtonUpEvent,
            new MouseButtonEventHandler(OnVolumeMouseUp), handledEventsToo: true);

        // 恢复上次的窗口尺寸（UI-R1.5 反馈）
        var ui = ConfigService.Current.Ui;
        if (ui.WindowWidth >= MinWidth && ui.WindowHeight >= MinHeight)
        {
            Width = ui.WindowWidth;
            Height = ui.WindowHeight;
        }
    }

    private void OnVolumeMouseDown(object sender, MouseButtonEventArgs e)
    {
        _volumeDragging = true;
        UpdateVolumeFromMouse(e.GetPosition(VolumeSquares));
        e.Handled = true;
    }

    private void OnVolumeMouseMove(object sender, MouseEventArgs e)
    {
        if (!_volumeDragging || e.LeftButton != MouseButtonState.Pressed) return;
        UpdateVolumeFromMouse(e.GetPosition(VolumeSquares));
        e.Handled = true;
    }

    private void OnVolumeMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_volumeDragging) return;
        _volumeDragging = false;
        Player?.EndVolumeDrag();
        e.Handled = true;
    }

    /// <summary>按横向位置换算音量 0..1 并设给引擎（dB 反馈由 VM 弹出）。</summary>
    private void UpdateVolumeFromMouse(Point p)
    {
        if (Player is null || VolumeSquares.ActualWidth <= 0) return;
        Player.SetVolumeFromDrag(Math.Clamp(p.X / VolumeSquares.ActualWidth, 0, 1));
    }

    /// <summary>关窗前记下窗口尺寸，退出时随配置落盘。</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        var bounds = RestoreBounds;
        var ui = ConfigService.Current.Ui;
        if (bounds.Width > 0 && bounds.Height > 0)
        {
            ui.WindowWidth = bounds.Width;
            ui.WindowHeight = bounds.Height;
        }
        else
        {
            ui.WindowWidth = ActualWidth;
            ui.WindowHeight = ActualHeight;
        }

        base.OnClosing(e);
    }

    /// <summary>定位正在播放：把当前曲目列表滚动到选中的那一行（UI-R1.5 ⑪）。</summary>
    private void ScrollToCurrentTrack()
    {
        _dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            var list = FindVisualChild<System.Windows.Controls.ListView>(
                this, lv => lv.DataContext is TrackListPageViewModel);
            if (list?.SelectedItem is not { } item) return;
            list.ScrollIntoView(item);
        });
    }

    /// <summary>按条件在可视树里找第一个后代（DataTemplate 里的控件不在窗口 namescope，只能这样找）。</summary>
    private static T? FindVisualChild<T>(DependencyObject root, Func<T, bool> predicate) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match && predicate(match)) return match;
            if (FindVisualChild(child, predicate) is { } nested) return nested;
        }
        return null;
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
        try
        {
            var data = new DataObject(TrackDragFormat, _dragPayload);
            DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy | DragDropEffects.Move);
        }
        finally
        {
            ClearDragState();
        }
    }

    private void OnTrackListPreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e) => ClearDragState();

    private void ClearDragState()
    {
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

        // 列表正被排序/过滤时可见顺序与底层顺序不一致，落点没有意义，一律追加到末尾
        var insertIndex = page.IsViewSortedOrFiltered ? page.Items.Count : page.IndexOfRow(targetRow);

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

    // ================= 歌词覆盖层（P3） =================

    /// <summary>右侧栏歌词：点击某行跳转（UI-R0）。</summary>
    private void OnSideLyricClicked(int index) => Player?.Lyrics.SeekToLine(index);

    private void OnSeekPressed(object sender, MouseButtonEventArgs e)
    {
        _seekPressedOnSlider = true;
        Player?.BeginSeek();
    }

    /// <summary>释放即 seek —— 点击和拖动都走这里，且必然执行一次。</summary>
    private void OnSeekReleased(object sender, MouseButtonEventArgs e)
    {
        // 按下不在滑条上（只是抬手时正好经过）就不该产生跳转
        if (!_seekPressedOnSlider) return;

        _seekPressedOnSlider = false;
        Player?.EndSeek(SeekSlider.Value);
    }

    private void OnSeekDragStarted(object sender, DragStartedEventArgs e)
    {
        _seekPressedOnSlider = true;
        Player?.BeginSeek();
    }

    private void OnSeekDragCompleted(object sender, DragCompletedEventArgs e)
    {
        _seekPressedOnSlider = false;
        Player?.EndSeek(SeekSlider.Value);
    }

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
