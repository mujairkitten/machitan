using System.Runtime.InteropServices;

namespace Machitan.Interop;

/// <summary>Windows' desktop window and the stacking order, for widgets on the desktop.</summary>
internal static partial class User32
{
    /// <summary>GetWindow: the window below in the stacking order.</summary>
    internal const uint GW_HWNDNEXT = 2;

    /// <summary>The shell's desktop window (Explorer's Progman), or 0.</summary>
    [LibraryImport("user32.dll")]
    internal static partial nint GetShellWindow();
}
