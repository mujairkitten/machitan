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
}
