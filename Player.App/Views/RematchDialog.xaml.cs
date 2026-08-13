using System.Windows;
using Player.Core.Online;
using Wpf.Ui.Controls;

namespace Player.App.Views;

/// <summary>
/// 手动重新匹配的候选选择框。返回 null = 取消；返回 Id &lt;= 0 的 SearchSong = 清除匹配；
/// 否则是用户选中的网易云曲目。
/// </summary>
public partial class RematchDialog : FluentWindow
{
    private SearchSong? _choice;

    public RematchDialog()
    {
        InitializeComponent();
    }

    public static SearchSong? Show(IReadOnlyList<SearchSong> candidates, string trackTitle)
    {
        var owner = Application.Current?.MainWindow;

        var dialog = new RematchDialog();
        if (owner is not null && !ReferenceEquals(owner, dialog) && owner.IsLoaded)
            dialog.Owner = owner;

        dialog.HintText.Text = $"为「{trackTitle}」选择正确的网易云曲目（双击也可直接应用）：";
        dialog.CandidateList.ItemsSource = candidates;

        dialog.ShowDialog();
        return dialog._choice;
    }

    private void OnListDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (CandidateList.SelectedItem is SearchSong song)
        {
            _choice = song;
            DialogResult = true;
            Close();
        }
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        _choice = CandidateList.SelectedItem as SearchSong;
        DialogResult = true;
        Close();
    }

    private void OnClearClick(object sender, RoutedEventArgs e)
    {
        // 哨兵：Id <= 0 表示"清除匹配"
        _choice = new SearchSong { Id = -1, Name = string.Empty, Artists = string.Empty };
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
