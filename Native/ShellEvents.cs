using System.Runtime.InteropServices;
using static Machitan.Native.NativeMethods;

namespace Machitan.Native;

/// <summary>
/// Listens for the broadcast "TaskbarCreated" window message, which Explorer
/// sends when it (re)starts its taskbar — after a crash, an update, or a
/// manual restart. Without this, a restarted Explorer brings its taskbar back
/// next to Machitan's and nothing ever hides it again.
/// </summary>
internal static class ShellEvents
{
    private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass, UIntPtr dwRefData);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    // Kept alive for the process lifetime: the OS calls this delegate.
    private static SubclassProc? _proc;
    private static uint _taskbarCreatedMessage;
    private static Action? _onTaskbarCreated;

    public static void InstallTaskbarCreatedHook(IntPtr hwnd, Action onTaskbarCreated)
    {
        _onTaskbarCreated = onTaskbarCreated;
        _taskbarCreatedMessage = RegisterWindowMessage("TaskbarCreated");
        _proc = Proc;
        SetWindowSubclass(hwnd, _proc, (UIntPtr)1, UIntPtr.Zero);
    }

    private static IntPtr Proc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, UIntPtr dwRefData)
    {
        if (_taskbarCreatedMessage != 0 && uMsg == _taskbarCreatedMessage)
        {
            try { _onTaskbarCreated?.Invoke(); }
            catch { }
        }

        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }
}
