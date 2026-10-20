using static Machitan.Native.NativeMethods;

namespace Machitan.Native;

internal static class WindowChrome
{
    /// <summary>Turns a WinUI window into a borderless tool window.</summary>
    public static void MakeBorderless(IntPtr hwnd, bool topmost)
    {
        long style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
        style &= ~(long)(WS_CAPTION | WS_THICKFRAME | WS_MINIMIZEBOX | WS_MAXIMIZEBOX | WS_SYSMENU);
        style |= WS_POPUP;
        SetWindowLongPtr(hwnd, GWL_STYLE, (IntPtr)style);

        long exStyle = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        exStyle |= WS_EX_TOOLWINDOW;
        if (topmost)
            exStyle |= WS_EX_TOPMOST;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)exStyle);

        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    /// <summary>Clips the window to a rounded rectangle (large radius pill shape).</summary>
    public static void ApplyRoundedRegion(IntPtr hwnd, int radiusPx)
    {
        // Diagnostic escape hatch: Machitan_NO_REGION=1 disables clipping.
        if (System.Environment.GetEnvironmentVariable("Machitan_NO_REGION") == "1")
            return;

        if (!GetWindowRect(hwnd, out RECT rect))
            return;

        int width = rect.Right - rect.Left;
        int height = rect.Bottom - rect.Top;
        if (width <= 0 || height <= 0)
            return;

        radiusPx = System.Math.Clamp(radiusPx, 0, System.Math.Min(width, height) / 2);
        IntPtr region = CreateRoundRectRgn(0, 0, width + 1, height + 1, radiusPx * 2, radiusPx * 2);
        if (region != IntPtr.Zero)
            SetWindowRgn(hwnd, region, true);
    }

    public static void SetCornerPreference(IntPtr hwnd, int preference)
    {
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
    }
}
