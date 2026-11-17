using System.Runtime.InteropServices;

namespace Machitan.Interop;

/// <summary>user32.dll imports. Grows batch by batch; only what is used is declared.</summary>
internal static unsafe partial class User32
{
    internal const int GWL_STYLE = -16;
    internal const int GWL_EXSTYLE = -20;

    internal const long WS_POPUP = 0x80000000;
    internal const long WS_BORDER = 0x00800000;
    internal const long WS_DLGFRAME = 0x00400000;
    internal const long WS_SYSMENU = 0x00080000;
    internal const long WS_THICKFRAME = 0x00040000;
    internal const long WS_MINIMIZEBOX = 0x00020000;
    internal const long WS_MAXIMIZEBOX = 0x00010000;

    internal const long WS_EX_TRANSPARENT = 0x00000020;
    internal const long WS_EX_TOOLWINDOW = 0x00000080;
    internal const long WS_EX_APPWINDOW = 0x00040000;
    internal const long WS_EX_LAYERED = 0x00080000;
    internal const long WS_EX_NOACTIVATE = 0x08000000;

    internal const uint LWA_ALPHA = 0x00000002;

    internal const int GCLP_HICONSM = -34;
    internal const int GCLP_HICON = -14;

    internal const nint ICON_BIG = 1;
    internal const nint ICON_SMALL2 = 2;

    internal const uint SMTO_ABORTIFHUNG = 0x0002;

    internal const int SM_MAXIMUMTOUCHES = 95;

    internal const uint INPUT_KEYBOARD = 1;
    internal const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    internal const uint KEYEVENTF_KEYUP = 0x0002;

    internal const ushort VK_CONTROL = 0x11;
    internal const ushort VK_ESCAPE = 0x1B;
    internal const ushort VK_LWIN = 0x5B;

    internal const uint GW_OWNER = 4;

    internal const int SW_HIDE = 0;
    internal const int SW_SHOWNOACTIVATE = 4;
    internal const int SW_SHOW = 5;
    internal const int SW_SHOWMINNOACTIVE = 7;
    internal const int SW_MINIMIZE = 6;
    internal const int SW_SHOWNA = 8;
    internal const int SW_RESTORE = 9;

    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_NOACTIVATE = 0x0010;
    internal const uint SWP_FRAMECHANGED = 0x0020;

    internal const nint HWND_TOPMOST = -1;

    internal const uint WM_SIZE = 0x0005;
    internal const uint WM_CLOSE = 0x0010;
    internal const uint WM_ERASEBKGND = 0x0014;
    internal const uint WM_ENDSESSION = 0x0016;
    internal const uint WM_SETTINGCHANGE = 0x001A;
    internal const uint WM_DISPLAYCHANGE = 0x007E;
    internal const uint WM_GETICON = 0x007F;
    internal const uint WM_SYSCOMMAND = 0x0112;
    internal const uint WM_DPICHANGED = 0x02E0;
    internal const uint WM_APP = 0x8000;

    internal const long SIZE_MINIMIZED = 1;
    internal const long SC_MINIMIZE = 0xF020;
    internal const long SPI_SETWORKAREA = 0x002F;

    /// <summary>Returns the DPI of the window (96 = 100% scale), or 0 for an invalid handle.</summary>
    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForWindow(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint FindWindow(string? className, string? windowName);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowExW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint FindWindowEx(nint parent, nint childAfter, string? className, string? windowName);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(nint hwnd, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsIconic(nint hwnd);

    [LibraryImport("user32.dll")]
    internal static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    internal static partial nint GetWindowLongPtrW(nint hwnd, int index);

    [LibraryImport("user32.dll")]
    internal static partial nint SetWindowLongPtrW(nint hwnd, int index, nint newValue);

    [LibraryImport("user32.dll")]
    internal static partial nint GetWindow(nint hwnd, uint command);

    [LibraryImport("user32.dll")]
    internal static partial int GetWindowTextLengthW(nint hwnd);

    [LibraryImport("user32.dll")]
    internal static partial int GetWindowTextW(nint hwnd, char* buffer, int maxCount);

    [LibraryImport("user32.dll")]
    internal static partial int GetClassNameW(nint hwnd, char* buffer, int maxCount);

    [LibraryImport("user32.dll")]
    internal static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    /// <summary>The callback returns 1 to continue, 0 to stop.</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumWindows(delegate* unmanaged<nint, nint, int> callback, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint RegisterWindowMessage(string name);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool RegisterShellHookWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeregisterShellHookWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowRect(nint hwnd, out RECT rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetCursorPos(out POINT point);

    /// <summary>On success the system owns the region; do not delete it afterwards.</summary>
    [LibraryImport("user32.dll")]
    internal static partial int SetWindowRgn(nint hwnd, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetClientRect(nint hwnd, out RECT rect);

    /// <summary>Fills a rectangle on a device context with a brush. Returns non-zero on success.</summary>
    [LibraryImport("user32.dll")]
    internal static partial int FillRect(nint deviceContext, ref RECT rect, nint brush);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetLayeredWindowAttributes(nint hwnd, uint colorKey, byte alpha, uint flags);

    /// <summary>Returns 0 on failure or timeout; the message's own result arrives in <paramref name="result"/>.</summary>
    [LibraryImport("user32.dll")]
    internal static partial nint SendMessageTimeoutW(nint hwnd, uint message, nint wParam, nint lParam, uint flags, uint timeoutMs, out nint result);

    [LibraryImport("user32.dll")]
    internal static partial nuint GetClassLongPtrW(nint hwnd, int index);

    /// <summary>
    /// Extracts icons at a requested pixel size. Returns the number extracted, or 0xFFFFFFFF
    /// when the file was not found. The caller must destroy each icon.
    /// </summary>
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial uint PrivateExtractIconsW(string fileName, int iconIndex, int width, int height, out nint icon, out uint iconId, uint iconCount, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyIcon(nint icon);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetIconInfo(nint icon, out ICONINFO info);

    [LibraryImport("user32.dll")]
    internal static partial nint GetDC(nint hwnd);

    [LibraryImport("user32.dll")]
    internal static partial int ReleaseDC(nint hwnd, nint deviceContext);

    /// <summary>Like ShowWindow, but never waits for the other app (a hung app cannot hang the shell).</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindowAsync(nint hwnd, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    internal static partial int GetSystemMetrics(int index);

    /// <summary>Returns the number of events inserted into the input stream.</summary>
    [LibraryImport("user32.dll")]
    internal static partial uint SendInput(uint count, INPUT* inputs, int size);

    /// <summary>Returns the region type: 0 = the window has no region (or error), 1 = empty, 2 = simple, 3 = complex.</summary>
    [LibraryImport("user32.dll")]
    internal static partial int GetWindowRgnBox(nint hwnd, out RECT box);

    /// <summary>True when the window is maximised.</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsZoomed(nint hwnd);
}
