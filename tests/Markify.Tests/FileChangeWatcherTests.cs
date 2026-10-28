using Markify.Services;
using Xunit;

namespace Markify.Tests;

/// <summary>
/// 文件外部修改监听。
///
/// 重点验证两个最容易"看起来对但不工作"的行为：防抖（一次保存触发多个事件）
/// 与自身写入抑制（自己保存不该提示"被外部修改"）。
///
/// **关于等待**：文件系统事件是异步的，到达时间取决于系统负载。用固定的
/// `Task.Delay` 等待会偶发失败（实测 105 个用例中偶见 1 个失败，重跑即过），
/// 因此这里一律用"轮询直到条件成立 + 超时"的方式，见 <see cref="WaitUntilAsync"/>。
/// </summary>
public class FileChangeWatcherTests
{
    /// <summary>文件事件通常在毫秒级到达；给足余量但不至于让失败用例拖太久。</summary>
    private static readonly TimeSpan EventTimeout = TimeSpan.FromSeconds(10);

    private static string TempFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"markify-watch-{Guid.NewGuid():N}.md");
        File.WriteAllText(path, "initial");
        return path;
    }

    /// <summary>
    /// 轮询等待条件成立。返回 true 表示在超时前满足。
    ///
    /// 相比固定 sleep，这种做法在慢机器上只是慢一点，而不是误判失败。
    /// </summary>
    private static async Task<bool> WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
                return true;

            await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        return condition();
    }

    /// <summary>
    /// 确认 watcher 已经真正开始监听。
    ///
    /// `FileSystemWatcher` 的句柄注册有延迟，紧接着 Watch() 的写入可能落在生效之前。
    /// 这里反复尝试探测写入，直到被检测到——把"就绪时间"交给实际行为决定，
    /// 而不是凭经验猜一个 sleep 时长。
    ///
    /// 用 <paramref name="readCounter"/> 而不是传值/ref：async 方法不允许 ref 参数，
    /// 而传值会读到局部副本，永远看不到事件处理器对原变量的递增。
    /// </summary>
    private static async Task EnsureWatchingAsync(FileChangeWatcher watcher, string path, Func<int> readCounter)
    {
        watcher.Watch(path);

        var deadline = DateTime.UtcNow + EventTimeout;
        var attempt = 0;

        while (DateTime.UtcNow < deadline)
        {
            // 首次先等一下，避免所有探测都撞在注册窗口里
            await Task.Delay(attempt == 0 ? 200 : 100, TestContext.Current.CancellationToken);

            var before = readCounter();
            await File.WriteAllTextAsync(path, $"probe-{attempt}", TestContext.Current.CancellationToken);

            // 每个探测给一个短暂窗口；没反应就再试
            var sawIt = await WaitUntilAsync(() => readCounter() > before, TimeSpan.FromMilliseconds(800));

            if (sawIt)
                return;

            attempt++;
        }

        Assert.Fail("watcher 在超时前始终未检测到探测写入，说明监听未生效");
    }

    [Fact]
    public async Task Watch_DetectsExternalWrite()
    {
        var path = TempFile();
        try
        {
            using var watcher = new FileChangeWatcher(TimeSpan.FromMilliseconds(80));
            var raised = 0;
            watcher.ExternalChangeDetected += (_, _) => Interlocked.Increment(ref raised);

            watcher.Watch(path);
            await Task.Delay(150, TestContext.Current.CancellationToken);

            await File.WriteAllTextAsync(path, "changed", TestContext.Current.CancellationToken);

            var detected = await WaitUntilAsync(() => Volatile.Read(ref raised) > 0, EventTimeout);
            Assert.True(detected, "外部写入未被检测到");
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public async Task SuppressOwnWrite_DoesNotRaise_ButWatcherStillWorksAfterwards()
    {
        var path = TempFile();
        try
        {
            using var watcher = new FileChangeWatcher(TimeSpan.FromMilliseconds(80));
            var raised = 0;
            watcher.ExternalChangeDetected += (_, _) => Interlocked.Increment(ref raised);

            // 先确认监听确实生效——否则"没有事件"可能只是因为整个 watcher 没工作
            await EnsureWatchingAsync(watcher, path, () => Volatile.Read(ref raised));
            var baseline = Volatile.Read(ref raised);

            // 模拟应用自身保存：抑制期间不应产生新事件
            using (watcher.SuppressOwnWrite())
            {
                await File.WriteAllTextAsync(path, "own save", TestContext.Current.CancellationToken);
                // 给事件窗口足够时间过去（防抖 80ms + 余量）
                await Task.Delay(500, TestContext.Current.CancellationToken);
            }

            var duringSuppression = Volatile.Read(ref raised) - baseline;
            Assert.Equal(0, duringSuppression);

            // 宽限窗口内到达的事件仍视为自身写入产生，这是有意设计（吸收迟到的文件事件）。
            // 因此这里先等宽限期过去，再验证外部修改能被正常检测。
            await Task.Delay(700, TestContext.Current.CancellationToken);

            var before = Volatile.Read(ref raised);
            await File.WriteAllTextAsync(path, "external after", TestContext.Current.CancellationToken);

            var detected = await WaitUntilAsync(() => Volatile.Read(ref raised) > before, EventTimeout);
            Assert.True(detected, "宽限期结束后应恢复正常检测外部修改");
        }
        finally { TryDelete(path); }
    }

    [Fact]
    public async Task MultipleRapidWrites_AreDebounced()
    {
        var path = TempFile();
        try
        {
            const int debounceMs = 250;
            using var watcher = new FileChangeWatcher(TimeSpan.FromMilliseconds(debounceMs));
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

            // 等第一次通知到达，而不是盲等一个固定时长（慢机器上会等不到）
            var fired = await WaitUntilAsync(() => Volatile.Read(ref raised) > 0, EventTimeout);
            Assert.True(fired, "防抖后仍应有通知");

            // 再等两倍防抖窗口，确认没有后续补发
            await Task.Delay(debounceMs * 2, TestContext.Current.CancellationToken);

            Assert.Equal(1, Volatile.Read(ref raised));
        }
        finally { TryDelete(path); }
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
        finally { TryDelete(path); }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 清理失败不影响结论 */ }
    }
}
