using Machitan.Interop;

namespace Machitan.Shell;

/// <summary>Decides which top-level windows get a button on the bar (the Alt+Tab rules).</summary>
internal static class WindowFilter
{
    internal const string AppFrameClass = "ApplicationFrameWindow";
    internal const string CoreWindowClass = "Windows.UI.Core.CoreWindow";

    internal static bool IsTaskWindow(nint hwnd, out string className)
    {
        className = string.Empty;

        if (!User32.IsWindowVisible(hwnd))
        {
            return false;
        }

        long exStyle = User32.GetWindowLongPtrW(hwnd, User32.GWL_EXSTYLE);
        bool forcedOntoTaskbar = (exStyle & User32.WS_EX_APPWINDOW) != 0;
        if (!forcedOntoTaskbar)
        {
            if ((exStyle & User32.WS_EX_TOOLWINDOW) != 0)
            {
                return false;
            }

            if ((exStyle & User32.WS_EX_NOACTIVATE) != 0)
            {
                return false;
            }

            if (User32.GetWindow(hwnd, User32.GW_OWNER) != 0)
            {
                return false;
            }
        }

        // Cloaked: suspended store apps and windows on other virtual desktops.
        if (WindowInfo.IsCloaked(hwnd))
        {
            return false;
        }

        className = WindowInfo.GetClassName(hwnd);
        switch (className)
        {
            case "Progman":
            case "WorkerW":
            case "Shell_TrayWnd":
            case "Shell_SecondaryTrayWnd":
            case CoreWindowClass:
                return false;
        }

        return User32.GetWindowTextLengthW(hwnd) > 0;
    }
}
