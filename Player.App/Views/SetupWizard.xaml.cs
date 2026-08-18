using System.IO;
using System.Windows;
using System.Windows.Controls;
using Player.Core.Audio;
using Player.Core.Infra;
using Player.Core.Online;

namespace Player.App.Views;

/// <summary>P6 首次运行引导：无曲库配置时弹出（选曲库 → 选输出 → Key 可跳过）。</summary>
public partial class SetupWizard : Window
{
    /// <summary>完成且曲库目录有效：调用方应触发一次扫描。</summary>
    public bool ShouldScan { get; private set; }

    public SetupWizard()
    {
        InitializeComponent();
        DeviceCombo.ItemsSource = new[]
        {
            "DirectSound（默认，兼容性最好）",
            "WASAPI（低延迟，共享/独占可在设置页切换）",
            "ASIO（独占设备，位完美；需 ASIO 驱动）"
        };
        DeviceCombo.SelectedIndex = 0;
        FolderBox.Text = ConfigService.Current.Library.Folders.FirstOrDefault() ?? string.Empty;
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "选择曲库目录" };
        if (dialog.ShowDialog(this) == true)
            FolderBox.Text = dialog.FolderName;
    }

    private void OnFinishClick(object sender, RoutedEventArgs e)
    {
        var ui = ConfigService.Current.Ui;
        var library = ConfigService.Current.Library;

        var folder = FolderBox.Text.Trim();
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            System.Windows.MessageBox.Show("请选择一个存在的曲库目录（或直接关闭窗口跳过，之后在设置页添加）。",
                "Lyra", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        library.Folders.Clear();
        library.Folders.Add(folder);
        ui.LastSelectedLibraryFolder = folder;

        ConfigService.Current.Output.Backend = DeviceCombo.SelectedIndex switch
        {
            1 => "wasapi",
            2 => "asio",
            _ => "directsound"
        };

        var key = KeyBox.Text.Trim();
        if (!string.IsNullOrEmpty(key))
        {
            var online = ConfigService.Current.Online;
            var ep = online.ApiEndpoints.FirstOrDefault(a => a.Kind == "chksz");
            if (ep is null)
            {
                ep = new ApiEndpointConfig { Kind = "chksz", Url = "https://api.chksz.com" };
                online.ApiEndpoints.Add(ep);
            }
            ep.Key = key;
        }

        ConfigService.Save();
        ShouldScan = true;
        DialogResult = true;
        Close();
    }
}
