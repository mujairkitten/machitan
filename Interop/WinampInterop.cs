using System;
using System.IO;

namespace Machitan.Interop;

/// <summary>
/// Winamp's message interface, which WACUP (and players that copy Winamp's, like AIMP) still
/// answer on their main window, class "Winamp v1.x". Used for what an app does not offer
/// through Windows' media controls: the position, the track length, and jumping to a place.
/// Only plain numbers go back and forth (no memory is shared), each with a short time limit.
/// </summary>
internal static class WinampInterop
{
    private const string WindowClass = "Winamp v1.x";
    private const uint WM_WA_IPC = 0x0400;
    private const nint IPC_GETOUTPUTTIME = 105;
    private const nint IPC_JUMPTOTIME = 106;
    private const uint TimeoutMs = 150;

    /// <summary>
    /// The Winamp-style window of the app behind a media session, or 0. The session's app ID
    /// (for a desktop player, its executable's name or path) must name that window's program.
    /// </summary>
    internal static nint FindFor(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return 0;
        }

        string id = appId.Trim();
        int bang = id.LastIndexOf('!');
        if (bang >= 0 && bang < id.Length - 1)
        {
            id = id[(bang + 1)..];
        }

        string idFile = Path.GetFileName(id);
        string idName = Path.GetFileNameWithoutExtension(idFile);

        nint window = 0;
        for (int guard = 0; guard < 16; guard++)
        {
            window = User32.FindWindowEx(0, window, WindowClass, null);
            if (window == 0)
            {
                return 0;
            }

            User32.GetWindowThreadProcessId(window, out uint processId);
            string? path = WindowInfo.GetProcessPath(processId);
            if (path is null)
            {
                continue;
            }

            string file = Path.GetFileName(path);
            if (string.Equals(path, id, StringComparison.OrdinalIgnoreCase)
                || string.Equals(file, idFile, StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetFileNameWithoutExtension(file), idName, StringComparison.OrdinalIgnoreCase))
            {
                return window;
            }
        }

        return 0;
    }

    /// <summary>Where the player is in the track and how long it is; false when it plays nothing or does not answer.</summary>
    internal static bool TryGetTimeline(nint window, out TimeSpan position, out TimeSpan length)
    {
        position = TimeSpan.Zero;
        length = TimeSpan.Zero;
        if (!Ask(window, 0, IPC_GETOUTPUTTIME, out nint positionMs) || positionMs < 0)
        {
            return false;
        }

        // Mode 2: the length in milliseconds; older players only know mode 1 (seconds).
        if (Ask(window, 2, IPC_GETOUTPUTTIME, out nint lengthMs) && lengthMs > 0)
        {
            length = TimeSpan.FromMilliseconds(lengthMs);
        }
        else if (Ask(window, 1, IPC_GETOUTPUTTIME, out nint lengthSeconds) && lengthSeconds > 0)
        {
            length = TimeSpan.FromSeconds(lengthSeconds);
        }
        else
        {
            return false;
        }

        position = TimeSpan.FromMilliseconds(positionMs);
        return true;
    }

    /// <summary>Jumps to a place in the playing track; true when the player says it did.</summary>
    internal static bool TryJump(nint window, TimeSpan to)
    {
        long ms = (long)Math.Max(to.TotalMilliseconds, 0);
        return Ask(window, (nint)ms, IPC_JUMPTOTIME, out nint answer) && answer == 0;
    }

    private static bool Ask(nint window, nint data, nint what, out nint answer)
    {
        answer = 0;
        return window != 0
            && User32.SendMessageTimeoutW(window, WM_WA_IPC, data, what, User32.SMTO_ABORTIFHUNG, TimeoutMs, out answer) != 0;
    }
}
