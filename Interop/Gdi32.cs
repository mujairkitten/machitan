using System.Runtime.InteropServices;

namespace Machitan.Interop;

/// <summary>gdi32.dll imports. Only what is used is declared.</summary>
internal static unsafe partial class Gdi32
{
    /// <summary>Stock object index of the solid black brush. Stock objects are never deleted.</summary>
    internal const int BLACK_BRUSH = 4;

    /// <summary>CombineRgn mode: union of the two regions.</summary>
    internal const int RGN_OR = 2;

    [LibraryImport("gdi32.dll")]
    internal static partial nint CreateRectRgn(int left, int top, int right, int bottom);

    /// <summary>Writes the combination into <paramref name="destination"/>. Returns 0 on failure.</summary>
    [LibraryImport("gdi32.dll")]
    internal static partial int CombineRgn(nint destination, nint source1, nint source2, int mode);

    [LibraryImport("gdi32.dll")]
    internal static partial nint CreateRoundRectRgn(int left, int top, int right, int bottom, int ellipseWidth, int ellipseHeight);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteObject(nint handle);

    [LibraryImport("gdi32.dll")]
    internal static partial nint GetStockObject(int index);

    /// <summary>Returns the number of bytes written, or 0 on failure.</summary>
    [LibraryImport("gdi32.dll")]
    internal static partial int GetObjectW(nint handle, int bufferSize, void* buffer);

    /// <summary>Returns the number of scan lines copied, or 0 on failure.</summary>
    [LibraryImport("gdi32.dll")]
    internal static partial int GetDIBits(nint deviceContext, nint bitmap, uint startLine, uint lineCount, byte* bits, BITMAPINFO* info, uint usage);
}
