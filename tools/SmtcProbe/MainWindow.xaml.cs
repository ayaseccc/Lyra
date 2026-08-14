using System;
using System.Windows;
using System.Windows.Interop;
using Windows.Media;
using Windows.Media.Core;

namespace SmtcProbe;

public partial class MainWindow : Window
{
    private SystemMediaTransportControls? _smtc;
    private bool _playing;

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        try
        {
            // 桌面应用 SMTC：Interop.GetForWindow 绑定本窗口
            _smtc = SystemMediaTransportControlsInterop.GetForWindow(hwnd);
            _smtc.IsEnabled = true;
            _smtc.IsPlayEnabled = true;
            _smtc.IsPauseEnabled = true;
            _smtc.IsNextEnabled = true;
            _smtc.IsPreviousEnabled = true;
            _smtc.PlaybackStatus = MediaPlaybackStatus.Closed;

            var updater = _smtc.DisplayUpdater;
            updater.Type = MediaPlaybackType.Music;
            updater.MusicProperties.Title = "ドライフラワー";
            updater.MusicProperties.Artist = "優里";
            updater.MusicProperties.AlbumTitle = "ドライフラワー";
            updater.Update();

            _smtc.ButtonPressed += (_, args) =>
            {
                var label = args.Button switch
                {
                    SystemMediaTransportControlsButton.Play => "PLAY",
                    SystemMediaTransportControlsButton.Pause => "PAUSE",
                    SystemMediaTransportControlsButton.Next => "NEXT",
                    SystemMediaTransportControlsButton.Previous => "PREV",
                    _ => args.Button.ToString()
                };
                Dispatcher.Invoke(() => Status.Text += "\n媒体键: " + label);
            };

            Status.Text = "SMTC OK: GetForWindow 成功\n媒体键按下会显示在下方";
        }
        catch (Exception ex)
        {
            Status.Text = "SMTC 失败: HRESULT=0x" + ex.HResult.ToString("X8") + "\n" + ex;
        }
    }

    private void OnToggle(object sender, RoutedEventArgs e)
    {
        if (_smtc is null) return;
        _playing = !_playing;
        _smtc.PlaybackStatus = _playing ? MediaPlaybackStatus.Playing : MediaPlaybackStatus.Paused;
        ToggleBtn.Content = _playing ? "暂停（Paused）" : "播放（Playing）";
    }
}
