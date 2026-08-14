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
    private bool _animating;

    /// <summary>FrameworkElement 没有 FontFamily 属性，这里自持字体。</summary>
    private static readonly FontFamily UiFont = new("Microsoft YaHei UI, Segoe UI");

    /// <summary>
    /// 主/副文本的 FormattedText 缓存（按行号）。FormattedText 构造开销大，
    /// 滚动时只有当前行（粗体）每帧重建，其余行命中缓存——解决滚动卡顿。
    /// 数据变化或尺寸变化时整体失效。
    /// </summary>
    private readonly Dictionary<int, FormattedText> _primaryCache = new();
    private readonly Dictionary<int, FormattedText> _secondaryCache = new();

    /// <summary>缓存失效标志：Lines 或宽度变化时置 true，下一帧重建。</summary>
    private bool _cacheDirty = true;

    /// <summary>当前行（粗体+强调色）缓存：key 为行号，播放推进时每行只重建一次。</summary>
    private readonly Dictionary<int, FormattedText> _currentCache = new();

    /// <summary>
    /// 预生成的淡出画刷（按距离 0..4 对应 LineFade 五档，主文本与副文本各一套）。
    /// 替代 PushOpacity——后者每帧做离屏渲染切换，是滚动卡顿的根源。
    /// </summary>
    private SolidColorBrush[] _primaryFadeBrushes = Array.Empty<SolidColorBrush>();
    private SolidColorBrush[] _secondaryFadeBrushes = Array.Empty<SolidColorBrush>();
    private Color _fadeBrushBase = Colors.Transparent;
    private Color _fadeBrushSubBase = Colors.Transparent;

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

        // 行数据变化 → 文本缓存失效
        if (e.Property == LinesProperty) canvas._cacheDirty = true;

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

        var count = Lines?.Count ?? 0;
        if (count == 0 || IsStatic)
        {
            // 静态模式无跟随目标；空列表无内容
            StopAnimation();
            return;
        }

        var target = LyricLayout.TargetOffsetFor(CurrentIndex, count, ActualHeight);

        var (next, settled) = LyricLayout.EaseTowards(_offset, target, dt);
        _offset = next;
        InvalidateVisual();

        if (settled)
            StopAnimation();
    }

    // ---------------- 交互 ----------------

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);

        // 跟随模式（有时间轴）：滚轮不介入，避免手动滚动破坏跟随（用户要求）。
        // 静态模式（无时间轴长歌词）：滚轮是唯一的浏览方式，保留。
        if (!IsStatic) return;

        var count = Lines?.Count ?? 0;
        if (count == 0) return;

        var maxOffset = Math.Max(0, count * LyricLayout.LineHeight - ActualHeight);
        _offset = Math.Clamp(_offset + LyricLayout.WheelStep(e.Delta), 0, maxOffset);
        InvalidateVisual();
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

        // 宽度变化会影响 MaxTextWidth → 文本缓存失效
        _cacheDirty = true;
        StartAnimation();
    }

    // ---------------- 绘制 ----------------

    protected override void OnRender(DrawingContext dc)
    {
        var lines = Lines;
        if (lines is null || lines.Count == 0) return;

        // 数据或宽度变了 → 缓存整体失效
        if (_cacheDirty)
        {
            _primaryCache.Clear();
            _secondaryCache.Clear();
            _currentCache.Clear();
            _cacheDirty = false;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var pixelsPerDip = dpi.PixelsPerDip;

        var accent = AccentBrush ?? Brushes.White;
        var baseText = TextBrush ?? Brushes.White;
        var subText = SubTextBrush ?? Brushes.White;
        var maxWidth = Math.Max(0, ActualWidth - 16);

        EnsureFadeBrushes(baseText, subText);

        var (first, last) = LyricLayout.VisibleRange(_offset, ActualHeight, lines.Count);
        if (first < 0) return;

        for (var i = first; i <= last; i++)
        {
            var line = lines[i];
            var y = i * LyricLayout.LineHeight - _offset;
            var isCurrent = !IsStatic && i == CurrentIndex;
            var fade = IsStatic ? 1.0 : LyricLayout.LineFade(i - CurrentIndex);

            var textY = y + 9;

            // ---- 主文本（单行省略） ----
            if (isCurrent)
            {
                // 当前行：粗体+强调色，按行号缓存（播放推进时每行只重建一次）
                if (!_currentCache.TryGetValue(i, out var current))
                {
                    current = new FormattedText(
                        line.Primary, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                        CurrentTypeface, LyricLayout.PrimaryFontSize, accent, pixelsPerDip);
                    current.MaxTextWidth = maxWidth;
                    current.MaxLineCount = 1;
                    current.Trimming = TextTrimming.CharacterEllipsis;
                    _currentCache[i] = current;
                }

                dc.DrawText(current, new Point(8, textY));
            }
            else
            {
                if (!_primaryCache.TryGetValue(i, out var primary))
                {
                    primary = new FormattedText(
                        line.Primary, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                        PrimaryTypeface, LyricLayout.PrimaryFontSize, baseText, pixelsPerDip);
                    primary.MaxTextWidth = maxWidth;
                    primary.MaxLineCount = 1;
                    primary.Trimming = TextTrimming.CharacterEllipsis;
                    _primaryCache[i] = primary;
                }

                // 淡出画刷预生成（无离屏渲染、无分配）
                var distance = Math.Min(4, Math.Abs(i - CurrentIndex));
                if (_primaryFadeBrushes.Length == 5)
                    primary.SetForegroundBrush(_primaryFadeBrushes[distance]);
                dc.DrawText(primary, new Point(8, textY));
            }

            // ---- 副文本（单行省略，走缓存；当前行不淡出） ----
            if (!string.IsNullOrWhiteSpace(line.Secondary))
            {
                if (!_secondaryCache.TryGetValue(i, out var secondary))
                {
                    secondary = new FormattedText(
                        line.Secondary, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                        PrimaryTypeface, LyricLayout.SecondaryFontSize, subText, pixelsPerDip);
                    secondary.MaxTextWidth = maxWidth;
                    secondary.MaxLineCount = 1;
                    secondary.Trimming = TextTrimming.CharacterEllipsis;
                    _secondaryCache[i] = secondary;
                }

                var distance = Math.Min(4, Math.Abs(i - CurrentIndex));
                if (_secondaryFadeBrushes.Length == 5)
                    secondary.SetForegroundBrush(_secondaryFadeBrushes[distance]);
                dc.DrawText(secondary, new Point(8, textY + LyricLayout.PrimaryFontSize + LyricLayout.PrimaryToSecondaryGap + 1));
            }
        }
    }

    private static readonly Typeface PrimaryTypeface =
        new(UiFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    private static readonly Typeface CurrentTypeface =
        new(UiFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    /// <summary>
    /// 按距离 0..4 预生成主/副文本的淡出画刷（LineFade 五档）。
    /// 绘制时用 FormattedText.SetForegroundBrush 覆盖颜色——没有离屏渲染、没有每帧分配。
    /// </summary>
    private void EnsureFadeBrushes(Brush baseText, Brush subText)
    {
        if (baseText is not SolidColorBrush primarySolid || subText is not SolidColorBrush subSolid)
            return;
        if (_primaryFadeBrushes.Length == 5 && _fadeBrushBase == primarySolid.Color)
            return;

        _fadeBrushBase = primarySolid.Color;
        _fadeBrushSubBase = subSolid.Color;

        _primaryFadeBrushes = new SolidColorBrush[5];
        _secondaryFadeBrushes = new SolidColorBrush[5];

        for (var d = 0; d < 5; d++)
        {
            var fade = LyricLayout.LineFade(d);

            var primary = new SolidColorBrush(Color.FromArgb(
                (byte)(primarySolid.Color.A * fade),
                primarySolid.Color.R, primarySolid.Color.G, primarySolid.Color.B));
            primary.Freeze();
            _primaryFadeBrushes[d] = primary;

            var secondary = new SolidColorBrush(Color.FromArgb(
                (byte)(subSolid.Color.A * fade * 0.8),
                subSolid.Color.R, subSolid.Color.G, subSolid.Color.B));
            secondary.Freeze();
            _secondaryFadeBrushes[d] = secondary;
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