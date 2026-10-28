using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Styling;
using Markify.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Markify.ViewModels;

/// <summary>
/// 工作区：管理多个文档标签、全局视图选项与文档无关的命令。
///
/// 拆分动机：原先把"文档状态"和"应用级设置"混在一起，导致切标签要整体重建状态。
/// 现在文档相关的一切在 <see cref="DocumentViewModel"/>，这里只管集合与外壳。
/// </summary>
public partial class MainViewModel : ObservableObject
{
    private readonly DocumentFileService _files = new();

    /// <summary>新建但尚未保存的文档计数，用于生成「未命名 N.md」。</summary>
    private int _untitledCount;

    public MainViewModel()
    {
        Documents.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasDocuments));
            OnPropertyChanged(nameof(CanCloseTab));
        };

        // 启动时给一个示例文档，避免空白窗口。
        _untitledCount = 1;
        var first = CreateDocument(WelcomeDocument, null, new UTF8Encoding(false), "未命名.md");
        Documents.Add(first);
        ActiveDocument = first;
    }

    public ObservableCollection<DocumentViewModel> Documents { get; } = [];

    /// <summary>询问用户是否放弃某个文档的未保存修改。</summary>
    public event Func<Task<bool>>? DiscardPromptRequested;

    /// <summary>外部修改确认：参数是文档名。</summary>
    public event Func<string, Task<bool>>? ExternalChangePromptRequested;

    /// <summary>请求打开编辑器查找面板。</summary>
    public event EventHandler? FindRequested;

    /// <summary>活动文档切换后触发，视图据此把编辑器的 Document 换掉。</summary>
    public event EventHandler<DocumentViewModel?>? ActiveDocumentChanged;

    public IDocumentDialogService? Dialogs { get; set; }

    [ObservableProperty]
    public partial DocumentViewModel? ActiveDocument { get; set; }

    [ObservableProperty]
    public partial bool IsWordWrap { get; set; }

    [ObservableProperty]
    public partial bool ShowLineNumbers { get; set; } = true;

    [ObservableProperty]
    public partial bool IsPreviewVisible { get; set; } = true;

    [ObservableProperty]
    public partial bool IsVerticalSplit { get; set; } = true;

    [ObservableProperty]
    public partial bool IsDarkTheme { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = "就绪";

    [ObservableProperty]
    public partial bool IsOutlineVisible { get; set; }

    public bool HasDocuments => Documents.Count > 0;

    public bool CanCloseTab => Documents.Count > 1;

    /// <summary>标题栏：活动文档名 + 修改标记。</summary>
    public string WindowTitle
    {
        get
        {
            var name = ActiveDocument?.DocumentName ?? "无文档";
            var dirty = ActiveDocument?.IsModified == true ? " *" : string.Empty;
            return $"{name}{dirty} — Markify";
        }
    }

    /// <summary>状态栏光标位置。</summary>
    public string CaretDisplay => ActiveDocument?.CaretDisplay ?? "行 -, 列 -";

    partial void OnActiveDocumentChanged(DocumentViewModel? oldValue, DocumentViewModel? newValue)
    {
        if (oldValue is not null)
        {
            oldValue.PropertyChanged -= OnActiveDocumentPropertyChanged;
            oldValue.IsActive = false;
            // 切走前把未完成的预览刷新，避免防抖回调在切走后落到别的文档上。
            oldValue.CommitPendingPreview();
        }

        if (newValue is not null)
        {
            newValue.PropertyChanged += OnActiveDocumentPropertyChanged;
            newValue.ExternalChangePrompt = PromptExternalChangeAsync;
            newValue.IsActive = true;
        }

        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(CaretDisplay));
        ActiveDocumentChanged?.Invoke(this, newValue);
    }

    private void OnActiveDocumentPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // 只有活动文档的状态变化才需要反映到窗口外壳上。
        if (!ReferenceEquals(sender, ActiveDocument))
            return;

        switch (e.PropertyName)
        {
            case nameof(DocumentViewModel.IsModified):
            case nameof(DocumentViewModel.FilePath):
                OnPropertyChanged(nameof(WindowTitle));
                break;
            case nameof(DocumentViewModel.CaretLine):
            case nameof(DocumentViewModel.CaretColumn):
                OnPropertyChanged(nameof(CaretDisplay));
                break;
        }
    }

    private DocumentViewModel CreateDocument(string text, string? path, Encoding encoding,
                                             string? untitledName = null)
    {
        var doc = new DocumentViewModel(_files, text, path, encoding, untitledName);
        doc.ExternalChangePrompt = PromptExternalChangeAsync;
        return doc;
    }

    // ---------- 标签操作 ----------

    [RelayCommand]
    private void NewDocument()
    {
        // 首个文档用「未命名.md」，之后递增，避免标签栏里一排同名标签无法区分。
        _untitledCount++;
        var name = _untitledCount == 1 ? "未命名.md" : $"未命名 {_untitledCount}.md";
        var doc = CreateDocument(string.Empty, null, new UTF8Encoding(false), name);
        Documents.Add(doc);
        ActiveDocument = doc;
        StatusMessage = $"已新建文档（共 {Documents.Count} 个标签）";
    }

    [RelayCommand]
    private async Task OpenDocumentAsync()
    {
        if (Dialogs is null)
            return;

        var path = await Dialogs.PickOpenFileAsync();
        if (path is null)
            return;

        await OpenPathAsync(path);
    }

    /// <summary>打开文件：若已打开则切到该标签，否则新建标签。</summary>
    public async Task OpenPathAsync(string path)
    {
        var full = Path.GetFullPath(path);

        var existing = Documents.FirstOrDefault(d =>
            d.FilePath is not null && string.Equals(Path.GetFullPath(d.FilePath), full, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            ActiveDocument = existing;
            StatusMessage = $"已切换到 {existing.DocumentName}";
            return;
        }

        try
        {
            StatusMessage = $"正在打开 {Path.GetFileName(path)}…";
            var (text, encoding) = await _files.ReadAsync(full);
            var doc = CreateDocument(text, full, encoding);
            Documents.Add(doc);
            ActiveDocument = doc;
            StatusMessage = $"已打开 {doc.DocumentName}（{doc.EncodingName}）";
        }
        catch (Exception ex)
        {
            StatusMessage = $"打开失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private async Task CloseTabAsync(DocumentViewModel? doc)
    {
        doc ??= ActiveDocument;
        if (doc is null)
            return;

        if (Documents.Count == 1)
        {
            // 最后一个标签：清空内容而不是关闭窗口，行为更可预期。
            StatusMessage = "已保留最后一个标签（无法关闭全部）";
            return;
        }

        if (doc.IsModified && !await ConfirmDiscardAsync(doc))
            return;

        CloseDocumentCore(doc);
    }

    private void CloseDocumentCore(DocumentViewModel doc)
    {
        var index = Documents.IndexOf(doc);
        if (index < 0)
            return;

        Documents.Remove(doc);

        if (ReferenceEquals(ActiveDocument, doc))
        {
            // 关掉当前标签后优先选右边的，没有就选左边的。
            var next = index < Documents.Count ? Documents[index] : Documents[^1];
            ActiveDocument = next;
        }

        doc.Dispose();
        StatusMessage = $"已关闭 {doc.DocumentName}（剩余 {Documents.Count} 个）";
        OnPropertyChanged(nameof(CanCloseTab));
    }

    [RelayCommand]
    private void NextTab() => CycleTab(1);

    [RelayCommand]
    private void PreviousTab() => CycleTab(-1);

    private void CycleTab(int delta)
    {
        if (Documents.Count <= 1 || ActiveDocument is null)
            return;

        var index = Documents.IndexOf(ActiveDocument);
        if (index < 0)
            return;

        var next = (index + delta + Documents.Count) % Documents.Count;
        ActiveDocument = Documents[next];
    }

    /// <summary>直接选择标签（标签栏点击用）。</summary>
    [RelayCommand]
    private void ActivateDocument(DocumentViewModel? doc)
    {
        if (doc is not null)
            ActiveDocument = doc;
    }

    // ---------- 保存 ----------

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (ActiveDocument is not { } doc)
            return;

        if (string.IsNullOrEmpty(doc.FilePath))
        {
            await SaveAsAsync();
            return;
        }

        await SaveDocumentAsync(doc);
    }

    [RelayCommand]
    private async Task SaveAsAsync()
    {
        if (Dialogs is null || ActiveDocument is not { } doc)
            return;

        var path = await Dialogs.PickSaveFileAsync(doc.FilePath);
        if (path is null)
            return;

        if (string.IsNullOrEmpty(Path.GetExtension(path)))
            path += ".md";

        await SaveDocumentAsync(doc, path);
    }

    /// <summary>
    /// 导出当前文档为 PDF。
    /// 复用 HTML 导出的排版（同一套 CSS + 内嵌图片），交给系统 Edge/Chrome 无头打印，
    /// 因此 PDF 的观感与 HTML 导出、与预览一致。
    /// </summary>
    [RelayCommand]
    private async Task ExportPdfAsync()
    {
        if (Dialogs is null || ActiveDocument is not { } doc)
            return;

        var suggested = string.IsNullOrEmpty(doc.FilePath)
            ? doc.DocumentName
            : Path.GetFileNameWithoutExtension(doc.FilePath);

        var target = await Dialogs.PickExportPdfPathAsync(suggested + ".pdf");
        if (target is null)
            return;

        if (string.IsNullOrEmpty(Path.GetExtension(target)))
            target += ".pdf";

        try
        {
            StatusMessage = "正在生成 PDF…";

            var documentDirectory = string.IsNullOrEmpty(doc.FilePath)
                ? null
                : Path.GetDirectoryName(Path.GetFullPath(doc.FilePath));

            var html = MarkdownHtmlExporter.Render(
                doc.SourceText,
                documentDirectory,
                doc.DocumentName,
                IsDarkTheme);

            var result = await PdfExporter.ExportAsync(html, target);

            if (result.Success)
            {
                StatusMessage = $"已导出 {Path.GetFileName(target)}";
                await Dialogs.ShowMessageAsync("导出完成", $"已导出到：\n{target}");
            }
            else
            {
                StatusMessage = "PDF 导出失败";
                await Dialogs.ShowMessageAsync("PDF 导出失败", result.Error ?? "未知错误");
            }
        }
        catch (Exception ex)
        {
            StatusMessage = $"PDF 导出失败：{ex.Message}";
            await Dialogs.ShowMessageAsync("PDF 导出失败", ex.Message);
        }
    }

    /// <summary>当前环境是否具备 PDF 导出条件（供菜单禁用提示用）。</summary>
    public bool CanExportPdf => PdfExporter.FindBrowser() is not null;

    /// <summary>
    /// 导出当前文档为独立 HTML 文件。
    /// 与预览使用同一套 Markdig 管线，并把本地图片内嵌为 data URI，因此导出的文件可单独分发。
    /// </summary>
    [RelayCommand]
    private async Task ExportHtmlAsync()
    {
        if (Dialogs is null || ActiveDocument is not { } doc)
            return;

        // 默认文件名与当前文档同名，扩展名换成 .html
        var suggested = string.IsNullOrEmpty(doc.FilePath)
            ? doc.DocumentName
            : Path.GetFileNameWithoutExtension(doc.FilePath);

        var target = await Dialogs.PickExportHtmlPathAsync(suggested + ".html");
        if (target is null)
            return;

        if (string.IsNullOrEmpty(Path.GetExtension(target)))
            target += ".html";

        try
        {
            var documentDirectory = string.IsNullOrEmpty(doc.FilePath)
                ? null
                : Path.GetDirectoryName(Path.GetFullPath(doc.FilePath));

            var html = MarkdownHtmlExporter.Render(
                doc.SourceText,
                documentDirectory,
                doc.DocumentName,
                IsDarkTheme);

            await File.WriteAllTextAsync(target, html, new UTF8Encoding(false));

            StatusMessage = $"已导出 {Path.GetFileName(target)}";
            await Dialogs.ShowMessageAsync("导出完成",
                $"已导出到：\n{target}\n\n本地图片已内嵌，文件可单独分发。");
        }
        catch (Exception ex)
        {
            StatusMessage = $"导出失败：{ex.Message}";
            await Dialogs.ShowMessageAsync("导出失败", ex.Message);
        }
    }

    private async Task SaveDocumentAsync(DocumentViewModel doc, string? path = null)
    {
        path ??= doc.FilePath;
        if (string.IsNullOrEmpty(path))
            return;

        var ok = await doc.SaveAsync(path);
        StatusMessage = ok
            ? $"已保存 {Path.GetFileName(path)}"
            : $"保存失败：无法写入 {Path.GetFileName(path)}";

        // 文档名/标题可能变化
        OnPropertyChanged(nameof(WindowTitle));
    }

    // ---------- 关闭全部 / 退出 ----------

    /// <summary>关闭所有标签前逐个确认；返回 false 表示用户取消了操作。</summary>
    public async Task<bool> ConfirmCloseAllAsync()
    {
        foreach (var doc in Documents.Where(d => d.IsModified).ToList())
        {
            if (!await ConfirmDiscardAsync(doc))
                return false;
        }

        return true;
    }

    public async Task<bool> ConfirmDiscardAsync(DocumentViewModel doc)
    {
        if (!doc.IsModified)
            return true;

        if (DiscardPromptRequested is null)
            return true;

        return await DiscardPromptRequested();
    }

    /// <summary>停止所有文档的监听（退出前调用）。</summary>
    public void DisposeDocuments()
    {
        foreach (var doc in Documents)
            doc.Dispose();
    }

    // ---------- 文档相关转发 ----------

    [RelayCommand]
    private void Find() => FindRequested?.Invoke(this, EventArgs.Empty);

    [RelayCommand]
    private void NavigateToHeading(MarkdownHeading? heading)
    {
        // 由视图订阅 ActiveDocumentChanged 后自行处理；这里保留命令以便大纲绑定。
        ActiveDocument?.SelectedHeading = heading;
    }

    // ---------- 视图选项 ----------

    [RelayCommand]
    private void ToggleOutline() => IsOutlineVisible = !IsOutlineVisible;

    [RelayCommand]
    private void ToggleWordWrap() => IsWordWrap = !IsWordWrap;

    [RelayCommand]
    private void ToggleLineNumbers() => ShowLineNumbers = !ShowLineNumbers;

    [RelayCommand]
    private void TogglePreview() => IsPreviewVisible = !IsPreviewVisible;

    [RelayCommand]
    private void ToggleSplitOrientation()
    {
        IsVerticalSplit = !IsVerticalSplit;
        IsPreviewVisible = true;
    }

    [RelayCommand]
    private void ToggleTheme() => IsDarkTheme = !IsDarkTheme;

    partial void OnIsDarkThemeChanged(bool value)
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = value ? ThemeVariant.Dark : ThemeVariant.Light;

        StatusMessage = value ? "已切换到深色主题" : "已切换到浅色主题";
    }

    private Task<bool> PromptExternalChangeAsync() =>
        ExternalChangePromptRequested is null
            ? Task.FromResult(false)
            : ExternalChangePromptRequested(ActiveDocument?.DocumentName ?? "文档");

    private const string WelcomeDocument = """
        # Markify

        一个用 Avalonia 构建的**轻量级 Markdown 编辑/预览客户端**。

        ## 已实现的能力

        - [x] 左侧 `AvaloniaEdit` 源码编辑，Markdown 语法高亮（TextMate）
        - [x] 右侧原生控件渲染预览（MarkView.Avalonia + Markdig），无 WebView
        - [x] 180ms 防抖的实时预览与滚动位置同步
        - [x] **多标签页**：同时打开多个文档，每个标签独立撤销栈与修改状态
        - [x] 文件打开/保存/另存为，UTF-8 / UTF-16 编码与 BOM 保留，原子写入
        - [x] 外部修改检测：文件被其他程序改动时提示重新加载
        - [x] `Ctrl+F` / `Ctrl+H` 查找与替换
        - [x] 字数、字符数、光标行列状态栏
        - [x] 明暗主题切换、横/纵分栏、专注预览模式

        ## 表格与代码

        | 方案 | 渲染方式 | 依赖 |
        | --- | --- | --- |
        | MarkView.Avalonia | 原生 Avalonia 控件 | Markdig |
        | Markdown.Avalonia | 原生 Avalonia 控件 | Markdig + AvaloniaEdit |
        | WebView | HTML + JS | 平台 WebView |

        ```csharp
        // 预览通过绑定驱动：MarkdownViewer.Markdown = PreviewMarkdown
        public partial string PreviewMarkdown { get; set; } = string.Empty;
        ```

        > 提示：`Ctrl+T` 新建标签，`Ctrl+W` 关闭当前标签，`Ctrl+Tab` 切换标签。

        1. 打开或新建一个 `.md` 文件
        2. 在左侧输入，右侧即时呈现
        3. 保存后标题栏的 `*` 消失
        """;
}
