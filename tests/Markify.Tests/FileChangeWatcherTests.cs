using Markify.Services;
using Xunit;

namespace Markify.Tests;

/// <summary>
/// 文件外部修改监听。
/// 这两个行为最容易写出"看起来对但不工作"的实现：防抖（一次保存触发多个事件）
/// 与自身写入抑制（自己保存不该提示"被外部修改"）。
/// </summary>
public class FileChangeWatcherTests
{
    private static string TempFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"markify-watch-{Guid.NewGuid():N}.md");
        File.WriteAllText(path, "initial");
        return path;
    }

    [Fact]
    public async Task Watch_DetectsExternalWrite()
    {
        var path = TempFile();
        try
        {
            using var watcher = new FileChangeWatcher(TimeSpan.FromMilliseconds(80));
            var hit = new TaskCompletionSource();
            watcher.ExternalChangeDetected += (_, _) => hit.TrySetResult();

            watcher.Watch(path);
            await Task.Delay(150, TestContext.Current.CancellationToken);                       // 等 watcher 就绪

            await File.WriteAllTextAsync(path, "changed", TestContext.Current.CancellationToken);

            var completed = await Task.WhenAny(hit.Task, Task.Delay(5000, TestContext.Current.CancellationToken));
            Assert.Same(hit.Task, completed);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task SuppressOwnWrite_DoesNotRaise()
    {
        var path = TempFile();
        try
        {
            using var watcher = new FileChangeWatcher(TimeSpan.FromMilliseconds(80));
            var raised = 0;
            watcher.ExternalChangeDetected += (_, _) => Interlocked.Increment(ref raised);

            watcher.Watch(path);
            await Task.Delay(150, TestContext.Current.CancellationToken);

            // 模拟应用自身保存
            using (watcher.SuppressOwnWrite())
            {
                await File.WriteAllTextAsync(path, "own save", TestContext.Current.CancellationToken);
                await Task.Delay(400, TestContext.Current.CancellationToken);                   // 让事件窗口过去
            }

            await Task.Delay(1200, TestContext.Current.CancellationToken);                      // 抑制解除后不应补发
            Assert.Equal(0, Volatile.Read(ref raised));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task MultipleRapidWrites_AreDebounced()
    {
        var path = TempFile();
        try
        {
            using var watcher = new FileChangeWatcher(TimeSpan.FromMilliseconds(250));
            var raised = 0;
            watcher.ExternalChangeDetected += (_, _) => Interlocked.Increment(ref raised);

            watcher.Watch(path);
            await Task.Delay(150, TestContext.Current.CancellationToken);

            // 模拟编辑器"写临时文件再替换"产生的多次事件
            for (var i = 0; i < 5; i++)
            {
                await File.WriteAllTextAsync(path, $"v{i}", TestContext.Current.CancellationToken);
                await Task.Delay(30, TestContext.Current.CancellationToken);
            }

            await Task.Delay(1500, TestContext.Current.CancellationToken);

            // 防抖后应只报一次，而不是 5 次
            Assert.Equal(1, Volatile.Read(ref raised));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Watch_NullOrEmptyPath_DoesNotThrow()
    {
        using var watcher = new FileChangeWatcher();
        watcher.Watch(null);
        watcher.Watch("");
        watcher.Watch("   ");
    }

    [Fact]
    public void Watch_ThenStop_IsIdempotent()
    {
        var path = TempFile();
        try
        {
            using var watcher = new FileChangeWatcher();
            watcher.Watch(path);
            watcher.Stop();
            watcher.Stop();          // 重复 Stop 不应抛异常
        }
        finally { File.Delete(path); }
    }
}
