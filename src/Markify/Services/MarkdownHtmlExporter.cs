using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using MarkView.Avalonia;

namespace Markify.Services;

/// <summary>
/// 把 Markdown 导出为独立 HTML 文件。
///
/// 三个关键点，都是实测踩出来的：
///
/// 1. **管线与预览对齐**。预览用 MarkView 的默认管线（GitHub 扩展 + 表格/任务列表/脚注/alert）。
///    这里用同一套 GitHub 扩展，避免"预览有表格、导出的没有"。
///
/// 2. **`&lt;details&gt;` 必须自己处理**。MarkView 的 `UseHtmlDetails()` 会在 AST 里生成
///    自定义的 `DetailsBlock` 节点，但 Markdig 的 HTML 渲染器不认识它——`Markdown.ToHtml()`
///    会把整段（连标签带内容）**静默丢弃**。实测：预览能折叠，导出的 HTML 里那块直接消失。
///    因此这里先把 details 段落抽取成占位符，交给 Markdig 解析其余内容，最后再还原。
///
/// 3. **本地图片内嵌为 data URI**。导出的 HTML 常被单独分发或归档，相对路径会失效。
///    远程图片保留 URL（不下载、不改语义）；找不到的本地图片保留原路径（静默删图更糟）。
///
/// 已知取舍：不做代码高亮。预览用 TextMate 着色，导出若引入 Highlight.js 需要 CDN（离线失效）
/// 或内嵌约 100KB 脚本，当前选择朴素等宽样式。
/// </summary>
public static partial class MarkdownHtmlExporter
{
    /// <summary>内嵌图片的体积上限；超过则保留原路径，避免生成几十 MB 的 HTML。</summary>
    private const long MaxEmbeddedImageBytes = 8 * 1024 * 1024;

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseGitHubExtensions()      // 表格、任务列表、自动链接、脚注、删除线
        .UseEmphasisExtras()        // ^上标^、++下划线++、==高亮==
        .UseYamlFrontMatter()       // 隐藏 front matter，与预览一致
        .UseHtmlSubset()            // 内联/块级 HTML 子集（b/i/kbd/img 等）
        .Build();

    /// <summary>匹配 details 块。非贪婪匹配到最近的 &lt;/details&gt;，不支持嵌套（已在文档注明）。</summary>
    [GeneratedRegex(@"<details\b[^>]*>.*?</details\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex DetailsBlockRegex();

    [GeneratedRegex(@"<summary\b[^>]*>(.*?)</summary\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex SummaryRegex();

    [GeneratedRegex("""(<img\b[^>]*?\bsrc\s*=\s*")([^"]+)(")""", RegexOptions.IgnoreCase)]
    private static partial Regex ImgSrcRegex();

    /// <summary>渲染为完整 HTML 文档。</summary>
    /// <param name="markdown">Markdown 源码。</param>
    /// <param name="documentDirectory">当前文档所在目录，用于解析相对图片路径；可为 null。</param>
    /// <param name="title">文档标题。</param>
    /// <param name="darkFirst">是否以深色为默认配色（跟随应用主题）。</param>
    public static string Render(string markdown, string? documentDirectory, string title, bool darkFirst)
    {
        var source = markdown ?? string.Empty;

        // 先把 details 段抽出来，Markdig 会把它们丢掉（见类注释第 2 点）。
        var details = new List<string>();
        source = DetailsBlockRegex().Replace(source, match =>
        {
            details.Add(match.Value);
            // 用纯文本占位：不含 HTML 标签，避免被 Markdig 的 HTML 块规则二次吞掉。
            return $"\n\nMARKIFY_DETAILS_PLACEHOLDER_{details.Count - 1}\n\n";
        });

        var body = Markdown.ToHtml(source, Pipeline);

        // 还原 details：标题走 HTML 转义，正文递归按 Markdown 渲染（与预览一致）。
        for (var i = 0; i < details.Count; i++)
        {
            var rendered = RenderDetails(details[i], documentDirectory);
            body = body.Replace(
                $"<p>MARKIFY_DETAILS_PLACEHOLDER_{i}</p>",
                rendered,
                StringComparison.Ordinal);
            body = body.Replace(
                $"MARKIFY_DETAILS_PLACEHOLDER_{i}",
                rendered,
                StringComparison.Ordinal);
        }

        body = EmbedLocalImages(body, documentDirectory);

        var sb = new StringBuilder(body.Length + Css.Length + 512);
        sb.Append("<!DOCTYPE html>\n");
        sb.Append("<html lang=\"zh-CN\">\n<head>\n");
        sb.Append("<meta charset=\"utf-8\">\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        sb.Append("<meta name=\"generator\" content=\"Markify\">\n");
        sb.Append("<title>").Append(WebUtility.HtmlEncode(title)).Append("</title>\n");
        sb.Append("<style>\n").Append(Css).Append("</style>\n");
        sb.Append("</head>\n");

        // 默认配色跟随应用主题；同时保留 prefers-color-scheme 自动切换。
        sb.Append("<body").Append(darkFirst ? " class=\"dark-default\"" : string.Empty).Append(">\n");
        sb.Append("<main class=\"markdown-body\">\n");
        sb.Append(body);
        sb.Append("\n</main>\n</body>\n</html>\n");

        return sb.ToString();
    }

    /// <summary>
    /// 把一段 &lt;details&gt;…&lt;/details&gt; 转成 HTML：
    /// summary 走内联 Markdown（允许 **粗体**），正文按块级 Markdown 渲染。
    /// </summary>
    private static string RenderDetails(string detailsHtml, string? documentDirectory)
    {
        var isOpen = Regex.IsMatch(detailsHtml, @"<details\b[^>]*\bopen\b", RegexOptions.IgnoreCase);

        var summaryMatch = SummaryRegex().Match(detailsHtml);
        var summaryInner = summaryMatch.Success ? summaryMatch.Groups[1].Value.Trim() : "Details";

        // summary 只取内联内容，去掉包裹的 <p>
        var summaryHtml = Markdown.ToHtml(summaryInner, Pipeline).Trim();
        if (summaryHtml.StartsWith("<p>", StringComparison.Ordinal) && summaryHtml.EndsWith("</p>", StringComparison.Ordinal))
            summaryHtml = summaryHtml[3..^4];

        var bodyStart = summaryMatch.Success ? summaryMatch.Index + summaryMatch.Length : "<details>".Length;
        var bodyEnd = detailsHtml.LastIndexOf("</details", StringComparison.OrdinalIgnoreCase);
        if (bodyEnd < bodyStart)
            bodyEnd = detailsHtml.Length;

        var bodyMarkdown = detailsHtml[bodyStart..bodyEnd].Trim();
        var bodyHtml = string.IsNullOrWhiteSpace(bodyMarkdown)
            ? string.Empty
            : Markdown.ToHtml(bodyMarkdown, Pipeline);

        var sb = new StringBuilder();
        sb.Append("<details").Append(isOpen ? " open" : string.Empty).Append(">\n");
        sb.Append("<summary>").Append(summaryHtml).Append("</summary>\n");
        if (!string.IsNullOrEmpty(bodyHtml))
            sb.Append(bodyHtml).Append('\n');
        sb.Append("</details>");

        return sb.ToString();
    }

    /// <summary>把本地图片转成 data URI。远程 URL、data URI、找不到的文件保持原样。</summary>
    private static string EmbedLocalImages(string html, string? documentDirectory)
    {
        if (string.IsNullOrEmpty(documentDirectory))
            return html;

        var cache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        return ImgSrcRegex().Replace(html, match =>
        {
            var src = WebUtility.HtmlDecode(match.Groups[2].Value);

            if (IsExternal(src))
                return match.Value;

            if (!cache.TryGetValue(src, out var dataUri))
            {
                dataUri = TryReadAsDataUri(src, documentDirectory);
                cache[src] = dataUri;
            }

            return dataUri is null
                ? match.Value
                : match.Groups[1].Value + dataUri + match.Groups[3].Value;
        });
    }

    private static bool IsExternal(string src) =>
        src.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || src.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || src.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
        || src.StartsWith("//", StringComparison.Ordinal);

    private static string? TryReadAsDataUri(string src, string documentDirectory)
    {
        try
        {
            var pathPart = src.Split('#', '?')[0];
            if (string.IsNullOrWhiteSpace(pathPart))
                return null;

            pathPart = Uri.UnescapeDataString(pathPart).Replace('/', Path.DirectorySeparatorChar);
            var full = Path.IsPathRooted(pathPart)
                ? pathPart
                : Path.Combine(documentDirectory, pathPart);

            if (!File.Exists(full))
                return null;

            if (new FileInfo(full).Length > MaxEmbeddedImageBytes)
                return null;

            var mime = MimeFromExtension(Path.GetExtension(full));
            if (mime is null)
                return null;

            return $"data:{mime};base64,{Convert.ToBase64String(File.ReadAllBytes(full))}";
        }
        catch
        {
            return null;
        }
    }

    private static string? MimeFromExtension(string extension) => extension.ToLowerInvariant() switch
    {
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".svg" => "image/svg+xml",
        ".bmp" => "image/bmp",
        ".avif" => "image/avif",
        _ => null,
    };

    /// <summary>
    /// 页面样式。刻意内联而非外链：导出的文件应当能离线打开、单独分发。
    /// 版式参考 GitHub 的阅读体验，但不追求像素级复刻。
    /// </summary>
    private const string Css = """
        :root {
          color-scheme: light dark;
          --fg: #1f2328;
          --bg: #ffffff;
          --muted: #59636e;
          --border: #d1d9e0;
          --code-bg: #f6f8fa;
          --link: #0969da;
          --quote-border: #d1d9e0;
          --table-head: #f6f8fa;
        }
        @media (prefers-color-scheme: dark) {
          :root {
            --fg: #e6edf3;
            --bg: #0d1117;
            --muted: #9198a1;
            --border: #3d444d;
            --code-bg: #151b23;
            --link: #4493f8;
            --quote-border: #3d444d;
            --table-head: #151b23;
          }
        }
        body.dark-default {
          --fg: #e6edf3;
          --bg: #0d1117;
          --muted: #9198a1;
          --border: #3d444d;
          --code-bg: #151b23;
          --link: #4493f8;
          --quote-border: #3d444d;
          --table-head: #151b23;
          color-scheme: dark;
        }

        * { box-sizing: border-box; }

        body {
          margin: 0;
          padding: 32px 16px 96px;
          background: var(--bg);
          color: var(--fg);
          font-family: -apple-system, BlinkMacSystemFont, "Segoe UI", "Noto Sans",
                       "Microsoft YaHei", "PingFang SC", Helvetica, Arial, sans-serif;
          font-size: 16px;
          line-height: 1.7;
          -webkit-text-size-adjust: 100%;
        }

        .markdown-body {
          max-width: 860px;
          margin: 0 auto;
          overflow-wrap: break-word;
        }

        h1, h2, h3, h4, h5, h6 {
          margin: 1.6em 0 0.6em;
          line-height: 1.3;
          font-weight: 600;
        }
        h1 { font-size: 2em; padding-bottom: 0.3em; border-bottom: 1px solid var(--border); }
        h2 { font-size: 1.5em; padding-bottom: 0.3em; border-bottom: 1px solid var(--border); }
        h3 { font-size: 1.25em; }
        h4 { font-size: 1em; }
        h5 { font-size: 0.875em; }
        h6 { font-size: 0.85em; color: var(--muted); }
        h1:first-child, h2:first-child { margin-top: 0; }

        p { margin: 0 0 1em; }

        a { color: var(--link); text-decoration: none; }
        a:hover { text-decoration: underline; }

        ul, ol { margin: 0 0 1em; padding-left: 2em; }
        li + li { margin-top: 0.25em; }
        li > ul, li > ol { margin: 0.25em 0 0; }

        li:has(> input[type="checkbox"]) { list-style: none; margin-left: -1.4em; }
        li > input[type="checkbox"] { margin-right: 0.5em; vertical-align: middle; }

        blockquote {
          margin: 0 0 1em;
          padding: 0 1em;
          color: var(--muted);
          border-left: 0.25em solid var(--quote-border);
        }

        code {
          font-family: ui-monospace, SFMono-Regular, "Cascadia Mono", Consolas,
                       "Liberation Mono", Menlo, monospace;
          font-size: 0.875em;
          background: var(--code-bg);
          padding: 0.2em 0.4em;
          border-radius: 4px;
        }

        pre {
          margin: 0 0 1em;
          padding: 16px;
          overflow: auto;
          background: var(--code-bg);
          border-radius: 6px;
          line-height: 1.5;
        }
        pre code {
          padding: 0;
          background: none;
          font-size: 0.85em;
          white-space: pre;
        }

        table {
          margin: 0 0 1em;
          border-collapse: collapse;
          display: block;
          width: max-content;
          max-width: 100%;
          overflow: auto;
        }
        th, td {
          padding: 6px 13px;
          border: 1px solid var(--border);
        }
        th { background: var(--table-head); font-weight: 600; }

        hr {
          height: 1px;
          margin: 2em 0;
          border: 0;
          background: var(--border);
        }

        img { max-width: 100%; height: auto; }

        /* alert 块：Markdig 输出为 blockquote，配合 class 上色 */
        .markdown-alert {
          margin: 0 0 1em;
          padding: 8px 16px;
          border-left: 0.25em solid var(--border);
        }
        .markdown-alert-title { font-weight: 600; margin: 0 0 0.5em; }
        .markdown-alert-note { border-left-color: #0969da; }
        .markdown-alert-tip { border-left-color: #1a7f37; }
        .markdown-alert-important { border-left-color: #8250df; }
        .markdown-alert-warning { border-left-color: #9a6700; }
        .markdown-alert-caution { border-left-color: #d1242f; }

        details {
          margin: 0 0 1em;
          padding: 0.4em 0.8em;
          border: 1px solid var(--border);
          border-radius: 6px;
        }
        summary { cursor: pointer; font-weight: 600; }
        details[open] summary { margin-bottom: 0.6em; }

        kbd {
          display: inline-block;
          padding: 0.1em 0.4em;
          font-family: ui-monospace, SFMono-Regular, Consolas, monospace;
          font-size: 0.85em;
          border: 1px solid var(--border);
          border-bottom-width: 2px;
          border-radius: 4px;
          background: var(--code-bg);
        }

        mark { background: #fff8c5; color: inherit; }
        body.dark-default mark { background: #6b5900; }

        @media print {
          body { padding: 0; }
          .markdown-body { max-width: none; }
          pre, table { page-break-inside: avoid; }
        }
        """;
}
