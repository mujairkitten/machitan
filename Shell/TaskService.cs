using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Machitan.Core;
using Machitan.Interop;
using Microsoft.UI.Dispatching;

namespace Machitan.Shell;

/// <summary>
/// The list of open apps. It is rebuilt from a full window enumeration each time the shell
/// hook reports a change (window created, destroyed, activated, retitled). If the shell hook
/// cannot be registered, the bar's 2-second timer polls instead.
/// Lives on the UI thread.
/// </summary>
internal sealed class TaskService
{
    // Shell hook codes that say nothing about the task list.
    private const long HSHELL_GETMINRECT = 5;
    private const long HSHELL_LANGUAGE = 8;
    private const long HSHELL_APPCOMMAND = 12;
    private const long HSHELL_WINDOWACTIVATED = 4;

    private readonly DispatcherQueueTimer _debounce;
    private readonly List<string> _order = new();
    private readonly Dictionary<string, string> _displayNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<nint, ProcessIdentity> _framePaths = new();
    private readonly Dictionary<nint, (WindowAppInfo Info, long ReadAt)> _windowAppIds = new();
    private readonly HashSet<string> _ownIdsNoted = new(StringComparer.OrdinalIgnoreCase);

    private IReadOnlyList<TaskGroup> _groups = Array.Empty<TaskGroup>();
    private string _signature = string.Empty;
    private nint _hookWindow;

    /// <summary>The one service the foreground event reaches (the callback is static).</summary>
    private static TaskService? s_foregroundListener;

    private nint _foregroundHook;

    /// <summary>The last window in front that was not the bar itself (an app, or the desktop).</summary>
    private nint _lastOutsideForeground;

    internal TaskService(DispatcherQueue queue)
    {
        _debounce = queue.CreateTimer();
        _debounce.Interval = TimeSpan.FromMilliseconds(60);
        _debounce.IsRepeating = false;
        _debounce.Tick += (_, _) => Refresh();
    }

    /// <summary>A window's app ID is read again after this long (apps may set it a moment after the window opens).</summary>
    private const long AppIdRereadMs = 3000;

    /// <summary>Raised on the UI thread when the list changed.</summary>
    internal event Action? Changed;

    /// <summary>The app IDs Start's shortcuts to a program carry (from the app list), or null when unknown.</summary>
    internal Func<string, IReadOnlySet<string>?>? ShortcutAppIds { get; set; }

    /// <summary>Apps in first-seen order.</summary>
    internal IReadOnlyList<TaskGroup> Groups => _groups;

    /// <summary>The registered "SHELLHOOK" message id, or 0 before the hook is attached.</summary>
    internal uint ShellHookMessage { get; private set; }

    internal bool HookActive { get; private set; }

    /// <summary>True when the shell hook is not delivering and a timer must poll instead.</summary>
    internal bool UsesPolling => !HookActive;

    /// <summary>Registers the window for shell hook messages. Failure switches to polling.</summary>
    internal void AttachShellHook(nint hwnd, bool messagesCanArrive)
    {
        try
        {
            _hookWindow = hwnd;
            ShellHookMessage = User32.RegisterWindowMessage("SHELLHOOK");
            HookActive = messagesCanArrive && ShellHookMessage != 0 && User32.RegisterShellHookWindow(hwnd);
        }
        catch (Exception ex)
        {
            HookActive = false;
            Log.Error("Shell hook registration failed.", ex);
        }

        Log.Info(HookActive
            ? "Shell hook registered; the task list follows window events."
            : "Shell hook NOT registered; the task list is polled every 2 seconds instead.");
        StartForegroundTracking();
    }

    /// <summary>
    /// Follows every change of the window in front (Windows' foreground event), so a click on
    /// the bar knows which window was in front just before the bar took the focus. The bar
    /// shares its thread with Machitan's Settings window; clicking the bar while Settings is in
    /// front moves the focus to the bar, so at the time of the click Windows reports the bar.
    /// </summary>
    private unsafe void StartForegroundTracking()
    {
        if (_foregroundHook != 0)
        {
            return;
        }

        try
        {
            s_foregroundListener = this;
            _foregroundHook = User32.SetWinEventHook(
                User32.EVENT_SYSTEM_FOREGROUND,
                User32.EVENT_SYSTEM_FOREGROUND,
                0,
                &OnForegroundEvent,
                0,
                0,
                User32.WINEVENT_OUTOFCONTEXT);
            NoteForeground(User32.GetForegroundWindow());
            if (_foregroundHook == 0)
            {
                Log.Warn("Windows did not accept the foreground event hook; the window in front is checked on bar refreshes only.");
            }
        }
        catch (Exception ex)
        {
            Log.Warn("The foreground event hook could not be set.", ex);
        }
    }

    [UnmanagedCallersOnly]
    private static void OnForegroundEvent(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        try
        {
            s_foregroundListener?.NoteForeground(hwnd);
        }
        catch (Exception)
        {
            // Never let an exception cross back into Windows.
        }
    }

    private void NoteForeground(nint hwnd)
    {
        if (hwnd != 0 && hwnd != _hookWindow)
        {
            _lastOutsideForeground = hwnd;
        }
    }

    internal void DetachShellHook()
    {
        try
        {
            _debounce.Stop();
            if (_foregroundHook != 0)
            {
                User32.UnhookWinEvent(_foregroundHook);
                _foregroundHook = 0;
            }

            if (ReferenceEquals(s_foregroundListener, this))
            {
                s_foregroundListener = null;
            }

            if (HookActive && _hookWindow != 0)
            {
                User32.DeregisterShellHookWindow(_hookWindow);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Shell hook could not be removed cleanly.", ex);
        }

        HookActive = false;
    }

    /// <summary>Called by the bar window for every shell hook message.</summary>
    internal void OnShellHookMessage(nint wParam, nint lParam)
    {
        long code = (long)wParam & 0x7FFF;
        if (code is HSHELL_GETMINRECT or HSHELL_LANGUAGE or HSHELL_APPCOMMAND)
        {
            return;
        }

        if (code == HSHELL_WINDOWACTIVATED)
        {
            NoteForeground(lParam);
        }

        // Events come in bursts; one refresh shortly after the last one is enough.
        _debounce.Stop();
        _debounce.Start();
    }

    /// <summary>Brings the app's front-most window forward, or minimises it if it already is in front.</summary>
    internal void ActivateOrMinimize(string groupKey)
    {
        try
        {
            TaskGroup? group = null;
            foreach (TaskGroup candidate in _groups)
            {
                if (string.Equals(candidate.Key, groupKey, StringComparison.OrdinalIgnoreCase))
                {
                    group = candidate;
                    break;
                }
            }

            if (group is null || group.Windows.Count == 0)
            {
                return;
            }

            nint target = group.Windows[0].Handle;
            nint inFront = EffectiveForeground();
            nint frontOfGroup = 0;
            foreach (TaskWindow window in group.Windows)
            {
                if (window.Handle == inFront)
                {
                    frontOfGroup = inFront;
                    break;
                }
            }

            bool minimise = frontOfGroup != 0 && !User32.IsIconic(frontOfGroup);
            Log.Info("Bar: " + group.DisplayName + (minimise ? " minimised (it was in front)." : " brought to the front (in front was " + Describe(inFront) + ")."));
            if (minimise)
            {
                User32.ShowWindow(frontOfGroup, User32.SW_MINIMIZE);
            }
            else
            {
                // Restores a minimised window first, then brings it to the front.
                WindowActivator.BringToFront(target);
            }

            _debounce.Stop();
            _debounce.Start();
        }
        catch (Exception ex)
        {
            Log.Error("Switching to an app failed.", ex);
        }
    }

    /// <summary>
    /// The window that counts as in front. While the bar itself has the focus (a click on it
    /// while Machitan's Settings was in front), that is the window that was in front just
    /// before the bar, unless it has been minimised since; otherwise it is the window Windows
    /// reports.
    /// </summary>
    internal nint EffectiveForeground()
    {
        nint foreground = User32.GetForegroundWindow();
        if ((foreground == 0 || foreground == _hookWindow)
            && _lastOutsideForeground != 0
            && User32.IsWindow(_lastOutsideForeground)
            && !User32.IsIconic(_lastOutsideForeground))
        {
            return _lastOutsideForeground;
        }

        return foreground;
    }

    /// <summary>
    /// The window that was in front before the bar took the focus (for giving it back when the
    /// bar's search box is left with Esc), or 0 when it is gone.
    /// </summary>
    internal nint PreviousForeground => _lastOutsideForeground != 0 && User32.IsWindow(_lastOutsideForeground) ? _lastOutsideForeground : 0;

    /// <summary>For the log: the window's class and program, or "nothing".</summary>
    private static string Describe(nint hwnd)
    {
        if (hwnd == 0)
        {
            return "nothing";
        }

        User32.GetWindowThreadProcessId(hwnd, out uint processId);
        string? path = WindowInfo.GetProcessPath(processId);
        return WindowInfo.GetClassName(hwnd) + " of " + (path is null ? "an unknown app" : System.IO.Path.GetFileName(path));
    }

    /// <summary>Asks every window of the app to close, the same way its own close button would.</summary>
    internal void CloseWindows(TaskGroup group)
    {
        foreach (TaskWindow window in group.Windows)
        {
            User32.PostMessageW(window.Handle, User32.WM_CLOSE, 0, 0);
        }
    }

    /// <summary>
    /// End task: ends the app's processes at once, without asking the app (as Task Manager's
    /// End task). The processes are those owning the app's windows; for a store app, the
    /// process behind the window's content (its frame belongs to ApplicationFrameHost, which
    /// hosts every store app); for any packaged app, every process of its package. Never
    /// Explorer, ApplicationFrameHost or Machitan itself. Returns how many processes ended.
    /// </summary>
    internal int EndTask(TaskGroup group)
    {
        var targets = new HashSet<uint>();
        foreach (TaskWindow window in group.Windows)
        {
            if (WindowInfo.GetClassName(window.Handle) == WindowFilter.AppFrameClass)
            {
                nint core = User32.FindWindowEx(window.Handle, 0, WindowFilter.CoreWindowClass, null);
                if (core != 0)
                {
                    User32.GetWindowThreadProcessId(core, out uint coreProcess);
                    targets.Add(coreProcess);
                }
            }
            else
            {
                targets.Add(window.ProcessId);
            }
        }

        if (group.PackageFullName is string package)
        {
            foreach (Process process in Process.GetProcesses())
            {
                using (process)
                {
                    uint id = (uint)process.Id;
                    if (string.Equals(WindowInfo.GetProcessIdentity(id).PackageFullName, package, StringComparison.OrdinalIgnoreCase))
                    {
                        targets.Add(id);
                    }
                }
            }
        }

        int ended = 0;
        var refused = new List<string>();
        foreach (uint id in targets)
        {
            string name = ProcessName(id);
            if (id <= 4 || id == (uint)Environment.ProcessId
                || name.Equals("explorer", StringComparison.OrdinalIgnoreCase)
                || name.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            nint handle = Kernel32.OpenProcess(Kernel32.PROCESS_TERMINATE, false, id);
            bool done = handle != 0 && Kernel32.TerminateProcess(handle, 1);
            if (handle != 0)
            {
                Kernel32.CloseHandle(handle);
            }

            if (done)
            {
                ended++;
            }
            else
            {
                refused.Add(name + " (" + id.ToString(CultureInfo.InvariantCulture) + ")");
            }
        }

        Log.Info("End task: " + group.DisplayName + ": ended " + ended.ToString(CultureInfo.InvariantCulture) + " process(es)"
            + (refused.Count > 0 ? "; Windows refused " + string.Join(", ", refused) + " (an app running as administrator cannot be ended from here)." : "."));
        return ended;
    }

    private static string ProcessName(uint id)
    {
        try
        {
            using Process process = Process.GetProcessById((int)id);
            return process.ProcessName;
        }
        catch (Exception)
        {
            return "process";
        }
    }

    /// <summary>Rebuilds the list now. Raises <see cref="Changed"/> only if something differs.</summary>
    internal void Refresh()
    {
        try
        {
            List<nint> handles = WindowInfo.EnumerateTopLevel();
            NoteForeground(User32.GetForegroundWindow());
            nint foreground = EffectiveForeground();

            var identityByProcess = new Dictionary<uint, ProcessIdentity>();
            var windowsByKey = new Dictionary<string, List<TaskWindow>>(StringComparer.OrdinalIgnoreCase);
            var identityByKey = new Dictionary<string, ProcessIdentity>(StringComparer.OrdinalIgnoreCase);
            var keysInOrderSeen = new List<string>();
            var liveFrames = new HashSet<nint>();
            var liveWindows = new HashSet<nint>();
            var ownAppIdByKey = new Dictionary<string, WindowAppInfo>(StringComparer.OrdinalIgnoreCase);
            long now = Environment.TickCount64;

            foreach (nint hwnd in handles)
            {
                if (!WindowFilter.IsTaskWindow(hwnd, out string className))
                {
                    continue;
                }

                User32.GetWindowThreadProcessId(hwnd, out uint processId);
                string title = WindowInfo.GetTitle(hwnd);

                ProcessIdentity identity;
                string key;
                if (className == WindowFilter.AppFrameClass)
                {
                    liveFrames.Add(hwnd);
                    identity = ResolveStoreAppIdentity(hwnd, processId, identityByProcess);
                    key = identity.Path ?? "store:" + title;
                }
                else
                {
                    identity = GetIdentity(processId, identityByProcess);
                    key = identity.Path ?? "pid:" + processId.ToString(CultureInfo.InvariantCulture);

                    // Windows with an app ID of their own get their own button, as on Windows' taskbar.
                    liveWindows.Add(hwnd);
                    WindowAppInfo own = WindowAppIdOf(hwnd, now);
                    if (identity.Path is string program && IsOwnIdentity(program, own.AppId))
                    {
                        key = program + "|" + own.AppId;
                        ownAppIdByKey.TryAdd(key, own);
                    }
                }

                if (!windowsByKey.TryGetValue(key, out List<TaskWindow>? windows))
                {
                    windows = new List<TaskWindow>();
                    windowsByKey[key] = windows;
                    identityByKey[key] = identity;
                    keysInOrderSeen.Add(key);
                }

                windows.Add(new TaskWindow(hwnd, title, processId, User32.IsIconic(hwnd)));
            }

            ForgetClosedFrames(liveFrames);
            if (_windowAppIds.Count > liveWindows.Count)
            {
                var gone = new List<nint>();
                foreach (nint window in _windowAppIds.Keys)
                {
                    if (!liveWindows.Contains(window))
                    {
                        gone.Add(window);
                    }
                }

                foreach (nint window in gone)
                {
                    _windowAppIds.Remove(window);
                }
            }

            // Keep apps where they are; new apps go to the end.
            _order.RemoveAll(key => !windowsByKey.ContainsKey(key));
            foreach (string key in keysInOrderSeen)
            {
                if (!_order.Contains(key, StringComparer.OrdinalIgnoreCase))
                {
                    _order.Add(key);
                }
            }

            var groups = new List<TaskGroup>(_order.Count);
            var signature = new StringBuilder();
            foreach (string key in _order)
            {
                List<TaskWindow> windows = windowsByKey[key];
                ProcessIdentity identity = identityByKey[key];
                string? path = identity.Path;

                bool focused = false;
                foreach (TaskWindow window in windows)
                {
                    if (window.Handle == foreground)
                    {
                        focused = true;
                    }

                    signature.Append((long)window.Handle).Append(window.IsMinimized ? 'm' : 'n').Append(window.Title).Append('\u001f');
                }

                string displayName = path is not null ? GetDisplayName(path) : windows[0].Title;
                ownAppIdByKey.TryGetValue(key, out WindowAppInfo? own);
                if (own?.DisplayName is string ownName)
                {
                    displayName = ownName;
                }

                groups.Add(new TaskGroup(key, displayName, path, identity.PackageFullName, windows, focused, own?.AppId, own?.IconResource));
                signature.Append(key).Append(focused ? '*' : '-').Append(displayName).Append('\u001e');
            }

            string newSignature = signature.ToString();
            if (newSignature == _signature)
            {
                return;
            }

            _signature = newSignature;
            _groups = groups;
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Error("The task list could not be refreshed.", ex);
        }
    }

    /// <summary>The window's own app ID, name and icon (read again every few seconds).</summary>
    private WindowAppInfo WindowAppIdOf(nint hwnd, long now)
    {
        if (_windowAppIds.TryGetValue(hwnd, out (WindowAppInfo Info, long ReadAt) known) && now - known.ReadAt < AppIdRereadMs)
        {
            return known.Info;
        }

        WindowAppInfo info;
        try
        {
            info = WindowAppId.Read(hwnd);
        }
        catch (Exception ex)
        {
            Log.Warn("Taskbar: a window's app ID could not be read.", ex);
            info = new WindowAppInfo(null, null, null);
        }

        _windowAppIds[hwnd] = (info, now);
        return info;
    }

    /// <summary>
    /// True when a window's app ID is not the program's usual one, so Windows' taskbar shows it
    /// as its own button. The usual one is what Start's shortcuts to the program carry; when
    /// they carry none, an ID with a ';' in it is an extra one (Firefox's private windows add
    /// ";PrivateBrowsingAUMID" to the usual ID).
    /// </summary>
    private bool IsOwnIdentity(string program, string? appId)
    {
        if (appId is null)
        {
            return false;
        }

        IReadOnlySet<string>? usual = ShortcutAppIds?.Invoke(program);
        bool own = usual is { Count: > 0 } ? !usual.Contains(appId) : appId.Contains(';');
        if (own && _ownIdsNoted.Add(program + "|" + appId))
        {
            Log.Info("Taskbar: windows of " + System.IO.Path.GetFileName(program) + " with their own app ID (" + appId + ") get their own button, as on Windows' taskbar.");
        }

        return own;
    }

    private static ProcessIdentity GetIdentity(uint processId, Dictionary<uint, ProcessIdentity> cache)
    {
        if (!cache.TryGetValue(processId, out ProcessIdentity identity))
        {
            identity = WindowInfo.GetProcessIdentity(processId);
            cache[processId] = identity;
        }

        return identity;
    }

    /// <summary>
    /// Store apps all sit inside frame windows owned by ApplicationFrameHost.exe. The real app
    /// is the process behind the CoreWindow inside the frame. Once found it is remembered for
    /// that frame, because the CoreWindow leaves the frame while the app is minimised.
    /// </summary>
    private ProcessIdentity ResolveStoreAppIdentity(nint frame, uint frameProcessId, Dictionary<uint, ProcessIdentity> cache)
    {
        if (_framePaths.TryGetValue(frame, out ProcessIdentity known))
        {
            return known;
        }

        nint core = User32.FindWindowEx(frame, 0, WindowFilter.CoreWindowClass, null);
        if (core == 0)
        {
            return default;
        }

        User32.GetWindowThreadProcessId(core, out uint coreProcessId);
        if (coreProcessId == 0 || coreProcessId == frameProcessId)
        {
            return default;
        }

        ProcessIdentity identity = GetIdentity(coreProcessId, cache);
        if (identity.Path is not null)
        {
            _framePaths[frame] = identity;
        }

        return identity;
    }

    private void ForgetClosedFrames(HashSet<nint> liveFrames)
    {
        if (_framePaths.Count == 0)
        {
            return;
        }

        List<nint>? gone = null;
        foreach (nint frame in _framePaths.Keys)
        {
            if (!liveFrames.Contains(frame))
            {
                gone ??= new List<nint>();
                gone.Add(frame);
            }
        }

        if (gone is null)
        {
            return;
        }

        foreach (nint frame in gone)
        {
            _framePaths.Remove(frame);
        }
    }

    /// <summary>The app's own description from its executable, else the file name. Cached.</summary>
    private string GetDisplayName(string path)
    {
        if (_displayNames.TryGetValue(path, out string? name))
        {
            return name;
        }

        try
        {
            name = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
        }
        catch (Exception)
        {
            name = null;
        }

        if (string.IsNullOrEmpty(name))
        {
            name = Path.GetFileNameWithoutExtension(path);
        }

        if (string.IsNullOrEmpty(name))
        {
            name = path;
        }

        _displayNames[path] = name;
        return name;
    }
}
