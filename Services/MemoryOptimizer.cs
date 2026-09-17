using System;
using System.Runtime;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using MuSync.Utils;

namespace MuSync;

/// <summary>
/// 后台内存优化：窗口隐藏到托盘后，清理图片缓存 → 强制完整 GC → 把空闲物理页归还给操作系统
/// （EmptyWorkingSet/SetProcessWorkingSetSize），显著降低托盘驻留时任务管理器显示的内存占用；
/// 窗口重新显示后内存会按需自然回升，不影响功能与响应速度。
/// </summary>
internal static class MemoryOptimizer
{
    private const int TrimDelayAfterHideMs = 4000;
    private static readonly TimeSpan PeriodicTrimInterval = TimeSpan.FromMinutes(5);
    private static CancellationTokenSource? _cts;
    private static readonly object SyncRoot = new();
    private static long _lastTrimTick;

    /// <summary>窗口隐藏到托盘时调用：延迟回收一次，之后周期性回收，直到窗口重新显示。</summary>
    public static void OnWindowHidden()
    {
        lock (SyncRoot)
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(TrimDelayAfterHideMs, token).ConfigureAwait(false);
                    TrimWorkingSet(aggressive: true);
                    while (!token.IsCancellationRequested)
                    {
                        await Task.Delay(PeriodicTrimInterval, token).ConfigureAwait(false);
                        // 周期性回收用非强制 GC（仅当确实有可回收垃圾时才做完整压缩）
                        TrimWorkingSet(aggressive: false);
                    }
                }
                catch (OperationCanceledException)
                {
                    // 窗口重新显示，后台回收循环正常结束
                }
                catch (Exception ex)
                {
                    Logger.Warn($"后台内存回收异常: {ex.Message}");
                }
            }, token);
        }
    }

    /// <summary>窗口重新显示时停止周期回收。</summary>
    public static void OnWindowShown()
    {
        lock (SyncRoot)
        {
            _cts?.Cancel();
            _cts = null;
        }
    }

    /// <summary>
    /// 执行一次回收。aggressive=true（刚隐藏）时先把图片缓存压到最少并做完整压缩 GC；
    /// 之后无论哪条路径都尝试 EmptyWorkingSet 归还物理页（私有字节不变，工作集下降）。
    /// </summary>
    public static void TrimWorkingSet(bool aggressive)
    {
        // 两次回收至少间隔 10 秒，避免 GC 抖动
        var now = Environment.TickCount64;
        if (!aggressive && now - Interlocked.Read(ref _lastTrimTick) < 10_000) return;
        Interlocked.Exchange(ref _lastTrimTick, now);

        try
        {
            if (aggressive)
            {
                // 托盘驻留时封面不可见，只保留少量最近使用的缓存，回窗后会按需重新加载
                ImageCacheManager.ForceCleanupCache(3);
                GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Forced, true, true);
                GC.WaitForPendingFinalizers();
                GC.Collect(2, GCCollectionMode.Forced, true, true);
            }
            else
            {
                GC.Collect(2, GCCollectionMode.Optimized, false, false);
            }
            ReturnWorkingSet();
        }
        catch (Exception ex)
        {
            Logger.Warn($"回收内存失败: {ex.Message}");
        }
    }

    /// <summary>EmptyWorkingSet 把进程驻留物理内存压到最小；失败则回退 SetProcessWorkingSetSize(-1,-1)。</summary>
    private static void ReturnWorkingSet()
    {
        var handle = GetCurrentProcess();
        if (EmptyWorkingSet(handle)) return;
        // 经典兜底：-1,-1 等价于让操作系统尽可能回收工作集
        SetProcessWorkingSetSize(handle, new IntPtr(-1), new IntPtr(-1));
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyWorkingSet(IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize,
        IntPtr dwMaximumWorkingSetSize);
}
