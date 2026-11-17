using System.Runtime.InteropServices;

namespace Machitan.Interop;

/// <summary>
/// dxva2.dll: monitor brightness over DDC/CI, the control channel most external monitors
/// have. A laptop's own screen usually does not answer it.
/// </summary>
internal static unsafe partial class Dxva2
{
    [LibraryImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetNumberOfPhysicalMonitorsFromHMONITOR(nint monitor, out uint count);

    [LibraryImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetPhysicalMonitorsFromHMONITOR(nint monitor, uint count, PHYSICAL_MONITOR* monitors);

    [LibraryImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetMonitorBrightness(nint physicalMonitor, out uint minimum, out uint current, out uint maximum);

    [LibraryImport("dxva2.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetMonitorBrightness(nint physicalMonitor, uint brightness);

    [LibraryImport("dxva2.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyPhysicalMonitor(nint physicalMonitor);
}

/// <summary>physicalmonitorenumerationapi.h PHYSICAL_MONITOR (handle + 128-character description).</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct PHYSICAL_MONITOR
{
    public nint hPhysicalMonitor;
    public fixed char szPhysicalMonitorDescription[128];
}

/// <summary>user32.dll: the list of display monitors.</summary>
internal static unsafe partial class User32
{
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumDisplayMonitors(nint hdc, nint clip, delegate* unmanaged<nint, nint, RECT*, nint, int> callback, nint lParam);
}
