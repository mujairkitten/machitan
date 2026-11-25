using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using Machitan.Core;
using Machitan.Interop;
using Microsoft.UI.Dispatching;

namespace Machitan.Shell;

/// <summary>
/// Hides Windows' own volume and brightness flyout and its popups for new notifications while
/// Dynaland shows them instead (v0.9.3, Most wanted item 5). No key reaches Machitan for the
/// brightness flyout, and Windows has no switch for either, so the only way is to hide
/// Windows' window the moment it shows. Test 2 on Sam's PC (2026-11-25): both were hidden in
/// the same millisecond Windows showed them, and stayed hidden; Explorer showed its flyout
/// again at every step, so nothing is left stuck when Machitan stops hiding.
///
/// Which windows:
/// - The volume and brightness flyout: Explorer's "XamlExplorerHostIslandWindow", small (a
///   third of its screen wide, a quarter high at most), in the lower third and centred. Alt+Tab,
///   Task View and the snap layouts use the same kind of window, but big or high up: left alone.
/// - A popup for a new notification: a "Windows.UI.Core.CoreWindow" of ShellExperienceHost.exe
///   (or ShellHost.exe) titled "New notification" (English Windows; another title where popups
///   show is logged once and left alone). Wherever it is: it may show before it reaches its place.
///   It is caught when Windows shows or uncloaks it, when it moves or changes size while on
///   screen, and, should both be missed, by a check four times a second (part 5b missed some
///   popups on Sam's PC; the lines marked "(test)" in diagnostics.log say which of these caught each).
///
/// Nothing is lost: each hide must be confirmed by what Dynaland shows. The flyout, by a
/// volume or brightness change shortly before or after (else it was some other flyout, such
/// as airplane mode); a popup, by Dynaland finding the new notification in Windows' list
/// (else it was one Windows keeps out of its list). Unconfirmed, Windows' own window is shown
/// again (late, but there), unless Windows already took it away itself, or only the check
/// found it on screen (then it was most likely an empty popup window), or it showed again
/// soon after a popup Dynaland showed: Windows shows its popup window once more about 6 s
/// after a popup (the end of its time, part 5c on Sam's PC), with the same notification, and
/// showing that again would bring the notification back a second time. Those are left
/// hidden (logged). A window shown again is left alone until Windows shows it afresh.
///
/// Through out-of-context WinEvent hooks on the UI thread, only while something is hidden.
/// </summary>
internal sealed unsafe class WindowsFlyouts : IDisposable
{
    private const uint EventObjectShow = 0x8002;
    private const uint EventObjectHide = 0x8003;
    private const uint EventObjectLocationChange = 0x800B;
    private const uint EventObjectCloaked = 0x8017;
    private const uint EventObjectUncloaked = 0x8018;
    private const uint SkipOwnProcess = 0x0002;
    private const int ObjectWindow = 0;
    private const int ChildSelf = 0;

    private const string LevelClass = "XamlExplorerHostIslandWindow";
    private const string PopupClass = "Windows.UI.Core.CoreWindow";

    /// <summary>A volume or brightness change this long before the flyout shows confirms it.</summary>
    private const long LevelBeforeMs = 1500;

    /// <summary>How long a hidden flyout waits for its change (Sam's PC: up to about 300 ms).</summary>
    private const long LevelWaitMs = 700;

    /// <summary>How long a hidden popup waits for Dynaland to find its notification.</summary>
    private const long PopupWaitMs = 2500;

    /// <summary>Events for one window this close together are one popup (uncloaked, then shown, then moved).</summary>
    private const long SamePopupMs = 300;

    /// <summary>Machitan's own showing of a window it gave back is not hidden again.</summary>
    private const long GivenBackMs = 400;

    /// <summary>The check for a popup on screen that no event announced.</summary>
    private const int CheckMs = 250;

    /// <summary>
    /// A popup window shown this soon after a popup Dynaland showed is most likely Windows showing
    /// the same notification once more at the end of its time (about 6 s later on Sam's PC).
    /// </summary>
    private const long RepeatMs = 12000;

    /// <summary>At most this many "(test)" lines a run (v0.9.3 test build; removed once popups are caught every time).</summary>
    private const int MaxTestLines = 80;

    private static readonly string[] PopupTitles = { "New notification" };
    private static readonly string[] ShellPrograms = { "shellexperiencehost.exe", "shellhost.exe" };

    private static WindowsFlyouts? s_current;

    private readonly DispatcherQueueTimer _timer;
    private readonly DispatcherQueueTimer _check;
    private readonly Dictionary<nint, Hidden> _hidden = new();
    private readonly Dictionary<nint, long> _givenBack = new();

    /// <summary>Windows given back: not hidden again by a move or the check until Windows shows them afresh.</summary>
    private readonly HashSet<nint> _givenBackHold = new();

    /// <summary>When Dynaland last showed the notification of each popup window's popup (not forgotten when Windows takes the popup away).</summary>
    private readonly Dictionary<nint, long> _popupShownAt = new();

    /// <summary>The shell's CoreWindows seen (the popup window among them), checked four times a second.</summary>
    private readonly HashSet<nint> _shellWindows = new();

    /// <summary>A move-and-size hook for each shell program's process (moves of the popup window).</summary>
    private readonly Dictionary<uint, nint> _moveHooks = new();
    private readonly HashSet<string> _titlesNoted = new(StringComparer.Ordinal);
    private nint _showHook;
    private nint _cloakHook;
    private bool _levels;
    private bool _popups;
    private long _levelChangedAt = -LevelBeforeMs;
    private bool _levelNoted;
    private bool _popupNoted;
    private int _testLines;

    internal WindowsFlyouts(DispatcherQueue queue)
    {
        _timer = queue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(100);
        _timer.IsRepeating = true;
        _timer.Tick += (_, _) => GiveBackUnconfirmed();
        _check = queue.CreateTimer();
        _check.Interval = TimeSpan.FromMilliseconds(CheckMs);
        _check.IsRepeating = true;
        _check.Tick += (_, _) => CheckShellWindows();
    }

    private enum Kind
    {
        Level,
        Popup,
    }

    /// <summary>Windows showed its volume and brightness flyout (hidden or not). UI thread.</summary>
    internal event Action? LevelFlyoutShown;

    /// <summary>Windows showed a popup for a new notification, and it was hidden: Dynaland shows it. UI thread.</summary>
    internal event Action? PopupHidden;

    /// <summary>What to hide now: the volume and brightness flyout, and the notification popups. UI thread.</summary>
    internal void Set(bool levels, bool popups)
    {
        if (levels == _levels && popups == _popups)
        {
            return;
        }

        bool popupsStarting = popups && !_popups;
        _levels = levels;
        _popups = popups;
        if (!levels)
        {
            Forget(Kind.Level);
        }

        if (!popups)
        {
            Forget(Kind.Popup);
            _check.Stop();
            UnhookMoves();
            _shellWindows.Clear();
            _givenBackHold.Clear();
        }

        bool hooks = levels || popups;
        if (hooks && _showHook == 0)
        {
            s_current = this;
            _showHook = User32.SetWinEventHook(
                EventObjectShow, EventObjectHide, 0, &OnEvent, 0, 0, User32.WINEVENT_OUTOFCONTEXT | SkipOwnProcess);
            _cloakHook = User32.SetWinEventHook(
                EventObjectCloaked, EventObjectUncloaked, 0, &OnEvent, 0, 0, User32.WINEVENT_OUTOFCONTEXT | SkipOwnProcess);
            if (_showHook == 0 || _cloakHook == 0)
            {
                Log.Warn("Windows' flyouts: Windows refused an event hook; its volume, brightness and notification popups may show beside Dynaland's.");
            }
        }
        else if (!hooks)
        {
            Unhook();
            _timer.Stop();
        }

        if (popupsStarting)
        {
            // The popup window may already exist (and be on screen) from before.
            FindShellWindows();
            _check.Start();
        }
    }

    /// <summary>A volume or brightness change Dynaland shows: Windows' flyout for it stays hidden. UI thread.</summary>
    internal void LevelChanged()
    {
        _levelChangedAt = Environment.TickCount64;
        foreach (Hidden hidden in _hidden.Values)
        {
            if (hidden.Kind == Kind.Level)
            {
                hidden.Confirmed = true;
            }
        }
    }

    /// <summary>Dynaland found and showed the new notification: Windows' popup for it stays hidden. UI thread.</summary>
    internal void PopupShownInDynaland()
    {
        long now = Environment.TickCount64;
        foreach (KeyValuePair<nint, Hidden> pair in _hidden)
        {
            if (pair.Value.Kind == Kind.Popup)
            {
                pair.Value.Confirmed = true;
                _popupShownAt[pair.Key] = now;
            }
        }

        if (_popupShownAt.Count > 20)
        {
            _popupShownAt.Clear();
        }
    }

    public void Dispose()
    {
        LevelFlyoutShown = null;
        PopupHidden = null;
        Set(false, false);
        if (ReferenceEquals(s_current, this))
        {
            s_current = null;
        }
    }

    // ---------------------------------------------------------------- the hooks

    [UnmanagedCallersOnly]
    private static void OnEvent(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            if (hwnd == 0 || idObject != ObjectWindow || idChild != ChildSelf || s_current is not { } flyouts)
            {
                return;
            }

            switch (eventType)
            {
                case EventObjectShow:
                    flyouts.OnShown(hwnd, "shown");
                    break;
                case EventObjectUncloaked:
                    flyouts.OnShown(hwnd, "uncloaked");
                    break;
                case EventObjectLocationChange:
                    flyouts.OnMoved(hwnd);
                    break;
                default:
                    flyouts.OnGone(hwnd);
                    break;
            }
        }
        catch (Exception ex)
        {
            // Nothing may escape into Windows.
            Log.Error("Windows' flyouts: a window event could not be handled.", ex);
        }
    }

    private void OnShown(nint hwnd, string how)
    {
        if (!(_levels || _popups) || User32.GetAncestor(hwnd, User32.GA_ROOT) != hwnd)
        {
            return;
        }

        long now = Environment.TickCount64;
        if (_givenBack.TryGetValue(hwnd, out long givenAt))
        {
            if (now - givenAt < GivenBackMs)
            {
                return;
            }

            _givenBack.Remove(hwnd);
        }

        string windowClass = WindowInfo.GetClassName(hwnd);
        if (_levels && windowClass == LevelClass && IsLevelPlace(hwnd) && ProgramIs(hwnd, "explorer.exe"))
        {
            bool recent = now - _levelChangedAt <= LevelBeforeMs;
            Hide(hwnd, Kind.Level, now, recent, LevelWaitMs, keepReason: null);
            if (!_levelNoted)
            {
                _levelNoted = true;
                Log.Info("Windows' flyouts: Windows' volume and brightness flyout is hidden while Dynaland shows the level.");
            }

            LevelFlyoutShown?.Invoke();
            return;
        }

        if (_popups && windowClass == PopupClass && IsShellProgram(hwnd, out uint processId))
        {
            Remember(hwnd, processId);

            // Windows shows it afresh: a window given back earlier is Machitan's to hide again.
            _givenBackHold.Remove(hwnd);
            TryHidePopup(hwnd, how, byCheck: false);
        }
    }

    /// <summary>A shell window moved or changed size: the popup window on screen without a show event.</summary>
    private void OnMoved(nint hwnd)
    {
        if (_popups && _shellWindows.Contains(hwnd) && !_givenBackHold.Contains(hwnd) && IsOnScreen(hwnd))
        {
            TryHidePopup(hwnd, "moved", byCheck: false);
        }
    }

    /// <summary>Four times a second while popups are hidden: a popup window on screen that no event announced.</summary>
    private void CheckShellWindows()
    {
        if (!_popups)
        {
            _check.Stop();
            return;
        }

        List<nint>? gone = null;
        foreach (nint hwnd in new List<nint>(_shellWindows))
        {
            if (!User32.IsWindow(hwnd))
            {
                (gone ??= new List<nint>()).Add(hwnd);
                continue;
            }

            if (!_givenBackHold.Contains(hwnd) && IsOnScreen(hwnd))
            {
                TryHidePopup(hwnd, "on screen, seen by the check", byCheck: true);
            }
        }

        if (gone is not null)
        {
            foreach (nint hwnd in gone)
            {
                _shellWindows.Remove(hwnd);
                _givenBackHold.Remove(hwnd);
                _hidden.Remove(hwnd);
            }
        }
    }

    /// <summary>A shell CoreWindow shown, moved or on screen: hidden when it is the popup window.</summary>
    private void TryHidePopup(nint hwnd, string how, bool byCheck)
    {
        string title = WindowInfo.GetTitle(hwnd);
        if (Array.IndexOf(PopupTitles, title) < 0)
        {
            // Probably Windows in another language (or another window of the shell): left alone.
            if (title.Length > 0 && IsOnScreen(hwnd) && IsPopupPlace(hwnd) && _titlesNoted.Count < 20 && _titlesNoted.Add(title))
            {
                Log.Info("Windows' flyouts: a shell window titled \"" + title + "\" showed where notification popups show; not hidden (only \"New notification\" is known as one).");
            }

            return;
        }

        long now = Environment.TickCount64;
        bool samePopup = _hidden.TryGetValue(hwnd, out Hidden? known) && now - known.ShownAt < SamePopupMs;
        long sinceShown = _popupShownAt.TryGetValue(hwnd, out long shownAt) ? now - shownAt : long.MaxValue;
        string? keepReason = byCheck
            ? "only the check saw it"
            : sinceShown < RepeatMs
                ? "it showed " + (sinceShown / 1000.0).ToString("0.0", CultureInfo.InvariantCulture) + " s after a popup Dynaland showed: most likely the same notification once more"
                : null;
        string where = Describe(hwnd);
        Hide(hwnd, Kind.Popup, now, confirmed: samePopup && known!.Confirmed, PopupWaitMs, keepReason);
        if (!_popupNoted)
        {
            _popupNoted = true;
            Log.Info("Windows' flyouts: Windows' popups for new notifications are hidden while Dynaland shows them.");
        }

        TestLine("popup window " + how + " (" + where + "); hidden" + (samePopup ? ", same popup as just before."
            : keepReason is null ? ", Dynaland looks for its notification." : ", Dynaland looks for a new notification (" + keepReason + ")."));
        if (!samePopup)
        {
            PopupHidden?.Invoke();
        }
    }

    /// <summary>A window hidden or cloaked: by Machitan (expected), or by its owner (then nothing is given back).</summary>
    private void OnGone(nint hwnd)
    {
        if (!_hidden.TryGetValue(hwnd, out Hidden? hidden))
        {
            // A window given back that Windows takes away itself is its own again.
            _givenBackHold.Remove(hwnd);
            return;
        }

        if (hidden.OwnHides > 0)
        {
            hidden.OwnHides--;
            return;
        }

        // Windows took it away itself (its time ran out): done with it.
        _hidden.Remove(hwnd);
        _givenBackHold.Remove(hwnd);
    }

    /// <param name="keepReason">Why the window stays hidden even if Dynaland finds no new notification for it; null: shown again then.</param>
    private void Hide(nint hwnd, Kind kind, long now, bool confirmed, long waitMs, string? keepReason)
    {
        if (!_hidden.TryGetValue(hwnd, out Hidden? hidden))
        {
            hidden = new Hidden(kind);
            _hidden[hwnd] = hidden;
        }

        hidden.ShownAt = now;
        hidden.Deadline = now + waitMs;
        hidden.Confirmed = confirmed;
        hidden.KeepReason = keepReason;
        if (User32.IsWindowVisible(hwnd))
        {
            hidden.OwnHides++;
        }

        User32.ShowWindowAsync(hwnd, User32.SW_HIDE);
        if (!hidden.Confirmed)
        {
            _timer.Start();
        }
    }

    /// <summary>Every 100 ms while a hide waits: one Dynaland did not confirm in time is shown again.</summary>
    private void GiveBackUnconfirmed()
    {
        long now = Environment.TickCount64;
        List<nint>? done = null;
        bool waiting = false;
        foreach (KeyValuePair<nint, Hidden> pair in _hidden)
        {
            Hidden hidden = pair.Value;
            if (hidden.Confirmed)
            {
                continue;
            }

            if (now < hidden.Deadline)
            {
                waiting = true;
                continue;
            }

            (done ??= new List<nint>()).Add(pair.Key);
            if (hidden.Kind == Kind.Popup && hidden.KeepReason is string reason)
            {
                // Windows' empty popup window, or the same notification once more: showing it
                // again would bring back an empty window, or the notification a second time.
                TestLine("popup window 0x" + pair.Key.ToString("X", CultureInfo.InvariantCulture) + ": Dynaland found no new notification for it; left hidden (" + reason + ").");
                continue;
            }

            if (User32.IsWindow(pair.Key))
            {
                _givenBack[pair.Key] = now;
                _givenBackHold.Add(pair.Key);
                User32.ShowWindowAsync(pair.Key, User32.SW_SHOWNOACTIVATE);
                Log.Info(hidden.Kind == Kind.Level
                    ? "Windows' flyouts: Windows' flyout showed for something Dynaland does not show (no volume or brightness change); shown again."
                    : "Windows' flyouts: Dynaland did not find the new notification in Windows' list; Windows' popup shown again.");
            }
        }

        if (done is not null)
        {
            foreach (nint hwnd in done)
            {
                _hidden.Remove(hwnd);
            }
        }

        if (!waiting)
        {
            _timer.Stop();
        }

        if (_givenBack.Count > 20)
        {
            _givenBack.Clear();
        }
    }

    private void Forget(Kind kind)
    {
        var gone = new List<nint>();
        foreach (KeyValuePair<nint, Hidden> pair in _hidden)
        {
            if (pair.Value.Kind == kind)
            {
                gone.Add(pair.Key);
            }
        }

        foreach (nint hwnd in gone)
        {
            _hidden.Remove(hwnd);
        }
    }

    // ---------------------------------------------------------------- the shell's windows

    /// <summary>The shell's CoreWindows that exist now (the popup window among them, maybe from before Machitan started hiding).</summary>
    private void FindShellWindows()
    {
        foreach (nint hwnd in WindowInfo.EnumerateTopLevel())
        {
            if (WindowInfo.GetClassName(hwnd) == PopupClass && IsShellProgram(hwnd, out uint processId))
            {
                Remember(hwnd, processId);
            }
        }

        TestLine(_shellWindows.Count.ToString(CultureInfo.InvariantCulture) + " window(s) of the shell's kind found when popups started being hidden.");
    }

    /// <summary>A shell CoreWindow to check, and a move hook for its process (one per process).</summary>
    private void Remember(nint hwnd, uint processId)
    {
        if (_shellWindows.Count < 64)
        {
            _shellWindows.Add(hwnd);
        }

        if (processId == 0 || _moveHooks.ContainsKey(processId) || _moveHooks.Count >= 4)
        {
            return;
        }

        nint hook = User32.SetWinEventHook(
            EventObjectLocationChange, EventObjectLocationChange, 0, &OnEvent, processId, 0, User32.WINEVENT_OUTOFCONTEXT);
        _moveHooks[processId] = hook;
        if (hook == 0)
        {
            Log.Warn("Windows' flyouts: Windows refused the hook for the popup window's moves; a popup may show for a moment before it is hidden.");
        }
    }

    private void UnhookMoves()
    {
        foreach (nint hook in _moveHooks.Values)
        {
            if (hook != 0)
            {
                User32.UnhookWinEvent(hook);
            }
        }

        _moveHooks.Clear();
    }

    private static bool IsOnScreen(nint hwnd)
    {
        return User32.IsWindowVisible(hwnd) && !WindowInfo.IsCloaked(hwnd);
    }

    /// <summary>Small (a third of its screen wide, a quarter high at most), in the lower third, centred.</summary>
    private static bool IsLevelPlace(nint hwnd)
    {
        if (!User32.GetWindowRect(hwnd, out RECT rect) || !User32.TryGetMonitorRect(hwnd, out RECT screen))
        {
            return false;
        }

        int middle = (rect.Left + rect.Right) / 2;
        int screenMiddle = (screen.Left + screen.Right) / 2;
        return rect.Width > 0 && rect.Height > 0
            && rect.Width <= screen.Width / 3 && rect.Height <= screen.Height / 4
            && rect.Bottom >= screen.Top + (screen.Height * 2 / 3)
            && Math.Abs(middle - screenMiddle) <= screen.Width / 6;
    }

    /// <summary>Where popups show: at its screen's right edge, in the lower half, at most a third of the screen wide (only for the note about other titles).</summary>
    private static bool IsPopupPlace(nint hwnd)
    {
        if (!User32.GetWindowRect(hwnd, out RECT rect) || !User32.TryGetMonitorRect(hwnd, out RECT screen))
        {
            return false;
        }

        return rect.Width > 0 && rect.Height > 0
            && rect.Width <= screen.Width / 3
            && rect.Right >= screen.Right - (screen.Width / 12)
            && rect.Bottom >= screen.Top + (screen.Height / 2);
    }

    private static bool ProgramIs(nint hwnd, string program)
    {
        User32.GetWindowThreadProcessId(hwnd, out uint processId);
        string? path = WindowInfo.GetProcessPath(processId);
        return path is not null && string.Equals(Path.GetFileName(path), program, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsShellProgram(nint hwnd, out uint processId)
    {
        User32.GetWindowThreadProcessId(hwnd, out processId);
        string? path = WindowInfo.GetProcessPath(processId);
        return path is not null && Array.IndexOf(ShellPrograms, Path.GetFileName(path).ToLowerInvariant()) >= 0;
    }

    /// <summary>For the test lines: the window, its place and size, and whether it is on screen.</summary>
    private static string Describe(nint hwnd)
    {
        string place = User32.GetWindowRect(hwnd, out RECT rect)
            ? rect.Left.ToString(CultureInfo.InvariantCulture) + "," + rect.Top.ToString(CultureInfo.InvariantCulture) + " "
                + rect.Width.ToString(CultureInfo.InvariantCulture) + "x" + rect.Height.ToString(CultureInfo.InvariantCulture)
            : "place unknown";
        return "0x" + hwnd.ToString("X", CultureInfo.InvariantCulture) + ", " + place
            + (User32.IsWindowVisible(hwnd) ? ", visible" : ", hidden")
            + (WindowInfo.IsCloaked(hwnd) ? ", cloaked" : string.Empty);
    }

    private void TestLine(string text)
    {
        if (_testLines < MaxTestLines)
        {
            _testLines++;
            Log.Info("Windows' flyouts (test): " + text);
        }
    }

    private void Unhook()
    {
        if (_showHook != 0)
        {
            User32.UnhookWinEvent(_showHook);
            _showHook = 0;
        }

        if (_cloakHook != 0)
        {
            User32.UnhookWinEvent(_cloakHook);
            _cloakHook = 0;
        }

        UnhookMoves();
    }

    /// <summary>One window Machitan hid: whether Dynaland confirmed it, and by when it must.</summary>
    private sealed class Hidden
    {
        internal Hidden(Kind kind)
        {
            Kind = kind;
        }

        internal Kind Kind { get; }

        internal long ShownAt { get; set; }

        internal long Deadline { get; set; }

        internal bool Confirmed { get; set; }

        /// <summary>Why it stays hidden even when unconfirmed (only the check saw it, or the same notification once more); null: shown again then.</summary>
        internal string? KeepReason { get; set; }

        /// <summary>Hide events Machitan's own hides will cause, not to be taken as Windows' own.</summary>
        internal int OwnHides { get; set; }
    }
}
