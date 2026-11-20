using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Machitan.Core;
using Machitan.Interop;
using Microsoft.UI.Dispatching;
using Microsoft.Win32;

namespace Machitan.Shell;

/// <summary>How a tray tile was clicked.</summary>
internal enum TrayClick
{
    Left,
    Double,
    Right,
}

/// <summary>One app icon in the tray, as its app last described it.</summary>
/// <param name="Key">Stays the same while the icon lives: its GUID, or its window and number.</param>
/// <param name="Name">Its tooltip, lines kept.</param>
/// <param name="Picture">Its picture, copied when the app set it; null when it has none Machitan can read.</param>
/// <param name="PictureStamp">Changes whenever the picture changes.</param>
/// <param name="Stable">Stays the same from one run to the next (its GUID, or its program and number): for keeping the icons' order.</param>
internal sealed record TrayIcon(string Key, string Name, IconPixels? Picture, int PictureStamp, string Stable);

/// <summary>
/// Receives the tray icons straight from the apps (Batch 6, option B; the way Zebar's
/// systray-util does it).
///
/// Apps add, change and remove their tray icons with Shell_NotifyIcon, which finds the
/// taskbar's window by its class name ("Shell_TrayWnd") and sends it a WM_COPYDATA message.
/// Machitan makes a hidden window with that class name, on its own thread, and keeps it in front
/// of Explorer's, so those messages reach Machitan first. Every message is then passed on to
/// Explorer's own taskbar window, unchanged, with Explorer's answer returned, so Explorer keeps
/// its tray as before and has it complete when Machitan stops. AppBar messages that carry an
/// answer block are passed on with a new block Explorer can open (AppBarRelay); unchanged,
/// Explorer answered them without acting (2026-11-20). If Machitan crashes, the window goes with
/// it and apps reach Explorer directly.
///
/// Explorer sometimes puts its own window in front again (each check, every quarter second,
/// steps Machitan's back in front). A tray icon an app sends in that moment reaches Explorer
/// only: an app that adds its icon as it starts can be missed that way. So after such a moment
/// Machitan asks the windows of apps started in the last minute for their icons again.
///
/// At start (and again after Explorer restarts) Machitan tells all apps "the taskbar was
/// created", as Explorer does when it starts; apps then send their icons again. An app that
/// ignores that message appears once it next changes its icon.
///
/// The pictures are copied the moment an app hands them over (it may destroy its icon right
/// after). A click on a tile goes to the app the way Explorer sends it: the app's own message
/// with the mouse event, after allowing the app to come to the front (for its menu). When an app
/// asks where its icon is (Shell_NotifyIconGetRect), the answer is the pointer, as the tiles
/// are where the user clicked. Left out: icons hidden by their own app, and Windows' own old
/// icons (volume, network, battery): Explorer still makes them, and Windows 11 shows those in its
/// quick settings button, not in the tray. They are known by the IDs Windows gives them, or as
/// Explorer's own icons with no way to click them.
///
/// Windows does not send a broadcast to message-only windows, and some apps keep their tray icon
/// on one; so the "taskbar was created" message also goes to each message-only window directly.
/// Machitan's window also carries Explorer's "TaskbandHWND" property, which Windows' taskbar
/// list looks for on the first "Shell_TrayWnd" window (see MirrorTaskband).
/// <see cref="Changed"/> is raised on the UI thread, at most a few times a second.
/// </summary>
internal sealed unsafe class TrayIconService : IDisposable
{
    private const string TrayClass = "Shell_TrayWnd";

    /// <summary>Explorer's property on its taskbar window: its button list (MSTaskSwWClass). See MirrorTaskband.</summary>
    private const string TaskbandProp = "TaskbandHWND";
    private const int TaskbandMostLines = 10;

    /// <summary>shell32's mark on a tray message (TRAYNOTIFYDATAW.dwSignature).</summary>
    private const uint TraySignature = 0x34753423;

    // COPYDATASTRUCT.dwData for messages to the taskbar (appbar messages are passed on as they are).
    private const nuint DataAppBar = 0;
    private const nuint DataTray = 1;
    private const nuint DataIconRect = 3;

    // Shell_NotifyIcon's messages.
    private const uint NimAdd = 0;
    private const uint NimModify = 1;
    private const uint NimDelete = 2;
    private const uint NimSetVersion = 4;

    // NOTIFYICONDATA's flags and states.
    private const uint NifMessage = 0x01;
    private const uint NifIcon = 0x02;
    private const uint NifTip = 0x04;
    private const uint NifState = 0x08;
    private const uint NifGuid = 0x20;
    private const uint NisHidden = 0x01;

    /// <summary>NIN_SELECT: sent after a left click to apps of version 3 and up.</summary>
    private const uint NinSelect = 0x0400;

    // Where things are in shell32's tray message: an 8-byte header (mark, message), then the
    // 32-bit NOTIFYICONDATAW (handles as 32 bits, so 32- and 64-bit apps send the same).
    private const int OffMessage = 4;
    private const int OffWindow = 12;
    private const int OffId = 16;
    private const int OffFlags = 20;
    private const int OffCallback = 24;
    private const int OffIcon = 28;
    private const int OffTip = 32;
    private const int TipChars = 128;
    private const int OffState = 288;
    private const int OffStateMask = 292;
    private const int OffVersion = 808;
    private const int OffGuid = 944;

    // Shell_NotifyIconGetRect's question: mark, which half (1 = left/right, 2 = top/bottom), size, padding, window, number, GUID.
    private const int RectOffWhich = 4;
    private const int RectOffWindow = 16;
    private const int RectOffId = 20;
    private const int RectOffGuid = 24;

    private const nuint TimerKeepFirst = 1;
    private const nuint TimerAskAgain = 2;
    private const nuint TimerReport = 3;
    private const uint KeepFirstMs = 250;
    private const int PruneEveryTicks = 8;
    private const uint AskAgainMs = 2000;
    private const uint ReportMs = 6000;
    private const uint ForwardTimeoutMs = 4000;

    /// <summary>FindWindowEx's parent for message-only windows.</summary>
    private const nint MessageOnlyParent = -3;

    private const nuint TimerWatch = 4;
    private const uint WatchMs = 10000;
    private const int WatchMostLines = 300;

    // Programs that sent the taskbar anything (remembered up to this many; see NoteNewSender).
    private const int SendersMost = 512;
    private const int NewSenderMostLines = 40;

    // After another "Shell_TrayWnd" window was in front: ask apps started this long before it.
    private const nuint TimerCatchUp = 5;
    private const uint CatchUpMs = 1500;
    private const long RecentStartMs = 60000;
    private const int GapMostLines = 20;

    /// <summary>
    /// The IDs of Windows' own volume, network and power (battery) icons, made by Explorer;
    /// Windows 11 shows these in its quick settings button instead of the tray.
    /// </summary>
    private static readonly Guid[] QuickSettingsIcons =
    {
        new("7820AE73-23E3-4229-82C1-E41CB67D5B9C"),
        new("7820AE74-23E3-4229-82C1-E41CB67D5B9C"),
        new("7820AE75-23E3-4229-82C1-E41CB67D5B9C"),
    };

    /// <summary>Windows' tray records (one per app icon it has ever seen).</summary>
    private const string RecordsKey = @"Control Panel\NotifyIconSettings";

    /// <summary>The one service whose window is up (window messages find it through this).</summary>
    private static TrayIconService? s_receiver;
    private static bool s_classRegistered;
    private static uint s_taskbarCreated;

    private readonly DispatcherQueue _queue;
    private readonly DispatcherQueueTimer _publish;
    private readonly object _gate = new();

    // Inside _gate.
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly List<Entry> _order = new();
    private int _stamp;
    private bool _publishQueued;

    // Tray thread.
    private Thread? _thread;
    private nint _hwnd;
    private uint _threadId;
    private nint _explorer;
    private RECT _mirrored;
    private bool _broadcasting;
    private int _ticks;
    private int _steppedInFront;
    private bool _noExplorerNoted;
    private bool _reported;
    private long _gapAt;
    private int _gapLines;
    private bool _askedAllAfterStartGap;
    private bool _catchUpPending;
    private string _askAgainWhy = "Explorer started again";
    private readonly HashSet<nint> _askedAfterGap = new();

    // Tray thread: what the apps sent in the 10 s after Machitan asked them (for the log).
    private long _watchUntil;
    private string _watchWhy = string.Empty;
    private readonly List<string> _watchLines = new();
    private readonly Dictionary<uint, string> _programs = new();

    // Tray thread: every program heard from, and how many were noted after the first look.
    private readonly HashSet<uint> _senders = new();
    private int _newSenderLines;

    // Tray thread: Explorer's button list as copied onto Machitan's window (see MirrorTaskband).
    private nint _mirroredTaskband;
    private int _taskbandLines;

    /// <summary>Explorer's process (both threads read it): its own icons that take no clicks are left out.</summary>
    private volatile uint _explorerProcess;

    // UI thread.
    private volatile bool _disposed;
    private bool _noCallbackNoted;
    private bool _clickFailNoted;

    internal TrayIconService(DispatcherQueue queue)
    {
        _queue = queue;
        _publish = queue.CreateTimer();
        _publish.Interval = TimeSpan.FromMilliseconds(150);
        _publish.IsRepeating = false;
        _publish.Tick += (_, _) => Publish();
    }

    /// <summary>Raised on the UI thread when the icons changed.</summary>
    internal event Action? Changed;

    /// <summary>The icons, in the order their apps first added them (UI thread).</summary>
    internal IReadOnlyList<TrayIcon> Icons { get; private set; } = Array.Empty<TrayIcon>();

    /// <summary>Starts receiving (UI thread). Only one receiver runs at a time.</summary>
    internal void Start()
    {
        if (_disposed || _thread is not null)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref s_receiver, this, null) is not null)
        {
            Log.Warn("Tray: another tray receiver is already running in Machitan; this one stays off.");
            return;
        }

        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Machitan tray",
        };
        _thread.Start();
    }

    /// <summary>The process of the icon's app; 0 when the icon is gone or its app unknown (any thread).</summary>
    internal uint OwnerOf(TrayIcon icon)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(icon.Key, out Entry? entry) ? entry.Owner : 0;
        }
    }

    /// <summary>A click on a tile, passed to the icon's app as Explorer would (UI thread).</summary>
    internal void Click(TrayIcon icon, TrayClick click)
    {
        if (_disposed)
        {
            return;
        }

        nint window;
        uint id;
        uint callback;
        uint version;
        lock (_gate)
        {
            if (!_entries.TryGetValue(icon.Key, out Entry? entry))
            {
                return;
            }

            window = entry.Window;
            id = entry.Id;
            callback = entry.Callback;
            version = entry.Version;
        }

        if (window == 0 || callback == 0)
        {
            if (!_noCallbackNoted)
            {
                _noCallbackNoted = true;
                Log.Info("Tray: \"" + OneLine(icon.Name) + "\" takes no clicks (its app gave no message for them).");
            }

            return;
        }

        if (!User32.IsWindow(window))
        {
            // Its app is gone without saying so.
            Prune();
            return;
        }

        // As after a click on Windows' own tray: the app may come to the front, so its menu
        // closes when the user clicks elsewhere.
        User32.GetWindowThreadProcessId(window, out uint processId);
        User32.AllowSetForegroundWindow(processId != 0 ? (int)processId : User32.ASFW_ANY);

        switch (click)
        {
            case TrayClick.Right:
                Notify(window, callback, id, version, User32.WM_RBUTTONDOWN);
                Notify(window, callback, id, version, User32.WM_RBUTTONUP);
                break;
            case TrayClick.Double:
                Notify(window, callback, id, version, User32.WM_LBUTTONDBLCLK);
                Notify(window, callback, id, version, User32.WM_LBUTTONUP);
                break;
            default:
                Notify(window, callback, id, version, User32.WM_LBUTTONDOWN);
                Notify(window, callback, id, version, User32.WM_LBUTTONUP);
                break;
        }

        if (version >= 3)
        {
            // Explorer adds these for version 3 and up (version 4 apps rely on them).
            Notify(window, callback, id, version, click == TrayClick.Right ? User32.WM_CONTEXTMENU : NinSelect);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _publish.Stop();

        nint hwnd = Volatile.Read(ref _hwnd);
        if (hwnd != 0)
        {
            User32.PostMessageW(hwnd, User32.WM_CLOSE, 0, 0);
        }
        else if (_threadId != 0)
        {
            User32.PostThreadMessageW(_threadId, User32.WM_QUIT, 0, 0);
        }

        // The thread lets go of the receiver slot itself as it ends.
        _thread?.Join(TimeSpan.FromSeconds(1));
    }

    // ---------------------------------------------------------------- tray thread

    private void Run()
    {
        try
        {
            _threadId = Kernel32.GetCurrentThreadId();

            // A message queue before anyone posts to this thread.
            User32.PeekMessageW(out _, 0, 0, 0, User32.PM_NOREMOVE);

            if (s_taskbarCreated == 0)
            {
                s_taskbarCreated = User32.RegisterWindowMessage("TaskbarCreated");
            }

            if (!MakeWindow())
            {
                return;
            }

            if (_disposed)
            {
                User32.DestroyWindow(_hwnd);
                return;
            }

            User32.SetTimer(_hwnd, TimerKeepFirst, KeepFirstMs, 0);
            User32.SetTimer(_hwnd, TimerReport, ReportMs, 0);
            KeepFirst();
            AskAppsAgain("Machitan started");

            while (User32.GetMessageW(out MSG message, 0, 0, 0) > 0)
            {
                User32.DispatchMessageW(&message);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Tray: the tray thread failed.", ex);
        }
        finally
        {
            nint hwnd = Volatile.Read(ref _hwnd);
            if (hwnd != 0)
            {
                User32.DestroyWindow(hwnd);
                Volatile.Write(ref _hwnd, 0);
            }

            Interlocked.CompareExchange(ref s_receiver, null, this);
        }
    }

    private bool MakeWindow()
    {
        nint instance = Kernel32.GetModuleHandleW(null);
        if (!s_classRegistered)
        {
            fixed (char* className = TrayClass)
            {
                var windowClass = new WNDCLASSEXW
                {
                    cbSize = (uint)sizeof(WNDCLASSEXW),
                    lpfnWndProc = &WndProc,
                    hInstance = instance,
                    lpszClassName = className,
                };
                if (User32.RegisterClassExW(&windowClass) == 0)
                {
                    int error = Marshal.GetLastPInvokeError();
                    if (error != User32.ERROR_CLASS_ALREADY_EXISTS)
                    {
                        Log.Warn("Tray: Machitan's tray window could not be set up (error "
                            + error.ToString(CultureInfo.InvariantCulture) + "); the tray stays out of Dynaland.");
                        return false;
                    }
                }
            }

            s_classRegistered = true;
        }

        // Hidden for good: never shown, never active, no taskbar button.
        nint hwnd = User32.CreateWindowEx(
            (uint)(User32.WS_EX_TOOLWINDOW | User32.WS_EX_TOPMOST | User32.WS_EX_NOACTIVATE),
            TrayClass,
            string.Empty,
            unchecked((uint)User32.WS_POPUP),
            0,
            0,
            0,
            0,
            0,
            0,
            instance,
            0);
        if (hwnd == 0)
        {
            Log.Warn("Tray: Machitan's tray window could not be made (error "
                + Marshal.GetLastPInvokeError().ToString(CultureInfo.InvariantCulture) + "); the tray stays out of Dynaland.");
            return false;
        }

        Volatile.Write(ref _hwnd, hwnd);

        // Explorer takes tray messages from apps running with lower rights too; so does Machitan.
        User32.ChangeWindowMessageFilterEx(hwnd, User32.WM_COPYDATA, User32.MSGFLT_ALLOW, 0);
        return true;
    }

    [UnmanagedCallersOnly]
    private static nint WndProc(nint hwnd, uint message, nint wParam, nint lParam)
    {
        try
        {
            TrayIconService? receiver = Volatile.Read(ref s_receiver);
            if (receiver is not null && hwnd != 0 && Volatile.Read(ref receiver._hwnd) == hwnd)
            {
                return receiver.OnMessage(hwnd, message, wParam, lParam);
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Tray: a message to Machitan's tray window failed.", ex);
        }

        return User32.DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private nint OnMessage(nint hwnd, uint message, nint wParam, nint lParam)
    {
        if (message == User32.WM_COPYDATA)
        {
            return OnCopyData(wParam, lParam);
        }

        if (message == User32.WM_TIMER)
        {
            OnTimer((nuint)wParam);
            return 0;
        }

        if (s_taskbarCreated != 0 && message == s_taskbarCreated)
        {
            if (!_broadcasting)
            {
                // Not Machitan's own: Explorer started (again).
                OnExplorerStarted();
            }

            return 0;
        }

        if (message == User32.WM_CLOSE)
        {
            User32.DestroyWindow(hwnd);
            return 0;
        }

        if (message == User32.WM_DESTROY)
        {
            User32.RemovePropW(hwnd, TaskbandProp);
            User32.KillTimer(hwnd, TimerKeepFirst);
            User32.KillTimer(hwnd, TimerAskAgain);
            User32.KillTimer(hwnd, TimerReport);
            User32.KillTimer(hwnd, TimerWatch);
            User32.KillTimer(hwnd, TimerCatchUp);
            Volatile.Write(ref _hwnd, 0);
            User32.PostQuitMessage(0);
            return 0;
        }

        if (message == User32.WM_COMMAND || (message >= User32.WM_USER && message < 0xC000))
        {
            // Commands apps send the taskbar: Explorer's to answer.
            return Forward(message, wParam, lParam);
        }

        return User32.DefWindowProcW(hwnd, message, wParam, lParam);
    }

    private nint OnCopyData(nint wParam, nint lParam)
    {
        var data = (COPYDATASTRUCT*)lParam;
        if (data == null || data->lpData == 0)
        {
            return ForwardCopyData(wParam, lParam);
        }

        var bytes = (byte*)data->lpData;
        int length = (int)Math.Min(data->cbData, int.MaxValue);
        NoteNewSender(wParam, data->dwData, bytes, length);

        if (data->dwData == DataAppBar && AppBarRelay.TryPassOn(ExplorerTray(), wParam, bytes, length, out nint relayed))
        {
            return relayed;
        }

        if (data->dwData == DataTray)
        {
            bool held = OnTrayMessage(bytes, length);
            nint explorer = ForwardCopyData(wParam, lParam);

            // Success when either side holds the icon: an app re-adding an icon Explorer still
            // has gets "no" from Explorer, and some apps would keep retrying.
            return explorer != 0 || held ? 1 : 0;
        }

        if (data->dwData == DataIconRect && TryAnswerIconRect(bytes, length, out nint answer))
        {
            return answer;
        }

        return ForwardCopyData(wParam, lParam);
    }

    /// <summary>
    /// The first message from a program that had sent nothing before the first look is noted
    /// (tray thread), whatever it is and whether or not it is understood: it shows whether an
    /// app with no icon here talks to the taskbar at all (Traydio and other apps of the
    /// Traytober 2026 set, Part 16). The sender is the window the message names (an app's
    /// icon window, else the window shell32 sent it for); messages naming no window are not
    /// counted.
    /// </summary>
    private void NoteNewSender(nint sender, nuint kind, byte* bytes, int length)
    {
        bool marked = length >= OffMessage + 4 && ReadUInt(bytes, 0) == TraySignature;
        if (kind == DataTray && marked && length >= OffWindow + 4 && ReadUInt(bytes, OffWindow) != 0)
        {
            sender = (nint)(int)ReadUInt(bytes, OffWindow);
        }

        if (sender == 0 || _senders.Count >= SendersMost)
        {
            return;
        }

        User32.GetWindowThreadProcessId(sender, out uint process);
        if (process == 0 || !_senders.Add(process) || !_reported || _newSenderLines >= NewSenderMostLines)
        {
            return;
        }

        _newSenderLines++;
        string what;
        if (kind == DataTray)
        {
            what = marked
                ? "tray " + CommandName(ReadUInt(bytes, OffMessage))
                    + (length < OffTip ? ", too short to read" : string.Empty)
                : "a tray message without shell32's mark";
        }
        else if (kind == DataAppBar)
        {
            what = "an appbar message";
        }
        else if (kind == DataIconRect)
        {
            what = "a question about where its icon is";
        }
        else
        {
            what = "message kind " + kind.ToString(CultureInfo.InvariantCulture);
        }

        Log.Info("Tray: first message from " + ProgramOf(process) + " after the first look: " + what
            + " (" + length.ToString(CultureInfo.InvariantCulture) + " bytes).");
    }

    /// <summary>Applies one Shell_NotifyIcon message. True when Machitan holds (or held) the icon.</summary>
    private bool OnTrayMessage(byte* p, int length)
    {
        if (length < OffTip || ReadUInt(p, 0) != TraySignature)
        {
            return false;
        }

        uint command = ReadUInt(p, OffMessage);
        uint flags = ReadUInt(p, OffFlags);
        nint window = (nint)(int)ReadUInt(p, OffWindow);
        uint id = ReadUInt(p, OffId);
        Guid guid = (flags & NifGuid) != 0 && length >= OffGuid + 16 ? ReadGuid(p, OffGuid) : Guid.Empty;

        // Everything read before taking the lock; the picture too, while the app still has it.
        uint owner = 0;
        if (window != 0)
        {
            User32.GetWindowThreadProcessId(window, out owner);
        }

        uint callback = ReadUInt(p, OffCallback);
        bool hasPicture = (flags & NifIcon) != 0;
        IconPixels? picture = hasPicture ? ReadPicture((nint)(int)ReadUInt(p, OffIcon)) : null;
        string? tip = (flags & NifTip) != 0 ? ReadTip(p, length) : null;
        bool? hidden = null;
        if ((flags & NifState) != 0 && length >= OffStateMask + 4
            && (ReadUInt(p, OffStateMask) & NisHidden) != 0)
        {
            hidden = (ReadUInt(p, OffState) & NisHidden) != 0;
        }

        bool changed = false;
        bool held;
        Entry? added = null;
        string outcome;
        lock (_gate)
        {
            Entry? entry = Find(window, id, guid);
            switch (command)
            {
                case NimAdd:
                case NimModify:
                    if (entry is null)
                    {
                        // A change to an icon Machitan never saw added (its app missed the "taskbar
                        // created" message): taken when it says enough to show and click it.
                        if (command == NimModify && (flags & (NifIcon | NifMessage)) != (NifIcon | NifMessage))
                        {
                            held = false;
                            outcome = "skipped (a change to an icon not seen added, without picture and message)";
                            break;
                        }

                        entry = new Entry(KeyFor(window, id, guid));
                        _entries[entry.Key] = entry;
                        _order.Add(entry);
                        changed = true;
                        added = entry;
                    }

                    if (window != 0)
                    {
                        entry.Window = window;
                        entry.Id = id;
                        entry.Owner = owner;
                    }

                    if (guid != Guid.Empty)
                    {
                        entry.Guid = guid;
                    }

                    if ((flags & NifMessage) != 0 && entry.Callback != callback)
                    {
                        entry.Callback = callback;
                        changed = true;
                    }

                    if (tip is not null && tip != entry.Tip)
                    {
                        entry.Tip = tip;
                        changed = true;
                    }

                    if (hasPicture && !SamePicture(entry.Picture, picture))
                    {
                        entry.Picture = picture;
                        entry.Stamp = ++_stamp;
                        changed = true;
                    }

                    if (hidden is bool isHidden && isHidden != entry.Hidden)
                    {
                        entry.Hidden = isHidden;
                        changed = true;
                    }

                    held = true;
                    outcome = added is not null ? "added" : "updated";
                    break;

                case NimDelete:
                    held = entry is not null;
                    outcome = entry is not null ? "removed" : "not known";
                    if (entry is not null)
                    {
                        _entries.Remove(entry.Key);
                        _order.Remove(entry);
                        changed = true;
                    }

                    break;

                case NimSetVersion:
                    held = entry is not null;
                    outcome = entry is not null ? "version set" : "not known";
                    if (entry is not null && length >= OffVersion + 4)
                    {
                        entry.Version = ReadUInt(p, OffVersion);
                    }

                    break;

                default:
                    held = entry is not null;
                    outcome = "passed on";
                    break;
            }
        }

        if (Environment.TickCount64 < _watchUntil && _watchLines.Count < WatchMostLines)
        {
            _watchLines.Add(CommandName(command) + " from " + ProgramOf(owner)
                + (guid != Guid.Empty ? " {" + guid.ToString("D", CultureInfo.InvariantCulture) + "}" : " id " + id.ToString(CultureInfo.InvariantCulture))
                + ", flags 0x" + flags.ToString("X", CultureInfo.InvariantCulture)
                + (tip is { Length: > 0 } ? ", \"" + OneLine(tip) + "\"" : string.Empty)
                + ": " + outcome);
        }

        if (changed)
        {
            MarkChanged();
        }

        if (added is not null && _reported)
        {
            // After the first look, each icon that arrives is noted (it shows what reaches Machitan).
            NoteLateIcon(added);
        }

        return held;
    }

    private static string CommandName(uint command)
    {
        return command switch
        {
            NimAdd => "add",
            NimModify => "change",
            NimDelete => "remove",
            3 => "focus",
            NimSetVersion => "version",
            _ => "message " + command.ToString(CultureInfo.InvariantCulture),
        };
    }

    /// <summary>Tray thread: a program's file name, remembered per process.</summary>
    private string ProgramOf(uint processId)
    {
        if (!_programs.TryGetValue(processId, out string? program))
        {
            program = ProgramName(processId);
            _programs[processId] = program;
        }

        return program;
    }

    private void NoteLateIcon(Entry entry)
    {
        string name;
        uint owner;
        bool windowsOwn;
        lock (_gate)
        {
            name = entry.Tip.Length > 0 ? OneLine(entry.Tip) : "(no tooltip)";
            owner = entry.Owner;
            windowsOwn = IsWindowsOwn(entry);
        }

        string program = ProgramName(owner);
        Log.Info("Tray: icon added: \"" + name + "\" (" + program + ")" + (windowsOwn ? ", Windows' own: left out." : "."));
    }

    /// <summary>An app asks where its icon is (to place its own pop-up): at the pointer, where the user clicked.</summary>
    private bool TryAnswerIconRect(byte* p, int length, out nint answer)
    {
        answer = 0;
        if (length < RectOffGuid + 16 || ReadUInt(p, 0) != TraySignature)
        {
            return false;
        }

        uint which = ReadUInt(p, RectOffWhich);
        nint window = (nint)(int)ReadUInt(p, RectOffWindow);
        uint id = ReadUInt(p, RectOffId);
        Guid guid = ReadGuid(p, RectOffGuid);
        lock (_gate)
        {
            if (Find(window, id, guid) is null)
            {
                return false;
            }
        }

        if ((which != 1 && which != 2) || !User32.GetCursorPos(out POINT point))
        {
            return false;
        }

        answer = which == 1 ? Pack(point.X, point.X + 1) : Pack(point.Y, point.Y + 1);
        return true;
    }

    private void OnTimer(nuint id)
    {
        if (id == TimerKeepFirst)
        {
            KeepFirst();
            if (++_ticks % PruneEveryTicks == 0)
            {
                Prune();
            }
        }
        else if (id == TimerAskAgain)
        {
            User32.KillTimer(_hwnd, TimerAskAgain);
            AskAppsAgain(_askAgainWhy);
        }
        else if (id == TimerCatchUp)
        {
            User32.KillTimer(_hwnd, TimerCatchUp);
            _catchUpPending = false;
            CatchUp();
        }
        else if (id == TimerReport)
        {
            User32.KillTimer(_hwnd, TimerReport);
            Report();
        }
        else if (id == TimerWatch)
        {
            User32.KillTimer(_hwnd, TimerWatch);
            FlushWatch(cutShort: false);
        }
    }

    /// <summary>
    /// What the apps send back in the next 10 s goes to the log in one block; lines of an earlier
    /// block not written yet are written first.
    /// </summary>
    private void StartWatch(string why)
    {
        if (_watchLines.Count > 0)
        {
            FlushWatch(cutShort: true);
        }

        _watchWhy = why;
        _watchUntil = Environment.TickCount64 + WatchMs;
        User32.SetTimer(_hwnd, TimerWatch, WatchMs + 200, 0);
    }

    /// <summary>Writes what the apps sent since Machitan asked them, in one block.</summary>
    private void FlushWatch(bool cutShort)
    {
        Log.Info("Tray: what the apps sent " + (cutShort ? "after Machitan asked (" + _watchWhy + "), until it asked again: " : "in the 10 s after Machitan asked (" + _watchWhy + "): ")
            + _watchLines.Count.ToString(CultureInfo.InvariantCulture) + " message(s)"
            + (_watchLines.Count > 0 ? ":\n  " + string.Join("\n  ", _watchLines) : "."));
        _watchLines.Clear();
    }

    /// <summary>
    /// Keeps Machitan's window first among the "Shell_TrayWnd" windows (FindWindow takes the
    /// front-most), and at the same place and size as Explorer's, for apps that look at it.
    /// Explorer's button list is copied onto it first (see MirrorTaskband).
    /// </summary>
    private void KeepFirst()
    {
        nint hwnd = _hwnd;
        if (hwnd == 0)
        {
            return;
        }

        nint explorer = ExplorerTray();
        MirrorTaskband(hwnd, explorer);

        nint first = User32.FindWindow(TrayClass, null);
        if (first != hwnd)
        {
            User32.SetWindowPos(hwnd, User32.HWND_TOPMOST, 0, 0, 0, 0,
                User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
            _steppedInFront++;

            // Not before Machitan first asked the apps (that question covers everything before it).
            if (first != 0 && _watchWhy.Length > 0)
            {
                NoteGap(first);
            }
        }

        if (explorer != 0 && User32.GetWindowRect(explorer, out RECT rect)
            && (rect.Left != _mirrored.Left || rect.Top != _mirrored.Top || rect.Right != _mirrored.Right || rect.Bottom != _mirrored.Bottom))
        {
            _mirrored = rect;
            User32.SetWindowPos(hwnd, 0, rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top,
                User32.SWP_NOZORDER | User32.SWP_NOACTIVATE);
        }
    }

    /// <summary>
    /// Copies Explorer's "TaskbandHWND" window property onto Machitan's tray window (tray
    /// thread, every quarter second, so a restarted Explorer is followed). Windows' taskbar list
    /// (ITaskbarList, explorerframe.dll on build 26300) finds the taskbar's button list as that
    /// property of the first "Shell_TrayWnd" window: FindWindowW, then GetPropW. Without it, its
    /// calls fail, and so does the Windows App SDK's AppWindow.IsShownInSwitchers ("Not
    /// implemented. SetShownInSwitchers failed.", as when no taskbar exists yet at sign-in).
    /// WinUIEx's tray icon sets that on its hidden window before it adds the icon (H.NotifyIcon
    /// does for its menu window), so Traydio stopped before its icon was ever added (Traydio's
    /// own log, Part 17). With the property, those calls reach Explorer's own button list, as
    /// they do without Machitan.
    /// </summary>
    private void MirrorTaskband(nint hwnd, nint explorer)
    {
        nint band = explorer != 0 ? User32.GetPropW(explorer, TaskbandProp) : 0;
        if (band == _mirroredTaskband)
        {
            return;
        }

        if (band == 0)
        {
            // Explorer's taskbar is gone (or has no button list): neither does Machitan's window.
            User32.RemovePropW(hwnd, TaskbandProp);
        }
        else if (!User32.SetPropW(hwnd, TaskbandProp, band))
        {
            if (_taskbandLines < TaskbandMostLines)
            {
                _taskbandLines++;
                Log.Warn("Tray: Explorer's taskbar button list could not be named on Machitan's tray window (error "
                    + Marshal.GetLastPInvokeError().ToString(CultureInfo.InvariantCulture) + "); apps that hide a window from Alt+Tab may fail. Tried again in a moment.");
            }

            return;
        }

        _mirroredTaskband = band;
        if (_taskbandLines < TaskbandMostLines)
        {
            _taskbandLines++;
            Log.Info(band != 0
                ? "Tray: Machitan's tray window names Explorer's taskbar button list (0x" + band.ToString("X", CultureInfo.InvariantCulture)
                    + "), as Explorer's does; Windows' taskbar list and apps that hide a window from Alt+Tab use it."
                : "Tray: Explorer's taskbar button list is gone; Machitan's tray window no longer names it.");
        }
    }

    /// <summary>
    /// Another "Shell_TrayWnd" window was in front of Machitan's until now (for up to a quarter
    /// second): tray icons sent in that moment reached it, not Machitan. Noted in the log; in a
    /// moment the apps that may have sent theirs then are asked again (see <see cref="CatchUp"/>).
    /// </summary>
    private void NoteGap(nint first)
    {
        _gapAt = Environment.TickCount64;

        // While the apps answer Machitan's start-up question, all of them are asked once more.
        bool askAll = string.Equals(_watchWhy, "Machitan started", StringComparison.Ordinal)
            && Environment.TickCount64 < _watchUntil && !_askedAllAfterStartGap;
        if (askAll)
        {
            _askedAllAfterStartGap = true;
            _askAgainWhy = "another window was in front while the apps answered";
            User32.SetTimer(_hwnd, TimerAskAgain, AskAgainMs, 0);
        }
        else if (!_catchUpPending)
        {
            // Armed once: a window that keeps stepping in front must not keep pushing it back.
            _catchUpPending = true;
            User32.SetTimer(_hwnd, TimerCatchUp, CatchUpMs, 0);
        }

        if (_gapLines < GapMostLines)
        {
            _gapLines++;
            User32.GetWindowThreadProcessId(first, out uint owner);
            Log.Info("Tray: another \"Shell_TrayWnd\" window (" + ProgramOf(owner) + ") was in front of Machitan's for up to a quarter second;"
                + " tray icons sent in that moment reached it, not Machitan. Machitan's is in front again; "
                + (askAll ? "all apps are asked for their icons once more in 2 s." : "apps started in the last minute are asked again in 1.5 s.")
                + (_gapLines == GapMostLines ? " (Further such lines this run are left out.)" : string.Empty));
        }
    }

    /// <summary>
    /// After another window was in front: every window (shown, hidden or message-only) of an app
    /// started in the minute before is told "the taskbar was created", so it sends its icon again,
    /// this time to Machitan. Each window is asked once a run; apps running longer are left alone.
    /// </summary>
    private void CatchUp()
    {
        if (s_taskbarCreated == 0)
        {
            return;
        }

        DateTime since = DateTime.Now.AddMilliseconds(-(RecentStartMs + Math.Max(0, Environment.TickCount64 - _gapAt)));
        uint self = (uint)Environment.ProcessId;
        var recent = new Dictionary<uint, bool>();
        var programs = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        int asked = 0;

        foreach (nint parent in new[] { (nint)0, MessageOnlyParent })
        {
            nint window = 0;
            for (int guard = 0; guard < 8192; guard++)
            {
                window = User32.FindWindowEx(parent, window, null, null);
                if (window == 0)
                {
                    break;
                }

                User32.GetWindowThreadProcessId(window, out uint process);
                if (process == 0 || process == self)
                {
                    continue;
                }

                if (!recent.TryGetValue(process, out bool isRecent))
                {
                    isRecent = StartedSince(process, since);
                    recent[process] = isRecent;
                }

                if (isRecent && _askedAfterGap.Add(window) && User32.SendNotifyMessageW(window, s_taskbarCreated, 0, 0))
                {
                    asked++;
                    programs.Add(ProgramOf(process));
                }
            }
        }

        if (asked == 0)
        {
            if (_gapLines < GapMostLines)
            {
                Log.Info("Tray: no app started in the last minute that was not asked already; nothing to ask again.");
            }

            return;
        }

        // What they send back goes to the log in one block, unless a block is open already.
        if (Environment.TickCount64 >= _watchUntil)
        {
            StartWatch("apps started while another window was in front");
        }

        Log.Info("Tray: asked " + asked.ToString(CultureInfo.InvariantCulture) + " window(s) of apps started in the last minute for their tray icons again: "
            + string.Join(", ", programs) + ".");
    }

    /// <summary>True when the process started at or after <paramref name="since"/> (false when that cannot be read).</summary>
    private static bool StartedSince(uint processId, DateTime since)
    {
        try
        {
            using Process process = Process.GetProcessById((int)processId);
            return process.StartTime >= since;
        }
        catch (Exception)
        {
            // Gone already, or a process Machitan may not look into: left alone.
            return false;
        }
    }

    private void OnExplorerStarted()
    {
        _explorer = 0;
        KeepFirst();

        // Apps answer Explorer's own message right away, some of them to Explorer's new window
        // before Machitan's is in front again; ask once more in a moment.
        _askAgainWhy = "Explorer started again";
        User32.SetTimer(_hwnd, TimerAskAgain, AskAgainMs, 0);
        Log.Info("Tray: Explorer started again; Machitan asks the apps for their tray icons again in 2 s.");
    }

    /// <summary>Tells every app "the taskbar was created", so each sends its tray icons again.</summary>
    private void AskAppsAgain(string why)
    {
        if (s_taskbarCreated == 0)
        {
            Log.Warn("Tray: Windows gave no \"TaskbarCreated\" message; apps cannot be asked for their icons, only new ones arrive.");
            return;
        }

        StartWatch(why);

        bool sent;
        _broadcasting = true;
        try
        {
            sent = User32.SendNotifyMessageW(User32.HWND_BROADCAST, s_taskbarCreated, 0, 0);
        }
        finally
        {
            _broadcasting = false;
        }

        int error = sent ? 0 : Marshal.GetLastPInvokeError();

        // Broadcasts skip message-only windows; some apps keep their tray icon on one.
        int direct = 0;
        nint window = 0;
        for (int guard = 0; guard < 8192; guard++)
        {
            window = User32.FindWindowEx(MessageOnlyParent, window, null, null);
            if (window == 0)
            {
                break;
            }

            if (User32.SendNotifyMessageW(window, s_taskbarCreated, 0, 0))
            {
                direct++;
            }
        }

        Log.Info((sent
                ? "Tray: asked all apps to send their tray icons again (" + why + ")"
                : "Tray: the broadcast asking apps for their tray icons failed (error " + error.ToString(CultureInfo.InvariantCulture) + ")")
            + "; also asked " + direct.ToString(CultureInfo.InvariantCulture) + " message-only window(s) directly.");
    }

    /// <summary>Explorer's own taskbar window, found again when it is gone (Explorer restarted).</summary>
    private nint ExplorerTray()
    {
        nint target = _explorer;
        if (target != 0 && User32.IsWindow(target))
        {
            return target;
        }

        target = ExplorerTaskbar.FindExplorerTray();
        _explorer = target;
        if (target == 0)
        {
            if (!_noExplorerNoted)
            {
                _noExplorerNoted = true;
                Log.Warn("Tray: Explorer's taskbar window is not there; tray messages stay with Machitan until it is back.");
            }
        }
        else
        {
            _noExplorerNoted = false;
            User32.GetWindowThreadProcessId(target, out uint process);
            _explorerProcess = process;
        }

        return target;
    }

    private nint ForwardCopyData(nint wParam, nint lParam)
    {
        nint target = ExplorerTray();
        if (target == 0)
        {
            return 0;
        }

        nint sent = User32.SendMessageTimeoutW(target, User32.WM_COPYDATA, wParam, lParam,
            User32.SMTO_ABORTIFHUNG, ForwardTimeoutMs, out nint result);
        return sent == 0 ? 0 : result;
    }

    private nint Forward(uint message, nint wParam, nint lParam)
    {
        nint target = ExplorerTray();
        if (target == 0)
        {
            return 0;
        }

        if (User32.InSendMessage())
        {
            nint sent = User32.SendMessageTimeoutW(target, message, wParam, lParam,
                User32.SMTO_ABORTIFHUNG, ForwardTimeoutMs, out nint result);
            return sent == 0 ? 0 : result;
        }

        User32.PostMessageW(target, message, wParam, lParam);
        return 0;
    }

    /// <summary>Drops icons whose window is gone (an app that ended without removing its icon).</summary>
    private void Prune()
    {
        bool changed = false;
        lock (_gate)
        {
            for (int i = _order.Count - 1; i >= 0; i--)
            {
                Entry entry = _order[i];
                if (entry.Window != 0 && !User32.IsWindow(entry.Window))
                {
                    _entries.Remove(entry.Key);
                    _order.RemoveAt(i);
                    changed = true;
                }
            }
        }

        if (changed)
        {
            MarkChanged();
        }
    }

    /// <summary>
    /// The first look, a few seconds after start, for the log: what arrived (with each icon's
    /// program), what was left out, and which running programs Windows has tray records for but
    /// sent nothing (yet). The slow part (programs, records) runs off the tray thread.
    /// </summary>
    private void Report()
    {
        _reported = true;
        var shown = new List<(string Name, uint Owner, bool Clicks)>();
        var windowsOwn = new List<string>();
        var explorerIcons = new List<string>();
        uint explorerProcess = _explorerProcess;
        int hiddenCount = 0;
        int noPicture = 0;
        lock (_gate)
        {
            foreach (Entry entry in _order)
            {
                string name = entry.Tip.Length > 0 ? OneLine(entry.Tip) : "(no tooltip)";
                if (entry.Owner != 0 && entry.Owner == explorerProcess)
                {
                    explorerIcons.Add(name
                        + (entry.Guid != Guid.Empty ? " {" + entry.Guid.ToString("D", CultureInfo.InvariantCulture) + "}" : " (no ID)")
                        + (entry.Callback != 0 ? ", clicks" : ", no clicks")
                        + (entry.Hidden ? ", hidden by Windows" : IsWindowsOwn(entry) ? ", left out" : ", shown"));
                }

                if (entry.Hidden)
                {
                    hiddenCount++;
                }
                else if (IsWindowsOwn(entry))
                {
                    windowsOwn.Add(name);
                }
                else
                {
                    if (entry.Picture is null)
                    {
                        noPicture++;
                    }

                    shown.Add((name, entry.Owner, entry.Callback != 0 && entry.Window != 0));
                }
            }
        }

        int stepped = _steppedInFront;
        bool explorerFound = _explorer != 0;
        _ = Task.Run(() =>
        {
            try
            {
                var text = new StringBuilder("Tray (first look): Machitan receives the tray. ")
                    .Append(shown.Count.ToString(CultureInfo.InvariantCulture)).Append(" icon(s) shown");
                if (windowsOwn.Count > 0)
                {
                    text.Append(", ").Append(windowsOwn.Count.ToString(CultureInfo.InvariantCulture))
                        .Append(" of Windows' own left out");
                }

                if (hiddenCount > 0)
                {
                    text.Append(", ").Append(hiddenCount.ToString(CultureInfo.InvariantCulture)).Append(" hidden by their apps");
                }

                if (noPicture > 0)
                {
                    text.Append("; ").Append(noPicture.ToString(CultureInfo.InvariantCulture)).Append(" without a readable picture");
                }

                text.Append("; Machitan's window stepped in front of Explorer's ")
                    .Append(stepped.ToString(CultureInfo.InvariantCulture)).Append(" time(s); Explorer's taskbar window ")
                    .Append(explorerFound ? "found" : "NOT found").Append('.');

                var programsShown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (shown.Count > 0)
                {
                    var parts = new List<string>();
                    foreach ((string name, uint owner, bool clicks) in shown)
                    {
                        string program = ProgramName(owner);
                        programsShown.Add(Path.GetFileNameWithoutExtension(program));
                        parts.Add(name + " (" + program + (clicks ? ")" : ", takes no clicks)"));
                    }

                    text.Append("\n  Shown: ").Append(string.Join(" | ", parts));
                }

                if (windowsOwn.Count > 0)
                {
                    text.Append("\n  Left out (Windows' own; Windows shows these in its quick settings): ")
                        .Append(string.Join(" | ", windowsOwn));
                }

                if (explorerIcons.Count > 0)
                {
                    text.Append("\n  Explorer's own icons: ").Append(string.Join(" | ", explorerIcons));
                }

                List<string> missing = RunningWithRecords(programsShown);
                text.Append("\n  Running programs Windows has tray records for that sent no icon (yet): ")
                    .Append(missing.Count > 0 ? string.Join(", ", missing) : "none")
                    .Append(missing.Count > 0 ? " (some may have no tray icon on now; the others can show only once they next send theirs)." : ".");
                Log.Info(text.ToString());
            }
            catch (Exception ex)
            {
                Log.Warn("Tray: the first look could not be written.", ex);
            }
        });
    }

    /// <summary>
    /// The programs (file names) that have a record in Windows' tray records and are running
    /// now, except those already in <paramref name="received"/>.
    /// </summary>
    private static List<string> RunningWithRecords(HashSet<string> received)
    {
        var recorded = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using (RegistryKey? records = Registry.CurrentUser.OpenSubKey(RecordsKey))
        {
            if (records is not null)
            {
                foreach (string name in records.GetSubKeyNames())
                {
                    using RegistryKey? record = records.OpenSubKey(name);
                    if (record?.GetValue("ExecutablePath") is string path && path.Length > 0)
                    {
                        string file = Path.GetFileName(path);
                        recorded.TryAdd(Path.GetFileNameWithoutExtension(file), file);
                    }
                }
            }
        }

        var running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Process process in Process.GetProcesses())
        {
            using (process)
            {
                running.Add(process.ProcessName);
            }
        }

        string self;
        using (Process current = Process.GetCurrentProcess())
        {
            self = current.ProcessName;
        }

        var missing = new List<string>();
        foreach (KeyValuePair<string, string> pair in recorded)
        {
            if (running.Contains(pair.Key) && !received.Contains(pair.Key)
                && !string.Equals(pair.Key, "explorer", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(pair.Key, self, StringComparison.OrdinalIgnoreCase))
            {
                missing.Add(pair.Value);
            }
        }

        missing.Sort(StringComparer.OrdinalIgnoreCase);
        return missing;
    }

    /// <summary>A process's program file name, for the log.</summary>
    private static string ProgramName(uint processId)
    {
        if (processId == 0)
        {
            return "no window";
        }

        string? path = WindowInfo.GetProcessPath(processId);
        return path is null ? "process " + processId.ToString(CultureInfo.InvariantCulture) : Path.GetFileName(path);
    }

    // ---------------------------------------------------------------- both threads

    private Entry? Find(nint window, uint id, Guid guid)
    {
        if (guid != Guid.Empty && _entries.TryGetValue(KeyFor(0, 0, guid), out Entry? byGuid))
        {
            return byGuid;
        }

        if (window == 0)
        {
            return null;
        }

        if (_entries.TryGetValue(KeyFor(window, id, Guid.Empty), out Entry? byWindow))
        {
            return byWindow;
        }

        // An icon added with a GUID and now named by its window and number.
        foreach (Entry entry in _order)
        {
            if (entry.Window == window && entry.Id == id)
            {
                return entry;
            }
        }

        return null;
    }

    /// <summary>
    /// One of Windows' own old icons (volume, network, battery): one of their IDs, or made by
    /// Explorer itself with no way to click it. Inside _gate.
    /// </summary>
    private bool IsWindowsOwn(Entry entry)
    {
        if (entry.Guid != Guid.Empty && Array.IndexOf(QuickSettingsIcons, entry.Guid) >= 0)
        {
            return true;
        }

        if (entry.Callback != 0)
        {
            return false;
        }

        uint explorer = _explorerProcess;
        return entry.Window == 0 || (entry.Owner != 0 && entry.Owner == explorer);
    }

    private void MarkChanged()
    {
        lock (_gate)
        {
            if (_publishQueued)
            {
                return;
            }

            _publishQueued = true;
        }

        if (!_queue.TryEnqueue(() =>
            {
                if (!_disposed)
                {
                    _publish.Start();
                }
            }))
        {
            lock (_gate)
            {
                _publishQueued = false;
            }
        }
    }

    /// <summary>UI thread: the icons as they are now, then <see cref="Changed"/>.</summary>
    private void Publish()
    {
        if (_disposed)
        {
            return;
        }

        var icons = new List<TrayIcon>();
        lock (_gate)
        {
            _publishQueued = false;
            foreach (Entry entry in _order)
            {
                if (!entry.Hidden && !IsWindowsOwn(entry))
                {
                    icons.Add(new TrayIcon(entry.Key, entry.Tip, entry.Picture, entry.Stamp, StableKeyOf(entry)));
                }
            }
        }

        Icons = icons;
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warn("Tray: showing the new icons failed.", ex);
        }
    }

    private void Notify(nint window, uint callback, uint id, uint version, uint mouseMessage)
    {
        nint wParam;
        nint lParam;
        if (version >= 4)
        {
            // Version 4: the pointer in wParam; the event (low) and the icon's number (high) in lParam.
            User32.GetCursorPos(out POINT point);
            wParam = Pack(point.X, point.Y);
            lParam = Pack((int)mouseMessage, (int)id);
        }
        else
        {
            wParam = (nint)id;
            lParam = (nint)mouseMessage;
        }

        if (!User32.SendNotifyMessageW(window, callback, wParam, lParam) && !_clickFailNoted)
        {
            _clickFailNoted = true;
            Log.Warn("Tray: a click could not be passed to its app (error "
                + Marshal.GetLastPInvokeError().ToString(CultureInfo.InvariantCulture) + "; an app running as administrator may refuse it).");
        }
    }

    private static IconPixels? ReadPicture(nint icon)
    {
        try
        {
            return IconReader.FromIconHandle(icon);
        }
        catch (Exception)
        {
            // An icon the app destroyed already, or one GDI cannot read: no picture.
            return null;
        }
    }

    private static bool SamePicture(IconPixels? a, IconPixels? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }

        return a.Width == b.Width && a.Height == b.Height
            && new ReadOnlySpan<byte>(a.Bgra).SequenceEqual(new ReadOnlySpan<byte>(b.Bgra));
    }

    private static string ReadTip(byte* p, int length)
    {
        int available = Math.Min(TipChars, (length - OffTip) / 2);
        if (available <= 0)
        {
            return string.Empty;
        }

        var text = (char*)(p + OffTip);
        int count = 0;
        while (count < available && text[count] != '\0')
        {
            count++;
        }

        return new string(text, 0, count).Replace("\r", string.Empty, StringComparison.Ordinal).Trim();
    }

    private static uint ReadUInt(byte* p, int offset)
    {
        var at = (uint*)(p + offset);
        return at[0];
    }

    private static Guid ReadGuid(byte* p, int offset)
    {
        var at = (Guid*)(p + offset);
        return at[0];
    }

    /// <summary>Two 16-bit values in one, low first (as MAKELPARAM).</summary>
    private static nint Pack(int low, int high)
    {
        return unchecked((nint)(int)(((uint)(ushort)high << 16) | (ushort)low));
    }

    /// <summary>
    /// A name for the icon that outlives its window: its GUID, else its program's file name
    /// and the icon's number. Found once per icon (the program from its owner process).
    /// </summary>
    private static string StableKeyOf(Entry entry)
    {
        if (entry.Guid != Guid.Empty)
        {
            return "g:" + entry.Guid.ToString("N", CultureInfo.InvariantCulture);
        }

        if (entry.Program is null && entry.Owner != 0)
        {
            string? path = WindowInfo.GetProcessPath(entry.Owner);
            entry.Program = path is null ? string.Empty : Path.GetFileName(path).ToLowerInvariant();
        }

        return string.IsNullOrEmpty(entry.Program)
            ? entry.Key
            : "p:" + entry.Program + ":" + entry.Id.ToString(CultureInfo.InvariantCulture);
    }

    private static string KeyFor(nint window, uint id, Guid guid)
    {
        return guid != Guid.Empty
            ? "g:" + guid.ToString("N", CultureInfo.InvariantCulture)
            : "w:" + ((long)window).ToString("X", CultureInfo.InvariantCulture) + ":" + id.ToString(CultureInfo.InvariantCulture);
    }

    private static string OneLine(string text)
    {
        return text.Replace("\n", " / ", StringComparison.Ordinal);
    }

    /// <summary>What Machitan knows of one icon.</summary>
    private sealed class Entry
    {
        internal Entry(string key)
        {
            Key = key;
        }

        internal string Key { get; }

        internal nint Window { get; set; }

        internal uint Id { get; set; }

        /// <summary>The process of the icon's window.</summary>
        internal uint Owner { get; set; }

        internal Guid Guid { get; set; }

        internal uint Callback { get; set; }

        internal uint Version { get; set; }

        internal string Tip { get; set; } = string.Empty;

        internal IconPixels? Picture { get; set; }

        internal int Stamp { get; set; }

        internal bool Hidden { get; set; }

        /// <summary>The owner program's file name (lower case), once looked up; empty when unknown.</summary>
        internal string? Program { get; set; }
    }
}
