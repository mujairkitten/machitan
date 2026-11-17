using System.Runtime.InteropServices;

namespace Machitan.Interop;

/// <summary>user32.dll: the front-to-back order of windows.</summary>
internal static partial class User32
{
    /// <summary>SetWindowPos: back to a normal window (from always-on-top), at the front of the normal ones.</summary>
    internal const nint HWND_NOTOPMOST = -2;

    /// <summary>GetWindow: the window just in front of this one.</summary>
    internal const uint GW_HWNDPREV = 3;

    /// <summary>SetWindowPos: the front of the normal (not always-on-top) windows.</summary>
    internal const nint HWND_TOP = 0;

    internal const uint SWP_NOOWNERZORDER = 0x0200;

    internal const long WS_EX_TOPMOST = 0x00000008;

    /// <summary>True when the window's app has stopped answering (calls to it could then wait).</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsHungAppWindow(nint hwnd);
}
