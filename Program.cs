using System;
using System.Globalization;
using System.Threading;
using Machitan.Core;
using Machitan.Shell;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Machitan;

/// <summary>
/// Process entry point. The XAML-generated Main is switched off in the project file
/// (DISABLE_XAML_GENERATED_MAIN) so start-up order is ours to control.
///
/// Order:
///   1. "--guardian &lt;pid&gt;": this process is the guardian. It never starts any UI.
///      "--adminkeys": the admin-keys helper (see AdminKeys). It never starts any UI either.
///      "--setup-adminkeys": the installer's step that turns the admin keys on (see
///      AdminKeys.SetUpForInstaller). No UI; it leaves at once.
///   2. "--exit": ask the running shell to close, then leave.
///   3. Otherwise run the shell: single-instance check, start the guardian, run (the shell hides
///      Explorer's taskbar once its own bar is up), and restore the taskbar in a finally block
///      whatever happens.
/// </summary>
internal static class EntryPoint
{
    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            if (args.Length > 0 && string.Equals(args[0], Guardian.Argument, StringComparison.OrdinalIgnoreCase))
            {
                return Guardian.Run(args);
            }

            if (args.Length > 0 && string.Equals(args[0], AdminKeys.Argument, StringComparison.OrdinalIgnoreCase))
            {
                // Started as administrator by its scheduled task; no UI at all.
                return AdminKeysHelper.Run();
            }

            if (args.Length > 0 && string.Equals(args[0], AdminKeys.SetupArgument, StringComparison.OrdinalIgnoreCase))
            {
                // Run by the installer, as administrator; no UI at all.
                return AdminKeys.SetUpForInstaller();
            }

            if (HasArgument(args, ExitSignal.Argument))
            {
                bool delivered = ExitSignal.Send();
                Log.Info(delivered
                    ? "Exit request sent to the running shell."
                    : "Exit requested, but no running shell was found.");
                return 0;
            }

            return RunShell();
        }
        catch (Exception ex)
        {
            Log.Error("Machitan stopped because of an unexpected error in Main.", ex);
            return 1;
        }
    }

    private static int RunShell()
    {
        if (!SingleInstance.TryAcquire())
        {
            Log.Info("Machitan is already running; this second start does nothing.");
            return 0;
        }

        try
        {
            Log.Info(
                "==== Machitan starting. Version " + typeof(EntryPoint).Assembly.GetName().Version +
                ", .NET " + Environment.Version +
                ", Windows build " + Environment.OSVersion.Version.Build.ToString(CultureInfo.InvariantCulture) + ".");

            // The guardian first: from here on something always puts the taskbar back.
            Guardian.Spawn();

            try
            {
                // Explorer's taskbar is hidden by the shell once Machitan's bar is on screen.
                RunXaml();
            }
            finally
            {
                ExplorerTaskbar.Restore();
                Log.Info("==== Machitan stopped; Explorer's taskbar restored.");
            }

            return 0;
        }
        finally
        {
            SingleInstance.Release();
        }
    }

    private static void RunXaml()
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();

        Application.Start(static callbackParams =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
    }

    private static bool HasArgument(string[] args, string name)
    {
        foreach (string argument in args)
        {
            if (string.Equals(argument, name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
