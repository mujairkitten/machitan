using System.Runtime.InteropServices;

namespace Machitan.Interop;

/// <summary>
/// shlwapi.dll imports. Only what is used is declared.
/// The shared-memory calls are the ones shell32 uses to get an answer back from Explorer's
/// taskbar for an AppBar message (a WM_COPYDATA message carries data one way only); AppBarRelay
/// uses them the same way when it passes such a message on.
/// </summary>
internal static unsafe partial class Shlwapi
{
    /// <summary>
    /// Copies <paramref name="size"/> bytes into a new shared block and returns a handle to it,
    /// valid in the process <paramref name="processId"/>; 0 on failure.
    /// </summary>
    [LibraryImport("shlwapi.dll")]
    internal static partial nint SHAllocShared(void* data, uint size, uint processId);

    /// <summary>Maps a shared block whose handle is valid in <paramref name="processId"/>; null on failure.</summary>
    [LibraryImport("shlwapi.dll")]
    internal static partial void* SHLockShared(nint shared, uint processId);

    [LibraryImport("shlwapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SHUnlockShared(void* view);

    [LibraryImport("shlwapi.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SHFreeShared(nint shared, uint processId);
}
