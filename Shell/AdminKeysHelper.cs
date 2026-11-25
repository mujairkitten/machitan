using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Machitan.Core;
using Machitan.Interop;

namespace Machitan.Shell;

/// <summary>
/// The admin-keys helper: Machitan.exe --adminkeys, started as administrator by its scheduled
/// task (see <see cref="AdminKeys"/>). No window to see, no UI framework: a message-only window,
/// Machitan's keyboard hook (the same logic as the shell's), and a message loop. The hook acts
/// only while an app with more rights than the shell is in front; then a lone Win and the
/// shortcuts the shell catches are held back from Windows and posted to the shell. Before
/// each, it lets the shell bring its own window to the front (Windows allows that only to a
/// program that just had the input). It ends when the shell asks, or when the shell is gone.
/// </summary>
internal static unsafe class AdminKeysHelper
{
    private const nuint TimerWatch = 1;
    private const long FrontRereadMs = 5000;

    private static nint s_host;
    private static uint s_shellProcess;
    private static int s_shellIntegrity = TokenInfo.MediumIntegrity;
    private static nint s_hwnd;

    // The front app's rights, read again when it changes (hook thread only).
    private static uint s_frontProcess;
    private static int s_frontIntegrity = -1;
    private static long s_frontReadAt;

    internal static int Run()
    {
        try
        {
            if (!TokenInfo.IsElevated())
            {
                Log.Warn("Admin keys helper: not running as administrator, so it cannot see more than the shell; ended.");
                return 1;
            }

            if (User32.FindWindowEx(AdminKeys.MessageOnlyParent, 0, AdminKeys.HelperClass, null) != 0)
            {
                Log.Info("Admin keys helper: one is already running; this one ends.");
                return 0;
            }

            // The shell's window (the shell starts the task right after making it).
            for (int i = 0; i < 60 && s_host == 0; i++)
            {
                s_host = User32.FindWindowEx(AdminKeys.MessageOnlyParent, 0, AdminKeys.HostClass, null);
                if (s_host == 0)
                {
                    Thread.Sleep(250);
                }
            }

            if (s_host == 0)
            {
                Log.Warn("Admin keys helper: Machitan's shell was not found within 15 s; ended.");
                return 1;
            }

            User32.GetWindowThreadProcessId(s_host, out s_shellProcess);
            int shellIntegrity = TokenInfo.IntegrityOf(s_shellProcess);
            if (shellIntegrity > 0)
            {
                s_shellIntegrity = shellIntegrity;
            }

            if (!MakeWindow())
            {
                return 1;
            }

            // The shell runs with fewer rights; let its WM_COPYDATA through (the only message it sends).
            User32.ChangeWindowMessageFilterEx(s_hwnd, User32.WM_COPYDATA, User32.MSGFLT_ALLOW, 0);

            nint hook = KeyboardService.InstallHelperHook(FrontHasMoreRights, OnLoneWin, OnShortcut);
            if (hook == 0)
            {
                Log.Warn("Admin keys helper: the keyboard hook could not be installed (error " + Marshal.GetLastPInvokeError() + "); ended.");
                User32.DestroyWindow(s_hwnd);
                return 1;
            }

            User32.SetTimer(s_hwnd, TimerWatch, 2000, 0);
            User32.PostMessageW(s_host, AdminKeys.MsgHello, s_hwnd, 0);
            Log.Info("Admin keys helper: running for the shell (process " + s_shellProcess + ").");

            MSG message;
            while (User32.GetMessageW(out message, 0, 0, 0) > 0)
            {
                User32.DispatchMessageW(&message);
            }

            User32.UnhookWindowsHookEx(hook);
            User32.KillTimer(s_hwnd, TimerWatch);
            User32.DestroyWindow(s_hwnd);
            if (User32.IsWindow(s_host))
            {
                User32.PostMessageW(s_host, AdminKeys.MsgGoodbye, 0, 0);
            }

            Log.Info("Admin keys helper: ended.");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Error("Admin keys helper: failed.", ex);
            return 1;
        }
    }

    private static bool MakeWindow()
    {
        nint instance = Kernel32.GetModuleHandleW(null);
        fixed (char* className = AdminKeys.HelperClass)
        {
            var windowClass = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WndProc,
                hInstance = instance,
                lpszClassName = className,
            };
            if (User32.RegisterClassExW(&windowClass) == 0 && Marshal.GetLastPInvokeError() != User32.ERROR_CLASS_ALREADY_EXISTS)
            {
                Log.Warn("Admin keys helper: its window could not be set up; ended.");
                return false;
            }
        }

        s_hwnd = User32.CreateWindowEx(0, AdminKeys.HelperClass, string.Empty, 0, 0, 0, 0, 0, AdminKeys.MessageOnlyParent, 0, instance, 0);
        if (s_hwnd == 0)
        {
            Log.Warn("Admin keys helper: its window could not be made (error " + Marshal.GetLastPInvokeError() + "); ended.");
            return false;
        }

        return true;
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        try
        {
            if (message == User32.WM_COPYDATA)
            {
                // Only the shell's own window may tell the helper anything.
                if (wParam != s_host || lParam == 0)
                {
                    return 0;
                }

                var data = (COPYDATASTRUCT*)lParam;
                switch (data->dwData)
                {
                    case AdminKeys.DataShortcuts:
                        TakeShortcuts((byte*)data->lpData, (int)data->cbData);
                        return 1;
                    case AdminKeys.DataForeground:
                        LetShellToFront();
                        return 1;
                    case AdminKeys.DataQuit:
                        User32.PostQuitMessage(0);
                        return 1;
                    case AdminKeys.DataVolumeKeys:
                        TakeVolumeKeys(data->cbData >= 1 && data->lpData != 0 && *(byte*)data->lpData != 0);
                        return 1;
                }

                return 0;
            }

            if (message == User32.WM_TIMER && (nuint)wParam == TimerWatch && !User32.IsWindow(s_host))
            {
                // The shell is gone (closed or crashed): nothing to catch keys for.
                User32.PostQuitMessage(0);
                return 0;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Admin keys helper: a message could not be handled.", ex);
        }

        return User32.DefWindowProcW(hwnd, message, wParam, lParam);
    }

    /// <summary>The shortcuts the shell catches: count, then index, modifiers and key for each (4 bytes apiece).</summary>
    private static void TakeShortcuts(byte* bytes, int length)
    {
        var shortcuts = new List<HotkeyDefinition>();
        if (bytes != null && length >= 4)
        {
            var table = new ReadOnlySpan<byte>(bytes, length);
            int count = BitConverter.ToInt32(table.Slice(0, 4));
            for (int i = 0; i < count && 4 + ((i + 1) * 12) <= length; i++)
            {
                int at = 4 + (i * 12);
                int index = BitConverter.ToInt32(table.Slice(at, 4));
                uint modifiers = BitConverter.ToUInt32(table.Slice(at + 4, 4));
                uint key = BitConverter.ToUInt32(table.Slice(at + 8, 4));
                shortcuts.Add(new HotkeyDefinition("#" + index, string.Empty, modifiers, key, () =>
                {
                    LetShellToFront();
                    User32.PostMessageW(s_host, AdminKeys.MsgShortcut, index, 0);
                }));
            }
        }

        KeyboardService.SetHelperShortcuts(shortcuts);
        Log.Info("Admin keys helper: catching " + shortcuts.Count + " shortcut(s) while an app running as administrator is in front.");
    }

    /// <summary>
    /// Volume up, down and mute while an app running as administrator is in front: kept from
    /// Windows and posted to the shell, which changes the volume (VolumeKeys) and shows it in Dynaland.
    /// </summary>
    private static void TakeVolumeKeys(bool on)
    {
        KeyboardService.SetVolumeKeySink(on ? key => User32.PostMessageW(s_host, AdminKeys.MsgVolumeKey, (nint)key, 0) : null);
        Log.Info(on
            ? "Admin keys helper: taking the volume keys while an app running as administrator is in front."
            : "Admin keys helper: the volume keys are Windows' again.");
    }

    private static void OnLoneWin()
    {
        LetShellToFront();
        User32.PostMessageW(s_host, AdminKeys.MsgLoneWin, 0, 0);
    }

    private static void OnShortcut(HotkeyDefinition definition)
    {
        definition.Run();
    }

    /// <summary>The helper may move the front (it runs with the front app's rights): it passes that on to the shell.</summary>
    private static void LetShellToFront()
    {
        KeyChord.SendEmptyMouseInput();
        User32.AllowSetForegroundWindow((int)s_shellProcess);
    }

    /// <summary>True while the app in front runs with more rights than the shell (the shell's own hook is blind then).</summary>
    private static bool FrontHasMoreRights()
    {
        nint front = User32.GetForegroundWindow();
        if (front == 0)
        {
            return false;
        }

        User32.GetWindowThreadProcessId(front, out uint process);
        long now = Environment.TickCount64;
        if (process != s_frontProcess || now - s_frontReadAt > FrontRereadMs)
        {
            s_frontProcess = process;
            s_frontReadAt = now;
            s_frontIntegrity = process == 0 ? -1 : TokenInfo.IntegrityOf(process);
        }

        return s_frontIntegrity > s_shellIntegrity;
    }
}
