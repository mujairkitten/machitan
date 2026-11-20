using System.Runtime.InteropServices;

namespace Machitan.Interop;

/// <summary>winbase.h SYSTEM_POWER_STATUS (12 bytes).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SYSTEM_POWER_STATUS
{
    public byte ACLineStatus;
    public byte BatteryFlag;
    public byte BatteryLifePercent;
    public byte SystemStatusFlag;
    public uint BatteryLifeTime;
    public uint BatteryFullLifeTime;
}

/// <summary>kernel32.dll: the battery, as Windows' own battery icon reads it.</summary>
internal static unsafe partial class Kernel32
{
    internal const byte BATTERY_FLAG_CHARGING = 8;
    internal const byte BATTERY_FLAG_NO_BATTERY = 128;
    internal const byte BATTERY_FLAG_UNKNOWN = 255;
    internal const byte BATTERY_PERCENT_UNKNOWN = 255;

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);
}

/// <summary>powrprof.dll: what this PC can do (sleep, hibernate) and putting it to sleep.</summary>
internal static unsafe partial class Powrprof
{
    // Byte offsets in winnt.h SYSTEM_POWER_CAPABILITIES (76 bytes).
    internal const int CapSystemS1 = 3;
    internal const int CapSystemS2 = 4;
    internal const int CapSystemS3 = 5;
    internal const int CapSystemS4 = 6;
    internal const int CapHiberFilePresent = 8;
    internal const int CapAoAc = 20;

    /// <summary>Fills <paramref name="capabilities"/> (at least 76 bytes). Nonzero on success.</summary>
    [LibraryImport("powrprof.dll")]
    internal static partial byte GetPwrCapabilities(byte* capabilities);

    /// <summary>S1-S3 sleep (or hibernate). Fails on PCs with Modern Standby. Nonzero on success.</summary>
    [LibraryImport("powrprof.dll", SetLastError = true)]
    internal static partial byte SetSuspendState(byte hibernate, byte force, byte wakeupEventsDisabled);
}

/// <summary>advapi32.dll: shutting down with updates, and the right to shut down.</summary>
internal static unsafe partial class Advapi32
{
    internal const uint SHUTDOWN_RESTART = 0x00000004;
    internal const uint SHUTDOWN_POWEROFF = 0x00000008;
    internal const uint SHUTDOWN_INSTALL_UPDATES = 0x00000040;

    /// <summary>SHTDN_REASON_FLAG_PLANNED | MAJOR_OPERATINGSYSTEM | MINOR_UPGRADE (reason.h).</summary>
    internal const uint ReasonPlannedUpgrade = 0x80020003;

    internal const uint TOKEN_ADJUST_PRIVILEGES = 0x0020;
    internal const uint TOKEN_QUERY = 0x0008;
    internal const uint SE_PRIVILEGE_ENABLED = 0x00000002;

    /// <summary>Returns ERROR_SUCCESS (0) or a Win32 error code.</summary>
    [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint InitiateShutdownW(string? machineName, string? message, uint gracePeriod, uint flags, uint reason);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool OpenProcessToken(nint process, uint access, out nint token);

    [LibraryImport("advapi32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool LookupPrivilegeValueW(string? systemName, string name, out long luid);

    /// <summary>newState points at a TOKEN_PRIVILEGES with one entry: count (4), LUID (8), attributes (4).</summary>
    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AdjustTokenPrivileges(nint token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, byte* newState, uint bufferLength, nint previousState, nint returnLength);
}
