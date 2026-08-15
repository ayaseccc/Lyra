using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.Win32;

namespace Player.App.Controls;

/// <summary>下载目标选择（P4 实机反馈）：媒体库根/子文件夹 + 自定义位置。</summary>
public partial class DownloadDirDialog : Window
{
    private readonly List<string> _candidates;

    public DownloadDirDialog(IEnumerable<string> candidates, string? current)
    {
        InitializeComponent();
        _candidates = candidates.ToList();

        var index = 0;
        if (!string.IsNullOrWhiteSpace(current))
        {
            var match = _candidates.FindIndex(c => string.Equals(c, current, System.StringComparison.OrdinalIgnoreCase));
            if (match >= 0) index = match;
        }

        DirList.ItemsSource = _candidates;
        if (_candidates.Count > 0) DirList.SelectedIndex = index;
    }

    /// <summary>用户选中的目录（取消时为 null）。</summary>
    public string? SelectedDir => DirList.SelectedItem as string;

    private void OnCustomClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog
        {
            Title = "选择下载目录",
            InitialDirectory = SelectedDir ?? string.Empty
        };
        if (dialog.ShowDialog(this) != true) return;
        var dir = dialog.FolderName;
        var index = _candidates.FindIndex(c => string.Equals(c, dir, System.StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            _candidates.Add(dir);
            DirList.ItemsSource = null;
            DirList.ItemsSource = _candidates;
            index = _candidates.Count - 1;
        }
        DirList.SelectedIndex = index;
        DialogResult = true;
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        if (SelectedDir is null) return;
        DialogResult = true;
    }
}
