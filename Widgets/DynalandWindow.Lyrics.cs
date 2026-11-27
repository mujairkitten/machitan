using System;
using System.Threading;
using System.Threading.Tasks;
using Machitan.Shell;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Machitan.Widgets;

/// <summary>
/// Synced lyrics (Sam, 2026-11-26: "Synced lyrics in Dynaland", from LRCLIB; off until turned on
/// in Settings > Dynaland): while a song with synced lyrics plays, the pill shows the line being
/// sung in place of the title, and the open player shows it under the artist. A pause in the
/// singing (an empty line) shows the title again. Asked once per song (LyricsService; a local
/// .lrc next to the song's file when LRCLIB has none, for WACUP and other Winamp-style players);
/// nothing is asked while the setting is off, or for an app that reports no position in the
/// song. While a line shows, the pill widens to fit it (Sam: "make the width adaptive to the
/// lyrics"), up to the open views' width (the trapezoid a little less, for its slanted sides);
/// back to the music width when the title shows again. A line still too long gets smaller,
/// down to 10 (from 12), and if even that is too long it slides once, to show its end before
/// the next line starts (Sam, 2026-11-26: "for some width, there's still not enough room").
/// For a Winamp-style player its position is read again every second while lines show (a
/// jump in its own window may not reach Windows' controls).
/// </summary>
public sealed partial class DynalandWindow
{
    /// <summary>A line shows this much before its time, so it is there as it is sung.</summary>
    private static readonly TimeSpan LyricLead = TimeSpan.FromMilliseconds(150);

    /// <summary>The pill's music line: its size, and the smallest a long lyric line gets before it slides.</summary>
    private const double MiniTextSize = 12;
    private const double LyricSmallestSize = 10;

    /// <summary>The last line slides (if it must) within this time; any line within 1 to 15 s.</summary>
    private static readonly TimeSpan LastLyricTime = TimeSpan.FromSeconds(4);

    /// <summary>Ticks (200 ms) between two readings of a Winamp-style player's position.</summary>
    private const int WinampCheckTicks = 5;

    /// <summary>The pill is widened for a lyric line now.</summary>
    private bool _lyricWide;
    private int _lyricTicks;

    private SyncedLyrics? _lyrics;

    /// <summary>The song the lyrics are for (its TrackKey); empty when none is followed.</summary>
    private string _lyricsKey = string.Empty;
    private CancellationTokenSource? _lyricsCancel;

    /// <summary>The line shown now; -1 for none, -2 for "not shown yet".</summary>
    private int _lyricIndex = -2;
    private TextBlock? _lyricNow;
    private TextBlock? _lyricNext;
    private StackPanel? _lyricArea;

    /// <summary>The player grows by this much (DIP) while the song has synced lyrics: two lines of the sung line and one of the next.</summary>
    private const double LyricAreaHeight = 66;
    private DispatcherQueueTimer? _lyricsTick;

    /// <summary>
    /// The player's lyrics, across its width below the cover and the title (Sam, 2026-11-26:
    /// "give lyrics ... more visibility"; before, one small line beside the cover): the line
    /// being sung, big and white, up to two lines, and the next line, dim. Shown, and the player
    /// that much taller, while the song has synced lyrics (the height stays for the whole song).
    /// </summary>
    private StackPanel MakeLyricArea()
    {
        _lyricNow = Text(15, FontWeights.SemiBold, _whiteBrush);
        _lyricNow.TextWrapping = TextWrapping.Wrap;
        _lyricNow.MaxLines = 2;
        _lyricNext = Text(12, FontWeights.Normal, _dimBrush);
        _lyricArea = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 4, 8, 4),
            Visibility = Visibility.Collapsed,
        };
        _lyricArea.Children.Add(_lyricNow);
        _lyricArea.Children.Add(_lyricNext);
        return _lyricArea;
    }

    /// <summary>The player's lyrics for the line at <paramref name="index"/> (the next line too), or none; the player's height follows.</summary>
    private void ShowLyricArea(MediaSnapshot media, int index)
    {
        SyncedLyrics? lyrics = media.HasMedia && media.TrackKey == _lyricsKey && _host.Settings.Current.DynalandLyrics ? _lyrics : null;
        if (_lyricArea is not null)
        {
            _lyricArea.Visibility = lyrics is null ? Visibility.Collapsed : Visibility.Visible;
        }

        SetMediaHeight(OpenHeight + (lyrics is null ? 0 : LyricAreaHeight));
        if (lyrics is null || _lyricNow is null || _lyricNext is null)
        {
            return;
        }

        _lyricNow.Text = index >= 0 ? lyrics.Lines[index].Text : string.Empty;
        string next = string.Empty;
        for (int i = index + 1; i < lyrics.Lines.Count; i++)
        {
            if (lyrics.Lines[i].Text.Length > 0)
            {
                next = lyrics.Lines[i].Text;
                break;
            }
        }

        _lyricNext.Text = next;
    }

    /// <summary>
    /// The pill's music line: the line being sung (smaller if it must be, then sliding once to
    /// its end before the next line), else the title and artist (sliding round and round when
    /// too long). <paramref name="newLine"/>: a new lyric line, so its sliding starts over even
    /// when its words are the same as the last line's.
    /// </summary>
    private void ShowMiniText(MediaSnapshot media, bool newLine = false)
    {
        int index = LyricIndex(media);
        string? sung = CurrentLyric(media);
        if (sung is not null)
        {
            _miniText.Show(sung, LyricSmallestSize, LyricTimeLeft(media, index), again: newLine);
        }
        else
        {
            _miniText.Show(media.Artist.Length > 0 ? media.Title + " - " + media.Artist : media.Title, MiniTextSize, null, again: false);
        }
    }

    /// <summary>How long the line at <paramref name="index"/> has left before the next one starts (the last: 4 s).</summary>
    private TimeSpan LyricTimeLeft(MediaSnapshot media, int index)
    {
        if (_lyrics is not SyncedLyrics lyrics || index < 0 || index + 1 >= lyrics.Lines.Count)
        {
            return LastLyricTime;
        }

        double seconds = (lyrics.Lines[index + 1].At - (media.PositionNow + LyricLead)).TotalSeconds;
        return TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 15));
    }

    private int LyricIndex(MediaSnapshot media)
    {
        return _lyrics is null || !media.HasMedia || media.TrackKey != _lyricsKey || !_host.Settings.Current.DynalandLyrics
            ? -1
            : _lyrics.IndexAt(media.PositionNow + LyricLead);
    }

    private string? CurrentLyric(MediaSnapshot media)
    {
        int index = LyricIndex(media);
        return index >= 0 && _lyrics is SyncedLyrics lyrics && lyrics.Lines[index].Text.Length > 0 ? lyrics.Lines[index].Text : null;
    }

    /// <summary>On every media change and settings change: a new song is looked up once; the line follows the song while it plays.</summary>
    private void FollowLyrics(MediaSnapshot media)
    {
        bool wanted = _host.Settings.Current.DynalandLyrics && media.HasMedia && media.HasTimeline && media.Title.Length > 0;
        if (!wanted)
        {
            if (_lyricsKey.Length > 0)
            {
                DropLyrics();
            }
        }
        else if (media.TrackKey != _lyricsKey)
        {
            DropLyrics();
            _lyricsKey = media.TrackKey;
            _ = LoadLyricsAsync(media);
        }

        if (_lyricsTick is not null)
        {
            if (wanted && media.IsPlaying && _lyrics is not null)
            {
                _lyricsTick.Start();
            }
            else
            {
                _lyricsTick.Stop();
            }
        }

        ShowLyric();
    }

    private async Task LoadLyricsAsync(MediaSnapshot media)
    {
        var cancel = new CancellationTokenSource();
        _lyricsCancel = cancel;

        // The song's file now (before the song can change): for the local .lrc fallback.
        string? songFile = _media.PlayingFile();
        SyncedLyrics? found = await _host.Lyrics.FindAsync(media.TrackKey, media.Title, media.Artist, media.Duration, songFile, cancel.Token);
        if (found is null && songFile is not null && !cancel.IsCancellationRequested && !_closed)
        {
            found = await LyricsService.ReadLocalAsync(songFile);
        }

        // Back on the UI thread: still the same song?
        if (_closed || cancel.IsCancellationRequested || media.TrackKey != _lyricsKey)
        {
            return;
        }

        _lyrics = found;
        _lyricIndex = -2;
        if (found is not null && _media.Current.IsPlaying)
        {
            _lyricsTick?.Start();
        }

        ShowLyric();
    }

    private void DropLyrics()
    {
        SetLyricWidth(false);
        _lyricsCancel?.Cancel();
        _lyricsCancel?.Dispose();
        _lyricsCancel = null;
        _lyrics = null;
        _lyricsKey = string.Empty;
        _lyricIndex = -2;
        _lyricsTick?.Stop();
    }

    /// <summary>The 200 ms tick while a song with lyrics plays: now and then a Winamp-style player's position, then the line.</summary>
    private void TickLyrics()
    {
        if (++_lyricTicks % WinampCheckTicks == 0)
        {
            _media.CheckWinampPosition();
        }

        ShowLyric();
    }

    /// <summary>The line being sung, in the pill (which widens to fit it) and the player.</summary>
    private void ShowLyric()
    {
        MediaSnapshot media = _media.Current;
        if (!media.HasMedia)
        {
            SetLyricWidth(false);
            ShowLyricArea(media, -1);
            return;
        }

        int index = LyricIndex(media);
        if (index == _lyricIndex)
        {
            return;
        }

        _lyricIndex = index;
        string? sung = CurrentLyric(media);
        ShowLyricArea(media, index);

        SetLyricWidth(sung is not null);
        ShowMiniText(media, newLine: true);

        // The pill takes the new line's width (Refresh measures and animates it).
        Refresh();
    }

    /// <summary>Wide (a lyric line shows) or not; the widths are worked out again each time (the camera and microphone dots may have come or gone).</summary>
    private void SetLyricWidth(bool wide)
    {
        _lyricWide = wide;
        ApplyLyricWidth();
    }

    /// <summary>The music line's widest and the pill's widest: wider while a lyric line shows (also after a width or shape change).</summary>
    private void ApplyLyricWidth()
    {
        double pill = Math.Round(MaxCollapsedWidth * WidthRatio);
        double text = Math.Round(MiniTextMaxWidth * WidthRatio);
        if (_lyricWide)
        {
            pill = Math.Max(pill, LyricPillWidest());
            text = Math.Max(text, MiniTextRoom(pill));
        }

        _maxCollapsedWidth = pill;
        _miniText.SetMaxWidth(text);
    }

    /// <summary>
    /// The widest the closed pill gets for a lyric line: as wide as the open views (its curves
    /// then sit where theirs do, just past the stage); the trapezoid as wide as its slanted
    /// sides let it be inside the window (which reaches Pad + EarRadius past the stage on each side).
    /// </summary>
    private double LyricPillWidest()
    {
        return _trapezoid
            ? Math.Floor(_stageWidth + (2 * (Pad + EarRadius - TrapezoidReach())) - 2)
            : _stageWidth;
    }

    /// <summary>
    /// The music line's room in a pill this wide: the pill less its two sides and the row's
    /// other parts that show (the picture, the play state, the camera and microphone dots) with
    /// the gaps between them.
    /// </summary>
    private double MiniTextRoom(double pill)
    {
        double used = 2 * CollapsedSidePad;
        var any = new Windows.Foundation.Size(double.PositiveInfinity, CollapsedHeight);
        foreach (UIElement part in _miniRow.Children)
        {
            if (!ReferenceEquals(part, _miniText.Element) && part.Visibility == Visibility.Visible)
            {
                part.Measure(any);
                used += part.DesiredSize.Width + _miniRow.Spacing;
            }
        }

        return Math.Floor(pill - used - 1);
    }
}
