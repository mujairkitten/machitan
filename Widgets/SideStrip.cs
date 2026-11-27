using System;
using System.Runtime.InteropServices;
using Machitan.Core;
using Machitan.Interop;
using Machitan.Shell;

namespace Machitan.Widgets;

/// <summary>
/// Keeps a strip at the left or right edge of the bar's screen for the docked widget sidebar,
/// through the Windows AppBar system, so maximised windows stop beside it. Made like the top
/// strip (TopStrip.cs), which works through Machitan's tray window by AppBarRelay: an AppBar of
/// its own, owned by a see-through, click-through window made here and kept on the strip
/// behind every other window. The sidebar's own window is shown on top of it. UI thread only.
/// </summary>
internal sealed class SideStrip : IDisposable
{
    private const uint CallbackMessage = User32.WM_APP + 0x53;
    private const uint AbmWindowPosChanged = 9;
    private const nint HwndBottom = 1;
    private const uint SwpShowWindow = 0x0040;

    private nint _hwnd;
    private WindowSubclass? _subclass;
    private bool _registered;
    private RECT _monitor;
    private int _thickness;
    private bool _left;
    private RECT _granted;
    private bool _windowFailed;

    /// <summary>Raised when Windows moved the strip (another app bar on that side came or went).</summary>
    internal event Action? Moved;

    /// <summary>The strip Windows gave, or null when none is kept.</summary>
    internal RECT? Granted => _registered ? _granted : null;

    /// <summary>Keeps a strip <paramref name="thickness"/> pixels wide at the left (or right) of the monitor; 0 gives it back.</summary>
    internal void Set(RECT monitor, int thickness, bool left)
    {
        if (thickness <= 0)
        {
            Release();
            return;
        }

        if (_registered && left != _left)
        {
            // The other side: give this one back and take that one.
            Release();
        }

        if (_registered && thickness == _thickness && monitor.SameAs(_monitor))
        {
            return;
        }

        _left = left;
        if (!EnsureWindow())
        {
            return;
        }

        if (!_registered)
        {
            APPBARDATA registration = NewData();
            registration.uCallbackMessage = CallbackMessage;
            if (Shell32.SHAppBarMessage(Shell32.ABM_NEW, ref registration) == 0)
            {
                Log.Warn("Sidebar strip: Windows refused the reservation; maximised windows can reach under the docked sidebar.");
                return;
            }

            _registered = true;
        }

        _monitor = monitor;
        _thickness = thickness;
        Apply();
        Log.Info("Sidebar strip: " + thickness + " px kept free at the " + (_left ? "left" : "right") + " for the docked sidebar.");
    }

    /// <summary>Gives the strip back. Safe to call when nothing is kept.</summary>
    internal void Release()
    {
        if (!_registered)
        {
            return;
        }

        APPBARDATA data = NewData();
        Shell32.SHAppBarMessage(Shell32.ABM_REMOVE, ref data);
        _registered = false;
        if (_hwnd != 0)
        {
            User32.ShowWindow(_hwnd, User32.SW_HIDE);
        }

        Log.Info("Sidebar strip: given back.");
    }

    public void Dispose()
    {
        Release();
        _subclass?.Dispose();
        _subclass = null;
        if (_hwnd != 0)
        {
            User32.DestroyWindow(_hwnd);
            _hwnd = 0;
        }
    }

    /// <summary>Asks Windows for the strip (it may shift it inside other app bars on that side), then takes it.</summary>
    private void Apply()
    {
        APPBARDATA data = Proposed();
        Shell32.SHAppBarMessage(Shell32.ABM_QUERYPOS, ref data);
        if (_left)
        {
            data.rc.Right = data.rc.Left + _thickness;
        }
        else
        {
            data.rc.Left = data.rc.Right - _thickness;
        }

        Shell32.SHAppBarMessage(Shell32.ABM_SETPOS, ref data);
        _granted = data.rc;

        if (!User32.SetWindowPos(_hwnd, HwndBottom, _granted.Left, _granted.Top, _granted.Width, _granted.Height,
            User32.SWP_NOACTIVATE | User32.SWP_NOOWNERZORDER | SwpShowWindow))
        {
            Log.Warn("Sidebar strip: its window could not be put on the strip (error " + Marshal.GetLastSystemError() + ").");
        }

        APPBARDATA moved = NewData();
        Shell32.SHAppBarMessage(AbmWindowPosChanged, ref moved);
    }

    private APPBARDATA Proposed()
    {
        APPBARDATA data = NewData();
        data.uEdge = _left ? Shell32.ABE_LEFT : Shell32.ABE_RIGHT;
        data.rc = _monitor;
        if (_left)
        {
            data.rc.Right = _monitor.Left + _thickness;
        }
        else
        {
            data.rc.Left = _monitor.Right - _thickness;
        }

        return data;
    }

    /// <summary>The window that owns the AppBar and receives its notices: see-through and click-through.</summary>
    private bool EnsureWindow()
    {
        if (_hwnd != 0)
        {
            return true;
        }

        if (_windowFailed)
        {
            return false;
        }

        _hwnd = User32.CreateWindowEx(
            (uint)(User32.WS_EX_TOOLWINDOW | User32.WS_EX_NOACTIVATE | User32.WS_EX_LAYERED | User32.WS_EX_TRANSPARENT),
            "STATIC",
            "Machitan sidebar strip",
            unchecked((uint)User32.WS_POPUP),
            0,
            0,
            0,
            0,
            0,
            0,
            Kernel32.GetModuleHandleW(null),
            0);
        if (_hwnd == 0)
        {
            _windowFailed = true;
            Log.Warn("Sidebar strip: its window could not be made (error " + Marshal.GetLastPInvokeError() + "); the docked sidebar keeps no strip.");
            return false;
        }

        User32.SetLayeredWindowAttributes(_hwnd, 0, 0, User32.LWA_ALPHA);
        _subclass = new WindowSubclass(_hwnd, OnMessage);
        return true;
    }

    private bool OnMessage(uint message, nint wParam, nint lParam, out nint result)
    {
        result = 0;
        if (message != CallbackMessage)
        {
            return false;
        }

        if ((uint)wParam == Shell32.ABN_POSCHANGED && _registered)
        {
            // Only act when Windows would now put the strip elsewhere, so two app bars never keep answering each other.
            APPBARDATA data = Proposed();
            Shell32.SHAppBarMessage(Shell32.ABM_QUERYPOS, ref data);
            bool moved = _left ? data.rc.Left != _granted.Left : data.rc.Right != _granted.Right;
            if (moved)
            {
                Apply();
                Moved?.Invoke();
            }
        }

        return true;
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
