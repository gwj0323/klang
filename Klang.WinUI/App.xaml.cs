using System;
using System.IO;
using Microsoft.UI.Xaml;

namespace Klang_WinUI;

public partial class App : Application
{
    public static MainWindow? MainWindowInstance { get; private set; }

    public App()
    {
        InitializeComponent();

        // 全局异常拦截，杜绝应用闪退崩溃
        this.UnhandledException += (sender, e) =>
        {
            try
            {
                File.AppendAllText("error.log", $"[{DateTime.Now}] Unhandled: {e.Exception}\n{e.Message}\n");
            }
            catch { }
            e.Handled = true; // 拦截异常，保持程序存活
        };
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        MainWindowInstance = new MainWindow();
        MainWindowInstance.Activate();
    }
}
