using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.Windows.AppLifecycle;

namespace Klang_WinUI;

/// <summary>
/// 程序入口（替代 XAML 自动生成的 Main）：
/// - 带 <see cref="Services.AnalysisWorker.WorkerArg"/> 参数启动时，作为后台分析子进程运行，不加载界面；
/// - 已经有一个 Klang 在运行（可能藏在托盘里）时，把它带到前台，自己退出；
/// - 否则照常启动 WinUI 应用。
/// </summary>
public static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == Services.AnalysisWorker.WorkerArg)
            return Services.AnalysisWorker.RunWorker();
        if (args.Length > 0 && args[0] == Services.Ai.AiWorkerHost.WorkerArg)
            return Services.Ai.AiWorkerHost.Run(args);

        WinRT.ComWrappersSupport.InitializeComWrappers();
        if (RedirectToRunningInstance()) return 0;

        XamlGeneratedProgram.XamlGeneratedMain();
        return 0;
    }

    /// <summary>
    /// 单实例：同一份数据目录只允许一个 Klang（两个实例会同时读写同一份状态文件）。
    /// 用 KLANG_DATA_DIR 指到别处的开发实例使用不同的键，可以和安装版同时运行。
    /// </summary>
    private static bool RedirectToRunningInstance()
    {
        try
        {
            var dir = Services.StateService.ResolveDataDirectory().ToLowerInvariant();
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(dir)))[..12];
            var main = AppInstance.FindOrRegisterForKey("Klang.Main." + hash);
            if (main.IsCurrent)
            {
                // 之后再启动的实例会把激活转给这里：把窗口（可能藏在托盘里）带到前台
                main.Activated += (s, e) =>
                {
                    var w = App.MainWindowInstance;
                    w?.DispatcherQueue.TryEnqueue(w.ShowFromTray);
                };
                return false;
            }

            var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
            var done = CreateEvent(IntPtr.Zero, true, false, null);
            Task.Run(() =>
            {
                try { main.RedirectActivationToAsync(activation).AsTask().Wait(); }
                catch { }
                SetEvent(done);
            });
            // 转交在后台线程完成；这里是 STA 线程，等待时要继续处理 COM 消息
            CoWaitForMultipleObjects(0, 10000, 1, new[] { done }, out _);
            CloseHandle(done);
            return true;
        }
        catch
        {
            return false;   // 出问题时退回原来的行为：照常启动
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr CreateEvent(IntPtr attributes, bool manualReset, bool initialState, string? name);
    [DllImport("kernel32.dll")] private static extern bool SetEvent(IntPtr handle);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("ole32.dll")] private static extern int CoWaitForMultipleObjects(uint flags, uint timeout, uint count, IntPtr[] handles, out uint index);
}
