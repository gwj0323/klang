using System;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

namespace Klang_WinUI;

/// <summary>
/// 投递到界面线程时统一兜底。
/// WinUI 3 里 DispatcherQueue.TryEnqueue 回调中抛出的异常不会经过 Application.UnhandledException，
/// 而是直接让进程以 0xc000027b（stowed exception）退出，也不留日志。
/// 所有投递都改走这里：异常被记录到 error.log，程序继续运行。
/// </summary>
public static class DispatcherExtensions
{
    public static bool SafeEnqueue(this DispatcherQueue queue, Action action) =>
        queue.TryEnqueue(() =>
        {
            try { action(); }
            catch (Exception ex) { App.LogError("UI callback", ex); }
        });

    /// <summary>异步回调：await 之后抛出的异常同样要接住（async void 的异常也会走同一条崩溃路径）。</summary>
    public static bool SafeEnqueue(this DispatcherQueue queue, Func<Task> action) =>
        queue.TryEnqueue(async () =>
        {
            try { await action(); }
            catch (Exception ex) { App.LogError("UI callback", ex); }
        });
}
