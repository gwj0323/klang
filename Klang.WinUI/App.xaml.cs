using Microsoft.UI.Xaml;

namespace Klang_WinUI;

public partial class App : Application
{
    public static MainWindow? MainWindowInstance { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        MainWindowInstance = new MainWindow();
        MainWindowInstance.Activate();
    }
}
