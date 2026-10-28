using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Markify.Views;

/// <summary>
/// 把 <c>IsDirectory</c> 布尔值转成树节点前缀图标。
///
/// 用字符而非图片资源：目录树里每行都要一个图标，位图资源会带来额外的加载与缩放开销，
/// 而这两个字形在等宽/系统字体里都已具备（📁/📄）。
/// 只有 Markdown 文件用白色文档图标，其他类型不显示（树里已经过滤掉了）。
/// </summary>
public sealed class FolderIconConverter : IValueConverter
{
    public static readonly FolderIconConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? "📁" : "📄";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
