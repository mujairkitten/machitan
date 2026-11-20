using System;
using System.Collections.Generic;
using System.Diagnostics;
using Machitan.Config;
using Machitan.Core;
using Machitan.Interop;
using Microsoft.Win32;

namespace Machitan.Shell;

/// <summary>One entry of Start's power menu.</summary>
/// <param name="Label">What the menu says.</param>
/// <param name="Ask">The question Start asks first, or null for no question (sleep, hibernate).</param>
/// <param name="Run">Does it; gets Start's window handle.</param>
internal sealed record PowerChoice(string Label, string? Ask, Action<nint> Run);

/// <summary>
/// Start's power menu: which entries this PC offers right now, and doing them.
/// Sleep shows only when the PC can sleep (S1-S3, or Modern Standby); Hibernate only when
/// hibernation is turned on (the hibernation file exists); the two update entries only while
/// Windows has an update waiting for a restart (the value Windows' own power menu reads:
/// HKLM\SOFTWARE\Microsoft\WindowsUpdate\Orchestrator ShutdownFlyoutOptions, not 0).
/// </summary>
internal static class PowerActions
{
    private const string OrchestratorKey = @"SOFTWARE\Microsoft\WindowsUpdate\Orchestrator";
    private const string FlyoutMenuKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\FlyoutMenuSettings";
    private const string ExplorerPolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\Explorer";

    /// <summary>What this PC can do, read fresh (cheap).</summary>
    internal static (bool Sleep, bool ModernStandby, bool Hibernate) Capabilities()
    {
        unsafe
        {
            byte* caps = stackalloc byte[128];
            new Span<byte>(caps, 128).Clear();
            if (Powrprof.GetPwrCapabilities(caps) == 0)
            {
                return (false, false, false);
            }

            bool modernStandby = caps[Powrprof.CapAoAc] != 0;
            bool sleep = modernStandby
                || caps[Powrprof.CapSystemS1] != 0
                || caps[Powrprof.CapSystemS2] != 0
                || caps[Powrprof.CapSystemS3] != 0;
            bool hibernate = caps[Powrprof.CapSystemS4] != 0 && caps[Powrprof.CapHiberFilePresent] != 0;
            return (sleep, modernStandby, hibernate);
        }
    }

    /// <summary>True while Windows has an update waiting for a restart (its own power menu then offers "Update and ...").</summary>
    internal static bool UpdateWaiting()
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(OrchestratorKey);
            return key?.GetValue("ShutdownFlyoutOptions") is int options && options != 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The menu, in Windows' order, from the settings and what this PC offers now.</summary>
    internal static List<PowerChoice> Choices(ShellSettings settings)
    {
        var choices = new List<PowerChoice>();
        (bool sleep, bool modernStandby, bool hibernate) = Capabilities();
        bool update = (settings.PowerUpdateShutDown || settings.PowerUpdateRestart) && UpdateWaiting();

        if (settings.PowerSleep && sleep)
        {
            choices.Add(new PowerChoice(Strings.PowerSleep, null, hwnd => Sleep(hwnd, modernStandby)));
        }

        if (settings.PowerHibernate && hibernate)
        {
            choices.Add(new PowerChoice(Strings.PowerHibernate, null, _ => RunShutdownTool("Hibernate", "/h")));
        }

        if (settings.PowerUpdateShutDown && update)
        {
            choices.Add(new PowerChoice(Strings.PowerUpdateShutDown, Strings.PowerUpdateShutDownAsk, _ => ShutDownWithUpdates(restart: false)));
        }

        if (settings.PowerShutDown)
        {
            choices.Add(new PowerChoice(Strings.PowerShutDown, Strings.PowerShutDownAsk, _ => RunShutdownTool("Shut down", "/s /t 0")));
        }

        if (settings.PowerUpdateRestart && update)
        {
            choices.Add(new PowerChoice(Strings.PowerUpdateRestart, Strings.PowerUpdateRestartAsk, _ => ShutDownWithUpdates(restart: true)));
        }

        if (settings.PowerRestart)
        {
            choices.Add(new PowerChoice(Strings.PowerRestart, Strings.PowerRestartAsk, _ => RunShutdownTool("Restart", "/r /t 0")));
        }

        return choices;
    }

    /// <summary>
    /// The Win+X menu's "Shut down or sign out" entries, as Windows' own menu has them: Sign out;
    /// Sleep and Hibernate when this PC can and Windows' "Show in power menu" choices (Control
    /// Panel > Power Options > Choose what the power buttons do, or a policy) have them, which
    /// shows Sleep and not Hibernate unless changed; the update entries while an update waits;
    /// Shut down and Restart. No questions: Windows' menu acts at once. Labels mark their key with '&amp;'.
    /// </summary>
    internal static List<PowerChoice> WinXChoices()
    {
        var choices = new List<PowerChoice>();
        (bool sleep, bool modernStandby, bool hibernate) = Capabilities();
        bool update = UpdateWaiting();

        choices.Add(new PowerChoice(Strings.WinXSignOut, null, _ => RunShutdownTool("Sign out", "/l")));
        if (sleep && ShownInPowerMenu("ShowSleepOption", byDefault: true))
        {
            choices.Add(new PowerChoice(Strings.WinXSleep, null, hwnd => Sleep(hwnd, modernStandby)));
        }

        if (hibernate && ShownInPowerMenu("ShowHibernateOption", byDefault: false))
        {
            choices.Add(new PowerChoice(Strings.WinXHibernate, null, _ => RunShutdownTool("Hibernate", "/h")));
        }

        if (update)
        {
            choices.Add(new PowerChoice(Strings.WinXUpdateShutDown, null, _ => ShutDownWithUpdates(restart: false)));
        }

        choices.Add(new PowerChoice(Strings.WinXShutDown, null, _ => RunShutdownTool("Shut down", "/s /t 0")));
        if (update)
        {
            choices.Add(new PowerChoice(Strings.WinXUpdateRestart, null, _ => ShutDownWithUpdates(restart: true)));
        }

        choices.Add(new PowerChoice(Strings.WinXRestart, null, _ => RunShutdownTool("Restart", "/r /t 0")));
        return choices;
    }

    /// <summary>Windows' "Show in power menu" choice for Sleep or Hibernate: a policy first, then the setting, else Windows' default.</summary>
    private static bool ShownInPowerMenu(string valueName, bool byDefault)
    {
        try
        {
            using (RegistryKey? policy = Registry.LocalMachine.OpenSubKey(ExplorerPolicyKey))
            {
                if (policy?.GetValue(valueName) is int forced)
                {
                    return forced != 0;
                }
            }

            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(FlyoutMenuKey);
            if (key?.GetValue(valueName) is int value)
            {
                return value != 0;
            }
        }
        catch (Exception)
        {
            // Unreadable: Windows' default.
        }

        return byDefault;
    }

    /// <summary>
    /// Modern Standby PCs have no S3: Windows enters standby when the screen turns off, which is
    /// what Start's Sleep does there too. Others sleep through SetSuspendState.
    /// </summary>
    private static void Sleep(nint hwnd, bool modernStandby)
    {
        Log.Info("Power: Sleep" + (modernStandby ? " (Modern Standby: screen off)." : "."));
        if (modernStandby)
        {
            User32.DefWindowProcW(hwnd, User32.WM_SYSCOMMAND, (nint)User32.SC_MONITORPOWER, 2);
            return;
        }

        EnableShutdownRight();
        if (Powrprof.SetSuspendState(0, 0, 0) == 0)
        {
            Log.Warn("Power: Windows refused to sleep (error " + System.Runtime.InteropServices.Marshal.GetLastPInvokeError() + ").");
        }
    }

    private static void ShutDownWithUpdates(bool restart)
    {
        string action = restart ? "Update and restart" : "Update and shut down";
        Log.Info("Power: " + action + " confirmed.");
        EnableShutdownRight();
        uint flags = Advapi32.SHUTDOWN_INSTALL_UPDATES | (restart ? Advapi32.SHUTDOWN_RESTART : Advapi32.SHUTDOWN_POWEROFF);
        uint result = Advapi32.InitiateShutdownW(null, null, 0, flags, Advapi32.ReasonPlannedUpgrade);
        if (result != 0)
        {
            Log.Error("Power: " + action + " failed (error " + result + ").");
        }
    }

    private static void RunShutdownTool(string action, string arguments)
    {
        try
        {
            Log.Info("Power: " + action + ".");
            using Process? process = Process.Start(new ProcessStartInfo("shutdown.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex)
        {
            Log.Error("Power: " + action + " failed.", ex);
        }
    }

    /// <summary>Turns on this process's right to shut down (every user has it, switched off).</summary>
    private static unsafe void EnableShutdownRight()
    {
        // -1: the current process (its pseudo handle, which needs no closing).
        if (!Advapi32.OpenProcessToken(-1, Advapi32.TOKEN_ADJUST_PRIVILEGES | Advapi32.TOKEN_QUERY, out nint token))
        {
            return;
        }

        try
        {
            if (!Advapi32.LookupPrivilegeValueW(null, "SeShutdownPrivilege", out long luid))
            {
                return;
            }

            // TOKEN_PRIVILEGES with one LUID_AND_ATTRIBUTES: count, LUID, attributes.
            byte* state = stackalloc byte[16];
            uint* count = (uint*)state;
            long* id = (long*)(state + 4);
            uint* attributes = (uint*)(state + 12);
            count[0] = 1;
            id[0] = luid;
            attributes[0] = Advapi32.SE_PRIVILEGE_ENABLED;
            Advapi32.AdjustTokenPrivileges(token, false, state, 16, 0, 0);
        }
        finally
        {
            Kernel32.CloseHandle(token);
        }
    }
}
