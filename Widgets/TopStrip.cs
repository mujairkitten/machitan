using System;
using System.Runtime.InteropServices;
using Machitan.Core;
using Machitan.Interop;
using Machitan.Shell;

namespace Machitan.Widgets;

/// <summary>
/// Keeps a strip at the free edge of the bar's screen (the edge without the bar: the top while
/// the bar is at the bottom, the bottom while it is at the top) for the corner widgets and
/// Dynaland there, through the Windows AppBar system, so maximised windows stay clear of them.
/// It is an AppBar of its own, owned by a small hidden window made here; the bar's own AppBar
/// is never touched. (Named for where it started: the top.) Use on the UI thread.
/// </summary>
internal sealed class TopStrip : IDisposable
{
    private const uint CallbackMessage = User32.WM_APP + 0x52;

    private nint _hwnd;
    private WindowSubclass? _subclass;
    private bool _registered;
    private RECT _monitor;
    private int _thickness;
    private bool _top = true;
    private RECT _granted;
    private bool _windowFailed;

    /// <summary>Raised when Windows moved the strip (another top app bar came or went).</summary>
    internal event Action? Moved;

    /// <summary>The strip Windows gave, or null when none is kept.</summary>
    internal RECT? Granted => _registered ? _granted : null;

    /// <summary>True when the strip is (or would be) at the top edge; false at the bottom.</summary>
    internal bool OnTop => _top;

    /// <summary>
    /// Keeps a strip <paramref name="thickness"/> pixels tall at the top (or bottom) of the
    /// monitor; 0 gives it back.
    /// </summary>
    internal void Set(RECT monitor, int thickness, bool top)
    {
        if (thickness <= 0)
        {
            Release();
            return;
        }

        if (_registered && top != _top)
        {
            // Another edge: give this one back and take the other.
            Release();
        }

        if (_registered && thickness == _thickness && monitor.SameAs(_monitor))
        {
            return;
        }

        _top = top;

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
                Log.Warn("Strip: Windows refused the reservation; maximised windows can reach under the widgets at the free edge.");
                return;
            }

            _registered = true;
        }

        _monitor = monitor;
        _thickness = thickness;
        Apply();
        Log.Info("Strip: " + thickness + " px kept free at the " + (_top ? "top" : "bottom") + " for the corner widgets and Dynaland.");
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
        Log.Info("Strip: given back.");
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

    /// <summary>Asks Windows for the strip (it may shift it below other top app bars), then takes it.</summary>
    private void Apply()
    {
        APPBARDATA data = Proposed();
        Shell32.SHAppBarMessage(Shell32.ABM_QUERYPOS, ref data);
        if (_top)
        {
            data.rc.Bottom = data.rc.Top + _thickness;
        }
        else
        {
            data.rc.Top = data.rc.Bottom - _thickness;
        }

        Shell32.SHAppBarMessage(Shell32.ABM_SETPOS, ref data);
        _granted = data.rc;
    }

    private APPBARDATA Proposed()
    {
        APPBARDATA data = NewData();
        data.uEdge = _top ? Shell32.ABE_TOP : Shell32.ABE_BOTTOM;
        data.rc = _monitor;
        if (_top)
        {
            data.rc.Bottom = _monitor.Top + _thickness;
        }
        else
        {
            data.rc.Top = _monitor.Bottom - _thickness;
        }

        return data;
    }

    /// <summary>A plain hidden window to own the AppBar and receive its notices.</summary>
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
            (uint)(User32.WS_EX_TOOLWINDOW | User32.WS_EX_NOACTIVATE),
            "STATIC",
            "Machitan top strip",
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
            Log.Warn("Top strip: its window could not be made (error " + Marshal.GetLastPInvokeError() + "); no strip is kept.");
            return false;
        }

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
            // Something else changed. Only act when Windows would now put the strip elsewhere,
            // so two app bars never keep answering each other.
            APPBARDATA data = Proposed();
            Shell32.SHAppBarMessage(Shell32.ABM_QUERYPOS, ref data);
            bool moved = _top ? data.rc.Top != _granted.Top : data.rc.Bottom != _granted.Bottom;
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
