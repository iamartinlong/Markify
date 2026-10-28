using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Markify.Services;

namespace Markify.ViewModels;

/// <summary>
/// 单个文档（一个标签页）。
///
/// 关键设计：每个文档持有自己的 <see cref="TextDocument"/>，因此撤销栈、修改状态天然相互隔离。
/// 视图侧只有一个 TextEditor，切换标签时替换它的 Document 而不是重建控件——
/// 每个 TextEditor 都要装一套 TextMate 语法与 transformer，按标签建控件在十几个文件时开销很明显。
/// </summary>
public partial class DocumentViewModel : ObservableObject, IDisposable
{
    private readonly DocumentFileService _files;
    private readonly FileChangeWatcher _watcher = new();
    private readonly DispatcherTimer _previewTimer;
    private readonly string? _untitledName;

    private Encoding _encoding = new UTF8Encoding(false);
    private string _lastSavedText = string.Empty;
    private bool _externalPromptOpen;
    private bool _disposed;

    public DocumentViewModel(DocumentFileService files, string text, string? path, Encoding encoding,
                             string? untitledName = null)
    {
        _files = files;
        _untitledName = untitledName;

        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(180) };
        _previewTimer.Tick += (_, _) =>
        {
            _previewTimer.Stop();
            FlushPreview();
        };

        _watcher.ExternalChangeDetected += (_, _) =>
            Dispatcher.UIThread.Post(() => _ = HandleExternalChangeAsync());

        Document = new TextDocument(text);

        FilePath = path;
        _encoding = encoding;
        EncodingName = DescribeEncoding(encoding);
        _lastSavedText = text;

        SourceText = text;
        PreviewMarkdown = text;
        RecalculateStatistics(text);
        RefreshOutline(text);

        _watcher.Watch(path);
    }

    /// <summary>编辑器持有的文档对象；切换标签时直接替换它。</summary>
    public TextDocument Document { get; }

    /// <summary>该文档在文件系统上发生变化且用户选择重载时触发（由工作区注入）。</summary>
    public Func<Task<bool>>? ExternalChangePrompt { get; set; }

    /// <summary>请求把焦点/光标放到该文档上次的位置。</summary>
    public event EventHandler? Activated;

    [ObservableProperty]
    public partial string SourceText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string PreviewMarkdown { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string? FilePath { get; set; }

    [ObservableProperty]
    public partial bool IsModified { get; set; }

    [ObservableProperty]
    public partial string EncodingName { get; set; } = "UTF-8";

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

    public ObservableCollection<MarkdownHeading> Outline { get; } = [];

    [ObservableProperty]
    public partial MarkdownHeading? SelectedHeading { get; set; }

    /// <summary>是否为当前活动标签。由工作区维护，供标签栏样式绑定。</summary>
    [ObservableProperty]
    public partial bool IsActive { get; set; }

    /// <summary>切换标签时保存光标位置，切回来能回到原处。</summary>
    public int SavedCaretOffset { get; set; }

    /// <summary>切换标签时保存垂直滚动偏移。</summary>
    public double SavedVerticalOffset { get; set; }

    public string DocumentName =>
        string.IsNullOrEmpty(FilePath)
            ? (_untitledName ?? "未命名.md")
            : Path.GetFileName(FilePath);

    /// <summary>
    /// 标签的悬浮提示：显示完整路径，方便确认打开的是哪个文件。
    /// 未保存的新文档没有路径，给出可操作的提示而不是空白。
    /// </summary>
    public string TabToolTip =>
        string.IsNullOrEmpty(FilePath)
            ? $"{DocumentName}（尚未保存，无文件路径）"
            : FilePath;

    /// <summary>标签页上显示的文字（带修改标记）。</summary>
    public string TabTitle =>
        $"{DocumentName}{(IsModified ? " •" : string.Empty)}";

    public string CaretDisplay => $"行 {CaretLine}, 列 {CaretColumn}";

    public bool CanClose => true;

    // ---------- 编辑流程 ----------

    public void OnTextChanged(string text, bool markModified = true)
    {
        SourceText = text;
        if (markModified)
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

    /// <summary>立即刷新预览（切换标签前调用，避免旧文档的防抖回调落到新文档上）。</summary>
    public void CommitPendingPreview()
    {
        _previewTimer.Stop();
        FlushPreview();
    }

    private void RefreshOutline(string text)
    {
        var headings = MarkdownOutline.Parse(text);

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

    private static string DescribeEncoding(Encoding encoding) =>
        encoding.WebName.ToUpperInvariant() switch
        {
            "UTF-8" => encoding.GetPreamble().Length > 0 ? "UTF-8 BOM" : "UTF-8",
            "UTF-16" => "UTF-16 LE",
            var other => other,
        };

    partial void OnIsModifiedChanged(bool value) => OnPropertyChanged(nameof(TabTitle));

    partial void OnFilePathChanged(string? value)
    {
        OnPropertyChanged(nameof(DocumentName));
        OnPropertyChanged(nameof(TabTitle));
        OnPropertyChanged(nameof(TabToolTip));
    }

    partial void OnCaretLineChanged(int value) => OnPropertyChanged(nameof(CaretDisplay));
    partial void OnCaretColumnChanged(int value) => OnPropertyChanged(nameof(CaretDisplay));

    // ---------- 重新加载 ----------

    /// <summary>用磁盘内容替换当前文档。保留撤销栈的可用性（作为一次整体替换）。</summary>
    public void Reload(string text, string? path, Encoding encoding)
    {
        FilePath = path;
        _encoding = encoding;
        EncodingName = DescribeEncoding(encoding);
        _lastSavedText = text;

        // 直接改 Document.Text 会清空撤销栈；这里是有意为之——
        // 重载意味着"接受磁盘版本"，把旧撤销历史保留下来会撤销到磁盘之外的状态，容易误操作。
        Document.Text = text;
        Document.UndoStack.ClearAll();
        Document.UndoStack.MarkAsOriginalFile();

        SourceText = text;
        PreviewMarkdown = text;
        IsModified = false;
        RecalculateStatistics(text);
        RefreshOutline(text);

        _watcher.Watch(path);
        Activated?.Invoke(this, EventArgs.Empty);
    }

    // ---------- 保存 ----------

    public async Task<bool> SaveAsync(string path)
    {
        try
        {
            using (_watcher.SuppressOwnWrite())
            {
                await _files.WriteAsync(path, SourceText, _encoding);
            }

            var pathChanged = !string.Equals(FilePath, path, StringComparison.OrdinalIgnoreCase);
            FilePath = path;
            _lastSavedText = SourceText;
            IsModified = false;

            if (pathChanged)
                _watcher.Watch(path);

            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---------- 外部修改 ----------

    private async Task HandleExternalChangeAsync()
    {
        if (_disposed || string.IsNullOrEmpty(FilePath) || ExternalChangePrompt is null)
            return;

        if (_externalPromptOpen)
            return;

        _externalPromptOpen = true;
        try
        {
            var (diskText, encoding) = await _files.ReadAsync(FilePath);
            if (string.Equals(diskText, _lastSavedText, StringComparison.Ordinal))
                return;   // 只是时间戳变化，内容没变

            var reload = await ExternalChangePrompt();
            if (!reload)
                return;

            Reload(diskText, FilePath, encoding);
        }
        catch
        {
            // 读取失败（文件正被替换等）时静默忽略，等下一次事件
        }
        finally
        {
            _externalPromptOpen = false;
        }
    }

    /// <summary>停止计时器与文件监听。</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        _previewTimer.Stop();
        _watcher.Dispose();
        GC.SuppressFinalize(this);
    }
}
