using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Machitan.Core;
using Machitan.Interop;

namespace Machitan.Shell;

/// <summary>
/// Hides Explorer's own taskbar while Machitan runs and puts it back afterwards.
///
/// Hiding the window alone would leave Explorer's strip of the screen reserved, so the
/// taskbar is first switched to auto-hide (which frees the space) and then hidden.
/// The state it had before is kept in a small file, so that any process can restore it:
/// the shell on a clean exit, or the guardian after a crash.
/// Nothing here throws.
/// </summary>
internal static class ExplorerTaskbar
{
    private const string PrimaryClass = "Shell_TrayWnd";
    private const string SecondaryClass = "Shell_SecondaryTrayWnd";

    /// <summary>Switches the taskbar to auto-hide and hides it.</summary>
    internal static void Hide()
    {
        try
        {
            nint tray = User32.FindWindow(PrimaryClass, null);
            if (tray == 0)
            {
                Log.Warn("Explorer's taskbar was not found; nothing to hide.");
                return;
            }

            uint current = GetState(tray);
            SaveOriginalStateOnce(current);

            if ((current & Shell32.ABS_AUTOHIDE) == 0)
            {
                SetState(tray, current | Shell32.ABS_AUTOHIDE);
            }

            int hidden = HideVisibleWindows();
            Log.Info("Explorer's taskbar set to auto-hide and hidden ("
                + hidden.ToString(CultureInfo.InvariantCulture)
                + " window(s) were showing at that moment; any that reappear are hidden again within a quarter second).");
        }
        catch (Exception ex)
        {
            Log.Error("Explorer's taskbar could not be hidden.", ex);
        }
    }

    /// <summary>
    /// Explorer shows its taskbar again on its own now and then (and after an Explorer
    /// restart). Called from a timer to hide it again. Cheap when nothing is visible.
    /// </summary>
    internal static void EnsureHidden()
    {
        try
        {
            HideVisibleWindows();
        }
        catch (Exception)
        {
            // Called several times a second; a failure here is not worth a log line each time.
        }
    }

    /// <summary>Puts the taskbar back the way it was. Safe to call more than once.</summary>
    internal static void Restore()
    {
        try
        {
            nint tray = User32.FindWindow(PrimaryClass, null);

            if (tray != 0 && TryReadOriginalState(out uint original))
            {
                SetState(tray, original);
            }

            DeleteStateFile();

            foreach (nint window in FindTaskbarWindows())
            {
                User32.ShowWindow(window, User32.SW_SHOWNA);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Explorer's taskbar could not be restored.", ex);
        }
    }

    private static int HideVisibleWindows()
    {
        int hidden = 0;
        foreach (nint window in FindTaskbarWindows())
        {
            if (User32.IsWindowVisible(window))
            {
                User32.ShowWindow(window, User32.SW_HIDE);
                hidden++;
            }
        }

        return hidden;
    }

    private static List<nint> FindTaskbarWindows()
    {
        var windows = new List<nint>(4);

        nint primary = User32.FindWindow(PrimaryClass, null);
        if (primary != 0)
        {
            windows.Add(primary);
        }

        // One secondary taskbar per extra monitor.
        nint secondary = 0;
        for (int guard = 0; guard < 16; guard++)
        {
            secondary = User32.FindWindowEx(0, secondary, SecondaryClass, null);
            if (secondary == 0)
            {
                break;
            }

            windows.Add(secondary);
        }

        return windows;
    }

    private static uint GetState(nint tray)
    {
        APPBARDATA data = NewData(tray);
        return (uint)Shell32.SHAppBarMessage(Shell32.ABM_GETSTATE, ref data);
    }

    private static void SetState(nint tray, uint state)
    {
        APPBARDATA data = NewData(tray);
        data.lParam = (nint)state;
        Shell32.SHAppBarMessage(Shell32.ABM_SETSTATE, ref data);
    }

    private static APPBARDATA NewData(nint tray)
    {
        return new APPBARDATA
        {
            cbSize = (uint)Marshal.SizeOf<APPBARDATA>(),
            hWnd = tray,
        };
    }

    /// <summary>
    /// If the file is already there, an earlier run ended without restoring. The state on
    /// file is then the real original, and the current state is our own change: keep the file.
    /// </summary>
    private static void SaveOriginalStateOnce(uint state)
    {
        if (File.Exists(AppPaths.TaskbarStateFile))
        {
            Log.Info("Explorer's taskbar state from an earlier run is still on file; keeping it.");
            return;
        }

        AppPaths.EnsureDataFolder();
        File.WriteAllText(AppPaths.TaskbarStateFile, state.ToString(CultureInfo.InvariantCulture));
    }

    private static bool TryReadOriginalState(out uint state)
    {
        state = 0;
        if (!File.Exists(AppPaths.TaskbarStateFile))
        {
            return false;
        }

        string text = File.ReadAllText(AppPaths.TaskbarStateFile).Trim();
        return uint.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out state);
    }

    private static void DeleteStateFile()
    {
        if (File.Exists(AppPaths.TaskbarStateFile))
        {
            File.Delete(AppPaths.TaskbarStateFile);
        }
    }
}
