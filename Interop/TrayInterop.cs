using System.Runtime.InteropServices;

namespace Machitan.Interop;

/// <summary>user32.dll: a window class of Machitan's own and its message loop (the tray receiver, Batch 6).</summary>
internal static unsafe partial class User32
{
    internal const uint WM_DESTROY = 0x0002;
    internal const uint WM_COPYDATA = 0x004A;
    internal const uint WM_CONTEXTMENU = 0x007B;
    internal const uint WM_COMMAND = 0x0111;
    internal const uint WM_TIMER = 0x0113;
    internal const uint WM_LBUTTONDOWN = 0x0201;
    internal const uint WM_LBUTTONUP = 0x0202;
    internal const uint WM_LBUTTONDBLCLK = 0x0203;
    internal const uint WM_RBUTTONDOWN = 0x0204;
    internal const uint WM_RBUTTONUP = 0x0205;
    internal const uint WM_USER = 0x0400;

    /// <summary>All top-level windows, for SendNotifyMessage.</summary>
    internal const nint HWND_BROADCAST = 0xFFFF;

    /// <summary>ChangeWindowMessageFilterEx: let the message through from lower-integrity processes.</summary>
    internal const uint MSGFLT_ALLOW = 1;

    /// <summary>Ends RegisterClassExW when this process already has the class (an earlier receiver).</summary>
    internal const int ERROR_CLASS_ALREADY_EXISTS = 1410;

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial ushort RegisterClassExW(WNDCLASSEXW* windowClass);

    [LibraryImport("user32.dll")]
    internal static partial nint DefWindowProcW(nint hwnd, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    internal static partial nint DispatchMessageW(MSG* message);

    [LibraryImport("user32.dll")]
    internal static partial void PostQuitMessage(int exitCode);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial nuint SetTimer(nint hwnd, nuint id, uint elapseMs, nint timerProc);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool KillTimer(nint hwnd, nuint id);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SendNotifyMessageW(nint hwnd, uint message, nint wParam, nint lParam);

    /// <summary>True while the message being handled was sent (not posted) by another thread.</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool InSendMessage();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ChangeWindowMessageFilterEx(nint hwnd, uint message, uint action, nint changeFilterStruct);

    [LibraryImport("user32.dll")]
    internal static partial uint GetDoubleClickTime();
}

/// <summary>winuser.h WNDCLASSEXW.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WNDCLASSEXW
{
    public uint cbSize;
    public uint style;
    public delegate* unmanaged<nint, uint, nint, nint, nint> lpfnWndProc;
    public int cbClsExtra;
    public int cbWndExtra;
    public nint hInstance;
    public nint hIcon;
    public nint hCursor;
    public nint hbrBackground;
    public char* lpszMenuName;
    public char* lpszClassName;
    public nint hIconSm;
}

/// <summary>winuser.h COPYDATASTRUCT: what a WM_COPYDATA message carries.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct COPYDATASTRUCT
{
    public nuint dwData;
    public uint cbData;
    public nint lpData;
}
