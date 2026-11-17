using Machitan.Core;
using Machitan.Interop;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;

namespace Machitan.Visuals;

/// <summary>
/// The see-through window recipe proven on the bar (--seethrough test): a plain popup window
/// (frame styles stripped, so Windows 11 draws no outline), a backdrop that draws nothing,
/// Windows alpha (DWM blur-behind with a region off the window), and the background erase
/// answered with black. Pair it with <see cref="WindowCutter"/> to cut the window to its shapes.
/// </summary>
internal static class SeeThrough
{
    /// <summary>Call once, right after InitializeComponent.</summary>
    internal static void Prepare(Window window, nint hwnd, bool alwaysOnTop)
    {
        if (window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = alwaysOnTop;
        }

        // Tool window: no Alt+Tab entry and no button on the bar.
        long exStyle = User32.GetWindowLongPtrW(hwnd, User32.GWL_EXSTYLE);
        exStyle |= User32.WS_EX_TOOLWINDOW;
        exStyle &= ~User32.WS_EX_APPWINDOW;
        User32.SetWindowLongPtrW(hwnd, User32.GWL_EXSTYLE, (nint)exStyle);

        long style = User32.GetWindowLongPtrW(hwnd, User32.GWL_STYLE);
        style &= ~(User32.WS_BORDER | User32.WS_DLGFRAME | User32.WS_THICKFRAME
            | User32.WS_SYSMENU | User32.WS_MINIMIZEBOX | User32.WS_MAXIMIZEBOX);
        style |= User32.WS_POPUP;
        User32.SetWindowLongPtrW(hwnd, User32.GWL_STYLE, (nint)style);
        User32.SetWindowPos(
            hwnd,
            0,
            0,
            0,
            0,
            0,
            User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE | User32.SWP_FRAMECHANGED);

        int none = Dwmapi.DWMWA_COLOR_NONE;
        Dwmapi.DwmSetWindowAttribute(hwnd, Dwmapi.DWMWA_BORDER_COLOR, ref none, sizeof(int));
        int square = Dwmapi.DWMWCP_DONOTROUND;
        Dwmapi.DwmSetWindowAttribute(hwnd, Dwmapi.DWMWA_WINDOW_CORNER_PREFERENCE, ref square, sizeof(int));

        window.SystemBackdrop = new TransparentBackdrop();

        nint offWindow = Gdi32.CreateRectRgn(-2, -2, -1, -1);
        try
        {
            var blur = new DWM_BLURBEHIND
            {
                dwFlags = Dwmapi.DWM_BB_ENABLE | Dwmapi.DWM_BB_BLURREGION,
                fEnable = 1,
                hRgnBlur = offWindow,
            };

            int hr = Dwmapi.DwmEnableBlurBehindWindow(hwnd, ref blur);
            if (hr != 0)
            {
                Log.Warn("Windows refused the see-through request (0x" + hr.ToString("X8") + "); empty areas may show black.");
            }
        }
        finally
        {
            Gdi32.DeleteObject(offWindow);
        }
    }

    /// <summary>Call from the window's message handler. Returns true when the message was handled.</summary>
    internal static bool TryHandleErase(nint hwnd, uint message, nint wParam, out nint result)
    {
        result = 0;
        if (message != User32.WM_ERASEBKGND || !User32.GetClientRect(hwnd, out RECT client))
        {
            return false;
        }

        // Under Windows alpha, black with zero alpha is see-through.
        User32.FillRect(wParam, ref client, Gdi32.GetStockObject(Gdi32.BLACK_BRUSH));
        result = 1;
        return true;
    }
}
