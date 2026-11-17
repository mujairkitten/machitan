using System;
using System.Runtime.InteropServices;
using Machitan.Core;
using Machitan.Interop;

namespace Machitan.Shell;

/// <summary>
/// Lets managed code see the raw window messages of a WinUI window (shell hook messages,
/// AppBar callbacks, display changes). Create and dispose on the window's own thread.
/// </summary>
internal sealed unsafe class WindowSubclass : IDisposable
{
    /// <summary>Return true to stop the message here and hand <paramref name="result"/> back.</summary>
    internal delegate bool MessageHandler(uint message, nint wParam, nint lParam, out nint result);

    private const nuint SubclassId = 0x4D43; // "MC"

    private readonly nint _hwnd;
    private readonly MessageHandler _handler;
    private GCHandle _self;
    private bool _installed;

    internal WindowSubclass(nint hwnd, MessageHandler handler)
    {
        _hwnd = hwnd;
        _handler = handler;
        _self = GCHandle.Alloc(this);

        _installed = Comctl32.SetWindowSubclass(hwnd, &SubclassProc, SubclassId, (nuint)GCHandle.ToIntPtr(_self));
        if (!_installed)
        {
            _self.Free();
            Log.Error("The bar window could not be subclassed; shell hook and AppBar messages will not arrive.");
        }
    }

    internal bool IsInstalled => _installed;

    public void Dispose()
    {
        if (!_installed)
        {
            return;
        }

        Comctl32.RemoveWindowSubclass(_hwnd, &SubclassProc, SubclassId);
        _installed = false;
        _self.Free();
    }

    [UnmanagedCallersOnly]
    private static nint SubclassProc(nint hwnd, uint message, nint wParam, nint lParam, nuint subclassId, nuint referenceData)
    {
        try
        {
            if (GCHandle.FromIntPtr((nint)referenceData).Target is WindowSubclass self
                && self._installed
                && self._handler(message, wParam, lParam, out nint result))
            {
                return result;
            }
        }
        catch (Exception ex)
        {
            // An exception must never cross back into native code.
            Log.Error("A window message handler failed (message 0x" + message.ToString("X4") + ").", ex);
        }

        return Comctl32.DefSubclassProc(hwnd, message, wParam, lParam);
    }
}
