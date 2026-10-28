using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Markify.Services;

/// <summary>
/// 监听当前文档在磁盘上的变化（其它编辑器/工具修改了同一文件）。
///
/// 三个必须处理的现实问题：
///   1. 一次保存会触发多个事件（Changed/Created/Renamed），需要用防抖合并；
///   2. 本应用自己保存时也会触发，必须能区分并忽略，否则会提示"文件被外部修改"；
///   3. FileSystemWatcher 在文件被短暂替换（编辑器惯用的"写临时文件再 Move"）时会抛
///      IOException / 短暂读不到文件，需要重试而不是崩溃。
/// </summary>
public sealed class FileChangeWatcher : IDisposable
{
    private readonly TimeSpan _debounce;
    private FileSystemWatcher? _watcher;
    private Timer? _timer;
    private string? _path;
    /// <summary>
    /// 最近一次"自身写入"的结束时刻。
    ///
    /// 用时间窗口而不是布尔开关：布尔量只能表达"正在写"，而文件系统事件在写入**之后**
    /// 才到达，需要一个短暂的宽限期。若用布尔量 + 固定延迟解除，宽限期内发生的
    /// **真实外部修改**会被误吞（审计发现的缺陷：保存后立刻被外部工具修改，那次修改会丢失）。
    /// 记录时间戳可以做到"只忽略紧接着自身写入的事件"，更晚的事件照常上报。
    /// </summary>
    private long _ownWriteTicks;

    /// <summary>自身写入之后允许文件事件迟到的宽限时间。</summary>
    private static readonly TimeSpan OwnWriteGrace = TimeSpan.FromMilliseconds(500);

    /// <summary>当前是否仍处于自身写入的宽限窗口内。</summary>
    private bool InOwnWriteGrace
    {
        get
        {
            var ticks = Interlocked.Read(ref _ownWriteTicks);
            if (ticks == 0)
                return false;

            return DateTime.UtcNow.Ticks - ticks < OwnWriteGrace.Ticks;
        }
    }

    /// <summary>检测到外部修改（已防抖）。回调在 ThreadPool 线程上触发。</summary>
    public event EventHandler? ExternalChangeDetected;

    public FileChangeWatcher(TimeSpan? debounce = null)
    {
        // 300ms 足以合并一次保存产生的多个事件，又不至于让用户觉得迟钝。
        _debounce = debounce ?? TimeSpan.FromMilliseconds(300);
    }

    /// <summary>开始监听指定文件。传 null、空或纯空白则停止监听。</summary>
    public void Watch(string? path)
    {
        Stop();

        // 注意用 IsNullOrWhiteSpace：只判 IsNullOrEmpty 时，"   " 会让
        // Path.GetFullPath 抛 ArgumentException（单元测试发现的实际缺陷）。
        if (string.IsNullOrWhiteSpace(path))
            return;

        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        var fileName = Path.GetFileName(path);
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(fileName) || !Directory.Exists(directory))
            return;

        _path = Path.GetFullPath(path);

        _watcher = new FileSystemWatcher(directory, fileName)
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime,
            // 不监听子目录：我们只关心这一个文件
            IncludeSubdirectories = false,
            EnableRaisingEvents = true,
        };

        _watcher.Changed += OnFileEvent;
        _watcher.Created += OnFileEvent;
        _watcher.Renamed += OnFileEvent;
    }

    /// <summary>
    /// 标记"接下来的写入是本应用自己发起的"，用于抑制自身保存引发的误报。
    /// 返回的 IDisposable 释放时结束抑制窗口（即便保存失败也能恢复）。
    /// </summary>
    public IDisposable SuppressOwnWrite() => new WriteScope(this);

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        if (InOwnWriteGrace)
            return;

        // 防抖：重置定时器，只有安静了 _debounce 才认为是一次真正的修改
        _timer ??= new Timer(OnDebounced, null, Timeout.Infinite, Timeout.Infinite);
        _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
    }

    private void OnDebounced(object? state)
    {
        if (InOwnWriteGrace)
            return;

        // 文件可能正处于"被替换"的瞬间，稍等并确认它真的存在且可读，
        // 避免通知了一个已经被删掉/正在重写的文件。
        if (!IsFileReady(_path))
        {
            // 再等一轮；仍不可读就放弃这次通知
            _timer?.Change(_debounce, Timeout.InfiniteTimeSpan);
            return;
        }

        ExternalChangeDetected?.Invoke(this, EventArgs.Empty);
    }

    private static bool IsFileReady(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        try
        {
            // 独占打开失败说明写入方还持有句柄
            using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.None);
            return stream.Length >= 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void Stop()
    {
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnFileEvent;
            _watcher.Created -= OnFileEvent;
            _watcher.Renamed -= OnFileEvent;
            _watcher.Dispose();
            _watcher = null;
        }

        _timer?.Dispose();
        _timer = null;
        _path = null;
    }

    public void Dispose()
    {
        Stop();
        GC.SuppressFinalize(this);
    }

    private sealed class WriteScope : IDisposable
    {
        private readonly FileChangeWatcher _owner;

        public WriteScope(FileChangeWatcher owner)
        {
            _owner = owner;
            // 开始抑制时就记下时间：写入期间与写入后的一小段时间内到达的事件都视为自身产生。
            Interlocked.Exchange(ref _owner._ownWriteTicks, DateTime.UtcNow.Ticks);
        }

        public void Dispose()
        {
            // 刷新结束时刻：从"写完"开始计算宽限窗口。
            // 不需要异步延迟——之后的判断由 InOwnWriteGrace 依据时间戳完成，
            // 因此宽限结束后到达的真实外部修改不会被打扰（这曾是一个真实缺陷）。
            Interlocked.Exchange(ref _owner._ownWriteTicks, DateTime.UtcNow.Ticks);
        }
    }
}
