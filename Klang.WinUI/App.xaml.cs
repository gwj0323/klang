using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;

namespace Klang_WinUI;

public partial class App : Application
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SetCurrentProcessExplicitAppUserModelID(string appId);
    public static MainWindow? MainWindowInstance { get; private set; }

    /// <summary>错误日志：%LocalAppData%\Klang\error.log（以前写在“当前目录”，安装版找不到也可能没有写入权限）。</summary>
    public static readonly string ErrorLogPath = Path.Combine(
        Services.StateService.ResolveDataDirectory(), "error.log");

    public static void LogError(string kind, object? detail)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ErrorLogPath)!);
            var fi = new FileInfo(ErrorLogPath);
            if (fi.Exists && fi.Length > 1024 * 1024) fi.Delete();   // 最多保留 1 MB
            File.AppendAllText(ErrorLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] v{Services.CrashReporter.AppVersion} {kind}: {detail}\n\n");
        }
        catch { }
        Services.CrashReporter.Note("错误：" + kind);
    }

    public App()
    {
        Services.CrashReporter.Start();
        // 开发实例与安装版使用不同的任务栏身份，避免窗口枚举、媒体控件及调试工具混认。
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("KLANG_DATA_DIR")))
            SetCurrentProcessExplicitAppUserModelID("Klang.Development");
        InitializeComponent();

        // 界面线程上未处理的异常：记录下来并拦截，保持程序运行
        this.UnhandledException += (sender, e) =>
        {
            LogError("Unhandled", e.Exception);
            e.Handled = true;
        };
        // 后台线程上未处理的异常（进程会退出，至少留下记录）
        AppDomain.CurrentDomain.UnhandledException += (s, e) => LogError("Fatal", e.ExceptionObject);
        // 没人等待的 Task 里抛出的异常
        TaskScheduler.UnobservedTaskException += (s, e) => { LogError("UnobservedTask", e.Exception); e.SetObserved(); };
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        L.Init();   // 必须在创建任何界面之前：代码里的 L.T 靠它加载翻译表
        MainWindowInstance = new MainWindow();
        MainWindowInstance.Activate();
    }
}
