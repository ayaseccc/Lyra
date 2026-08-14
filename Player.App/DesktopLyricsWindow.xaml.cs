using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Player.App.Controls;
using Player.App.ViewModels;
using Player.Core.Infra;

namespace Player.App;

/// <summary>
/// 桌面歌词（L1 第三步 + L1.1-③ 个性化）：置顶无边框透明条，屏幕下方。
/// 当前句 原文+翻译 双行；锁定=鼠标穿透（缩成小柄可解锁）；解锁=拖动/调宽；
/// 颜色走主题刷子（DynamicResource，随取色主题联动）或自定义纯色；
/// 背景卡片可隐藏/调透明（纯文字模式靠描边阴影保可读）；右键菜单 + 悬停迷你工具条；
/// 无时间轴歌词显示曲名。
/// </summary>
public partial class DesktopLyricsWindow : Window
{
    // B5（目验发现）：锁定=鼠标穿透改走 WM_NCHITTEST 逐点判定——
    // 只有小柄区域返回 HTCLIENT（可点），其余返回 HTTRANSPARENT（穿透）。
    // 不再使用 WS_EX_TRANSPARENT（它让整窗包括小柄都收不到鼠标，锁定后永远无法解锁）。
    private const int WmNcHitTest = 0x0084;
    private const int HtTransparent = -1;
    private const int HtClient = 1;
    private const double HandleHitSize = 26;

    private bool _locked = true;
    private bool _dragging;
    private Point _dragStart;

    /// <summary>右键菜单"字体设置…" → 主窗打开设置页歌词组。</summary>
    public event Action? OpenFontSettingsRequested;

    public DesktopLyricsWindow()
    {
        InitializeComponent();
        ApplySettings();
        ApplyTextColor();
    }

    /// <summary>设置页「歌词」组 / 右键菜单改动后即时应用（字体/字号/单双行/宽度/背景/文字颜色）。</summary>
    public void ApplySettings()
    {
        var ui = ConfigService.Current.Ui;

        // 单双行
        SecondaryText.Visibility = ui.DesktopLyricsTwoLines ? Visibility.Visible : Visibility.Collapsed;
        Height = ui.DesktopLyricsTwoLines ? 120 : 70;

        // 宽度 / 字号（桌面歌词字号独立于右栏）
        if (ui.DesktopLyricsWidth > 200) Width = ui.DesktopLyricsWidth;
        PrimaryText.FontSize = ui.DesktopLyricsFontSize;
        SecondaryText.FontSize = ui.DesktopLyricsFontSize * 0.7;

        // L1.1-② 字体/字重（与右栏/大歌词页共用）
        PrimaryText.FontFamily = LyricUiOptions.ResolveFontFamily(ui.LyricFontFamily);
        SecondaryText.FontFamily = PrimaryText.FontFamily;
        var weight = LyricUiOptions.ParseWeight(ui.LyricFontWeight);
        PrimaryText.FontWeight = weight;
        SecondaryText.FontWeight = weight;

        // L1.1-③ 背景卡片：隐藏或调透明；隐藏时描边/阴影加强保可读（锁定态恒为纯文字，见 ApplyBackdropVisibility）
        ApplyBackdropVisibility();
    }

    /// <summary>文字颜色：跟随主题（清本地值回退 DynamicResource）/ 自定义纯色（副文本 75% 透明）。</summary>
    private void ApplyTextColor()
    {
        var ui = ConfigService.Current.Ui;
        if (ui.DesktopLyricsTextColorMode != "Custom" || !TryParseHex(ui.DesktopLyricsTextColor, out var color))
        {
            PrimaryText.ClearValue(TextBlock.ForegroundProperty);
            SecondaryText.ClearValue(TextBlock.ForegroundProperty);
            return;
        }

        var primary = new SolidColorBrush(color);
        primary.Freeze();
        var secondary = new SolidColorBrush(Color.FromArgb((byte)(color.A * 0.75), color.R, color.G, color.B));
        secondary.Freeze();
        PrimaryText.Foreground = primary;
        SecondaryText.Foreground = secondary;
    }

    private static bool TryParseHex(string? text, out Color color)
    {
        color = Colors.White;
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            color = (Color)ColorConverter.ConvertFromString(text);
            return true;
        }
        catch
        {
            return false;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (PresentationSource.FromVisual(this) is HwndSource source)
            source.AddHook(WndProc);
    }

    /// <summary>
    /// B5：锁定态逐点鼠标穿透。WM_NCHITTEST 里把除小柄外的区域返回 HTTRANSPARENT，
    /// 鼠标事件直接落到下层窗口；小柄区域返回 HTCLIENT 保持可点（解锁入口）。
    /// 解锁态不拦截（返回默认）。
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmNcHitTest || !_locked) return IntPtr.Zero;

        var x = (short)((long)lParam & 0xFFFF);
        var y = (short)(((long)lParam >> 16) & 0xFFFF);
        var inHandle = x >= Left + ActualWidth - HandleHitSize
                       && x < Left + ActualWidth
                       && y >= Top
                       && y < Top + HandleHitSize;
        handled = true;
        return new IntPtr(inHandle ? HtClient : HtTransparent);
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => ApplyLockedStyle();

    private void ApplyLockedStyle()
    {
        // B1：锁定语义 = 配置值本体（此前取反导致锁定/解锁颠倒）
        _locked = ConfigService.Current.Ui.DesktopLyricsLocked;
        if (_locked)
        {
            UnlockHandle.Visibility = Visibility.Visible;
            ResizeHandle.Visibility = Visibility.Collapsed;
            HoverToolbar.Visibility = Visibility.Collapsed;
        }
        else
        {
            UnlockHandle.Visibility = Visibility.Collapsed;
            ResizeHandle.Visibility = Visibility.Visible;
        }
        // 目验修复②：锁定（鼠标穿透）= 纯文字模式（背景透明，靠描边阴影保可读）；
        // 解锁态才按设置显示背景卡片
        ApplyBackdropVisibility();
    }

    /// <summary>背景卡片可见性：锁定=隐藏（纯文字）；解锁=按设置。</summary>
    private void ApplyBackdropVisibility()
    {
        var show = !_locked && ConfigService.Current.Ui.DesktopLyricsShowBackground;
        Backdrop.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        Backdrop.Opacity = show ? ConfigService.Current.Ui.DesktopLyricsBgOpacity : 1.0;
        var shadowStrength = show ? 0.9 : 1.0;
        var shadowBlur = show ? 6.0 : 12.0;
        if (PrimaryText.Effect is DropShadowEffect ps) { ps.Opacity = shadowStrength; ps.BlurRadius = shadowBlur; }
        if (SecondaryText.Effect is DropShadowEffect ss) { ss.Opacity = shadowStrength; ss.BlurRadius = shadowBlur; }
    }

    private void SetLocked(bool locked)
    {
        ConfigService.Current.Ui.DesktopLyricsLocked = locked;
        ConfigService.Save();
        ApplyLockedStyle();
    }

    // ================= 小柄 / 拖动 / 调宽 =================

    private void OnUnlockHandleClick(object sender, MouseButtonEventArgs e) => SetLocked(false);

    private void OnWindowMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_locked) return;
        if (e.ChangedButton != MouseButton.Left) return;

        // 悬停工具条按钮不触发拖动
        var source = e.OriginalSource as DependencyObject;
        while (source is not null)
        {
            if (source is Button) return;
            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }

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
            ConfigService.Current.Ui.DesktopLyricsWidth = Width;
            ConfigService.Save();
        }
        Mouse.AddMouseMoveHandler(this, OnMove);
        Mouse.AddMouseUpHandler(this, OnUp);
        e.Handled = true;
    }

    // ================= 悬停迷你工具条（解锁态） =================

    private void OnRootMouseEnter(object sender, MouseEventArgs e)
    {
        if (_locked) return;
        HoverToolbar.Visibility = Visibility.Visible;
    }

    private void OnRootMouseLeave(object sender, MouseEventArgs e)
    {
        HoverToolbar.Visibility = Visibility.Collapsed;
    }

    private void OnToolbarLockClick(object sender, RoutedEventArgs e) => SetLocked(true);

    private void OnToolbarCloseClick(object sender, RoutedEventArgs e) => CloseDesktopLyrics();

    // ================= 右键菜单 =================

    private void OnMenuOpened(object sender, RoutedEventArgs e)
    {
        var ui = ConfigService.Current.Ui;
        LockMenuItem.Header = _locked ? "解锁（可拖动 / 调宽）" : "锁定（鼠标穿透）";
        TwoLinesMenuItem.IsChecked = ui.DesktopLyricsTwoLines;
        BgShowMenuItem.IsChecked = ui.DesktopLyricsShowBackground;

        // 字体子菜单：置顶中日文友好项 + 当前字体，末尾给设置页入口
        FontFamilyMenu.Items.Clear();
        var installed = LyricUiOptions.FontFamilies;
        var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in installed.Take(18))
        {
            shown.Add(name);
            var familyItem = new MenuItem { Header = name };
            familyItem.Click += OnMenuFontFamily;
            FontFamilyMenu.Items.Add(familyItem);
        }
        if (!shown.Contains(ui.LyricFontFamily) && !string.IsNullOrEmpty(ui.LyricFontFamily))
        {
            var currentItem = new MenuItem { Header = ui.LyricFontFamily };
            currentItem.Click += OnMenuFontFamily;
            FontFamilyMenu.Items.Add(currentItem);
        }
        FontFamilyMenu.Items.Add(new Separator());
        var fontSettings = new MenuItem { Header = "打开字体设置…" };
        fontSettings.Click += (_, _) => OpenFontSettingsRequested?.Invoke();
        FontFamilyMenu.Items.Add(fontSettings);

        // 文字颜色子菜单：跟随主题 + 预设纯色，勾选当前
        TextColorMenu.Items.Clear();
        var currentKey = ui.DesktopLyricsTextColorMode == "Custom" ? ui.DesktopLyricsTextColor : "Theme";
        foreach (var option in LyricUiOptions.TextColors)
        {
            var item = new MenuItem
            {
                Header = option.Name,
                IsCheckable = true,
                IsChecked = string.Equals(option.Key, currentKey, StringComparison.OrdinalIgnoreCase),
                Tag = option
            };
            item.Click += OnMenuTextColor;
            TextColorMenu.Items.Add(item);
        }
    }

    private void OnMenuToggleLock(object sender, RoutedEventArgs e) => SetLocked(!_locked);

    private void OnMenuToggleTwoLines(object sender, RoutedEventArgs e)
    {
        var ui = ConfigService.Current.Ui;
        ui.DesktopLyricsTwoLines = !ui.DesktopLyricsTwoLines;
        ConfigService.Save();
        ApplySettings();
        UpdateLyrics(PrimaryText.Text, SecondaryText.Text, hasTimeline: ui.DesktopLyricsTwoLines);
    }

    private void OnMenuToggleBackground(object sender, RoutedEventArgs e)
    {
        var ui = ConfigService.Current.Ui;
        ui.DesktopLyricsShowBackground = !ui.DesktopLyricsShowBackground;
        ConfigService.Save();
        ApplySettings();
    }

    private void OnMenuBgOpacity(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string tag } || !double.TryParse(tag, out var opacity)) return;
        var ui = ConfigService.Current.Ui;
        ui.DesktopLyricsBgOpacity = opacity;
        ConfigService.Save();
        ApplySettings();
    }

    private void OnMenuFontSizePlus(object sender, RoutedEventArgs e) => AdjustFontSize(+2);

    private void OnMenuFontSizeMinus(object sender, RoutedEventArgs e) => AdjustFontSize(-2);

    private void AdjustFontSize(double delta)
    {
        var ui = ConfigService.Current.Ui;
        ui.DesktopLyricsFontSize = Math.Clamp(ui.DesktopLyricsFontSize + delta, 12, 40);
        ConfigService.Save();
        ApplySettings();
    }

    private void OnMenuFontFamily(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Header: string family }) return;
        var ui = ConfigService.Current.Ui;
        ui.LyricFontFamily = family;
        ConfigService.Save();
        ApplySettings();
    }

    private void OnMenuTextColor(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: DesktopLyricsColorOption option }) return;
        var ui = ConfigService.Current.Ui;
        if (option.Key == "Theme")
        {
            ui.DesktopLyricsTextColorMode = "Theme";
        }
        else
        {
            ui.DesktopLyricsTextColorMode = "Custom";
            ui.DesktopLyricsTextColor = option.Key;
        }
        ConfigService.Save();
        ApplyTextColor();
    }

    private void OnMenuClose(object sender, RoutedEventArgs e) => CloseDesktopLyrics();

    private void CloseDesktopLyrics()
    {
        Hide();
        ConfigService.Current.Ui.DesktopLyricsEnabled = false;
        ConfigService.Save();
    }

    // ================= 内容刷新 =================

    /// <summary>刷新当前句（原文+翻译；无时间轴/无歌词时显示曲名）。</summary>
    public void UpdateLyrics(string primary, string secondary, bool hasTimeline)
    {
        PrimaryText.Text = primary;
        SecondaryText.Text = hasTimeline ? secondary : string.Empty;
        SecondaryText.Visibility = ConfigService.Current.Ui.DesktopLyricsTwoLines && !string.IsNullOrEmpty(secondary)
            ? Visibility.Visible
            : Visibility.Collapsed;
    }
}
