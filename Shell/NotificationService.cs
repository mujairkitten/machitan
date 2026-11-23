using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Machitan.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace Machitan.Shell;

/// <summary>One notification as Windows' notification centre holds it right now.</summary>
internal sealed record NotificationEntry(uint Id, string AppName, string AppId, DateTimeOffset Time, string Title, string Body);

/// <summary>
/// A notification's pictures, ready to show (Part 25): the big picture at the top ("hero"), the
/// picture beside the text (the app's picture for that notification, a person's photo, round
/// when the notification asks), pictures below the text, and the small credit line.
/// </summary>
internal sealed record NotificationPictureView(
    ImageSource? Hero,
    ImageSource? Logo,
    bool LogoCircle,
    IReadOnlyList<ImageSource> Inline,
    string Attribution);

/// <summary>Whether Windows lets Machitan read the user's notifications.</summary>
internal enum NotificationAccess
{
    /// <summary>Not asked yet.</summary>
    Unknown,
    Allowed,

    /// <summary>The user (or Windows) said no: Settings > Privacy &amp; security > Notifications.</summary>
    NotAllowed,

    /// <summary>Windows gave no notification reader at all.</summary>
    Unavailable,
}

/// <summary>
/// Windows' own list of notifications (its notification centre), for Dynaland's notification
/// tray (CHANGELOG Part 24). Machitan has no package identity, so Windows gives it no live
/// updates (Part 23's check: NotificationChanged fails with "Element not found"), but it does
/// let it read the list: the list is read again every second while Dynaland's page with it is
/// open, else every few seconds, and only while Dynaland is on screen.
///
/// No notification history: <see cref="Current"/> is only what Windows holds now; what Windows
/// no longer holds is gone from Machitan too. Nothing is written anywhere, and the log never
/// gets a notification's text, title or app. Dismissing or clearing here does it in Windows.
/// Without package identity Windows allows reading and dismissing one by one, but not its
/// live-update event or its own "clear all".
/// </summary>
internal sealed class NotificationService : IDisposable
{
    private const int FastMs = 1000;
    private const int SlowMs = 4000;
    private const int PicturePixels = 40;

    // A notification's own pictures (Part 25): looked up a few times, a few seconds apart, as
    // Windows may write a new notification's description a moment after showing it.
    private const int MaxPictureTries = 3;
    private const long PictureRetryMs = 3000;
    private const int WidePicturePixels = 720;
    private const int LogoPicturePixels = 96;

    private readonly DispatcherQueueTimer _timer;

    /// <summary>Each app's picture, by app ID: pictures of apps, not of notifications.</summary>
    private readonly Dictionary<string, BitmapImage?> _pictures = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The pictures of notifications Windows' list holds now, by Windows' id (dropped when one goes).</summary>
    private readonly Dictionary<uint, NotificationPictureView> _notePictures = new();
    private readonly Dictionary<uint, (int Tries, long At)> _pictureTries = new();
    private bool _lookingUp;
    private UserNotificationListener? _listener;
    private bool _starting;
    private bool _active;
    private bool _fast;
    private bool _reading;
    private bool _readAgain;
    private bool _disposed;
    private bool _failing;

    internal NotificationService(DispatcherQueue queue)
    {
        _timer = queue.CreateTimer();
        _timer.IsRepeating = true;
        _timer.Interval = TimeSpan.FromMilliseconds(SlowMs);
        _timer.Tick += (_, _) => _ = ReadAsync();
    }

    /// <summary>Raised on the UI thread when <see cref="Current"/> or <see cref="Access"/> changed.</summary>
    internal event Action? Changed;

    /// <summary>Windows' notifications now, newest first.</summary>
    internal IReadOnlyList<NotificationEntry> Current { get; private set; } = Array.Empty<NotificationEntry>();

    internal NotificationAccess Access { get; private set; } = NotificationAccess.Unknown;

    /// <summary>Grows when a notification's pictures arrive, so Dynaland redraws its rows.</summary>
    internal int PicturesVersion { get; private set; }

    /// <summary>
    /// Reading on or off (Dynaland on screen or not). The first time on, Windows is asked for
    /// access (on the UI thread, as it requires; it may ask the user).
    /// </summary>
    internal bool Active
    {
        get => _active;
        set
        {
            if (_disposed || value == _active)
            {
                return;
            }

            _active = value;
            if (!value)
            {
                _timer.Stop();
                return;
            }

            if (_listener is null)
            {
                // Asked once; when Windows has no reader at all, not again this run.
                if (Access != NotificationAccess.Unavailable)
                {
                    _ = StartAsync();
                }

                return;
            }

            _timer.Start();
            _ = ReadAsync();
        }
    }

    /// <summary>Every second while someone looks at the list (Dynaland's page open), else every few seconds.</summary>
    internal bool Fast
    {
        get => _fast;
        set
        {
            if (_disposed || value == _fast)
            {
                return;
            }

            _fast = value;
            _timer.Interval = TimeSpan.FromMilliseconds(value ? FastMs : SlowMs);
            if (value && _active && _listener is not null)
            {
                _ = ReadAsync();
            }
        }
    }

    /// <summary>A notification's own pictures, or null when it has none (or they are not read yet).</summary>
    internal NotificationPictureView? PicturesFor(uint id)
    {
        return _notePictures.TryGetValue(id, out NotificationPictureView? pictures) ? pictures : null;
    }

    /// <summary>A picture from bytes, decoded in the background (it shows when ready).</summary>
    internal static BitmapImage BitmapFrom(byte[] bytes, int decodePixels)
    {
        var bitmap = new BitmapImage
        {
            DecodePixelWidth = decodePixels,
            DecodePixelType = DecodePixelType.Physical,
        };
        _ = FillBitmapAsync(bitmap, bytes);
        return bitmap;
    }

    /// <summary>The app's picture (loads in the background; shows when ready), or null.</summary>
    internal ImageSource? PictureFor(string appId)
    {
        return appId.Length > 0 && _pictures.TryGetValue(appId, out BitmapImage? picture) ? picture : null;
    }

    /// <summary>Dismisses one notification, in Windows too.</summary>
    internal void Dismiss(uint id)
    {
        UserNotificationListener? listener = _listener;
        if (listener is null || _disposed)
        {
            return;
        }

        try
        {
            listener.RemoveNotification(id);
            Log.Info("Notifications: one dismissed from Dynaland.");
        }
        catch (Exception ex)
        {
            Log.Warn("Notifications: Windows did not dismiss the notification.", ex);
        }

        // Gone at once here; the next read confirms it.
        var left = new List<NotificationEntry>(Current.Count);
        foreach (NotificationEntry entry in Current)
        {
            if (entry.Id != id)
            {
                left.Add(entry);
            }
        }

        Current = left;
        ForgetGone();
        Changed?.Invoke();
        _ = ReadAsync();
    }

    /// <summary>
    /// Clears every notification, in Windows too (Dynaland shows them all, so nothing unseen
    /// goes). Windows' own "clear all" (ClearNotifications) needs package identity, which
    /// Machitan lacks (0x80070490, "Element not found"), so each one is dismissed in turn, as
    /// the X does.
    /// </summary>
    internal void ClearAll()
    {
        UserNotificationListener? listener = _listener;
        if (listener is null || _disposed)
        {
            return;
        }

        int cleared = 0;
        var left = new List<NotificationEntry>();
        foreach (NotificationEntry entry in Current)
        {
            try
            {
                listener.RemoveNotification(entry.Id);
                cleared++;
            }
            catch (Exception ex)
            {
                if (left.Count == 0)
                {
                    Log.Warn("Notifications: Windows did not dismiss a notification while clearing all.", ex);
                }

                left.Add(entry);
            }
        }

        Log.Info(string.Format(
            CultureInfo.InvariantCulture,
            "Notifications: clear all from Dynaland: {0} dismissed, {1} not.",
            cleared,
            left.Count));

        // Gone at once here; the next read confirms it.
        Current = left;
        ForgetGone();
        Changed?.Invoke();
        _ = ReadAsync();
    }

    public void Dispose()
    {
        _disposed = true;
        _active = false;
        _timer.Stop();
        Changed = null;
    }

    private async Task StartAsync()
    {
        if (_starting || _disposed)
        {
            return;
        }

        _starting = true;
        try
        {
            UserNotificationListener listener;
            try
            {
                listener = UserNotificationListener.Current;
            }
            catch (Exception ex)
            {
                Log.Warn("Notifications: Windows gave no notification reader; Dynaland shows none.", ex);
                SetAccess(NotificationAccess.Unavailable);
                return;
            }

            UserNotificationListenerAccessStatus status;
            try
            {
                status = await listener.RequestAccessAsync();
            }
            catch (Exception ex)
            {
                Log.Warn("Notifications: asking Windows for access failed; Dynaland shows none.", ex);
                SetAccess(NotificationAccess.Unavailable);
                return;
            }

            if (_disposed)
            {
                return;
            }

            _listener = listener;
            SetAccess(status == UserNotificationListenerAccessStatus.Allowed ? NotificationAccess.Allowed : NotificationAccess.NotAllowed);
            Log.Info("Notifications: access " + status + "; Windows' list is read every "
                + (FastMs / 1000).ToString(CultureInfo.InvariantCulture) + " s while Dynaland's page is open, else every "
                + (SlowMs / 1000).ToString(CultureInfo.InvariantCulture) + " s.");

            if (_active)
            {
                _timer.Start();
                await ReadAsync();
            }
        }
        finally
        {
            _starting = false;
        }
    }

    private void SetAccess(NotificationAccess access)
    {
        if (access == Access)
        {
            return;
        }

        Access = access;
        if (access != NotificationAccess.Allowed)
        {
            Current = Array.Empty<NotificationEntry>();
        }

        Changed?.Invoke();
    }

    private async Task ReadAsync()
    {
        UserNotificationListener? listener = _listener;
        if (listener is null || _disposed || !_active)
        {
            return;
        }

        if (_reading)
        {
            // One more read when this one ends (a dismiss happened meanwhile).
            _readAgain = true;
            return;
        }

        _reading = true;
        try
        {
            do
            {
                _readAgain = false;
                await ReadOnceAsync(listener);
            }
            while (_readAgain && !_disposed && _active);
        }
        finally
        {
            _reading = false;
        }
    }

    private async Task ReadOnceAsync(UserNotificationListener listener)
    {
        // Windows' answer may change in Settings while Machitan runs.
        NotificationAccess access;
        try
        {
            access = listener.GetAccessStatus() == UserNotificationListenerAccessStatus.Allowed
                ? NotificationAccess.Allowed
                : NotificationAccess.NotAllowed;
        }
        catch (Exception)
        {
            access = Access;
        }

        if (access != Access)
        {
            Log.Info("Notifications: access is now " + access + ".");
            SetAccess(access);
        }

        if (access != NotificationAccess.Allowed)
        {
            return;
        }

        IReadOnlyList<UserNotification> list;
        try
        {
            list = await listener.GetNotificationsAsync(NotificationKinds.Toast);
        }
        catch (Exception ex)
        {
            // Logged once, not every second.
            if (!_failing)
            {
                _failing = true;
                Log.Warn("Notifications: reading Windows' list failed; trying again.", ex);
            }

            return;
        }

        if (_failing)
        {
            _failing = false;
            Log.Info("Notifications: reading Windows' list works again.");
        }

        if (_disposed)
        {
            return;
        }

        var entries = new List<NotificationEntry>(list.Count);
        foreach (UserNotification item in list)
        {
            NotificationEntry? entry = EntryOf(item);
            if (entry is not null)
            {
                entries.Add(entry);
            }
        }

        entries.Sort((a, b) => b.Time.CompareTo(a.Time));
        if (!Same(entries, Current))
        {
            Current = entries;
            ForgetGone();
            Changed?.Invoke();
        }

        _ = LookUpPicturesAsync();
    }

    /// <summary>What Windows no longer holds is forgotten here too, pictures included.</summary>
    private void ForgetGone()
    {
        var shown = new HashSet<uint>();
        foreach (NotificationEntry entry in Current)
        {
            shown.Add(entry.Id);
        }

        foreach (uint id in new List<uint>(_notePictures.Keys))
        {
            if (!shown.Contains(id))
            {
                _notePictures.Remove(id);
            }
        }

        foreach (uint id in new List<uint>(_pictureTries.Keys))
        {
            if (!shown.Contains(id))
            {
                _pictureTries.Remove(id);
            }
        }
    }

    /// <summary>
    /// The pictures of notifications that have none yet, from Windows' notification database
    /// (NotificationPictures), on a worker thread; each notification is looked up at most a few
    /// times. Their pictures are decoded here, on the UI thread.
    /// </summary>
    private async Task LookUpPicturesAsync()
    {
        if (_lookingUp || _disposed)
        {
            return;
        }

        long now = Environment.TickCount64;
        var wanted = new List<NotificationEntry>();
        foreach (NotificationEntry entry in Current)
        {
            if (_notePictures.ContainsKey(entry.Id))
            {
                continue;
            }

            if (_pictureTries.TryGetValue(entry.Id, out (int Tries, long At) tried)
                && (tried.Tries >= MaxPictureTries || now - tried.At < PictureRetryMs))
            {
                continue;
            }

            wanted.Add(entry);
        }

        if (wanted.Count == 0)
        {
            return;
        }

        _lookingUp = true;
        try
        {
            Dictionary<uint, NotificationPictureBytes> found = await Task.Run(() => NotificationPictures.FindAsync(wanted));
            if (_disposed)
            {
                return;
            }

            var shown = new HashSet<uint>();
            foreach (NotificationEntry entry in Current)
            {
                shown.Add(entry.Id);
            }

            bool arrived = false;
            foreach (NotificationEntry entry in wanted)
            {
                if (!shown.Contains(entry.Id))
                {
                    continue;
                }

                if (found.TryGetValue(entry.Id, out NotificationPictureBytes? bytes))
                {
                    _notePictures[entry.Id] = ToView(bytes);
                    _pictureTries.Remove(entry.Id);
                    arrived = true;
                }
                else
                {
                    int tries = _pictureTries.TryGetValue(entry.Id, out (int Tries, long At) before) ? before.Tries + 1 : 1;
                    _pictureTries[entry.Id] = (tries, Environment.TickCount64);
                }
            }

            if (arrived)
            {
                PicturesVersion++;
                Changed?.Invoke();
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Notifications: the notifications' pictures could not be read.", ex);
        }
        finally
        {
            _lookingUp = false;
        }
    }

    private static NotificationPictureView ToView(NotificationPictureBytes bytes)
    {
        var inline = new List<ImageSource>(bytes.Inline.Count);
        foreach (byte[] picture in bytes.Inline)
        {
            inline.Add(BitmapFrom(picture, WidePicturePixels));
        }

        return new NotificationPictureView(
            bytes.Hero is null ? null : BitmapFrom(bytes.Hero, WidePicturePixels),
            bytes.Logo is null ? null : BitmapFrom(bytes.Logo, LogoPicturePixels),
            bytes.LogoCircle,
            inline,
            bytes.Attribution);
    }

    private static async Task FillBitmapAsync(BitmapImage bitmap, byte[] bytes)
    {
        try
        {
            using var memory = new InMemoryRandomAccessStream();
            await memory.WriteAsync(bytes.AsBuffer());
            memory.Seek(0);
            await bitmap.SetSourceAsync(memory);
        }
        catch (Exception ex)
        {
            Log.Warn("Notifications: a picture could not be decoded.", ex);
        }
    }

    private NotificationEntry? EntryOf(UserNotification item)
    {
        try
        {
            string appName = string.Empty;
            string appId = string.Empty;
            try
            {
                appName = item.AppInfo.DisplayInfo.DisplayName ?? string.Empty;
                appId = item.AppInfo.AppUserModelId ?? string.Empty;
            }
            catch (Exception)
            {
                // Some apps' notifications carry no app details: shown without them.
            }

            if (appId.Length > 0 && !_pictures.ContainsKey(appId))
            {
                _pictures[appId] = LoadPicture(item);
            }

            (string title, string body) = TextOf(item);
            return new NotificationEntry(item.Id, appName, appId, item.CreationTime, title, body);
        }
        catch (Exception)
        {
            // Gone while being read, or unreadable: left out of this read.
            return null;
        }
    }

    /// <summary>The first text is the title, the rest the body (as Windows shows a notification).</summary>
    private static (string Title, string Body) TextOf(UserNotification item)
    {
        NotificationBinding? binding = null;
        try
        {
            binding = item.Notification.Visual.GetBinding(KnownNotificationBindings.ToastGeneric);
            if (binding is null)
            {
                foreach (NotificationBinding any in item.Notification.Visual.Bindings)
                {
                    binding = any;
                    break;
                }
            }
        }
        catch (Exception)
        {
            binding = null;
        }

        if (binding is null)
        {
            return (string.Empty, string.Empty);
        }

        string title = string.Empty;
        var body = new List<string>();
        foreach (AdaptiveNotificationText text in binding.GetTextElements())
        {
            string line = (text.Text ?? string.Empty).Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (title.Length == 0)
            {
                title = line;
            }
            else
            {
                body.Add(line);
            }
        }

        return (title, string.Join("\n", body));
    }

    /// <summary>The app's picture from Windows, decoded small; null when Windows has none.</summary>
    private static BitmapImage? LoadPicture(UserNotification item)
    {
        RandomAccessStreamReference? reference;
        try
        {
            reference = item.AppInfo.DisplayInfo.GetLogo(new global::Windows.Foundation.Size(PicturePixels, PicturePixels));
        }
        catch (Exception)
        {
            return null;
        }

        if (reference is null)
        {
            return null;
        }

        var picture = new BitmapImage { DecodePixelWidth = PicturePixels };
        _ = FillPictureAsync(picture, reference);
        return picture;
    }

    private static async Task FillPictureAsync(BitmapImage picture, RandomAccessStreamReference reference)
    {
        try
        {
            using IRandomAccessStreamWithContentType stream = await reference.OpenReadAsync();
            await picture.SetSourceAsync(stream);
        }
        catch (Exception ex)
        {
            Log.Warn("Notifications: an app's picture could not be read.", ex);
        }
    }

    private static bool Same(List<NotificationEntry> a, IReadOnlyList<NotificationEntry> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i])
            {
                return false;
            }
        }

        return true;
    }
}
