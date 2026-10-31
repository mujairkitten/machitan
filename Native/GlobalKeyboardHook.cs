using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Dispatching;

namespace Machitan.Native;

/// <summary>
/// Installs a low-level keyboard hook (WH_KEYBOARD_LL) so Machitan can
/// intercept global hotkeys even when it does not have the foreground
/// window — exactly like Explorer's Win-key shortcuts.
///
/// Safety design (per Microsoft docs and reverse-engineering findings):
///   * The hook callback does ONLY synchronous, non-throwing work:
///     capture all state into a HookEvent struct and enqueue it.
///     No UI calls, no DispatcherQueue, no file I/O — nothing that could
///     throw and propagate to native code as ExecutionEngineException.
///   * The HookProc delegate is stored as a static readonly field so it
///     can never be garbage collected while the hook is installed.
///   * All WinUI3 interaction happens on the UI thread via a
///     DispatcherQueueTimer that drains the event queue.
///   * Modifier state is tracked ONLY in the hook thread's local variables
///     captured into HookEvent — never read back from another thread.
/// </summary>
internal sealed class GlobalKeyboardHook : IDisposable
{
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private readonly DispatcherQueue _queue;
    private readonly ConcurrentQueue<HookEvent> _pending = new();
    private IntPtr _hHook;
    private bool _disposed;

    // The HookProc delegate must stay alive for the process lifetime.
    private static readonly NativeMethods.HookProc _proc = Callback;
    private static GlobalKeyboardHook? _current;
    private static readonly object _currentLock = new();

    /// <summary>
    /// Set by <see cref="ShellPanels"/> while it injects keybd_event calls,
    /// so the hook can skip them and avoid feedback loops.
    /// </summary>
    internal static bool IsInjecting { get; set; }

    /// <summary>
    /// Whether the hook is currently active.
    /// </summary>
    internal static bool IsActive { get; private set; }

    // Modifier state — updated only on the hook thread, read via HookEvent.
    private bool _winDown;
    private bool _ctrlDown;
    private bool _shiftDown;
    private bool _altDown;

    // Win-alone detection — hook thread only.
    private bool _winPressed;
    private DateTime _winPressedAt = DateTime.MinValue;

    // Hotkey registry — accessed by hook thread (write) and UI thread (read).
    private readonly Dictionary<Hotkey, Action> _hotkeys = new();

    public GlobalKeyboardHook(DispatcherQueue queue)
    {
        _queue = queue;

        lock (_currentLock)
        {
            if (_current is not null)
                throw new InvalidOperationException("Only one GlobalKeyboardHook instance may exist.");
            _current = this;
        }

        _thread = new Thread(Run) { IsBackground = true, Name = "Machitan Global Keyboard Hook" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
        IsActive = true;
    }

    public void Register(Hotkey hotkey, Action action)
    {
        lock (_hotkeys)
        {
            _hotkeys[hotkey] = action;
        }
    }

    public void Unregister(Hotkey hotkey)
    {
        lock (_hotkeys)
        {
            _hotkeys.Remove(hotkey);
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        IsActive = false;

        lock (_currentLock)
        {
            if (_current == this)
                _current = null;
        }

        // Signal the hook thread to exit by posting WM_QUIT.
        NativeMethods.PostMessage(IntPtr.Zero, 0x0012, IntPtr.Zero, IntPtr.Zero);

        // Wait for the hook thread to finish so no orphaned thread remains.
        try
        {
            _thread.Join(TimeSpan.FromSeconds(5));
        }
        catch
        {
            // Thread join failure is not fatal; the thread may already be gone.
        }
    }

    private void Run()
    {
        _hHook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_KEYBOARD_LL, _proc, IntPtr.Zero, 0);
        if (_hHook == IntPtr.Zero)
        {
            DiagnosticsLog.WriteException("GlobalKeyboardHook",
                new InvalidOperationException($"SetWindowsHookEx failed: {Marshal.GetLastWin32Error()}"));
            _ready.Set();
            return;
        }

        _ready.Set();

        NativeMethods.MSG msg;
        while (NativeMethods.GetMessage(out msg, IntPtr.Zero, 0, 0) != 0)
        {
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessage(ref msg);
        }

        NativeMethods.UnhookWindowsHookEx(_hHook);
    }

    /// <summary>
    /// Hook callback — runs on the hook thread. Does ONLY synchronous,
    /// non-throwing work. All state needed for later processing is captured
    /// into the HookEvent struct at this moment; nothing is read later from
    /// another thread.
    /// </summary>
    private static IntPtr Callback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode < 0 || _current is not { } self || lParam == IntPtr.Zero)
                return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);

            var ks = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            bool isDown = wParam == (IntPtr)NativeMethods.WM_KEYDOWN
                       || wParam == (IntPtr)NativeMethods.WM_SYSKEYDOWN;
            bool isUp = wParam == (IntPtr)NativeMethods.WM_KEYUP
                     || wParam == (IntPtr)NativeMethods.WM_SYSKEYUP;

            byte vk = (byte)ks.vkCode;

            // Skip events injected by ShellPanels to avoid feedback loops.
            if (IsInjecting)
                return NativeMethods.CallNextHookEx(self._hHook, nCode, wParam, lParam);

            // Capture all state at this moment into the event struct.
            // Nothing is read from another thread later.
            self.UpdateModifiers(vk, isDown, isUp);

            var evt = new HookEvent(
                vk,
                isDown,
                isUp,
                ks.flags,
                self._winDown,
                self._ctrlDown,
                self._shiftDown,
                self._altDown,
                IsWinAlone: false);

            // ---------- Win key: always suppress ----------
            if (vk is NativeMethods.VK_LWIN or NativeMethods.VK_RWIN)
            {
                if (isDown)
                {
                    self._pending.Enqueue(evt);
                    self._winPressed = true;
                    self._winPressedAt = DateTime.UtcNow;
                    return (IntPtr)1;
                }
                if (isUp && self._winPressed)
                {
                    self._winPressed = false;
                    var ms = (DateTime.UtcNow - self._winPressedAt).TotalMilliseconds;
                    if (ms > 50 && !self._ctrlDown && !self._shiftDown && !self._altDown)
                    {
                        self._pending.Enqueue(evt with { IsDown = false, IsUp = true, IsWinAlone = true });
                    }
                    return (IntPtr)1;
                }
                return (IntPtr)1;
            }

            // ---------- Non-Win key with Win held ----------
            if (isDown && !IsModifier(vk))
            {
                var hotkey = new Hotkey(self._winDown, self._ctrlDown, self._shiftDown, self._altDown, vk);
                lock (self._hotkeys)
                {
                    if (self._hotkeys.TryGetValue(hotkey, out var _))
                    {
                        // Mark Win as "used" so its release isn't treated as "Win alone".
                        self._winPressed = false;
                        self._pending.Enqueue(evt with { IsWinAlone = false });
                        return (IntPtr)1;
                    }
                }

                // Not a registered hotkey: mark Win as "used" anyway.
                if (self._winDown)
                    self._winPressed = false;
            }

            // Track key-up for non-modifier keys with Win held.
            if (isUp && !IsModifier(vk) && self._winDown)
                self._winPressed = false;

            return NativeMethods.CallNextHookEx(self._hHook, nCode, wParam, lParam);
        }
        catch (Exception ex)
        {
            DiagnosticsLog.WriteException("GlobalKeyboardHook callback", ex);
            // Never let an exception propagate to native code.
            return NativeMethods.CallNextHookEx(IntPtr.Zero, nCode, wParam, lParam);
        }
    }

    /// <summary>
    /// Drains queued hook events on the UI thread and invokes actions.
    /// Called from a DispatcherQueueTimer (~60 Hz).
    /// </summary>
    internal void ProcessPendingEvents()
    {
        try
        {
            while (_pending.TryDequeue(out var ev))
            {
                try
                {
                    if (ev.IsWinAlone)
                    {
                        try { App.MainWindow?.CloseStartMenu(); } catch { }
                        try { App.MainWindow?.ShowStartMenu(null); } catch { }
                    }
                    else if (ev.IsDown && !IsModifier(ev.Vk))
                    {
                        var hotkey = new Hotkey(
                            ev.WinDown, ev.CtrlDown, ev.ShiftDown, ev.AltDown, ev.Vk);
                        lock (_hotkeys)
                        {
                            if (_hotkeys.TryGetValue(hotkey, out var action))
                                action();
                        }
                    }
                }
                catch (Exception ex)
                {
                    DiagnosticsLog.WriteException("GlobalKeyboardHook event processing", ex);
                }
            }
        }
        catch (Exception ex)
        {
            DiagnosticsLog.WriteException("GlobalKeyboardHook pump", ex);
        }
    }

    private static bool IsModifier(byte vk)
        => vk is NativeMethods.VK_LWIN or NativeMethods.VK_RWIN
          or NativeMethods.VK_CONTROL or NativeMethods.VK_SHIFT
          or NativeMethods.VK_MENU;

    private void UpdateModifiers(byte vk, bool isDown, bool isUp)
    {
        switch (vk)
        {
            case NativeMethods.VK_LWIN:
            case NativeMethods.VK_RWIN:
                _winDown = isDown;
                break;
            case NativeMethods.VK_CONTROL:
                _ctrlDown = isDown;
                break;
            case NativeMethods.VK_SHIFT:
                _shiftDown = isDown;
                break;
            case NativeMethods.VK_MENU:
                _altDown = isDown;
                break;
        }
    }
}

internal readonly record struct Hotkey(bool Win, bool Ctrl, bool Shift, bool Alt, byte Key);

/// <summary>
/// A single keyboard event captured by the hook, with all modifier state
/// frozen at capture time so the UI thread processes it consistently.
/// </summary>
internal readonly record struct HookEvent(
    byte Vk,
    bool IsDown,
    bool IsUp,
    uint Flags,
    bool WinDown,
    bool CtrlDown,
    bool ShiftDown,
    bool AltDown,
    bool IsWinAlone = false);
