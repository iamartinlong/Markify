using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using Markify.Services;
using Markify.Views;

namespace Markify;

/// <summary>
/// AOT 冒烟自检：在真实原生二进制里跑一遍「最容易因裁剪/反射而失效」的路径。
/// 由 CI 通过 <c>--selftest --selftest-output &lt;file&gt;</c> 触发，结果写入文件并作为退出码返回。
///
/// 之所以要自检而不是只看编译是否成功：AOT 下编译通过但运行时因反射/泛型实例化缺失而崩溃
/// 是常态，XAML 加载与 MarkView 的控件构建都属于这类高风险路径。
/// </summary>
public static class SelfTest
{
    private const string Flag = "--selftest";
    private const string OutputFlag = "--selftest-output";

    public static bool Requested { get; private set; }

    private static string? _outputPath;

    /// <summary>读取命令行参数；必须在 Avalonia 初始化之前调用。</summary>
    public static void Parse(string[] args)
    {
        Requested = args.Any(a => string.Equals(a, Flag, StringComparison.OrdinalIgnoreCase));

        var index = Array.FindIndex(args, a => string.Equals(a, OutputFlag, StringComparison.OrdinalIgnoreCase));
        if (index >= 0 && index + 1 < args.Length)
            _outputPath = args[index + 1];

        // CI 诊断：先落一行到文件，确认参数确实传进来了（WinExe 下看不到控制台）。
        if (Requested)
            Write(_outputPath, $"selftest requested\nargs: {string.Join(' ', args)}\noutput: {_outputPath}\n");
    }

    private static void Write(string? path, string text)
    {
        if (string.IsNullOrEmpty(path))
            return;

        try
        {
            var full = Path.GetFullPath(path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.AppendAllText(full, text);
        }
        catch
        {
            // 诊断写入失败不应影响主流程。
        }
    }

    /// <summary>挂接窗口：等界面真正渲染出内容后再断言，而不是在构造函数里抢先检查。</summary>
    public static void Attach(MainWindow window, IClassicDesktopStyleApplicationLifetime desktop)
    {
        window.Opened += (_, _) =>
        {
            Write(_outputPath, "window opened\n");
            // 让布局、懒渲染和首帧绘制都跑完再验证。
            DispatcherTimer.RunOnce(
                () => _ = CompleteAsync(window, desktop),
                TimeSpan.FromSeconds(2));
        };
    }

    private static async Task CompleteAsync(MainWindow window, IClassicDesktopStyleApplicationLifetime desktop)
    {
        var results = new List<(string Name, bool Ok, string Detail)>();

        try
        {
            Write(_outputPath, "complete: begin\n");

            void Check(string name, Func<string> probe)
            {
                try
                {
                    Write(_outputPath, $"check: {name} ...\n");
                    var detail = probe();
                    results.Add((name, true, detail));
                }
                catch (Exception ex)
                {
                    results.Add((name, false, $"{ex.GetType().Name}: {ex.Message}"));
                }
            }

        Check("xaml-app-resources", () =>
        {
            var app = Avalonia.Application.Current ?? throw new InvalidOperationException("Application.Current is null");
            var hasTheme = app.Styles.Count > 0;
            return $"styles={app.Styles.Count}, themeVariant={app.ActualThemeVariant}";
        });

        Check("editor-loaded", () =>
        {
            var editor = window.FindControl<TextEditor>("Editor") ?? throw new InvalidOperationException("Editor not found");
            if (editor.Document is null) throw new InvalidOperationException("Document is null");
            if (editor.Text.Length == 0) throw new InvalidOperationException("Editor text is empty");
            return $"chars={editor.Text.Length}, lines={editor.LineCount}";
        });

        Check("textmate-grammar", () =>
        {
            var editor = window.FindControl<TextEditor>("Editor") ?? throw new InvalidOperationException("Editor not found");
            var transformers = editor.TextArea.TextView.LineTransformers.Count;
            if (transformers == 0) throw new InvalidOperationException("No line transformers installed");
            return $"lineTransformers={transformers}";
        });

        Check("markdig-parse", () =>
        {
            var headings = MarkdownOutline.Parse("# A\n\n## B\n\ntext **bold**\n\n```csharp\nvar x = 1;\n```\n");
            if (headings.Count != 2) throw new InvalidOperationException($"expected 2 headings, got {headings.Count}");
            if (headings[1].Slug != "b") throw new InvalidOperationException($"unexpected slug '{headings[1].Slug}'");
            return $"headings={headings.Count}, slugs={string.Join(',', headings.Select(h => h.Slug))}";
        });

        // MarkView 会把 Markdown 转成大量原生控件；这一步能暴露 XAML 样式查找、TextBlock 构建等反射路径。
        Check("markview-render", () =>
        {
            var preview = window.FindControl<MarkView.Avalonia.MarkdownViewer>("Preview")
                          ?? throw new InvalidOperationException("Preview not found");

            var descendants = preview.GetVisualDescendants().ToList();
            var textBlocks = descendants.OfType<TextBlock>().Count();

            var histogram = string.Join(", ",
                descendants.GroupBy(d => d.GetType().Name)
                           .OrderByDescending(g => g.Count())
                           .Take(8)
                           .Select(g => $"{g.Key}={g.Count()}"));

            Write(_outputPath, $"  markdown={preview.Markdown?.Length ?? -1} chars; types: {histogram}\n");

            if (descendants.Count < 20) throw new InvalidOperationException($"only {descendants.Count} visual descendants; mdLen={preview.Markdown?.Length ?? -1}; types: {histogram}");
            if (textBlocks < 3) throw new InvalidOperationException($"only {textBlocks} TextBlocks; mdLen={preview.Markdown?.Length ?? -1}; types: {histogram}");

            return $"visualDescendants={descendants.Count}, textBlocks={textBlocks}";
        });

        Check("preview-scrollviewer", () =>
        {
            var preview = window.FindControl<MarkView.Avalonia.MarkdownViewer>("Preview")
                          ?? throw new InvalidOperationException("Preview not found");
            var scroll = preview.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault()
                         ?? throw new InvalidOperationException("PART_ScrollViewer not found (theme not applied)");
            return $"extent={scroll.Extent.Height:F0}, viewport={scroll.Viewport.Height:F0}";
        });

        Check("theme-switch", () =>
        {
            var app = Avalonia.Application.Current ?? throw new InvalidOperationException("Application.Current is null");
            var original = app.RequestedThemeVariant;
            app.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Dark;
            var dark = app.ActualThemeVariant;
            app.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Light;
            var light = app.ActualThemeVariant;
            app.RequestedThemeVariant = original;
            if (!string.Equals(dark.Key?.ToString(), "Dark", StringComparison.Ordinal)
                || !string.Equals(light.Key?.ToString(), "Light", StringComparison.Ordinal))
                throw new InvalidOperationException($"theme variant did not switch: {dark}/{light}");
            return $"dark={dark}, light={light}";
        });

        Check("word-count-cjk", () =>
        {
            // 中文按字计、英文按词计：'你好 world' => 2 + 1
            var count = ViewModels.DocumentViewModel.CountWords("你好 world");
            if (count != 3) throw new InvalidOperationException($"expected 3, got {count}");
            return $"count={count}";
        });

        // 文件 IO 检查必须用真正的 await：在 UI 线程上 .GetAwaiter().GetResult() 会与
        // Avalonia 的同步上下文互相等待而死锁（本项目实测会永久挂起）。
        Check("file-io-roundtrip", () =>
        {
            var service = new DocumentFileService();
            var path = Path.Combine(Path.GetTempPath(), $"markify-selftest-{Guid.NewGuid():N}.md");
            try
            {
                const string content = "# 编码测试\n\n中文与 emoji 🎉\n";
                return Task.Run(async () =>
                {
                    await service.WriteAsync(path, content, new UTF8Encoding(false));
                    var (text, encoding) = await service.ReadAsync(path);

                    if (text != content)
                        throw new InvalidOperationException("roundtrip mismatch");

                    var bytes = File.ReadAllBytes(path);
                    if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                        throw new InvalidOperationException("unexpected BOM written");

                    return $"bytes={bytes.Length}, encoding={encoding.WebName}";
                }).GetAwaiter().GetResult();
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        });

        // HTML 导出覆盖：[GeneratedRegex] 的源生成、Markdig 渲染管线、以及 details 的
        // 抽取还原逻辑都可能在裁剪/AOT 下失效，而单测是在 JIT 下跑的，抓不到这类问题。
        Check("html-export", () =>
        {
            const string markdown = """
                # Title

                | A | B |
                |---|---|
                | 1 | 2 |

                <details>
                <summary>More</summary>

                Inner **bold**

                </details>
                """;

            var html = Services.MarkdownHtmlExporter.Render(markdown, null, "selftest", darkFirst: false);

            if (!html.StartsWith("<!DOCTYPE html>", StringComparison.Ordinal))
                throw new InvalidOperationException("missing doctype");
            if (!html.Contains("<table>", StringComparison.Ordinal))
                throw new InvalidOperationException("table not rendered");
            if (!html.Contains("<details>", StringComparison.Ordinal))
                throw new InvalidOperationException("details block dropped");
            if (html.Contains("MARKIFY_DETAILS_PLACEHOLDER", StringComparison.Ordinal))
                throw new InvalidOperationException("internal placeholder leaked");

            return $"bytes={html.Length}";
        });

        var passed = results.Count(r => r.Ok);
        var failed = results.Count - passed;
        Write(_outputPath, $"complete: all checks done, passed={passed} failed={failed}\n");

        var report = new StringBuilder();
        report.AppendLine("Markify AOT self-test");
        report.AppendLine($"runtime      : {Environment.Version}");
        report.AppendLine($"framework    : {System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription}");
        report.AppendLine($"os           : {System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        report.AppendLine($"arch         : {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        report.AppendLine($"assembly     : {typeof(SelfTest).Assembly.Location}");
        report.AppendLine($"isAot (approx): {IsProbablyAot()}");
        report.AppendLine();
        foreach (var (name, ok, detail) in results)
            report.AppendLine($"[{(ok ? "PASS" : "FAIL")}] {name,-24} {detail}");
        report.AppendLine();
        report.AppendLine($"summary: {passed} passed, {failed} failed");

        var text2 = report.ToString();

        try
        {
            if (!string.IsNullOrEmpty(_outputPath))
            {
                var full = Path.GetFullPath(_outputPath);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                // 覆盖诊断日志：此时诊断阶段已结束，报告才是最终产物。
                File.WriteAllText(full, text2);
            }
        }
        catch (Exception ex)
        {
            Write(_outputPath, $"failed to write report: {ex.Message}\n");
        }

        // 给文件写入一点时间刷新，再退出。
        await Task.Delay(100);
        Exit(desktop, failed == 0 ? 0 : 1);
        }
        catch (Exception ex)
        {
            // 任何未预期的异常都要落盘并返回失败，不能让进程静默挂起。
            var fatal = $"FATAL: {ex}\n";
            Write(_outputPath, fatal);
            Exit(desktop, 1);
        }
    }

    /// <summary>
    /// 结束自检进程。
    /// WinExe 下 <see cref="IClassicDesktopStyleApplicationLifetime.Shutdown"/> 在 UI 线程的
    /// 异步回调里可能无法真正终止进程，导致 CI 一直挂起；因此这里显式兜底 Environment.Exit。
    /// </summary>
    private static void Exit(IClassicDesktopStyleApplicationLifetime desktop, int code)
    {
        try
        {
            desktop.Shutdown(code);
        }
        catch
        {
            // 忽略：下面统一兜底退出。
        }

        // Shutdown 是异步的（会先关闭窗口），给消息循环一点时间；随后强制退出保证 CI 不挂起。
        Dispatcher.UIThread.Post(() =>
        {
            System.Threading.Thread.Sleep(200);
            Environment.Exit(code);
        }, DispatcherPriority.Background);
    }

    /// <summary>
    /// AOT 下 AppContext 里不会有“assembled”标记，且 JIT 不可用；
    /// 这里用「是否存在 JIT」的间接判断给出提示（非严格判定）。
    /// </summary>
    private static string IsProbablyAot()
    {
        try
        {
            // NativeAOT 下 RuntimeFeature.IsDynamicCodeSupported 为 false。
            return System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported ? "no" : "yes";
        }
        catch
        {
            return "unknown";
        }
    }
}
