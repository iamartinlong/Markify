using Markify.Services;
using Xunit;

namespace Markify.Tests;

/// <summary>
/// 大纲解析与锚点生成。
/// 这部分的正确性直接影响「点击大纲能否跳到预览正确位置」——锚点规则与 GitHub 不一致就会跳错。
/// </summary>
public class MarkdownOutlineTests
{
    [Fact]
    public void Parse_ReturnsEmpty_ForNullOrWhitespace()
    {
        Assert.Empty(MarkdownOutline.Parse(null));
        Assert.Empty(MarkdownOutline.Parse(""));
        Assert.Empty(MarkdownOutline.Parse("   \n\n  "));
    }

    [Fact]
    public void Parse_ExtractsLevelAndText()
    {
        var headings = MarkdownOutline.Parse("# One\n\n## Two\n\n### Three\n");

        Assert.Equal(3, headings.Count);
        Assert.Equal((1, "One"), (headings[0].Level, headings[0].Text));
        Assert.Equal((2, "Two"), (headings[1].Level, headings[1].Text));
        Assert.Equal((3, "Three"), (headings[2].Level, headings[2].Text));
    }

    [Fact]
    public void Parse_ReportsOneBasedLineNumbers()
    {
        // 源码第 3 行是标题，Line 必须是 3 而不是 2（内部是 0 基）。
        var headings = MarkdownOutline.Parse("intro\n\n## Heading\n");

        Assert.Single(headings);
        Assert.Equal(3, headings[0].Line);
    }

    [Fact]
    public void Parse_StripsInlineFormatting_FromHeadingText()
    {
        var headings = MarkdownOutline.Parse("## Hello **bold** and `code`\n");

        Assert.Single(headings);
        // 大纲显示的是纯文本，不应带 ** 或反引号
        Assert.DoesNotContain("**", headings[0].Text);
        Assert.DoesNotContain("`", headings[0].Text);
        Assert.Contains("bold", headings[0].Text);
        Assert.Contains("code", headings[0].Text);
    }

    [Theory]
    [InlineData("# Hello World", "hello-world")]
    [InlineData("# Hello   World", "hello---world")]          // GitHub 对连续空格保留多个连字符
    [InlineData("## Installation", "installation")]
    [InlineData("# C# Guide", "c-guide")]                      // 标点移除
    [InlineData("# 中文标题", "中文标题")]                       // CJK 保留
    [InlineData("# A_B-C", "a_b-c")]                           // 下划线与连字符保留
    [InlineData("# 100% Done!", "100-done")]                   // % 与 ! 移除，空格转连字符
    public void Parse_GeneratesGitHubCompatibleSlugs(string markdown, string expectedSlug)
    {
        var headings = MarkdownOutline.Parse(markdown);

        Assert.Single(headings);
        Assert.Equal(expectedSlug, headings[0].Slug);
    }

    [Fact]
    public void Parse_DisambiguatesDuplicateHeadings_WithNumericSuffix()
    {
        var headings = MarkdownOutline.Parse("# Notes\n\n# Notes\n\n# Notes\n");

        Assert.Equal(3, headings.Count);
        Assert.Equal("notes", headings[0].Slug);
        Assert.Equal("notes-1", headings[1].Slug);
        Assert.Equal("notes-2", headings[2].Slug);
    }

    [Fact]
    public void Parse_IgnoresHeadingsInsideFencedCodeBlocks()
    {
        // 代码块里的 "# 不是标题" 不应出现在大纲里
        var markdown = """
            # Real Heading

            ```markdown
            # Not A Heading
            ```

            ## Another Real
            """;

        var headings = MarkdownOutline.Parse(markdown);

        Assert.Equal(2, headings.Count);
        Assert.Equal("Real Heading", headings[0].Text);
        Assert.Equal("Another Real", headings[1].Text);
    }

    [Fact]
    public void Parse_PreservesDocumentOrder_AcrossLevels()
    {
        var headings = MarkdownOutline.Parse("# A\n\n### C\n\n## B\n");

        Assert.Equal(["A", "C", "B"], headings.Select(h => h.Text));
        Assert.Equal([1, 3, 2], headings.Select(h => h.Level));
    }
}
