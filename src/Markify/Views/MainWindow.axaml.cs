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
    private double _lastEditorRatio;
    private MainViewModel? _viewModel;

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
            _viewModel.DocumentLoaded -= OnDocumentLoaded;
            _viewModel.HeadingNavigationRequested -= OnHeadingNavigationRequested;
        }

        _viewModel = DataContext as MainViewModel;
        if (_viewModel is null)
            return;

        _viewModel.Dialogs = this;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.DocumentLoaded += OnDocumentLoaded;
        _viewModel.HeadingNavigationRequested += OnHeadingNavigationRequested;

        if (!string.IsNullOrEmpty(_viewModel.SourceText))
            SetEditorText(_viewModel.SourceText);
    }

    private void OnDocumentLoaded(object? sender, EventArgs e)
    {
        if (_viewModel is null)
            return;

        SetEditorText(_viewModel.SourceText);
        Editor.CaretOffset = 0;
        Editor.ScrollToHome();
    }

    // ---------- 大纲导航 ----------

    private void OnOutlineSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_viewModel is null || OutlineList.SelectedItem is not MarkdownHeading heading)
            return;

        NavigateToHeading(heading);
    }

    private void OnHeadingNavigationRequested(object? sender, MarkdownHeading heading) => NavigateToHeading(heading);

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

    private void SetEditorText(string text)
    {
        if (Editor.Text == text)
            return;

        Editor.Text = text;
        Editor.Document.UndoStack.ClearAll();
        Editor.Document.UndoStack.MarkAsOriginalFile();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsDarkTheme):
                ApplyEditorTheme(_viewModel?.IsDarkTheme == true);
                break;
            case nameof(MainViewModel.IsWordWrap):
            case nameof(MainViewModel.ShowLineNumbers):
                break;
            case nameof(MainViewModel.PreviewMarkdown):
                // MarkdownViewer 重新渲染会把滚动位置归零，稍后恢复。
                _restoringPreviewScroll = true;
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
            MinHeight = vertical ? 120 : 120,
        };
        rows[1] = new RowDefinition(
            new GridLength(!vertical && showPreview ? 1 : 0, GridUnitType.Pixel));
        rows[2] = new RowDefinition(
            !vertical && showPreview ? new GridLength(1, GridUnitType.Star) : new GridLength(0))
        {
            MinHeight = 120,
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
        _viewModel?.OnEditorTextChanged(Editor.Text);
    }

    private void OnCaretPositionChanged(object? sender, EventArgs e)
    {
        var caret = Editor.TextArea.Caret;
        _viewModel?.OnCaretChanged(caret.Line, caret.Column, Editor.SelectionLength);
    }

    private void OnEditorKeyDown(object? sender, KeyEventArgs e)
    {
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
    private void OnItalicClick(object? sender, RoutedEventArgs e) => WrapSelection("*", "*", "斜体文本");
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
        if (!await _viewModel.ConfirmDiscardAsync())
            return;

        await _viewModel.LoadAsync(path);
    }

    // ---------- 关闭确认 ----------

    private void OnExitClick(object? sender, RoutedEventArgs e) => Close();

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        if (_viewModel is { IsModified: true } vm)
        {
            e.Cancel = true;
            if (!await vm.ConfirmDiscardAsync())
                return;

            vm.IsModified = false;
            Dispatcher.UIThread.Post(Close);
            return;
        }

        base.OnClosing(e);
    }
}
