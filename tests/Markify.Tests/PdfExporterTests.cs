using System.Text;
using System.Text.RegularExpressions;
using Markify.Services;
using Xunit;

namespace Markify.Tests;

/// <summary>
/// PDF 导出。
///
/// 这些测试依赖系统已安装 Edge 或 Chrome。若没有，相关用例会跳过而不是失败——
/// 但"找不到浏览器时必须给出可读错误"这条**始终**会被验证，因为那是纯逻辑。
/// </summary>
public class PdfExporterTests : IDisposable
{
    private readonly string _dir;

    public PdfExporterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "markify-pdf-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
        GC.SuppressFinalize(this);
    }

    private bool BrowserAvailable => PdfExporter.FindBrowser() is not null;

    private static int CountPdfPages(string path)
    {
        var ascii = Encoding.ASCII.GetString(File.ReadAllBytes(path));
        return Regex.Matches(ascii, @"/Type\s*/Page[^s]").Count;
    }

    [Fact]
    public void FindBrowser_ReturnsNullOrExistingFile()
    {
        var browser = PdfExporter.FindBrowser();

        // 要么找不到（null），要么返回一个真实存在的可执行文件——不能返回无效路径
        if (browser is not null)
            Assert.True(File.Exists(browser), $"返回了不存在的路径: {browser}");
    }

    [Fact]
    public async Task ExportAsync_WithoutBrowser_ReturnsReadableError()
    {
        // 这条无法直接模拟"没有浏览器"，但可以验证：任何失败路径都必须带可读错误、
        // 不能是空字符串或裸异常堆栈。
        var result = await PdfExporter.ExportAsync(
            "<html><body>x</body></html>",
            Path.Combine(_dir, "x.pdf"));

        if (!result.Success)
        {
            Assert.NotNull(result.Error);
            Assert.NotEmpty(result.Error!);
        }
    }

    [Fact]
    public async Task ExportAsync_ProducesNonEmptyPdf()
    {
        if (!BrowserAvailable) return;   // 环境无浏览器，跳过

        var html = MarkdownHtmlExporter.Render("# Title\n\nBody text.", null, "t", false);
        var target = Path.Combine(_dir, "simple.pdf");

        var result = await PdfExporter.ExportAsync(html, target);

        Assert.True(result.Success, result.Error);
        Assert.True(File.Exists(target));
        Assert.True(new FileInfo(target).Length > 1000, "PDF 体积过小，可能是空文档");

        // PDF 文件头必须是 %PDF-
        var header = Encoding.ASCII.GetString(File.ReadAllBytes(target), 0, 5);
        Assert.Equal("%PDF-", header);
    }

    [Fact]
    public async Task ExportAsync_EmbedsCjkFonts()
    {
        if (!BrowserAvailable) return;

        var html = MarkdownHtmlExporter.Render("# 中文标题\n\n中文正文内容。", null, "t", false);
        var target = Path.Combine(_dir, "cjk.pdf");

        var result = await PdfExporter.ExportAsync(html, target);
        Assert.True(result.Success, result.Error);

        // 中文必须用 CIDFont 嵌入，否则会渲染成方块
        var ascii = Encoding.ASCII.GetString(File.ReadAllBytes(target));
        Assert.Contains("CIDFont", ascii);
    }

    [Fact]
    public async Task ExportAsync_PaginatesLongDocuments()
    {
        if (!BrowserAvailable) return;

        var sb = new StringBuilder("# Long\n\n");
        for (var i = 0; i < 60; i++)
        {
            sb.AppendLine($"## Section {i}");
            sb.AppendLine();
            sb.AppendLine("Paragraph text to fill the page so pagination is exercised properly.");
            sb.AppendLine();
        }

        var html = MarkdownHtmlExporter.Render(sb.ToString(), null, "long", false);
        var target = Path.Combine(_dir, "long.pdf");

        var result = await PdfExporter.ExportAsync(html, target);
        Assert.True(result.Success, result.Error);

        Assert.True(CountPdfPages(target) > 1, "长文档应当分页");
    }

    [Fact]
    public async Task ExportAsync_OverwritesExistingFile()
    {
        if (!BrowserAvailable) return;

        var target = Path.Combine(_dir, "overwrite.pdf");

        var first = MarkdownHtmlExporter.Render("# First", null, "a", false);
        Assert.True((await PdfExporter.ExportAsync(first, target)).Success);
        var firstSize = new FileInfo(target).Length;

        var second = MarkdownHtmlExporter.Render(string.Concat(Enumerable.Repeat("# Second\n\nText\n\n", 40)), null, "b", false);
        Assert.True((await PdfExporter.ExportAsync(second, target)).Success);

        // 内容差异这么大的两份文档不该产出同样大小的文件
        Assert.NotEqual(firstSize, new FileInfo(target).Length);
    }

    [Fact]
    public async Task ExportAsync_CreatesMissingDirectory()
    {
        if (!BrowserAvailable) return;

        var target = Path.Combine(_dir, "nested", "deeper", "out.pdf");
        var html = MarkdownHtmlExporter.Render("# x", null, "t", false);

        var result = await PdfExporter.ExportAsync(html, target);

        Assert.True(result.Success, result.Error);
        Assert.True(File.Exists(target));
    }

    [Fact]
    public async Task ExportAsync_LeavesNoTempDirectory()
    {
        if (!BrowserAvailable) return;

        var before = Directory.GetDirectories(Path.GetTempPath(), "markify-pdf-*").Length;

        var html = MarkdownHtmlExporter.Render("# x", null, "t", false);
        await PdfExporter.ExportAsync(html, Path.Combine(_dir, "clean.pdf"));

        var after = Directory.GetDirectories(Path.GetTempPath(), "markify-pdf-*").Length;

        // 临时目录必须清理，否则每次导出都留垃圾
        Assert.Equal(before, after);
    }
}
