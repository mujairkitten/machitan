using System.Runtime.InteropServices;

namespace Machitan.Interop;

/// <summary>comctl32.dll imports: window subclassing.</summary>
internal static unsafe partial class Comctl32
{
    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowSubclass(
        nint hwnd,
        delegate* unmanaged<nint, uint, nint, nint, nuint, nuint, nint> subclassProc,
        nuint subclassId,
        nuint referenceData);

    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RemoveWindowSubclass(
        nint hwnd,
        delegate* unmanaged<nint, uint, nint, nint, nuint, nuint, nint> subclassProc,
        nuint subclassId);

    [LibraryImport("comctl32.dll")]
    internal static partial nint DefSubclassProc(nint hwnd, uint message, nint wParam, nint lParam);
}
