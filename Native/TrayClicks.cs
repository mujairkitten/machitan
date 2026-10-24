using System.Runtime.InteropServices;

namespace Machitan.Native;

internal enum TrayMouse
{
    Left,
    Right,
    Middle,
}

/// <summary>
/// Sends clicks on a tray icon (in the chevron pop-up) to the app that owns it,
/// the way Explorer does: as the app's own "callback message".
///
/// Old-style icons (version 0 to 3): wParam = icon id, lParam = the mouse
/// message (WM_LBUTTONUP, WM_RBUTTONUP, ...). That is how the app tells left
/// from right. Version 4: wParam = click position (x low, y high),
/// lParam = event in the low word and icon id in the high word.
///
/// An app tells the shell its version once, when it adds the icon. An app that
/// started before Machitan never tells us, so its version is unknown. Windows
/// Security is such an app and only reacts to the version 4 messages. So the
/// "select" (left click) and "context menu" (right click) messages are always
/// sent in the version 4 layout, in addition to the plain mouse messages. An
/// old-style app does not match them and ignores them.
///
/// Messages are sent with SendNotifyMessage, which never waits for the app, so
/// a frozen app cannot freeze the bar. Windows may refuse to deliver them to
/// an app running as administrator; the failure is written to the log.
/// </summary>
internal static class TrayClicks
{
    private const uint WM_CONTEXTMENU = 0x007B;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_LBUTTONDBLCLK = 0x0203;
    private const uint WM_RBUTTONDOWN = 0x0204;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_MBUTTONDOWN = 0x0207;
    private const uint WM_MBUTTONUP = 0x0208;
    private const uint NIN_SELECT = 0x0400;

    public static void MouseDown(TrayIconEntry entry, TrayMouse button)
    {
        IntPtr owner = OwnerWindow(entry);
        if (!NativeCalls.IsWindow(owner))
        {
            DiagnosticsLog.Write($"Tray click skipped: owner window of {entry.Owner} is gone");
            return;
        }

        // Let the app bring its window or menu to the front (and close the
        // menu again when the user clicks elsewhere).
        NativeCalls.GetWindowThreadProcessId(owner, out uint pid);
        NativeCalls.AllowSetForegroundWindow(pid);

        uint message;
        switch (button)
        {
            case TrayMouse.Left:
                // Double-click is per icon: two quick clicks on different icons are not one.
                bool isDouble = (DateTime.UtcNow - entry.LastLeftDown).TotalMilliseconds < NativeCalls.GetDoubleClickTime();
                message = isDouble ? WM_LBUTTONDBLCLK : WM_LBUTTONDOWN;
                entry.LastLeftDown = DateTime.UtcNow;
                break;
            case TrayMouse.Right:
                message = WM_RBUTTONDOWN;
                break;
            default:
                message = WM_MBUTTONDOWN;
                break;
        }

        Post(entry, owner, message, "down", entry.Version);
    }

    /// <param name="completeClick">
    /// False when the button was released away from the icon: the app still gets
    /// its "up" (so it is not left holding a pressed button) but no select / context menu.
    /// </param>
    public static void MouseUp(TrayIconEntry entry, TrayMouse button, bool completeClick = true)
    {
        IntPtr owner = OwnerWindow(entry);
        if (!NativeCalls.IsWindow(owner))
            return;

        switch (button)
        {
            case TrayMouse.Left:
                Post(entry, owner, WM_LBUTTONUP, "up", entry.Version);
                if (completeClick)
                    Post(entry, owner, NIN_SELECT, "select", SelectLayout(entry));
                break;

            case TrayMouse.Right:
                Post(entry, owner, WM_RBUTTONUP, "up", entry.Version);
                if (completeClick)
                    Post(entry, owner, WM_CONTEXTMENU, "context-menu", SelectLayout(entry));
                break;

            default:
                Post(entry, owner, WM_MBUTTONUP, "up", entry.Version);
                break;
        }
    }

    /// <summary>
    /// Builds the two message parameters for one event. Pure, so it can be tested.
    /// <paramref name="mouse"/> is the screen position packed as x (low word), y (high word).
    /// </summary>
    internal static (long wParam, long lParam) Encode(uint version, uint uid, uint mouse, uint message)
    {
        if (version > 3)
            return (mouse, message | ((uid & 0xFFFF) << 16));

        return (uid, message);
    }

    /// <summary>
    /// Layout for select / context-menu: version 4 unless the app announced the
    /// older version 3 (Explorer sends them to version 3 apps in the old layout).
    /// </summary>
    private static uint SelectLayout(TrayIconEntry entry) => entry.Version == 3 ? 3u : 4u;

    internal static uint PackPosition(int x, int y)
        => (ushort)x | ((uint)(ushort)y << 16);

    private static IntPtr OwnerWindow(TrayIconEntry entry)
        => new IntPtr(unchecked((int)entry.HwndRaw));

    private static void Post(TrayIconEntry entry, IntPtr owner, uint message, string what, uint layoutVersion)
    {
        // An icon registered without a callback message wants no notifications.
        // (Callback 0 would send the app a meaningless WM_NULL.)
        if (entry.Callback == 0)
        {
            DiagnosticsLog.Write($"Tray click {what} skipped: {entry.Owner} registered no callback message");
            return;
        }

        try
        {
            NativeCalls.GetCursorPos(out NativeCalls.Point pt);
            var (wParam, lParam) = Encode(layoutVersion, entry.Uid, PackPosition(pt.X, pt.Y), message);

            bool sent = NativeCalls.SendNotifyMessageW(owner, entry.Callback, new IntPtr(wParam), new IntPtr(lParam));
            int error = sent ? 0 : Marshal.GetLastWin32Error();

            string note = error == 5 ? " (access denied: the app probably runs as administrator)" : "";
            string version = entry.Version + (entry.VersionAnnounced ? "" : "?"); // "?" = never announced
            // "posted" = Windows accepted the message for delivery; it does not prove the app acted on it.
            DiagnosticsLog.Write(
                $"Tray click {what} app={entry.Owner} elev={TrayListener.ElevationOf(entry.HwndRaw)} " +
                $"version={version} layout={(layoutVersion > 3 ? "v4" : "legacy")} id={entry.Uid} " +
                $"message=0x{message:X} {(sent ? "posted" : $"FAILED error={error}{note}")}");
        }
        catch (Exception ex)
        {
            DiagnosticsLog.WriteException("Tray click", ex);
        }
    }

    private static class NativeCalls
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct Point
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        public static extern bool AllowSetForegroundWindow(uint processId);

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out Point point);

        [DllImport("user32.dll")]
        public static extern uint GetDoubleClickTime();

        [DllImport("user32.dll", EntryPoint = "SendNotifyMessageW", SetLastError = true)]
        public static extern bool SendNotifyMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    }
}
