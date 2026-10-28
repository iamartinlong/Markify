using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Markify.Services;

/// <summary>PDF 导出结果：成功时带文件路径，失败时带可读原因。</summary>
public sealed record PdfExportResult(bool Success, string? Path, string? Error)
{
    public static PdfExportResult Ok(string path) => new(true, path, null);
    public static PdfExportResult Fail(string error) => new(false, null, error);
}

/// <summary>
/// 通过系统的 Chromium 内核浏览器（Edge / Chrome）以无头模式打印 PDF。
///
/// 为什么不用第三方 PDF 库：
///   - QuestPDF 有社区版体积与授权限制，且要把 Markdown 重新排版一遍，
///     等于维护第二套渲染器，和预览、HTML 导出三者的样式会逐渐分叉；
///   - Edge/Chrome 在 Windows 10+ 上是系统自带（Edge 尤其），
///     复用浏览器的排版引擎能保证 **PDF 与 HTML 导出、与预览的观感一致**。
///
/// 已知前提与限制（都写进 README）：
///   - 需要 Edge 或 Chrome；都没有时返回明确错误，而不是静默失败；
///   - 无头打印不执行我们无法控制的外部请求（图片已在 HTML 阶段内嵌，正好契合）；
///   - 页边距、纸张由浏览器的 print CSS 决定，应用内不提供参数。
/// </summary>
public static class PdfExporter
{
    /// <summary>候选浏览器路径，按优先级排列（Edge 在 Windows 上最普遍）。</summary>
    private static IEnumerable<string> BrowserCandidates()
    {
        var pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        yield return Path.Combine(pf86, @"Microsoft\Edge\Application\msedge.exe");
        yield return Path.Combine(pf, @"Microsoft\Edge\Application\msedge.exe");
        yield return Path.Combine(pf, @"Google\Chrome\Application\chrome.exe");
        yield return Path.Combine(pf86, @"Google\Chrome\Application\chrome.exe");
        yield return Path.Combine(local, @"Google\Chrome\Application\chrome.exe");
    }

    /// <summary>找到可用的 Chromium 内核浏览器；找不到返回 null。</summary>
    public static string? FindBrowser()
    {
        foreach (var candidate in BrowserCandidates())
        {
            try
            {
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
                // 路径不可访问就继续找下一个
            }
        }

        return null;
    }

    /// <summary>
    /// 把 HTML 打印为 PDF。
    /// </summary>
    /// <param name="html">完整 HTML 文档（应已内联样式与图片）。</param>
    /// <param name="targetPdfPath">输出 PDF 路径。</param>
    /// <param name="timeout">超时；默认 60 秒。</param>
    public static async Task<PdfExportResult> ExportAsync(
        string html, string targetPdfPath, TimeSpan? timeout = null)
    {
        var browser = FindBrowser();
        if (browser is null)
        {
            return PdfExportResult.Fail(
                "未找到 Microsoft Edge 或 Google Chrome。\n\n" +
                "PDF 导出依赖系统浏览器的排版引擎。请安装 Edge（Windows 10/11 自带）后重试，" +
                "或改用「导出为 HTML」再用浏览器打印。");
        }

        var workDir = Path.Combine(Path.GetTempPath(), "markify-pdf-" + Guid.NewGuid().ToString("N"));
        var htmlPath = Path.Combine(workDir, "document.html");

        try
        {
            Directory.CreateDirectory(workDir);
            // 必须保留 UTF-8 BOM 吗？不需要——HTML 里有 <meta charset>，但为了浏览器
            // 在读取本地文件时万无一失，写 BOM 更稳。
            await File.WriteAllTextAsync(htmlPath, html, new UTF8Encoding(true));

            var pdfDir = Path.GetDirectoryName(Path.GetFullPath(targetPdfPath));
            if (!string.IsNullOrEmpty(pdfDir))
                Directory.CreateDirectory(pdfDir);

            // 先删旧文件，否则浏览器可能因为文件已存在而不覆盖，导致拿到上一次的结果。
            if (File.Exists(targetPdfPath))
                File.Delete(targetPdfPath);

            // --headless=new 是当前推荐的写法；旧版 --headless 在新版本里仍可用但不保证长期有效。
            // 注意不要加 --no-sandbox：以普通用户权限运行时它没有收益，
            // 却会关闭渲染进程的沙箱，属于白白削弱安全性。
            var args = string.Join(' ',
                "--headless=new",
                "--disable-gpu",
                "--no-first-run",
                "--no-default-browser-check",
                "--disable-extensions",
                "--disable-background-networking",
                "--run-all-compositor-stages-before-draw",
                "--virtual-time-budget=3000",                 // 给字体/布局留时间
                $"--print-to-pdf=\"{targetPdfPath}\"",
                "--no-pdf-header-footer",
                $"\"{htmlPath}\"");

            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = browser,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    WorkingDirectory = workDir,
                },
            };

            process.Start();

            var limit = timeout ?? TimeSpan.FromSeconds(60);
            using var cts = new CancellationTokenSource(limit);

            // 必须持续读走输出，否则子进程可能因管道写满而卡死（进程被隐藏时尤其隐蔽）。
            // 两个流都要处理：只读 stderr 而不管 stdout，一旦 stdout 写满同样会挂住。
            var stderrTask = process.StandardError.ReadToEndAsync(cts.Token);
            var stdoutTask = process.StandardOutput.ReadToEndAsync(cts.Token);

            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                return PdfExportResult.Fail($"PDF 生成超时（{limit.TotalSeconds:0} 秒）。");
            }

            // 进程已退出，确保两个读取任务都完成（避免留下未观察的异常/未释放的流）。
            await SafeReadAsync(stdoutTask);
            // 写文件有轻微延迟，给一小段时间并轮询，避免"进程已退出但文件还没落盘"。
            var produced = await WaitForFileAsync(targetPdfPath, TimeSpan.FromSeconds(5));

            if (!produced)
            {
                var stderr = await SafeReadAsync(stderrTask);
                var detail = string.IsNullOrWhiteSpace(stderr)
                    ? $"退出码 {process.ExitCode}，未生成 PDF 文件。"
                    : stderr.Trim();

                // 太长会撑爆对话框，截断但保留开头（错误原因通常在最前面）。
                if (detail.Length > 600)
                    detail = detail[..600] + "…";

                return PdfExportResult.Fail(detail);
            }

            return PdfExportResult.Ok(targetPdfPath);
        }
        catch (Exception ex)
        {
            return PdfExportResult.Fail(ex.Message);
        }
        finally
        {
            try
            {
                if (Directory.Exists(workDir))
                    Directory.Delete(workDir, recursive: true);
            }
            catch
            {
                // 临时目录清理失败不影响导出结果
            }
        }
    }

    private static async Task<bool> WaitForFileAsync(string path, TimeSpan maxWait)
    {
        var deadline = DateTime.UtcNow + maxWait;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                if (File.Exists(path) && new FileInfo(path).Length > 0)
                    return true;
            }
            catch
            {
                // 文件正被写入时可能抛异常，继续等
            }

            await Task.Delay(100);
        }

        return false;
    }

    private static async Task<string> SafeReadAsync(Task<string> task)
    {
        try
        {
            // 进程已退出，这个读取会很快完成；加个上限避免极端情况挂住。
            var completed = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(2)));
            return completed == task ? await task : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // 进程可能已经退出
        }
    }
}
