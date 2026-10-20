using System.Runtime.InteropServices;

namespace Machitan.Native;

/// <summary>Which acrylic mechanism (if any) is active on a window.</summary>
internal enum AcrylicKind
{
    None,
    Dwm,    // DWMWA_SYSTEMBACKDROP_TYPE (Win11 22H2+)
    Accent, // SetWindowCompositionAttribute (Win10 1803+, all Win11)
}

/// <summary>
/// Fluent Design building block: real backdrop acrylic for full windows
/// like the start menu, where the material can own the whole client area.
///
/// Two tiers, tried in order:
///  1. DWMWA_SYSTEMBACKDROP_TYPE — the modern Win11 API.
///  2. SetWindowCompositionAttribute with ACCENT_ENABLE_ACRYLICBLURBEHIND —
///     the legacy API, for builds without the DWM attribute.
///
/// The caller thins its own tint according to the returned kind (the
/// accent path already carries a tint in its gradient color, so the
/// window tint goes nearly transparent there). Returns AcrylicKind.None
/// where neither is available; callers then keep their full tint.
/// </summary>
internal static class FluentBackdrop
{
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMSBT_TRANSIENTWINDOW = 3; // the "Acrylic" member
    private const int DWMWCP_ROUND = 2;

    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int Left, Right, Top, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor; // 0xAABBGGRR
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    /// <summary>
    /// Applies backdrop acrylic to a window whose own background is
    /// (semi-)transparent. <paramref name="dark"/> picks the accent tint
    /// for the legacy path.
    /// </summary>
    public static AcrylicKind TryApplyAcrylic(IntPtr hwnd, bool dark)
    {
        // Tier 1: modern DWM backdrop.
        try
        {
            int backdrop = DWMSBT_TRANSIENTWINDOW;
            if (DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int)) == 0)
            {
                int corner = DWMWCP_ROUND;
                DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));

                var margins = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                DwmExtendFrameIntoClientArea(hwnd, ref margins);
                return AcrylicKind.Dwm;
            }
        }
        catch
        {
            // Fall through to the legacy path.
        }

        // Tier 2: legacy accent acrylic.
        try
        {
            var accent = new AccentPolicy
            {
                AccentState = ACCENT_ENABLE_ACRYLICBLURBEHIND,
                AccentFlags = 0,
                GradientColor = dark
                    ? unchecked((int)0xB315181C) // dark tint, ~70% alpha
                    : unchecked((int)0xB8E8EEF2), // light tint, ~72% alpha
                AnimationId = 0,
            };

            int size = Marshal.SizeOf(accent);
            IntPtr ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, ptr, false);
                var data = new WindowCompositionAttributeData
                {
                    Attribute = WCA_ACCENT_POLICY,
                    Data = ptr,
                    SizeOfData = size,
                };
                if (SetWindowCompositionAttribute(hwnd, ref data) != 0)
                    return AcrylicKind.Accent;
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
        }
        catch
        {
            // No acrylic available.
        }

        return AcrylicKind.None;
    }
}
