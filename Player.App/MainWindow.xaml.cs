using System.IO;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Player.App.Controls;
using Player.App.ViewModels;
using Player.Core.Audio;
using Player.Core.Infra;
using Player.Core.Library;
using Wpf.Ui.Controls;

namespace Player.App;

public partial class MainWindow : FluentWindow
{

    /// <summary>拖动曲目行时用的自定义剪贴板格式。</summary>
    private const string TrackDragFormat = "Player.TrackRecords";

    private readonly System.Windows.Threading.Dispatcher _dispatcher =
        Application.Current?.Dispatcher ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;

    private Point _dragStartPoint;
    private TrackRecord? _draggingTrack;
    private TrackRecord[] _dragPayload = Array.Empty<TrackRecord>();
    private bool _volumeDragging;

    public MainWindow()
    {
        InitializeComponent();

        // 播放位置滑条接管统一走 SeekSliderBehavior（工程铁律：禁止散装复制，
        // 见 XAML 中 SeekSlider 与 BigLyricsSeekSlider 的 ctrl:SeekSliderBehavior.Enable="True"）。

        // 播放条输出徽章：左键单击也弹出设备切换菜单（UI-R1.5 ⑫）
        OutputBadgeButton.Click += (_, _) =>
        {
            if (OutputBadgeButton.ContextMenu is not { } menu) return;
            menu.PlacementTarget = OutputBadgeButton;
            menu.IsOpen = true;
        };

        // 输出设备菜单项点击：直连命令（ItemContainerStyle 里的 RelativeSource 绑定不执行，实测确认）
        OutputBadgeButton.ContextMenu.AddHandler(System.Windows.Controls.MenuItem.ClickEvent,
            new RoutedEventHandler(OnOutputDeviceMenuClick));


        // DataContext 是构造后由 App 赋值的，定位事件在这里挂才挂得上
        DataContextChanged += (_, _) =>
        {
            if (Shell is not null)
            {
                Shell.TrackLocateRequested += ScrollToCurrentTrack;
                HookDesktopLyricsUpdates();
                HookSettingsLyricsUpdates();
                // 恢复桌面歌词（L1 第三步：开关持久化）
                if (ConfigService.Current.Ui.DesktopLyricsEnabled && _desktopLyrics is null)
                {
                    _desktopLyrics = CreateDesktopLyricsWindow();
                    _desktopLyrics.Show();
                    _dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, UpdateDesktopLyrics);
                }
            }
        };

        // 大歌词页：鼠标活动淡入控制条，3 秒无操作淡出（L1.1-④）
        BigLyricsOverlay.MouseMove += OnBigLyricsMouseMove;
        _bigLyricsIdle.Tick += (_, _) =>
        {
            // 目验修复：淡出同时关闭命中——隐形控制条不再拦截底部点击/误触隐藏滑条
            BigLyricsControlBar.IsHitTestVisible = false;
            BigLyricsControlBar.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200)));
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
        // 捕获鼠标：拖到控件外松手也能收到抬起事件，dB 文字才能按时消失
        Mouse.Capture(VolumeSquares);
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
        if (ReferenceEquals(Mouse.Captured, VolumeSquares)) Mouse.Capture(null);
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

    /// <summary>定位正在播放：找到对应行（分组模式是 TrackRowItem）选中、滚动并**居中**（UI-R1.5 ⑪ / R2 反馈四）。</summary>
    private void ScrollToCurrentTrack()
    {
        _dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, () =>
        {
            var list = FindVisualChild<System.Windows.Controls.ListBox>(
                this, lb => lb.DataContext is TrackListPageViewModel);
            if (list is null || CurrentTrackPage is not { } page) return;

            var item = page.DisplayItems.OfType<TrackRowItem>().FirstOrDefault(i =>
                string.Equals(i.Track.Path, page.SelectedTrack?.Path, StringComparison.OrdinalIgnoreCase));
            if (item is null) return;

            list.SelectedItem = item;
            list.ScrollIntoView(item);

            // 居中：CanContentScroll 下 ScrollViewer 偏移单位是"行"，滚到 行号 - 可见行数/2
            var scroll = FindVisualChild<System.Windows.Controls.ScrollViewer>(list, _ => true);
            if (scroll is null) return;
            var index = page.DisplayItems.IndexOf(item);
            if (scroll.ViewportHeight > 1)
                scroll.ScrollToVerticalOffset(Math.Max(0, index - scroll.ViewportHeight / 2));
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

    /// <summary>双击侧边栏文件夹：直接播放该文件夹（UI-R2 反馈；单击仍只切换列表）。</summary>
    private void OnNavListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var nav = FindDataContext<NavItemViewModel>(e.OriginalSource as DependencyObject);
        if (nav is null) return;
        Shell?.PlayFolderPlaylist(nav);
    }

    /// <summary>Esc：先退大歌词页，再退设置页（UI-R2 bug 修复）。</summary>
    private void Window_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (_bigLyricsVisible)
        {
            ToggleBigLyrics();
            e.Handled = true;
            return;
        }
        if (Shell?.CurrentPage is SettingsPageViewModel)
        {
            Shell.LeaveSettings();
            e.Handled = true;
        }
    }

    // ================= 桌面歌词（L1 第三步） =================

    private DesktopLyricsWindow? _desktopLyrics;

    /// <summary>新建桌面歌词窗（统一接上"打开字体设置"请求）。</summary>
    private DesktopLyricsWindow CreateDesktopLyricsWindow()
    {
        var window = new DesktopLyricsWindow();
        window.OpenFontSettingsRequested += OpenLyricSettingsFromDesktopLyrics;
        return window;
    }

    /// <summary>桌面歌词右键菜单"字体设置…"：跳设置页歌词组。</summary>
    private void OpenLyricSettingsFromDesktopLyrics()
    {
        if (Shell is not { } shell) return;
        if (shell.OpenSettingsCommand.CanExecute(null)) shell.OpenSettingsCommand.Execute(null);
        if (shell.CurrentPage is SettingsPageViewModel settings) settings.IsLyricTab = true;
    }

    private void OnDesktopLyricsButtonClick(object sender, RoutedEventArgs e) => ToggleDesktopLyrics();

    private void ToggleDesktopLyrics()
    {
        // 目验修复④：打开桌面歌词前关闭按钮 ToolTip（避免弹出层悬浮在歌词条上）
        DesktopLyricsButtonTip.IsOpen = false;
        if (_desktopLyrics is null)
        {
            _desktopLyrics = CreateDesktopLyricsWindow();
            _desktopLyrics.Show();
        }
        else if (_desktopLyrics.IsVisible)
        {
            _desktopLyrics.Hide();
        }
        else
        {
            _desktopLyrics.Show();
            _desktopLyrics.ApplySettings();
            UpdateDesktopLyrics();
        }
        ConfigService.Current.Ui.DesktopLyricsEnabled = _desktopLyrics.IsVisible;
        ConfigService.Save();
    }

    private void UpdateDesktopLyrics()
    {
        if (_desktopLyrics is null || !_desktopLyrics.IsVisible) return;
        var lyrics = Player?.Lyrics;
        if (lyrics is null) return;

        // 无歌词 / 无时间轴 / 未开始播放 → 显示曲名（W1：完全无歌词或未播放时避免空条）
        if (!lyrics.HasLyrics || lyrics.IsStatic || lyrics.CurrentIndex < 0)
        {
            _desktopLyrics.UpdateLyrics(Player?.Title ?? string.Empty, Player?.Artist ?? string.Empty, hasTimeline: false);
        }
        else
        {
            _desktopLyrics.UpdateLyrics(lyrics.CurrentPrimary, lyrics.CurrentSecondary, hasTimeline: true);
        }
    }

    /// <summary>设置页「歌词」组：字体/字重/字号/单双行/个性化改动即时作用（桌面歌词窗 + 两个 LyricCanvas）。</summary>
    private void HookSettingsLyricsUpdates()
    {
        if (Shell is null) return;
        Shell.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(ShellViewModel.CurrentPage)) return;
            if (Shell.CurrentPage is not SettingsPageViewModel settings) return;
            settings.PropertyChanged += (_, se) =>
            {
                switch (se.PropertyName)
                {
                    // 字体/字重：三处全刷（右栏/大歌词页画布重排 + 桌面歌词重设）
                    case nameof(SettingsPageViewModel.SelectedLyricFontFamily):
                    case nameof(SettingsPageViewModel.SelectedLyricFontWeight):
                        SideLyricCanvas?.InvalidateVisual();
                        BigLyricCanvas?.InvalidateVisual();
                        _desktopLyrics?.ApplySettings();
                        break;

                    // 桌面歌词个性化（背景/透明度/文字颜色/字号/单双行）
                    case nameof(SettingsPageViewModel.SelectedLyricFontSize):
                    case nameof(SettingsPageViewModel.DesktopLyricsTwoLines):
                    case nameof(SettingsPageViewModel.DesktopLyricsShowBackground):
                    case nameof(SettingsPageViewModel.SelectedDesktopLyricsBgOpacity):
                    case nameof(SettingsPageViewModel.SelectedDesktopLyricsTextColor):
                        _desktopLyrics?.ApplySettings();
                        break;
                }
            };
        };
    }

    private void HookDesktopLyricsUpdates()
    {
        if (Player is null) return;
        Player.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(PlayerViewModel.Title) or nameof(PlayerViewModel.CoverImage))
                UpdateDesktopLyrics();
        };
        Player.Lyrics.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(LyricsViewModel.CurrentPrimary) or nameof(LyricsViewModel.CurrentSecondary)
                or nameof(LyricsViewModel.HasLyrics) or nameof(LyricsViewModel.IsStatic))
                UpdateDesktopLyrics();
        };
    }

    // ================= 大歌词页（L1 第二步 + L1.1-④ 收尾） =================

    private bool _bigLyricsVisible;

    private readonly System.Windows.Threading.DispatcherTimer _bigLyricsIdle = new()
    {
        Interval = TimeSpan.FromSeconds(3)
    };

    private void OnBigLyricsButtonClick(object sender, RoutedEventArgs e) => ToggleBigLyrics();

    private void OnBigLyricsCloseClick(object sender, RoutedEventArgs e) => ToggleBigLyrics();

    private void OnBigLyricClicked(int index) => Player?.Lyrics.SeekToLine(index);

    /// <summary>点击空白（非歌词画布/滑条/按钮）退出（L1.1-④：顺带恢复"再点按钮①退出"——按钮①位于覆盖层下方，
    /// 其落点即空白，走同一路径）。</summary>
    private void OnBigLyricsOverlayMouseDown(object sender, MouseButtonEventArgs e)
    {
        var source = e.OriginalSource as DependencyObject;
        while (source is not null)
        {
            if (source is System.Windows.Controls.Button or Slider or LyricCanvas) return;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        ToggleBigLyrics();
    }

    /// <summary>鼠标活动：淡入完整控制条并重启 3 秒无操作计时。</summary>
    private void OnBigLyricsMouseMove(object sender, MouseEventArgs e)
    {
        if (!_bigLyricsVisible) return;
        if (BigLyricsControlBar.Opacity < 0.99)
        {
            BigLyricsControlBar.IsHitTestVisible = true;
            BigLyricsControlBar.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
        }
        _bigLyricsIdle.Stop();
        _bigLyricsIdle.Start();
    }

    private void ToggleBigLyrics()
    {
        _bigLyricsVisible = !_bigLyricsVisible;
        if (_bigLyricsVisible)
        {
            // 目验修复④：覆盖层打开前关闭按钮 ToolTip（其弹出层是独立置顶窗口，否则会悬浮在覆盖层上）
            BigLyricsButtonTip.IsOpen = false;
            BigLyricsOverlay.Visibility = Visibility.Visible;
            BigLyricsOverlay.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
            // 进场先显示完整控制，3 秒无操作后自动收敛为细进度线
            BigLyricsControlBar.IsHitTestVisible = true;
            BigLyricsControlBar.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150)));
            _bigLyricsIdle.Stop();
            _bigLyricsIdle.Start();
        }
        else
        {
            _bigLyricsIdle.Stop();
            BigLyricsControlBar.IsHitTestVisible = false;
            BigLyricsControlBar.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(150)));
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200));
            fade.Completed += (_, _) => BigLyricsOverlay.Visibility = Visibility.Collapsed;
            BigLyricsOverlay.BeginAnimation(OpacityProperty, fade);
        }
    }

    /// <summary>输出设备菜单项点击：直连 Player 命令（UI-R2 修复：菜单绑定不执行）。</summary>
    private void OnOutputDeviceMenuClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not System.Windows.Controls.MenuItem item) return;
        if (item.DataContext is not PlayerViewModel.OutputDeviceItem device) return;
        Player?.SwitchOutputDeviceCommand.Execute(device);
    }

    // ================= 曲目列表 =================

    /// <summary>静态列头点击排序（UI-R2）：平铺模式有效；分组模式按专辑固定顺序。</summary>
    private void OnTrackHeaderClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement el || el.Tag is not string property) return;
        if (CurrentTrackPage is not { } page || page.IsGrouped) return;
        page.SortBy(property);
    }

    private void OnTrackListSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // WPF-UI 也有一个同名的 ListView，这里要的是标准控件
        if (sender is not System.Windows.Controls.ListBox list) return;
        var tracks = list.SelectedItems.OfType<TrackRowItem>().Select(i => i.Track).ToList();
        if (CurrentTrackPage is { } page)
        {
            page.SetSelection(tracks);
            page.SelectedTrack = tracks.LastOrDefault();
        }
    }

    private void OnTrackListDoubleClick(object sender, MouseButtonEventArgs e)
    {
        var track = FindTrack(e.OriginalSource as DependencyObject);
        if (track is null) return;

        CurrentTrackPage?.Play(track);
    }

    private void OnTrackListPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(null);
        _draggingTrack = FindTrack(e.OriginalSource as DependencyObject);

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

        var targetRow = FindTrack(e.OriginalSource as DependencyObject);

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

    /// <summary>曲目列表行的数据解包（UI-R2）：分组模式的行是 TrackRowItem。</summary>
    private static TrackRecord? FindTrack(DependencyObject? source)
    {
        var data = FindDataContext<object>(source);
        return data switch
        {
            TrackRecord track => track,
            TrackRowItem row => row.Track,
            _ => null
        };
    }
}
