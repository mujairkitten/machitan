using System.Runtime.InteropServices;

namespace Machitan.Interop;

/// <summary>kernel32.dll imports. Only what is used is declared.</summary>
internal static unsafe partial class Kernel32
{
    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    internal const uint PROCESS_TERMINATE = 0x0001;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TerminateProcess(nint process, uint exitCode);

    [LibraryImport("kernel32.dll")]
    internal static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);

    /// <summary>On input size is the buffer length in chars; on output the path length without the null.</summary>
    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryFullProcessImageNameW(nint process, uint flags, char* buffer, ref uint size);

    /// <summary>
    /// Package identity of a process. Returns 0 on success, 15700 when the process has no
    /// package, 122 when the buffer is too small (length then holds the size needed).
    /// </summary>
    [LibraryImport("kernel32.dll")]
    internal static partial int GetPackageFullName(nint process, ref uint length, char* name);
}
