using System;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Machitan.Native;

namespace Machitan.Native;

/// <summary>
/// Machitan's own icon in the notification area ("app tray").
///
/// Left-click toggles the Start menu. Right-click opens the Start menu
/// (which contains Settings, power options, and Exit). The icon is loaded
/// from Machitan's own executable.
///
/// A hidden message window receives the callback messages that
/// Shell_NotifyIcon sends back. Clicks are marshaled to the WinUI3 UI thread
/// via DispatcherQueue so they can open XAML UI.
/// </summary>
internal sealed class MachitanTray : IDisposable
{
    private const uint TrayId = 1;
    private const uint CallbackMessage = 0x8001; // WM_APP + 1

    private static readonly Guid MachitanGuid = new("{4F42A3C1-8E9D-4B2F-9C7A-6D5E3F1A0B8C}");

    // The one active tray instance: the static WndProc needs it to reach the
    // DispatcherQueue (same pattern TrayListener uses for its window proc).
    private static MachitanTray? _current;

    private readonly DispatcherQueue _queue;
    private readonly IntPtr _hwnd;
    private readonly IntPtr _hIcon;
    private readonly Icon? _iconOwner; // keeps the cloned icon (and its HICON) alive
    private readonly Timer? _tipTimer; // updates tray tip to show hook state
    private bool _disposed;

    // Keeps the WndProc delegate alive for the process lifetime.
    private static readonly NativeMethods.WndProc _wndProc = WndProc;
    private static readonly IntPtr _wndProcPtr = Marshal.GetFunctionPointerForDelegate(_wndProc);

    public MachitanTray(Window owner, DispatcherQueue queue)
    {
        _queue = queue;
        _current = this;

        var hInstance = NativeMethods.GetModuleHandle(null);
        _iconOwner = LoadAppIcon(out _hIcon);
        _hwnd = CreateMessageWindow(hInstance);
        AddIcon();

        // Periodically update the tray tip to show whether the global hook
        // is active, so the user can see at a glance whether Machitan is
        // intercepting keyboard input (G1-2, H2-1).
        _tipTimer = new Timer(UpdateTrayTip, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        if (_current == this)
            _current = null;

        _tipTimer?.Dispose();

        try
        {
            var nid = new NativeMethods.NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = TrayId,
            };
            NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref nid);
        }
        catch { }

        // _iconOwner.Dispose() destroys the HICON; do not destroy _hIcon
        // separately to avoid a double-free.
        _iconOwner?.Dispose();

        if (_hwnd != IntPtr.Zero)
        {
            try { NativeMethods.DestroyWindow(_hwnd); } catch { }
        }
    }

    // ---------- Window creation ----------

    private static IntPtr CreateMessageWindow(IntPtr hInstance)
    {
        const uint WS_POPUP = 0x80000000;
        const uint WS_EX_TOOLWINDOW = 0x00000080;
        const uint WS_EX_NOACTIVATE = 0x08000000;

        var wc = new NativeMethods.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            lpfnWndProc = _wndProcPtr,
            hInstance = hInstance,
            lpszClassName = "MachitanTrayMsgWnd"!,
        };
        if (NativeMethods.RegisterClassEx(ref wc) == 0)
            throw new InvalidOperationException($"RegisterClassEx failed: {Marshal.GetLastWin32Error()}");

        var hwnd = NativeMethods.CreateWindowEx(
            WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            wc.lpszClassName!, string.Empty,
            WS_POPUP,
            0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");

        return hwnd;
    }

    private static Icon? LoadAppIcon(out IntPtr hIcon)
    {
        hIcon = IntPtr.Zero;
        try
        {
            // Process.GetCurrentProcess().MainModule can throw "Access is denied"
            // on some builds; fall back to the assembly location if it does.
            string? exe;
            try
            {
                exe = Process.GetCurrentProcess().MainModule?.FileName;
            }
            catch
            {
                exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
            }
            if (string.IsNullOrEmpty(exe))
                return null;

            var result = NativeMethods.SHGetFileInfo(
                exe, 0, out NativeMethods.SHFILEINFO info,
                (uint)Marshal.SizeOf<NativeMethods.SHFILEINFO>(),
                NativeMethods.SHGFI_ICON | NativeMethods.SHGFI_LARGEICON);
            if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
                return null;

            // Icon.FromHandle takes ownership of info.hIcon. Clone() makes a
            // copy (via CopyIcon) that the returned Icon owns. Wrapping
            // FromHandle in a `using` destroys info.hIcon deterministically
            // so the temporary wrapper doesn't double-free it during finalization.
            using var temp = Icon.FromHandle(info.hIcon);
            var cloned = (Icon)temp.Clone();
            hIcon = cloned.Handle;
            return cloned;
        }
        catch
        {
            return null;
        }
    }

    // ---------- Shell_NotifyIcon ----------

    private void AddIcon()
    {
        var nid = new NativeMethods.NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = TrayId,
            uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP | NativeMethods.NIF_GUID,
            uCallbackMessage = CallbackMessage,
            hIcon = _hIcon,
            szTip = "Machitan Shell",
            uVersion = 4,
            guidItem = MachitanGuid,
        };
        NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref nid);
    }

    private void UpdateTrayTip(object? state)
    {
        try
        {
            var tip = GlobalKeyboardHook.IsActive
                ? "Machitan Shell (global hotkeys active)"
                : "Machitan Shell (global hotkeys disabled)";
            var nid = new NativeMethods.NOTIFYICONDATA
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
                hWnd = _hwnd,
                uID = TrayId,
                uFlags = NativeMethods.NIF_TIP,
                szTip = tip,
            };
            NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref nid);
        }
        catch
        {
            // Tip update failure is not fatal.
        }
    }

    // ---------- Window procedure ----------

    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == CallbackMessage)
            {
                uint mouseMsg = (uint)lParam & 0xFFFF;
                if (mouseMsg == NativeMethods.WM_LBUTTONUP || mouseMsg == NativeMethods.WM_LBUTTONDBLCLK)
                {
                    _current?._queue.TryEnqueue(() =>
                    {
                        try { App.MainWindow?.CloseStartMenu(); } catch { }
                    });
                    return IntPtr.Zero;
                }
                if (mouseMsg == NativeMethods.WM_RBUTTONUP)
                {
                    _current?._queue.TryEnqueue(ShowContextMenu);
                    return IntPtr.Zero;
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticsLog.WriteException("MachitanTray WndProc", ex);
        }

        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private static void ShowContextMenu()
    {
        try
        {
            App.MainWindow?.CloseStartMenu();
            App.MainWindow?.ShowStartMenuFromTray();
        }
        catch { }
    }
}
