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
