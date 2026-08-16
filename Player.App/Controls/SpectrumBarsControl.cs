using System;
using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace Player.App.Controls;

/// <summary>Allocation-free 16-bar spectrum renderer for the mini surface.</summary>
public sealed class SpectrumBarsControl : FrameworkElement
{
    public const int BarCount = 16;
    private const double AttackSeconds = 0.065;
    private const double ReleaseSeconds = 0.22;
    private const float NoiseFloor = 0.012f;

    private readonly float[] _levels = new float[BarCount];
    private long _lastUpdateTimestamp;

    public static readonly DependencyProperty BarBrushProperty = DependencyProperty.Register(
        nameof(BarBrush),
        typeof(Brush),
        typeof(SpectrumBarsControl),
        new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public Brush BarBrush
    {
        get => (Brush)GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    public void SetLevels(ReadOnlySpan<float> levels)
    {
        var now = Stopwatch.GetTimestamp();
        var elapsedSeconds = _lastUpdateTimestamp == 0
            ? 1d / 30d
            : (now - _lastUpdateTimestamp) / (double)Stopwatch.Frequency;
        _lastUpdateTimestamp = now;
        elapsedSeconds = Math.Clamp(elapsedSeconds, 1d / 240d, 0.1d);

        var count = Math.Min(levels.Length, BarCount);
        for (var i = 0; i < count; i++)
        {
            var target = Math.Clamp(levels[i], 0, 1);
            if (target < NoiseFloor) target = 0;
            _levels[i] = Smooth(_levels[i], target, elapsedSeconds);
        }
        for (var i = count; i < BarCount; i++)
            _levels[i] = Smooth(_levels[i], 0, elapsedSeconds);

        InvalidateVisual();
    }

    private static float Smooth(float current, float target, double elapsedSeconds)
    {
        var timeConstant = target > current ? AttackSeconds : ReleaseSeconds;
        var blend = 1d - Math.Exp(-elapsedSeconds / timeConstant);
        var next = current + (target - current) * (float)blend;
        return target == 0 && next < 0.002f ? 0 : next;
    }

    public void Clear()
    {
        Array.Clear(_levels);
        _lastUpdateTimestamp = 0;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0 || BarBrush is null) return;

        const double gap = 3;
        var barWidth = Math.Clamp((width - gap * (BarCount - 1)) / BarCount, 2, 9);
        var contentWidth = barWidth * BarCount + gap * (BarCount - 1);
        var startX = Math.Max(0, (width - contentWidth) / 2);

        drawingContext.PushOpacity(0.82);
        for (var i = 0; i < BarCount; i++)
        {
            var level = _levels[i];
            if (level <= 0.002f) continue;

            var barHeight = Math.Max(2, level * height);
            var x = startX + i * (barWidth + gap);
            var rect = new Rect(x, height - barHeight, barWidth, barHeight);
            drawingContext.DrawRoundedRectangle(BarBrush, null, rect, 1.5, 1.5);
        }
        drawingContext.Pop();
    }
}
