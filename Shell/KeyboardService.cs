using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Machitan.Core;
using Machitan.Interop;
using Microsoft.UI.Dispatching;

namespace Machitan.Shell;

/// <summary>One shortcut Machitan asks Windows for.</summary>
internal sealed class HotkeyDefinition
{
    internal HotkeyDefinition(string name, string group, uint modifiers, uint key, Action run, bool catchWhenWindowsOwnsIt = false)
    {
        Name = name;
        Group = group;
        Modifiers = modifiers;
        Key = key;
        Run = run;
        CatchWhenWindowsOwnsIt = catchWhenWindowsOwnsIt;
    }

    /// <summary>Shown in Settings and the log, e.g. "Win+3".</summary>
    internal string Name { get; }

    /// <summary>Shortcuts with the same group are reported together when they all fare the same, e.g. "Win+1..9".</summary>
    internal string Group { get; }

    internal uint Modifiers { get; }

    internal uint Key { get; }

    /// <summary>Runs on the UI thread.</summary>
    internal Action Run { get; }

    /// <summary>
    /// When Windows keeps this shortcut for itself, catch it in the keyboard hook instead.
    /// Only for shortcuts whose Windows version does something different (Win+1..9 would
    /// otherwise act on Explorer's hidden taskbar, not on Machitan's dock).
    /// </summary>
    internal bool CatchWhenWindowsOwnsIt { get; }
}

/// <summary>
/// The Windows key and the shortcuts, on a thread of their own, so a busy UI thread can never
/// make the keyboard lag (Windows drops a keyboard hook that answers slowly).
///
/// The hook: a lone Win key (pressed and released with nothing else) opens Machitan's Start.
/// Every combination (Win+E, Win+L, ...) passes through untouched. For the lone press, the
/// release is held back and sent again after an unassigned key, so Windows sees a
/// combination and keeps its own Start shut, and no key is left looking held down.
///
/// The shortcuts: registered with Windows one by one. Windows refuses the ones it owns itself
/// (error 1409); those are logged and left to Windows, which is expected, not a failure.
/// The one exception: a shortcut marked "catch when Windows owns it" (Win+1..9 and
/// Win+Shift+1..9) is then caught by the hook. Its key is held back from Windows (press,
/// repeats and release), and the Win release is masked as above so Windows' Start stays shut.
/// Every other key still passes through.
/// </summary>
internal sealed unsafe class KeyboardService
{
    /// <summary>
    /// Auto-repeat sends Win "down" again every few dozen milliseconds while it is held (the
    /// first repeat after at most 1 s). A longer gap means a new press whose release was never
    /// seen, for example after Win+L locked the screen.
    /// </summary>
    private const long FreshPressGapMs = 1200;

    private const int FirstHotkeyId = 0x4D00;

    // The hook callback is static (Windows calls it directly), so its state is too.
    // Only one KeyboardService runs at a time.
    private static DispatcherQueue? s_queue;
    private static Action? s_onLoneWin;
    private static volatile bool s_takeLoneWin;
    private static bool s_winDown;
    private static bool s_chord;
    private static bool s_heldBackDuringWin;
    private static long s_lastWinEvent;

    // Hook-thread only (the hook runs on the thread that installed it).
    private static readonly bool[] s_heldBack = new bool[256];
    private static List<HotkeyDefinition> s_caught = new();

    private readonly DispatcherQueue _queue;
    private readonly IReadOnlyList<HotkeyDefinition> _definitions;
    private readonly HotkeyState[] _states;
    private readonly ManualResetEventSlim _ready = new(false);
    private Thread? _thread;
    private uint _threadId;
    private int _hookError;

    private KeyboardService(DispatcherQueue queue, IReadOnlyList<HotkeyDefinition> definitions)
    {
        _queue = queue;
        _definitions = definitions;
        _states = new HotkeyState[definitions.Count];
    }

    private enum HotkeyResult
    {
        NotTried,
        Registered,
        OwnedByWindows,
        CaughtByHook,
        Failed,
    }

    /// <summary>True when the Windows key hook is installed.</summary>
    internal bool HookActive { get; private set; }

    /// <summary>
    /// Starts the keyboard thread and waits (briefly) until the hook and shortcuts are in place.
    /// <paramref name="onLoneWin"/> runs on the UI thread; null leaves a lone Win key to Windows.
    /// </summary>
    internal static KeyboardService Start(DispatcherQueue queue, Action? onLoneWin, IReadOnlyList<HotkeyDefinition> definitions)
    {
        var service = new KeyboardService(queue, definitions);
        s_queue = queue;
        s_onLoneWin = onLoneWin;
        s_takeLoneWin = onLoneWin is not null;

        service._thread = new Thread(service.Run)
        {
            IsBackground = true,
            Name = "Machitan keyboard",
        };
        service._thread.Start();

        if (!service._ready.Wait(TimeSpan.FromSeconds(3)))
        {
            Log.Warn("Keyboard: the keyboard thread did not report back within 3 s.");
        }

        return service;
    }

    /// <summary>Removes the hook and the shortcuts. Safe to call more than once.</summary>
    internal void Stop()
    {
        s_takeLoneWin = false;
        s_onLoneWin = null;

        uint threadId = _threadId;
        if (threadId != 0)
        {
            User32.PostThreadMessageW(threadId, User32.WM_QUIT, 0, 0);
            _thread?.Join(TimeSpan.FromSeconds(1));
            _threadId = 0;
        }
    }

    /// <summary>The "System status" text for Settings.</summary>
    internal string Describe()
    {
        var text = new StringBuilder();
        if (HookActive)
        {
            text.Append(s_takeLoneWin
                ? "Windows key hook: active. A lone Win key opens Machitan's Start; Win combinations pass through to Windows."
                : "Windows key hook: active, but Machitan's Start is not available, so a lone Win key opens Windows' Start.");
        }
        else
        {
            text.Append("Windows key hook: not active (error " + _hookError + "). A lone Win key opens Windows' Start.");
        }

        (List<string> registered, List<string> caught, List<string> owned, List<string> failed) = SortResults();

        text.AppendLine();
        text.Append("Shortcuts Machitan handles: " + (registered.Count > 0 ? string.Join(", ", registered) : "none") + ".");
        if (caught.Count > 0)
        {
            text.AppendLine();
            text.Append("Owned by Windows, so Machitan catches them in its keyboard hook: " + string.Join(", ", caught) + ".");
        }

        if (owned.Count > 0)
        {
            text.AppendLine();
            text.Append("Owned by Windows, so Windows handles them as usual: " + string.Join(", ", owned) + ".");
        }

        if (failed.Count > 0)
        {
            text.AppendLine();
            text.Append("Could not be set up: " + string.Join(", ", failed) + ".");
        }

        return text.ToString();
    }

    // ---------------------------------------------------------------- keyboard thread

    private void Run()
    {
        nint hook = 0;
        var registeredIds = new List<int>();
        try
        {
            _threadId = Kernel32.GetCurrentThreadId();

            // Make sure this thread has a message queue before anyone posts to it.
            User32.PeekMessageW(out _, 0, 0, 0, User32.PM_NOREMOVE);

            hook = User32.SetWindowsHookExW(User32.WH_KEYBOARD_LL, &HookProc, Kernel32.GetModuleHandleW(null), 0);
            if (hook != 0)
            {
                HookActive = true;
                Log.Info("Keyboard: Windows key hook installed.");
            }
            else
            {
                _hookError = Marshal.GetLastPInvokeError();
                Log.Warn("Keyboard: the Windows key hook could not be installed (error " + _hookError + "); a lone Win key opens Windows' Start.");
            }

            RegisterAll(registeredIds);
            _ready.Set();

            while (User32.GetMessageW(out MSG message, 0, 0, 0) > 0)
            {
                if (message.message == User32.WM_HOTKEY)
                {
                    OnHotkey((int)message.wParam);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Keyboard: the keyboard thread failed.", ex);
        }
        finally
        {
            foreach (int id in registeredIds)
            {
                User32.UnregisterHotKey(0, id);
            }

            if (hook != 0)
            {
                User32.UnhookWindowsHookEx(hook);
            }

            HookActive = false;
            _ready.Set();
        }
    }

    private void RegisterAll(List<int> registeredIds)
    {
        for (int i = 0; i < _definitions.Count; i++)
        {
            HotkeyDefinition definition = _definitions[i];
            int id = FirstHotkeyId + i;
            if (User32.RegisterHotKey(0, id, definition.Modifiers | User32.MOD_NOREPEAT, definition.Key))
            {
                _states[i] = new HotkeyState(HotkeyResult.Registered, 0);
                registeredIds.Add(id);
                continue;
            }

            int error = Marshal.GetLastPInvokeError();
            HotkeyResult result = HotkeyResult.Failed;
            if (error == User32.ERROR_HOTKEY_ALREADY_REGISTERED)
            {
                result = definition.CatchWhenWindowsOwnsIt && HookActive ? HotkeyResult.CaughtByHook : HotkeyResult.OwnedByWindows;
            }

            _states[i] = new HotkeyState(result, error);
        }

        // The hook runs on this same thread, so it can read this list without locking.
        var caughtByHook = new List<HotkeyDefinition>();
        for (int i = 0; i < _definitions.Count; i++)
        {
            if (_states[i].Result == HotkeyResult.CaughtByHook)
            {
                caughtByHook.Add(_definitions[i]);
            }
        }

        s_caught = caughtByHook;

        (List<string> registered, List<string> caught, List<string> owned, List<string> failed) = SortResults();
        var summary = new StringBuilder("Keyboard: shortcuts Machitan handles: ");
        summary.Append(registered.Count > 0 ? string.Join(", ", registered) : "none");
        if (caught.Count > 0)
        {
            summary.Append(". Owned by Windows, caught in the keyboard hook instead: " + string.Join(", ", caught));
        }

        if (owned.Count > 0)
        {
            summary.Append(". Owned by Windows (error 1409), left to Windows: " + string.Join(", ", owned));
        }

        if (failed.Count > 0)
        {
            summary.Append(". Could not be set up: " + string.Join(", ", failed));
        }

        summary.Append('.');
        if (failed.Count > 0)
        {
            Log.Warn(summary.ToString());
        }
        else
        {
            Log.Info(summary.ToString());
        }
    }

    /// <summary>Shortcut labels sorted by how registration went.</summary>
    private (List<string> Registered, List<string> Caught, List<string> Owned, List<string> Failed) SortResults()
    {
        var failed = new List<string>();
        for (int i = 0; i < _definitions.Count; i++)
        {
            if (_states[i].Result is HotkeyResult.Failed or HotkeyResult.NotTried)
            {
                failed.Add(_definitions[i].Name + (_states[i].Error != 0 ? " (error " + _states[i].Error + ")" : " (not tried)"));
            }
        }

        return (
            GroupedLabels(HotkeyResult.Registered),
            GroupedLabels(HotkeyResult.CaughtByHook),
            GroupedLabels(HotkeyResult.OwnedByWindows),
            failed);
    }

    private void OnHotkey(int id)
    {
        int index = id - FirstHotkeyId;
        if (index < 0 || index >= _definitions.Count)
        {
            return;
        }

        RunOnUiThread(_queue, _definitions[index]);
    }

    private static void RunOnUiThread(DispatcherQueue queue, HotkeyDefinition definition)
    {
        queue.TryEnqueue(() =>
        {
            try
            {
                definition.Run();
            }
            catch (Exception ex)
            {
                Log.Error("Shortcut " + definition.Name + " failed.", ex);
            }
        });
    }

    /// <summary>
    /// Labels of the shortcuts with this result. A group whose members all have it is shown
    /// by its group name ("Win+1..9"); otherwise its members are listed one by one.
    /// </summary>
    private List<string> GroupedLabels(HotkeyResult result)
    {
        var labels = new List<string>();
        var seenGroups = new HashSet<string>();
        for (int i = 0; i < _definitions.Count; i++)
        {
            string group = _definitions[i].Group;
            if (!seenGroups.Add(group))
            {
                continue;
            }

            bool all = true;
            bool any = false;
            var members = new List<string>();
            for (int j = i; j < _definitions.Count; j++)
            {
                if (_definitions[j].Group != group)
                {
                    continue;
                }

                if (_states[j].Result == result)
                {
                    any = true;
                    members.Add(_definitions[j].Name);
                }
                else
                {
                    all = false;
                }
            }

            if (all)
            {
                labels.Add(group);
            }
            else if (any)
            {
                labels.AddRange(members);
            }
        }

        return labels;
    }

    // ---------------------------------------------------------------- the hook

    [UnmanagedCallersOnly]
    private static nint HookProc(int code, nint wParam, nint lParam)
    {
        try
        {
            if (code == User32.HC_ACTION && OnKey((uint)wParam, (KBDLLHOOKSTRUCT*)lParam))
            {
                return 1;
            }
        }
        catch (Exception ex)
        {
            // An exception must never cross back into native code.
            Log.Error("Keyboard: the hook callback failed.", ex);
        }

        return User32.CallNextHookEx(0, code, wParam, lParam);
    }

    /// <summary>Returns true to hold the key event back from Windows.</summary>
    private static bool OnKey(uint message, KBDLLHOOKSTRUCT* key)
    {
        // Keys Machitan sent itself pass straight through.
        if (key->dwExtraInfo == KeyChord.Mark)
        {
            return false;
        }

        bool down = message is User32.WM_KEYDOWN or User32.WM_SYSKEYDOWN;
        uint virtualKey = key->vkCode;
        long now = Environment.TickCount64;

        if (virtualKey is User32.VK_LWIN or User32.VK_RWIN)
        {
            if (down)
            {
                if (!s_winDown || now - s_lastWinEvent > FreshPressGapMs)
                {
                    s_winDown = true;
                    s_chord = false;
                    s_heldBackDuringWin = false;
                }

                s_lastWinEvent = now;
                return false;
            }

            bool lone = s_winDown && !s_chord;
            bool heldBack = s_heldBackDuringWin;
            s_winDown = false;
            s_chord = false;
            s_heldBackDuringWin = false;
            s_lastWinEvent = now;

            if (heldBack)
            {
                // Windows never saw the other key of this combination, so to Windows this
                // was a lone Win press: mask the release, or Windows' Start would open.
                return KeyChord.SendMaskedWinRelease((ushort)virtualKey);
            }

            Action? onLoneWin = s_onLoneWin;
            DispatcherQueue? queue = s_queue;
            if (!lone || !s_takeLoneWin || onLoneWin is null || queue is null)
            {
                return false;
            }

            if (!KeyChord.SendMaskedWinRelease((ushort)virtualKey))
            {
                // Could not send the replacement release: let this one through, so the key
                // is not left held down. Windows' Start may then open as well.
                return false;
            }

            queue.TryEnqueue(() =>
            {
                try
                {
                    onLoneWin();
                }
                catch (Exception ex)
                {
                    Log.Error("Keyboard: opening Start from the Win key failed.", ex);
                }
            });
            return true;
        }

        if (virtualKey < 256 && s_heldBack[virtualKey])
        {
            // Repeats and the release of a key whose press was held back go the same way.
            if (!down)
            {
                s_heldBack[virtualKey] = false;
            }

            return true;
        }

        if (down && s_winDown && s_queue is not null && virtualKey < 256)
        {
            HotkeyDefinition? caught = FindCaught(virtualKey);
            if (caught is not null)
            {
                s_chord = true;
                s_heldBackDuringWin = true;
                s_heldBack[virtualKey] = true;
                RunOnUiThread(s_queue, caught);
                return true;
            }
        }

        if (s_winDown)
        {
            // Any other key while Win is held makes it a combination.
            s_chord = true;
        }

        return false;
    }

    /// <summary>The caught shortcut for this key with Win held and the other modifiers as they are now.</summary>
    private static HotkeyDefinition? FindCaught(uint virtualKey)
    {
        List<HotkeyDefinition> caught = s_caught;
        if (caught.Count == 0)
        {
            return null;
        }

        uint modifiers = User32.MOD_WIN;
        if (IsDown(User32.VK_SHIFT))
        {
            modifiers |= User32.MOD_SHIFT;
        }

        if (IsDown(User32.VK_CONTROL))
        {
            modifiers |= User32.MOD_CONTROL;
        }

        if (IsDown(User32.VK_MENU))
        {
            modifiers |= User32.MOD_ALT;
        }

        foreach (HotkeyDefinition definition in caught)
        {
            if (definition.Key == virtualKey && definition.Modifiers == modifiers)
            {
                return definition;
            }
        }

        return null;
    }

    private static bool IsDown(ushort virtualKey)
    {
        return (User32.GetAsyncKeyState(virtualKey) & 0x8000) != 0;
    }

    private readonly struct HotkeyState
    {
        internal HotkeyState(HotkeyResult result, int error)
        {
            Result = result;
            Error = error;
        }

        internal HotkeyResult Result { get; }

        internal int Error { get; }
    }
}
