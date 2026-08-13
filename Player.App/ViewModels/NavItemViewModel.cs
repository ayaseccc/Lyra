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

    [ObservableProperty]
    private string _countText = string.Empty;
}
