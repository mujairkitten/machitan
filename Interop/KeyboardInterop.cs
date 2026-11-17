using System.Runtime.InteropServices;

namespace Machitan.Interop;

/// <summary>user32.dll: the keyboard hook, hotkeys, a thread's own message loop, and focus.</summary>
internal static unsafe partial class User32
{
    internal const int WH_KEYBOARD_LL = 13;
    internal const int HC_ACTION = 0;

    internal const uint WM_QUIT = 0x0012;
    internal const uint WM_KEYDOWN = 0x0100;
    internal const uint WM_KEYUP = 0x0101;
    internal const uint WM_SYSKEYDOWN = 0x0104;
    internal const uint WM_SYSKEYUP = 0x0105;
    internal const uint WM_HOTKEY = 0x0312;

    internal const uint PM_NOREMOVE = 0x0000;

    internal const uint MOD_ALT = 0x0001;
    internal const uint MOD_CONTROL = 0x0002;
    internal const uint MOD_SHIFT = 0x0004;
    internal const uint MOD_WIN = 0x0008;
    internal const uint MOD_NOREPEAT = 0x4000;

    internal const ushort VK_SHIFT = 0x10;
    internal const ushort VK_MENU = 0x12;
    internal const ushort VK_RWIN = 0x5C;

    /// <summary>A virtual-key code Windows leaves unassigned; pressing it means nothing to any app.</summary>
    internal const ushort VK_UNASSIGNED_E8 = 0xE8;

    /// <summary>"Hot key is already registered": another program (usually Windows itself) owns the chord.</summary>
    internal const int ERROR_HOTKEY_ALREADY_REGISTERED = 1409;

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nint SetWindowsHookExW(int hookId, delegate* unmanaged<int, nint, nint, nint> callback, nint module, uint threadId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnhookWindowsHookEx(nint hook);

    [LibraryImport("user32.dll")]
    internal static partial nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint virtualKey);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UnregisterHotKey(nint hwnd, int id);

    /// <summary>Returns 0 on WM_QUIT, -1 on error, else a positive value.</summary>
    [LibraryImport("user32.dll")]
    internal static partial int GetMessageW(out MSG message, nint hwnd, uint filterMin, uint filterMax);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PeekMessageW(out MSG message, nint hwnd, uint filterMin, uint filterMax, uint remove);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostThreadMessageW(uint threadId, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AttachThreadInput(uint attachThreadId, uint attachToThreadId, [MarshalAs(UnmanagedType.Bool)] bool attach);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool BringWindowToTop(nint hwnd);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool LockWorkStation();

    /// <summary>High bit set: the key is down right now.</summary>
    [LibraryImport("user32.dll")]
    internal static partial short GetAsyncKeyState(int virtualKey);
}

/// <summary>kernel32.dll: thread and module identity.</summary>
internal static unsafe partial class Kernel32
{
    [LibraryImport("kernel32.dll")]
    internal static partial uint GetCurrentThreadId();

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint GetModuleHandleW(string? moduleName);
}

/// <summary>winuser.h MSG.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MSG
{
    public nint hwnd;
    public uint message;
    public nint wParam;
    public nint lParam;
    public uint time;
    public POINT pt;
    public uint lPrivate;
}

/// <summary>winuser.h KBDLLHOOKSTRUCT: one key event seen by the low-level keyboard hook.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct KBDLLHOOKSTRUCT
{
    public uint vkCode;
    public uint scanCode;
    public uint flags;
    public uint time;
    public nuint dwExtraInfo;
}
