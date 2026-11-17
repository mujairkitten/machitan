using System;
using System.Runtime.InteropServices;

namespace Machitan.Interop;

/// <summary>
/// Sends key presses to Windows, as if pressed on the keyboard. Used to hand work to the
/// system's own UI (its Start menu, its search, later its notification centre), and by the
/// keyboard hook to keep Windows' Start shut after a lone Win key.
/// Every key sent here carries <see cref="Mark"/>, so Machitan's own hook can tell them apart.
/// </summary>
internal static unsafe class KeyChord
{
    /// <summary>Stamped on every key Machitan sends ("MACH").</summary>
    internal const nuint Mark = 0x4D414348;

    /// <summary>Ctrl+Esc: the system Start menu.</summary>
    internal static bool SendCtrlEsc()
    {
        return Send(User32.VK_CONTROL, User32.VK_ESCAPE);
    }

    /// <summary>Win plus a letter, e.g. 'S' for the system search.</summary>
    internal static bool SendWin(char letter)
    {
        return Send(User32.VK_LWIN, char.ToUpperInvariant(letter));
    }

    /// <summary>
    /// For the keyboard hook, which has held back the release of a lone Win key: presses and
    /// releases an unassigned key, then releases the Win key. Windows then sees "Win plus
    /// another key", so its own Start menu stays shut, and the Win key is not left held down.
    /// </summary>
    internal static bool SendMaskedWinRelease(ushort winKey)
    {
        INPUT* inputs = stackalloc INPUT[3];
        inputs[0] = Key(User32.VK_UNASSIGNED_E8, down: true);
        inputs[1] = Key(User32.VK_UNASSIGNED_E8, down: false);
        inputs[2] = Key(winKey, down: false);

        uint sent = User32.SendInput(3, inputs, sizeof(INPUT));
        return sent == 3;
    }

    /// <summary>
    /// A quick Alt press and release. Windows lets a program bring its own window to the
    /// front right after it sent a key press; used only when the gentler ways failed.
    /// </summary>
    internal static bool SendAltTap()
    {
        INPUT* inputs = stackalloc INPUT[2];
        inputs[0] = Key(User32.VK_MENU, down: true);
        inputs[1] = Key(User32.VK_MENU, down: false);

        uint sent = User32.SendInput(2, inputs, sizeof(INPUT));
        return sent == 2;
    }

    /// <summary>Presses the modifier, presses and releases the key, releases the modifier.</summary>
    private static bool Send(ushort modifier, ushort key)
    {
        INPUT* inputs = stackalloc INPUT[4];
        inputs[0] = Key(modifier, down: true);
        inputs[1] = Key(key, down: true);
        inputs[2] = Key(key, down: false);
        inputs[3] = Key(modifier, down: false);

        uint sent = User32.SendInput(4, inputs, sizeof(INPUT));
        return sent == 4;
    }

    private static INPUT Key(ushort virtualKey, bool down)
    {
        uint flags = down ? 0 : User32.KEYEVENTF_KEYUP;
        if (virtualKey is User32.VK_LWIN or User32.VK_RWIN)
        {
            flags |= User32.KEYEVENTF_EXTENDEDKEY;
        }

        return new INPUT
        {
            type = User32.INPUT_KEYBOARD,
            u = new INPUTUNION
            {
                ki = new KEYBDINPUT { wVk = virtualKey, dwFlags = flags, dwExtraInfo = Mark },
            },
        };
    }
}
