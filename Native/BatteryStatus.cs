using System.Runtime.InteropServices;

namespace Machitan.Native;

internal static class BatteryStatus
{
    private const byte BatteryCharging = 0x08;
    private const byte NoSystemBattery = 0x80;
    private const byte UnknownPercent = 0xFF;

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    public static bool TryGet(out int percent, out bool charging, out bool hasBattery)
    {
        percent = 0;
        charging = false;
        hasBattery = false;

        if (!GetSystemPowerStatus(out var status))
            return false;

        hasBattery = status.BatteryFlag != NoSystemBattery && status.BatteryFlag != UnknownPercent;
        charging = (status.BatteryFlag & BatteryCharging) != 0;
        if (!hasBattery)
            return true;

        if (status.BatteryLifePercent == UnknownPercent)
        {
            percent = -1;
            return true;
        }

        percent = Math.Clamp((int)status.BatteryLifePercent, 0, 100);
        return true;
    }
}
