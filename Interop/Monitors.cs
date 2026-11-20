using System.Runtime.InteropServices;

namespace Machitan.Interop;

/// <summary>winuser.h MONITORINFO (40 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MONITORINFO
{
    public int cbSize;
    public RECT rcMonitor;
    public RECT rcWork;
    public uint dwFlags;
}

/// <summary>user32.dll: which screen a window is on, and the part of it windows may use.</summary>
internal static partial class User32
{
    internal const uint MONITOR_DEFAULTTOPRIMARY = 1;

    [LibraryImport("user32.dll")]
    internal static partial nint MonitorFromWindow(nint hwnd, uint flags);

    internal const uint MONITOR_DEFAULTTONEAREST = 2;

    [LibraryImport("user32.dll")]
    internal static partial nint MonitorFromPoint(POINT point, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetMonitorInfo(nint monitor, ref MONITORINFO info);

    /// <summary>The whole screen the window is on (or the main screen when the window is 0). Physical pixels.</summary>
    internal static bool TryGetMonitorRect(nint hwnd, out RECT monitorRect)
    {
        monitorRect = default;
        nint monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTOPRIMARY);
        if (monitor == 0)
        {
            return false;
        }

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        monitorRect = info.rcMonitor;
        return true;
    }

    /// <summary>The whole screen a point is on (the nearest screen when it is on none). Physical pixels.</summary>
    internal static bool TryGetMonitorRectAt(POINT point, out RECT monitorRect)
    {
        monitorRect = default;
        nint monitor = MonitorFromPoint(point, MONITOR_DEFAULTTONEAREST);
        if (monitor == 0)
        {
            return false;
        }

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        monitorRect = info.rcMonitor;
        return true;
    }

    /// <summary>
    /// The work area (the screen minus the strips taskbars keep for themselves) of the screen
    /// the window is on, or of the main screen when the window is 0. Physical pixels.
    /// </summary>
    internal static bool TryGetWorkArea(nint hwnd, out RECT workArea)
    {
        workArea = default;
        nint monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTOPRIMARY);
        if (monitor == 0)
        {
            return false;
        }

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            return false;
        }

        workArea = info.rcWork;
        return true;
    }
}
