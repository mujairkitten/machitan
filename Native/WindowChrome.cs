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

        // WS_EX_TOPMOST only takes effect when SetWindowPos is allowed to
        // change the z-order: insert after HWND_TOPMOST (-1), no NOZORDER.
        if (topmost)
        {
            SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_FRAMECHANGED);
        }
        else
        {
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }
    }

    // Corners come from DWM (DWMWA_WINDOW_CORNER_PREFERENCE = ROUND), which
    // anti-aliases them. The pill and menu used to be clipped with a GDI
    // window region (CreateRoundRectRgn), whose hard aliased edges were the
    // "rough corners" complaint — that mechanism is gone.
    public static void SetCornerPreference(IntPtr hwnd, int preference)
    {
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference, sizeof(int));
    }
}
