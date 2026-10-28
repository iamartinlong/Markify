using System.Text;
using Markify.Services;
using Markify.ViewModels;
using Xunit;

namespace Markify.Tests;

/// <summary>
/// 工作区（多标签）逻辑。
/// 这里最容易出两类问题：标签间内容串写、关闭最后一个标签后的状态异常。
/// </summary>
public class WorkspaceTests
{
    private static DocumentViewModel NewDoc(string text = "content", string? path = null) =>
        new(new DocumentFileService(), text, path, new UTF8Encoding(false));

    [Fact]
    public void NewDocument_AssignsDistinctNames()
    {
        // MainViewModel 构造时已创建一个示例文档，因此从这里开始计数
        var vm = new MainViewModel();
        var names = new List<string> { vm.Documents[0].DocumentName };

        vm.NewDocumentCommand.Execute(null);
        vm.NewDocumentCommand.Execute(null);

        names.AddRange(vm.Documents.Skip(1).Select(d => d.DocumentName));

        Assert.Equal(3, names.Count);
        Assert.Equal(names.Distinct().Count(), names.Count);   // 每个标签名唯一
    }

    [Fact]
    public void NewDocument_BecomesActive()
    {
        var vm = new MainViewModel();
        var first = vm.ActiveDocument;

        vm.NewDocumentCommand.Execute(null);

        Assert.NotSame(first, vm.ActiveDocument);
        Assert.Same(vm.Documents[^1], vm.ActiveDocument);
        Assert.True(vm.ActiveDocument!.IsActive);
        Assert.False(first!.IsActive);
    }

    [Fact]
    public void Documents_HaveIndependentDocumentsAndModificationState()
    {
        var vm = new MainViewModel();
        vm.NewDocumentCommand.Execute(null);

        var first = vm.Documents[0];
        var second = vm.Documents[1];

        // 两个文档的 TextDocument 必须是不同实例，否则撤销栈会互相污染
        Assert.NotSame(first.Document, second.Document);

        second.OnTextChanged("changed", markModified: true);

        Assert.True(second.IsModified);
        Assert.False(first.IsModified);
        Assert.NotEqual(first.SourceText, second.SourceText);
    }

    [Fact]
    public void WindowTitle_TracksActiveDocument()
    {
        var vm = new MainViewModel();
        vm.NewDocumentCommand.Execute(null);

        var initialTitle = vm.WindowTitle;
        Assert.Contains(vm.ActiveDocument!.DocumentName, initialTitle);
        Assert.DoesNotContain("*", initialTitle);      // 新文档未修改

        vm.ActiveDocument.OnTextChanged("dirty", markModified: true);

        Assert.Contains("*", vm.WindowTitle);
    }

    [Fact]
    public async Task CloseLastDocument_IsRejected()
    {
        var vm = new MainViewModel();
        Assert.Single(vm.Documents);

        await vm.CloseTabCommand.ExecuteAsync(vm.Documents[0]);

        // 最后一个标签不允许关闭，避免留下空窗口
        Assert.Single(vm.Documents);
    }

    [Fact]
    public async Task CloseTab_ActivatesNeighbour()
    {
        var vm = new MainViewModel();
        vm.NewDocumentCommand.Execute(null);
        vm.NewDocumentCommand.Execute(null);

        var middle = vm.Documents[1];
        vm.ActivateDocumentCommand.Execute(middle);
        Assert.Same(middle, vm.ActiveDocument);

        await vm.CloseTabCommand.ExecuteAsync(middle);

        Assert.DoesNotContain(middle, vm.Documents);
        Assert.NotNull(vm.ActiveDocument);
        Assert.Contains(vm.ActiveDocument!, vm.Documents);
    }

    [Fact]
    public void CycleTab_WrapsAround()
    {
        var vm = new MainViewModel();
        vm.NewDocumentCommand.Execute(null);
        vm.NewDocumentCommand.Execute(null);

        var first = vm.Documents[0];
        var last = vm.Documents[^1];

        vm.ActivateDocumentCommand.Execute(first);
        vm.PreviousTabCommand.Execute(null);         // 从第一个往前应回绕到最后一个
        Assert.Same(last, vm.ActiveDocument);

        vm.NextTabCommand.Execute(null);             // 从最后一个往后应回绕到第一个
        Assert.Same(first, vm.ActiveDocument);
    }

    [Fact]
    public void SingleDocument_CannotCycle()
    {
        var vm = new MainViewModel();
        var only = vm.ActiveDocument;

        vm.NextTabCommand.Execute(null);

        Assert.Same(only, vm.ActiveDocument);
        Assert.False(vm.CanCloseTab);   // 只剩一个时不显示关闭按钮
    }

    [Fact]
    public void Document_TabToolTip_ShowsFullPathWhenSaved()
    {
        var path = Path.Combine(Path.GetTempPath(), "markify-tip-" + Guid.NewGuid().ToString("N") + ".md");
        var doc = NewDoc("# x", path);

        // 悬浮提示要给出完整路径，而不是仅文件名
        Assert.Equal(path, doc.TabToolTip);
    }

    [Fact]
    public void Document_TabToolTip_ExplainsWhenUnsaved()
    {
        var doc = NewDoc("# x", path: null);

        // 未保存的新文档没有路径，提示应说明原因而不是显示空白
        Assert.Contains("尚未保存", doc.TabToolTip);
        Assert.DoesNotContain(doc.TabToolTip, Path.DirectorySeparatorChar.ToString());
    }

    [Fact]
    public void Document_TabToolTip_FollowsSaveAs()
    {
        var doc = NewDoc("# x", path: null);
        var newPath = Path.Combine(Path.GetTempPath(), "markify-tip2-" + Guid.NewGuid().ToString("N") + ".md");

        doc.FilePath = newPath;

        Assert.Equal(newPath, doc.TabToolTip);
    }

    [Fact]
    public void Document_CountWords_MatchesLegacyBehaviour()
    {
        // 与重构前的统计口径保持一致：中文按字、英文按词
        Assert.Equal(3, DocumentViewModel.CountWords("你好 world"));
        Assert.Equal(0, DocumentViewModel.CountWords(""));
    }
}
