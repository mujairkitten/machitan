using System.Runtime.InteropServices;
using Machitan.Native;

namespace Machitan.Native;

internal static class AppBarController
{
    private const int PillHeightInset = 14;
    private const int MaxPillWidth = 1020;

    private static uint _callbackMessage;

    public static uint CallbackMessage => _callbackMessage;

    public static void Register(IntPtr hwnd, int barHeight, DockEdge edge)
    {
        _callbackMessage = (uint)NativeMethods.RegisterWindowMessage("Machitan_AppBarCallback");

        var data = new NativeMethods.APPBARDATA
        {
            cbSize = Marshal.SizeOf<NativeMethods.APPBARDATA>(),
            hWnd = hwnd,
            uCallbackMessage = _callbackMessage,
        };
        NativeMethods.SHAppBarMessage(NativeMethods.ABM_NEW, ref data);

        Reposition(hwnd, barHeight, edge);
    }

    /// <summary>Full-width strip reserved from the work area.</summary>
    private static NativeMethods.RECT ComputeReservedRect(IntPtr hwnd, int barHeight, DockEdge edge)
    {
        int screenW = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
        int screenH = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);
        uint dpi = NativeMethods.GetDpiForWindow(hwnd);
        int hPx = Math.Max(1, (int)Math.Round(barHeight * (dpi == 0 ? 1 : dpi / 96d)));

        return edge == DockEdge.Top
            ? new NativeMethods.RECT { Left = 0, Top = 0, Right = screenW, Bottom = hPx }
            : new NativeMethods.RECT { Left = 0, Top = screenH - hPx, Right = screenW, Bottom = screenH };
    }

    /// <summary>The floating pill window itself, positioned inside the reserved strip.</summary>
    public static NativeMethods.RECT ComputeVisualRect(IntPtr hwnd, int barHeight, DockEdge edge, int widthDip, DockAlignment alignment = DockAlignment.Center, TaskbarMode mode = TaskbarMode.Joined)
    {
        int screenW = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
        int screenH = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);
        uint dpi = NativeMethods.GetDpiForWindow(hwnd);
        double scale = dpi == 0 ? 1 : dpi / 96d;

        int stripPx = Math.Max(1, (int)Math.Round(barHeight * scale));

        if (mode == TaskbarMode.Classic)
        {
            int topClassic = edge == DockEdge.Top ? 0 : screenH - stripPx;
            return new NativeMethods.RECT
            {
                Left = 0,
                Top = topClassic,
                Right = screenW,
                Bottom = topClassic + stripPx,
            };
        }

        int pillH = Math.Max(1, (int)Math.Round((barHeight - PillHeightInset) * scale));
        int pillW = Math.Min((int)Math.Round(widthDip * scale), (int)Math.Round(screenW * 0.96));

        int left;
        int marginPx = (int)Math.Round(16 * scale);
        switch (alignment)
        {
            case DockAlignment.Left:
                left = marginPx;
                break;
            case DockAlignment.Right:
                left = screenW - pillW - marginPx;
                break;
            case DockAlignment.Center:
            default:
                left = (screenW - pillW) / 2;
                break;
        }

        int top = edge == DockEdge.Top
            ? Math.Max(0, (stripPx - pillH) / 2)
            : screenH - stripPx + Math.Max(0, (stripPx - pillH) / 2);

        return new NativeMethods.RECT
        {
            Left = left,
            Top = top,
            Right = left + pillW,
            Bottom = top + pillH,
        };
    }

    public static bool IsAtDesiredPosition(IntPtr hwnd, int barHeight, DockEdge edge, int widthDip, DockAlignment alignment = DockAlignment.Center, TaskbarMode mode = TaskbarMode.Joined)
    {
        if (!NativeMethods.GetWindowRect(hwnd, out NativeMethods.RECT actual))
            return false;

        var want = ComputeVisualRect(hwnd, barHeight, edge, widthDip, alignment, mode);
        return Math.Abs(actual.Left - want.Left) <= 1
            && Math.Abs(actual.Top - want.Top) <= 1
            && Math.Abs(actual.Right - want.Right) <= 1
            && Math.Abs(actual.Bottom - want.Bottom) <= 1;
    }

    public static void Reposition(IntPtr hwnd, int barHeight, DockEdge edge, int widthDip = MaxPillWidth, DockAlignment alignment = DockAlignment.Center, TaskbarMode mode = TaskbarMode.Joined)
    {
        var reserved = ComputeReservedRect(hwnd, barHeight, edge);
        var data = new NativeMethods.APPBARDATA
        {
            cbSize = Marshal.SizeOf<NativeMethods.APPBARDATA>(),
            hWnd = hwnd,
            uEdge = edge == DockEdge.Top ? NativeMethods.ABE_TOP : NativeMethods.ABE_BOTTOM,
            rc = reserved,
        };
        NativeMethods.SHAppBarMessage(NativeMethods.ABM_QUERYPOS, ref data);
        data.rc = reserved;
        NativeMethods.SHAppBarMessage(NativeMethods.ABM_SETPOS, ref data);

        var visual = ComputeVisualRect(hwnd, barHeight, edge, widthDip, alignment, mode);
        int width = visual.Right - visual.Left;
        int height = visual.Bottom - visual.Top;
        NativeMethods.MoveWindow(hwnd, visual.Left, visual.Top, width, height, true);

        // Corners come from DWM now (anti-aliased): rounded in Dock mode,
        // square in Classic. This used to clip the window with a GDI region
        // (CreateRoundRectRgn) — the aliased-corner look is gone with it.
        WindowChrome.SetCornerPreference(hwnd,
            mode == TaskbarMode.Classic ? NativeMethods.DWMWCP_DONOTROUND : NativeMethods.DWMWCP_ROUND);
    }

    public static void Unregister(IntPtr hwnd)
    {
        var data = new NativeMethods.APPBARDATA
        {
            cbSize = Marshal.SizeOf<NativeMethods.APPBARDATA>(),
            hWnd = hwnd,
        };
        NativeMethods.SHAppBarMessage(NativeMethods.ABM_REMOVE, ref data);
    }
}
