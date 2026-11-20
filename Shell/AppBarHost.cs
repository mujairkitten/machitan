using System;
using System.Runtime.InteropServices;
using Machitan.Core;
using Machitan.Interop;

namespace Machitan.Shell;

/// <summary>
/// Reserves a strip of the screen for the bar through the Windows AppBar system
/// (SHAppBarMessage), so maximised windows stop above it. Used only when auto-hide is off;
/// an auto-hiding bar reserves nothing. While Machitan's tray receiver runs, the messages reach
/// its "Shell_TrayWnd" window first and AppBarRelay passes them on to Explorer.
/// </summary>
internal sealed class AppBarHost
{
    private readonly nint _hwnd;
    private readonly uint _callbackMessage;

    private bool _registered;
    private uint _edge;
    private RECT _monitor;
    private int _thickness;
    private RECT _granted;
    private long _lastSetTick;

    internal AppBarHost(nint hwnd, uint callbackMessage)
    {
        _hwnd = hwnd;
        _callbackMessage = callbackMessage;
    }

    internal bool IsRegistered => _registered;

    /// <summary>True for a short moment after we changed the reservation ourselves.</summary>
    internal bool ChangedJustNow => Environment.TickCount64 - _lastSetTick < 500;

    /// <summary>
    /// Reserves a strip of <paramref name="thickness"/> pixels on the given edge of the monitor.
    /// Returns the strip Windows granted (it can be shifted by other app bars), or null when
    /// registration was refused.
    /// </summary>
    internal RECT? Reserve(uint edge, RECT monitor, int thickness, bool force)
    {
        if (!_registered)
        {
            APPBARDATA registration = NewData();
            registration.uCallbackMessage = _callbackMessage;
            if (Shell32.SHAppBarMessage(Shell32.ABM_NEW, ref registration) == 0)
            {
                Log.Warn("AppBar registration was refused by Windows; no screen space is reserved.");
                return null;
            }

            _registered = true;
            force = true;
            Log.Info("AppBar registered.");
        }

        if (!force && edge == _edge && thickness == _thickness && monitor.SameAs(_monitor))
        {
            return _granted;
        }

        APPBARDATA data = NewData();
        data.uEdge = edge;
        data.rc = monitor;
        if (edge == Shell32.ABE_TOP)
        {
            data.rc.Bottom = monitor.Top + thickness;
        }
        else
        {
            data.rc.Top = monitor.Bottom - thickness;
        }

        // Windows may move the rectangle to make room for other app bars...
        Shell32.SHAppBarMessage(Shell32.ABM_QUERYPOS, ref data);

        // ...so set our thickness again from the edge it settled on.
        if (edge == Shell32.ABE_TOP)
        {
            data.rc.Bottom = data.rc.Top + thickness;
        }
        else
        {
            data.rc.Top = data.rc.Bottom - thickness;
        }

        Shell32.SHAppBarMessage(Shell32.ABM_SETPOS, ref data);

        _edge = edge;
        _monitor = monitor;
        _thickness = thickness;
        _granted = data.rc;
        _lastSetTick = Environment.TickCount64;
        return _granted;
    }

    /// <summary>Gives the reserved strip back. Safe to call when nothing is reserved.</summary>
    internal void Release()
    {
        if (!_registered)
        {
            return;
        }

        APPBARDATA data = NewData();
        Shell32.SHAppBarMessage(Shell32.ABM_REMOVE, ref data);
        _registered = false;
        _lastSetTick = Environment.TickCount64;
        Log.Info("AppBar released.");
    }

    private APPBARDATA NewData()
    {
        return new APPBARDATA
        {
            cbSize = (uint)Marshal.SizeOf<APPBARDATA>(),
            hWnd = _hwnd,
        };
    }
}
