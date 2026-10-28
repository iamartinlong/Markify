using System.Text;
using Markify.Services;
using Markify.ViewModels;
using Xunit;

namespace Markify.Tests;

/// <summary>
/// 标签右键菜单的批量关闭。
///
/// 这类"按位置批量删除"的代码最容易出现索引错位（边遍历边删集合），
/// 因此对左侧/右侧/其他三种都单独验证结果集合。
/// </summary>
public class TabBatchCloseTests
{
    /// <summary>建一个含指定数量文档的工作区，返回便于断言的名称列表。</summary>
    private static MainViewModel WorkspaceWith(int count)
    {
        var vm = new MainViewModel();
        for (var i = 1; i < count; i++)
            vm.NewDocumentCommand.Execute(null);

        return vm;
    }

    private static string[] Names(MainViewModel vm) =>
        vm.Documents.Select(d => d.DocumentName).ToArray();

    [Fact]
    public async Task CloseTabsToLeft_RemovesOnlyLeftNeighbours()
    {
        var vm = WorkspaceWith(4);
        var target = vm.Documents[2];
        var keep = Names(vm)[2..];

        await vm.CloseTabsToLeftCommand.ExecuteAsync(target);

        Assert.Equal(keep, Names(vm));
        Assert.Contains(target, vm.Documents);
    }

    [Fact]
    public async Task CloseTabsToRight_RemovesOnlyRightNeighbours()
    {
        var vm = WorkspaceWith(4);
        var target = vm.Documents[1];
        var keep = Names(vm)[..2];

        await vm.CloseTabsToRightCommand.ExecuteAsync(target);

        Assert.Equal(keep, Names(vm));
    }

    [Fact]
    public async Task CloseOtherTabs_LeavesOnlyTarget()
    {
        var vm = WorkspaceWith(4);
        var target = vm.Documents[2];
        var name = target.DocumentName;

        await vm.CloseOtherTabsCommand.ExecuteAsync(target);

        Assert.Single(vm.Documents);
        Assert.Equal(name, vm.Documents[0].DocumentName);
        Assert.Same(target, vm.ActiveDocument);
    }

    [Fact]
    public async Task CloseTabsToLeft_OnFirstTab_IsNoOp()
    {
        var vm = WorkspaceWith(3);
        var before = Names(vm);

        await vm.CloseTabsToLeftCommand.ExecuteAsync(vm.Documents[0]);

        Assert.Equal(before, Names(vm));
    }

    [Fact]
    public async Task CloseTabsToRight_OnLastTab_IsNoOp()
    {
        var vm = WorkspaceWith(3);
        var before = Names(vm);

        await vm.CloseTabsToRightCommand.ExecuteAsync(vm.Documents[^1]);

        Assert.Equal(before, Names(vm));
    }

    [Fact]
    public async Task CloseOtherTabs_KeepsActiveDocumentValid()
    {
        var vm = WorkspaceWith(3);
        vm.ActivateDocumentCommand.Execute(vm.Documents[0]);

        // 关闭第一个标签以外的所有标签，活动文档应当仍然有效
        await vm.CloseOtherTabsCommand.ExecuteAsync(vm.Documents[0]);

        Assert.NotNull(vm.ActiveDocument);
        Assert.Contains(vm.ActiveDocument!, vm.Documents);
    }

    [Fact]
    public async Task BatchClose_DoesNotLeaveCollectionInInconsistentState()
    {
        var vm = WorkspaceWith(6);
        var target = vm.Documents[3];

        await vm.CloseTabsToRightCommand.ExecuteAsync(target);
        await vm.CloseTabsToLeftCommand.ExecuteAsync(target);

        // 三种批量关叠加后只剩目标一个
        Assert.Single(vm.Documents);
        Assert.False(vm.CanCloseTab);   // 只剩一个时不显示关闭按钮
    }

    [Fact]
    public void TabToolTip_ForSavedDocument_IsFullPath()
    {
        var path = Path.Combine(Path.GetTempPath(), "markify-batch-" + Guid.NewGuid().ToString("N") + ".md");
        var doc = new DocumentViewModel(new DocumentFileService(), "# x", path, new UTF8Encoding(false));

        Assert.Equal(path, doc.TabToolTip);
    }
}
