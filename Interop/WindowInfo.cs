using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Machitan.Interop;

/// <summary>Safe wrappers over the raw window and process queries.</summary>
internal static unsafe class WindowInfo
{
    private const int MaxTextLength = 1024;

    /// <summary>All top-level windows, front to back.</summary>
    internal static List<nint> EnumerateTopLevel()
    {
        var windows = new List<nint>(256);
        GCHandle handle = GCHandle.Alloc(windows);
        try
        {
            User32.EnumWindows(&EnumWindowsCallback, GCHandle.ToIntPtr(handle));
        }
        finally
        {
            handle.Free();
        }

        return windows;
    }

    [UnmanagedCallersOnly]
    private static int EnumWindowsCallback(nint hwnd, nint lParam)
    {
        try
        {
            if (GCHandle.FromIntPtr(lParam).Target is List<nint> windows)
            {
                windows.Add(hwnd);
                return 1;
            }

            return 0;
        }
        catch (Exception)
        {
            // An exception must never cross back into native code.
            return 0;
        }
    }

    internal static string GetTitle(nint hwnd)
    {
        int length = User32.GetWindowTextLengthW(hwnd);
        if (length <= 0)
        {
            return string.Empty;
        }

        int capacity = Math.Min(length + 1, MaxTextLength);
        char* buffer = stackalloc char[capacity];
        int copied = User32.GetWindowTextW(hwnd, buffer, capacity);
        return copied > 0 ? new string(buffer, 0, copied) : string.Empty;
    }

    internal static string GetClassName(nint hwnd)
    {
        const int Capacity = 256;
        char* buffer = stackalloc char[Capacity];
        int copied = User32.GetClassNameW(hwnd, buffer, Capacity);
        return copied > 0 ? new string(buffer, 0, copied) : string.Empty;
    }

    internal static bool IsCloaked(nint hwnd)
    {
        int hr = Dwmapi.DwmGetWindowAttribute(hwnd, Dwmapi.DWMWA_CLOAKED, out int cloaked, sizeof(int));
        return hr == 0 && cloaked != 0;
    }

    /// <summary>Full path of the process executable, or null when Windows will not tell us.</summary>
    internal static string? GetProcessPath(uint processId)
    {
        return GetProcessIdentity(processId).Path;
    }

    /// <summary>
    /// The executable path and, for packaged apps (store apps and packaged desktop apps),
    /// the package full name. Either part is null when Windows will not tell us.
    /// </summary>
    internal static ProcessIdentity GetProcessIdentity(uint processId)
    {
        if (processId == 0)
        {
            return default;
        }

        nint process = Kernel32.OpenProcess(Kernel32.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (process == 0)
        {
            return default;
        }

        try
        {
            string? path = null;
            const int Capacity = 1024;
            char* buffer = stackalloc char[Capacity];
            uint size = Capacity;
            if (Kernel32.QueryFullProcessImageNameW(process, 0, buffer, ref size) && size > 0)
            {
                path = new string(buffer, 0, (int)size);
            }

            string? package = null;
            const int PackageCapacity = 256;
            char* packageBuffer = stackalloc char[PackageCapacity];
            uint packageLength = PackageCapacity;
            if (Kernel32.GetPackageFullName(process, ref packageLength, packageBuffer) == 0 && packageLength > 1)
            {
                // The length includes the terminating null.
                package = new string(packageBuffer, 0, (int)packageLength - 1);
            }

            return new ProcessIdentity(path, package);
        }
        finally
        {
            Kernel32.CloseHandle(process);
        }
    }
}

/// <summary>What Windows says a process is.</summary>
internal readonly record struct ProcessIdentity(string? Path, string? PackageFullName);
