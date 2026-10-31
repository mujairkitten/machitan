using System;
using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Machitan.Native;

namespace Machitan.Native;

/// <summary>
/// Registers global shell hotkeys via the low-level keyboard hook and maps
/// them to shell actions:
///
///   Win            - open / close Start menu
///   Ctrl+Esc       - open Start menu
///   Win+1..9       - switch to / launch taskbar app at that position
///   Win+Shift+1..9 - launch a new instance of taskbar app at that position
///   Win+D          - show desktop / restore windows
///   Win+E          - open File Explorer
///   Win+R          - open Run dialog
///   Win+L          - lock workstation
///   Win+A          - Quick Settings
///   Win+N          - Notification Center
///   Win+Tab        - Task View
///   Win+I          - Settings
///   Win+X          - Quick links (opens Start menu)
///   Win+V          - Clipboard history
///   Win+Shift+S    - Snipping tool
///   Win+.          - Emoji panel
///   Alt+Tab        - Task View
///   Alt+Esc        - cycle through windows
///
/// Uses <see cref="GlobalKeyboardHook"/> (WH_KEYBOARD_LL) so Machitan can
/// suppress Explorer's built-in Win-key shortcuts and handle them itself.
/// The hook only queues events; all actions run on the UI thread via a
/// DispatcherQueueTimer to avoid cross-thread issues.
/// </summary>
internal sealed class ShellHotkeys : IDisposable
{
    private readonly GlobalKeyboardHook _hook;
    private readonly DispatcherQueue _queue;
    private readonly DispatcherQueueTimer _processTimer;
    private bool _disposed;

    public ShellHotkeys(DispatcherQueue queue)
    {
        _queue = queue;
        _hook = new GlobalKeyboardHook(queue);

        Register(NativeMethods.MOD_CONTROL, NativeMethods.VK_ESCAPE, () => ToggleStartMenu()); // Ctrl+Esc
        Register(NativeMethods.MOD_WIN, 0, () => ToggleStartMenu());                            // Win alone

        // Win+1 .. Win+9: switch to / launch taskbar app at that position
        for (int i = 1; i <= 9; i++)
        {
            int index = i - 1;
            byte vk = (byte)(0x30 + i); // '1'..'9'
            Register(NativeMethods.MOD_WIN, vk, () => SwitchOrLaunchTaskbarApp(index));
        }

        // Win+Shift+1 .. Win+9: launch a new instance of taskbar app at that position
        for (int i = 1; i <= 9; i++)
        {
            int index = i - 1;
            byte vk = (byte)(0x30 + i); // '1'..'9'
            Register(NativeMethods.MOD_WIN | NativeMethods.MOD_SHIFT, vk, () => LaunchTaskbarApp(index));
        }

        // Win+D: show desktop / restore
        Register(NativeMethods.MOD_WIN, NativeMethods.VK_D, ToggleDesktop);

        // Win+E: File Explorer
        Register(NativeMethods.MOD_WIN, NativeMethods.VK_E, () => StartProcess("explorer.exe"));

        // Win+R: Run dialog
        Register(NativeMethods.MOD_WIN, NativeMethods.VK_R, () => StartProcess("rundll32.exe", "shell32.dll,Options_RunDLL 1"));

        // Win+L: Lock workstation
        Register(NativeMethods.MOD_WIN, NativeMethods.VK_L, LockWorkstation);

        // Win+A: Quick Settings
        Register(NativeMethods.MOD_WIN, NativeMethods.VK_A, () => ShellPanels.OpenQuickSettings());

        // Win+N: Notifications / calendar
        Register(NativeMethods.MOD_WIN, NativeMethods.VK_N, () => ShellPanels.OpenNotifications());

        // Win+Tab: Task View
        Register(NativeMethods.MOD_WIN, NativeMethods.VK_TAB, () => ShellPanels.OpenTaskView());

        // Win+I: Settings
        Register(NativeMethods.MOD_WIN, NativeMethods.VK_I, () =>
        {
            App.MainWindow?.OpenSettings();
        });

        // Win+X: Quick links menu
        Register(NativeMethods.MOD_WIN, NativeMethods.VK_X, OpenQuickLinks);

        // Win+V: Clipboard history
        Register(NativeMethods.MOD_WIN, NativeMethods.VK_V, () => StartProcess("clipboard.exe"));

        // Win+Shift+S: Snipping tool
        Register(NativeMethods.MOD_WIN | NativeMethods.MOD_SHIFT, NativeMethods.VK_S, () =>
            StartProcess("SnippingTool.exe"));

        // Win+. : Emoji panel
        Register(NativeMethods.MOD_WIN, NativeMethods.VK_PERIOD, () =>
            StartProcess("explorer.exe", "shell:::{214222c7-bb62-4c8a-8183-8a37c6a7e965}"));

        // Alt+Tab: Task View
        Register(NativeMethods.MOD_ALT, NativeMethods.VK_TAB, () => ShellPanels.OpenTaskView());

        // Alt+Esc: cycle windows
        Register(NativeMethods.MOD_ALT, NativeMethods.VK_ESCAPE, CycleWindows);

        // UI-thread timer: process queued hook events (actions, Start menu toggle).
        _processTimer = _queue.CreateTimer();
        _processTimer.Interval = TimeSpan.FromMilliseconds(16); // ~60 Hz
        _processTimer.Tick += (_, _) => _hook.ProcessPendingEvents();
        _processTimer.Start();
    }

    private void Register(uint modifiers, byte vk, Action action)
    {
        var win = (modifiers & NativeMethods.MOD_WIN) != 0;
        var ctrl = (modifiers & NativeMethods.MOD_CONTROL) != 0;
        var shift = (modifiers & NativeMethods.MOD_SHIFT) != 0;
        var alt = (modifiers & NativeMethods.MOD_ALT) != 0;

        _hook.Register(new Hotkey(win, ctrl, shift, alt, vk), action);
    }

    private void ToggleStartMenu()
    {
        // ShowStartMenu handles the toggle internally.
        App.MainWindow?.ShowStartMenu(null);
    }

    private void SwitchOrLaunchTaskbarApp(int index)
    {
        var main = App.MainWindow;
        if (main == null)
            return;

        main.SwitchTaskbarAppByIndex(index);
    }

    private void LaunchTaskbarApp(int index)
    {
        var main = App.MainWindow;
        if (main == null)
            return;

        main.LaunchTaskbarApp(index);
    }

    private void ToggleDesktop()
    {
        try
        {
            var shell = Type.GetTypeFromProgID("Shell.Application");
            if (shell != null)
            {
                var instance = Activator.CreateInstance(shell);
                shell.InvokeMember("ToggleDesktop", System.Reflection.BindingFlags.InvokeMethod,
                    null, instance, null);
            }
        }
        catch (Exception ex)
        {
            DiagnosticsLog.WriteException("ToggleDesktop", ex);
            // Fallback: minimize all windows via Win+D simulation is not
            // available without keybd_event; log and continue.
        }
    }

    private static void LockWorkstation()
    {
        NativeMethods.LockWorkStation();
    }

    private static void StartProcess(string fileName, string? arguments = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments ?? string.Empty,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            DiagnosticsLog.WriteException($"StartProcess({fileName})", ex);
            // Swallow: the app may not be installed, and we must not crash
            // the shell. The failure is logged for diagnostics.
        }
    }

    private void OpenQuickLinks()
    {
        App.MainWindow?.ShowStartMenu(null);
    }

    private void CycleWindows()
    {
        var main = App.MainWindow;
        if (main == null)
            return;

        main.CycleNextWindow();
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _processTimer?.Stop();
            _hook.Dispose();
        }
    }
}