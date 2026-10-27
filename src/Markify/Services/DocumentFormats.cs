using System;
using System.IO;

namespace Markify.Services;

/// <summary>集中管理受支持的文档扩展名，避免各处重复硬编码。</summary>
public static class DocumentFormats
{
    private static readonly string[] Extensions = [".md", ".markdown", ".mdown", ".mkd", ".mdx", ".txt"];

    public const string PickerPatterns = "*.md;*.markdown;*.mdown;*.mkd;*.mdx;*.txt";

    public static bool IsSupported(string path)
    {
        var extension = Path.GetExtension(path);
        return Array.Exists(Extensions, e => string.Equals(e, extension, StringComparison.OrdinalIgnoreCase));
    }
}
