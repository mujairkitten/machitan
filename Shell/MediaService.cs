using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Machitan.Core;
using Machitan.Interop;
using Microsoft.UI.Dispatching;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace Machitan.Shell;

/// <summary>What is playing, as Windows' media controls report it. All from the playing app.</summary>
internal sealed record MediaSnapshot(
    bool HasMedia,
    string Title,
    string Artist,
    string AppName,
    bool IsPlaying,
    bool CanPrevious,
    bool CanNext,
    bool CanPlayPause,
    bool HasTimeline,
    TimeSpan Position,
    TimeSpan Duration,
    DateTimeOffset PositionAt,
    IRandomAccessStreamReference? Art,
    string TrackKey,
    bool CanSeek)
{
    internal static readonly MediaSnapshot Nothing = new(
        false, string.Empty, string.Empty, string.Empty, false, false, false, false, false,
        TimeSpan.Zero, TimeSpan.Zero, DateTimeOffset.MinValue, null, string.Empty, false);

    /// <summary>The position now: the app's last report, moved on by the time since while playing.</summary>
    internal TimeSpan PositionNow
    {
        get
        {
            if (!HasTimeline)
            {
                return TimeSpan.Zero;
            }

            TimeSpan position = Position;
            if (IsPlaying && PositionAt > DateTimeOffset.MinValue)
            {
                TimeSpan since = DateTimeOffset.Now - PositionAt;
                if (since > TimeSpan.Zero)
                {
                    position += since;
                }
            }

            return position < TimeSpan.Zero ? TimeSpan.Zero : position > Duration ? Duration : position;
        }
    }
}

/// <summary>
/// Follows Windows' media controls (System Media Transport Controls): the app Windows treats
/// as the current media session, its title, artist, cover art, playing state and position,
/// and passes play/pause, next, previous and seeking on to it. Windows raises its events on
/// other threads; <see cref="Changed"/> is raised on the UI thread. Nothing is invented: with
/// no session, or a session without a title, there is nothing to show.
/// A player with a Winamp-style window (WACUP, Winamp, AIMP) also answers Winamp's messages:
/// its position and track length are read from there (fresh, also right after a jump), and
/// seeking goes there when the player does not take it through Windows' controls.
/// </summary>
internal sealed class MediaService : IDisposable
{
    private readonly DispatcherQueue _queue;
    private readonly Func<string, string> _appName;
    private readonly HashSet<string> _noted = new();
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;
    private bool _started;
    private bool _disposed;
    private bool _reading;
    private bool _readAgain;
    private string _lastApp = string.Empty;

    /// <summary>The current player's Winamp-style window, or 0 (found with each read).</summary>
    private nint _winamp;

    /// <summary>The current player takes seeking through Windows' controls.</summary>
    private bool _appSeeks;

    /// <param name="appName">Turns Windows' app ID for the session into a name to show.</param>
    internal MediaService(DispatcherQueue queue, Func<string, string> appName)
    {
        _queue = queue;
        _appName = appName;
    }

    /// <summary>On the UI thread, after <see cref="Current"/> changed.</summary>
    internal event Action? Changed;

    internal MediaSnapshot Current { get; private set; } = MediaSnapshot.Nothing;

    internal async void Start()
    {
        if (_started || _disposed)
        {
            return;
        }

        _started = true;
        try
        {
            GlobalSystemMediaTransportControlsSessionManager manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (_disposed)
            {
                return;
            }

            _manager = manager;
            manager.CurrentSessionChanged += OnCurrentSessionChanged;
            manager.SessionsChanged += OnSessionsChanged;
            Attach(manager.GetCurrentSession());
            await ReadAsync();
        }
        catch (Exception ex)
        {
            Note("start", "Dynaland: Windows' media controls are not available; Dynaland shows the clock only.", ex);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        Attach(null);
        if (_manager is not null)
        {
            try
            {
                _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
                _manager.SessionsChanged -= OnSessionsChanged;
            }
            catch (Exception)
            {
                // Already gone.
            }

            _manager = null;
        }
    }

    internal Task<bool> TogglePlayPauseAsync() => Send("play/pause", s => s.TryTogglePlayPauseAsync().AsTask());

    internal Task<bool> NextAsync() => Send("next", s => s.TrySkipNextAsync().AsTask());

    internal Task<bool> PreviousAsync() => Send("previous", s => s.TrySkipPreviousAsync().AsTask());

    /// <summary>
    /// Jumps to a place in the track (from its start), within the track: through Windows'
    /// controls when the player takes it there, else through its Winamp-style window.
    /// </summary>
    internal async Task<bool> SeekAsync(TimeSpan fromStart)
    {
        if (_appSeeks)
        {
            bool done = await Send("seek", s =>
            {
                GlobalSystemMediaTransportControlsSessionTimelineProperties timeline = s.GetTimelineProperties();
                TimeSpan length = timeline.EndTime - timeline.StartTime;
                TimeSpan within = fromStart < TimeSpan.Zero ? TimeSpan.Zero : fromStart > length ? length : fromStart;
                return s.TryChangePlaybackPositionAsync((timeline.StartTime + within).Ticks).AsTask();
            });
            if (done)
            {
                return true;
            }
        }

        nint winamp = _winamp;
        if (winamp == 0)
        {
            return false;
        }

        TimeSpan duration = Current.Duration;
        TimeSpan to = fromStart < TimeSpan.Zero ? TimeSpan.Zero : duration > TimeSpan.Zero && fromStart > duration ? duration : fromStart;
        if (WinampInterop.TryJump(winamp, to))
        {
            // Read the new position at once (Winamp's messages answer right away).
            QueueRead();
            return true;
        }

        Note("refused:seek-winamp", "Dynaland: the player did not take the jump through its Winamp-style window.", null);
        return false;
    }

    private async Task<bool> Send(string what, Func<GlobalSystemMediaTransportControlsSession, Task<bool>> action)
    {
        GlobalSystemMediaTransportControlsSession? session = _session;
        if (session is null)
        {
            return false;
        }

        try
        {
            bool done = await action(session);
            if (!done)
            {
                Note("refused:" + what, "Dynaland: the playing app did not take \"" + what + "\".", null);
            }

            return done;
        }
        catch (Exception ex)
        {
            Note("send:" + what, "Dynaland: \"" + what + "\" could not be sent to the playing app.", ex);
            return false;
        }
    }

    private void Attach(GlobalSystemMediaTransportControlsSession? session)
    {
        if (ReferenceEquals(session, _session))
        {
            return;
        }

        if (_session is not null)
        {
            try
            {
                _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
                _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
            }
            catch (Exception)
            {
                // The app has gone.
            }
        }

        _session = session;
        if (session is not null)
        {
            try
            {
                session.MediaPropertiesChanged += OnMediaPropertiesChanged;
                session.PlaybackInfoChanged += OnPlaybackInfoChanged;
                session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;
            }
            catch (Exception ex)
            {
                Note("attach", "Dynaland: the media session's events could not be followed.", ex);
            }
        }
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args)
    {
        _queue.TryEnqueue(() =>
        {
            if (_disposed || _manager is null)
            {
                return;
            }

            try
            {
                Attach(_manager.GetCurrentSession());
            }
            catch (Exception ex)
            {
                Note("current", "Dynaland: the current media session could not be read.", ex);
                Attach(null);
            }

            _ = ReadAsync();
        });
    }

    private void OnSessionsChanged(GlobalSystemMediaTransportControlsSessionManager sender, SessionsChangedEventArgs args)
    {
        OnCurrentSessionChanged(sender, null!);
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args)
    {
        QueueRead();
    }

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args)
    {
        QueueRead();
    }

    private void OnTimelinePropertiesChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args)
    {
        QueueRead();
    }

    private void QueueRead()
    {
        _queue.TryEnqueue(() => _ = ReadAsync());
    }

    /// <summary>One read at a time; a change during a read reads again after it.</summary>
    private async Task ReadAsync()
    {
        if (_disposed)
        {
            return;
        }

        if (_reading)
        {
            _readAgain = true;
            return;
        }

        _reading = true;
        try
        {
            do
            {
                _readAgain = false;
                MediaSnapshot snapshot = await SnapshotAsync(_session);
                if (_disposed)
                {
                    return;
                }

                if (snapshot != Current)
                {
                    Current = snapshot;
                    if (snapshot.HasMedia && snapshot.AppName != _lastApp)
                    {
                        _lastApp = snapshot.AppName;
                        Log.Info("Dynaland: media from " + snapshot.AppName + ".");
                    }

                    Changed?.Invoke();
                }
            }
            while (_readAgain && !_disposed);
        }
        finally
        {
            _reading = false;
        }
    }

    private async Task<MediaSnapshot> SnapshotAsync(GlobalSystemMediaTransportControlsSession? session)
    {
        if (session is null)
        {
            _winamp = 0;
            _appSeeks = false;
            return MediaSnapshot.Nothing;
        }

        try
        {
            GlobalSystemMediaTransportControlsSessionMediaProperties? properties = null;
            try
            {
                properties = await session.TryGetMediaPropertiesAsync();
            }
            catch (Exception ex)
            {
                Note("properties", "Dynaland: the playing app's title and art could not be read.", ex);
            }

            GlobalSystemMediaTransportControlsSessionPlaybackInfo? info = session.GetPlaybackInfo();
            GlobalSystemMediaTransportControlsSessionTimelineProperties? timeline = session.GetTimelineProperties();

            string title = (properties?.Title ?? string.Empty).Trim();
            string artist = (properties?.Artist ?? string.Empty).Trim();
            if (artist.Length == 0)
            {
                artist = (properties?.AlbumArtist ?? string.Empty).Trim();
            }

            GlobalSystemMediaTransportControlsSessionPlaybackStatus status = info?.PlaybackStatus
                ?? GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed;
            if (title.Length == 0 || status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Closed)
            {
                return MediaSnapshot.Nothing;
            }

            GlobalSystemMediaTransportControlsSessionPlaybackControls? controls = info?.Controls;
            bool canPlayPause = controls is not null
                && (controls.IsPlayPauseToggleEnabled || controls.IsPlayEnabled || controls.IsPauseEnabled);

            TimeSpan duration = TimeSpan.Zero;
            TimeSpan position = TimeSpan.Zero;
            DateTimeOffset positionAt = DateTimeOffset.MinValue;
            if (timeline is not null)
            {
                duration = timeline.EndTime - timeline.StartTime;
                position = timeline.Position - timeline.StartTime;
                positionAt = timeline.LastUpdatedTime;
            }

            // A Winamp-style player: its own position and length, read fresh.
            string appId = session.SourceAppUserModelId ?? string.Empty;
            nint winamp = WinampInterop.FindFor(appId);
            if (winamp != 0 && WinampInterop.TryGetTimeline(winamp, out TimeSpan playerPosition, out TimeSpan playerLength))
            {
                duration = playerLength;
                position = playerPosition;
                positionAt = DateTimeOffset.Now;
            }
            else
            {
                winamp = 0;
            }

            bool appSeeks = controls?.IsPlaybackPositionEnabled ?? false;
            _winamp = winamp;
            _appSeeks = appSeeks;
            if (winamp != 0 && !appSeeks)
            {
                Note("winamp:" + appId, "Dynaland: " + appId + " takes jumps through its Winamp-style window; seeking goes there.", null);
            }

            bool hasTimeline = duration > TimeSpan.FromSeconds(1);
            string app = _appName(appId);
            string album = (properties?.AlbumTitle ?? string.Empty).Trim();

            return new MediaSnapshot(
                true,
                title,
                artist,
                app,
                status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                controls?.IsPreviousEnabled ?? false,
                controls?.IsNextEnabled ?? false,
                canPlayPause,
                hasTimeline,
                hasTimeline ? position : TimeSpan.Zero,
                hasTimeline ? duration : TimeSpan.Zero,
                positionAt,
                properties?.Thumbnail,
                app + "\n" + title + "\n" + artist + "\n" + album,
                hasTimeline && (appSeeks || winamp != 0));
        }
        catch (Exception ex)
        {
            Note("snapshot", "Dynaland: the media session could not be read.", ex);
            return MediaSnapshot.Nothing;
        }
    }

    private void Note(string key, string message, Exception? ex)
    {
        if (!_noted.Add(key))
        {
            return;
        }

        if (ex is null)
        {
            Log.Warn(message);
        }
        else
        {
            Log.Warn(message, ex);
        }
    }
}
