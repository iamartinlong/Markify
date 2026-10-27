using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Markify.Services;

namespace Markify.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly DocumentFileService _files = new();
    private readonly DispatcherTimer _previewTimer;

    private Encoding _encoding = new UTF8Encoding(false);
    private string _lastSavedText = string.Empty;
    private bool _suppressModified;

    public MainViewModel()
    {
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _previewTimer.Tick += (_, _) =>
        {
            _previewTimer.Stop();
            FlushPreview();
        };

        PreviewMarkdown = SourceText = WelcomeDocument;
        _lastSavedText = WelcomeDocument;
        RecalculateStatistics(SourceText);
        StatusMessage = "就绪";
        RefreshOutline(SourceText);
    }

    public IDocumentDialogService? Dialogs { get; set; }

    /// <summary>打开/新建文档后触发，视图据此把内容推入编辑器并复位光标。</summary>
    public event EventHandler? DocumentLoaded;

    /// <summary>大纲点击后触发，视图把编辑器与预览跳到对应标题。</summary>
    public event EventHandler<MarkdownHeading>? HeadingNavigationRequested;

    [ObservableProperty]
    public partial string SourceText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PreviewMarkdown { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? FilePath { get; set; }

    [ObservableProperty]
    public partial bool IsModified { get; set; }

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
    public partial int CaretLine { get; set; } = 1;

    [ObservableProperty]
    public partial int CaretColumn { get; set; } = 1;

    [ObservableProperty]
    public partial int SelectionLength { get; set; }

    [ObservableProperty]
    public partial int WordCount { get; set; }

    [ObservableProperty]
    public partial int CharCount { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EncodingName { get; set; } = "UTF-8";

    /// <summary>由源码解析出的标题大纲，供侧栏导航使用。</summary>
    public ObservableCollection<MarkdownHeading> Outline { get; } = [];

    [ObservableProperty]
    public partial bool IsOutlineVisible { get; set; }

    [ObservableProperty]
    public partial MarkdownHeading? SelectedHeading { get; set; }

    public string DocumentName =>
        string.IsNullOrEmpty(FilePath) ? "未命名.md" : Path.GetFileName(FilePath);

    public string WindowTitle =>
        $"{DocumentName}{(IsModified ? " *" : string.Empty)} — Markify";

    public string CaretDisplay => $"行 {CaretLine}, 列 {CaretColumn}";

    // ---------- 编辑流程 ----------

    /// <summary>编辑器每次文本变化都调用；写入 SourceText 并启动预览防抖。</summary>
    public void OnEditorTextChanged(string text)
    {
        SourceText = text;
        if (!_suppressModified)
            IsModified = !string.Equals(text, _lastSavedText, StringComparison.Ordinal);

        RecalculateStatistics(text);
        _previewTimer.Stop();
        _previewTimer.Start();
    }

    public void OnCaretChanged(int line, int column, int selectionLength)
    {
        CaretLine = line;
        CaretColumn = column;
        SelectionLength = selectionLength;
    }

    private void FlushPreview()
    {
        if (!string.Equals(PreviewMarkdown, SourceText, StringComparison.Ordinal))
            PreviewMarkdown = SourceText;

        RefreshOutline(SourceText);
    }

    private void RefreshOutline(string text)
    {
        var headings = MarkdownOutline.Parse(text);

        // 仅在标题集合真正变化时重建，避免每次输入都刷新侧栏。
        if (headings.Count == Outline.Count)
        {
            var same = true;
            for (var i = 0; i < headings.Count; i++)
            {
                if (!string.Equals(headings[i].Text, Outline[i].Text, StringComparison.Ordinal)
                    || headings[i].Level != Outline[i].Level
                    || headings[i].Line != Outline[i].Line)
                {
                    same = false;
                    break;
                }
            }

            if (same)
                return;
        }

        Outline.Clear();
        foreach (var heading in headings)
            Outline.Add(heading);
    }

    [RelayCommand]
    private void NavigateToHeading(MarkdownHeading? heading)
    {
        if (heading is null)
            return;

        HeadingNavigationRequested?.Invoke(this, heading);
    }

    [RelayCommand]
    private void ToggleOutline() => IsOutlineVisible = !IsOutlineVisible;

    private void RecalculateStatistics(string text)
    {
        CharCount = text.Length;
        WordCount = CountWords(text);
    }

    /// <summary>统计词数：CJK 按字计，拉丁文字按空白/标点分词。</summary>
    internal static int CountWords(string text)
    {
        var words = 0;
        var inLatinWord = false;
        foreach (var ch in text)
        {
            // CJK 统一表意文字与扩展区、假名、谚文：每个字符视作一个词。
            if (ch is >= '\u3040' and <= '\u30FF' or >= '\u3400' and <= '\u4DBF' or >= '\u4E00' and <= '\u9FFF'
                or >= '\uF900' and <= '\uFAFF' or >= '\uAC00' and <= '\uD7AF')
            {
                words++;
                inLatinWord = false;
                continue;
            }

            if (char.IsLetterOrDigit(ch) || ch == '_' || ch == '\'')
            {
                if (!inLatinWord)
                {
                    words++;
                    inLatinWord = true;
                }
            }
            else
            {
                inLatinWord = false;
            }
        }

        return words;
    }

    partial void OnIsModifiedChanged(bool value)
    {
        OnPropertyChanged(nameof(WindowTitle));
    }

    partial void OnFilePathChanged(string? value)
    {
        OnPropertyChanged(nameof(DocumentName));
        OnPropertyChanged(nameof(WindowTitle));
    }

    partial void OnCaretLineChanged(int value) => OnPropertyChanged(nameof(CaretDisplay));

    partial void OnCaretColumnChanged(int value) => OnPropertyChanged(nameof(CaretDisplay));

    // ---------- 命令 ----------

    [RelayCommand]
    private async Task NewDocumentAsync()
    {
        if (!await ConfirmDiscardAsync())
            return;

        ApplyDocument(string.Empty, null, new UTF8Encoding(false));
        StatusMessage = "已新建文档";
    }

    [RelayCommand]
    private async Task OpenDocumentAsync()
    {
        if (Dialogs is null || !await ConfirmDiscardAsync())
            return;

        var path = await Dialogs.PickOpenFileAsync();
        if (path is null)
            return;

        await LoadAsync(path);
    }

    public async Task LoadAsync(string path)
    {
        try
        {
            StatusMessage = $"正在打开 {Path.GetFileName(path)}…";
            var (text, encoding) = await _files.ReadAsync(path);
            ApplyDocument(text, path, encoding);
            StatusMessage = $"已打开 {Path.GetFileName(path)}（{EncodingName}）";
        }
        catch (Exception ex)
        {
            StatusMessage = $"打开失败：{ex.Message}";
        }
    }

    private void ApplyDocument(string text, string? path, Encoding encoding)
    {
        _suppressModified = true;
        try
        {
            FilePath = path;
            _encoding = encoding;
            EncodingName = encoding.WebName.ToUpperInvariant() switch
            {
                "UTF-8" => encoding.GetPreamble().Length > 0 ? "UTF-8 BOM" : "UTF-8",
                "UTF-16" => "UTF-16 LE",
                var other => other,
            };

            _lastSavedText = text;
            SourceText = text;
            PreviewMarkdown = text;
            IsModified = false;
            RecalculateStatistics(text);
            RefreshOutline(text);
            DocumentLoaded?.Invoke(this, EventArgs.Empty);
        }
        finally
        {
            _suppressModified = false;
        }
    }

    [RelayCommand]
    private async Task SaveAsync()
    {
        if (string.IsNullOrEmpty(FilePath))
        {
            await SaveAsAsync();
            return;
        }

        await SaveToAsync(FilePath);
    }

    [RelayCommand]
    private async Task SaveAsAsync()
    {
        if (Dialogs is null)
            return;

        var path = await Dialogs.PickSaveFileAsync(FilePath);
        if (path is null)
            return;

        if (string.IsNullOrEmpty(Path.GetExtension(path)))
            path += ".md";

        await SaveToAsync(path);
    }

    private async Task SaveToAsync(string path)
    {
        try
        {
            await _files.WriteAsync(path, SourceText, _encoding);
            FilePath = path;
            _lastSavedText = SourceText;
            IsModified = false;
            StatusMessage = $"已保存 {Path.GetFileName(path)}";
        }
        catch (Exception ex)
        {
            StatusMessage = $"保存失败：{ex.Message}";
        }
    }

    public async Task<bool> ConfirmDiscardAsync()
    {
        if (!IsModified || Dialogs is null)
            return true;

        return await Dialogs.ConfirmDiscardAsync(DocumentName);
    }

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

    private const string WelcomeDocument = """
        # Markify

        一个用 Avalonia 构建的**轻量级 Markdown 编辑/预览客户端**。

        ## 已实现的能力

        - [x] 左侧 `AvaloniaEdit` 源码编辑，Markdown 语法高亮（TextMate）
        - [x] 右侧原生控件渲染预览（MarkView.Avalonia + Markdig），无 WebView
        - [x] 180ms 防抖的实时预览与滚动位置同步
        - [x] 文件打开/保存/另存为，UTF-8 / UTF-16 编码与 BOM 保留，原子写入
        - [x] 字数、字符数、光标行列状态栏
        - [x] 明暗主题切换、横/纵分栏、专注预览模式
        - [ ] 待办：大纲导航、导出 PDF/HTML、多标签页

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

        > 提示：按 `Ctrl+B` 加粗、`Ctrl+I` 斜体、`Ctrl+K` 插入链接，`Ctrl+S` 保存。

        1. 打开或新建一个 `.md` 文件
        2. 在左侧输入，右侧即时呈现
        3. 保存后标题栏的 `*` 消失
        """;
}
