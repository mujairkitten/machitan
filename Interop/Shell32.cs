using System.Runtime.InteropServices;

namespace Machitan.Interop;

/// <summary>
/// shell32.dll imports. Only what is used is declared.
/// SHGetFileInfo is deliberately absent: it can overflow the stack on .NET 9/10.
/// </summary>
internal static partial class Shell32
{
    internal const uint ABM_NEW = 0;
    internal const uint ABM_REMOVE = 1;
    internal const uint ABM_QUERYPOS = 2;
    internal const uint ABM_SETPOS = 3;
    internal const uint ABM_GETSTATE = 4;
    internal const uint ABM_GETTASKBARPOS = 5;
    internal const uint ABM_SETSTATE = 10;

    internal const uint ABS_AUTOHIDE = 1;

    internal const uint ABE_TOP = 1;
    internal const uint ABE_BOTTOM = 3;

    internal const uint ABN_POSCHANGED = 1;
    internal const uint ABN_FULLSCREENAPP = 2;

    [LibraryImport("shell32.dll")]
    internal static partial nuint SHAppBarMessage(uint message, ref APPBARDATA data);
}
