using System;
using System.Collections.Generic;
using System.Text;
using Markdig;
using Markdig.Syntax;

namespace Markify.Services;

/// <summary>一个标题及其在源码中的行号与预览锚点。</summary>
public sealed record MarkdownHeading(int Line, int Level, string Text, string Slug)
{
    /// <summary>用于缩进显示的子级层级（相对 H1）。</summary>
    public string Indent => new(' ', (Level - 1) * 2);
}

/// <summary>
/// 从 Markdown 源码中提取标题大纲。
/// 锚点规则与 GitHub/MarkView 一致：转小写、空格转连字符、其余标点移除、重名追加 -1/-2。
/// </summary>
public static class MarkdownOutline
{
    private static readonly MarkdownPipeline Pipeline =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    /// <summary>按出现顺序返回全部标题。</summary>
    public static IReadOnlyList<MarkdownHeading> Parse(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
            return [];

        var document = Markdown.Parse(markdown, Pipeline);
        var headings = new List<MarkdownHeading>();
        var slugCounts = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var heading in document.Descendants<HeadingBlock>())
        {
            var text = GetPlainText(heading);
            var slug = ToSlug(text);

            if (slugCounts.TryGetValue(slug, out var count))
            {
                slugCounts[slug] = count + 1;
                slug = $"{slug}-{count}";
            }
            else
            {
                slugCounts[slug] = 1;
            }

            headings.Add(new MarkdownHeading(heading.Line + 1, heading.Level, text, slug));
        }

        return headings;
    }

    private static string GetPlainText(HeadingBlock heading)
    {
        var builder = new StringBuilder();
        if (heading.Inline is not null)
        {
            foreach (var inline in heading.Inline)
                AppendInline(inline, builder);
        }

        return builder.ToString().Trim();
    }

    private static void AppendInline(Markdig.Syntax.Inlines.Inline inline, StringBuilder builder)
    {
        switch (inline)
        {
            case Markdig.Syntax.Inlines.LiteralInline literal:
                builder.Append(literal.Content.ToString());
                break;
            case Markdig.Syntax.Inlines.CodeInline code:
                builder.Append(code.Content);
                break;
            case Markdig.Syntax.Inlines.LineBreakInline:
                builder.Append(' ');
                break;
            case Markdig.Syntax.Inlines.ContainerInline container:
                foreach (var child in container)
                    AppendInline(child, builder);
                break;
        }
    }

    private static string ToSlug(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var ch in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch) || ch is '-' or '_')
                builder.Append(ch);
            else if (ch is ' ')
                builder.Append('-');
        }

        return builder.ToString();
    }
}
