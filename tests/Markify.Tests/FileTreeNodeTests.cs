using Markify.ViewModels;
using Xunit;

namespace Markify.Tests;

/// <summary>
/// 目录树模型。
///
/// 重点是过滤规则与容错：树里只该出现目录和 Markdown 文件，
/// 而一个无权限或已被删除的子目录不能拖垮整个文件夹的加载。
/// </summary>
public class FileTreeNodeTests : IDisposable
{
    private readonly string _root;

    public FileTreeNodeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "markify-tree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
        GC.SuppressFinalize(this);
    }

    private void Touch(string relativePath, string content = "# x")
    {
        var full = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [Fact]
    public void EnumerateChildren_ListsDirectoriesBeforeFiles()
    {
        Directory.CreateDirectory(Path.Combine(_root, "zdir"));
        Touch("afile.md");

        var children = FileTreeNode.EnumerateChildren(_root).ToList();

        Assert.Equal(2, children.Count);
        Assert.True(children[0].IsDirectory);
        Assert.False(children[1].IsDirectory);
    }

    [Fact]
    public void EnumerateChildren_FiltersOutNonMarkdownFiles()
    {
        Touch("note.md");
        Touch("image.png");
        Touch("script.ps1");
        Touch("data.json");

        var names = FileTreeNode.EnumerateChildren(_root).Select(n => n.Name).ToList();

        // 只保留受支持的文档类型
        Assert.Contains("note.md", names);
        Assert.DoesNotContain("image.png", names);
        Assert.DoesNotContain("script.ps1", names);
        Assert.DoesNotContain("data.json", names);
    }

    [Fact]
    public void EnumerateChildren_AcceptsTxtAsWell()
    {
        // DocumentFormats 把 .txt 也算作可编辑文档
        Touch("readme.txt");

        var names = FileTreeNode.EnumerateChildren(_root).Select(n => n.Name).ToList();

        Assert.Contains("readme.txt", names);
    }

    [Fact]
    public void EnumerateChildren_SkipsNoiseDirectories()
    {
        // 这些目录出现在树里只会是噪音，而且往往极大
        foreach (var noise in new[] { ".git", "node_modules", "bin", "obj", ".vs" })
            Directory.CreateDirectory(Path.Combine(_root, noise));

        Directory.CreateDirectory(Path.Combine(_root, "src"));

        var names = FileTreeNode.EnumerateChildren(_root).Select(n => n.Name).ToList();

        Assert.Contains("src", names);
        Assert.DoesNotContain(".git", names);
        Assert.DoesNotContain("node_modules", names);
        Assert.DoesNotContain("bin", names);
        Assert.DoesNotContain("obj", names);
    }

    [Fact]
    public void EnumerateChildren_ReturnsEmpty_ForMissingDirectory()
    {
        // 不抛异常：调用方是 UI，一次异常会让整棵树加载失败
        var children = FileTreeNode.EnumerateChildren(Path.Combine(_root, "does-not-exist"));

        Assert.Empty(children);
    }

    [Fact]
    public void EnumerateChildren_SortsByNameIgnoringCase()
    {
        Touch("b.md");
        Touch("A.md");
        Touch("c.md");

        var names = FileTreeNode.EnumerateChildren(_root).Select(n => n.Name).ToList();

        Assert.Equal(["A.md", "b.md", "c.md"], names);
    }

    [Fact]
    public void IsMarkdown_TrueForMarkdownFilesOnly()
    {
        Touch("a.md");
        Touch("b.txt");

        var md = FileTreeNode.ForFile(Path.Combine(_root, "a.md"));
        var txt = FileTreeNode.ForFile(Path.Combine(_root, "b.txt"));
        var dir = FileTreeNode.ForDirectory(_root);

        Assert.True(md.IsMarkdown);
        Assert.False(txt.IsMarkdown);
        Assert.False(dir.IsMarkdown);
    }

    [Fact]
    public void EnsureChildrenLoaded_IsIdempotent()
    {
        Touch("a.md");
        var dir = FileTreeNode.ForDirectory(_root);

        dir.EnsureChildrenLoaded();
        var first = dir.Children.Count;
        dir.EnsureChildrenLoaded();

        // 不应重复追加
        Assert.Equal(first, dir.Children.Count);
    }

    [Fact]
    public void EnsureChildrenLoaded_LazyLoadsNestedDirectories()
    {
        Touch(Path.Combine("sub", "deep.md"));
        var dir = FileTreeNode.ForDirectory(_root);

        // 加载前：只有一个子目录节点，且它自身还没有子节点
        dir.EnsureChildrenLoaded();
        var sub = Assert.Single(dir.Children);
        Assert.True(sub.IsDirectory);
        Assert.Empty(sub.Children);

        // 展开后才加载下一层
        sub.IsExpanded = true;
        Assert.Single(sub.Children);
        Assert.Equal("deep.md", sub.Children[0].Name);
    }

    [Fact]
    public void SetFolder_PopulatesRootAndPath()
    {
        Touch("doc.md");

        var vm = new MainViewModel();
        vm.SetFolder(_root);

        Assert.Equal(_root, vm.FolderPath);
        Assert.NotNull(vm.FolderRoot);
        Assert.True(vm.IsFileTreeActive);
        Assert.Single(vm.FolderRoot!.Children);
    }

    [Fact]
    public void SetFolder_RejectsMissingPath_WithoutThrowing()
    {
        var vm = new MainViewModel();
        vm.SetFolder(Path.Combine(_root, "nope"));

        Assert.Null(vm.FolderRoot);
        Assert.Contains("不存在", vm.StatusMessage);
    }

    [Fact]
    public void CloseFolder_ClearsState()
    {
        Touch("doc.md");
        var vm = new MainViewModel();
        vm.SetFolder(_root);

        vm.CloseFolderCommand.Execute(null);

        Assert.Null(vm.FolderRoot);
        Assert.Null(vm.FolderPath);
    }
}
