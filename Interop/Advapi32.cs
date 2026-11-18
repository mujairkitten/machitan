using System.Runtime.InteropServices;

namespace Machitan.Interop;

/// <summary>advapi32.dll: being told when a registry key changes.</summary>
internal static partial class Advapi32
{
    /// <summary>HKEY_CURRENT_USER, ((HKEY)(LONG)0x80000001), sign-extended on x64.</summary>
    internal static readonly nint HKEY_CURRENT_USER = unchecked((nint)(int)0x80000001);

    internal const int KEY_NOTIFY = 0x0010;

    /// <summary>Notify when a value of the key is set or deleted.</summary>
    internal const uint REG_NOTIFY_CHANGE_LAST_SET = 0x00000004;

    /// <summary>Notify when a subkey is added or deleted.</summary>
    internal const uint REG_NOTIFY_CHANGE_NAME = 0x00000001;

    [LibraryImport("advapi32.dll", EntryPoint = "RegOpenKeyExW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int RegOpenKeyEx(nint key, string subKey, uint options, int desiredAccess, out nint result);

    [LibraryImport("advapi32.dll")]
    internal static partial int RegNotifyChangeKeyValue(
        nint key,
        [MarshalAs(UnmanagedType.Bool)] bool watchSubtree,
        uint notifyFilter,
        nint eventHandle,
        [MarshalAs(UnmanagedType.Bool)] bool asynchronous);

    [LibraryImport("advapi32.dll")]
    internal static partial int RegCloseKey(nint key);
}
