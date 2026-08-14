using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Player.App.ViewModels;

/// <summary>
/// 歌词全页视图（UI-R0 位置二）：主区正常导航进入，有返回按钮，Esc 也可退。
/// 渲染本体是自绘控件 <see cref="Controls.LyricCanvas"/>（绑定 Player.Lyrics）。
/// </summary>
public sealed partial class LyricsPageViewModel : ObservableObject
{
    public LyricsPageViewModel(PlayerViewModel player, IRelayCommand backCommand)
    {
        Player = player;
        BackCommand = backCommand;
    }

    public PlayerViewModel Player { get; }

    public IRelayCommand BackCommand { get; }

    public string Title => "歌词";

    public string Subtitle => "跟随播放 · 滚轮浏览 · 点击跳转";
}
