using System.Text;
using Markify.Services;
using Xunit;

namespace Markify.Tests;

/// <summary>
/// 文件读写与编码保持。
/// 编码/BOM 处理错了会静默损坏用户文件，是最值得锁死的部分。
/// </summary>
public class DocumentFileServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly DocumentFileService _service = new();

    public DocumentFileServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "markify-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 清理失败不影响测试结论 */ }
        GC.SuppressFinalize(this);
    }

    private string PathFor(string name) => Path.Combine(_dir, name);

    [Fact]
    public async Task Read_Utf8WithoutBom_ReturnsPlainText()
    {
        var path = PathFor("plain.md");
        const string content = "# Title\n\n中文内容\n";
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(false), TestContext.Current.CancellationToken);

        var (text, encoding) = await _service.ReadAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(content, text);
        Assert.Empty(encoding.GetPreamble());   // 无 BOM
    }

    [Fact]
    public async Task Read_Utf8WithBom_DetectsAndStripsBom()
    {
        var path = PathFor("bom.md");
        const string content = "# BOM Test\n\n中文\n";
        await File.WriteAllTextAsync(path, content, new UTF8Encoding(true), TestContext.Current.CancellationToken);

        var (text, encoding) = await _service.ReadAsync(path, TestContext.Current.CancellationToken);

        // 文本里不能残留 U+FEFF
        Assert.Equal(content, text);
        Assert.DoesNotContain('\uFEFF', text);
        Assert.True(encoding.GetPreamble().Length > 0);    // 识别为带 BOM
    }

    [Fact]
    public async Task Read_Utf16Le_DetectsEncoding()
    {
        var path = PathFor("utf16.md");
        const string content = "# UTF-16\n\n中文内容\n";
        await File.WriteAllTextAsync(path, content, Encoding.Unicode, TestContext.Current.CancellationToken);

        var (text, encoding) = await _service.ReadAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(content, text);
        Assert.Equal(Encoding.Unicode.WebName, encoding.WebName);
    }

    [Fact]
    public async Task WriteThenRead_RoundTripsExactly_PreservingLineEndings()
    {
        var path = PathFor("roundtrip.md");
        // 混用 CRLF 与 LF，确认不会被归一化
        const string content = "line1\r\nline2\nline3\r\n";

        await _service.WriteAsync(path, content, new UTF8Encoding(false), TestContext.Current.CancellationToken);
        var (text, _) = await _service.ReadAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(content, text);
    }

    [Fact]
    public async Task Write_Utf8WithoutBom_DoesNotEmitBom()
    {
        var path = PathFor("nobom.md");
        await _service.WriteAsync(path, "abc", new UTF8Encoding(false), TestContext.Current.CancellationToken);

        var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);

        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "不应写入 BOM");
    }

    [Fact]
    public async Task Write_Utf8WithBom_EmitsBom()
    {
        var path = PathFor("withbom.md");
        await _service.WriteAsync(path, "abc", new UTF8Encoding(true), TestContext.Current.CancellationToken);

        var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);

        Assert.True(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
            "应写入 BOM");
    }

    [Fact]
    public async Task Write_OverwritesExistingFile_WithoutLeavingTempFile()
    {
        var path = PathFor("overwrite.md");
        await _service.WriteAsync(path, "first", new UTF8Encoding(false), TestContext.Current.CancellationToken);
        await _service.WriteAsync(path, "second", new UTF8Encoding(false), TestContext.Current.CancellationToken);

        var (text, _) = await _service.ReadAsync(path, TestContext.Current.CancellationToken);
        Assert.Equal("second", text);

        // 原子写入用的临时文件必须清理掉
        Assert.False(File.Exists(path + ".markify-tmp"), "临时文件应被清理");
    }

    [Fact]
    public async Task Write_CreatesMissingDirectory()
    {
        var path = Path.Combine(_dir, "nested", "deeper", "doc.md");

        await _service.WriteAsync(path, "content", new UTF8Encoding(false), TestContext.Current.CancellationToken);

        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Read_RoundTripsEmojiAndSurrogatePairs()
    {
        var path = PathFor("emoji.md");
        const string content = "# Emoji 🎉🚀\n\n组合字符：e\u0301\n";

        await _service.WriteAsync(path, content, new UTF8Encoding(false), TestContext.Current.CancellationToken);
        var (text, _) = await _service.ReadAsync(path, TestContext.Current.CancellationToken);

        Assert.Equal(content, text);
    }
}
