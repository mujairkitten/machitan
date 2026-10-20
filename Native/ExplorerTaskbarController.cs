using System.Text;
using static Machitan.Native.NativeMethods;

namespace Machitan.Native;

internal static class ExplorerTaskbarController
{
    private static readonly Dictionary<IntPtr, bool> TaskbarVisibility = new();
    private static bool _isHidden;

    static ExplorerTaskbarController()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Restore();
    }

    public static void Hide()
    {
        if (_isHidden)
            return;

        EnumWindows((hWnd, _) =>
        {
            var className = new StringBuilder(64);
            GetClassName(hWnd, className, className.Capacity);
            if (className.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
            {
                bool wasVisible = IsWindowVisible(hWnd);
                TaskbarVisibility[hWnd] = wasVisible;
                if (wasVisible)
                    ShowWindow(hWnd, SW_HIDE);
            }

            return true;
        }, IntPtr.Zero);

        _isHidden = true;
    }

    public static void Restore()
    {
        foreach (var (hWnd, wasVisible) in TaskbarVisibility)
        {
            if (wasVisible && IsWindow(hWnd))
                ShowWindow(hWnd, SW_SHOW);
        }

        TaskbarVisibility.Clear();
        _isHidden = false;
    }
}
