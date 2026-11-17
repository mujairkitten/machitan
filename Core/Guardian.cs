using System;
using System.Diagnostics;
using System.Globalization;
using System.Threading;
using Machitan.Shell;

namespace Machitan.Core;

/// <summary>
/// The guardian is a second, tiny Machitan process started as "--guardian &lt;pid&gt;".
/// It does nothing but wait for the shell process to end, then puts Explorer's taskbar back.
/// It covers the cases where the shell cannot clean up after itself: a crash, a kill from
/// Task Manager, or "Stop debugging" in Visual Studio.
/// </summary>
internal static class Guardian
{
    internal const string Argument = "--guardian";

    /// <summary>Starts the guardian for the current process. Failure is logged, never fatal.</summary>
    internal static void Spawn()
    {
        try
        {
            string? executable = Environment.ProcessPath;
            if (string.IsNullOrEmpty(executable))
            {
                Log.Warn("Guardian not started: the path of this executable is unknown.");
                return;
            }

            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            start.ArgumentList.Add(Argument);
            start.ArgumentList.Add(Environment.ProcessId.ToString(CultureInfo.InvariantCulture));

            using Process? guardian = Process.Start(start);
            Log.Info(guardian is null
                ? "Guardian not started: Windows returned no process."
                : "Guardian started (pid " + guardian.Id.ToString(CultureInfo.InvariantCulture) + ").");
        }
        catch (Exception ex)
        {
            Log.Error("Guardian could not be started.", ex);
        }
    }

    /// <summary>Entry point of the guardian process. Returns the process exit code.</summary>
    internal static int Run(string[] args)
    {
        if (args.Length < 2 || !int.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int shellPid))
        {
            Log.Warn("Guardian started without a valid process id; exiting.");
            return 2;
        }

        try
        {
            using Process shell = Process.GetProcessById(shellPid);
            shell.WaitForExit();
        }
        catch (ArgumentException)
        {
            // The shell process is already gone.
        }
        catch (Exception ex)
        {
            Log.Error("Guardian could not watch the shell process; restoring the taskbar now.", ex);
        }

        // Give a cleanly exiting shell a moment to finish its own restore.
        Thread.Sleep(300);

        if (SingleInstance.IsHeldBySomeone())
        {
            Log.Info("Guardian: another Machitan instance is running; leaving the taskbar alone.");
            return 0;
        }

        ExplorerTaskbar.Restore();
        Log.Info("Guardian: shell process " + shellPid.ToString(CultureInfo.InvariantCulture) + " ended; Explorer's taskbar restored.");
        return 0;
    }
}
