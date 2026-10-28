using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AvaloniaEdit;
using AvaloniaEdit.Document;
using AvaloniaEdit.TextMate;
using Markify.Services;
using Markify.ViewModels;
using MarkView.Avalonia;
using TextMateSharp.Grammars;

namespace Markify.Views;

public partial class MainWindow : Window, IDocumentDialogService
{
    private readonly TextMate.Installation _textMate;
    private RegistryOptions _registryOptions;

    private ScrollViewer? _previewScroll;
    private ScrollViewer? _hookedPreview;
    private bool _syncingScroll;
    private bool _restoringPreviewScroll;
    private bool _navigating;
    private bool _suppressEditorEvents;
    private double _lastEditorRatio;
    private MainViewModel? _viewModel;
    private DocumentViewModel? _attachedDocument;

    public MainWindow()
    {
        InitializeComponent();

        _registryOptions = new RegistryOptions(ThemeName.LightPlus);
        _textMate = Editor.InstallTextMate(_registryOptions);
        ApplyMarkdownGrammar();

        Editor.TextChanged += OnEditorTextChanged;
        Editor.TextArea.Caret.PositionChanged += OnCaretPositionChanged;
        Editor.TextArea.TextView.ScrollOffsetChanged += OnEditorScrollOffsetChanged;

        Editor.AddHandler(KeyDownEvent, OnEditorKeyDown, RoutingStrategies.Tunnel);

        // Ctrl+H 兜底：AvaloniaEdit 的 TextArea 会在隧道阶段消费该组合键，
        // 编辑器级的处理器（上面那个）收不到（用日志实测确认过）。
        // 在窗口级的隧道阶段截获，优先级更高。
        AddHandler(KeyDownEvent, OnWindowPreviewKeyDown, RoutingStrategies.Tunnel);
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);

        DataContextChanged += OnDataContextChanged;
        LayoutUpdated += OnLayoutUpdated;
        OutlineList.SelectionChanged += OnOutlineSelectionChanged;

        // 首次显示时按 ViewModel 的初始状态排一次布局（上下/左右、是否显示预览）。
        Loaded += (_, _) => ApplyPaneLayout();

        // 预览模板内的 ScrollViewer 要到模板应用后才存在，故延迟挂接。
        PreviewHost.AttachedToVisualTree += (_, _) => HookPreviewScroll();
    }

    private void HookPreviewScroll()
    {
        var preview = PreviewScroll;
        if (preview is null || ReferenceEquals(_hookedPreview, preview))
            return;

        if (_hookedPreview is not null)
            _hookedPreview.ScrollChanged -= OnPreviewScrollChanged;

        _hookedPreview = preview;
        preview.ScrollChanged += OnPreviewScrollChanged;
    }

    private void ApplyMarkdownGrammar()
    {
        var language = _registryOptions.GetLanguageByExtension(".md")
                       ?? _registryOptions.GetAvailableLanguages().First(l => l.Id == "markdown");
        _textMate.SetGrammar(_registryOptions.GetScopeByLanguageId(language.Id));
    }

    // ---------- ViewModel 连接 ----------

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.ActiveDocumentChanged -= OnActiveDocumentChanged;
            _viewModel.FindRequested -= OnFindRequested;
            _viewModel.ReplaceRequested -= OnReplaceRequested;
            _viewModel.ExternalChangePromptRequested -= OnExternalChangePromptRequested;
            _viewModel.DiscardPromptRequested -= OnDiscardPromptRequested;
        }

        _viewModel = DataContext as MainViewModel;
        if (_viewModel is null)
            return;

        _viewModel.Dialogs = this;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.ActiveDocumentChanged += OnActiveDocumentChanged;
        _viewModel.FindRequested += OnFindRequested;
        _viewModel.ReplaceRequested += OnReplaceRequested;
        _viewModel.ExternalChangePromptRequested += OnExternalChangePromptRequested;
        _viewModel.DiscardPromptRequested += OnDiscardPromptRequested;

        // 初始文档或后续切换标签都走同一条路径。
        AttachActiveDocument(_viewModel.ActiveDocument);
    }

    /// <summary>
    /// 绑定活动文档：把编辑器的 Document 换成该文档自己的 TextDocument。
    /// 只换文档不重建编辑器——每个 TextEditor 都要装一套 TextMate，按标签建控件开销明显。
    /// </summary>
    private void OnActiveDocumentChanged(object? sender, DocumentViewModel? doc) => AttachActiveDocument(doc);

    private void AttachActiveDocument(DocumentViewModel? doc)
    {
        // 切走前记住旧文档的光标与滚动位置
        if (_attachedDocument is not null && !ReferenceEquals(_attachedDocument, doc))
        {
            _attachedDocument.SavedCaretOffset = Editor.CaretOffset;
            _attachedDocument.SavedVerticalOffset = Editor.VerticalOffset;
            _attachedDocument.PropertyChanged -= OnActiveDocumentPropertyChanged;
        }

        _attachedDocument = doc;

        if (doc is null)
        {
            Editor.Document = new TextDocument();
            return;
        }

        _suppressEditorEvents = true;
        try
        {
            if (!ReferenceEquals(Editor.Document, doc.Document))
                Editor.Document = doc.Document;

            Editor.CaretOffset = Math.Clamp(doc.SavedCaretOffset, 0, Math.Max(0, doc.Document.TextLength));
        }
        finally
        {
            _suppressEditorEvents = false;
        }

        doc.PropertyChanged += OnActiveDocumentPropertyChanged;

        // 恢复滚动位置要等布局完成
        Dispatcher.UIThread.Post(() =>
        {
            if (doc.SavedVerticalOffset > 0)
                Editor.ScrollToVerticalOffset(doc.SavedVerticalOffset);
            else
                Editor.ScrollToHome();

            Editor.TextArea.Focus();
        }, DispatcherPriority.Loaded);
    }

    private void OnActiveDocumentPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 大纲选中项变化时同步滚动编辑器与预览
        if (e.PropertyName == nameof(DocumentViewModel.SelectedHeading)
            && sender is DocumentViewModel doc
            && ReferenceEquals(doc, _attachedDocument)
            && doc.SelectedHeading is { } heading)
        {
            NavigateToHeading(heading);
        }
    }

    // ---------- 大纲导航 ----------

    private void OnOutlineSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (OutlineList.SelectedItem is not MarkdownHeading heading)
            return;

        NavigateToHeading(heading);
    }

    private void OnFindRequested(object? sender, EventArgs e)
    {
        Editor.TextArea.Focus();
        ShowSearchPanel();
    }

    private void OnReplaceRequested(object? sender, EventArgs e)
    {
        Editor.TextArea.Focus();
        ShowSearchPanel(replaceMode: true);
    }

    /// <summary>外部修改确认对话框。返回 true 表示用户选择重新加载。</summary>
    private Task<bool> OnExternalChangePromptRequested(string documentName) => ConfirmReloadAsync(documentName);

    /// <summary>未保存修改的确认框（关闭标签用）。</summary>
    private Task<bool> OnDiscardPromptRequested() =>
        _viewModel?.ActiveDocument is { } doc
            ? ConfirmDiscardAsync(doc.DocumentName)
            : Task.FromResult(true);

    private Task<bool> ConfirmReloadAsync(string documentName)
    {
        var tcs = new TaskCompletionSource<bool>();

        var dialog = new Window
        {
            Title = "文件已被外部修改",
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var message = new TextBlock
        {
            Text = $"「{documentName}」已被其他程序修改，是否重新加载？\n\n" +
                   "选择「重新加载」将丢弃当前未保存的修改。",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16),
        };

        var reload = new Button { Content = "重新加载", MinWidth = 96 };
        var keep = new Button { Content = "保留当前内容", MinWidth = 96, IsCancel = true };

        reload.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };
        keep.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };
        dialog.Closed += (_, _) => tcs.TrySetResult(false);

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                message,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { reload, keep },
                },
            },
        };

        dialog.ShowDialog(this);
        return tcs.Task;
    }

    /// <summary>把编辑器滚动到标题所在行，并把预览滚到同名锚点。</summary>
    private void NavigateToHeading(MarkdownHeading heading)
    {
        if (_navigating)
            return;

        _navigating = true;
        try
        {
            var line = Math.Clamp(heading.Line, 1, Editor.Document.LineCount);
            Editor.ScrollToLine(line);
            Editor.CaretOffset = Editor.Document.GetLineByNumber(line).Offset;
            Editor.TextArea.Focus();

            if (Preview.IsVisible)
                Preview.ScrollToAnchor(heading.Slug);
        }
        finally
        {
            _navigating = false;
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsDarkTheme):
                ApplyEditorTheme(_viewModel?.IsDarkTheme == true);
                break;
        }

        if (e.PropertyName is nameof(MainViewModel.IsPreviewVisible) or nameof(MainViewModel.IsVerticalSplit))
            ApplyPaneLayout();
    }

    private void ApplyEditorTheme(bool dark)
    {
        _registryOptions = new RegistryOptions(dark ? ThemeName.DarkPlus : ThemeName.LightPlus);
        _textMate.SetTheme(_registryOptions.GetDefaultTheme());
        Editor.TextArea.TextView.Redraw();
    }

    /// <summary>
    /// 按当前状态重排「编辑器 / 分割条 / 预览」。
    /// 预览隐藏时收起预览轨道与分割条，让编辑器占满整块区域。
    /// </summary>
    private void ApplyPaneLayout()
    {
        var vertical = _viewModel?.IsVerticalSplit ?? true;
        var showPreview = _viewModel?.IsPreviewVisible ?? true;

        var columns = EditorPreviewGrid.ColumnDefinitions;
        var rows = EditorPreviewGrid.RowDefinitions;

        // 分栏方向决定「编辑器与预览」落在列还是行上；隐藏预览时对应轨道宽度归零。
        columns[0] = new ColumnDefinition(new GridLength(1, GridUnitType.Star))
        {
            MinWidth = vertical ? 200 : 0,
        };
        columns[1] = new ColumnDefinition(
            new GridLength(vertical && showPreview ? 1 : 0, GridUnitType.Pixel));
        columns[2] = new ColumnDefinition(
            vertical && showPreview ? new GridLength(1, GridUnitType.Star) : new GridLength(0))
        {
            MinWidth = vertical && showPreview ? 200 : 0,
        };

        rows[0] = new RowDefinition(new GridLength(1, GridUnitType.Star))
        {
            // 编辑器行：上下分栏时才需要最小高度限制；左右分栏时它独占整行，不受影响。
            MinHeight = 120,
        };
        rows[1] = new RowDefinition(
            new GridLength(!vertical && showPreview ? 1 : 0, GridUnitType.Pixel));
        rows[2] = new RowDefinition(
            !vertical && showPreview ? new GridLength(1, GridUnitType.Star) : new GridLength(0))
        {
            // 预览行的最小高度必须跟随可见性。
            // Grid 的行即使 Height 为 0，只要 MinHeight > 0 仍会被撑开——
            // 之前无条件写 120，导致左右分栏时底部多出一条 120px 的空白。
            MinHeight = !vertical && showPreview ? 120 : 0,
        };

        if (vertical)
        {
            Place(EditorHost, column: 0, row: 0);
            Place(Splitter, column: 1, row: 0);
            Place(PreviewHost, column: 2, row: 0);
            Splitter.ResizeDirection = GridResizeDirection.Columns;
            Splitter.Cursor = new Cursor(StandardCursorType.SizeWestEast);
        }
        else
        {
            Place(EditorHost, column: 0, row: 0);
            Place(Splitter, column: 0, row: 1);
            Place(PreviewHost, column: 0, row: 2);
            Splitter.ResizeDirection = GridResizeDirection.Rows;
            Splitter.Cursor = new Cursor(StandardCursorType.SizeNorthSouth);
        }

        Splitter.IsVisible = showPreview;
    }

    private static void Place(Control control, int column, int row)
    {
        Grid.SetColumn(control, column);
        Grid.SetRow(control, row);
        Grid.SetColumnSpan(control, 1);
        Grid.SetRowSpan(control, 1);
    }

    // ---------- 编辑事件 ----------

    private void OnEditorTextChanged(object? sender, EventArgs e)
    {
        if (_suppressEditorEvents)
            return;

        _attachedDocument?.OnTextChanged(Editor.Text);
        if (_attachedDocument is not null)
        {
            // TextDocument 是文档自己的，撤销栈天然隔离；
            // 这里同步一次脏标记（TextDocument 的变化不一定经过 OnTextChanged 的所有路径）。
            _attachedDocument.IsModified = !_attachedDocument.Document.UndoStack.IsOriginalFile;
        }
    }

    private void OnCaretPositionChanged(object? sender, EventArgs e)
    {
        var caret = Editor.TextArea.Caret;
        _attachedDocument?.OnCaretChanged(caret.Line, caret.Column, Editor.SelectionLength);
    }

    /// <summary>
    /// 窗口级隧道处理器，专门截获 AvaloniaEdit 会自行消费的组合键。
    ///
    /// 为什么需要它在窗口上而不是编辑器上：AvaloniaEdit 的 TextArea 在
    /// 隧道阶段就处理了 Ctrl+H（历史上用于"替换"），编辑器级的处理器收不到。
    /// 窗口作为隧道起点会更早拿到事件（已用日志确认编辑器级处理器未被调用）。
    /// </summary>
    /// <summary>
    /// 窗口级隧道处理器，专门截获 AvaloniaEdit 会自行消费的组合键。
    ///
    /// 为什么放在窗口而不是编辑器上：AvaloniaEdit 的 TextArea 在隧道阶段就处理了 Ctrl+H，
    /// 编辑器级的处理器收不到（已用日志确认）。
    /// </summary>
    private void OnWindowPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.Control)
            return;

        // 只处理编辑器内的替换；焦点在菜单或对话框时不抢键。
        if (e.Key == Key.H && Editor.IsKeyboardFocusWithin)
        {
            ShowSearchPanel(replaceMode: true);
            e.Handled = true;
        }
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
        // Ctrl+F：打开编辑器内置的查找面板。
        // AvaloniaEdit 虽然通过 SearchPanel.Install 绑定了 ApplicationCommands.Find，
        // 但命令需要从 TextArea 内部路由；窗口级快捷键会先被拦下，因此显式处理。
        if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.F)
        {
            ShowSearchPanel(replaceMode: false);
            e.Handled = true;
            return;
        }

        if (e.KeyModifiers != KeyModifiers.Control)
            return;

        switch (e.Key)
        {
            case Key.B:
                WrapSelection("**", "**", "粗体文本");
                e.Handled = true;
                break;
            case Key.I:
                WrapSelection("*", "*", "斜体文本");
                e.Handled = true;
                break;
            case Key.K:
                WrapSelection("[", "](https://)", "链接文字");
                e.Handled = true;
                break;
            case Key.OemTilde:
                WrapSelection("`", "`", "code");
                e.Handled = true;
                break;
        }
    }

    /// <summary>打开（或重新聚焦）编辑器查找面板。</summary>
    private void ShowSearchPanel(bool replaceMode = false)
    {
        // SearchPanel 由 TextEditor.OnApplyTemplate 安装；模板未应用时才可能为 null。
        var panel = Editor.SearchPanel;
        if (panel is null)
            return;

        if (panel.IsClosed)
            panel.Open();

        panel.IsReplaceMode = replaceMode;
        panel.Reactivate();   // 聚焦输入框并全选，便于直接覆盖上次的搜索词
    }

    /// <summary>用前后缀包裹选区；无选区时插入占位文本并选中它。</summary>
    private void WrapSelection(string prefix, string suffix, string placeholder)
    {
        var text = Editor.SelectedText;
        var start = Editor.SelectionStart;

        if (string.IsNullOrEmpty(text))
        {
            Editor.Document.Insert(start, prefix + placeholder + suffix);
            Editor.Select(start + prefix.Length, placeholder.Length);
        }
        else
        {
            Editor.Document.Replace(start, text.Length, prefix + text + suffix);
            Editor.Select(start + prefix.Length, text.Length);
        }

        Editor.TextArea.Focus();
    }

    private void OnBoldClick(object? sender, RoutedEventArgs e) => WrapSelection("**", "**", "粗体文本");
    private void OnReplaceClick(object? sender, RoutedEventArgs e)
    {
        Editor.TextArea.Focus();
        ShowSearchPanel(replaceMode: true);
    }    private void OnItalicClick(object? sender, RoutedEventArgs e) => WrapSelection("*", "*", "斜体文本");
    private void OnLinkClick(object? sender, RoutedEventArgs e) => WrapSelection("[", "](https://)", "链接文字");
    private void OnCodeClick(object? sender, RoutedEventArgs e) => WrapSelection("`", "`", "code");

    // ---------- 滚动同步 ----------

    private ScrollViewer? PreviewScroll
    {
        get
        {
            // 预览模板内可能重建 ScrollViewer，因此在失效时重新查找。
            if (_previewScroll is null || !_previewScroll.IsAttachedToVisualTree())
                _previewScroll = Preview.GetVisualDescendants().OfType<ScrollViewer>().FirstOrDefault();
            return _previewScroll;
        }
    }

    private static double Ratio(double offset, double extent, double viewport)
    {
        var max = extent - viewport;
        return max <= 0 ? 0 : Math.Clamp(offset / max, 0, 1);
    }

    private void OnEditorScrollOffsetChanged(object? sender, EventArgs e)
    {
        if (_syncingScroll || _viewModel?.IsPreviewVisible != true)
            return;

        var preview = PreviewScroll;
        if (preview is null)
            return;

        // 注意：不能用 TextView.DocumentHeight 计算比例——它只覆盖已构建的可视行，
        // 真实可滚动范围在 TextEditor 的外层 ScrollViewer 上。
        var ratio = Ratio(Editor.VerticalOffset, Editor.ExtentHeight, Editor.ViewportHeight);
        _lastEditorRatio = ratio;

        SyncScroll(preview, ratio);
    }

    private void OnPreviewScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        if (_syncingScroll || _viewModel?.IsPreviewVisible != true)
            return;

        if (e.ExtentDelta != default)
            return;

        var preview = PreviewScroll;
        if (preview is null)
            return;

        var ratio = Ratio(preview.Offset.Y, preview.Extent.Height, preview.Viewport.Height);
        _lastEditorRatio = ratio;

        _syncingScroll = true;
        try
        {
            ScrollEditorTo(ratio * Math.Max(0, Editor.ExtentHeight - Editor.ViewportHeight));
        }
        finally
        {
            _syncingScroll = false;
        }
    }

    /// <summary>
    /// 让编辑器滚动到指定像素偏移。
    /// AvaloniaEdit 的 TextView 是逻辑滚动源，直接改 ScrollViewer.Offset 会被它覆盖，
    /// 因此必须通过 IScrollable 设置 TextView 自己的偏移。
    /// </summary>
    private void ScrollEditorTo(double offset)
    {
        var textView = Editor.TextArea.TextView;
        ((IScrollable)textView).Offset = new Vector(textView.ScrollOffset.X, Math.Max(0, offset));
    }

    private void SyncScroll(ScrollViewer preview, double ratio)
    {
        var previewMax = preview.Extent.Height - preview.Viewport.Height;
        if (previewMax <= 0)
            return;

        _syncingScroll = true;
        try
        {
            preview.Offset = preview.Offset.WithY(ratio * previewMax);
        }
        finally
        {
            _syncingScroll = false;
        }
    }

    /// <summary>预览重新渲染后，把滚动位置恢复到与编辑器一致的比例。</summary>
    private void OnLayoutUpdated(object? sender, EventArgs e)
    {
        // 预览的 ScrollViewer 在模板应用后才创建，这里兜底挂接一次。
        HookPreviewScroll();

        if (!_restoringPreviewScroll)
            return;

        var preview = PreviewScroll;
        if (preview is null || preview.Extent.Height <= 0)
            return;

        _restoringPreviewScroll = false;

        var previewMax = preview.Extent.Height - preview.Viewport.Height;
        if (previewMax <= 0)
            return;

        _syncingScroll = true;
        try
        {
            preview.Offset = preview.Offset.WithY(_lastEditorRatio * previewMax);
        }
        finally
        {
            _syncingScroll = false;
        }
    }

    // ---------- IDocumentDialogService ----------

    public async Task<string?> PickOpenFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "打开 Markdown 文档",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Markdown")
                {
                    Patterns = DocumentFormats.PickerPatterns.Split(';'),
                    MimeTypes = ["text/markdown"],
                },
                FilePickerFileTypes.All,
            ],
        });

        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    public async Task<string?> PickSaveFileAsync(string? suggestedPath)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "保存 Markdown 文档",
            SuggestedFileName = suggestedPath is null ? "未命名.md" : Path.GetFileName(suggestedPath),
            DefaultExtension = "md",
            ShowOverwritePrompt = true,
            FileTypeChoices =
            [
                new FilePickerFileType("Markdown") { Patterns = ["*.md"], MimeTypes = ["text/markdown"] },
            ],
        });

        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickExportHtmlPathAsync(string? suggestedPath)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出为 HTML",
            SuggestedFileName = suggestedPath ?? "document.html",
            DefaultExtension = "html",
            ShowOverwritePrompt = true,
            FileTypeChoices =
            [
                new FilePickerFileType("HTML") { Patterns = ["*.html", "*.htm"], MimeTypes = ["text/html"] },
            ],
        });

        return file?.TryGetLocalPath();
    }

    public async Task<string?> PickExportPdfPathAsync(string? suggestedPath)
    {
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "导出为 PDF",
            SuggestedFileName = suggestedPath ?? "document.pdf",
            DefaultExtension = "pdf",
            ShowOverwritePrompt = true,
            FileTypeChoices =
            [
                new FilePickerFileType("PDF") { Patterns = ["*.pdf"], MimeTypes = ["application/pdf"] },
            ],
        });

        return file?.TryGetLocalPath();
    }

    public Task ShowMessageAsync(string title, string message)
    {
        var tcs = new TaskCompletionSource();

        var dialog = new Window
        {
            Title = title,
            Width = 480,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var ok = new Button { Content = "好", MinWidth = 88, IsDefault = true, IsCancel = true };
        ok.Click += (_, _) => dialog.Close();
        dialog.Closed += (_, _) => tcs.TrySetResult();

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Children =
            {
                new TextBlock
                {
                    Text = message,
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                    Margin = new Thickness(0, 0, 0, 16),
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Children = { ok },
                },
            },
        };

        dialog.ShowDialog(this);
        return tcs.Task;
    }

    public Task<bool> ConfirmDiscardAsync(string documentName)
    {
        var tcs = new TaskCompletionSource<bool>();

        var dialog = new Window
        {
            Title = "未保存的修改",
            Width = 420,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };

        var message = new TextBlock
        {
            Text = $"「{documentName}」有未保存的修改，要放弃这些修改吗？",
            TextWrapping = Avalonia.Media.TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 16),
        };

        var discard = new Button { Content = "放弃修改", MinWidth = 88 };
        var cancel = new Button { Content = "取消", MinWidth = 88, IsCancel = true };

        discard.Click += (_, _) => { tcs.TrySetResult(true); dialog.Close(); };
        cancel.Click += (_, _) => { tcs.TrySetResult(false); dialog.Close(); };
        dialog.Closed += (_, _) => tcs.TrySetResult(false);

        dialog.Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 0,
            Children =
            {
                message,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Children = { discard, cancel },
                },
            },
        };

        dialog.ShowDialog(this);
        return tcs.Task;
    }

    // ---------- 拖放打开 ----------

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var hasMarkdown = e.DataTransfer.TryGetFiles()?
            .Any(f => f.TryGetLocalPath() is { } p
                      && Services.DocumentFormats.IsSupported(p)) == true;

        e.DragEffects = hasMarkdown ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        var path = e.DataTransfer.TryGetFiles()?
            .Select(f => f.TryGetLocalPath())
            .FirstOrDefault(p => p is not null && Services.DocumentFormats.IsSupported(p));

        if (path is null || _viewModel is null)
            return;

        e.Handled = true;
        // 拖放打开为新标签，不影响当前文档，因此无需确认丢弃。
        await _viewModel.OpenPathAsync(path);
    }

    // ---------- 关闭确认 ----------

    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        if (_viewModel is { } vm)
        {
            if (!await vm.ConfirmCloseAllAsync())
            {
                e.Cancel = true;
                return;
            }

            vm.DisposeDocuments();
        }

        base.OnClosing(e);
    }
}
