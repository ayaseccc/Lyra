using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Player.Core.Lyrics;

namespace Player.App.Controls;

/// <summary>歌词渲染的一行（主文本 + 可选的翻译/罗马音副文本 + 时间点）。</summary>
public sealed class LyricRenderLine
{
    public required TimeSpan Time { get; init; }

    public required string Primary { get; init; }

    public string Secondary { get; init; } = string.Empty;
}

/// <summary>
/// 自绘歌词控件（UI-R0）。基于 FrameworkElement + OnRender + FormattedText，
/// 自行管理滚动偏移与缓动动画；**禁止** ItemsControl/ListBox/DataTemplate/ScrollViewer。
///
/// 行为：
/// - 跟随模式：当前行居中为目标，指数缓动平滑滚向目标；当前行加粗+强调色，相邻行按距离淡出。
/// - 滚轮：临时自由浏览，静置 3 秒自动回到跟随。
/// - 点击行：触发 <see cref="LineClicked"/>（由宿主负责 seek）。
/// - 静态模式（无时间轴歌词）：不跟随不淡出，整篇正常显示，仍可滚动浏览。
/// </summary>
public sealed class LyricCanvas : FrameworkElement
{
    private static readonly Stopwatch FrameClock = Stopwatch.StartNew();
    private static double _lastFrameMs = FrameClock.Elapsed.TotalMilliseconds;

    private double _offset;
    private bool _userScrolling;
    private DateTime _lastUserScroll = DateTime.MinValue;
    private bool _animating;

    /// <summary>FrameworkElement 没有 FontFamily 属性，这里自持字体。</summary>
    private static readonly FontFamily UiFont = new("Microsoft YaHei UI, Segoe UI");

    /// <summary>点击某行（参数为行号）。</summary>
    public event Action<int>? LineClicked;

    public LyricCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
        Cursor = Cursors.Hand;
    }

    // ---------------- 依赖属性（供 XAML 绑定） ----------------

    public static readonly DependencyProperty LinesProperty = DependencyProperty.Register(
        nameof(Lines), typeof(IReadOnlyList<LyricRenderLine>), typeof(LyricCanvas),
        new PropertyMetadata(null, OnVisualPropertyChanged));

    public IReadOnlyList<LyricRenderLine>? Lines
    {
        get => (IReadOnlyList<LyricRenderLine>?)GetValue(LinesProperty);
        set => SetValue(LinesProperty, value);
    }

    public static readonly DependencyProperty CurrentIndexProperty = DependencyProperty.Register(
        nameof(CurrentIndex), typeof(int), typeof(LyricCanvas),
        new PropertyMetadata(-1, OnVisualPropertyChanged));

    public int CurrentIndex
    {
        get => (int)GetValue(CurrentIndexProperty);
        set => SetValue(CurrentIndexProperty, value);
    }

    public static readonly DependencyProperty IsStaticProperty = DependencyProperty.Register(
        nameof(IsStatic), typeof(bool), typeof(LyricCanvas),
        new PropertyMetadata(false, OnVisualPropertyChanged));

    /// <summary>无时间轴歌词：整篇静态显示，不跟随不淡出。</summary>
    public bool IsStatic
    {
        get => (bool)GetValue(IsStaticProperty);
        set => SetValue(IsStaticProperty, value);
    }

    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(
        nameof(AccentBrush), typeof(Brush), typeof(LyricCanvas),
        new PropertyMetadata(null, OnVisualPropertyChanged));

    public Brush? AccentBrush
    {
        get => (Brush?)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public static readonly DependencyProperty TextBrushProperty = DependencyProperty.Register(
        nameof(TextBrush), typeof(Brush), typeof(LyricCanvas),
        new PropertyMetadata(null, OnVisualPropertyChanged));

    public Brush? TextBrush
    {
        get => (Brush?)GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    public static readonly DependencyProperty SubTextBrushProperty = DependencyProperty.Register(
        nameof(SubTextBrush), typeof(Brush), typeof(LyricCanvas),
        new PropertyMetadata(null, OnVisualPropertyChanged));

    public Brush? SubTextBrush
    {
        get => (Brush?)GetValue(SubTextBrushProperty);
        set => SetValue(SubTextBrushProperty, value);
    }

    private static void OnVisualPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not LyricCanvas canvas) return;

        // 数据或目标变化：重绘并让滚动向目标缓动
        canvas.InvalidateVisual();
        canvas.StartAnimation();
    }

    // ---------------- 滚动与动画 ----------------

    private void StartAnimation()
    {
        if (_animating) return;
        _animating = true;
        _lastFrameMs = FrameClock.Elapsed.TotalMilliseconds;
        CompositionTarget.Rendering += OnRenderingFrame;
    }

    private void StopAnimation()
    {
        _animating = false;
        CompositionTarget.Rendering -= OnRenderingFrame;
    }

    private void OnRenderingFrame(object? sender, EventArgs e)
    {
        var nowMs = FrameClock.Elapsed.TotalMilliseconds;
        var dt = Math.Min(0.05, Math.Max(0.001, (nowMs - _lastFrameMs) / 1000.0));
        _lastFrameMs = nowMs;

        // 静置 3 秒回跟随（临时自由浏览结束）
        if (_userScrolling && (DateTime.UtcNow - _lastUserScroll).TotalSeconds >= 3)
            _userScrolling = false;

        var count = Lines?.Count ?? 0;
        if (count == 0)
        {
            StopAnimation();
            return;
        }

        double target;
        if (IsStatic || _userScrolling)
        {
            target = _offset;   // 静态/自由浏览：保持用户位置
        }
        else
        {
            target = LyricLayout.TargetOffsetFor(CurrentIndex, count, ActualHeight);
        }

        var (next, settled) = LyricLayout.EaseTowards(_offset, target, dt);
        _offset = next;
        InvalidateVisual();

        if (settled && !_userScrolling)
            StopAnimation();
    }

    // ---------------- 交互 ----------------

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);

        var count = Lines?.Count ?? 0;
        if (count == 0) return;

        _userScrolling = true;
        _lastUserScroll = DateTime.UtcNow;

        var maxOffset = Math.Max(0, count * LyricLayout.LineHeight - ActualHeight);
        _offset = Math.Clamp(_offset + LyricLayout.WheelStep(e.Delta), 0, maxOffset);
        InvalidateVisual();
        StartAnimation();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);

        var count = Lines?.Count ?? 0;
        if (count == 0) return;

        var index = LyricLayout.HitTest(e.GetPosition(this).Y, _offset, count);
        if (index >= 0) LineClicked?.Invoke(index);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        StartAnimation();
    }

    // ---------------- 绘制 ----------------

    protected override void OnRender(DrawingContext dc)
    {
        var lines = Lines;
        if (lines is null || lines.Count == 0) return;

        var dpi = VisualTreeHelper.GetDpi(this);
        var pixelsPerDip = dpi.PixelsPerDip;

        var accent = AccentBrush ?? Brushes.White;
        var baseText = TextBrush ?? Brushes.White;
        var subText = SubTextBrush ?? Brushes.White;

        var (first, last) = LyricLayout.VisibleRange(_offset, ActualHeight, lines.Count);
        if (first < 0) return;

        var primaryTypeface = new Typeface(UiFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
        var currentTypeface = new Typeface(UiFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

        for (var i = first; i <= last; i++)
        {
            var line = lines[i];
            var y = i * LyricLayout.LineHeight - _offset;
            var isCurrent = !IsStatic && i == CurrentIndex;
            var fade = IsStatic ? 1.0 : LyricLayout.LineFade(i - CurrentIndex);

            var primaryBrush = isCurrent ? accent : WithOpacity(baseText, fade);
            var textY = y + 9;

            var primary = new FormattedText(
                line.Primary, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                isCurrent ? currentTypeface : primaryTypeface,
                LyricLayout.PrimaryFontSize, primaryBrush, pixelsPerDip);

            // 水平：跟随模式当前行略放大强调；统一左对齐 + 右边缘修剪
            primary.MaxTextWidth = Math.Max(0, ActualWidth - 16);
            primary.MaxLineCount = 2;
            primary.Trimming = TextTrimming.CharacterEllipsis;
            dc.DrawText(primary, new Point(8, textY));

            if (!string.IsNullOrWhiteSpace(line.Secondary))
            {
                var secondary = new FormattedText(
                    line.Secondary, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    primaryTypeface, LyricLayout.SecondaryFontSize,
                    WithOpacity(subText, fade * 0.8), pixelsPerDip);
                secondary.MaxTextWidth = Math.Max(0, ActualWidth - 16);
                secondary.MaxLineCount = 2;
                secondary.Trimming = TextTrimming.CharacterEllipsis;
                dc.DrawText(secondary, new Point(8, textY + LyricLayout.PrimaryFontSize + LyricLayout.PrimaryToSecondaryGap + 1));
            }
        }
    }

    private static Brush WithOpacity(Brush source, double opacity)
    {
        if (opacity >= 0.999) return source;
        if (source is SolidColorBrush solid)
        {
            var color = solid.Color;
            return new SolidColorBrush(Color.FromArgb(
                (byte)(color.A * Math.Clamp(opacity, 0, 1)),
                color.R, color.G, color.B));
        }
        return source;
    }
}
