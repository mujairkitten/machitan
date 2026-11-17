using System;
using System.Collections.Generic;
using Machitan.Core;
using Machitan.Interop;

namespace Machitan.Shell;

/// <summary>
/// Show Desktop: the first click minimises every app window, the second click brings back
/// exactly the windows it minimised, in their old stacking order. The "desktop shown" state
/// ends by itself as soon as any app window is visible again.
/// </summary>
internal sealed class DesktopToggle
{
    private const long SettleMs = 1200;

    private readonly List<nint> _minimized = new();
    private nint _previousForeground;
    private long _toggledAt;

    /// <summary>Raised when <see cref="IsDesktopShown"/> changes.</summary>
    internal event Action? Changed;

    internal bool IsDesktopShown { get; private set; }

    internal void Toggle(IReadOnlyList<TaskGroup> groups)
    {
        try
        {
            if (IsDesktopShown)
            {
                RestoreWindows();
            }
            else
            {
                MinimizeWindows(groups);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Show desktop failed.", ex);
        }
    }

    /// <summary>Called whenever the task list changes.</summary>
    internal void OnTasksChanged(IReadOnlyList<TaskGroup> groups)
    {
        if (!IsDesktopShown || Environment.TickCount64 - _toggledAt < SettleMs)
        {
            return;
        }

        foreach (TaskGroup group in groups)
        {
            foreach (TaskWindow window in group.Windows)
            {
                if (!window.IsMinimized)
                {
                    // Something is back on screen, so the desktop is no longer "shown".
                    _minimized.Clear();
                    SetShown(false);
                    return;
                }
            }
        }
    }

    private void MinimizeWindows(IReadOnlyList<TaskGroup> groups)
    {
        var ours = new HashSet<nint>();
        foreach (TaskGroup group in groups)
        {
            foreach (TaskWindow window in group.Windows)
            {
                ours.Add(window.Handle);
            }
        }

        _previousForeground = User32.GetForegroundWindow();
        _minimized.Clear();

        // Front to back, so the list remembers the stacking order.
        foreach (nint hwnd in WindowInfo.EnumerateTopLevel())
        {
            if (ours.Contains(hwnd) && !User32.IsIconic(hwnd))
            {
                User32.ShowWindowAsync(hwnd, User32.SW_SHOWMINNOACTIVE);
                _minimized.Add(hwnd);
            }
        }

        _toggledAt = Environment.TickCount64;
        SetShown(true);
    }

    private void RestoreWindows()
    {
        // Back to front, so the front-most window ends up in front again.
        for (int i = _minimized.Count - 1; i >= 0; i--)
        {
            nint hwnd = _minimized[i];
            if (User32.IsWindow(hwnd) && User32.IsIconic(hwnd))
            {
                // SW_RESTORE brings a maximised window back maximised.
                User32.ShowWindowAsync(hwnd, User32.SW_RESTORE);
            }
        }

        if (_previousForeground != 0 && User32.IsWindow(_previousForeground))
        {
            User32.SetForegroundWindow(_previousForeground);
        }

        _minimized.Clear();
        _toggledAt = Environment.TickCount64;
        SetShown(false);
    }

    private void SetShown(bool shown)
    {
        if (IsDesktopShown == shown)
        {
            return;
        }

        IsDesktopShown = shown;
        Changed?.Invoke();
    }
}
