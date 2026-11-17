using System.Runtime.InteropServices;

namespace Machitan.Interop;

/// <summary>secur32.dll imports.</summary>
internal static unsafe partial class Secur32
{
    /// <summary>EXTENDED_NAME_FORMAT: the user's display name ("Sam Smith").</summary>
    internal const int NameDisplay = 3;

    /// <summary>On input size is the buffer length in chars; on success, the length written.</summary>
    [LibraryImport("secur32.dll", EntryPoint = "GetUserNameExW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetUserNameEx(int nameFormat, char* buffer, ref uint size);
}
