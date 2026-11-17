using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using Machitan.Core;
using Machitan.Interop;

namespace Machitan.Shell;

/// <summary>
/// What Machitan's shortcuts do. All of this runs on the UI thread.
///
/// Win+1..9: the app in that place on the bar's dock. Not running: start it. Running but not
///   in front: bring it forward. In front with one window: minimise it. In front with
///   several windows: step to the next one.
/// Win+Shift+1..9: start another copy of that app.
/// Win+1..9 and Win+Shift+1..9 are caught in the keyboard hook when Windows keeps them for
///   itself, because Windows' versions act on Explorer's hidden taskbar instead of the dock.
/// Win+D: Show desktop (the same as the bar's Show Desktop strip).
/// Win+E: File Explorer. Win+L: lock the PC. Win+R: Windows' Run box.
/// Alt+Esc: the next open window, in the bar's order (minimised windows are skipped).
/// Win+N: Machitan's calendar. Win+A: Machitan's quick settings (both caught in the hook
///   when Windows keeps them, like Win+1..9).
/// Win+Tab and Win+`: Machitan's switcher, both caught in the hook when Windows keeps them
///   (Sam's choice: Win+Tab opens Machitan's switcher, not Windows' Task View).
/// </summary>
internal static class HotkeyActions
{
    private const uint KeyA = 0x41;
    private const uint KeyD = 0x44;
    private const uint KeyE = 0x45;
    private const uint KeyL = 0x4C;
    private const uint KeyR = 0x52;
    private const uint KeyN = 0x4E;
    private const uint Key1 = 0x31;
    private const uint KeyTab = 0x09;

    /// <summary>The key left of 1 on US-style keyboards (` and ~).</summary>
    private const uint KeyBacktick = 0xC0;

    internal static List<HotkeyDefinition> Build(ShellHost host)
    {
        var list = new List<HotkeyDefinition>();

        for (int slot = 1; slot <= 9; slot++)
        {
            int index = slot - 1;
            uint key = Key1 + (uint)index;
            list.Add(new HotkeyDefinition("Win+" + slot, "Win+1..9", User32.MOD_WIN, key, () => SwitchToDockItem(host, index), catchWhenWindowsOwnsIt: true));
        }

        for (int slot = 1; slot <= 9; slot++)
        {
            int index = slot - 1;
            uint key = Key1 + (uint)index;
            list.Add(new HotkeyDefinition("Win+Shift+" + slot, "Win+Shift+1..9", User32.MOD_WIN | User32.MOD_SHIFT, key, () => StartNewCopy(host, index), catchWhenWindowsOwnsIt: true));
        }

        list.Add(new HotkeyDefinition("Win+D", "Win+D", User32.MOD_WIN, KeyD, () => host.Desktop.Toggle(host.Tasks.Groups)));
        list.Add(new HotkeyDefinition("Win+E", "Win+E", User32.MOD_WIN, KeyE, () => Launcher.Start("explorer.exe")));
        list.Add(new HotkeyDefinition("Win+L", "Win+L", User32.MOD_WIN, KeyL, LockPc));
        list.Add(new HotkeyDefinition("Win+R", "Win+R", User32.MOD_WIN, KeyR, ShowRunBox));
        list.Add(new HotkeyDefinition("Alt+Esc", "Alt+Esc", User32.MOD_ALT, User32.VK_ESCAPE, () => NextWindow(host)));
        list.Add(new HotkeyDefinition("Win+N", "Win+N", User32.MOD_WIN, KeyN, host.ToggleCalendar, catchWhenWindowsOwnsIt: true));
        list.Add(new HotkeyDefinition("Win+A", "Win+A", User32.MOD_WIN, KeyA, host.ToggleQuickSettings, catchWhenWindowsOwnsIt: true));
        list.Add(new HotkeyDefinition("Win+Tab", "Win+Tab", User32.MOD_WIN, KeyTab, host.ShowSwitcher, catchWhenWindowsOwnsIt: true));
        list.Add(new HotkeyDefinition("Win+`", "Win+`", User32.MOD_WIN, KeyBacktick, host.ShowSwitcher, catchWhenWindowsOwnsIt: true));
        return list;
    }

    private static void SwitchToDockItem(ShellHost host, int index)
    {
        List<DockItem> items = DockModel.Build(host.Pins.Pins, host.Tasks.Groups);
        if (index >= items.Count)
        {
            return;
        }

        DockItem item = items[index];
        TaskGroup? group = item.Group;
        if (group is null || group.Windows.Count == 0)
        {
            if (item.LaunchTarget is not null)
            {
                Launcher.Start(item.LaunchTarget);
            }

            return;
        }

        if (!group.IsFocused)
        {
            // Front-most window of the app first.
            WindowActivator.BringToFront(group.Windows[0].Handle);
            return;
        }

        if (group.Windows.Count == 1)
        {
            User32.ShowWindow(group.Windows[0].Handle, User32.SW_MINIMIZE);
            return;
        }

        // Windows are listed front-most first; the back-most one is the next in turn,
        // so repeated presses step through all of them.
        WindowActivator.BringToFront(group.Windows[group.Windows.Count - 1].Handle);
    }

    private static void StartNewCopy(ShellHost host, int index)
    {
        List<DockItem> items = DockModel.Build(host.Pins.Pins, host.Tasks.Groups);
        if (index >= items.Count)
        {
            return;
        }

        string? target = items[index].LaunchTarget ?? items[index].Path;
        if (target is not null)
        {
            Launcher.Start(target);
        }
    }

    private static void NextWindow(ShellHost host)
    {
        // A steady order: the bar's app order, and within an app by window handle (the
        // front-most-first order changes with every switch and would only flip between two).
        var windows = new List<nint>();
        foreach (DockItem item in DockModel.Build(host.Pins.Pins, host.Tasks.Groups))
        {
            if (item.Group is null)
            {
                continue;
            }

            var own = new List<nint>();
            foreach (TaskWindow window in item.Group.Windows)
            {
                if (!window.IsMinimized)
                {
                    own.Add(window.Handle);
                }
            }

            own.Sort();
            windows.AddRange(own);
        }

        if (windows.Count == 0)
        {
            return;
        }

        int current = windows.IndexOf(User32.GetForegroundWindow());
        nint next = windows[(current + 1) % windows.Count];
        WindowActivator.BringToFront(next);
    }

    private static void LockPc()
    {
        if (!User32.LockWorkStation())
        {
            Log.Warn("Win+L: Windows did not lock the PC (error " + Marshal.GetLastPInvokeError() + ").");
        }
    }

    /// <summary>Windows' own Run box, through the Shell's scripting object (Explorer shows it).</summary>
    private static void ShowRunBox()
    {
        object? shell = null;
        try
        {
            Type? type = Type.GetTypeFromProgID("Shell.Application");
            if (type is null)
            {
                Log.Warn("Win+R: Windows' Shell object is not available.");
                return;
            }

            shell = Activator.CreateInstance(type);
            if (shell is null)
            {
                Log.Warn("Win+R: Windows' Shell object could not be created.");
                return;
            }

            type.InvokeMember("FileRun", BindingFlags.InvokeMethod, null, shell, null);
        }
        catch (Exception ex)
        {
            Log.Error("Win+R: the Run box could not be shown.", ex);
        }
        finally
        {
            if (shell is not null && Marshal.IsComObject(shell))
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
    }
}
