using System.Runtime.InteropServices;

namespace Machitan.Interop;

/// <summary>user32.dll: the clipboard's change counter (Dynaland's copy note; what is on the clipboard is never read).</summary>
internal static partial class User32
{
    /// <summary>Goes up by one at every change of the clipboard.</summary>
    [LibraryImport("user32.dll")]
    internal static partial uint GetClipboardSequenceNumber();
}
