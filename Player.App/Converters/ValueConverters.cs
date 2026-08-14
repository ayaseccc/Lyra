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