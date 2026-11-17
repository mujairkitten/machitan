using System.Runtime.InteropServices;

namespace Machitan.Interop;

/// <summary>dwmapi.h DWM_BLURBEHIND.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DWM_BLURBEHIND
{
    public uint dwFlags;
    public int fEnable;
    public nint hRgnBlur;
    public int fTransitionOnMaximized;
}

/// <summary>dwmapi.dll imports. Grows batch by batch; only what is used is declared.</summary>
internal static partial class Dwmapi
{
    /// <summary>Window attribute: non-zero when the window is cloaked (hidden by the system).</summary>
    internal const uint DWMWA_CLOAKED = 14;

    /// <summary>
    /// Window attribute: lets the window's composition content sample what is behind the
    /// window (the "host backdrop"). Acrylic needs it. Value is a Win32 BOOL (4 bytes).
    /// </summary>
    internal const uint DWMWA_USE_HOSTBACKDROPBRUSH = 17;

    /// <summary>Window attribute: corner rounding (Windows 11).</summary>
    internal const uint DWMWA_WINDOW_CORNER_PREFERENCE = 33;

    /// <summary>Window attribute: colour of the 1-pixel window outline (Windows 11).</summary>
    internal const uint DWMWA_BORDER_COLOR = 34;

    internal const int DWMWCP_DONOTROUND = 1;

    /// <summary>Border colour value meaning "draw no outline".</summary>
    internal const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);

    internal const uint DWM_BB_ENABLE = 0x1;
    internal const uint DWM_BB_BLURREGION = 0x2;

    /// <summary>Returns an HRESULT: 0 means the attribute was accepted.</summary>
    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmSetWindowAttribute(nint hwnd, uint attribute, ref int value, uint size);

    /// <summary>Returns an HRESULT: 0 means the value was read.</summary>
    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmGetWindowAttribute(nint hwnd, uint attribute, out int value, uint size);

    /// <summary>
    /// Returns an HRESULT. With a blur region that lies off the window, nothing is blurred,
    /// but DWM starts honouring the window's alpha channel: fully transparent pixels show
    /// what is behind the window. The caller keeps ownership of the region.
    /// </summary>
    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmEnableBlurBehindWindow(nint hwnd, ref DWM_BLURBEHIND blurBehind);

    internal const uint DWM_TNP_RECTDESTINATION = 0x1;
    internal const uint DWM_TNP_OPACITY = 0x4;
    internal const uint DWM_TNP_VISIBLE = 0x8;

    /// <summary>Starts a live picture of <paramref name="source"/>, drawn by Windows into <paramref name="destination"/>.</summary>
    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmRegisterThumbnail(nint destination, nint source, out nint thumbnail);

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmUnregisterThumbnail(nint thumbnail);

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmUpdateThumbnailProperties(nint thumbnail, ref DWM_THUMBNAIL_PROPERTIES properties);

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmQueryThumbnailSourceSize(nint thumbnail, out SIZE size);
}

/// <summary>
/// dwmapi.h DWM_THUMBNAIL_PROPERTIES. Laid out like most C# callers do (natural alignment);
/// with opacity 255, visible on and "client area only" off, Windows reads the same values
/// whether its header packs the struct or not.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DWM_THUMBNAIL_PROPERTIES
{
    public uint dwFlags;
    public RECT rcDestination;
    public RECT rcSource;
    public byte opacity;
    public int fVisible;
    public int fSourceClientAreaOnly;
}

/// <summary>windef.h SIZE.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SIZE
{
    public int cx;
    public int cy;
}
