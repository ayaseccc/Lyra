namespace Player.Core.Lyrics;

/// <summary>
/// 自绘歌词控件的布局与滚动计算（UI-R0）。全部纯函数，harness 可离线断言。
/// 与 WPF 无关（不引用任何 UI 类型），行高、目标偏移、可见范围、淡出、缓动都在这。
/// </summary>
public static class LyricLayout
{
    /// <summary>每行固定高度（px）。主文本 24px + 副文本 16px + 上下留白 12px。</summary>
    public const double LineHeight = 52;

    /// <summary>主文本字号。</summary>
    public const double PrimaryFontSize = 17;

    /// <summary>副文本（翻译/罗马音）字号。</summary>
    public const double SecondaryFontSize = 12.5;

    /// <summary>主文本行内距（主文本与副文本之间）。</summary>
    public const double PrimaryToSecondaryGap = 3;

    /// <summary>
    /// 当前行居中的目标滚动偏移（内容坐标，px）。
    /// 目标 = 行中心 - 视口中心；首行/末行自动钳制到边界（0 和最大偏移）。
    /// </summary>
    public static double TargetOffsetFor(int currentIndex, int lineCount, double viewportHeight)
    {
        if (lineCount <= 0 || viewportHeight <= 0) return 0;
        if (currentIndex < 0) return 0;

        var target = currentIndex * LineHeight + LineHeight / 2 - viewportHeight / 2;
        var maxOffset = Math.Max(0, lineCount * LineHeight - viewportHeight);
        return Math.Clamp(target, 0, maxOffset);
    }

    /// <summary>当前可见行范围 [first, last]（含），空列表返回 (-1, -1)。</summary>
    public static (int First, int Last) VisibleRange(double offset, double viewportHeight, int lineCount)
    {
        if (lineCount <= 0 || viewportHeight <= 0) return (-1, -1);

        var first = (int)Math.Max(0, Math.Floor(offset / LineHeight));
        var last = (int)Math.Min(lineCount - 1, Math.Ceiling((offset + viewportHeight) / LineHeight) - 1);
        return (first, Math.Max(first, last));
    }

    /// <summary>
    /// 行淡出曲线：距离当前行越远越淡（跟随模式用）。
    /// distance=0 → 1.0（当前行，加粗+强调色）；之后逐行递减，>=4 行收敛到 0.38。
    /// </summary>
    public static double LineFade(int distanceFromCurrent)
    {
        var d = Math.Abs(distanceFromCurrent);
        return d switch
        {
            0 => 1.0,
            1 => 0.82,
            2 => 0.66,
            3 => 0.52,
            _ => 0.38
        };
    }

    /// <summary>
    /// 缓动一步：offset 向 target 收敛。dt 秒、系数 k 越大越快。
    /// 返回新偏移。|diff| 小于 0.5px 视为已到位。
    /// </summary>
    public static (double Offset, bool Settled) EaseTowards(double offset, double target, double dt, double k = 10.0)
    {
        if (Math.Abs(target - offset) < 0.5) return (target, true);

        // 指数衰减：新位置 = 旧 + (目标-旧) * (1 - e^(-k*dt))，dt 无关帧率
        var factor = 1.0 - Math.Exp(-k * dt);
        var next = offset + (target - offset) * factor;
        return (next, Math.Abs(target - next) < 0.5);
    }

    /// <summary>命中测试：视口内 y 坐标 → 行号（点击行=跳转用）。越界返回 -1。</summary>
    public static int HitTest(double y, double offset, int lineCount)
    {
        if (lineCount <= 0) return -1;
        var index = (int)Math.Floor((y + offset) / LineHeight);
        return index >= 0 && index < lineCount ? index : -1;
    }

    /// <summary>滚轮一步的滚动量（px）。按行滚动，一次约 2.5 行，方向与滚轮一致。</summary>
    public static double WheelStep(double delta) => delta > 0 ? -LineHeight * 2.5 : LineHeight * 2.5;
}
