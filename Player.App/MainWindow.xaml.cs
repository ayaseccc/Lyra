using System.IO;
using System.Windows;
using System.Windows.Media.Animation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Player.App.Controls;
using Player.App.ViewModels;
using Player.Core.Audio;
using Player.Core.Hotkeys;
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

    private Player.App.SystemMedia.SmtcService? _smtcService;

    /// <summary>L2 托盘（菜单播放控制/桌面歌词开关/显示主窗/退出，双击还原）。</summary>
    private Player.App.SystemTray.TrayService? _tray;

    /// <summary>L3.2 迷你悬浮窗（开=主窗隐藏，关=主窗恢复）。</summary>
    private Controls.MiniPlayerWindow? _miniWindow;

    /// <summary>L2 全局热键（RegisterHotKey，默认全关；占用逐条提示）。</summary>
    private Player.App.GlobalHotkeys.GlobalHotkeyService? _globalHotkeys;

    /// <summary>托盘菜单「退出」置位：放行后续真实的关闭（配合"关闭到托盘"拦截）。</summary>
    private bool _exitingFromTray;

    /// <summary>系统会话结束（关机/重启/注销）：放行关闭，避免"关闭到托盘"拦截阻塞系统关机（审查修复）。</summary>
    private bool _sessionEnding;

    /// <summary>SMTC 初始化失败后的有界重试（审查修复：仅靠 Loaded 重试不可靠）。</summary>
    private int _smtcRetryCount;
    private System.Windows.Threading.DispatcherTimer? _smtcRetryTimer;

    /// <summary>L2 快捷键映射（可改绑；配置改动时重建）。</summary>
    private ShortcutMap? _shortcutMap;

    /// <summary>改绑捕获状态：非空 = 等待按键。</summary>
    private ShortcutKey? _rebindAction;
    private string? _rebindGlobalName;

    /// <summary>当前打开着的设置页 VM（改绑结果回写用）。</summary>
    private ViewModels.SettingsPageViewModel? _settingsVm;

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
                Shell.DownloadDirPicker = PickDownloadDirectory;   // P4 实机反馈：下载时弹窗选目标
                HookDesktopLyricsUpdates();
                HookSettingsLyricsUpdates();
                InitTray();
                RebuildShortcutMap();
                // 恢复桌面歌词（L1 第三步：开关持久化）
                if (ConfigService.Current.Ui.DesktopLyricsEnabled && _desktopLyrics is null)
                {
                    _desktopLyrics = CreateDesktopLyricsWindow();
                    _desktopLyrics.Show();
                    _dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded, UpdateDesktopLyrics);
                }
                _tray?.RefreshDesktopLyricsCheck();
            }
        };

        // 音量方块：点击/拖动设置音量（UI-R1.5 反馈）
        VolumeSquares.AddHandler(PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(OnVolumeMouseDown), handledEventsToo: true);
        VolumeSquares.AddHandler(PreviewMouseMoveEvent,
            new MouseEventHandler(OnVolumeMouseMove), handledEventsToo: true);
        VolumeSquares.AddHandler(PreviewMouseLeftButtonUpEvent,
            new MouseButtonEventHandler(OnVolumeMouseUp), handledEventsToo: true);

        // L2 SMTC：窗口**显示后**初始化（GetForWindow 在窗口可见前调用会失败）；
        // 失败保持 _smtcService = null，由有界重试定时器兜底（审查修复）
        Loaded += (_, _) =>
        {
            _smtcRetryCount = 0; // 重新显示窗口 = 新的初始化机会
            InitSmtc();
            // L2 全局热键：配置里开启过就恢复注册（失败会逐条提示）
            if (ConfigService.Current.Ui.GlobalHotkeysEnabled) ApplyGlobalHotkeys();
        };

        // L2 托盘兼容：系统关机/重启/注销时放行退出，不能被"关闭到托盘"的拦截卡住（审查修复）
        System.Windows.Application.Current!.SessionEnding += (_, _) => _sessionEnding = true;

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

    /// <summary>L2 SMTC：Player 就绪且窗口已显示后创建服务（GetForWindow 要求窗口可见）。
    /// 失败保持 null 并安排有界重试（最多 3 次、间隔 8s，审查修复）。</summary>
    private void InitSmtc()
    {
        if (_smtcService is not null || Player is null) return;
        try
        {
            _smtcService = new Player.App.SystemMedia.SmtcService(new WindowInteropHelper(this).Handle, Player);
            _smtcRetryTimer?.Stop();
            _smtcRetryTimer = null;
            Serilog.Log.Information("SMTC 已就绪（媒体键/锁屏控制可用）");
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "SMTC 初始化失败（媒体键/锁屏控制不可用，HRESULT 0x{HR:X8}）", ex.HResult);
            _smtcRetryCount++;
            if (_smtcRetryCount <= 3)
            {
                _smtcRetryTimer ??= new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(8)
                };
                if (!_smtcRetryTimer.IsEnabled)
                {
                    _smtcRetryTimer.Tick += (_, _) =>
                    {
                        _smtcRetryTimer!.Stop();
                        InitSmtc();
                    };
                    _smtcRetryTimer.Start();
                }
            }
        }
    }

    /// <summary>P4 在线搜索：回车触发搜索。</summary>
    private void OnOnlineSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (Shell?.CurrentPage is ViewModels.OnlineSearchViewModel vm && vm.CanSearch)
            vm.SearchCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>P4 在线搜索：双击结果 = 试听（临时播放，不写歌单/队列）。</summary>
    private void OnOnlineSearchDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (Shell?.CurrentPage is not ViewModels.OnlineSearchViewModel vm || vm.SelectedItem is not { } item) return;
        _ = Player?.PlayOnlinePreviewAsync(item.Track, item.SourceKey, vm.SelectedBr);
    }

    /// <summary>L3.1 分组折叠：组头行点击切换该组折叠状态。</summary>
    private void OnGroupHeaderClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject d) return;
        if (FindAncestor<System.Windows.Controls.ListBox>(d)?.DataContext is not ViewModels.TrackListPageViewModel page) return;
        if (FindAncestor<System.Windows.Controls.ListBoxItem>(d)?.DataContext is ViewModels.GroupHeaderItem header)
            page.ToggleGroup(header.GroupKey);
    }

    /// <summary>P4 搜索结果右键菜单。菜单项 Click 优先取 ContextMenu.PlacementTarget——
    /// 框架打开菜单时自动设为所属行（独立弹出树也可靠），不依赖点击时刻的鼠标命中；
    /// 右键按下那一刻的捕获值做兜底（实测：菜单在右键抬起后才弹出，弹后再取位置不可靠）。</summary>
    private ViewModels.OnlineSearchItem? _onlineMenuItem;
    private ViewModels.OnlineSearchViewModel? _onlineMenuVm;

    private void OnOnlineSearchPreviewMouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 捕获兜底（右键按下即记下所在行；菜单打开时 ContextMenuOpening 会再覆盖一次）
        if (e.OriginalSource is not DependencyObject d) return;
        var row = FindAncestor<System.Windows.Controls.ListBoxItem>(d);
        _onlineMenuItem = row?.DataContext as ViewModels.OnlineSearchItem;
        _onlineMenuVm = FindAncestor<System.Windows.Controls.ListBox>(d)?.DataContext as ViewModels.OnlineSearchViewModel;
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match) return match;
            node = System.Windows.Media.VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    private sealed record OnlineMenuTarget(ViewModels.OnlineSearchItem Item, ViewModels.OnlineSearchViewModel Vm);

    private OnlineMenuTarget? ResolveOnlineMenuTarget(object sender)
    {
        if (sender is not System.Windows.Controls.MenuItem mi || mi.Parent is not ContextMenu menu) return null;

        // 首选：PlacementTarget = 打开菜单的行（框架在打开时设置）
        if (menu.PlacementTarget is System.Windows.Controls.ListBoxItem row
            && row.DataContext is ViewModels.OnlineSearchItem item
            && FindAncestor<System.Windows.Controls.ListBox>(row)?.DataContext is ViewModels.OnlineSearchViewModel vm)
            return new OnlineMenuTarget(item, vm);

        // 兜底：右键按下时的捕获值
        if (_onlineMenuItem is { } captured && _onlineMenuVm is { } capturedVm)
            return new OnlineMenuTarget(captured, capturedVm);

        return null;
    }

    /// <summary>
    /// 右键菜单打开时：①从 OriginalSource 向上找到所属行（OriginalSource 是行内元素，不是 ListBoxItem），
    /// 顺便捕获行数据；②把 Click 处理器挂到菜单实例上。WPF 铁律：Style Setter 里的 XAML Click 事件处理器
    /// 会被静默丢弃（实测 2026-08-15 不触发），必须程序化 AddHandler；同一实例只挂一次。
    /// </summary>
    private ContextMenu? _wiredOnlineMenu;

    private void OnOnlineSearchContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var row = FindAncestor<System.Windows.Controls.ListBoxItem>(e.OriginalSource as DependencyObject);
        if (row is null) return;

        _onlineMenuItem = row.DataContext as ViewModels.OnlineSearchItem;
        _onlineMenuVm = FindAncestor<System.Windows.Controls.ListBox>(row)?.DataContext as ViewModels.OnlineSearchViewModel;

        if (row.ContextMenu is not { } menu) return;
        if (!ReferenceEquals(menu, _wiredOnlineMenu))
        {
            menu.AddHandler(System.Windows.Controls.MenuItem.ClickEvent,
                new RoutedEventHandler(OnOnlineSearchMenuItemClick));
            _wiredOnlineMenu = menu;
        }
    }

    private void OnOnlineSearchMenuItemClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not System.Windows.Controls.MenuItem mi) return;
        switch (mi.Header as string)
        {
            case "试听（临时播放）":
                if (ResolveOnlineMenuTarget(mi) is { } t)
                    _ = Player?.PlayOnlinePreviewAsync(t.Item.Track, t.Item.SourceKey, t.Vm.SelectedBr);
                break;
            case "下载":
                var dl = ResolveOnlineMenuTarget(mi);
                if (dl is null) break;
                dl.Vm.SelectedItem = dl.Item;   // 命令作用于选中项
                dl.Vm.DownloadSelectedCommand.Execute(null);
                break;
        }
    }

    /// <summary>P4-5 下载管理：重复确认「继续下载」。</summary>
    private void OnDownloadConfirmClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.FrameworkElement { DataContext: ViewModels.DownloadPageViewModel.DownloadRow row })
            (Shell?.CurrentPage as ViewModels.DownloadPageViewModel)?.Confirm(row);
    }

    /// <summary>P4-5 下载管理：重复确认「取消」。</summary>
    private void OnDownloadCancelClick(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.FrameworkElement { DataContext: ViewModels.DownloadPageViewModel.DownloadRow row })
            (Shell?.CurrentPage as ViewModels.DownloadPageViewModel)?.Cancel(row);
    }

    /// <summary>P4 实机反馈：下载时弹出目标选择（媒体库根/一级子文件夹 + 自定义），返回选中目录或 null（取消）。</summary>
    private string? PickDownloadDirectory(string? current)
    {
        var candidates = new List<string>();
        foreach (var root in ConfigService.Current.Library.Folders)
        {
            if (!Directory.Exists(root)) continue;
            candidates.Add(root);
            try
            {
                foreach (var sub in Directory.EnumerateDirectories(root))
                    candidates.Add(sub);
            }
            catch { /* 无权限子目录跳过 */ }
        }
        if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current)
            && !candidates.Any(c => string.Equals(c, current, System.StringComparison.OrdinalIgnoreCase)))
            candidates.Add(current);
        if (candidates.Count == 0)
            candidates.Add(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        var dialog = new Controls.DownloadDirDialog(candidates, current) { Owner = this };
        return dialog.ShowDialog() == true ? dialog.SelectedDir : null;
    }

    /// <summary>L3.2 迷你窗按钮④：开=主窗隐藏+迷你窗显示；关=Escape/回主窗按钮/再按。</summary>
    private void OnMiniButtonClick(object sender, RoutedEventArgs e)
    {
        if (_miniWindow is null)
        {
            _miniWindow = new Controls.MiniPlayerWindow(Player!)
            {
                Owner = null   // 独立置顶小窗，不随主窗
            };
            _miniWindow.RestoreRequested += ShowMainFromMini;
            _miniWindow.Show();
            Hide();
            return;
        }

        if (_miniWindow.IsVisible)
        {
            _miniWindow.Hide();
            ShowMainFromMini();
        }
        else
        {
            _miniWindow.Show();
            Hide();
        }
    }

    /// <summary>迷你窗请求回主窗：主窗恢复显示（迷你窗已自行隐藏）。</summary>
    private void ShowMainFromMini()
    {
        if (!IsVisible)
        {
            Show();
            if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
            Activate();
        }
    }

    /// <summary>L2 托盘：Player 就绪后创建（菜单播放控制需要命令）。</summary>
    private void InitTray()
    {
        if (_tray is not null || Player is null) return;
        try
        {
            _tray = new Player.App.SystemTray.TrayService(Player, ToggleDesktopLyrics, ShowMainWindowFromTray, ExitFromTray);
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "托盘初始化失败");
        }
    }

    /// <summary>托盘「显示主窗」/ 双击还原：恢复最小化并抢焦点。</summary>
    private void ShowMainWindowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    /// <summary>托盘「退出」：唯一显式退出路径（"关闭到托盘"开启时关窗只是隐藏）。</summary>
    private void ExitFromTray()
    {
        _exitingFromTray = true;
        System.Windows.Application.Current.Shutdown();
    }

    /// <summary>
    /// L2 全局热键：按配置开/关注册。开启时被占用的组合不抢（其余照常生效），
    /// 逐条列给用户明确提示，不静默。
    /// </summary>
    private void ApplyGlobalHotkeys(bool force = false)
    {
        if (Player is null) return;
        var enabled = ConfigService.Current.Ui.GlobalHotkeysEnabled;
        if (enabled && (_globalHotkeys is null || force))
        {
            _globalHotkeys?.Dispose();
            _globalHotkeys = null;
            try
            {
                _globalHotkeys = new Player.App.GlobalHotkeys.GlobalHotkeyService(
                    new WindowInteropHelper(this).Handle, Player, BuildGlobalHotkeyCombos());
                if (_globalHotkeys.Conflicts.Count > 0)
                {
                    System.Windows.MessageBox.Show(this,
                        "以下全局热键注册失败（可能已被其他程序占用）：\n\n- " +
                        string.Join("\n- ", _globalHotkeys.Conflicts) +
                        "\n\n其余热键已正常生效。",
                        "全局热键", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
                }
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "全局热键初始化失败");
                _globalHotkeys = null;
            }
        }
        else if (!enabled && _globalHotkeys is not null)
        {
            _globalHotkeys.Dispose();
            _globalHotkeys = null;
        }
    }

    /// <summary>预设组合 + 配置覆盖（GlobalHotkeyCombos 按名字覆盖）。</summary>
    private static IReadOnlyList<(string Name, string Combo)> BuildGlobalHotkeyCombos()
    {
        var overrides = ConfigService.Current.Ui.GlobalHotkeyCombos;
        return GlobalHotkeys.GlobalHotkeyService.DefaultCombos
            .Select(c => (c.Name, overrides.TryGetValue(c.Name, out var over) ? over : c.Combo))
            .ToList();
    }

    /// <summary>关窗前记下窗口尺寸，退出时随配置落盘。</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // L2 托盘（B4 ShutdownMode 语义）：开启"关闭到托盘"时关主窗 = 隐藏而非退出，
        // 进程与托盘继续存活（SMTC 媒体键也不断）；退出只能走托盘菜单。
        // 会话结束（关机/重启/注销）与托盘「退出」都放行真实关闭（审查修复）。
        if (ConfigService.Current.Ui.CloseToTray && !_exitingFromTray && !_sessionEnding)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        _smtcRetryTimer?.Stop();
        _smtcRetryTimer = null;
        _tray?.Dispose();
        _tray = null;
        _globalHotkeys?.Dispose();
        _globalHotkeys = null;
        _smtcService?.Dispose();
        _smtcService = null;
        if (_miniWindow is not null)
        {
            _miniWindow.AllowRealClose = true;
            _miniWindow.Close();
            _miniWindow = null;
        }
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

    /// <summary>
    /// 窗口级键盘统一入口（L1 保留项 + L2 应用内快捷键，一条规则链）：
    /// - Tab：全局吞掉并切换平铺/封面模式（L1 目验九）；
    /// - 大歌词页打开时方向键吞掉（无焦点框，L1）；
    /// - Esc：先退大歌词页，再退设置页（L1）；
    /// - L2 快捷键（Space/←→/Ctrl+←→/Ctrl+F/Enter/Delete/Ctrl+L/F5）走 ShortcutPolicy——
    ///   任何文本输入框/下拉框聚焦时一律不响应（L2 约束②），按钮聚焦 Space 归按钮、滑条聚焦方向键归滑条。
    /// </summary>
    private void Window_OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab)
        {
            if (CurrentTrackPage is { } page && page.ToggleViewModeCommand.CanExecute(null))
                page.ToggleViewModeCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (_bigLyricsVisible && (e.Key == Key.Left || e.Key == Key.Right || e.Key == Key.Up || e.Key == Key.Down))
        {
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape)
        {
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
            return;
        }

        // ===== L2 应用内快捷键（可改绑，ShortcutMap 裁决） =====

        // 改绑捕获优先：任何按键都进捕获流程，不再走正常分发
        if (_rebindAction is not null || _rebindGlobalName is not null)
        {
            HandleRebindCapture(e);
            e.Handled = true;
            return;
        }

        var keyName = e.Key == Key.System ? e.SystemKey.ToString() : e.Key.ToString();
        if (_shortcutMap is null
            || !_shortcutMap.TryResolve(keyName, ModsFrom(Keyboard.Modifiers), CurrentFocusKind(), out var shortcut))
        {
            return;
        }

        e.Handled = true;
        switch (shortcut)
        {
            case ShortcutKey.Space:
                Player?.PlayPauseCommand.Execute(null);   // 全局播放/暂停（与大歌词页同一条规则）
                break;
            case ShortcutKey.SeekBack:
                SeekRelative(-5);
                break;
            case ShortcutKey.SeekForward:
                SeekRelative(+5);
                break;
            case ShortcutKey.PrevTrack:
                Player?.PreviousCommand.Execute(null);
                break;
            case ShortcutKey.NextTrack:
                Player?.NextCommand.Execute(null);
                break;
            case ShortcutKey.FocusSearch:
                FilterBox?.Focus();
                break;
            case ShortcutKey.Enter:
                if (CurrentTrackPage is { } page && page.SelectedTrack is { } track) page.Play(track);
                else if (Shell?.CurrentPage is ViewModels.OnlineSearchViewModel search && search.SelectedItem is { } item)
                    _ = Player?.PlayOnlinePreviewAsync(item.Track, item.SourceKey, search.SelectedBr);   // P4：Enter 试听选中结果
                break;
            case ShortcutKey.Delete:
                if (CurrentTrackPage is { } editPage && editPage.CanEdit)
                    editPage.RemoveSelectedCommand.Execute(null);
                break;
            case ShortcutKey.Locate:
                Player?.LocateCurrentTrackCommand.Execute(null);
                break;
            case ShortcutKey.Rescan:
                if (Shell is { } shell) _ = shell.ScanAsync(fullRescan: false);
                break;
        }
    }

    /// <summary>相对当前播放位置 seek（±5 秒快捷键）。</summary>
    private void SeekRelative(double seconds)
    {
        if (Player is null || !Player.HasTrack) return;
        var target = Math.Clamp(Player.PositionSeconds + seconds, 0, Math.Max(1, Player.DurationSeconds));
        Player.EndSeek(target);
    }

    // ================= L2 快捷键自定义改绑 =================

    /// <summary>按配置重建应用内快捷键映射（改绑后立即生效）。</summary>
    private void RebuildShortcutMap()
    {
        _shortcutMap = new ShortcutMap(ConfigService.Current.Ui.ShortcutBindings);
    }

    private static ModifierMask ModsFrom(ModifierKeys m)
    {
        var mask = ModifierMask.None;
        if (m.HasFlag(ModifierKeys.Control)) mask |= ModifierMask.Ctrl;
        if (m.HasFlag(ModifierKeys.Shift)) mask |= ModifierMask.Shift;
        if (m.HasFlag(ModifierKeys.Alt)) mask |= ModifierMask.Alt;
        return mask;
    }

    private void OnRebindRequested(ShortcutKey action)
    {
        _rebindAction = action;
        _rebindGlobalName = null;
    }

    private void OnRebindGlobalRequested(string name)
    {
        _rebindGlobalName = name;
        _rebindAction = null;
    }

    /// <summary>捕获按键：校验 → 落盘 → 重建映射/重注册全局热键。</summary>
    private void HandleRebindCapture(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Escape or Key.Tab)
        {
            CancelRebind("已取消改绑");
            return;
        }

        var mods = ModsFrom(Keyboard.Modifiers);
        var combo = ShortcutMap.Format(mods, key.ToString());
        if (!ShortcutMap.TryParse(combo, out _, out var keyName) || !KeyNames.IsAllowed(keyName))
        {
            _settingsVm?.SetRebindStatus("不支持该按键，请按其他组合（Esc 取消）");
            return;
        }

        if (_rebindAction is { } action)
        {
            var map = new ShortcutMap(ConfigService.Current.Ui.ShortcutBindings);
            if (map.TryAddOverride(action, combo))
            {
                ConfigService.Current.Ui.ShortcutBindings[action.ToString()] = combo;
                ConfigService.Save();
                RebuildShortcutMap();
                _rebindAction = null;
                _settingsVm?.EndRebind(true, $"已改绑：{ShortcutMap.Describe(action)} → {combo}");
            }
            else
            {
                _settingsVm?.SetRebindStatus("组合无效或已被其他动作占用，请换一个（Esc 取消）");
            }
            return;
        }

        if (_rebindGlobalName is { } name)
        {
            // 全局热键必须带修饰键，且不能与其他全局组合重复
            if (mods == ModifierMask.None)
            {
                _settingsVm?.SetRebindStatus("全局热键需要至少一个修饰键（Ctrl/Shift/Alt），请重按（Esc 取消）");
                return;
            }
            var duplicates = ConfigService.Current.Ui.GlobalHotkeyCombos
                .Where(kv => kv.Key != name)
                .Any(kv => kv.Value == combo);
            if (duplicates)
            {
                _settingsVm?.SetRebindStatus("该组合已用于其他全局热键，请换一个（Esc 取消）");
                return;
            }
            ConfigService.Current.Ui.GlobalHotkeyCombos[name] = combo;
            ConfigService.Save();
            _rebindGlobalName = null;
            ApplyGlobalHotkeys(force: true);
            _settingsVm?.EndRebind(true, $"全局热键已改绑：{combo}");
        }
    }

    private void CancelRebind(string message)
    {
        _rebindAction = null;
        _rebindGlobalName = null;
        _settingsVm?.EndRebind(false, message);
    }

    /// <summary>当前键盘焦点类别（沿可视树向上归类；文本框/下拉/按钮/滑条/列表优先于普通区）。</summary>
    private static FocusKind CurrentFocusKind()
    {
        var f = Keyboard.FocusedElement as DependencyObject;
        while (f is not null)
        {
            switch (f)
            {
                case System.Windows.Controls.Primitives.TextBoxBase or System.Windows.Controls.PasswordBox:
                    return FocusKind.TextInput;
                case ComboBox:
                    return FocusKind.ComboBox;
                case Slider:
                    return FocusKind.Slider;
                case System.Windows.Controls.Primitives.ButtonBase:
                    return FocusKind.ButtonBase;
                case System.Windows.Controls.ListBox:
                    return FocusKind.ListBox;
            }
            f = f is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(f)
                : LogicalTreeHelper.GetParent(f);
        }
        return FocusKind.None;
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

    /// <summary>桌面歌词右键菜单"字体设置…"：跳设置页外观区（歌词组在此区，L3.0-3 分区重构）。</summary>
    private void OpenLyricSettingsFromDesktopLyrics()
    {
        if (Shell is not { } shell) return;
        if (shell.OpenSettingsCommand.CanExecute(null)) shell.OpenSettingsCommand.Execute(null);
        if (shell.CurrentPage is SettingsPageViewModel settings)
            settings.SelectedSection = settings.Sections.FirstOrDefault(s => s.Key == "appearance") ?? settings.Sections[0];
    }

    private void OnDesktopLyricsButtonClick(object sender, RoutedEventArgs e) => ToggleDesktopLyrics();

    private void ToggleDesktopLyrics()
    {
        // 目验修复④：打开桌面歌词前关闭按钮 ToolTip（避免弹出层悬浮在歌词条上）
        DesktopLyricsButtonTip.IsOpen = false;
        if (_desktopLyrics is null)
        {
            _desktopLyrics = CreateDesktopLyricsWindow();
            _desktopLyrics.ForceUnlocked();   // 重新打开必定解锁（目验修复：锁柄可能在屏幕外）
            _desktopLyrics.Show();
        }
        else if (_desktopLyrics.IsVisible)
        {
            _desktopLyrics.Hide();
        }
        else
        {
            _desktopLyrics.ForceUnlocked();   // 隐藏后再显示：必定回到解锁态
            _desktopLyrics.Show();
            _desktopLyrics.ApplySettings();
            UpdateDesktopLyrics();
        }
        ConfigService.Current.Ui.DesktopLyricsEnabled = _desktopLyrics.IsVisible;
        ConfigService.Save();
        // L2 托盘：菜单勾选与播放条按钮保持同步
        _tray?.RefreshDesktopLyricsCheck();
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
            if (Shell.CurrentPage is not SettingsPageViewModel settings)
            {
                // 离开设置页：取消进行中的改绑捕获
                CancelRebind("已取消改绑");
                _settingsVm = null;
                return;
            }
            _settingsVm = settings;
            settings.RebindRequested += OnRebindRequested;
            settings.RebindGlobalRequested += OnRebindGlobalRequested;
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

                    // L2 全局热键：开关即时生效（占用冲突逐条提示）
                    case nameof(SettingsPageViewModel.GlobalHotkeysEnabled):
                        ApplyGlobalHotkeys();
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

    private void OnBigLyricsButtonClick(object sender, RoutedEventArgs e) => ToggleBigLyrics();

    /// <summary>大歌词页：点击歌词行=跳转播放（充当进度条）；同时取消待执行的空白切歌（目验八）。</summary>
    private void OnBigLyricClicked(int index)
    {
        CancelPendingBlankNav();
        Player?.Lyrics.SeekToLine(index);
    }

    /// <summary>大歌词页：双击任意位置=退出。</summary>
    private void OnBigLyricDoubleClicked() => ToggleBigLyrics();

    /// <summary>大歌词页：点击空白——左半=上一曲、右半=下一曲（目验五修复：点击判定收进 LyricCanvas，命中行与空白不再分层）。
    /// 目验八修复：切歌延迟 300ms 判定——窗口期内第二次空白点击=双击=取消切歌并退出；
    /// 两次点击间隔 ≥300ms 才各自切歌。</summary>
    private System.Windows.Threading.DispatcherTimer? _blankClickTimer;
    private bool _pendingNavRight;

    private void OnBigLyricBlankClicked(double x)
    {
        if (Player is null) return;

        if (_blankClickTimer?.IsEnabled == true)
        {
            // 300ms 内第二次空白点击：双击 → 取消待执行切歌并退出
            _blankClickTimer.Stop();
            ToggleBigLyrics();
            return;
        }

        _pendingNavRight = x >= BigLyricCanvas.ActualWidth / 2;
        _blankClickTimer ??= new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(300)
        };
        _blankClickTimer.Tick -= OnBlankClickTimerTick;
        _blankClickTimer.Tick += OnBlankClickTimerTick;
        _blankClickTimer.Stop();
        _blankClickTimer.Start();
    }

    private void OnBlankClickTimerTick(object? sender, EventArgs e)
    {
        if (_blankClickTimer is null) return;
        _blankClickTimer.Stop();
        if (Player is null) return;
        if (_pendingNavRight) Player.NextCommand.Execute(null);
        else Player.PreviousCommand.Execute(null);
    }

    /// <summary>点行跳转是即时动作；若恰在待切歌窗口内，取消待执行的切歌（用户已改主意）。</summary>
    private void CancelPendingBlankNav()
    {
        if (_blankClickTimer?.IsEnabled == true) _blankClickTimer.Stop();
    }

    /// <summary>大歌词页键盘（L2 统一：空格已并入窗口级全局播放/暂停规则，这里只吞掉焦点键）。</summary>
    private void OnBigLyricsPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab || e.Key == Key.Left || e.Key == Key.Right || e.Key == Key.Up || e.Key == Key.Down)
        {
            e.Handled = true;   // 不做焦点移动（无焦点框）
        }
    }

    /// <summary>目验六修复：覆盖层兜底——点击落在歌词画布之外（左右 60px 边距等）时按左/右半区切曲，双击退出。</summary>
    private void OnBigLyricsOverlayMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 画布自身已处理其区域内的点击（透明背景使空白也可命中，行点击=跳转、空白=切曲、双击=退出）
        var source = e.OriginalSource as DependencyObject;
        while (source is not null)
        {
            if (source is LyricCanvas) return;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
        if (e.ClickCount >= 2)
        {
            ToggleBigLyrics();
            e.Handled = true;
            return;
        }
        if (Player is null || sender is not FrameworkElement fe) return;
        var x = e.GetPosition(fe).X;
        if (x >= fe.ActualWidth / 2) Player.NextCommand.Execute(null);
        else Player.PreviousCommand.Execute(null);
        e.Handled = true;
    }

    private void ToggleBigLyrics()
    {
        _bigLyricsVisible = !_bigLyricsVisible;
        if (_bigLyricsVisible)
        {
            // 目验修复④：覆盖层打开前关闭按钮 ToolTip（其弹出层是独立置顶窗口，否则会悬浮在覆盖层上）
            BigLyricsButtonTip.IsOpen = false;
            BigLyricsOverlay.Visibility = Visibility.Visible;
            // 目验五修复：覆盖层取得键盘焦点（空格/方向键等只在大页内生效；FocusVisualStyle 已置空无焦点框）
            BigLyricsOverlay.Focus();
            // 目验七修复：大页期间窗口 Tab 导航整体关闭（任何焦点位置按 Tab 都不动，选框无法出现）
            KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.None);
            BigLyricsOverlay.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        }
        else
        {
            KeyboardNavigation.SetTabNavigation(this, KeyboardNavigationMode.Continue);
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(200));
            // 复查修复：重入守卫——关闭淡出期间若已重新打开，旧回调不得把覆盖层拉回 Collapsed
            fade.Completed += (_, _) =>
            {
                if (!_bigLyricsVisible) BigLyricsOverlay.Visibility = Visibility.Collapsed;
            };
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


