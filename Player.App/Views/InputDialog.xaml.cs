using System.Windows;
using Wpf.Ui.Controls;

namespace Player.App.Views;

/// <summary>新建 / 重命名歌单用的小输入框。WPF 没有内置 InputBox，自己做一个最小的。</summary>
public partial class InputDialog : FluentWindow
{
    public InputDialog()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            InputBox.Focus();
            InputBox.SelectAll();
        };
    }

    public static string? Show(string title, string label, string initialValue)
    {
        var owner = Application.Current?.MainWindow;

        var dialog = new InputDialog { Title = title };
        if (owner is not null && !ReferenceEquals(owner, dialog) && owner.IsLoaded)
            dialog.Owner = owner;

        dialog.LabelText.Text = label;
        dialog.InputBox.Text = initialValue;

        return dialog.ShowDialog() == true ? dialog.InputBox.Text.Trim() : null;
    }

    private void OnOkClick(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
