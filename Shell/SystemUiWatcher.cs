using System;
using System.Runtime.InteropServices;
using Machitan.Core;
using Machitan.Interop;

namespace Machitan.Shell;

/// <summary>
/// Watches Windows' own Start, which Machitan cannot always stop at the keyboard: while an app
/// running as administrator (regedit, Event Viewer, ...) is in front, Windows hides every key
/// from programs that are not (UIPI), Machitan's keyboard hook included, so the Win key then
/// opens Windows' Start. When Windows' Start comes to the front and Machitan did not ask for it
/// (its Ctrl+Esc fallback), it is closed (Esc) and Machitan's Start opens instead.
///
/// (Parts 10 to 12 also moved Windows' Win+X menu here. On this Windows build that menu is a
/// XAML popup that Windows keeps placing again, so it jumped about; Machitan now has its own
/// Win+X menu, Flyouts/WinXMenuWindow.cs.)
///
/// Through an out-of-context WinEvent hook on the UI thread. Use on the UI thread.
/// </summary>
internal sealed unsafe class SystemUiWatcher : IDisposable
{
    private const int ObjectWindow = 0;
    private const string WindowsStartHost = "StartMenuExperienceHost.exe";
    private const long OwnRequestWindowMs = 3000;

    private static SystemUiWatcher? s_current;

    private readonly ShellHost _host;
    private nint _foregroundHook;
    private long _replacedAt;

    internal SystemUiWatcher(ShellHost host)
    {
        _host = host;
        s_current = this;
        _foregroundHook = User32.SetWinEventHook(
            User32.EVENT_SYSTEM_FOREGROUND, User32.EVENT_SYSTEM_FOREGROUND, 0, &OnEvent, 0, 0, User32.WINEVENT_OUTOFCONTEXT);
        if (_foregroundHook == 0)
        {
            Log.Warn("Windows' Start: not watched (Windows refused an event hook).");
        }
    }

    public void Dispose()
    {
        if (_foregroundHook != 0)
        {
            User32.UnhookWinEvent(_foregroundHook);
            _foregroundHook = 0;
        }

        if (ReferenceEquals(s_current, this))
        {
            s_current = null;
        }
    }

    [UnmanagedCallersOnly]
    private static void OnEvent(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            if (hwnd != 0 && idObject == ObjectWindow && s_current is SystemUiWatcher watcher)
            {
                watcher.OnForeground(hwnd);
            }
        }
        catch (Exception ex)
        {
            // Nothing may escape into Windows.
            Log.Warn("Windows' Start watch failed once.", ex);
        }
    }

    private void OnForeground(nint hwnd)
    {
        if (_host.StartMenu is not { } start || !IsProgram(hwnd, WindowsStartHost))
        {
            return;
        }

        long now = Environment.TickCount64;
        if (now - KeyChord.CtrlEscSentAt < OwnRequestWindowMs || now - _replacedAt < 500)
        {
            return; // Machitan asked for Windows' Start itself (its fallback), or this is the same opening.
        }

        _replacedAt = now;
        KeyChord.SendEscape();
        Log.Info("Windows' Start opened (the Win key went past Machitan, e.g. while an app running as administrator was in front); closed it and opened Machitan's Start.");
        start.Open();
    }

    private static bool IsProgram(nint hwnd, string fileName)
    {
        User32.GetWindowThreadProcessId(hwnd, out uint process);
        string? path = WindowInfo.GetProcessPath(process);
        return path is not null && string.Equals(System.IO.Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase);
    }
}
