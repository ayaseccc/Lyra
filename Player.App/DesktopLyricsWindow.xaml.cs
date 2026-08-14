using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Player.App.ViewModels;

namespace Player.App;

/// <summary>
/// 桌面歌词（L1 第三步）：置顶无边框透明条，屏幕下方。
/// 当前句 原文+翻译 双行；锁定=鼠标穿透（缩成小柄可解锁）；解锁=拖动/调宽；
/// 颜色走主题刷子（DynamicResource，随取色主题联动）；无时间轴歌词显示曲名。
/// </summary>
public partial class DesktopLyricsWindow : Window
{
    private const int WsExTransparent = 0x00000020;
    private const int WsExLayered = 0x00080000;
    private const int GwlExStyle = -20;

    private bool _locked = true;
    private bool _dragging;
    private Point _dragStart;

    public DesktopLyricsWindow()
    {
        InitializeComponent();

        SecondaryText.Visibility = Player.Core.Infra.ConfigService.Current.Ui.DesktopLyricsTwoLines
            ? Visibility.Visible
            : Visibility.Collapsed;
        Width = Player.Core.Infra.ConfigService.Current.Ui.DesktopLyricsWidth > 200
            ? Player.Core.Infra.ConfigService.Current.Ui.DesktopLyricsWidth
            : 560;
        PrimaryText.FontSize = Player.Core.Infra.ConfigService.Current.Ui.DesktopLyricsFontSize;
        SecondaryText.FontSize = Player.Core.Infra.ConfigService.Current.Ui.DesktopLyricsFontSize * 0.7;
    }

    public LyricsViewModel? Lyrics { get; set; }

    /// <summary>设置页「歌词」组改动后即时应用（字号 / 单双行 / 宽度）。</summary>
    public void ApplySettings()
    {
        var ui = Player.Core.Infra.ConfigService.Current.Ui;
        SecondaryText.Visibility = ui.DesktopLyricsTwoLines ? Visibility.Visible : Visibility.Collapsed;
        if (ui.DesktopLyricsWidth > 200) Width = ui.DesktopLyricsWidth;
        PrimaryText.FontSize = ui.DesktopLyricsFontSize;
        SecondaryText.FontSize = ui.DesktopLyricsFontSize * 0.7;
        // 双行收起后高度回缩，避免透明区过大
        Height = ui.DesktopLyricsTwoLines ? 120 : 70;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => ApplyLockedStyle();

    private void ApplyLockedStyle()
    {
        // B1：锁定语义 = 配置值本体（此前取反导致锁定/解锁颠倒）
        _locked = Player.Core.Infra.ConfigService.Current.Ui.DesktopLyricsLocked;
        if (_locked)
        {
            UnlockHandle.Visibility = Visibility.Visible;
            ResizeHandle.Visibility = Visibility.Collapsed;
            SetWindowExTransparent(true);
        }
        else
        {
            UnlockHandle.Visibility = Visibility.Collapsed;
            ResizeHandle.Visibility = Visibility.Visible;
            SetWindowExTransparent(false);
        }
    }

    private void SetWindowExTransparent(bool transparent)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var style = GetWindowLong(hwnd, GwlExStyle);
        // B2：只切换 WS_EX_TRANSPARENT。WS_EX_LAYERED 是 WPF AllowsTransparency 的基石，
        // 清除它会让窗口失去逐像素透明（黑底实心块），绝不能动。
        if (transparent) style |= WsExTransparent;
        else style &= ~WsExTransparent;
        SetWindowLong(hwnd, GwlExStyle, style);
    }

    private void OnUnlockHandleClick(object sender, MouseButtonEventArgs e)
    {
        Player.Core.Infra.ConfigService.Current.Ui.DesktopLyricsLocked = false;
        Player.Core.Infra.ConfigService.Save();
        ApplyLockedStyle();
    }

    private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_locked) return;
        if (e.ChangedButton != MouseButton.Left) return;
        _dragging = true;
        _dragStart = e.GetPosition(this);
        CaptureMouse();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging) return;
        var pos = e.GetPosition(this);
        Left += pos.X - _dragStart.X;
        Top += pos.Y - _dragStart.Y;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragging)
        {
            _dragging = false;
            ReleaseMouseCapture();
        }
    }

    private void OnResizeHandleDown(object sender, MouseButtonEventArgs e)
    {
        if (_locked) return;
        var start = e.GetPosition(this);
        var startWidth = Width;
        void OnMove(object? s, MouseEventArgs args)
        {
            var delta = args.GetPosition(this).X - start.X;
            Width = Math.Clamp(startWidth + delta, 320, 1600);
        }
        void OnUp(object? s, MouseButtonEventArgs args)
        {
            Mouse.RemoveMouseMoveHandler(this, OnMove);
            Mouse.RemoveMouseUpHandler(this, OnUp);
            Player.Core.Infra.ConfigService.Current.Ui.DesktopLyricsWidth = Width;
            Player.Core.Infra.ConfigService.Save();
        }
        Mouse.AddMouseMoveHandler(this, OnMove);
        Mouse.AddMouseUpHandler(this, OnUp);
        e.Handled = true;
    }

    /// <summary>刷新当前句（原文+翻译；无时间轴/无歌词时显示曲名）。</summary>
    public void UpdateLyrics(string primary, string secondary, bool hasTimeline)
    {
        PrimaryText.Text = primary;
        SecondaryText.Text = hasTimeline ? secondary : string.Empty;
        SecondaryText.Visibility = Player.Core.Infra.ConfigService.Current.Ui.DesktopLyricsTwoLines && !string.IsNullOrEmpty(secondary)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
}