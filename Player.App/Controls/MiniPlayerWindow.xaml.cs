using System;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Player.App.ViewModels;
using Player.Core.Infra;

namespace Player.App.Controls;

/// <summary>
/// L3.2 mini surface. Spectrum data only comes from the mixer's DSP observer through
/// PlayerViewModel; this window never reads a BASS channel or owns an audio handle.
/// </summary>
public partial class MiniPlayerWindow : Window
{
    private const int SpectrumBarCount = 16;
    private const double DefaultWidthDip = 340;
    private const double DefaultHeightDip = 104;
    private const double MinimumScale = 0.8;
    private const double MaximumScale = 2.0;
    private const double MinimumWidthDip = DefaultWidthDip * MinimumScale;
    private const double MaximumWidthDip = DefaultWidthDip * MaximumScale;
    private const double MinimumHeightDip = DefaultHeightDip * MinimumScale;
    private const double MaximumHeightDip = DefaultHeightDip * MaximumScale;
    private const double ResizeBorderDip = 5;
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const int WmNcHitTest = 0x0084;
    private const int WmNcLeftButtonDown = 0x00A1;
    private const int WmSizing = 0x0214;
    private const int WmEnterSizeMove = 0x0231;
    private const int WmExitSizeMove = 0x0232;
    private const int WmDpiChanged = 0x02E0;
    private const int WmszLeft = 1;
    private const int WmszRight = 2;
    private const int WmszTop = 3;
    private const int WmszTopLeft = 4;
    private const int WmszTopRight = 5;
    private const int WmszBottom = 6;
    private const int WmszBottomLeft = 7;
    private const int WmszBottomRight = 8;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;
    private const int HtCaption = 2;
    private const double DefaultWorkAreaInsetDip = 24;
    private const string MonitorPositionPrefix = "v2|";

    private readonly PlayerViewModel _player;
    private readonly DispatcherTimer _marqueeTimer;
    private readonly DispatcherTimer _spectrumTimer;
    private readonly float[] _spectrumLevels = new float[SpectrumBarCount];

    private IDisposable? _spectrumLease;
    private DateTime _marqueePauseUntil;
    private double _marqueeOffset;
    private bool _marqueeAtEnd;
    private bool _marqueeStarted;
    private bool _surfaceActive;
    private bool _positionRestored;
    private bool _restorePending;
    private bool _closed;
    private HwndSource? _hwndSource;
    private bool _dragActive;
    private bool _sizeMoveActive;
    private bool _hitTestMetricsValid;
    private IntPtr _hitTestMonitor;
    private NativeRect _hitTestMonitorRect;
    private int _hitTestBorderPixels;
    private NativeRect _sizeMoveStartRect;
    private bool _hasSizeMoveStart;

    /// <summary>Raised for Esc, Alt+F4 and the restore button.</summary>
    public event Action? RestoreRequested;

    /// <summary>Compatibility switch for the previous MainWindow shutdown path.</summary>
    public bool AllowRealClose { get; set; }

    public MiniPlayerWindow(PlayerViewModel player)
    {
        InitializeComponent();
        RestoreSizeFromConfig();
        _player = player;
        DataContext = player;

        _marqueeTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(50)
        };
        _marqueeTimer.Tick += OnMarqueeTick;

        _spectrumTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _spectrumTimer.Tick += OnSpectrumTick;

        BorderRoot.MouseEnter += OnSurfaceMouseEnter;
        BorderRoot.MouseLeave += OnSurfaceMouseLeave;
        IsVisibleChanged += OnWindowVisibilityChanged;
        SourceInitialized += OnSourceInitialized;
        Closed += OnWindowClosed;
        _player.PropertyChanged += OnPlayerPropertyChanged;

        ResetMarquee();
        UpdateSpectrumVisibility();
    }

    /// <summary>Called by the surface coordinator immediately before showing this window.</summary>
    public void ActivateSurface()
    {
        if (_closed || _surfaceActive) return;

        _surfaceActive = true;
        _restorePending = false;
        RestorePositionOnce();
        ClampCurrentPositionToWorkingArea();
        ResetMarquee();
        _marqueeTimer.Start();

        if (ConfigService.Current.Ui.MiniSpectrum)
        {
            try
            {
                _spectrumLease = _player.AcquireSpectrum();
                _spectrumTimer.Start();
            }
            catch (Exception ex)
            {
                _spectrumLease = null;
                Serilog.Log.Warning(ex, "迷你窗频谱启动失败");
            }
        }

        UpdateSpectrumVisibility();
    }

    /// <summary>Called before hiding this window. It is intentionally idempotent.</summary>
    public void DeactivateSurface()
    {
        if (!_surfaceActive && _spectrumLease is null) return;

        _surfaceActive = false;
        _marqueeTimer.Stop();
        _spectrumTimer.Stop();

        _spectrumLease?.Dispose();
        _spectrumLease = null;
        Array.Clear(_spectrumLevels);
        SpectrumView.Clear();
        ResetMarquee();
        SetHoverVisible(false, animate: false);
    }

    /// <summary>Allows and performs a real close during the application's unified exit path.</summary>
    public void CloseForExit()
    {
        if (_closed) return;
        AllowRealClose = true;
        DeactivateSurface();
        Close();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwndSource = PresentationSource.FromVisual(this) as HwndSource;
        _hwndSource?.AddHook(WindowProc);
        RestorePositionOnce();
    }

    private void OnWindowVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        // Compatibility fallback for callers not yet migrated to AppSurfaceCoordinator.
        if (IsVisible) ActivateSurface();
        else DeactivateSurface();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _closed = true;
        DeactivateSurface();
        _player.PropertyChanged -= OnPlayerPropertyChanged;
        IsVisibleChanged -= OnWindowVisibilityChanged;
        SourceInitialized -= OnSourceInitialized;
        _hwndSource?.RemoveHook(WindowProc);
        _hwndSource = null;
    }

    private void OnPlayerPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlayerViewModel.Title)) ResetMarquee();
        if (e.PropertyName is nameof(PlayerViewModel.Artist) or nameof(PlayerViewModel.Album))
            ArtistText.GetBindingExpression(System.Windows.Controls.TextBlock.TextProperty)?.UpdateTarget();
        if (e.PropertyName == nameof(PlayerViewModel.HasTrack)) UpdateSpectrumVisibility();
    }

    private void OnSurfaceMouseEnter(object sender, MouseEventArgs e)
    {
        if (!_sizeMoveActive) SetHoverVisible(true, animate: true);
    }

    private void OnSurfaceMouseLeave(object sender, MouseEventArgs e)
    {
        if (!_sizeMoveActive) SetHoverVisible(false, animate: true);
    }

    private void SetHoverVisible(bool visible, bool animate)
    {
        var currentControlsOpacity = HoverControls.Opacity;
        var currentControlsY = HoverControlsTranslate.Y;
        var currentSpectrumOpacity = SpectrumView.Opacity;
        var targetControlsOpacity = visible ? 1d : 0d;
        var targetControlsY = visible ? 0d : 3d;
        var targetMetaMargin = visible ? new Thickness(0, 2, 129, 0) : new Thickness(0, 2, 0, 0);
        var targetSpectrumOpacity = visible ? 0.86d : 1d;

        HoverControls.IsHitTestVisible = visible;
        HoverControls.BeginAnimation(OpacityProperty, null);
        HoverControlsTranslate.BeginAnimation(TranslateTransform.YProperty, null);
        MetaPanel.BeginAnimation(MarginProperty, null);
        SpectrumView.BeginAnimation(OpacityProperty, null);

        HoverControls.Opacity = targetControlsOpacity;
        HoverControlsTranslate.Y = targetControlsY;
        MetaPanel.Margin = targetMetaMargin;
        SpectrumView.Opacity = targetSpectrumOpacity;

        if (!animate)
            return;

        var easing = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        HoverControls.BeginAnimation(OpacityProperty, new DoubleAnimation(
            currentControlsOpacity,
            targetControlsOpacity,
            TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop
            });
        HoverControlsTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(
            currentControlsY,
            targetControlsY,
            TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop
            });
        SpectrumView.BeginAnimation(OpacityProperty, new DoubleAnimation(
            currentSpectrumOpacity,
            targetSpectrumOpacity,
            TimeSpan.FromMilliseconds(150))
            {
                EasingFunction = easing,
                FillBehavior = FillBehavior.Stop
            });
    }

    private void ResetMarquee()
    {
        _marqueeOffset = 0;
        _marqueeAtEnd = false;
        _marqueeStarted = false;
        _marqueePauseUntil = DateTime.UtcNow.AddMilliseconds(1200);
        TitleTranslate.X = 0;
        TitleEllipsisText.Opacity = 1;
        TitleMarqueeText.Opacity = 0;
    }

    private void OnMarqueeTick(object? sender, EventArgs e)
    {
        var viewportWidth = TitleViewport.ActualWidth;
        var titleWidth = TitleMarqueeText.ActualWidth;
        if (viewportWidth <= 0 || titleWidth <= viewportWidth + 1)
        {
            if (_marqueeStarted) ResetMarquee();
            return;
        }

        var now = DateTime.UtcNow;
        if (now < _marqueePauseUntil) return;

        if (!_marqueeStarted)
        {
            _marqueeStarted = true;
            TitleEllipsisText.Opacity = 0;
            TitleMarqueeText.Opacity = 1;
        }

        if (_marqueeAtEnd)
        {
            _marqueeAtEnd = false;
            _marqueeOffset = 0;
            TitleTranslate.X = 0;
            _marqueePauseUntil = now.AddSeconds(1);
            return;
        }

        _marqueeOffset -= 1;
        var minimumOffset = viewportWidth - titleWidth;
        if (_marqueeOffset <= minimumOffset)
        {
            _marqueeOffset = minimumOffset;
            _marqueeAtEnd = true;
            _marqueePauseUntil = now.AddSeconds(1);
        }

        TitleTranslate.X = _marqueeOffset;
    }

    private void OnSpectrumTick(object? sender, EventArgs e)
    {
        if (_spectrumLease is null || !_player.TryCopySpectrum(_spectrumLevels))
            Array.Clear(_spectrumLevels);

        SpectrumView.SetLevels(_spectrumLevels);
    }

    private void UpdateSpectrumVisibility()
    {
        var visible = ConfigService.Current.Ui.MiniSpectrum && _player.HasTrack && _spectrumLease is not null;
        SpectrumView.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        MetaPanel.VerticalAlignment = visible ? VerticalAlignment.Top : VerticalAlignment.Center;
    }

    private void RestoreSizeFromConfig()
    {
        var ui = ConfigService.Current.Ui;
        var scale = double.IsFinite(ui.MiniWidth) && ui.MiniWidth > 0
            ? ui.MiniWidth / DefaultWidthDip
            : double.IsFinite(ui.MiniHeight) && ui.MiniHeight > 0
                ? ui.MiniHeight / DefaultHeightDip
                : 1.0;
        ApplyWindowScale(scale);
    }

    private void ApplyWindowScale(double scale)
    {
        scale = double.IsFinite(scale) ? Math.Clamp(scale, MinimumScale, MaximumScale) : 1.0;
        Width = DefaultWidthDip * scale;
        Height = DefaultHeightDip * scale;
    }

    private IntPtr WindowProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmNcHitTest && TryGetResizeHit(hwnd, out var hit))
        {
            handled = true;
            return new IntPtr(hit);
        }

        if (message == WmDpiChanged)
            _hitTestMetricsValid = false;

        if (message == WmEnterSizeMove)
        {
            _sizeMoveActive = true;
            _hasSizeMoveStart = GetWindowRect(hwnd, out _sizeMoveStartRect);
            _hitTestMetricsValid = false;
            _marqueeTimer.Stop();
            _spectrumTimer.Stop();
            SetHoverVisible(false, animate: false);
        }

        if (message == WmSizing && lParam != IntPtr.Zero)
        {
            ConstrainSizingToAspectRatio(wParam.ToInt32(), lParam);
            handled = true;
            return new IntPtr(1);
        }

        if (message == WmExitSizeMove)
        {
            _sizeMoveActive = false;
            _hasSizeMoveStart = false;
            _hitTestMetricsValid = false;
            if (_surfaceActive)
            {
                _marqueeTimer.Start();
                if (_spectrumLease is not null)
                    _spectrumTimer.Start();
            }
            SetHoverVisible(BorderRoot.IsMouseOver, animate: false);
            if (!_dragActive)
                Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(SavePlacement));
        }

        return IntPtr.Zero;
    }

    private void ConstrainSizingToAspectRatio(int edge, IntPtr rectPointer)
    {
        var rect = Marshal.PtrToStructure<NativeRect>(rectPointer);
        var width = Math.Max(1, rect.Right - rect.Left);
        var height = Math.Max(1, rect.Bottom - rect.Top);
        var monitor = GetCursorPos(out var cursor)
            ? MonitorFromPoint(cursor, MonitorDefaultToNearest)
            : MonitorFromPoint(new NativePoint(rect.Left, rect.Top), MonitorDefaultToNearest);
        var dpi = GetEffectiveMonitorDpi(monitor);
        var baseWidth = DefaultWidthDip * dpi / 96d;
        var baseHeight = DefaultHeightDip * dpi / 96d;

        double scale;
        if (edge is WmszLeft or WmszRight)
        {
            scale = width / baseWidth;
        }
        else if (edge is WmszTop or WmszBottom)
        {
            scale = height / baseHeight;
        }
        else
        {
            var startWidth = _hasSizeMoveStart
                ? Math.Max(1, _sizeMoveStartRect.Right - _sizeMoveStartRect.Left)
                : width;
            var startHeight = _hasSizeMoveStart
                ? Math.Max(1, _sizeMoveStartRect.Bottom - _sizeMoveStartRect.Top)
                : height;
            var horizontalChange = Math.Abs(width - startWidth) / baseWidth;
            var verticalChange = Math.Abs(height - startHeight) / baseHeight;
            scale = horizontalChange >= verticalChange ? width / baseWidth : height / baseHeight;
        }

        scale = Math.Clamp(scale, MinimumScale, MaximumScale);
        var targetWidth = Math.Max(1, (int)Math.Round(baseWidth * scale));
        var targetHeight = Math.Max(1, (int)Math.Round(baseHeight * scale));

        switch (edge)
        {
            case WmszLeft:
                rect.Left = rect.Right - targetWidth;
                CenterVertically(ref rect, targetHeight);
                break;
            case WmszRight:
                rect.Right = rect.Left + targetWidth;
                CenterVertically(ref rect, targetHeight);
                break;
            case WmszTop:
                rect.Top = rect.Bottom - targetHeight;
                CenterHorizontally(ref rect, targetWidth);
                break;
            case WmszBottom:
                rect.Bottom = rect.Top + targetHeight;
                CenterHorizontally(ref rect, targetWidth);
                break;
            case WmszTopLeft:
                rect.Left = rect.Right - targetWidth;
                rect.Top = rect.Bottom - targetHeight;
                break;
            case WmszTopRight:
                rect.Right = rect.Left + targetWidth;
                rect.Top = rect.Bottom - targetHeight;
                break;
            case WmszBottomLeft:
                rect.Left = rect.Right - targetWidth;
                rect.Bottom = rect.Top + targetHeight;
                break;
            case WmszBottomRight:
                rect.Right = rect.Left + targetWidth;
                rect.Bottom = rect.Top + targetHeight;
                break;
        }

        Marshal.StructureToPtr(rect, rectPointer, false);
    }

    private static void CenterVertically(ref NativeRect rect, int targetHeight)
    {
        var center = ((long)rect.Top + rect.Bottom) / 2;
        rect.Top = (int)(center - targetHeight / 2d);
        rect.Bottom = rect.Top + targetHeight;
    }

    private static void CenterHorizontally(ref NativeRect rect, int targetWidth)
    {
        var center = ((long)rect.Left + rect.Right) / 2;
        rect.Left = (int)(center - targetWidth / 2d);
        rect.Right = rect.Left + targetWidth;
    }

    private bool TryGetResizeHit(IntPtr hwnd, out int hit)
    {
        hit = 0;
        if (_sizeMoveActive || !GetCursorPos(out var cursor)) return false;

        if (!_hitTestMetricsValid || !Contains(_hitTestMonitorRect, cursor))
            RefreshHitTestMetrics(cursor);

        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect)) return false;

        var border = _hitTestBorderPixels;
        var onLeft = cursor.X >= rect.Left && cursor.X < rect.Left + border;
        var onRight = cursor.X < rect.Right && cursor.X >= rect.Right - border;
        var onTop = cursor.Y >= rect.Top && cursor.Y < rect.Top + border;
        var onBottom = cursor.Y < rect.Bottom && cursor.Y >= rect.Bottom - border;

        hit = (onLeft, onRight, onTop, onBottom) switch
        {
            (true, _, true, _) => HtTopLeft,
            (_, true, true, _) => HtTopRight,
            (true, _, _, true) => HtBottomLeft,
            (_, true, _, true) => HtBottomRight,
            (true, _, _, _) => HtLeft,
            (_, true, _, _) => HtRight,
            (_, _, true, _) => HtTop,
            (_, _, _, true) => HtBottom,
            _ => 0
        };
        return hit != 0;
    }

    private void RefreshHitTestMetrics(NativePoint cursor)
    {
        _hitTestMonitor = MonitorFromPoint(cursor, MonitorDefaultToNearest);
        var dpi = GetEffectiveMonitorDpi(_hitTestMonitor);
        _hitTestBorderPixels = Math.Max(4, (int)Math.Round(ResizeBorderDip * dpi / 96d));

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        _hitTestMonitorRect = GetMonitorInfo(_hitTestMonitor, ref info)
            ? info.Monitor
            : new NativeRect
            {
                Left = int.MinValue / 2,
                Top = int.MinValue / 2,
                Right = int.MaxValue / 2,
                Bottom = int.MaxValue / 2
            };
        _hitTestMetricsValid = true;
    }

    private static bool Contains(NativeRect rect, NativePoint point) =>
        point.X >= rect.Left && point.X < rect.Right &&
        point.Y >= rect.Top && point.Y < rect.Bottom;

    private void RestorePositionOnce()
    {
        if (_positionRestored) return;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;

        var pos = ConfigService.Current.Ui.MiniPos;
        if (string.IsNullOrWhiteSpace(pos))
        {
            MoveToDefaultPosition(hwnd);
            _positionRestored = true;
            return;
        }

        if (TryRestoreMonitorRelativePosition(hwnd, pos))
        {
            _positionRestored = true;
            QueueFinalPositionClamp(hwnd);
            return;
        }
        if (pos.StartsWith(MonitorPositionPrefix, StringComparison.Ordinal))
        {
            MoveToDefaultPosition(hwnd);
            _positionRestored = true;
            return;
        }

        var isPhysical = pos.StartsWith("px:", StringComparison.Ordinal);
        var coordinates = isPhysical ? pos[3..] : pos;
        var parts = coordinates.Split(',');
        if (parts.Length != 2
            || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var x)
            || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var y))
        {
            MoveToDefaultPosition(hwnd);
            _positionRestored = true;
            return;
        }

        if (!isPhysical)
        {
            var toDevice = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice
                ?? Matrix.Identity;
            var devicePoint = toDevice.Transform(new Point(x, y));
            x = devicePoint.X;
            y = devicePoint.Y;
        }

        MoveIntoNearestWorkingArea(hwnd, (int)Math.Round(x), (int)Math.Round(y), useTargetDpiSize: true);
        _positionRestored = true;
        QueueFinalPositionClamp(hwnd);
    }

    private void SavePlacement()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var rect))
        {
            var screen = System.Windows.Forms.Screen.FromHandle(hwnd);
            var monitor = MonitorFromPoint(
                new NativePoint(screen.Bounds.Left + screen.Bounds.Width / 2, screen.Bounds.Top + screen.Bounds.Height / 2),
                MonitorDefaultToNearest);
            var dpi = GetEffectiveMonitorDpi(monitor);
            var offsetX = (rect.Left - screen.WorkingArea.Left) * 96d / dpi;
            var offsetY = (rect.Top - screen.WorkingArea.Top) * 96d / dpi;
            var device = Convert.ToBase64String(Encoding.UTF8.GetBytes(screen.DeviceName));
            var ui = ConfigService.Current.Ui;
            ui.MiniPos = FormattableString.Invariant(
                $"{MonitorPositionPrefix}{device}|{offsetX:0.###}|{offsetY:0.###}");
            var scale = Math.Clamp(
                (rect.Right - rect.Left) * 96d / dpi / DefaultWidthDip,
                MinimumScale,
                MaximumScale);
            ui.MiniWidth = DefaultWidthDip * scale;
            ui.MiniHeight = DefaultHeightDip * scale;
            ConfigService.Save();
            return;
        }

        if (double.IsNaN(Left) || double.IsNaN(Top) || double.IsInfinity(Left) || double.IsInfinity(Top))
            return;

        var fallbackUi = ConfigService.Current.Ui;
        fallbackUi.MiniPos = FormattableString.Invariant($"{Left:0},{Top:0}");
        var fallbackScale = double.IsFinite(ActualWidth) && ActualWidth > 0
            ? Math.Clamp(ActualWidth / DefaultWidthDip, MinimumScale, MaximumScale)
            : 1.0;
        fallbackUi.MiniWidth = DefaultWidthDip * fallbackScale;
        fallbackUi.MiniHeight = DefaultHeightDip * fallbackScale;
        ConfigService.Save();
    }

    private bool TryRestoreMonitorRelativePosition(IntPtr hwnd, string value)
    {
        if (!value.StartsWith(MonitorPositionPrefix, StringComparison.Ordinal)) return false;

        var parts = value.Split('|');
        if (parts.Length != 4
            || !double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var offsetX)
            || !double.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var offsetY))
            return false;

        string deviceName;
        try
        {
            deviceName = Encoding.UTF8.GetString(Convert.FromBase64String(parts[1]));
        }
        catch (FormatException)
        {
            return false;
        }

        var screen = System.Windows.Forms.Screen.AllScreens.FirstOrDefault(candidate =>
                         string.Equals(candidate.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                     ?? System.Windows.Forms.Screen.PrimaryScreen
                     ?? System.Windows.Forms.Screen.AllScreens.FirstOrDefault();
        if (screen is null) return false;

        var center = new NativePoint(
            screen.Bounds.Left + screen.Bounds.Width / 2,
            screen.Bounds.Top + screen.Bounds.Height / 2);
        var monitor = MonitorFromPoint(center, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return false;

        var dpi = GetEffectiveMonitorDpi(monitor);
        var proposedX = screen.WorkingArea.Left + (int)Math.Round(offsetX * dpi / 96d);
        var proposedY = screen.WorkingArea.Top + (int)Math.Round(offsetY * dpi / 96d);
        MoveIntoWorkingArea(hwnd, monitor, proposedX, proposedY, useTargetDpiSize: true);
        return true;
    }

    private void MoveToDefaultPosition(IntPtr hwnd)
    {
        var screen = System.Windows.Forms.Screen.PrimaryScreen
                     ?? System.Windows.Forms.Screen.AllScreens.FirstOrDefault();
        if (screen is null) return;

        var center = new NativePoint(
            screen.Bounds.Left + screen.Bounds.Width / 2,
            screen.Bounds.Top + screen.Bounds.Height / 2);
        var monitor = MonitorFromPoint(center, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return;

        var dpi = GetEffectiveMonitorDpi(monitor);
        var width = (int)Math.Round(Width * dpi / 96d);
        var height = (int)Math.Round(Height * dpi / 96d);
        var inset = (int)Math.Round(DefaultWorkAreaInsetDip * dpi / 96d);
        var proposedX = screen.WorkingArea.Right - width - inset;
        var proposedY = screen.WorkingArea.Bottom - height - inset;
        MoveIntoWorkingArea(hwnd, monitor, proposedX, proposedY, useTargetDpiSize: true);
        QueueFinalPositionClamp(hwnd);
    }

    private void QueueFinalPositionClamp(IntPtr hwnd)
    {
        // A per-monitor DPI transition may resize the HWND after the first move.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, () =>
        {
            if (_closed || !GetWindowRect(hwnd, out var rect)) return;
            MoveIntoNearestWorkingArea(hwnd, rect.Left, rect.Top, useTargetDpiSize: false);
        });
    }

    private void ClampCurrentPositionToWorkingArea()
    {
        if (!_positionRestored) return;

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var rect)) return;
        MoveIntoNearestWorkingArea(hwnd, rect.Left, rect.Top, useTargetDpiSize: false);
    }

    private void MoveIntoNearestWorkingArea(IntPtr hwnd, int proposedX, int proposedY, bool useTargetDpiSize)
    {
        var monitor = MonitorFromPoint(new NativePoint(proposedX, proposedY), MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return;

        MoveIntoWorkingArea(hwnd, monitor, proposedX, proposedY, useTargetDpiSize);
    }

    private void MoveIntoWorkingArea(
        IntPtr hwnd,
        IntPtr monitor,
        int proposedX,
        int proposedY,
        bool useTargetDpiSize)
    {

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return;

        int windowWidth;
        int windowHeight;
        if (useTargetDpiSize)
        {
            var dpi = GetEffectiveMonitorDpi(monitor);
            windowWidth = Math.Max(1, (int)Math.Round(Width * dpi / 96d));
            windowHeight = Math.Max(1, (int)Math.Round(Height * dpi / 96d));
        }
        else if (GetWindowRect(hwnd, out var currentRect))
        {
            windowWidth = currentRect.Right - currentRect.Left;
            windowHeight = currentRect.Bottom - currentRect.Top;
        }
        else
        {
            windowWidth = (int)Math.Ceiling(ActualWidth > 0 ? ActualWidth : Width);
            windowHeight = (int)Math.Ceiling(ActualHeight > 0 ? ActualHeight : Height);
        }

        var maxX = Math.Max(info.Work.Left, info.Work.Right - windowWidth);
        var maxY = Math.Max(info.Work.Top, info.Work.Bottom - windowHeight);
        var x = Math.Clamp(proposedX, info.Work.Left, maxX);
        var y = Math.Clamp(proposedY, info.Work.Top, maxY);
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);
    }

    private static uint GetEffectiveMonitorDpi(IntPtr monitor)
    {
        try
        {
            if (GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 && dpiX > 0)
                return dpiX;
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }

        return 96;
    }

    private void OnSurfacePointerDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left
            || IsInteractiveSource(e.OriginalSource as DependencyObject))
            return;

        if (e.ClickCount >= 2)
        {
            ResetDefaultSize();
            e.Handled = true;
            return;
        }

        _dragActive = true;
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd != IntPtr.Zero)
            {
                ReleaseCapture();
                SendMessage(hwnd, WmNcLeftButtonDown, new IntPtr(HtCaption), IntPtr.Zero);
            }
        }
        finally
        {
            _dragActive = false;
            SavePlacement();
        }

        e.Handled = true;
    }

    private void ResetDefaultSize()
    {
        ApplyWindowScale(1.0);
        Dispatcher.BeginInvoke(DispatcherPriority.Render, () =>
        {
            ClampCurrentPositionToWorkingArea();
            SavePlacement();
        });
    }

    private static bool IsInteractiveSource(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase or Slider) return true;
            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private void RequestRestore()
    {
        if (_restorePending || AllowRealClose) return;
        _restorePending = true;
        SavePlacement();
        RestoreRequested?.Invoke();
    }

    private void OnRestoreClick(object sender, RoutedEventArgs e) => RequestRestore();

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        RequestRestore();
        e.Handled = true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (AllowRealClose)
        {
            DeactivateSurface();
            base.OnClosing(e);
            return;
        }

        e.Cancel = true;
        RequestRestore();
        base.OnClosing(e);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public NativePoint(int x, int y)
        {
            X = x;
            Y = y;
        }

        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(
        IntPtr hwnd,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
}
