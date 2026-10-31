using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Machitan.Native;

/// <summary>
/// What the tray listener should do with the answer to the app's message.
/// </summary>
internal enum TrayReplyAdvice
{
    None,

    /// <summary>
    /// Tell the app its NIM_MODIFY failed (Explorer still got the update). An app
    /// whose icon we never saw being added then adds it again, and that add
    /// carries the callback message and, right after, the version we are missing.
    /// </summary>
    FailModify,

    /// <summary>
    /// The app is adding an icon Explorer already has. Explorer refuses a second
    /// add; tell the app it worked.
    /// </summary>
    ReportSuccess,
}

/// <summary>
/// One app's notification-area icon, as last described by its owner.
/// </summary>
internal sealed class TrayIconEntry
{
    public uint HwndRaw;          // owner window (32-bit handle, as the shell sends it)
    public uint Uid;
    public Guid Guid;
    public uint Callback;         // callback message the owner wants clicks on (used from i9)
    public uint Version;          // NOTIFYICON_VERSION the owner asked for
    public bool VersionAnnounced; // true once we saw its NIM_SETVERSION (apps that started before us never tell us again)
    public bool SeenAdd;          // we saw its NIM_ADD (so we know its callback message and version)
    public int RelearnAttempts;   // how many times we told it a MODIFY failed so that it adds itself again
    public DateTime LastRelearn = DateTime.MinValue;
    public bool AwaitingReadd;
    public bool SkipLogged;       // the "cannot click" log line is written once per icon
    public uint State;
    public string Tip = "";
    public string Owner = "?";
    public bool Hidden;           // NIS_HIDDEN
    public bool IsSystem;         // Windows' own volume / network / power icons
    public uint IconHandle;
    public uint PendingIcon;      // newest handle seen during a burst; read on the next tick
    public byte[]? Png;
    public DateTime IconReadAt = DateTime.MinValue;
    public DateTime LastLeftDown = DateTime.MinValue;   // for double-click detection, per icon

    private ImageSource? _image;
    private byte[]? _imagePng;

    public bool HasGuid => Guid != Guid.Empty;

    /// <summary>We know how to reach the app. Without its callback message a click cannot be delivered.</summary>
    public bool CanClick => Callback != 0;

    /// <summary>Shown in the chevron pop-up: has a picture, not hidden, not a system icon.</summary>
    public bool IsVisible => !Hidden && !IsSystem && Png != null;

    /// <summary>UI-thread only. Built once per picture, then reused.</summary>
    public ImageSource? GetImage()
    {
        if (Png == null)
            return null;

        if (!ReferenceEquals(_imagePng, Png))
        {
            var image = new BitmapImage();
            _ = SetSourceAsync(image, Png);
            _image = image;
            _imagePng = Png;
        }

        return _image;
    }

    private static async Task SetSourceAsync(BitmapImage image, byte[] png)
    {
        try
        {
            using var memory = new MemoryStream(png);
            using var stream = memory.AsRandomAccessStream();
            await image.SetSourceAsync(stream);
        }
        catch
        {
            // The icon simply stays empty.
        }
    }
}

/// <summary>
/// The list of notification-area icons that the tray listener has seen.
/// Fed by <see cref="TrayListener"/> on the UI thread; read by the UI thread.
/// Icons are identified the way the shell does it: by GUID when the app gave
/// one, otherwise by owner window + id.
/// </summary>
internal static class TrayIconStore
{
    private const uint NIF_MESSAGE = 0x01;
    private const uint NIF_ICON = 0x02;
    private const uint NIF_TIP = 0x04;
    private const uint NIF_STATE = 0x08;
    private const uint NIF_GUID = 0x20;
    private const uint NIS_HIDDEN = 0x01;

    // Windows' own icons that Machitan already covers with its quick-settings
    // and battery buttons: volume, network, power.
    private static readonly HashSet<Guid> SystemGuids = new()
    {
        new Guid("7820ae73-23e3-4229-82c1-e41cb67d5b9c"), // volume
        new Guid("7820ae74-23e3-4229-82c1-e41cb67d5b9c"), // network
        new Guid("7820ae75-23e3-4229-82c1-e41cb67d5b9c"), // power
    };

    private static readonly List<TrayIconEntry> Entries = new();
    private static int _lastVisibleCount;
    private const double MinIconReadGapMs = 200;
    private const int MaxRelearnAttempts = 3;
    private const double RelearnGapSeconds = 10;
    private static int _pngCount;
    private static double _pngTotalMs, _pngMaxMs;

    /// <summary>How long turning icon handles into pictures took (for the log).</summary>
    public static string TimingSummary => _pngCount == 0
        ? "none"
        : FormattableString.Invariant($"{_pngCount} reads, avg {_pngTotalMs / _pngCount:F1} ms, max {_pngMaxMs:F1} ms");

    /// <summary>Raised (UI thread) when the number of shown icons changes.</summary>
    public static event Action? Changed;

    public static int VisibleCount => Entries.Count(e => e.IsVisible);

    public static List<TrayIconEntry> GetVisible() => Entries.Where(e => e.IsVisible).ToList();

    public static void Clear()
    {
        Entries.Clear();
        NotifyIfCountChanged();
    }

    /// <summary>Drops icons whose owner window no longer exists (the app died without deleting its icon).</summary>
    public static void Prune(Func<uint, bool> ownerIsAlive)
    {
        if (Entries.RemoveAll(e => !ownerIsAlive(e.HwndRaw)) > 0)
            NotifyIfCountChanged();
    }

    /// <summary>
    /// Applies one SHELLTRAYDATA payload (see TrayListener for the layout).
    /// Every read is bounds-checked against <paramref name="cb"/>.
    /// </summary>
    public static TrayReplyAdvice ApplyMessage(IntPtr p, int cb, Func<uint, string> ownerOf, out string? note)
    {
        note = null;
        if (p == IntPtr.Zero || cb < 24)
            return TrayReplyAdvice.None;

        uint nim = U32(p, cb, 4);
        uint hwnd = U32(p, cb, 12);
        uint uid = U32(p, cb, 16);
        uint flags = U32(p, cb, 20);
        if (hwnd == 0)
            return TrayReplyAdvice.None;

        Guid guid = Guid.Empty;
        if ((flags & NIF_GUID) != 0 && cb >= 960)
        {
            var bytes = new byte[16];
            Marshal.Copy(IntPtr.Add(p, 944), bytes, 0, 16);
            guid = new Guid(bytes);
        }

        TrayIconEntry? entry = Find(hwnd, uid, guid);
        TrayReplyAdvice advice = TrayReplyAdvice.None;

        switch (nim)
        {
            case 0: // NIM_ADD
            case 1: // NIM_MODIFY
                if (entry == null)
                {
                    entry = new TrayIconEntry { Owner = ownerOf(hwnd) };
                    Entries.Add(entry);
                }

                // A repeated ADD (the shell's volume icon does this on every
                // change) is just an update.
                entry.HwndRaw = hwnd;
                entry.Uid = uid;
                if (guid != Guid.Empty)
                    entry.Guid = guid;
                ApplyFields(entry, p, cb, flags);

                if (nim == 0)
                {
                    entry.SeenAdd = true;
                    if (entry.AwaitingReadd)
                    {
                        entry.AwaitingReadd = false;
                        advice = TrayReplyAdvice.ReportSuccess;
                        note = $"app={entry.Owner} id={entry.Uid} added itself again";
                    }
                }
                else if (WantsRelearn(entry))
                {
                    entry.RelearnAttempts++;
                    entry.LastRelearn = DateTime.UtcNow;
                    entry.AwaitingReadd = true;
                    advice = TrayReplyAdvice.FailModify;
                    note = $"app={entry.Owner} id={entry.Uid} attempt={entry.RelearnAttempts}";
                }
                break;

            case 2: // NIM_DELETE
                if (entry != null)
                    Entries.Remove(entry);
                break;

            case 4: // NIM_SETVERSION
                if (entry != null && cb >= 812)
                {
                    entry.Version = U32(p, cb, 808);
                    entry.VersionAnnounced = true;
                }
                break;
        }

        NotifyIfCountChanged();
        return advice;
    }

    // An icon we first met through a MODIFY (we never saw it being added) has no
    // callback message and no version in our table; only the app can tell us
    // again, and an app that is told "modify failed" typically adds the icon
    // again. Windows' own icons (owned by Explorer) are left alone. A few tries,
    // spaced out, so an app that ignores this is not nagged.
    private static bool WantsRelearn(TrayIconEntry e)
        => !e.SeenAdd
           && !e.IsSystem
           && !string.Equals(e.Owner, "explorer", StringComparison.OrdinalIgnoreCase)
           && e.RelearnAttempts < MaxRelearnAttempts
           && (DateTime.UtcNow - e.LastRelearn).TotalSeconds >= RelearnGapSeconds;

    private static TrayIconEntry? Find(uint hwnd, uint uid, Guid guid)
    {
        foreach (var e in Entries)
        {
            if ((guid != Guid.Empty && e.Guid == guid) || (e.HwndRaw == hwnd && e.Uid == uid))
                return e;
        }

        return null;
    }

    private static void ApplyFields(TrayIconEntry e, IntPtr p, int cb, uint flags)
    {
        if ((flags & NIF_MESSAGE) != 0)
            e.Callback = U32(p, cb, 24);

        if ((flags & NIF_TIP) != 0 && cb >= 288)
        {
            string tip = Marshal.PtrToStringUni(IntPtr.Add(p, 32), 128) ?? "";
            int end = tip.IndexOf('\0');
            e.Tip = end >= 0 ? tip[..end] : tip;
        }

        if ((flags & NIF_STATE) != 0 && cb >= 296)
        {
            uint state = U32(p, cb, 288);
            uint mask = U32(p, cb, 292);
            e.State = (e.State & ~mask) | (state & mask);
            e.Hidden = (e.State & NIS_HIDDEN) != 0;
        }

        e.IsSystem = e.HasGuid && SystemGuids.Contains(e.Guid);

        if ((flags & NIF_ICON) != 0 && cb >= 32 && !e.IsSystem)
            UpdateIcon(e, U32(p, cb, 28));
    }

    private static void UpdateIcon(TrayIconEntry e, uint handle)
    {
        if (handle == 0)
        {
            e.Png = null;
            e.IconHandle = 0;
            e.PendingIcon = 0;
            return;
        }

        // Same picture again: nothing to do.
        if (handle == e.IconHandle && e.Png != null)
        {
            e.PendingIcon = 0;
            return;
        }

        // A burst of changes: keep only the newest handle and read it on the
        // next tick (FlushPending), so the last picture is never lost.
        if (e.Png != null && (DateTime.UtcNow - e.IconReadAt).TotalMilliseconds < MinIconReadGapMs)
        {
            e.PendingIcon = handle;
            return;
        }

        e.PendingIcon = 0;
        ReadIcon(e, handle);
    }

    private static void ReadIcon(TrayIconEntry e, uint handle)
    {
        e.IconReadAt = DateTime.UtcNow;

        var watch = Stopwatch.StartNew();
        byte[]? png = TrayIconPicture.ToPng(handle);
        watch.Stop();

        double ms = watch.Elapsed.TotalMilliseconds;
        _pngCount++;
        _pngTotalMs += ms;
        if (ms > _pngMaxMs)
            _pngMaxMs = ms;

        // If the handle is already gone, keep the picture we have.
        if (png != null)
        {
            e.Png = png;
            e.IconHandle = handle;
        }
    }

    /// <summary>Reads the newest picture of icons that changed during a burst. Called every tick.</summary>
    public static void FlushPending()
    {
        foreach (var e in Entries)
        {
            if (e.PendingIcon == 0)
                continue;

            uint handle = e.PendingIcon;
            e.PendingIcon = 0;
            ReadIcon(e, handle);
        }

        NotifyIfCountChanged();
    }

    private static void NotifyIfCountChanged()
    {
        int count = VisibleCount;
        if (count == _lastVisibleCount)
            return;

        _lastVisibleCount = count;
        Changed?.Invoke();
    }

    private static uint U32(IntPtr p, int cb, int offset)
        => offset + 4 <= cb ? unchecked((uint)Marshal.ReadInt32(p, offset)) : 0;
}

/// <summary>
/// Turns another process's icon handle into PNG bytes, the same way
/// <see cref="IconLoader"/> does for app icons: copy the handle, clone it into
/// a System.Drawing icon, release the copy at once.
/// </summary>
internal static class TrayIconPicture
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CopyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    public static byte[]? ToPng(uint hIconRaw)
    {
        IntPtr copy = IntPtr.Zero;
        try
        {
            copy = CopyIcon(new IntPtr(unchecked((int)hIconRaw)));
            if (copy == IntPtr.Zero)
                return null;

            using var icon = (Icon)Icon.FromHandle(copy).Clone();
            DestroyIcon(copy);
            copy = IntPtr.Zero;

            using var bitmap = icon.ToBitmap();
            using var memory = new MemoryStream();
            bitmap.Save(memory, ImageFormat.Png);
            return memory.ToArray();
        }
        catch
        {
            return null;
        }
        finally
        {
            if (copy != IntPtr.Zero)
                DestroyIcon(copy);
        }
    }
}
