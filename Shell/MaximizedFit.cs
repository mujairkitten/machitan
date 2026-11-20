using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Machitan.Core;
using Machitan.Interop;

namespace Machitan.Shell;

/// <summary>
/// Keeps windows that fill the screen out of a strip Machitan keeps free (Settings > Edge strip).
///
/// Windows re-fits truly maximised windows when an app bar takes space, but not always, and
/// not every window that fills the screen is maximised in Windows' sense: an app can size its
/// window to the work area itself (Electron apps with a see-through window do that for
/// "maximise", Claude's among them), and such a window keeps that size when the work area
/// changes. Seen 2026-11-20: Claude's window stayed under the corner widgets, and the checks
/// of Parts 10 and 11 found no maximised window at all (they looked at maximised ones only).
///
/// So two kinds of window are moved out of the strip, the rest left alone:
/// - maximised windows (kept maximised): fitted to the work area on every side, but only once
///   Windows took the strip off the work area. Before that (2026-11-20, Part 12) moving them
///   by hand showed the band apps keep under a maximised window's top edge, left a gap above
///   the bar, and some moved straight back (Claude): Windows and the app must agree on where
///   "maximised" is;
/// - ordinary windows that look maximised: as wide as the free area, at least 60% of its
///   height, reaching into the strip. Only their top (or bottom) edge moves.
/// Full-screen windows (covering the whole screen, as the bar judges them), tool windows,
/// always-on-top windows, click-through overlays and windows owned by another window are left
/// alone, and so is a window that moves back three times within 10 s.
///
/// The free area is the work area, made smaller by the strip itself when Windows did not take
/// the strip off the work area, so the strip stays clear either way.
/// Use on the UI thread.
/// </summary>
internal static unsafe partial class MaximizedFit
{
    private const uint DwmwaExtendedFrameBounds = 9;
    private const int MaxListed = 5;
    private const int MaxFitsPerWindow = 3;
    private const long FitWindowMs = 10000;
    private const double MinHeightShare = 0.6;
    private const uint GuiInMoveSize = 0x00000002;

    private static readonly Dictionary<nint, (long Since, int Count)> s_fits = new();
    private static readonly HashSet<nint> s_gaveUp = new();
    private static readonly HashSet<string> s_programsNoted = new(StringComparer.OrdinalIgnoreCase);

    private enum Outcome
    {
        Clear,
        Fitted,
        LeftAlone,
    }

    /// <summary>
    /// Looks at every window on the strip's screen and fits the ones that reach into it.
    /// Logs one line: the free area, and which windows reached into the strip and why each was
    /// fitted or left alone.
    /// </summary>
    internal static void Run(nint anyWindowThere, RECT strip, bool stripOnTop, string why)
    {
        if (!TryGetFreeArea(anyWindowThere, strip, stripOnTop, out RECT free, out RECT work, out RECT monitor, out nint screen))
        {
            return;
        }

        bool windowsTookIt = WindowsTookStrip(work, strip, stripOnTop);
        int maximised = 0;
        var fitted = new List<string>();
        var leftAlone = new List<string>();
        foreach (nint hwnd in WindowInfo.EnumerateTopLevel())
        {
            try
            {
                if (!IsWindowToLookAt(hwnd, screen, out uint process))
                {
                    continue;
                }

                bool zoomed = User32.IsZoomed(hwnd);
                if (zoomed)
                {
                    maximised++;
                }

                Outcome outcome = Fit(hwnd, zoomed, windowsTookIt, free, monitor, stripOnTop, out string reason);
                if (outcome == Outcome.Fitted)
                {
                    fitted.Add(ProgramOf(process) + (zoomed ? " (maximised)" : " (filling the screen)"));
                }
                else if (outcome == Outcome.LeftAlone && leftAlone.Count < MaxListed)
                {
                    leftAlone.Add(ProgramOf(process) + " (" + reason + ")");
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Strip: a window could not be checked.", ex);
            }
        }

        string area = "free area " + Describe(free) + " (work area " + Describe(work) + ", strip " + Describe(strip)
            + (windowsTookIt ? "; Windows took the strip off the work area)" : "; Windows did NOT take the strip off the work area)");
        if (fitted.Count == 0 && leftAlone.Count == 0)
        {
            Log.Info("Strip check (" + why + "): no window reaches into the strip; " + maximised + " maximised window(s) on this screen; " + area + ".");
            return;
        }

        Log.Info("Strip check (" + why + "): "
            + (fitted.Count > 0 ? "fitted " + string.Join(", ", fitted) : "nothing fitted")
            + (leftAlone.Count > 0 ? "; left alone: " + string.Join(", ", leftAlone) : string.Empty)
            + "; " + area + ".");
    }

    /// <summary>
    /// Looks at one window that just appeared, moved or changed size, and fits it when it
    /// reaches into the strip. Logs the first fit of each program this run.
    /// </summary>
    internal static void Check(nint hwnd, nint anyWindowThere, RECT strip, bool stripOnTop)
    {
        if (!TryGetFreeArea(anyWindowThere, strip, stripOnTop, out RECT free, out RECT work, out RECT monitor, out nint screen)
            || !IsWindowToLookAt(hwnd, screen, out uint process))
        {
            return;
        }

        bool windowsTookIt = WindowsTookStrip(work, strip, stripOnTop);

        // Being dragged or sized right now: left to the person; looked at when that ends.
        uint thread = User32.GetWindowThreadProcessId(hwnd, out _);
        var gui = new GuiThreadInfo { cbSize = (uint)sizeof(GuiThreadInfo) };
        if (thread != 0 && GetGuiThreadInfo(thread, ref gui) && (gui.flags & GuiInMoveSize) != 0)
        {
            return;
        }

        bool zoomed = User32.IsZoomed(hwnd);
        if (Fit(hwnd, zoomed, windowsTookIt, free, monitor, stripOnTop, out _) == Outcome.Fitted)
        {
            string program = ProgramOf(process);
            if (s_programsNoted.Add(program))
            {
                Log.Info("Strip: a window of " + program + (zoomed ? " (maximised)" : " (filling the screen)")
                    + " reached into the strip; fitted to the free area " + Describe(free) + ". (Logged once per program.)");
            }
        }
    }

    /// <summary>The work area, made smaller by the strip where Windows did not take it off.</summary>
    private static bool TryGetFreeArea(nint anyWindowThere, RECT strip, bool stripOnTop, out RECT free, out RECT work, out RECT monitor, out nint screen)
    {
        free = default;
        monitor = default;
        screen = 0;
        if (!User32.TryGetWorkArea(anyWindowThere, out work) || !User32.TryGetMonitorRect(anyWindowThere, out monitor))
        {
            return false;
        }

        screen = User32.MonitorFromWindow(anyWindowThere, User32.MONITOR_DEFAULTTOPRIMARY);
        free = work;
        bool onThisScreen = strip.Width > 0 && strip.Height > 0 && strip.Left < monitor.Right && strip.Right > monitor.Left;
        if (onThisScreen)
        {
            if (stripOnTop)
            {
                free.Top = Math.Max(free.Top, strip.Bottom);
            }
            else
            {
                free.Bottom = Math.Min(free.Bottom, strip.Top);
            }
        }

        return screen != 0 && free.Height > 100 && free.Width > 100;
    }

    /// <summary>True when the work area no longer reaches into the strip (Windows took it off).</summary>
    private static bool WindowsTookStrip(RECT work, RECT strip, bool stripOnTop)
    {
        return stripOnTop ? work.Top >= strip.Bottom - 1 : work.Bottom <= strip.Top + 1;
    }

    /// <summary>A shown, not minimised top-level window of another program on that screen, not Windows' desktop or taskbar.</summary>
    private static bool IsWindowToLookAt(nint hwnd, nint screen, out uint process)
    {
        process = 0;
        if (!User32.IsWindowVisible(hwnd) || User32.IsIconic(hwnd)
            || User32.GetWindowThreadProcessId(hwnd, out process) == 0
            || process == (uint)Environment.ProcessId
            || User32.MonitorFromWindow(hwnd, User32.MONITOR_DEFAULTTOPRIMARY) != screen
            || WindowInfo.IsCloaked(hwnd))
        {
            return false;
        }

        string className = WindowInfo.GetClassName(hwnd);
        return className is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd");
    }

    private static Outcome Fit(nint hwnd, bool zoomed, bool windowsTookIt, RECT free, RECT monitor, bool stripOnTop, out string reason)
    {
        reason = string.Empty;
        if (!User32.GetWindowRect(hwnd, out RECT outer))
        {
            return Outcome.Clear;
        }

        // What shows on screen (windows reach past it by their invisible sizing frame).
        RECT visible = outer;
        if (DwmGetFrameBounds(hwnd, DwmwaExtendedFrameBounds, out RECT bounds, (uint)sizeof(RECT)) == 0)
        {
            visible = bounds;
        }

        bool intoStrip = stripOnTop
            ? visible.Top < free.Top - 1 && visible.Bottom > free.Top
            : visible.Bottom > free.Bottom + 1 && visible.Top < free.Bottom;
        bool outsideFree = visible.Top < free.Top - 1 || visible.Bottom > free.Bottom + 1
            || visible.Left < free.Left - 1 || visible.Right > free.Right + 1;
        if (!intoStrip && !(zoomed && outsideFree))
        {
            return Outcome.Clear;
        }

        long style = User32.GetWindowLongPtrW(hwnd, User32.GWL_STYLE);
        long extended = User32.GetWindowLongPtrW(hwnd, User32.GWL_EXSTYLE);
        // Covering the whole screen: a full-screen app (a game, a video, a browser after F11),
        // the same rule as the bar's. A maximised window with a title bar or sizing border that
        // grew to the whole screen (no app bar left for a moment) is not one.
        bool coversScreen = visible.Left <= monitor.Left && visible.Top <= monitor.Top
            && visible.Right >= monitor.Right && visible.Bottom >= monitor.Bottom;
        if (coversScreen && (!zoomed || (style & (User32.WS_CAPTION | User32.WS_THICKFRAME)) == 0))
        {
            reason = "full screen";
            return Outcome.LeftAlone;
        }

        if (zoomed && !windowsTookIt)
        {
            reason = "maximised; Windows did not take the strip off the work area, so it is left to Windows";
            return Outcome.LeftAlone;
        }

        if (!zoomed)
        {
            string? notLikeMaximised = NotLikeMaximised(hwnd, visible, free, extended);
            if (notLikeMaximised is not null)
            {
                reason = notLikeMaximised;
                return Outcome.LeftAlone;
            }
        }

        if (s_gaveUp.Contains(hwnd))
        {
            reason = "it moved back each time";
            return Outcome.LeftAlone;
        }

        int top = Math.Max(0, free.Top - visible.Top);
        int bottom = Math.Max(0, visible.Bottom - free.Bottom);
        int left = zoomed ? Math.Max(0, free.Left - visible.Left) : 0;
        int right = zoomed ? Math.Max(0, visible.Right - free.Right) : 0;
        if (top <= 1 && bottom <= 1 && left <= 1 && right <= 1)
        {
            return Outcome.Clear;
        }

        int width = outer.Width - left - right;
        int height = outer.Height - top - bottom;
        if (width < 100 || height < 100)
        {
            reason = "too small once fitted";
            return Outcome.LeftAlone;
        }

        if (!User32.SetWindowPos(hwnd, 0, outer.Left + left, outer.Top + top, width, height,
            User32.SWP_NOZORDER | User32.SWP_NOACTIVATE | User32.SWP_NOOWNERZORDER))
        {
            int error = Marshal.GetLastSystemError();
            reason = error == 5 ? "Windows refused: it runs as administrator" : "Windows refused (error " + error + ")";
            return Outcome.LeftAlone;
        }

        NoteFit(hwnd);
        return Outcome.Fitted;
    }

    /// <summary>Null when an ordinary window looks maximised; else why it is left where it is.</summary>
    private static string? NotLikeMaximised(nint hwnd, RECT visible, RECT free, long extended)
    {
        if ((extended & (User32.WS_EX_TOOLWINDOW | User32.WS_EX_NOACTIVATE)) != 0)
        {
            return "a tool window";
        }

        if ((extended & User32.WS_EX_TOPMOST) != 0)
        {
            return "always on top";
        }

        if ((extended & User32.WS_EX_LAYERED) != 0 && (extended & User32.WS_EX_TRANSPARENT) != 0)
        {
            return "a click-through overlay";
        }

        if (User32.GetWindow(hwnd, User32.GW_OWNER) != 0)
        {
            return "a dialog or popup of another window";
        }

        bool fullWidth = visible.Left <= free.Left + 2 && visible.Right >= free.Right - 2;
        bool tall = visible.Height >= free.Height * MinHeightShare;
        if (!fullWidth || !tall)
        {
            return "an ordinary window placed there, not filling the screen";
        }

        return null;
    }

    /// <summary>Counts fits per window; a window fitted three times within 10 s is left alone from then on.</summary>
    private static void NoteFit(nint hwnd)
    {
        long now = Environment.TickCount64;
        if (s_fits.Count > 200)
        {
            var old = new List<nint>();
            foreach (KeyValuePair<nint, (long Since, int Count)> entry in s_fits)
            {
                if (now - entry.Value.Since > FitWindowMs)
                {
                    old.Add(entry.Key);
                }
            }

            foreach (nint window in old)
            {
                s_fits.Remove(window);
            }
        }

        if (!s_fits.TryGetValue(hwnd, out (long Since, int Count) fits) || now - fits.Since > FitWindowMs)
        {
            fits = (now, 0);
        }

        fits.Count++;
        s_fits[hwnd] = fits;
        if (fits.Count >= MaxFitsPerWindow && s_gaveUp.Add(hwnd))
        {
            User32.GetWindowThreadProcessId(hwnd, out uint process);
            Log.Info("Strip: a window of " + ProgramOf(process) + " moved back into the strip " + MaxFitsPerWindow
                + " times within " + (FitWindowMs / 1000) + " s; it is left alone from now on (this run).");
        }
    }

    private static string ProgramOf(uint process)
    {
        return System.IO.Path.GetFileName(WindowInfo.GetProcessPath(process) ?? "?");
    }

    private static string Describe(RECT rect)
    {
        return rect.Left + "," + rect.Top + " to " + rect.Right + "," + rect.Bottom;
    }

    [LibraryImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static partial int DwmGetFrameBounds(nint hwnd, uint attribute, out RECT value, uint size);

    [LibraryImport("user32.dll", EntryPoint = "GetGUIThreadInfo")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetGuiThreadInfo(uint threadId, ref GuiThreadInfo info);

    /// <summary>winuser.h GUITHREADINFO.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct GuiThreadInfo
    {
        public uint cbSize;
        public uint flags;
        public nint hwndActive;
        public nint hwndFocus;
        public nint hwndCapture;
        public nint hwndMenuOwner;
        public nint hwndMoveSize;
        public nint hwndCaret;
        public RECT rcCaret;
    }
}
