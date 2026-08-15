using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Player.Core.Library;

namespace Player.App.Views;

/// <summary>曲目属性对话框（用户反馈：系统 SHObjectProperties 属性页在 Win11 不弹，改自绘信息窗）。</summary>
public partial class TrackPropertiesDialog : Window
{
    public sealed record Row(string Label, string Value);

    public TrackPropertiesDialog()
    {
        InitializeComponent();
    }

    public static void Show(TrackRecord track, Window? owner)
    {
        var dialog = new TrackPropertiesDialog();
        if (owner is not null && owner.IsLoaded && !ReferenceEquals(owner, dialog))
            dialog.Owner = owner;

        dialog.TitleText.Text = track.Title;
        dialog.ArtistText.Text = track.DisplayArtist + " · " + track.DisplayAlbum;

        var rows = new ObservableCollection<Row>();
        var info = File.Exists(track.Path) ? new FileInfo(track.Path) : null;
        rows.Add(new Row("文件", Path.GetFileName(track.Path)));
        rows.Add(new Row("路径", track.Path));
        rows.Add(new Row("大小", info is null ? "—" : FormatBytes(info.Length)));
        rows.Add(new Row("时长", track.DurationText));
        rows.Add(new Row("格式", track.Format));
        rows.Add(new Row("采样率", track.SampleRateText));
        rows.Add(new Row("位深", track.BitDepthText));
        rows.Add(new Row("码率", track.BitrateText));
        rows.Add(new Row("艺术家", track.DisplayArtist));
        rows.Add(new Row("专辑", track.DisplayAlbum));
        dialog.Rows.ItemsSource = rows;

        dialog.ShowDialog();
    }

    private static string FormatBytes(long bytes)
    {
        var mb = bytes / 1024.0 / 1024.0;
        return mb >= 1 ? $"{mb:0.00} MB" : $"{bytes / 1024.0:0.0} KB";
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }
}
