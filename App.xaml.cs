using Microsoft.UI.Xaml;
using Machitan.Native;

namespace Machitan;

public partial class App : Application
{
    public static MainWindow? MainWindow { get; private set; }
    internal static ShellHotkeys? _hotkeys;

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

        // Global keyboard hook for Win-key shortcuts (Win, Win+1..9, Win+D, ...).
        // Failure-latched: if the hook can't be installed, the shell still works
        // — just without global hotkeys.
        // Respect the user's EnableGlobalHotkeys setting (H2-1: opt-out).
        try
        {
            var settings = MachitanSettingsStore.Load();
            if (settings.EnableGlobalHotkeys)
            {
                DiagnosticsLog.Write("App: creating ShellHotkeys");
                _hotkeys = new ShellHotkeys(MainWindow.DispatcherQueue);
                DiagnosticsLog.Write("App: ShellHotkeys created");
            }
            else
            {
                DiagnosticsLog.Write("App: ShellHotkeys disabled by user setting");
            }
        }
        catch (Exception ex)
        {
            DiagnosticsLog.WriteException("ShellHotkeys start", ex);
        }

        // Warm up installed apps list in background
        System.Threading.Tasks.Task.Run(() => StartMenu.AppScanner.GetAllApps());
    }

}
