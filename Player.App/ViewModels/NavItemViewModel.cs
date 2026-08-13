using CommunityToolkit.Mvvm.ComponentModel;
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

    [ObservableProperty]
    private string _countText = string.Empty;
}
