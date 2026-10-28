using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;

namespace Markify.ViewModels;

/// <summary>
/// 目录树中的一个节点（文件夹或 Markdown 文件）。
///
/// 设计要点：
///   - **懒加载**：只在展开时才枚举子目录。整个磁盘或大仓库一次性递归会在打开文件夹时卡住，
///     而且用户多数时候只看一两个子目录。
///   - **过滤**：文件夹里只显示目录与受支持的 Markdown 文件，其余（.git、node_modules、图片等）
///     默认不显示，避免树里全是噪音。
///   - **容错**：无权限的目录、枚举中途被删除的目录都会抛出异常；这里吞掉并标记为空，
///     不能让一个坏目录拖垮整棵树。
/// </summary>
public partial class FileTreeNode : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    /// <summary>单层最多枚举的条目数，防止误选根目录时把界面拖死。</summary>
    private const int MaxEntriesPerDirectory = 2000;

    private static readonly string[] DirectorySkipList =
    [
        ".git", ".svn", ".hg", "node_modules", "bin", "obj", ".vs", ".idea", ".vscode",
    ];

    private bool _childrenLoaded;
    private bool _isExpanded;

    private FileTreeNode(string fullPath, string name, bool isDirectory)
    {
        FullPath = fullPath;
        Name = name;
        IsDirectory = isDirectory;
    }

    public string FullPath { get; }

    public string Name { get; }

    public bool IsDirectory { get; }

    /// <summary>文件节点的扩展名标记，供界面显示不同图标（当前仅用于区分 md 与其他）。</summary>
    public bool IsMarkdown =>
        !IsDirectory && string.Equals(Path.GetExtension(FullPath), ".md", StringComparison.OrdinalIgnoreCase);

    public ObservableCollection<FileTreeNode> Children { get; } = [];

    /// <summary>展开时是否加载子节点；界面双向绑定。</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!SetProperty(ref _isExpanded, value))
                return;

            if (value)
                EnsureChildrenLoaded();
        }
    }

    public static FileTreeNode ForDirectory(string path) =>
        new(path, Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)) is { Length: > 0 } n ? n : path,
            isDirectory: true);

    public static FileTreeNode ForFile(string path) =>
        new(path, Path.GetFileName(path), isDirectory: false);

    /// <summary>加载直接子节点（幂等）。文件夹优先，同类按名称排序。</summary>
    public void EnsureChildrenLoaded()
    {
        if (_childrenLoaded || !IsDirectory)
            return;

        _childrenLoaded = true;
        Children.Clear();

        foreach (var child in EnumerateChildren(FullPath))
            Children.Add(child);
    }

    /// <summary>
    /// 枚举一层子节点。抽成静态方法便于单测直接验证过滤与排序规则，而不必构造整棵树。
    /// </summary>
    internal static IEnumerable<FileTreeNode> EnumerateChildren(string directory, Func<string, bool>? fileFilter = null)
    {
        string[] directories;
        string[] files;

        try
        {
            directories = Directory.GetDirectories(directory);
            files = Directory.GetFiles(directory);
        }
        catch
        {
            // 无权限 / 目录已删除 / 路径过长：当作空目录，不中断整棵树
            return [];
        }

        var result = new List<FileTreeNode>();

        foreach (var dir in directories
                     .Where(d => !DirectorySkipList.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
                     .OrderBy(d => Path.GetFileName(d), StringComparer.CurrentCultureIgnoreCase)
                     .Take(MaxEntriesPerDirectory))
        {
            result.Add(ForDirectory(dir));
        }

        var nameFilter = fileFilter ?? IsMarkdownDocument;

        foreach (var file in files
                     .Where(f => nameFilter(f))
                     .OrderBy(f => Path.GetFileName(f), StringComparer.CurrentCultureIgnoreCase)
                     .Take(MaxEntriesPerDirectory))
        {
            result.Add(ForFile(file));
        }

        return result;
    }

    /// <summary>目录树只显示 Markdown 与纯文本，其它文件不进入树。</summary>
    public static bool IsMarkdownDocument(string path) => Services.DocumentFormats.IsSupported(path);
}
