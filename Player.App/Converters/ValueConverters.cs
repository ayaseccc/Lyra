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

/// <summary>音量方块（UI-R1.5 反馈）：已到达的方块用浅灰，未到达的用深灰/近黑（容量槽样式）。</summary>
public sealed class VolumeSquareConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var level = value is int i ? i : 0;
        var index = parameter is string s && int.TryParse(s, out var n) ? n : 0;
        if (level > index)
        {
            return System.Windows.Application.Current?.TryFindResource("VolumeSlotBrush") as SolidColorBrush
                   ?? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8B, 0x8B, 0x8B));
        }
        return System.Windows.Application.Current?.TryFindResource("VolumeReachedBrush") as SolidColorBrush
               ?? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3A, 0x3A, 0x3A));
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