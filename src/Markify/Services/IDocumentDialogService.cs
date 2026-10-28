using System.Threading.Tasks;

namespace Markify.Services;

/// <summary>
/// 由主窗口实现的对话框抽象，让 ViewModel 不依赖具体窗口实现。
/// </summary>
public interface IDocumentDialogService
{
    /// <summary>选择要打开的 Markdown 文件，取消时返回 null。</summary>
    Task<string?> PickOpenFileAsync();

    /// <summary>选择保存路径，取消时返回 null。</summary>
    Task<string?> PickSaveFileAsync(string? suggestedPath);

    /// <summary>询问是否丢弃未保存修改，返回 true 表示可以继续。</summary>
    Task<bool> ConfirmDiscardAsync(string documentName);

    /// <summary>选择 HTML 导出的目标路径，取消时返回 null。</summary>
    Task<string?> PickExportHtmlPathAsync(string? suggestedPath);

    /// <summary>选择 PDF 导出的目标路径，取消时返回 null。</summary>
    Task<string?> PickExportPdfPathAsync(string? suggestedPath);

    /// <summary>导出完成后的提示（失败时用于显示原因）。</summary>
    Task ShowMessageAsync(string title, string message);

    /// <summary>把文本写入系统剪贴板。</summary>
    Task SetClipboardTextAsync(string text);
}
