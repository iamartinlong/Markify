using Markify.ViewModels;
using Xunit;

namespace Markify.Tests;

/// <summary>
/// 字数统计。中文按字计、拉丁按词计，这是中文用户的常见预期；
/// 之前的实现里 CJK 分支容易漏掉某些区段，这里把边界固定下来。
/// </summary>
public class WordCountTests
{
    [Theory]
    [InlineData("", 0)]
    [InlineData("   ", 0)]
    [InlineData("hello", 1)]
    [InlineData("hello world", 2)]
    [InlineData("hello, world!", 2)]           // 标点分隔
    [InlineData("don't", 1)]                   // 撇号在词内
    [InlineData("snake_case", 1)]              // 下划线在词内
    [InlineData("a1b2", 1)]                    // 数字与字母连续算一个词
    public void CountWords_LatinText_SplitsOnSeparators(string text, int expected)
    {
        Assert.Equal(expected, DocumentViewModel.CountWords(text));
    }

    [Theory]
    [InlineData("你好", 2)]                      // 中文每个字算一个词
    [InlineData("你好世界", 4)]
    [InlineData("你好 world", 3)]                // 2 个汉字 + 1 个英文词
    [InlineData("日本語", 3)]                    // 平假名/汉字
    [InlineData("こんにちは", 5)]                 // 平假名
    [InlineData("カタカナ", 4)]                   // 片假名
    [InlineData("한국어", 3)]                    // 谚文
    public void CountWords_CjkText_CountsPerCharacter(string text, int expected)
    {
        Assert.Equal(expected, DocumentViewModel.CountWords(text));
    }

    [Fact]
    public void CountWords_MixedScripts_CombinesBothRules()
    {
        // "Hello 世界" => 1 英文词 + 2 汉字
        Assert.Equal(3, DocumentViewModel.CountWords("Hello 世界"));
    }

    [Fact]
    public void CountWords_MarkdownSyntax_CountsMarkersAsSeparators()
    {
        // "# Title" 的 # 不是词，Title 是 1 个词
        Assert.Equal(1, DocumentViewModel.CountWords("# Title"));
    }

    [Fact]
    public void CountWords_ConsecutiveCjkAndLatin_DoesNotMerge()
    {
        // 汉字与英文相邻时应有明确边界：2 个汉字 + 1 个词
        Assert.Equal(3, DocumentViewModel.CountWords("中文abc"));
    }
}
