using Microsoft.UI.Xaml;
using Machitan.Native;

namespace Machitan;

public partial class App : Application
{
    public static MainWindow? MainWindow { get; private set; }

    public App()
    {
        this.InitializeComponent();
        UnhandledException += (_, e) =>
        {
            DiagnosticsLog.WriteException("Application.UnhandledException", e.Exception);
        };
        DiagnosticsLog.Write("App constructed");
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        DiagnosticsLog.Write("OnLaunched");
        MainWindow = new MainWindow();
        DiagnosticsLog.Write("MainWindow created");
        MainWindow.Activate();
        DiagnosticsLog.Write("MainWindow activated");

        // Warm up installed apps list in background
        System.Threading.Tasks.Task.Run(() => StartMenu.AppScanner.GetAllApps());
    }
}
