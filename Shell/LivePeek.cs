using System;
using Machitan.Core;
using Machitan.Interop;

namespace Machitan.Shell;

/// <summary>
/// Windows' own Peek, drawn by DWM (UI thread). One window is shown as it is and every other
/// window turns see-through, so it is seen in place: nothing is moved, activated, restored or
/// minimised, so ending the peek puts nothing back. Explorer's taskbar uses the same call for
/// its thumbnail peek and, on Windows 7 and 10, for "Peek at desktop".
///
/// The call is undocumented (dwmapi.dll ordinal 113, see Dwmapi.DwmpActivateLivePreview). Its
/// fourth value is the kind of peek; third-party taskbars use 1 to peek at the desktop and 3
/// to peek at a window. Neither is promised, so when Windows refuses one kind the other is
/// tried once, and the one that worked is kept for the run (the log names it). When Windows
/// refuses both (an error code, or the call is missing from a later Windows), Start returns
/// false and the caller does without; the log says so once a run. Windows' own Peek setting
/// (Performance Options > Enable Peek) can turn it off for every program.
///
/// Machitan's own shell windows that are on screen (its visible tool windows: the bar,
/// Dynaland, the corner widgets, flyouts) are kept from fading with the documented
/// DWMWA_EXCLUDED_FROM_PEEK, as Explorer's taskbar is.
/// </summary>
internal static class LivePeek
{
    /// <summary>The peek kind third-party taskbars ask for at the desktop.</summary>
    private const uint KindDesktop = 1;

    /// <summary>The peek kind third-party taskbars ask for at a window (from a thumbnail).</summary>
    private const uint KindWindow = 3;

    private static nint s_window;
    private static nint s_keepAbove;
    private static uint s_kind;
    private static bool s_missing;
    private static bool s_refusedNoted;

    // The kind that worked for each use, once one has (0: not known yet).
    private static uint s_desktopKind;
    private static uint s_windowKind;

    /// <summary>True while a peek is shown.</summary>
    internal static bool IsActive { get; private set; }

    /// <summary>
    /// Peeks at <paramref name="window"/> (<paramref name="desktop"/>: the desktop window, every
    /// other window see-through); <paramref name="keepAbove"/> stays solid on top. False when
    /// Windows refused (the caller then does without). <paramref name="what"/> is for the log.
    /// </summary>
    internal static bool Start(nint window, nint keepAbove, bool desktop, string what)
    {
        if (s_missing || window == 0 || !User32.IsWindow(window))
        {
            return false;
        }

        KeepOwnWindowsSolid();

        uint known = desktop ? s_desktopKind : s_windowKind;
        uint first = known != 0 ? known : desktop ? KindDesktop : KindWindow;
        uint second = first == KindDesktop ? KindWindow : KindDesktop;

        int result;
        uint kind = first;
        try
        {
            result = Dwmapi.DwmpActivateLivePreview(1, window, keepAbove, kind, 0);
            if (result != 0 && known == 0)
            {
                // Not this kind on this Windows: the other one, once.
                kind = second;
                result = Dwmapi.DwmpActivateLivePreview(1, window, keepAbove, kind, 0);
            }
        }
        catch (Exception ex)
        {
            s_missing = true;
            Log.Warn("Peek: Windows' own peek is not available on this Windows (" + ex.GetType().Name
                + "); Machitan does without it for the rest of this run.");
            return false;
        }

        if (result != 0)
        {
            if (!s_refusedNoted)
            {
                s_refusedNoted = true;
                Log.Warn("Peek: Windows refused its own peek (0x" + result.ToString("X8", System.Globalization.CultureInfo.InvariantCulture)
                    + ", " + what + "); Windows' Peek setting may be off (Performance Options > Enable Peek).");
            }

            return false;
        }

        if (desktop)
        {
            s_desktopKind = kind;
        }
        else
        {
            s_windowKind = kind;
        }

        s_window = window;
        s_keepAbove = keepAbove;
        s_kind = kind;
        IsActive = true;
        if (known == 0)
        {
            Log.Info("Peek: Windows' own peek works (first this run: " + what + ", kind " + kind.ToString(System.Globalization.CultureInfo.InvariantCulture) + ").");
        }

        return true;
    }

    /// <summary>Ends the peek; every window looks as before. Does nothing when none is shown.</summary>
    internal static void End()
    {
        if (!IsActive)
        {
            return;
        }

        IsActive = false;
        try
        {
            Dwmapi.DwmpActivateLivePreview(0, s_window, s_keepAbove, s_kind, 0);
        }
        catch (Exception ex)
        {
            Log.Warn("Peek: Windows' own peek could not be ended.", ex);
        }

        s_window = 0;
        s_keepAbove = 0;
        s_kind = 0;
    }

    /// <summary>Machitan's visible tool windows (the bar, widgets, flyouts) do not fade during a peek.</summary>
    private static void KeepOwnWindowsSolid()
    {
        uint self = (uint)Environment.ProcessId;
        foreach (nint hwnd in WindowInfo.EnumerateTopLevel())
        {
            User32.GetWindowThreadProcessId(hwnd, out uint process);
            if (process != self || !User32.IsWindowVisible(hwnd)
                || (User32.GetWindowLongPtrW(hwnd, User32.GWL_EXSTYLE) & User32.WS_EX_TOOLWINDOW) == 0)
            {
                continue;
            }

            int excluded = 1;
            Dwmapi.DwmSetWindowAttribute(hwnd, Dwmapi.DWMWA_EXCLUDED_FROM_PEEK, ref excluded, sizeof(int));
        }
    }
}
