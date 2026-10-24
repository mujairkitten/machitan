using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace Machitan.Native;

/// <summary>
/// SPIKE (Priority 4: system tray investigation). Research only — no tray UI.
///
/// When Shell_NotifyIcon adds a tray icon, Windows finds the first top-level
/// window of class "Shell_TrayWnd" and sends it a WM_COPYDATA message with the
/// icon data. This class creates such a window (hidden, kept above Explorer's
/// own), WATCHES those messages, writes what it sees to the diagnostics log,
/// and FORWARDS every message to Explorer's real tray window, so Explorer's
/// tray keeps its full state. If Machitan dies, nothing is lost.
///
/// From i8 it also feeds <see cref="TrayIconStore"/>, which the bar's tray
/// chevron pop-up reads (display only; clicks come in i9).
///
/// Opt-in: does nothing unless the flag file
///   %AppData%\Machitan\tray-spike.flag
/// exists. Delete the file to switch the spike off.
///
/// Privacy: the log holds process names and icon ids only — never tooltip
/// text, which can contain private titles.
/// </summary>
internal static class TrayListener
{
    private const string TrayClass = "Shell_TrayWnd";
    private const string NotifyClass = "TrayNotifyWnd";

    private const uint WM_COPYDATA = 0x004A;
    private const uint WM_COMMAND = 0x0111;
    private const uint WM_USER = 0x0400;
    private const uint FirstRegisteredMessage = 0xC000;

    // Appbar messages that carry their data in shared memory.
    private const int ABM_QUERYPOS = 2;
    private const int ABM_SETPOS = 3;
    private const int ABM_GETTASKBARPOS = 5;
    private const int AppBarMsgSize = 64;   // APPBARMSGDATAV3 on 64-bit
    private const int AppBarDataSize = 40;  // APPBARDATAV2 on 64-bit

    private const int MaxLoggedEvents = 400;
    private const uint ForwardTimeoutMs = 2000;

    private static readonly string FlagPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Machitan", "tray-spike.flag");

    // The OS calls these delegates: keep them alive for the process lifetime.
    private static TrayNative.WndProcDelegate? _proc;
    private static TrayNative.WndProcDelegate? _childProc;

    private static IntPtr _hwnd;
    private static IntPtr _hwndNotify;
    private static IntPtr _hwndFwd;
    private static DispatcherQueueTimer? _timer;
    private static bool _started;
    private static int _tick;
    private static DateTime _suppressUntil = DateTime.MinValue;

    // Everything below is touched on the UI thread only (the window's thread).
    private static int _adds, _modifies, _deletes, _otherIcon;
    private static int _appBar, _appBarRemarshaled, _rectQueries;
    private static int _commandFwd, _privateFwd, _badData, _forwardFailed, _raised;
    private static int _eventsLogged;
    private static int _iconOk, _iconFail;
    private static int _onTopLogged, _appBarAtLastCheck;
    private static int _raisedAtLastTick, _resyncs;
    private static bool _resyncPending;
    private static DateTime _lastBroadcast = DateTime.MinValue;
    private static string _lastSummary = "";
    private static readonly HashSet<string> _modifySeen = new();
    private static readonly HashSet<string> _appsSeen = new();
    private static readonly HashSet<uint> _privateIds = new();
    private static readonly HashSet<long> _unknownCopyData = new();
    private static readonly Dictionary<int, string> _procNames = new();
    private static readonly Dictionary<uint, string> _elevation = new();

    /// <summary>
    /// True for a few seconds after the spike broadcast "TaskbarCreated" itself,
    /// so MainWindow does not mistake it for an Explorer restart (that path
    /// re-hides the taskbar and would overwrite the saved Explorer state).
    /// </summary>
    public static bool IsSuppressingTaskbarCreated => DateTime.UtcNow < _suppressUntil;

    /// <summary>True while the listener window exists (the opt-in flag file was found and start-up worked).</summary>
    public static bool IsActive => _started && _hwnd != IntPtr.Zero;

    public static void StartIfEnabled(DispatcherQueue queue)
    {
        if (_started || !File.Exists(FlagPath))
            return;

        try
        {
            Start(queue);
        }
        catch (Exception ex)
        {
            // Failure-latched like the segment windows: log, tear down, carry on.
            DiagnosticsLog.WriteException("TrayListener start", ex);
            Stop();
        }
    }

    public static void Stop()
    {
        try
        {
            if (_timer != null)
            {
                _timer.Stop();
                _timer = null;
            }

            if (_started)
                LogSummary(true);

            TrayIconStore.Clear();

            if (_hwnd != IntPtr.Zero)
                TrayNative.DestroyWindow(_hwnd); // also destroys the child

            IntPtr hInst = TrayNative.GetModuleHandleW(null);
            TrayNative.UnregisterClassW(NotifyClass, hInst);
            TrayNative.UnregisterClassW(TrayClass, hInst);
        }
        catch (Exception ex)
        {
            DiagnosticsLog.WriteException("TrayListener stop", ex);
        }
        finally
        {
            _hwnd = IntPtr.Zero;
            _hwndNotify = IntPtr.Zero;
            _hwndFwd = IntPtr.Zero;
            _started = false;
        }
    }

    private static void Start(DispatcherQueue queue)
    {
        DiagnosticsLog.Write("TrayListener: spike ON (flag file found)");

        // Mirror Explorer's tray rectangle so apps that read it still get the
        // same numbers they got before.
        int x = 0, y = 0, w = TrayNative.GetSystemMetrics(0), h = 48;
        IntPtr explorerTray = FindExplorerTray();
        if (explorerTray != IntPtr.Zero
            && TrayNative.GetWindowRect(explorerTray, out TrayNative.TrayRect r)
            && r.Right > r.Left && r.Bottom > r.Top)
        {
            x = r.Left;
            y = r.Top;
            w = r.Right - r.Left;
            h = r.Bottom - r.Top;
        }

        IntPtr hInst = TrayNative.GetModuleHandleW(null);
        _proc = WndProc;
        _childProc = ChildProc;

        RegisterClass(TrayClass, _proc, hInst);
        RegisterClass(NotifyClass, _childProc, hInst);

        const uint WS_POPUP = 0x80000000;
        const uint WS_CHILD = 0x40000000;
        const uint WS_CLIPCHILDREN = 0x02000000;
        const uint WS_CLIPSIBLINGS = 0x04000000;
        const uint WS_EX_TOPMOST = 0x00000008;
        const uint WS_EX_TOOLWINDOW = 0x00000080;
        const uint WS_EX_NOACTIVATE = 0x08000000;

        // Never shown: no WS_VISIBLE and no ShowWindow call.
        _hwnd = TrayNative.CreateWindowExW(
            WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            TrayClass, null,
            WS_POPUP | WS_CLIPCHILDREN | WS_CLIPSIBLINGS,
            x, y, w, h, IntPtr.Zero, IntPtr.Zero, hInst, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowEx({TrayClass}) failed: {Marshal.GetLastWin32Error()}");

        _hwndNotify = TrayNative.CreateWindowExW(
            0, NotifyClass, null,
            WS_CHILD | WS_CLIPCHILDREN | WS_CLIPSIBLINGS,
            0, 0, w, h, _hwnd, IntPtr.Zero, hInst, IntPtr.Zero);
        if (_hwndNotify == IntPtr.Zero)
            DiagnosticsLog.Write($"TrayListener: child {NotifyClass} not created ({Marshal.GetLastWin32Error()}); continuing without it");

        RaiseToTop();
        _started = true;

        // Slow (2 s) check that we are still the first Shell_TrayWnd. One tick
        // later, ask every app to send its icons again (see OnTick).
        _timer = queue.CreateTimer();
        _timer.Interval = TimeSpan.FromSeconds(2);
        _timer.Tick += (_, _) => OnTick();
        _timer.Start();

        DiagnosticsLog.Write($"TrayListener: listening (window 0x{_hwnd.ToInt64():X}, rect {x},{y} {w}x{h}, explorer tray 0x{explorerTray.ToInt64():X})");
    }

    private static void RegisterClass(string name, TrayNative.WndProcDelegate proc, IntPtr hInst)
    {
        var wc = new TrayNative.WNDCLASS
        {
            style = 0,
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc),
            hInstance = hInst,
            lpszClassName = name,
        };

        if (TrayNative.RegisterClassW(ref wc) == 0)
            throw new InvalidOperationException($"RegisterClass({name}) failed: {Marshal.GetLastWin32Error()}");
    }

    // ---- Timer ------------------------------------------------------------

    private static void OnTick()
    {
        try
        {
            _tick++;
            EnsureOnTop();
            TrayIconStore.Prune(owner => TrayNative.IsWindow(new IntPtr(unchecked((int)owner))));
            TrayIconStore.FlushPending();
            ResyncAfterTopSpotLoss();
            _appBarAtLastCheck = _appBar;

            // Tick 1 (~2 s after start): apps normally re-send their icons
            // when they see "TaskbarCreated". Without this the spike would
            // only see icons that change after it started.
            if (_tick == 1)
                BroadcastTaskbarCreated();

            if (_tick % 8 == 0) // ~16 s
                LogSummary(false);
        }
        catch (Exception ex)
        {
            DiagnosticsLog.WriteException("TrayListener tick", ex);
        }
    }

    // While Explorer's window was first, apps' icon messages went to Explorer
    // and we missed them. Once we are back on top (a tick with no new loss),
    // ask apps to send their icons again so the list catches up. At most once
    // every 30 seconds.
    private static void ResyncAfterTopSpotLoss()
    {
        if (_raised != _raisedAtLastTick)
        {
            _raisedAtLastTick = _raised;
            _resyncPending = true;
            return;
        }

        if (_resyncPending && (DateTime.UtcNow - _lastBroadcast).TotalSeconds >= 30)
        {
            _resyncPending = false;
            _resyncs++;
            BroadcastTaskbarCreated();
        }
    }

    private static void EnsureOnTop()
    {
        if (_hwnd == IntPtr.Zero)
            return;

        IntPtr top = TrayNative.FindWindowW(TrayClass, null);
        if (top != _hwnd)
        {
            LogWhoIsOnTop(top);
            RaiseToTop();
            _raised++;
        }
    }

    // i7: find out who takes the first Shell_TrayWnd spot back from us.
    private static void LogWhoIsOnTop(IntPtr top)
    {
        if (_onTopLogged >= 20)
            return;
        _onTopLogged++;

        string owner = "none";
        uint pid = 0;
        bool visible = false;
        if (top != IntPtr.Zero)
        {
            TrayNative.GetWindowThreadProcessId(top, out pid);
            visible = TrayNative.IsWindowVisible(top);
            try
            {
                using var proc = Process.GetProcessById((int)pid);
                owner = proc.ProcessName;
            }
            catch
            {
                owner = "?";
            }
        }

        DiagnosticsLog.Write(
            $"TrayListener: first Shell_TrayWnd was 0x{top.ToInt64():X} owner={owner} pid={pid} visible={visible}; " +
            $"appbar messages since last check={_appBar - _appBarAtLastCheck}");
    }

    private static void RaiseToTop()
    {
        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOMOVE = 0x0002;
        const uint SWP_NOACTIVATE = 0x0010;
        TrayNative.SetWindowPos(_hwnd, TrayNative.HwndTopmost, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    private static void BroadcastTaskbarCreated()
    {
        uint msg = TrayNative.RegisterWindowMessageW("TaskbarCreated");
        if (msg == 0)
            return;

        _lastBroadcast = DateTime.UtcNow;
        _suppressUntil = DateTime.UtcNow.AddSeconds(6);
        TrayNative.SendNotifyMessageW(new IntPtr(0xFFFF), msg, IntPtr.Zero, IntPtr.Zero);
        DiagnosticsLog.Write("TrayListener: broadcast TaskbarCreated (apps should re-send icons)");
    }

    private static void LogSummary(bool force)
    {
        string summary =
            $"icons add={_adds} modify={_modifies} delete={_deletes} other={_otherIcon}; " +
            $"appbar={_appBar} (re-marshaled {_appBarRemarshaled}); rect-queries={_rectQueries}; " +
            $"command={_commandFwd} private={_privateFwd}; bad-data={_badData}; " +
            $"forward-failed={_forwardFailed}; raised-to-top={_raised}; resyncs={_resyncs}; icon-read ok={_iconOk} failed={_iconFail}; " +
            $"picture-time=[{TrayIconStore.TimingSummary}]; " +
            $"apps=[{string.Join(", ", _appsSeen)}]";

        if (!force && summary == _lastSummary)
            return;

        _lastSummary = summary;
        DiagnosticsLog.Write("TrayListener summary: " + summary);
    }

    // ---- Window procedures ------------------------------------------------

    private static IntPtr ChildProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        => TrayNative.DefWindowProcW(hWnd, msg, wParam, lParam);

    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (msg == WM_COPYDATA)
                return HandleCopyData(wParam, lParam);

            if (msg == WM_COMMAND)
            {
                _commandFwd++;
                return ForwardSend(msg, wParam, lParam);
            }

            // Private messages for the tray window class. Registered messages
            // (0xC000 and up, e.g. TaskbarCreated) are broadcasts: not ours
            // to forward.
            if (msg >= WM_USER && msg < FirstRegisteredMessage)
            {
                _privateFwd++;
                if (_privateIds.Count < 32 && _privateIds.Add(msg))
                    DiagnosticsLog.Write($"TrayListener: private message 0x{msg:X} forwarded to Explorer");

                return ForwardPrivate(msg, wParam, lParam);
            }
        }
        catch (Exception ex)
        {
            DiagnosticsLog.WriteException("TrayListener WndProc", ex);
        }

        return TrayNative.DefWindowProcW(hWnd, msg, wParam, lParam);
    }

    private static IntPtr HandleCopyData(IntPtr wParam, IntPtr lParam)
    {
        if (lParam == IntPtr.Zero)
            return IntPtr.Zero;

        var cds = Marshal.PtrToStructure<TrayNative.TrayCopyData>(lParam);
        long kind = cds.dwData.ToInt64();

        switch (kind)
        {
            case 0: // appbar registration / position
                return HandleAppBar(wParam, lParam, cds);

            case 1: // tray icon add / modify / delete / set version
                ObserveIcon(cds);
                break;

            case 3: // "where is this icon?" queries (Shell_NotifyIconGetRect)
                _rectQueries++;
                break;

            default:
                if (_unknownCopyData.Count < 16 && _unknownCopyData.Add(kind))
                    DiagnosticsLog.Write($"TrayListener: unknown WM_COPYDATA kind {kind} (size {cds.cbData}) forwarded");
                break;
        }

        return ForwardSend(WM_COPYDATA, wParam, lParam);
    }

    // ---- Tray icon messages (log only) ------------------------------------

    // SHELLTRAYDATA layout (64-bit): dwUnknown@0, dwMessage@4, then a
    // NOTIFYICONDATA with 32-bit handles: cbSize@8, hWnd@12, uID@16,
    // uFlags@20, uCallbackMessage@24, hIcon@28, szTip@32 (128 wchars),
    // dwState@288, dwStateMask@292, szInfo@296 (256 wchars), uVersion@808,
    // szInfoTitle@812 (64 wchars), dwInfoFlags@940, guidItem@944 (16 bytes),
    // hBalloonIcon@960. Every read is bounds-checked against cbData.
    private static void ObserveIcon(TrayNative.TrayCopyData cds)
    {
        int cb = cds.cbData;
        IntPtr p = cds.lpData;
        if (p == IntPtr.Zero || cb < 24)
        {
            _badData++;
            return;
        }

        // i8: feed the icon list first. This must never stop the message from
        // being forwarded to Explorer, so any failure is only logged.
        try
        {
            TrayIconStore.ApplyMessage(p, cb, NameOf);
        }
        catch (Exception ex)
        {
            DiagnosticsLog.WriteException("TrayIconStore", ex);
        }

        uint nim = U32(p, cb, 4);
        switch (nim)
        {
            case 0: _adds++; break;
            case 1: _modifies++; break;
            case 2: _deletes++; break;
            default: _otherIcon++; break;
        }

        uint hwndRaw = U32(p, cb, 12);
        uint uid = U32(p, cb, 16);
        uint flags = U32(p, cb, 20);

        // MODIFY can arrive many times a second: log it once per icon.
        if (nim == 1)
        {
            if (_modifySeen.Count > 2000)
                _modifySeen.Clear();
            if (!_modifySeen.Add($"{hwndRaw:X}:{uid}"))
                return;
        }

        string app = NameOf(hwndRaw);
        if (_appsSeen.Count < 60)
            _appsSeen.Add(app);

        if (_eventsLogged >= MaxLoggedEvents)
            return;
        _eventsLogged++;

        string line = $"Tray {NimName(nim)} app={app} elev={ElevationOf(hwndRaw)} id={uid} flags=0x{flags:X}";

        // i7: can we read the icon picture? (NIF_ICON = 0x2; hIcon@28)
        if ((flags & 0x2) != 0 && cb >= 32)
            line += " " + DescribeIcon(U32(p, cb, 28));

        if ((flags & 0x20) != 0 && cb >= 960) // NIF_GUID
        {
            var bytes = new byte[16];
            Marshal.Copy(IntPtr.Add(p, 944), bytes, 0, 16);
            line += $" guid={new Guid(bytes)}";
        }

        if (nim == 4 && cb >= 812) // NIM_SETVERSION
            line += $" version={U32(p, cb, 808)}";

        DiagnosticsLog.Write(line);
    }

    private static uint U32(IntPtr p, int cb, int offset)
        => offset + 4 <= cb ? unchecked((uint)Marshal.ReadInt32(p, offset)) : 0;

    private static string NimName(uint nim) => nim switch
    {
        0 => "ADD",
        1 => "MODIFY",
        2 => "DELETE",
        3 => "SETFOCUS",
        4 => "SETVERSION",
        _ => $"MSG{nim}",
    };

    // Reads only the picture's size, from a private copy of the handle that is
    // always released. Proves we can get at the pixels; nothing is drawn yet.
    private static string DescribeIcon(uint hIconRaw)
    {
        if (hIconRaw == 0)
            return "icon=none";

        IntPtr copy = IntPtr.Zero;
        TrayNative.TrayIconInfo info = default;
        try
        {
            copy = TrayNative.CopyIcon(new IntPtr(unchecked((int)hIconRaw)));
            if (copy == IntPtr.Zero)
            {
                _iconFail++;
                return $"icon=copy-failed({Marshal.GetLastWin32Error()})";
            }

            if (!TrayNative.GetIconInfo(copy, out info))
            {
                _iconFail++;
                return "icon=info-failed";
            }

            bool hasColor = info.hbmColor != IntPtr.Zero;
            IntPtr bitmap = hasColor ? info.hbmColor : info.hbmMask;
            if (TrayNative.GetObjectW(bitmap, Marshal.SizeOf<TrayNative.TrayBitmap>(), out TrayNative.TrayBitmap bmp) == 0)
            {
                _iconFail++;
                return "icon=size-failed";
            }

            // A mask-only (black and white) icon stores two pictures in one.
            int height = hasColor ? bmp.bmHeight : bmp.bmHeight / 2;
            _iconOk++;
            return $"icon={bmp.bmWidth}x{height}" + (hasColor ? "" : " mono");
        }
        catch (Exception ex)
        {
            _iconFail++;
            return $"icon=error({ex.GetType().Name})";
        }
        finally
        {
            if (info.hbmColor != IntPtr.Zero) TrayNative.DeleteObject(info.hbmColor);
            if (info.hbmMask != IntPtr.Zero) TrayNative.DeleteObject(info.hbmMask);
            if (copy != IntPtr.Zero) TrayNative.DestroyIcon(copy);
        }
    }

    // i7: is the app that owns this icon running as administrator? Clicks from
    // a normal process to an elevated one may be blocked, so we need to know.
    internal static string ElevationOf(uint hwndRaw)
    {
        uint pid = 0;
        try
        {
            TrayNative.GetWindowThreadProcessId(new IntPtr(unchecked((int)hwndRaw)), out pid);
        }
        catch
        {
            return "?";
        }

        if (pid == 0)
            return "?";
        if (_elevation.TryGetValue(pid, out string? cached))
            return cached;

        string result = "?";
        IntPtr hProcess = IntPtr.Zero;
        IntPtr hToken = IntPtr.Zero;
        try
        {
            const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
            const uint TOKEN_QUERY = 0x0008;
            const int TokenElevation = 20;

            hProcess = TrayNative.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            if (hProcess != IntPtr.Zero
                && TrayNative.OpenProcessToken(hProcess, TOKEN_QUERY, out hToken)
                && TrayNative.GetTokenInformation(hToken, TokenElevation, out int elevated, sizeof(int), out _))
            {
                result = elevated != 0 ? "yes" : "no";
            }
        }
        catch
        {
            // Keep "?".
        }
        finally
        {
            if (hToken != IntPtr.Zero) TrayNative.CloseHandle(hToken);
            if (hProcess != IntPtr.Zero) TrayNative.CloseHandle(hProcess);
        }

        if (_elevation.Count > 200)
            _elevation.Clear();
        _elevation[pid] = result;
        return result;
    }

    private static string NameOf(uint hwndRaw)
    {
        int key = unchecked((int)hwndRaw);
        if (_procNames.TryGetValue(key, out string? cached))
            return cached;

        string name = "?";
        try
        {
            TrayNative.GetWindowThreadProcessId(new IntPtr(key), out uint pid);
            if (pid != 0)
            {
                using var proc = Process.GetProcessById((int)pid);
                name = proc.ProcessName;
            }
        }
        catch
        {
            // Process ended or access denied: keep "?".
        }

        if (_procNames.Count > 500)
            _procNames.Clear();
        _procNames[key] = name;
        return name;
    }

    // ---- Appbar messages (must keep working) ------------------------------

    // Machitan's own work-area reservation goes through here too. QUERYPOS,
    // SETPOS and GETTASKBARPOS hand their data over in shared memory that is
    // made for the tray window's process. That is us now, so Explorer could not
    // open it if we forwarded as-is: copy it into shared memory made for
    // Explorer, forward, then copy the answer back. Same technique as
    // ManagedShell (Apache-2.0). Anything unusual is forwarded untouched.
    private static IntPtr HandleAppBar(IntPtr wParam, IntPtr lParam, TrayNative.TrayCopyData cds)
    {
        _appBar++;

        if (cds.cbData != AppBarMsgSize || cds.lpData == IntPtr.Zero)
        {
            if (_appBar <= 3)
                DiagnosticsLog.Write($"TrayListener: appbar message with size {cds.cbData} forwarded as-is");
            return ForwardSend(WM_COPYDATA, wParam, lParam);
        }

        int abm = Marshal.ReadInt32(cds.lpData, 40);
        long hOld = Marshal.ReadInt64(cds.lpData, 48);
        if ((abm is ABM_QUERYPOS or ABM_SETPOS or ABM_GETTASKBARPOS) && hOld != 0)
            return ForwardAppBarShared(wParam, lParam, cds, hOld);

        return ForwardSend(WM_COPYDATA, wParam, lParam);
    }

    private static IntPtr ForwardAppBarShared(IntPtr wParam, IntPtr lParam, TrayNative.TrayCopyData cds, long hOld)
    {
        IntPtr fwd = GetForwardTarget();
        if (fwd == IntPtr.Zero)
            return IntPtr.Zero;

        TrayNative.GetWindowThreadProcessId(fwd, out uint explorerPid);
        if (explorerPid == 0)
            return IntPtr.Zero;

        int sourcePid = Marshal.ReadInt32(cds.lpData, 56);
        IntPtr oldPtr = TrayNative.SHLockShared(new IntPtr(hOld), unchecked((uint)sourcePid));
        if (oldPtr == IntPtr.Zero)
        {
            _forwardFailed++;
            return IntPtr.Zero;
        }

        IntPtr hNew = IntPtr.Zero;
        IntPtr pMsg = IntPtr.Zero;
        IntPtr pCds = IntPtr.Zero;
        try
        {
            hNew = TrayNative.SHAllocShared(IntPtr.Zero, AppBarDataSize, explorerPid);
            if (hNew == IntPtr.Zero)
            {
                _forwardFailed++;
                return IntPtr.Zero;
            }

            IntPtr newPtr = TrayNative.SHLockShared(hNew, explorerPid);
            if (newPtr == IntPtr.Zero)
            {
                _forwardFailed++;
                return IntPtr.Zero;
            }

            CopyBytes(oldPtr, newPtr, AppBarDataSize);
            TrayNative.SHUnlockShared(newPtr);

            // Same message, but pointing at the shared memory made for Explorer.
            var msg = new byte[AppBarMsgSize];
            Marshal.Copy(cds.lpData, msg, 0, AppBarMsgSize);
            BitConverter.GetBytes(hNew.ToInt64()).CopyTo(msg, 48);
            BitConverter.GetBytes(unchecked((int)explorerPid)).CopyTo(msg, 56);

            pMsg = Marshal.AllocHGlobal(AppBarMsgSize);
            Marshal.Copy(msg, 0, pMsg, AppBarMsgSize);

            pCds = Marshal.AllocHGlobal(Marshal.SizeOf<TrayNative.TrayCopyData>());
            Marshal.StructureToPtr(new TrayNative.TrayCopyData
            {
                dwData = IntPtr.Zero,
                cbData = AppBarMsgSize,
                lpData = pMsg,
            }, pCds, false);

            if (TrayNative.SendMessageTimeoutW(fwd, WM_COPYDATA, wParam, pCds,
                    TrayNative.SMTO_ABORTIFHUNG, ForwardTimeoutMs, out IntPtr result) == IntPtr.Zero)
            {
                _forwardFailed++;
                return IntPtr.Zero;
            }

            // Explorer may have written the answer into its copy: bring it back.
            IntPtr back = TrayNative.SHLockShared(hNew, explorerPid);
            if (back != IntPtr.Zero)
            {
                CopyBytes(back, oldPtr, AppBarDataSize);
                TrayNative.SHUnlockShared(back);
            }

            _appBarRemarshaled++;
            return result;
        }
        finally
        {
            if (pCds != IntPtr.Zero) Marshal.FreeHGlobal(pCds);
            if (pMsg != IntPtr.Zero) Marshal.FreeHGlobal(pMsg);
            if (hNew != IntPtr.Zero) TrayNative.SHFreeShared(hNew, explorerPid);
            TrayNative.SHUnlockShared(oldPtr);
        }
    }

    private static void CopyBytes(IntPtr from, IntPtr to, int count)
    {
        var buffer = new byte[count];
        Marshal.Copy(from, buffer, 0, count);
        Marshal.Copy(buffer, 0, to, count);
    }

    // ---- Forwarding to Explorer's real tray window ------------------------

    private static IntPtr FindExplorerTray()
    {
        IntPtr h = TrayNative.FindWindowW(TrayClass, null);
        while (h != IntPtr.Zero && h == _hwnd)
            h = TrayNative.FindWindowExW(IntPtr.Zero, h, TrayClass, null);
        return h;
    }

    private static IntPtr GetForwardTarget()
    {
        if (_hwndFwd == IntPtr.Zero || _hwndFwd == _hwnd || !TrayNative.IsWindow(_hwndFwd))
            _hwndFwd = FindExplorerTray();
        return _hwndFwd;
    }

    private static IntPtr ForwardSend(uint msg, IntPtr wParam, IntPtr lParam)
    {
        IntPtr fwd = GetForwardTarget();
        if (fwd == IntPtr.Zero)
            return IntPtr.Zero;

        // The timeout keeps a hung Explorer from freezing the bar.
        if (TrayNative.SendMessageTimeoutW(fwd, msg, wParam, lParam,
                TrayNative.SMTO_ABORTIFHUNG, ForwardTimeoutMs, out IntPtr result) == IntPtr.Zero)
        {
            _forwardFailed++;
            if (_forwardFailed <= 10)
                DiagnosticsLog.Write($"TrayListener: forward of 0x{msg:X} failed (error {Marshal.GetLastWin32Error()})");
            return IntPtr.Zero;
        }

        return result;
    }

    private static IntPtr ForwardPrivate(uint msg, IntPtr wParam, IntPtr lParam)
    {
        // A posted message must stay posted; a sent one is sent.
        if (TrayNative.InSendMessage())
            return ForwardSend(msg, wParam, lParam);

        IntPtr fwd = GetForwardTarget();
        if (fwd != IntPtr.Zero)
            TrayNative.PostMessageW(fwd, msg, wParam, lParam);
        return IntPtr.Zero;
    }

    // ---- Win32 declarations (private to the spike) ------------------------

    private static class TrayNative
    {
        public delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        public const uint SMTO_ABORTIFHUNG = 0x0002;
        public static readonly IntPtr HwndTopmost = new(-1);

        [StructLayout(LayoutKind.Sequential)]
        public struct TrayCopyData
        {
            public IntPtr dwData;
            public int cbData;
            public IntPtr lpData;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct TrayRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct TrayIconInfo
        {
            public bool fIcon;
            public int xHotspot;
            public int yHotspot;
            public IntPtr hbmMask;
            public IntPtr hbmColor;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct TrayBitmap
        {
            public int bmType;
            public int bmWidth;
            public int bmHeight;
            public int bmWidthBytes;
            public ushort bmPlanes;
            public ushort bmBitsPixel;
            public IntPtr bmBits;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        public struct WNDCLASS
        {
            public uint style;
            public IntPtr lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public IntPtr hInstance;
            public IntPtr hIcon;
            public IntPtr hCursor;
            public IntPtr hbrBackground;
            public string? lpszMenuName;
            public string lpszClassName;
        }

        [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindowW(string lpClassName, string? lpWindowName);

        [DllImport("user32.dll", EntryPoint = "FindWindowExW", CharSet = CharSet.Unicode)]
        public static extern IntPtr FindWindowExW(IntPtr parent, IntPtr childAfter, string? className, string? windowName);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out TrayRect rect);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr CopyIcon(IntPtr hIcon);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetIconInfo(IntPtr hIcon, out TrayIconInfo info);

        [DllImport("user32.dll")]
        public static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("gdi32.dll", EntryPoint = "GetObjectW")]
        public static extern int GetObjectW(IntPtr handle, int count, out TrayBitmap bitmap);

        [DllImport("gdi32.dll")]
        public static extern bool DeleteObject(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint access, bool inheritHandle, uint processId);

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr handle);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);

        [DllImport("advapi32.dll", SetLastError = true)]
        public static extern bool GetTokenInformation(IntPtr token, int infoClass, out int info, int length, out int returnLength);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

        [DllImport("user32.dll")]
        public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

        [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateWindowExW(uint exStyle, string className, string? windowName, uint style,
            int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

        [DllImport("user32.dll")]
        public static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
        public static extern IntPtr DefWindowProcW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", EntryPoint = "RegisterClassW", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern ushort RegisterClassW(ref WNDCLASS wc);

        [DllImport("user32.dll", EntryPoint = "UnregisterClassW", CharSet = CharSet.Unicode)]
        public static extern bool UnregisterClassW(string className, IntPtr instance);

        [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true)]
        public static extern IntPtr SendMessageTimeoutW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
            uint flags, uint timeoutMs, out IntPtr result);

        [DllImport("user32.dll", EntryPoint = "PostMessageW")]
        public static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool InSendMessage();

        [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)]
        public static extern uint RegisterWindowMessageW(string name);

        [DllImport("user32.dll", EntryPoint = "SendNotifyMessageW")]
        public static extern bool SendNotifyMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int index);

        [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode)]
        public static extern IntPtr GetModuleHandleW(string? moduleName);

        [DllImport("shlwapi.dll")]
        public static extern IntPtr SHAllocShared(IntPtr data, uint size, uint processId);

        [DllImport("shlwapi.dll")]
        public static extern IntPtr SHLockShared(IntPtr handle, uint processId);

        [DllImport("shlwapi.dll")]
        public static extern bool SHUnlockShared(IntPtr data);

        [DllImport("shlwapi.dll")]
        public static extern bool SHFreeShared(IntPtr handle, uint processId);
    }
}
