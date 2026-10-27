using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Markify.Services;

/// <summary>
/// 极简的文件读写服务：负责编码探测、BOM 保留与原子写入。
/// </summary>
public sealed class DocumentFileService
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    public async Task<(string Text, Encoding Encoding)> ReadAsync(string path, CancellationToken ct = default)
    {
        var bytes = await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false);

        if (bytes.Length >= 3 && bytes[0] == Utf8Bom[0] && bytes[1] == Utf8Bom[1] && bytes[2] == Utf8Bom[2])
            return (Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), new UTF8Encoding(true));

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), Encoding.Unicode);

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return (Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), Encoding.BigEndianUnicode);

        return (new UTF8Encoding(false).GetString(bytes), new UTF8Encoding(false));
    }

    /// <summary>先写临时文件再替换，避免写入中断导致文档损坏。</summary>
    public async Task WriteAsync(string path, string text, Encoding encoding, CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var temp = path + ".markify-tmp";
        await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
        await using (var writer = new StreamWriter(stream, encoding))
        {
            await writer.WriteAsync(text.AsMemory(), ct).ConfigureAwait(false);
        }

        if (File.Exists(path))
            File.Replace(temp, path, null);
        else
            File.Move(temp, path);
    }
}
