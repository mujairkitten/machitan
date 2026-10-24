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
            // Primary taskbar only. Secondary taskbars (Shell_SecondaryTrayWnd)
            // stay visible: Machitan renders its bar on the primary display
            // only, and a secondary monitor with NO taskbar is worse than one
            // with Explorer's.
            if (className.ToString() is "Shell_TrayWnd")
            {
                bool wasVisible = IsWindowVisible(hWnd);
                // A window we already hid reports "not visible": never let that
                // overwrite an earlier "was visible", or Restore() would leave
                // the Explorer taskbar hidden after a clean exit.
                TaskbarVisibility[hWnd] = wasVisible
                    || (TaskbarVisibility.TryGetValue(hWnd, out bool before) && before);
                if (wasVisible)
                    ShowWindow(hWnd, SW_HIDE);
            }

            return true;
        }, IntPtr.Zero);

        _isHidden = true;
    }

    /// <summary>
    /// Re-runs the hide pass even though the taskbars are already recorded as
    /// hidden. Needed after Explorer restarts: the new Shell_TrayWnd is a
    /// fresh, visible window that Hide() would otherwise skip.
    /// </summary>
    public static void Rehide()
    {
        _isHidden = false;
        Hide();
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
