using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Machitan.Native;

/// <summary>
/// One app's notification-area icon, as last described by its owner.
/// </summary>
internal sealed class TrayIconEntry
{
    public uint HwndRaw;          // owner window (32-bit handle, as the shell sends it)
    public uint Uid;
    public Guid Guid;
    public uint Callback;         // callback message the owner wants clicks on (used from i9)
    public uint Version;          // NOTIFYICON_VERSION the owner asked for (used from i9)
    public uint State;
    public string Tip = "";
    public string Owner = "?";
    public bool Hidden;           // NIS_HIDDEN
    public bool IsSystem;         // Windows' own volume / network / power icons
    public uint IconHandle;
    public byte[]? Png;
    public DateTime IconReadAt = DateTime.MinValue;

    private ImageSource? _image;
    private byte[]? _imagePng;

    public bool HasGuid => Guid != Guid.Empty;

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
    public static void ApplyMessage(IntPtr p, int cb, Func<uint, string> ownerOf)
    {
        if (p == IntPtr.Zero || cb < 24)
            return;

        uint nim = U32(p, cb, 4);
        uint hwnd = U32(p, cb, 12);
        uint uid = U32(p, cb, 16);
        uint flags = U32(p, cb, 20);
        if (hwnd == 0)
            return;

        Guid guid = Guid.Empty;
        if ((flags & NIF_GUID) != 0 && cb >= 960)
        {
            var bytes = new byte[16];
            Marshal.Copy(IntPtr.Add(p, 944), bytes, 0, 16);
            guid = new Guid(bytes);
        }

        TrayIconEntry? entry = Find(hwnd, uid, guid);

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
                break;

            case 2: // NIM_DELETE
                if (entry != null)
                    Entries.Remove(entry);
                break;

            case 4: // NIM_SETVERSION
                if (entry != null && cb >= 812)
                    entry.Version = U32(p, cb, 808);
                break;
        }

        NotifyIfCountChanged();
    }

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
            return;
        }

        // Same picture again: nothing to do. A burst of changes: skip, the
        // next message carries the newer picture.
        if (handle == e.IconHandle && e.Png != null)
            return;
        if (e.Png != null && (DateTime.UtcNow - e.IconReadAt).TotalMilliseconds < 200)
            return;

        e.IconReadAt = DateTime.UtcNow;
        byte[]? png = TrayIconPicture.ToPng(handle);
        if (png != null)
        {
            e.Png = png;
            e.IconHandle = handle;
        }
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
