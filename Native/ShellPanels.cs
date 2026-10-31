using System;
using System.Diagnostics;
using static Machitan.Native.NativeMethods;

namespace Machitan.Native;

/// <summary>
/// Opens the shell panels (notification center, quick settings, task view).
/// Uses shell URI schemes instead of keybd_event to avoid feedback loops
/// with the global keyboard hook.
/// </summary>
internal static class ShellPanels
{
    /// <summary>
    /// Task View shell namespace GUID (Windows Task View).
    /// </summary>
    private const string TaskViewShellGuid = "shell:::{3080F90D-D7AD-11D9-BD98-0000947B0257}";

    public static void OpenNotifications()
    {
        // Open Windows Notification Center / Action Center via shell command.
        StartShellUri("shell:::{036E073D-E6D2-4C33-B8EE-7E7C4E6D7B2F}");
    }

    public static void OpenQuickSettings()
    {
        // Open Windows Quick Settings via ms-settings URI (Windows 11).
        StartShellUri("ms-settings:quicksettings");
    }

    public static void OpenTaskView()
    {
        StartShellUri(TaskViewShellGuid);
    }

    private static void StartShellUri(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = uri,
                UseShellExecute = true,
            });
        }
        catch
        {
            // Silently ignore: the shell panel may not be available on this
            // Windows version, and we must not crash the shell.
        }
    }
}
