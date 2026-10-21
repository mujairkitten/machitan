using System.Diagnostics;
using System.Text;
using static Machitan.Native.NativeMethods;

namespace Machitan.Native;

/// <summary>
/// Hard-kill insurance for the hidden Explorer taskbar.
///
/// Nothing running inside Machitan can restore the taskbar if Machitan is
/// hard-killed (taskkill /F, Task Manager "End task", a crash). So on
/// startup Machitan launches a second copy of itself with --guardian &lt;pid&gt;.
/// That copy shows no UI at all: it waits for the main process to exit —
/// the process handle signals on ANY exit, however violent — and then
/// re-shows the Explorer taskbar. On a clean exit the main instance
/// restores the taskbar itself and kills its guardian.
/// </summary>
internal static class GuardianMode
{
    public const string Flag = "--guardian";

    public static bool IsGuardianRequest(string[] args) =>
        args.Length >= 2 && args[0] == Flag && int.TryParse(args[1], out _);

    public static int GuardianPid(string[] args) => int.Parse(args[1]);

    /// <summary>Launches the guardian copy of this exe, or null if that failed.</summary>
    public static Process? Launch()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe == null)
                return null;

            return Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                Arguments = $"{Flag} {Environment.ProcessId}",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch
        {
            return null; // best effort — clean-exit restore still works
        }
    }

    /// <summary>
    /// Runs the guardian: block until the main process is gone,
    /// then make sure the Explorer taskbar is visible again.
    /// </summary>
    public static void Run(int mainPid)
    {
        try
        {
            using var main = Process.GetProcessById(mainPid);
            // PID-reuse guard: if the PID now belongs to some other program,
            // the real main process is already gone — restore right away
            // instead of waiting on a stranger.
            if (string.Equals(main.ProcessName, Process.GetCurrentProcess().ProcessName, StringComparison.OrdinalIgnoreCase))
                main.WaitForExit();
        }
        catch
        {
            // Main process already gone — restore anyway.
        }

        EnumWindows((h, _) =>
        {
            var name = new StringBuilder(64);
            GetClassName(h, name, name.Capacity);
            if (name.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
                ShowWindow(h, SW_SHOW);
            return true;
        }, IntPtr.Zero);
    }
}
