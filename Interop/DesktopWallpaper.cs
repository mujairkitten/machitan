using System.Runtime.InteropServices;

namespace Machitan.Interop;

/// <summary>shobjidl.h DesktopWallpaper: Windows' own record of each screen's wallpaper.</summary>
[ComImport]
[Guid("C2CF3110-460E-4FC1-B9D0-8A1C0C9CC4BD")]
internal class DesktopWallpaperObject
{
}

/// <summary>
/// shobjidl.h IDesktopWallpaper. Only the methods up to GetPosition are declared (the
/// v-table order matters, the rest is not needed). Strings come back as CoTaskMem pointers.
/// </summary>
[ComImport]
[Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDesktopWallpaper
{
    [PreserveSig]
    int SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);

    [PreserveSig]
    int GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorId, out nint wallpaper);

    [PreserveSig]
    int GetMonitorDevicePathAt(uint monitorIndex, out nint monitorId);

    [PreserveSig]
    int GetMonitorDevicePathCount(out uint count);

    [PreserveSig]
    int GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorId, out RECT displayRect);

    [PreserveSig]
    int SetBackgroundColor(uint color);

    [PreserveSig]
    int GetBackgroundColor(out uint color);

    [PreserveSig]
    int SetPosition(int position);

    [PreserveSig]
    int GetPosition(out int position);
}

/// <summary>shobjidl.h DESKTOP_WALLPAPER_POSITION.</summary>
internal static class WallpaperPosition
{
    internal const int Center = 0;
    internal const int Tile = 1;
    internal const int Stretch = 2;
    internal const int Fit = 3;
    internal const int Fill = 4;
    internal const int Span = 5;
}

/// <summary>user32.dll: the wallpaper-changed notice, and a plain window for the top strip.</summary>
internal static partial class User32
{
    /// <summary>WM_SETTINGCHANGE wParam: the wallpaper changed.</summary>
    internal const long SPI_SETDESKWALLPAPER = 0x0014;

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial nint CreateWindowEx(
        uint exStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        nint parent,
        nint menu,
        nint instance,
        nint param);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyWindow(nint hwnd);
}
