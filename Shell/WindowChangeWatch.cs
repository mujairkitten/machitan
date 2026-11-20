using System;
using System.Collections.Generic;
using Machitan.Core;
using Machitan.Interop;
using Microsoft.UI.Dispatching;

namespace Machitan.Shell;

/// <summary>
/// Tells when a top-level window of another program was shown, moved or changed size
/// (maximised, restored, snapped, or dragged and let go), so the kept strip can be checked
/// for that window alone.
/// Changes are gathered and handed over 150 ms after the first, each window once, so a window
/// being dragged costs one look per 150 ms. Out-of-context WinEvent hooks on the UI thread,
/// skipping Machitan's own windows. Use on the UI thread.
/// </summary>
internal sealed unsafe class WindowChangeWatch : IDisposable
{
    private const uint EventMoveSizeEnd = 0x000B;
    private const uint EventObjectShow = 0x8002;
    private const uint EventObjectLocationChange = 0x800B;
    private const uint SkipOwnProcess = 0x0002;
    private const uint GaRoot = 2;
    private const int ObjectWindow = 0;
    private const int ChildSelf = 0;

    private static WindowChangeWatch? s_current;

    private readonly Action<nint> _changed;
    private readonly HashSet<nint> _pending = new();
    private readonly DispatcherQueueTimer _timer;
    private nint _showHook;
    private nint _moveHook;
    private nint _dragEndHook;

    internal WindowChangeWatch(DispatcherQueue queue, Action<nint> changed)
    {
        _changed = changed;
        _timer = queue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(150);
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => HandOver();

        s_current = this;
        _showHook = User32.SetWinEventHook(
            EventObjectShow, EventObjectShow, 0, &OnEvent, 0, 0, User32.WINEVENT_OUTOFCONTEXT | SkipOwnProcess);
        _moveHook = User32.SetWinEventHook(
            EventObjectLocationChange, EventObjectLocationChange, 0, &OnEvent, 0, 0, User32.WINEVENT_OUTOFCONTEXT | SkipOwnProcess);
        _dragEndHook = User32.SetWinEventHook(
            EventMoveSizeEnd, EventMoveSizeEnd, 0, &OnEvent, 0, 0, User32.WINEVENT_OUTOFCONTEXT | SkipOwnProcess);
        if (_showHook == 0 || _moveHook == 0 || _dragEndHook == 0)
        {
            Log.Warn("Strip: windows that move or change size later are not watched (Windows refused an event hook); the strip is checked only when it is taken.");
        }
    }

    public void Dispose()
    {
        if (_showHook != 0)
        {
            User32.UnhookWinEvent(_showHook);
            _showHook = 0;
        }

        if (_moveHook != 0)
        {
            User32.UnhookWinEvent(_moveHook);
            _moveHook = 0;
        }

        if (_dragEndHook != 0)
        {
            User32.UnhookWinEvent(_dragEndHook);
            _dragEndHook = 0;
        }

        _timer.Stop();
        _pending.Clear();
        if (ReferenceEquals(s_current, this))
        {
            s_current = null;
        }
    }

    [System.Runtime.InteropServices.UnmanagedCallersOnly]
    private static void OnEvent(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            // The window itself (not its caret, cursor or a part inside it).
            if (hwnd != 0 && idObject == ObjectWindow && idChild == ChildSelf && s_current is WindowChangeWatch watch)
            {
                watch.Note(hwnd);
            }
        }
        catch (Exception)
        {
            // Nothing may escape into Windows.
        }
    }

    private void Note(nint hwnd)
    {
        if (_pending.Count < 256)
        {
            _pending.Add(hwnd);
        }

        if (!_timer.IsRunning)
        {
            _timer.Start();
        }
    }

    private void HandOver()
    {
        if (_pending.Count == 0)
        {
            return;
        }

        var windows = new List<nint>(_pending);
        _pending.Clear();
        foreach (nint hwnd in windows)
        {
            try
            {
                // Top-level windows only: child windows move inside their window.
                if (User32.IsWindow(hwnd) && User32.GetAncestor(hwnd, GaRoot) == hwnd)
                {
                    _changed(hwnd);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Strip: a window that changed could not be checked.", ex);
            }
        }
    }
}
