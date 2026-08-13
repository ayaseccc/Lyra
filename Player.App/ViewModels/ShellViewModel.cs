using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using Player.App.Views;
using Player.Core.Audio;
using Player.Core.Infra;
using Player.Core.Library;
using Serilog;
using Wpf.Ui.Controls;

namespace Player.App.ViewModels;

/// <summary>
/// 主窗口的编排者：左侧栏导航、页面切换、扫描调度、拖放入库。
/// 所有耗时活儿都在后台线程，UI 线程只负责显示（PLAN P1 约束：扫描不得阻塞 UI）。
/// </summary>
public sealed partial class ShellViewModel : ObservableObject, IDisposable
{
    private readonly LibraryService _library;
    private readonly PlaylistService _playlists;
    private readonly IPlaybackEngine _engine;
    private readonly Dispatcher _dispatcher;

    private bool _suppressNavigation;
    private bool _rebuildQueued;
    private bool _pendingLibraryChanged;
    private long? _pendingPlaylistSelection;
    private bool _disposed;

    public ShellViewModel(LibraryService library, PlaylistService playlists, PlayerViewModel player,
        IPlaybackEngine engine)
    {
        _library = library;
        _playlists = playlists;
        _engine = engine;
        Player = player;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;

        _library.LibraryChanged += OnLibraryChanged;
        _library.ScanStarted += OnScanStarted;
        _library.ScanProgressChanged += OnScanProgressChanged;
        _library.ScanCompleted += OnScanCompleted;
        _playlists.PlaylistsChanged += OnPlaylistsChanged;
        Player.PropertyChanged += OnPlayerPropertyChanged;
    }

    public PlayerViewModel Player { get; }

    public ObservableCollection<NavItemViewModel> NavItems { get; } = new();

    [ObservableProperty]
    private NavItemViewModel? _selectedNav;

    [ObservableProperty]
    private object? _currentPage;

    [ObservableProperty]
    private bool _isScanning;

    [ObservableProperty]
    private string _scanStatus = string.Empty;

    [ObservableProperty]
    private double _scanPercent;

    [ObservableProperty]
    private bool _hasRoots;

    // ---------------- 启动 ----------------

    public async Task InitializeAsync()
    {
        // 万级曲库的载入与聚合放后台线程，别卡住第一帧
        await Task.Run(() =>
        {
            _library.Load();
            _playlists.Load();
        });

        RebuildNavigation();
        HasRoots = _library.Roots.Count > 0;

        SelectFirstPage();

        if (HasRoots)
        {
            _library.StartWatching();
            // 启动即跑一次增量扫描，把上次退出后新增/删除的文件补上
            await ScanAsync(fullRescan: false);
        }
        else
        {
            ScanStatus = "还没有配置音乐文件夹";
        }
    }

    private void SelectFirstPage()
    {
        var first = NavItems.FirstOrDefault(n => n.Kind == NavKind.AllTracks);
        if (first is not null) SelectedNav = first;
    }

    // ---------------- 导航 ----------------

    private static string? NavKey(NavItemViewModel? nav) =>
        nav is null ? null : $"{nav.Kind}|{nav.PlaylistId}|{nav.FolderPath}";

    /// <summary>把重建排进队列；同一轮里多次触发（载入曲库 + 载入歌单 + 扫描完成）只重建一次。</summary>
    private void QueueRebuild(bool libraryChanged)
    {
        _pendingLibraryChanged |= libraryChanged;

        if (_rebuildQueued) return;
        _rebuildQueued = true;

        _dispatcher.BeginInvoke(() =>
        {
            _rebuildQueued = false;
            var flag = _pendingLibraryChanged;
            _pendingLibraryChanged = false;
            RebuildNavigation(flag);
        });
    }

    private void RebuildNavigation(bool libraryChanged = false)
    {
        var previousKey = NavKey(SelectedNav);

        _suppressNavigation = true;
        NavItems.Clear();

        NavItems.Add(new NavItemViewModel { Kind = NavKind.Header, Title = "媒体库" });
        NavItems.Add(new NavItemViewModel
        {
            Kind = NavKind.AllTracks, Title = "全部歌曲", Icon = SymbolRegular.MusicNote224,
            CountText = _library.Tracks.Count.ToString()
        });
        NavItems.Add(new NavItemViewModel
        {
            Kind = NavKind.Albums, Title = "专辑", Icon = SymbolRegular.Album24,
            CountText = _library.GetAlbums().Count.ToString()
        });
        NavItems.Add(new NavItemViewModel
        {
            Kind = NavKind.Artists, Title = "艺术家", Icon = SymbolRegular.Person24,
            CountText = _library.GetArtists().Count.ToString()
        });

        var manual = _playlists.Playlists;
        NavItems.Add(new NavItemViewModel { Kind = NavKind.Header, Title = "歌单" });
        if (manual.Count == 0)
        {
            NavItems.Add(new NavItemViewModel
            {
                Kind = NavKind.Header, Title = "（还没有歌单）"
            });
        }
        foreach (var playlist in manual)
        {
            var id = playlist.Id;
            var name = playlist.Name;

            NavItems.Add(new NavItemViewModel
            {
                Kind = NavKind.Playlist,
                Title = name,
                Icon = SymbolRegular.AppsList24,
                PlaylistId = id,
                RenameCommand = new RelayCommand(() => RenamePlaylist(id, name)),
                DeleteCommand = new RelayCommand(() => DeletePlaylist(id, name))
            });
        }

        var folders = _library.GetFolderPlaylists();
        if (folders.Count > 0)
        {
            NavItems.Add(new NavItemViewModel { Kind = NavKind.Header, Title = "文件夹" });
            foreach (var folder in folders)
            {
                NavItems.Add(new NavItemViewModel
                {
                    Kind = NavKind.FolderPlaylist,
                    Title = folder.Name,
                    Icon = SymbolRegular.Folder24,
                    FolderPath = folder.FullPath,
                    CountText = folder.TrackCount.ToString()
                });
            }
        }

        // 新建/导入歌单后要跳到那个歌单上；歌单列表是异步重建的，所以用一个待选 id 转交
        NavItemViewModel? target = null;
        if (_pendingPlaylistSelection is { } pendingId)
        {
            target = NavItems.FirstOrDefault(n => n.Kind == NavKind.Playlist && n.PlaylistId == pendingId);
            _pendingPlaylistSelection = null;
        }

        target ??= previousKey is null
            ? null
            : NavItems.FirstOrDefault(n => NavKey(n) == previousKey);

        var sameAsBefore = target is not null && NavKey(target) == previousKey;

        SelectedNav = target;          // 仍在抑制中，这一步不会触发导航
        _suppressNavigation = false;

        if (target is null) return;

        if (!sameAsBefore)
        {
            Navigate(target);
            return;
        }

        // 只是数据刷新：从专辑/艺术家钻进来的页面保持原样，免得把用户弹回上一层
        if (CurrentPage is TrackListPageViewModel { HasBack: true }) return;

        // 当前页的数据没变就别重建：重建会把滚动位置、多选、列排序全丢掉。
        // 歌单变更只影响歌单页；曲库变更才影响全部歌曲/文件夹等页面。
        var pageAffected = libraryChanged || CurrentPage is TrackListPageViewModel { IsPlaylistPage: true };
        if (!pageAffected) return;

        Navigate(target, isRefresh: true);
    }

    partial void OnSelectedNavChanged(NavItemViewModel? value)
    {
        if (_suppressNavigation || value is null || value.IsHeader) return;
        Navigate(value);
    }

    private void Navigate(NavItemViewModel nav, bool isRefresh = false)
    {
        // 只有"扫描完成后刷新当前页"才把过滤词带过去；用户主动切页时不该继承上一页的过滤
        var keepFilter = isRefresh
            ? (CurrentPage as TrackListPageViewModel)?.FilterText ?? string.Empty
            : string.Empty;

        switch (nav.Kind)
        {
            case NavKind.AllTracks:
                CurrentPage = CreateTrackPage("全部歌曲", _library.Tracks, "全部歌曲", filter: keepFilter);
                break;

            case NavKind.Albums:
                CurrentPage = new AlbumPageViewModel(_library.GetAlbums(), OpenAlbum, PlayAlbum);
                break;

            case NavKind.Artists:
                CurrentPage = new ArtistPageViewModel(_library.GetArtists(), OpenArtist, PlayArtist);
                break;

            case NavKind.Playlist:
            {
                var playlistId = nav.PlaylistId;
                CurrentPage = CreateTrackPage(
                    nav.Title, _playlists.GetTracks(playlistId), "歌单：" + nav.Title,
                    playlistId: playlistId, filter: keepFilter);
                break;
            }

            case NavKind.FolderPlaylist:
            {
                var folder = _library.GetFolderPlaylists()
                    .FirstOrDefault(f => string.Equals(f.FullPath, nav.FolderPath, StringComparison.OrdinalIgnoreCase));
                CurrentPage = CreateTrackPage(
                    nav.Title,
                    folder?.Tracks ?? Array.Empty<TrackRecord>(),
                    "文件夹：" + nav.Title,
                    filter: keepFilter);
                break;
            }

            case NavKind.Settings:
                CurrentPage = new SettingsPageViewModel(_library, _engine, ScanAsync);
                break;
        }
    }

    /// <summary>
    /// 统一的建页入口。页面用到的每个命令都在这里注入，
    /// 界面上就不需要再用 {RelativeSource AncestorType=Window} 去够 Shell —— 那种绑定
    /// 在 DataTemplate 里不可靠、在右键菜单里根本不成立（P1.1 的哑按钮就是这么来的）。
    /// </summary>
    private TrackListPageViewModel CreateTrackPage(
        string title,
        IEnumerable<TrackRecord> tracks,
        string sourceName,
        long? playlistId = null,
        string? backTitle = null,
        IRelayCommand? backCommand = null,
        string filter = "")
    {
        var page = new TrackListPageViewModel(title, tracks, sourceName, RequestPlay)
        {
            PlaylistId = playlistId,
            BackTitle = backTitle,
            BackCommand = backCommand,
            PlaylistTargets = _playlists.Playlists.ToList(),
            AddToPlaylistRequested = AddTracksToPlaylist,
            ExportRequested = ExportPage,
            AddLibraryFolderRequested = () => _ = AddLibraryFolderAsync(),
            AddFilesRequested = page => _ = AddFilesToPlaylistAsync(page),
            ItemsReordered = playlistId is null
                ? null
                : items => _playlists.SetTracks(playlistId.Value, items),
            InsertRequested = playlistId is null
                ? null
                : (index, incoming) => _playlists.InsertTracks(playlistId.Value, index, incoming)
        };

        if (!string.IsNullOrEmpty(filter)) page.FilterText = filter;
        return page;
    }

    private void RequestPlay(IReadOnlyList<TrackRecord> tracks, int startIndex, string sourceName)
        => Player.PlayTracks(tracks, startIndex, sourceName);

    private void OpenAlbum(AlbumGroup album)
    {
        var back = new RelayCommand(() => CurrentPage =
            new AlbumPageViewModel(_library.GetAlbums(), OpenAlbum, PlayAlbum));

        CurrentPage = CreateTrackPage(album.Album, album.Tracks, "专辑：" + album.Album,
            backTitle: "返回专辑", backCommand: back);
    }

    private void PlayAlbum(AlbumGroup album) => Player.PlayTracks(album.Tracks, 0, "专辑：" + album.Album);

    private void OpenArtist(ArtistGroup artist)
    {
        var back = new RelayCommand(() => CurrentPage =
            new ArtistPageViewModel(_library.GetArtists(), OpenArtist, PlayArtist));

        CurrentPage = CreateTrackPage(artist.Name, artist.Tracks, "艺术家：" + artist.Name,
            backTitle: "返回艺术家", backCommand: back);
    }

    private void PlayArtist(ArtistGroup artist) => Player.PlayTracks(artist.Tracks, 0, "艺术家：" + artist.Name);

    // ---------------- 扫描 ----------------

    public async Task ScanAsync(bool fullRescan)
    {
        if (_library.Roots.Count == 0)
        {
            ScanStatus = "还没有配置音乐文件夹";
            return;
        }

        IsScanning = true;
        ScanPercent = 0;
        ScanStatus = fullRescan ? "正在全量扫描…" : "正在检查曲库更新…";

        try
        {
            await _library.ScanAsync(fullRescan);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "扫描出错");
            ScanStatus = "扫描失败，详见日志";
        }
        finally
        {
            IsScanning = false;
        }
    }

    private void OnScanStarted(object? sender, EventArgs e) => _dispatcher.BeginInvoke(() =>
    {
        // 目录监听自动触发的扫描也要让进度条出来
        IsScanning = true;
    });

    private void OnPlayerPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // 播放器的提示（打不开、已经是最后一首……）借左下角状态行显示，否则用户根本看不到
        if (e.PropertyName != nameof(PlayerViewModel.StatusText)) return;
        if (!string.IsNullOrWhiteSpace(Player.StatusText)) ScanStatus = Player.StatusText;
    }

    private void OnScanProgressChanged(object? sender, ScanProgress progress) => _dispatcher.BeginInvoke(() =>
    {
        ScanPercent = progress.Percent;
        ScanStatus = progress.Total > 0
            ? $"{progress.Phase} {progress.Processed}/{progress.Total}"
            : progress.Phase;
    });

    private void OnScanCompleted(object? sender, ScanResult result) => _dispatcher.BeginInvoke(() =>
    {
        IsScanning = false;
        ScanPercent = 100;
        ScanStatus = result.Cancelled
            ? "扫描已取消"
            : $"曲库 {_library.Tracks.Count} 首 · {result}";
    });

    private void OnLibraryChanged(object? sender, EventArgs e) => _dispatcher.BeginInvoke(() =>
    {
        HasRoots = _library.Roots.Count > 0;
        QueueRebuild(libraryChanged: true);
    });

    private void OnPlaylistsChanged(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(() => QueueRebuild(libraryChanged: false));

    // ---------------- 命令 ----------------

    [RelayCommand]
    private void OpenSettings()
    {
        // 设置页不在左侧栏列表里，进入时清空选中项
        SelectedNav = null;
        CurrentPage = new SettingsPageViewModel(_library, _engine, ScanAsync);
    }

    [RelayCommand]
    private void CreatePlaylist()
    {
        var name = InputDialog.Show("新建歌单", "歌单名称", "新建歌单");
        if (string.IsNullOrWhiteSpace(name)) return;

        var id = _playlists.Create(name);
        _pendingPlaylistSelection = id;   // 左侧栏是异步重建的，重建完再跳过去
    }

    /// <summary>把一批曲目加进某个手工歌单（右键菜单、拖到侧边栏歌单都走这里）。</summary>
    private void AddTracksToPlaylist(PlaylistRecord playlist, IReadOnlyList<TrackRecord> tracks)
    {
        if (tracks.Count == 0)
        {
            ScanStatus = "先在列表里选中要添加的歌曲";
            return;
        }

        _playlists.AddTracks(playlist.Id, tracks);
        ScanStatus = tracks.Count == 1
            ? $"已添加到歌单「{playlist.Name}」：{tracks[0].DisplayTitle}"
            : $"已添加 {tracks.Count} 首到歌单「{playlist.Name}」";
    }

    /// <summary>空曲库时的「添加音乐文件夹」：直接开目录选择框，不再绕设置页。</summary>
    public async Task AddLibraryFolderAsync()
    {
        var dialog = new OpenFolderDialog { Title = "选择音乐文件夹", Multiselect = false };
        if (dialog.ShowDialog() != true) return;

        var folder = dialog.FolderName;
        if (string.IsNullOrWhiteSpace(folder)) return;

        if (!AddRootFolder(folder))
        {
            ScanStatus = "这个文件夹已经在媒体库里了";
            return;
        }

        await ScanAsync(fullRescan: false);
    }

    /// <summary>空歌单时的「添加文件」：选中的文件先并入曲库，再加进这个歌单。</summary>
    public async Task AddFilesToPlaylistAsync(TrackListPageViewModel page)
    {
        if (page.PlaylistId is not { } playlistId) return;

        var dialog = new OpenFileDialog
        {
            Title = "添加到歌单",
            Multiselect = true,
            Filter = AudioFormats.DialogFilter
        };

        if (dialog.ShowDialog() != true) return;

        var tracks = await _library.ImportFilesAsync(dialog.FileNames);
        if (tracks.Count == 0)
        {
            ScanStatus = "没有可添加的音频文件";
            return;
        }

        _playlists.AddTracks(playlistId, tracks);
        ScanStatus = $"已添加 {tracks.Count} 首到歌单「{page.Title}」";
    }

    /// <summary>拖到侧边栏某个歌单上：文件/文件夹先并入曲库，曲目行直接加入。</summary>
    public async Task DropOnPlaylistAsync(
        long playlistId, IReadOnlyList<string> filePaths, IReadOnlyList<TrackRecord> tracks)
    {
        var combined = new List<TrackRecord>(tracks);

        if (filePaths.Count > 0)
        {
            ScanStatus = "正在读取拖入的文件…";
            combined.AddRange(await _library.ImportFilesAsync(filePaths));
        }

        if (combined.Count == 0)
        {
            ScanStatus = "没有找到可添加的音频";
            return;
        }

        var playlist = _playlists.Playlists.FirstOrDefault(p => p.Id == playlistId);
        _playlists.AddTracks(playlistId, combined);
        ScanStatus = $"已添加 {combined.Count} 首到歌单「{playlist?.Name ?? "歌单"}」";
    }

    /// <summary>在歌单详情页里按落点插入（页内拖动、从别处拖入、从资源管理器拖入）。</summary>
    public async Task InsertIntoPlaylistAsync(
        TrackListPageViewModel page, int index, IReadOnlyList<string> filePaths, IReadOnlyList<TrackRecord> tracks)
    {
        if (page.PlaylistId is null) return;

        var combined = new List<TrackRecord>(tracks);

        if (filePaths.Count > 0)
        {
            ScanStatus = "正在读取拖入的文件…";
            combined.AddRange(await _library.ImportFilesAsync(filePaths));
        }

        if (combined.Count == 0)
        {
            ScanStatus = "没有找到可添加的音频";
            return;
        }

        page.RequestInsert(index, combined);
        ScanStatus = $"已插入 {combined.Count} 首到歌单「{page.Title}」";
    }

    /// <summary>加一个媒体库根目录（已被现有根目录覆盖时返回 false）。</summary>
    private bool AddRootFolder(string folder)
    {
        var config = ConfigService.Current.Library.Folders;

        var normalized = folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        var covered = config.Any(root =>
        {
            var normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.Equals(normalizedRoot, normalized, StringComparison.OrdinalIgnoreCase) ||
                   LibraryScanner.IsUnderAnyRoot(normalized, new[] { normalizedRoot });
        });

        if (covered) return false;

        config.Add(folder);
        ConfigService.Save();
        HasRoots = true;
        _library.StartWatching();
        return true;
    }

    private void ExportPage(TrackListPageViewModel page)
    {
        if (page.Items.Count == 0)
        {
            ScanStatus = "当前列表是空的，没什么可导出";
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "导出为 m3u8",
            Filter = "播放列表|*.m3u8",
            FileName = SanitizeFileName(page.Title) + ".m3u8"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            // 导出用户当前看到的顺序（含过滤与列排序），而不是底层原始顺序
            var visible = page.View.Cast<TrackRecord>().ToList();
            _playlists.ExportM3u(visible, dialog.FileName);
            ScanStatus = $"已导出 {visible.Count} 首：" + Path.GetFileName(dialog.FileName);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "导出 m3u8 失败");
            System.Windows.MessageBox.Show("导出失败：" + ex.Message, "Player",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }

    private void RenamePlaylist(long playlistId, string currentName)
    {
        var name = InputDialog.Show("重命名歌单", "歌单名称", currentName);
        if (string.IsNullOrWhiteSpace(name)) return;

        _playlists.Rename(playlistId, name);
    }

    private void DeletePlaylist(long playlistId, string name)
    {
        var confirm = System.Windows.MessageBox.Show(
            $"确定删除歌单「{name}」吗？（只删歌单，不动音频文件）",
            "Player", System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Question);

        if (confirm != System.Windows.MessageBoxResult.OK) return;

        _playlists.Delete(playlistId);
        SelectFirstPage();
    }

    [RelayCommand]
    private void ImportM3u()
    {
        var dialog = new OpenFileDialog
        {
            Title = "导入 m3u8 播放列表",
            Filter = "播放列表|*.m3u8;*.m3u|所有文件|*.*"
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            var (id, matched, skipped) = _playlists.ImportM3u(dialog.FileName);
            ScanStatus = $"已导入歌单：匹配 {matched} 首" + (skipped > 0 ? $"，跳过 {skipped} 首（不在曲库里）" : "");
            _pendingPlaylistSelection = id;
        }
        catch (Exception ex)
        {
            Log.Error(ex, "导入 m3u8 失败");
            System.Windows.MessageBox.Show("导入失败：" + ex.Message, "Player", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
        }
    }


    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }

    // ---------------- 拖放：入库并开播 ----------------

    public async Task HandleDroppedPathsAsync(IReadOnlyList<string> paths)
    {
        var folders = paths.Where(Directory.Exists).ToList();
        var files = paths.Where(p => File.Exists(p) && AudioFormats.IsSupported(p)).ToList();

        if (folders.Count > 0)
        {
            var config = ConfigService.Current.Library.Folders;
            var added = new List<string>();

            foreach (var folder in folders)
            {
                // 必须按目录分隔符对齐判断，否则已有 D:\Music 时拖入 D:\MusicVideos 会被当成"已覆盖"而静默丢弃
                var alreadyCovered = config.Any(root =>
                    LibraryScanner.IsUnderAnyRoot(folder, new[]
                        { root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) }));

                if (alreadyCovered) continue;

                config.Add(folder);
                added.Add(folder);
            }

            if (added.Count > 0)
            {
                ConfigService.Save();
                HasRoots = true;
                _library.StartWatching();
                ScanStatus = "已加入媒体库：" + string.Join("、", added.Select(LibraryService.RootDisplayName));
            }

            await ScanAsync(fullRescan: false);

            // 扫描完成后从第一个拖入的文件夹开始播
            var target = folders[0].TrimEnd(Path.DirectorySeparatorChar);
            var tracks = _library.Tracks
                .Where(t => t.Path.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                .OrderBy(t => t.Path, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            if (tracks.Count > 0)
                Player.PlayTracks(tracks, 0, "文件夹：" + LibraryService.RootDisplayName(target));
            else
                ScanStatus = "这个文件夹里没有找到可播放的音频";

            return;
        }

        if (files.Count == 0)
        {
            ScanStatus = "没有找到可播放的音频文件";
            return;
        }

        // 单纯拖文件进来：不入库，读一下标签直接播
        var records = await Task.Run(() => files
            .Select(f => _library.GetByPath(f) ?? TagReader.Read(f))
            .Where(r => r is not null)
            .Select(r => r!)
            .ToList());

        if (records.Count == 0)
        {
            ScanStatus = "这些文件都读不出来";
            return;
        }

        Player.PlayTracks(records, 0, "拖入的文件");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _library.LibraryChanged -= OnLibraryChanged;
        _library.ScanStarted -= OnScanStarted;
        _library.ScanProgressChanged -= OnScanProgressChanged;
        _library.ScanCompleted -= OnScanCompleted;
        _playlists.PlaylistsChanged -= OnPlaylistsChanged;
        Player.PropertyChanged -= OnPlayerPropertyChanged;
    }
}
