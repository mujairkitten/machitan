using System;
using Machitan.Core;
using Microsoft.Win32;

namespace Machitan.Shell;

/// <summary>
/// "Run at startup": Windows' own list of programs started at sign-in, HKCU\Software\
/// Microsoft\Windows\CurrentVersion\Run, value "Machitan" = this exe in quotes. Windows'
/// Task Manager (Startup apps) can switch the entry off without removing it; it keeps that
/// in ...\Explorer\StartupApproved\Run (first byte odd = off). The setting is read from
/// Windows each time, never kept in settings.json, so it always tells the truth.
/// </summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
    private const string ValueName = "Machitan";

    private static string Command => "\"" + (Environment.ProcessPath ?? string.Empty) + "\"";

    /// <summary>True when Windows will start this copy of Machitan at sign-in.</summary>
    internal static bool IsOn()
    {
        try
        {
            using RegistryKey? run = Registry.CurrentUser.OpenSubKey(RunKey);
            if (run?.GetValue(ValueName) is not string command
                || !string.Equals(command.Trim(), Command, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            using RegistryKey? approved = Registry.CurrentUser.OpenSubKey(ApprovedKey);
            return approved?.GetValue(ValueName) is not byte[] state || state.Length == 0 || (state[0] & 1) == 0;
        }
        catch (Exception ex)
        {
            Log.Warn("Run at startup: Windows' startup list could not be read.", ex);
            return false;
        }
    }

    /// <summary>Adds or removes the entry. On also clears a Task Manager "off" for it. False when Windows refused.</summary>
    internal static bool Set(bool on)
    {
        try
        {
            using RegistryKey run = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (on)
            {
                if (string.IsNullOrEmpty(Environment.ProcessPath))
                {
                    return false;
                }

                run.SetValue(ValueName, Command, RegistryValueKind.String);
                using RegistryKey? approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, writable: true);
                approved?.DeleteValue(ValueName, throwOnMissingValue: false);
            }
            else
            {
                run.DeleteValue(ValueName, throwOnMissingValue: false);
            }

            Log.Info("Run at startup: " + (on ? "on (" + Command + ")." : "off."));
            return true;
        }
        catch (Exception ex)
        {
            Log.Error("Run at startup: Windows' startup list could not be changed.", ex);
            return false;
        }
    }
}
