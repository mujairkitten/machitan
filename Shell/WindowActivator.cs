using Machitan.Core;
using Machitan.Interop;

namespace Machitan.Shell;

/// <summary>
/// Brings a window to the front and gives it the keyboard.
/// Windows only lets a program take the front when it has just received input itself. After a
/// click on the bar or a hotkey that is usually true; after the keyboard hook saw the Win key
/// it is not (the key went to the app in front). So this tries the plain way first, then
/// after an empty mouse input (no move, no button), then borrows the front app's input state
/// for a moment. No key is ever sent: an Alt tap made apps show their Alt key tips.
/// </summary>
internal static class WindowActivator
{
    internal static bool BringToFront(nint hwnd)
    {
        if (hwnd == 0 || !User32.IsWindow(hwnd))
        {
            return false;
        }

        // A minimised window must be restored first; activating alone never does it.
        if (User32.IsIconic(hwnd))
        {
            User32.ShowWindow(hwnd, User32.SW_RESTORE);
        }

        if (User32.SetForegroundWindow(hwnd) && User32.GetForegroundWindow() == hwnd)
        {
            return true;
        }

        if (KeyChord.SendEmptyMouseInput() && User32.SetForegroundWindow(hwnd) && User32.GetForegroundWindow() == hwnd)
        {
            return true;
        }

        nint front = User32.GetForegroundWindow();
        uint frontThread = front == 0 ? 0 : User32.GetWindowThreadProcessId(front, out _);
        uint ownThread = Kernel32.GetCurrentThreadId();
        if (frontThread != 0 && frontThread != ownThread && User32.AttachThreadInput(ownThread, frontThread, true))
        {
            try
            {
                User32.BringWindowToTop(hwnd);
                User32.SetForegroundWindow(hwnd);
            }
            finally
            {
                User32.AttachThreadInput(ownThread, frontThread, false);
            }
        }

        if (User32.GetForegroundWindow() == hwnd)
        {
            return true;
        }

        // An app running as administrator in front: only a program with its rights may hand the
        // front over. The admin-keys helper (when on) does that for Machitan.
        if (AdminKeys.Current is { } keys && keys.AllowForeground()
            && User32.SetForegroundWindow(hwnd) && User32.GetForegroundWindow() == hwnd)
        {
            return true;
        }

        Log.Warn("Windows did not let a window come to the front (" + WindowInfo.GetTitle(hwnd) + ").");
        return false;
    }
}
