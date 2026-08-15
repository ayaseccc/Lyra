using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Player.App.Infra;

namespace Player.App.Converters;

/// <summary>封面 hash → 位图；没有封面时返回 null，界面上会退回占位图标。</summary>
public sealed class CoverHashToImageConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => CoverImageCache.Get(value as string);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>值为 null / 空字符串时 Visible，否则 Collapsed（用于封面占位图标）。</summary>
public sealed class NullToVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null || (value is string s && s.Length == 0) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>bool 取反后转 Visibility。</summary>
public sealed class InverseBoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>字符串非空时 Visible（"有内容才显示"场景，如歌词来源 badge）。</summary>
public sealed class NonEmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s && s.Length > 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>字符串为空时 Visible（UI-R4：标签没有制作信息时显示 LRC 头部补位）。</summary>
public sealed class EmptyToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not string s || s.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}


/// <summary>bool → 强调色/半透明画刷（播放模式按钮、歌词开关的选中高亮，UI-R1）。</summary>
public sealed class BoolToAccentConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is true)
        {
            var accent = System.Windows.Application.Current?.TryFindResource("AccentBrush") as SolidColorBrush
                         ?? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0xCD, 0xFF));
            return accent;
        }

        return new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// 音量方块（L1.1-① 语义色修正）：返回该格的透明度——已到达格=1.0（前景强调色，醒目），
/// 未到达格=0.22（弱化）。方块背景统一绑定 DynamicResource VolumeReachedBrush：
/// 主题切换/300ms 过渡由 DynamicResource 自动跟随，明暗语义在深浅两挡主题下都成立
/// （浅色：已到达=深色前景、未到达≈浅灰；深色：已到达=亮前景、未到达=深灰）。
/// 旧实现把 R1.5 写死的灰度直接当颜色用，浅色主题下明暗倒挂。
/// </summary>
public sealed class VolumeSquareConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var level = value is int i ? i : 0;
        var index = parameter is string s && int.TryParse(s, out var n) ? n : 0;
        return level > index ? 1.0 : 0.22;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>bool → 列表首列宽度（UI-R2 修订）：分组模式 84px 封面列，平铺模式 0（无封面列）。</summary>
public sealed class BoolToGroupCoverWidthConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? new System.Windows.GridLength(84) : new System.Windows.GridLength(0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>double → GridLength（L3.1 列宽绑定；<=0 视为 0 隐藏列）。</summary>
public sealed class DoubleToGridLengthConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is double d && d > 0 ? new System.Windows.GridLength(d) : new System.Windows.GridLength(0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>列索引 → 可见性（L3.1 列隐藏：-1 → Collapsed，其余 Visible）。</summary>
public sealed class ColumnIndexToVisibilityConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int i && i < 0 ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>bool → 分组折叠箭头（L3.1：true=折叠 ▸ / false=展开 ▾）。</summary>
public sealed class BoolToCollapseArrowConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "▸" : "▾";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>多绑定：行路径 == 当前播放路径（UI-R2 的 ▶ 与整行淡色高亮）。</summary>
public sealed class TrackIsCurrentConverter : IMultiValueConverter
{
    public object? Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var rowPath = values.Length > 0 ? values[0] as string : null;
        var currentPath = values.Length > 1 ? values[1] as string : null;
        if (string.IsNullOrEmpty(rowPath) || string.IsNullOrEmpty(currentPath)) return false;
        return string.Equals(rowPath, currentPath, StringComparison.OrdinalIgnoreCase);
    }

    public object?[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>bool → 强调色/次级文字色（UI-R1.5 ⑬：激活态用强调色着色，不用实心填充）。</summary>
public sealed class BoolToTintBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var app = System.Windows.Application.Current;
        if (value is true)
            return app?.TryFindResource("AccentBrush") as SolidColorBrush
                   ?? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x60, 0xCD, 0xFF));
        return app?.TryFindResource("TextSecondaryBrush") as SolidColorBrush
               ?? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xB6, 0xB6, 0xB6));
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}