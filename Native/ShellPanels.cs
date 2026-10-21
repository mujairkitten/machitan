using static Machitan.Native.NativeMethods;

namespace Machitan.Native;

/// <summary>
/// Opens the real Windows panels the bar's right cluster points at, using the
/// same Win-key chords a user would press: Win+N (notification center +
/// calendar), Win+A (quick settings), Win+Tab (Task View). The WPF ShellBar
/// wired its clock/quick-settings buttons this way; the WinUI rewrite shipped
/// them as empty stubs — this restores the behavior.
/// </summary>
internal static class ShellPanels
{
    private const byte VK_N = 0x4E;
    private const byte VK_A = 0x41;

    public static void OpenNotifications() => SendWinChord(VK_N);

    public static void OpenQuickSettings() => SendWinChord(VK_A);

    public static void OpenTaskView() => SendWinChord(VK_TAB);

    private static void SendWinChord(byte vk)
    {
        try
        {
            keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
            keybd_event(vk, 0, 0, UIntPtr.Zero);
            keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
            keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        }
        catch
        {
            // Never let a panel shortcut kill the shell.
        }
    }
}
