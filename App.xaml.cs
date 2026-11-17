using System;
using System.Threading.Tasks;
using Machitan.Core;
using Machitan.Shell;
using Microsoft.UI.Xaml;

namespace Machitan;

public partial class App : Application
{
    private Window? _testWindow;
    private ShellHost? _shell;

    public App()
    {
        InitializeComponent();

        // Failures are logged, never fatal.
        ExceptionTrail.Start();
        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (EntryPoint.Mode == StartupMode.Proof)
        {
            _testWindow = new MainWindow();
            _testWindow.Activate();
            return;
        }

        if (EntryPoint.Mode == StartupMode.SeeThrough)
        {
            _testWindow = new SeeThroughTestWindow();
            _testWindow.Activate();
            return;
        }

        try
        {
            _shell = new ShellHost();
            _shell.Start();
        }
        catch (Exception ex)
        {
            // Without a bar there is nothing to run. Leave; Main's finally block restores the taskbar.
            Log.Error("The shell could not start.", ex);
            Exit();
        }
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled error on the UI thread: " + e.Message, e.Exception);
        e.Handled = true;
    }

    private static void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        Log.Error("Unhandled error outside the UI thread.", e.ExceptionObject as Exception);
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        Log.Error("Unobserved task error.", e.Exception);
        e.SetObserved();
    }
}
