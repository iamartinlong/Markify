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
}
