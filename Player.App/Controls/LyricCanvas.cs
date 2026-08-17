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
/// 自绘歌词控件（UI-R5 单元化排版）。基于 FrameworkElement + OnRender + FormattedText，
/// **禁止** ItemsControl/ListBox/DataTemplate/ScrollViewer。
///
/// R5 规则：
/// - 成对单元：原文+翻译=一个布局单元，无翻译不留空位（动态高度）。
/// - 按栏宽折行（CJK 逐字符 / 拉丁按词），任何情况不省略号、不截断。
/// - 全部水平居中（含折行续行）。
/// - 当前单元整对高亮（原文加粗+强调色，翻译同强调色略淡）；非当前单元统一次级色。
/// - 滚动目标 = 当前单元几何中心，缓动保留；栏宽变化即时重排（布局缓存按宽度失效）。
/// - 元数据行（作词/作曲/编曲/OP/ED 等）已在 VM 层剥离，本控件永不绘制、不参与当前行判定。
/// </summary>
public sealed class LyricCanvas : FrameworkElement
{
    private static readonly Stopwatch FrameClock = Stopwatch.StartNew();
    private double _lastFrameMs = FrameClock.Elapsed.TotalMilliseconds;

    private double _offset;
    private bool _animating;
    private bool _freeBrowse;          // 大页滚轮自由浏览中
    private DateTime _freeBrowseUntil;   // 自由浏览超时点（到点自动回跟随）
    private double _freeBrowseTarget;    // 自由浏览的目标偏移（帧循环滑行到位）

    // 目验八修复：左键按住拖动滚动（大歌词页习惯手势）
    private bool _dragScrollActive;
    private double _dragStartY;
    private double _dragStartOffset;

    private static readonly FontFamily UiFont = new("Microsoft YaHei UI, Segoe UI");

    /// <summary>L1.1-② 字体/字重（跟随设置，右栏/大歌词页共用；桌面歌词字号独立）。缓存键变化时重建。</summary>
    private FontFamily _fontFamily = UiFont;
    private Typeface _primaryTypeface = new(UiFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private Typeface _currentTypeface = new(UiFont, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    private string _layoutFontKey = string.Empty;

    /// <summary>单元渲染缓存（按行号）：折行后的主/副文本 + 单元高度。宽度或数据变化时整体失效。</summary>
    private sealed class UnitRenderData
    {
        public required FormattedText[] Primary;
        public required FormattedText[] Secondary;
        public required double Height;
    }

    private readonly Dictionary<int, UnitRenderData> _unitCache = new();

    /// <summary>当前单元（粗体+强调色）缓存：key 为行号，播放推进时每行只重建一次。</summary>
    private readonly Dictionary<int, FormattedText[]> _currentCache = new();

    /// <summary>布局缓存对应的画布宽度（变化即重排）。</summary>
    private double _layoutWidth = double.NaN;

    /// <summary>布局缓存对应的字号缩放。</summary>
    private double _layoutScale = 1.0;

    // 几何缓存：布局重建时一次生成，渲染帧和鼠标交互只读，避免每帧
    // ComputeHeights/ComputeUnitTops 产生数组并重复 O(n) 扫描。
    private double[] _layoutHeights = Array.Empty<double>();
    private double[] _layoutTops = Array.Empty<double>();
    private double _layoutTotalHeight;

    /// <summary>缓存失效标志：Lines 或宽度变化时置 true，下一帧重建。</summary>
    private bool _cacheDirty = true;

    // 预生成画刷：当前单元原文/翻译、非当前单元原文/翻译
    private SolidColorBrush? _currentBrush;
    private SolidColorBrush? _currentSubBrush;
    private SolidColorBrush? _normalBrush;
    private SolidColorBrush? _normalSubBrush;
    private Color _brushBase;
    private Color _brushSubBase;
    private Color _brushAccentBase;

    /// <summary>点击某行（参数为行号）。</summary>
    public event Action<int>? LineClicked;

    /// <summary>大歌词页：双击空白/歌词任意处=退出（由宿主处理）。</summary>
    public event Action? DoubleClicked;

    /// <summary>大歌词页：点击未命中歌词行的空白（参数为画布内 x），宿主按左/右半区切曲。</summary>
    public event Action<double>? BlankClicked;

    /// <summary>
    /// 大歌词页点击模式（目验五修复：跳转+分区导航合一）：
    /// Seek=右栏默认（点行跳转）；SeekOrNavigate=大页（点行=跳转，点空白=左/右半区切曲，双击=退出）；Disabled=不处理点击。
    /// </summary>
    public enum LyricClickMode { Seek, SeekOrNavigate, Disabled }

    public LyricClickMode ClickMode { get; set; } = LyricClickMode.Seek;

    /// <summary>大歌词页滚轮自由浏览（目验五修复）：滚轮临时滚动，播放推进到新行时自动回到跟随。</summary>
    public bool WheelBrowsing { get; set; }

    /// <summary>字号缩放（大歌词页用，默认 1.0；同时缩放行高/间距/折行宽度判断）。</summary>
    public double FontScale
    {
        get => (double)GetValue(FontScaleProperty);
        set => SetValue(FontScaleProperty, value);
    }

    public static readonly DependencyProperty FontScaleProperty = DependencyProperty.Register(
        nameof(FontScale), typeof(double), typeof(LyricCanvas),
        new PropertyMetadata(1.0, OnVisualPropertyChanged));

    public LyricCanvas()
    {
        ClipToBounds = true;
        // 目验五修复：不再自设 Focusable（Tab 焦点框问题），需要键盘时由宿主统一管理
        Cursor = Cursors.Hand;
        Loaded += OnLoaded;
        IsVisibleChanged += OnIsVisibleChanged;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e) => StartAnimation();

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible) StartAnimation();
        else StopAnimation();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e) => StopAnimation();

    /// <summary>目验六修复：自定义元素全区域可命中（无 Background 的 FrameworkElement 默认只在渲染内容上命中，
    /// 导致点击空白/双击/滚轮在空白区域全部落空）。</summary>
    protected override HitTestResult HitTestCore(PointHitTestParameters hitTestParameters) =>
        new PointHitTestResult(this, hitTestParameters.HitPoint);

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

        // 行数据或宽度变化 → 布局缓存整体失效
        if (e.Property == LinesProperty)
        {
            canvas._cacheDirty = true;
            canvas.ClearGeometryCache();
        }

        canvas.InvalidateVisual();
        canvas.StartAnimation();
    }

    // ---------------- 滚动与动画 ----------------

    private void StartAnimation()
    {
        if (_animating || !IsVisible || ActualWidth <= 0 || ActualHeight <= 0 || IsStatic) return;
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
        if (!IsVisible || ActualWidth <= 0 || ActualHeight <= 0)
        {
            StopAnimation();
            return;
        }

        if (_cacheDirty)
        {
            // OnRender 将在本帧重建布局；不要用尚未完成的旧几何推进偏移。
            InvalidateVisual();
            return;
        }

        var nowMs = FrameClock.Elapsed.TotalMilliseconds;
        var dt = Math.Min(0.05, Math.Max(0.001, (nowMs - _lastFrameMs) / 1000.0));
        _lastFrameMs = nowMs;

        var heights = ComputeHeights();
        if (heights.Length == 0 || IsStatic)
        {
            StopAnimation();
            return;
        }

        // 目验六/七修复：大页滚轮自由浏览——超时（2 秒无操作）才回跟随；浏览期间向目标偏移滑动（滚动感）
        if (_freeBrowse && DateTime.UtcNow >= _freeBrowseUntil)
            _freeBrowse = false;
        if (_freeBrowse)
        {
            var diff = _freeBrowseTarget - _offset;
            if (Math.Abs(diff) < 0.5) _offset = _freeBrowseTarget;
            else _offset += diff * Math.Min(1.0, dt * 22.0);   // 快速滑行（≈100ms 到位）
            InvalidateVisual();
            return;
        }

        var target = TargetOffsetForCurrent(ActualHeight);
        var (next, settled) = LyricLayout.EaseTowards(_offset, target, dt);
        _offset = next;
        InvalidateVisual();

        if (settled)
            StopAnimation();
    }

    /// <summary>所有单元的当前高度（无布局时为 -1 表示需要重排）。</summary>
    private double[] ComputeHeights()
        => _layoutHeights;

    private void ClearGeometryCache()
    {
        _layoutHeights = Array.Empty<double>();
        _layoutTops = Array.Empty<double>();
        _layoutTotalHeight = 0;
    }

    private void RebuildGeometryCache(IReadOnlyList<LyricRenderLine> lines)
    {
        if (lines.Count == 0)
        {
            ClearGeometryCache();
            return;
        }

        var heights = new double[lines.Count];
        var tops = new double[lines.Count];
        var total = 0.0;
        for (var i = 0; i < lines.Count; i++)
        {
            tops[i] = total;
            heights[i] = _unitCache.TryGetValue(i, out var data) ? data.Height : 0;
            total += heights[i];
            if (i + 1 < lines.Count) total += LyricLayout.UnitGap;
        }

        _layoutHeights = heights;
        _layoutTops = tops;
        _layoutTotalHeight = total;
    }

    private double TargetOffsetForCurrent(double viewportHeight)
    {
        if (CurrentIndex < 0 || _layoutHeights.Length == 0 || viewportHeight <= 0)
            return 0;

        var index = Math.Clamp(CurrentIndex, 0, _layoutHeights.Length - 1);
        var target = _layoutTops[index] + _layoutHeights[index] / 2 - viewportHeight / 2;
        return Math.Clamp(target, 0, Math.Max(0, _layoutTotalHeight - viewportHeight));
    }

    private (int First, int Last) VisibleUnitRange(double offset, double viewportHeight)
    {
        if (_layoutHeights.Length == 0 || viewportHeight <= 0) return (-1, -1);

        var lo = 0;
        var hi = _layoutHeights.Length - 1;
        var first = _layoutHeights.Length;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) / 2);
            if (_layoutTops[mid] + _layoutHeights[mid] >= offset)
            {
                first = mid;
                hi = mid - 1;
            }
            else
            {
                lo = mid + 1;
            }
        }

        if (first == _layoutHeights.Length) return (-1, -1);

        lo = first;
        hi = _layoutHeights.Length - 1;
        var last = first;
        var bottom = offset + viewportHeight;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) / 2);
            if (_layoutTops[mid] <= bottom)
            {
                last = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return (first, last);
    }

    private int HitTestUnit(double y, double offset)
    {
        var contentY = y + offset;
        var lo = 0;
        var hi = _layoutHeights.Length - 1;
        var candidate = -1;
        while (lo <= hi)
        {
            var mid = lo + ((hi - lo) / 2);
            if (_layoutTops[mid] <= contentY)
            {
                candidate = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return candidate >= 0 && contentY < _layoutTops[candidate] + _layoutHeights[candidate]
            ? candidate
            : -1;
    }

    // ---------------- 交互 ----------------

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);

        // 静态模式（无时间轴长歌词）：滚轮是唯一的浏览方式，保留。
        // 大歌词页（WheelBrowsing 目验六修复）：滚轮完全接管（高灵敏度 4 行/格），
        // 停止操作 2 秒后自动回跳当前歌词。
        if (!IsStatic && !WheelBrowsing) return;

        var heights = ComputeHeights();
        if (heights.Length == 0) return;

        var maxOffset = Math.Max(0, _layoutTotalHeight - ActualHeight);
        var step = WheelBrowsing && !IsStatic ? LyricLayout.PrimaryLineHeight * 4 : LyricLayout.WheelStep(e.Delta);
        if (WheelBrowsing && !IsStatic)
        {
            // 目验七修复：目标偏移累加 + 帧循环滑行到位（滚动感），连续滚轮自然累积
            _freeBrowse = true;
            _freeBrowseTarget = Math.Clamp((_freeBrowseTarget == 0 ? _offset : _freeBrowseTarget)
                                           + (e.Delta > 0 ? -step : step), 0, maxOffset);
            _freeBrowseUntil = DateTime.UtcNow.AddSeconds(2);   // 2 秒无操作自动回跟随
        }
        else
        {
            _offset = Math.Clamp(_offset + (e.Delta > 0 ? -step : step), 0, maxOffset);
        }
        InvalidateVisual();
        StartAnimation();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        // 目验八修复：记录按下起点（可能开始拖动滚动）；不标记 Handled，点击逻辑照常
        _dragStartY = e.GetPosition(this).Y;
        _dragStartOffset = _offset;
        _dragScrollActive = false;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (ClickMode != LyricClickMode.SeekOrNavigate) return;
        if (e.LeftButton != MouseButtonState.Pressed) return;

        var pos = e.GetPosition(this);
        if (!_dragScrollActive && Math.Abs(pos.Y - _dragStartY) > 8)
        {
            _dragScrollActive = true;
            CaptureMouse();
        }
        if (!_dragScrollActive) return;

        var heights = ComputeHeights();
        if (heights.Length == 0) return;
        var maxOffset = Math.Max(0, _layoutTotalHeight - ActualHeight);
        // 手指上滑 = 内容上移 = 偏移增大
        _offset = Math.Clamp(_dragStartOffset + (_dragStartY - pos.Y), 0, maxOffset);
        _freeBrowse = true;
        _freeBrowseTarget = _offset;
        _freeBrowseUntil = DateTime.UtcNow.AddSeconds(2);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        _dragScrollActive = false;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (ClickMode == LyricClickMode.Disabled) return;

        // 目验八修复：拖动结束=滚动完成（不触发点击动作）
        if (_dragScrollActive)
        {
            _dragScrollActive = false;
            ReleaseMouseCapture();
            e.Handled = true;
            return;
        }

        var heights = ComputeHeights();
        if (heights.Length == 0) return;

        var pos = e.GetPosition(this);
        if (ClickMode == LyricClickMode.SeekOrNavigate && e.ClickCount >= 2)
        {
            DoubleClicked?.Invoke();
            return;
        }

        var index = HitTestUnit(pos.Y, _offset);
        // 目验七修复：命中收窄到文字横向范围——行 Y 上但 x 远离文字的点击算空白（左/右半区导航）
        if (index >= 0 && IsOverUnitText(index, pos.X))
        {
            LineClicked?.Invoke(index);
            return;
        }
        if (ClickMode == LyricClickMode.SeekOrNavigate)
            BlankClicked?.Invoke(pos.X);
    }

    /// <summary>判断 x 是否落在某单元的文本横向范围（居中文本 ±24px 容差）；无缓存数据时视为命中。</summary>
    private bool IsOverUnitText(int index, double x)
    {
        if (!_unitCache.TryGetValue(index, out var data)) return true;
        var maxWidth = 0.0;
        foreach (var ft in data.Primary) maxWidth = Math.Max(maxWidth, ft.Width);
        foreach (var ft in data.Secondary) maxWidth = Math.Max(maxWidth, ft.Width);
        if (maxWidth <= 0) return true;
        var center = ActualWidth / 2;
        return Math.Abs(x - center) <= maxWidth / 2 + 24;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        _cacheDirty = true;   // 栏宽变化即时重排（R5 ⑥）
        ClearGeometryCache();
        StartAnimation();
    }

    // ---------------- 布局与绘制 ----------------

    protected override void OnRender(DrawingContext dc)
    {
        var lines = Lines;
        if (lines is null || lines.Count == 0)
        {
            _unitCache.Clear();
            _currentCache.Clear();
            ClearGeometryCache();
            _cacheDirty = false;
            _offset = 0;
            StopAnimation();
            return;
        }

        var dpi = VisualTreeHelper.GetDpi(this);
        var pixelsPerDip = dpi.PixelsPerDip;
        var scale = FontScale;
        var primaryFont = LyricLayout.PrimaryFontSize * scale;
        var secondaryFont = LyricLayout.SecondaryFontSize * scale;
        var primaryLine = LyricLayout.PrimaryLineHeight * scale;
        var secondaryLine = LyricLayout.SecondaryLineHeight * scale;
        var innerGap = LyricLayout.InnerGap * scale;
        var maxWidth = Math.Max(0, ActualWidth - 16);

        // L1.1-②：字体/字重跟随设置，变化时重建字型并整表重排
        var fontKey = LyricFontKey();
        if (_layoutFontKey != fontKey)
        {
            _layoutFontKey = fontKey;
            _fontFamily = ResolveFontFamily();
            var weightKey = Player.Core.Infra.ConfigService.Current.Ui.LyricFontWeight;
            _primaryTypeface = new Typeface(_fontFamily, FontStyles.Normal,
                LyricUiOptions.ParseWeight(weightKey), FontStretches.Normal);
            _currentTypeface = new Typeface(_fontFamily, FontStyles.Normal,
                LyricUiOptions.CurrentLineWeight(weightKey), FontStretches.Normal);
            _cacheDirty = true;
        }

        // 数据、宽度、字号缩放或字体变化 → 整表重排（R5 ⑥：栏宽变化即时重排）
        if (_cacheDirty || Math.Abs(_layoutWidth - ActualWidth) > 0.5 || Math.Abs(_layoutScale - scale) > 0.001)
        {
            _unitCache.Clear();
            _currentCache.Clear();
            _layoutWidth = ActualWidth;
            _layoutScale = scale;
            _cacheDirty = false;

            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                var primary = LyricLayout.WrapText(line.Primary, maxWidth, s => Measure(s, _primaryTypeface, primaryFont, pixelsPerDip));
                var secondary = string.IsNullOrWhiteSpace(line.Secondary)
                    ? Array.Empty<string>()
                    : LyricLayout.WrapText(line.Secondary, maxWidth, s => Measure(s, _primaryTypeface, secondaryFont, pixelsPerDip));

                var primaryFt = primary.Select(t => Build(t, _primaryTypeface, primaryFont, pixelsPerDip)).ToArray();
                var secondaryFt = secondary.Select(t => Build(t, _primaryTypeface, secondaryFont, pixelsPerDip)).ToArray();

                var height = primaryFt.Length * primaryLine
                             + (secondaryFt.Length > 0
                                 ? secondaryFt.Length * secondaryLine + innerGap
                                 : 0);
                _unitCache[i] = new UnitRenderData { Primary = primaryFt, Secondary = secondaryFt, Height = height };
            }

            RebuildGeometryCache(lines);
        }

        var heights = ComputeHeights();
        if (heights.Length == 0) return;

        var accent = AccentBrush ?? Brushes.White;
        var baseText = TextBrush ?? Brushes.White;
        var subText = SubTextBrush ?? Brushes.White;
        EnsureBrushes(baseText, subText, accent);

        var (first, last) = VisibleUnitRange(_offset, ActualHeight);
        if (first < 0) return;
        last = Math.Min(last, lines.Count - 1);   // 防御（曾偶发越界）

        var tops = _layoutTops;

        for (var i = first; i <= last; i++)
        {
            if (i < 0 || i >= lines.Count) continue;   // 防御
            if (!_unitCache.TryGetValue(i, out var data)) continue;

            var isCurrent = !IsStatic && i == CurrentIndex;
            var y = tops[i] - _offset;

            var primaryFts = isCurrent ? CurrentFts(i, lines[i].Primary, maxWidth, pixelsPerDip) : data.Primary;

            // ---- 原文（全部水平居中，含折行续行） ----
            foreach (var ft in primaryFts)
            {
                var x = Math.Max(0, (ActualWidth - ft.Width) / 2);
                ft.SetForegroundBrush(isCurrent ? _currentBrush : _normalBrush);
                dc.DrawText(ft, new Point(x, y));
                y += LyricLayout.PrimaryLineHeight * scale;
            }

            // ---- 翻译 / 罗马音（当前单元整对高亮） ----
            if (data.Secondary.Length > 0)
            {
                y += LyricLayout.InnerGap * scale;
                foreach (var ft in data.Secondary)
                {
                    var x = Math.Max(0, (ActualWidth - ft.Width) / 2);
                    ft.SetForegroundBrush(isCurrent ? _currentSubBrush : _normalSubBrush);
                    dc.DrawText(ft, new Point(x, y));
                    y += LyricLayout.SecondaryLineHeight * scale;
                }
            }
        }
    }

    /// <summary>当前单元：粗体+强调色，按行号缓存（播放推进时每行只重建一次）。</summary>
    private FormattedText[] CurrentFts(int index, string text, double maxWidth, double pixelsPerDip)
    {
        if (_currentCache.TryGetValue(index, out var cached)) return cached;

        var scale = FontScale;
        var wrapped = LyricLayout.WrapText(text, maxWidth, s => Measure(s, _currentTypeface, LyricLayout.PrimaryFontSize * scale, pixelsPerDip));
        var fts = wrapped.Select(t => Build(t, _currentTypeface, LyricLayout.PrimaryFontSize * scale, pixelsPerDip)).ToArray();
        _currentCache[index] = fts;
        return fts;
    }

    /// <summary>设置里的字体键（族名|字重），变化即重建字型与布局。</summary>
    private static string LyricFontKey() =>
        (Player.Core.Infra.ConfigService.Current.Ui.LyricFontFamily ?? string.Empty) + "|"
        + (Player.Core.Infra.ConfigService.Current.Ui.LyricFontWeight ?? string.Empty);

    private static FontFamily ResolveFontFamily()
    {
        var name = Player.Core.Infra.ConfigService.Current.Ui.LyricFontFamily;
        return string.IsNullOrWhiteSpace(name) ? UiFont : new FontFamily(name);
    }

    private static double Measure(string text, Typeface typeface, double fontSize, double pixelsPerDip) =>
        new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            typeface, fontSize, Brushes.Black, pixelsPerDip).Width;

    private static FormattedText Build(string text, Typeface typeface, double fontSize, double pixelsPerDip) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
            typeface, fontSize, Brushes.Black, pixelsPerDip);

    /// <summary>
    /// 预生成四把画刷：当前单元 原文=强调色 / 翻译=强调色 70%；非当前 原文=次级色 / 翻译=次级色 75%。
    /// 主题（刷子颜色）变化时自动重建。
    /// </summary>
    private void EnsureBrushes(Brush baseText, Brush subText, Brush accent)
    {
        if (baseText is not SolidColorBrush primarySolid || subText is not SolidColorBrush subSolid || accent is not SolidColorBrush accentSolid)
            return;
        if (_normalBrush is not null && _brushBase == primarySolid.Color && _brushSubBase == subSolid.Color && _brushAccentBase == accentSolid.Color)
            return;

        _brushBase = primarySolid.Color;
        _brushSubBase = subSolid.Color;
        _brushAccentBase = accentSolid.Color;

        _normalBrush = Freeze(new SolidColorBrush(subSolid.Color));
        _normalSubBrush = Freeze(new SolidColorBrush(Color.FromArgb((byte)(subSolid.Color.A * 0.75), subSolid.Color.R, subSolid.Color.G, subSolid.Color.B)));
        _currentBrush = Freeze(new SolidColorBrush(accentSolid.Color));
        _currentSubBrush = Freeze(new SolidColorBrush(Color.FromArgb((byte)(accentSolid.Color.A * 0.70), accentSolid.Color.R, accentSolid.Color.G, accentSolid.Color.B)));
    }

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
