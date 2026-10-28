using System.Text;
using Markify.Services;
using Xunit;

namespace Markify.Tests;

/// <summary>
/// HTML 导出。重点覆盖两类容易静默出错的行为：
///   1. 相对路径图片是否真的内嵌（否则导出的文件换个目录就没图）
///   2. 远程/缺失图片是否被误改（不能把网络图片写死或静默删掉）
/// </summary>
public class MarkdownHtmlExporterTests : IDisposable
{
    private readonly string _dir;

    public MarkdownHtmlExporterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "markify-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
        GC.SuppressFinalize(this);
    }

    // 1x1 透明 PNG
    private static readonly byte[] TinyPng = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    [Fact]
    public void Render_ProducesCompleteHtmlDocument()
    {
        var html = MarkdownHtmlExporter.Render("# Hello", null, "My Doc", darkFirst: false);

        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.Contains("<title>My Doc</title>", html);
        Assert.Contains("<h1", html);
        Assert.Contains("Hello", html);
        Assert.Contains("</html>", html);
    }

    [Fact]
    public void Render_EscapesTitle()
    {
        var html = MarkdownHtmlExporter.Render("body", null, "a < b & \"c\"", darkFirst: false);

        Assert.Contains("&lt;", html);
        Assert.Contains("&amp;", html);
        Assert.DoesNotContain("<title>a < b", html);
    }

    [Fact]
    public void Render_EmitsInlineCss_SoFileWorksOffline()
    {
        var html = MarkdownHtmlExporter.Render("text", null, "t", darkFirst: false);

        // 不能有外部样式表引用，否则离线打开会掉样式
        Assert.DoesNotContain("<link", html);
        Assert.Contains("<style>", html);
    }

    [Fact]
    public void Render_RespectsDarkDefault()
    {
        var dark = MarkdownHtmlExporter.Render("t", null, "t", darkFirst: true);
        var light = MarkdownHtmlExporter.Render("t", null, "t", darkFirst: false);

        Assert.Contains("class=\"dark-default\"", dark);
        Assert.DoesNotContain("class=\"dark-default\"", light);
    }

    [Fact]
    public void Render_SupportsGfmTablesAndTaskLists_LikeThePreview()
    {
        var md = """
            | A | B |
            |---|---|
            | 1 | 2 |

            - [x] done
            - [ ] todo
            """;

        var html = MarkdownHtmlExporter.Render(md, null, "t", darkFirst: false);

        Assert.Contains("<table>", html);
        Assert.Contains("<th>", html);
        Assert.Contains("type=\"checkbox\"", html);
    }

    [Fact]
    public void Render_EmbedsLocalImageAsDataUri()
    {
        File.WriteAllBytes(Path.Combine(_dir, "pic.png"), TinyPng);
        var md = "![alt](pic.png)";

        var html = MarkdownHtmlExporter.Render(md, _dir, "t", darkFirst: false);

        Assert.Contains("src=\"data:image/png;base64,", html);
        Assert.DoesNotContain("src=\"pic.png\"", html);
    }

    [Fact]
    public void Render_KeepsRemoteImageUrlUntouched()
    {
        var md = "![alt](https://example.com/a.png)";

        var html = MarkdownHtmlExporter.Render(md, _dir, "t", darkFirst: false);

        // 远程图片不应被下载或改写
        Assert.Contains("https://example.com/a.png", html);
        Assert.DoesNotContain("data:image", html);
    }

    [Fact]
    public void Render_KeepsMissingLocalImagePath_InsteadOfDeleting()
    {
        var md = "![alt](missing.png)";

        var html = MarkdownHtmlExporter.Render(md, _dir, "t", darkFirst: false);

        // 找不到就保留原路径：静默删图比留个坏链接更糟
        Assert.Contains("missing.png", html);
    }

    [Fact]
    public void Render_EmbedsImageInSubdirectory()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "assets"));
        File.WriteAllBytes(Path.Combine(_dir, "assets", "logo.png"), TinyPng);

        var html = MarkdownHtmlExporter.Render("![l](assets/logo.png)", _dir, "t", darkFirst: false);

        Assert.Contains("data:image/png;base64,", html);
    }

    [Fact]
    public void Render_HandlesImagePathWithUrlEncoding()
    {
        // 文件名带空格时，Markdown 里常写作 %20
        File.WriteAllBytes(Path.Combine(_dir, "my pic.png"), TinyPng);

        var html = MarkdownHtmlExporter.Render("![l](my%20pic.png)", _dir, "t", darkFirst: false);

        Assert.Contains("data:image/png;base64,", html);
    }

    [Fact]
    public void Render_SvgImageGetsCorrectMimeType()
    {
        File.WriteAllText(Path.Combine(_dir, "icon.svg"), "<svg xmlns='http://www.w3.org/2000/svg'/>");

        var html = MarkdownHtmlExporter.Render("![l](icon.svg)", _dir, "t", darkFirst: false);

        Assert.Contains("data:image/svg+xml;base64,", html);
    }

    [Fact]
    public void Render_WithoutDocumentDirectory_DoesNotAttemptEmbedding()
    {
        var html = MarkdownHtmlExporter.Render("![l](pic.png)", null, "t", darkFirst: false);

        Assert.Contains("pic.png", html);
        Assert.DoesNotContain("data:image", html);
    }

    [Fact]
    public void Render_EmptyMarkdown_StillValidDocument()
    {
        var html = MarkdownHtmlExporter.Render("", null, "empty", darkFirst: false);

        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.Contains("</html>", html);
    }

    [Fact]
    public void Render_StripsYamlFrontMatter_LikeThePreview()
    {
        var md = """
            ---
            title: hidden
            tags: [a, b]
            ---

            # Visible
            """;

        var html = MarkdownHtmlExporter.Render(md, null, "t", darkFirst: false);

        Assert.DoesNotContain("tags:", html);
        Assert.Contains("Visible", html);
    }

    [Fact]
    public void Render_PreservesDetailsBlocks_WithMarkdownInside()
    {
        // 回归测试：Markdig 默认会整段丢弃 <details>（连标签带内容）。
        // 预览能折叠而导出后消失，是本项目实际踩过的坑。
        var md = """
            Before

            <details>
            <summary>Click me</summary>

            Inner **bold** text.

            </details>

            After
            """;

        var html = MarkdownHtmlExporter.Render(md, null, "t", darkFirst: false);

        Assert.Contains("<details>", html);
        Assert.Contains("<summary>", html);
        Assert.Contains("Click me", html);
        Assert.Contains("<strong>bold</strong>", html);   // 内部 Markdown 也被渲染
        Assert.Contains("After", html);
    }

    [Fact]
    public void Render_DetailsWithOpenAttribute_StaysOpen()
    {
        var md = """
            <details open>
            <summary>Expanded</summary>

            Body.

            </details>
            """;

        var html = MarkdownHtmlExporter.Render(md, null, "t", darkFirst: false);

        Assert.Contains("<details open>", html);
    }

    [Fact]
    public void Render_DoesNotLeakInternalPlaceholder()
    {
        // 占位符是内部实现细节，绝不能出现在输出里
        var md = """
            <details>
            <summary>S</summary>

            B

            </details>
            """;

        var html = MarkdownHtmlExporter.Render(md, null, "t", darkFirst: false);

        Assert.DoesNotContain("MARKIFY_DETAILS_PLACEHOLDER", html);
    }

    [Fact]
    public void Render_MultipleDetailsBlocks_AllRendered()
    {
        var md = """
            <details><summary>One</summary>

            First.

            </details>

            <details><summary>Two</summary>

            Second.

            </details>
            """;

        var html = MarkdownHtmlExporter.Render(md, null, "t", darkFirst: false);

        Assert.Contains("One", html);
        Assert.Contains("Two", html);
        Assert.Contains("First", html);
        Assert.Contains("Second", html);
    }
}
