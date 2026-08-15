using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wpf.Ui.Controls;

namespace Player.App.ViewModels;

public enum NavKind
{
    Header,
    AllTracks,
    Albums,
    Artists,
    Playlist,
    FolderPlaylist,
    OnlineSearch,
    Downloads,
    Settings
}

/// <summary>左侧栏的一行。分组标题也是同一个集合里的条目，只是不可选中。</summary>
public sealed partial class NavItemViewModel : ObservableObject
{
    public required NavKind Kind { get; init; }

    public required string Title { get; init; }

    public SymbolRegular Icon { get; init; } = SymbolRegular.MusicNote224;

    /// <summary>手工歌单 id（Kind = Playlist 时有效）。</summary>
    public long PlaylistId { get; init; }

    /// <summary>文件夹虚拟歌单的完整路径（Kind = FolderPlaylist 时有效）。</summary>
    public string? FolderPath { get; init; }

    public bool IsHeader => Kind == NavKind.Header;

    public bool IsPlaylist => Kind == NavKind.Playlist;

    /// <summary>
    /// 右键菜单的命令直接挂在条目上，菜单里就不需要任何 RelativeSource 查找了
    /// （右键菜单是独立弹出树，跨树查找是 P1.1 那批哑绑定的根源）。
    /// </summary>
    public IRelayCommand? RenameCommand { get; init; }

    public IRelayCommand? DeleteCommand { get; init; }

    /// <summary>导出 m3u8（歌单/文件夹右键菜单，UI-R0）。</summary>
    public IRelayCommand? ExportCommand { get; init; }

    /// <summary>分组标题上的操作命令（UI-R1.5 ⑧/⑩）：媒体库标题的 + 添加文件夹、
    /// 歌单标题的 + 新建歌单、空歌单提示行「＋ 新建歌单」。</summary>
    public IRelayCommand? Command { get; init; }

    /// <summary>标题行右侧显示悬浮 + 按钮（UI-R1.5 ⑧）。</summary>
    public bool ShowAddButton { get; init; }

    /// <summary>悬浮 + 按钮的提示文案。</summary>
    public string AddToolTip { get; init; } = string.Empty;

    /// <summary>可点击的操作行（区别于普通标题）：有命令且不是「标题 + 按钮」形态。</summary>
    public bool IsAction => Command is not null && !ShowAddButton;

    [ObservableProperty]
    private string _countText = string.Empty;
}
