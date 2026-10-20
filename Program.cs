using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Machitan.Native;

namespace Machitan;

public static class Program
{
    private const string MutexName = "Local\\Machitan_SingleInstance";

    [STAThread]
    public static void Main(string[] args)
    {
        if (GuardianMode.IsGuardianRequest(args))
        {
            GuardianMode.Run(GuardianMode.GuardianPid(args));
            return;
        }

        using var mutex = new Mutex(true, MutexName, out bool isNewInstance);
        if (!isNewInstance)
            return;

        ExplorerTaskbarController.Hide();
        using var guardian = GuardianMode.Launch();
        DiagnosticsLog.Write("Program start");

        try
        {
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Application.Start(p =>
            {
                var queue = DispatcherQueue.GetForCurrentThread();
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherQueueSynchronizationContext(queue));
                _ = new App();
            });
            DiagnosticsLog.Write("Application.Start returned");
        }
        catch (Exception ex)
        {
            DiagnosticsLog.WriteException("Program.Main", ex);
            throw;
        }
        finally
        {
            ExplorerTaskbarController.Restore();
            try { guardian?.Kill(); } catch { }
        }
    }
}
