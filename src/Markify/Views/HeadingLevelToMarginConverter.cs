using System;
using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;

namespace Markify.Views;

/// <summary>把标题层级（H1..H6）换算成大纲列表的左侧缩进。</summary>
public sealed class HeadingLevelToMarginConverter : IValueConverter
{
    public static readonly HeadingLevelToMarginConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var level = value is int i ? i : 1;
        var indent = Math.Clamp(level - 1, 0, 5) * 12;
        return new Thickness(indent, 0, 0, 0);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
