using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Machitan.Core;
using Machitan.Shell;
using Machitan.Visuals;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace Machitan.Widgets;

/// <summary>
/// The Now playing widget: what Windows' media controls say is playing (the same source as
/// Dynaland's player, followed on its own while the widget is shown): the cover, title, artist
/// and app; the synced lyrics when Synced lyrics is on (Settings > Dynaland; the line being
/// sung, big, and the next one, from the same search as Dynaland's: Sam, 2026-11-26: "give
/// lyrics on both widgets ... more visibility"); a bar of how far it is that can be dragged
/// or clicked to jump (when the app takes jumps; Sam: "make slider in the 'now playing'
/// slideable"); and previous, play or pause, and next (each only when the app takes it). With
/// nothing playing it says so. The log never gets a title, an artist or lyrics. Its choices
/// (Customize): the cover, the lyrics, the bar and the buttons each on or off, and left (the
/// cover beside the title) or centre (the cover above it, all centred). A title too long for
/// the card scrolls sideways by itself (Sam, 2026-11-26: "make the title (not lyrics) scrolls
/// automatically in the widget"), as Dynaland's does; it stands still while the music is paused.
/// </summary>
internal sealed class NowPlayingWidget : WidgetView
{
    private const int PreviousGlyph = 0xE892;
    private const int NextGlyph = 0xE893;
    private const int PlayGlyph = 0xE768;
    private const int PauseGlyph = 0xE769;
    private const int NoArtGlyph = 0xEC4F;
    private const double ArtDip = 72;

    private const string CoverKey = "Cover";
    private const string LyricsKey = "Lyrics";
    private const string BarKey = "Bar";
    private const string ButtonsKey = "Buttons";

    /// <summary>The bar's steps (a thousandth of the song each).</summary>
    private const double BarSteps = 1000;

    /// <summary>A lyric line shows this much before its time (as in Dynaland).</summary>
    private static readonly TimeSpan LyricLead = TimeSpan.FromMilliseconds(150);

    /// <summary>After a jump, the bar waits this long for the app to report its new place.</summary>
    private const int SeekHoldMs = 1500;

    private readonly DispatcherQueueTimer _tick;
    private readonly DispatcherQueueTimer _seekWait;
    private MediaService? _media;
    private Border? _art;
    private ImageBrush? _artBrush;
    private FontIcon? _noArt;
    private Marquee? _title;

    /// <summary>The title's colour, which follows the widget's theme (the scrolling line needs a brush of its own).</summary>
    private readonly SolidColorBrush _ink = new(Colors.White);
    private TextBlock? _artist;
    private TextBlock? _app;
    private StackPanel? _lyricArea;
    private TextBlock? _lyricNow;
    private TextBlock? _lyricNext;
    private Slider? _bar;
    private TextBlock? _position;
    private TextBlock? _length;
    private Grid? _timeline;
    private Button? _previous;
    private Button? _playPause;
    private FontIcon? _playPauseIcon;
    private Button? _next;
    private StackPanel? _playing;
    private TextBlock? _nothing;
    private Grid? _top;
    private StackPanel? _words;
    private StackPanel? _controls;
    private string _artKey = string.Empty;
    private int _artVersion;
    private bool _shown;
    private int _ticks;

    // The bar: set by the widget (not a jump), held down by the pointer, a jump waiting, and when the app's place counts again.
    private bool _settingBar;
    private bool _holding;
    private double? _jumpTo;
    private long _holdUntil;

    // The lyrics: for which song, the lines, the line shown, and the search under way.
    private string _lyricsKey = string.Empty;
    private SyncedLyrics? _lyrics;
    private int _lyricIndex = -2;
    private CancellationTokenSource? _lyricsCancel;

    internal NowPlayingWidget(ShellHost host, WidgetItem item)
        : base(host, item)
    {
        // Four times a second while playing: how far it is, and the lyric line.
        DispatcherQueue queue = DispatcherQueue.GetForCurrentThread();
        _tick = queue.CreateTimer();
        _tick.Interval = TimeSpan.FromMilliseconds(250);
        _tick.IsRepeating = true;
        _tick.Tick += (_, _) => Tick();

        // A click or a key on the bar jumps a moment later (a drag jumps when let go).
        _seekWait = queue.CreateTimer();
        _seekWait.Interval = TimeSpan.FromMilliseconds(300);
        _seekWait.IsRepeating = false;
        _seekWait.Tick += (_, _) => JumpNow();
    }

    internal override double DesktopHeightDip => 270;

    internal override bool HasOptions => true;

    internal override FrameworkElement BuildOptions()
    {
        return OptionsPanel(
            AlignChoice(withRight: false),
            Switch(Strings.WgOptCover, CoverKey, fallback: true),
            Switch(Strings.WgOptLyrics, LyricsKey, fallback: true),
            Switch(Strings.WgOptBar, BarKey, fallback: true),
            Switch(Strings.WgOptButtons, ButtonsKey, fallback: true));
    }

    /// <summary>Left: the cover beside the title; centre: the cover above it and everything centred. Then the parts chosen.</summary>
    private protected override void ApplyOptions()
    {
        if (_top is null || _art is null || _words is null || _controls is null || _lyricNow is null || _lyricNext is null)
        {
            return;
        }

        bool centre = Option(AlignKey, "Left") == "Center";
        _top.ColumnDefinitions[0].Width = centre ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        _top.ColumnDefinitions[1].Width = centre ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        _top.RowSpacing = centre ? 8 : 0;
        Grid.SetRow(_words, centre ? 1 : 0);
        Grid.SetColumn(_words, centre ? 0 : 1);
        Grid.SetColumnSpan(_art, centre ? 2 : 1);
        Grid.SetColumnSpan(_words, centre ? 2 : 1);
        _art.HorizontalAlignment = centre ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        _words.HorizontalAlignment = HorizontalAlignment.Stretch;
        if (_title is not null)
        {
            _title.Element.HorizontalAlignment = centre ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        }

        TextAlignment text = centre ? TextAlignment.Center : TextAlignment.Left;
        foreach (TextBlock? line in new[] { _artist, _app, _lyricNow, _lyricNext })
        {
            if (line is not null)
            {
                line.TextAlignment = text;
            }
        }

        _art.Visibility = OptionOn(CoverKey, fallback: true) ? Visibility.Visible : Visibility.Collapsed;
        _controls.Visibility = OptionOn(ButtonsKey, fallback: true) ? Visibility.Visible : Visibility.Collapsed;
        Apply();
    }

    internal override FrameworkElement Build()
    {
        _artBrush = new ImageBrush { Stretch = Stretch.UniformToFill };
        _noArt = new FontIcon
        {
            Glyph = ((char)NoArtGlyph).ToString(),
            FontSize = 28,
            Opacity = 0.6,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _art = new Border
        {
            Width = ArtDip,
            Height = ArtDip,
            CornerRadius = new CornerRadius(6),
            Child = _noArt,
            VerticalAlignment = VerticalAlignment.Top,
        };

        // The title: as wide as its text, up to the room it has; longer, it scrolls.
        var title = new Marquee(14, FontWeights.SemiBold, _ink, maxWidth: 200);
        title.Element.ActualThemeChanged += (_, _) => UpdateInk(title.Element);
        title.Element.Loaded += (_, _) => UpdateInk(title.Element);
        _title = title;
        _artist = Line(12, 0.8, FontWeights.Normal);
        _app = Line(11, 0.6, FontWeights.Normal);
        var words = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        words.SizeChanged += (_, e) =>
        {
            if (e.NewSize.Width >= 1)
            {
                title.SetMaxWidth(Math.Floor(e.NewSize.Width));
            }
        };
        words.Children.Add(title.Element);
        words.Children.Add(_artist);
        words.Children.Add(_app);

        var top = new Grid { ColumnSpacing = 12 };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        top.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        top.Children.Add(_art);
        Grid.SetColumn(words, 1);
        top.Children.Add(words);
        _top = top;
        _words = words;

        _lyricNow = new TextBlock
        {
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 3,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _lyricNext = Line(12, 0.6, FontWeights.Normal);
        _lyricArea = new StackPanel { Spacing = 2, Visibility = Visibility.Collapsed };
        _lyricArea.Children.Add(_lyricNow);
        _lyricArea.Children.Add(_lyricNext);

        _bar = new Slider
        {
            Minimum = 0,
            Maximum = BarSteps,
            StepFrequency = 1,
            SmallChange = 10,
            LargeChange = 50,
            IsThumbToolTipEnabled = false,
        };
        AutomationProperties.SetName(_bar, Strings.DynPosition);
        _bar.ValueChanged += OnBarChanged;
        _bar.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => _holding = true), true);
        _bar.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) => LetGo()), true);
        _bar.AddHandler(UIElement.PointerCaptureLostEvent, new PointerEventHandler((_, _) => LetGo()), true);
        _position = Line(11, 0.6, FontWeights.Normal);
        _length = Line(11, 0.6, FontWeights.Normal);
        _length.HorizontalAlignment = HorizontalAlignment.Right;
        _timeline = new Grid();
        _timeline.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _timeline.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _timeline.Children.Add(_bar);
        Grid.SetRow(_position, 1);
        Grid.SetRow(_length, 1);
        _timeline.Children.Add(_position);
        _timeline.Children.Add(_length);

        _previous = Control(PreviousGlyph, Strings.DynPrevious, () => _media?.PreviousAsync(), out _);
        _playPause = Control(PlayGlyph, Strings.DynPlayPause, () => _media?.TogglePlayPauseAsync(), out FontIcon playPauseIcon);
        _playPauseIcon = playPauseIcon;
        _next = Control(NextGlyph, Strings.DynNext, () => _media?.NextAsync(), out _);
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Center };
        controls.Children.Add(_previous);
        controls.Children.Add(_playPause);
        controls.Children.Add(_next);
        _controls = controls;

        _playing = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed };
        _playing.Children.Add(top);
        _playing.Children.Add(_lyricArea);
        _playing.Children.Add(_timeline);
        _playing.Children.Add(controls);

        _nothing = new TextBlock { Text = Strings.WgNothingPlaying, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };

        var panel = new Grid { VerticalAlignment = VerticalAlignment.Top };
        panel.Children.Add(_playing);
        panel.Children.Add(_nothing);
        ApplyOptions();
        return panel;
    }

    internal override void SetShown(bool shown)
    {
        if (shown == _shown)
        {
            return;
        }

        _shown = shown;
        if (shown)
        {
            _media = new MediaService(DispatcherQueue.GetForCurrentThread(), appId => MediaAppNames.NameFor(Host.Catalog, appId));
            _media.Changed += Apply;
            Host.Settings.Changed += Apply;
            _media.Start();
            Apply();
            return;
        }

        _tick.Stop();
        _seekWait.Stop();
        if (_title is not null)
        {
            _title.Paused = true;
        }

        Host.Settings.Changed -= Apply;
        DropLyrics();
        if (_media is not null)
        {
            _media.Changed -= Apply;
            _media.Dispose();
            _media = null;
        }
    }

    /// <summary>The title's colour for the theme it is shown in (as the other lines' own: near black, or white).</summary>
    private void UpdateInk(FrameworkElement element)
    {
        _ink.Color = element.ActualTheme == ElementTheme.Light ? ColorHelper.FromArgb(0xE4, 0, 0, 0) : Colors.White;
    }

    private static TextBlock Line(double size, double opacity, Windows.UI.Text.FontWeight weight)
    {
        return new TextBlock
        {
            FontSize = size,
            FontWeight = weight,
            Opacity = opacity,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
    }

    private static Button Control(int glyph, string name, Func<Task<bool>?> action, out FontIcon icon)
    {
        icon = new FontIcon { Glyph = ((char)glyph).ToString(), FontSize = 16 };
        var button = new Button
        {
            Content = icon,
            Width = 44,
            Height = 36,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        AutomationProperties.SetName(button, name);
        button.Click += async (_, _) =>
        {
            try
            {
                if (action() is Task<bool> running)
                {
                    await running;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Now playing widget: \"" + name + "\" failed.", ex);
            }
        };
        return button;
    }

    private static string Clock(TimeSpan time)
    {
        return time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : time.ToString(@"m\:ss", CultureInfo.InvariantCulture);
    }

    private void Apply()
    {
        if (_playing is null || _nothing is null)
        {
            return;
        }

        MediaSnapshot media = _media?.Current ?? MediaSnapshot.Nothing;
        bool has = media.HasMedia;
        _playing.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        _nothing.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        FollowLyrics(media);
        if (has && media.IsPlaying)
        {
            _tick.Start();
        }
        else
        {
            _tick.Stop();
        }

        if (!has)
        {
            _artKey = string.Empty;
            if (_title is not null)
            {
                _title.Paused = true;
            }

            return;
        }

        _title!.Text = media.Title;
        _title.Paused = !media.IsPlaying || !_shown;
        Set(_artist, media.Artist);
        Set(_app, media.AppName);
        _artist!.Visibility = media.Artist.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _previous!.IsEnabled = media.CanPrevious;
        _next!.IsEnabled = media.CanNext;
        _playPause!.IsEnabled = media.CanPlayPause;
        _playPauseIcon!.Glyph = ((char)(media.IsPlaying ? PauseGlyph : PlayGlyph)).ToString();
        _bar!.IsEnabled = media.CanSeek;
        AutomationProperties.SetName(_playing, media.Artist.Length > 0 ? media.Title + ", " + media.Artist : media.Title);

        if (media.TrackKey != _artKey)
        {
            _artKey = media.TrackKey;
            _ = LoadArtAsync(media.Art);
        }

        UpdateTimeline();
        ShowLyric(force: false);
    }

    /// <summary>A quarter second while playing: the bar and the times; now and then a Winamp-style player's place; the lyric line.</summary>
    private void Tick()
    {
        if (++_ticks % 4 == 0)
        {
            _media?.CheckWinampPosition();
        }

        UpdateTimeline();
        ShowLyric(force: false);
    }

    /// <summary>How far it is: shown only when the app reports where it is and how long it lasts; left alone while the bar is held or a jump settles.</summary>
    private void UpdateTimeline()
    {
        if (_timeline is null || _bar is null)
        {
            return;
        }

        MediaSnapshot media = _media?.Current ?? MediaSnapshot.Nothing;
        bool shown = media.HasMedia && media.HasTimeline && media.Duration > TimeSpan.Zero && OptionOn(BarKey, fallback: true);
        _timeline.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
        if (!shown || _holding || _jumpTo is not null || Environment.TickCount64 < _holdUntil)
        {
            return;
        }

        TimeSpan now = media.PositionNow;
        _settingBar = true;
        try
        {
            _bar.Value = Math.Clamp(now.TotalSeconds / media.Duration.TotalSeconds, 0, 1) * BarSteps;
        }
        finally
        {
            _settingBar = false;
        }

        Set(_position, Clock(now));
        Set(_length, Clock(media.Duration));
    }

    /// <summary>The bar moved by the user (a click, a drag, a key): the time follows, and the jump waits until it is let go.</summary>
    private void OnBarChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_settingBar || _media is null)
        {
            return;
        }

        MediaSnapshot media = _media.Current;
        if (!media.CanSeek || media.Duration <= TimeSpan.Zero)
        {
            return;
        }

        _jumpTo = e.NewValue / BarSteps;
        Set(_position, Clock(TimeSpan.FromSeconds(media.Duration.TotalSeconds * _jumpTo.Value)));
        _seekWait.Stop();
        _seekWait.Start();
    }

    private void LetGo()
    {
        if (!_holding)
        {
            return;
        }

        _holding = false;
        JumpNow();
    }

    /// <summary>The app jumps to the bar's place (not while the bar is still held).</summary>
    private void JumpNow()
    {
        _seekWait.Stop();
        if (_holding || _jumpTo is not double fraction || _media is null)
        {
            return;
        }

        _jumpTo = null;
        _holdUntil = Environment.TickCount64 + SeekHoldMs;
        _ = _media.SeekAsync(TimeSpan.FromSeconds(_media.Current.Duration.TotalSeconds * fraction));

        // The lyric line follows the new place at once.
        ShowLyric(force: true);
    }

    // ---------------------------------------------------------------- lyrics

    /// <summary>A new song is looked up once (Synced lyrics on, and the app reports a place in the song); otherwise the lyrics go.</summary>
    private void FollowLyrics(MediaSnapshot media)
    {
        bool wanted = Host.Settings.Current.DynalandLyrics && OptionOn(LyricsKey, fallback: true)
            && media.HasMedia && media.HasTimeline && media.Title.Length > 0;
        if (!wanted)
        {
            if (_lyricsKey.Length > 0 || _lyrics is not null)
            {
                DropLyrics();
                ShowLyric(force: true);
            }

            return;
        }

        if (media.TrackKey != _lyricsKey)
        {
            DropLyrics();
            _lyricsKey = media.TrackKey;
            ShowLyric(force: true);
            _ = LoadLyricsAsync(media);
        }
    }

    private async Task LoadLyricsAsync(MediaSnapshot media)
    {
        if (_media is null)
        {
            return;
        }

        var cancel = new CancellationTokenSource();
        _lyricsCancel = cancel;
        try
        {
            string? songFile = _media.PlayingFile();
            SyncedLyrics? found = await Host.Lyrics.FindAsync(media.TrackKey, media.Title, media.Artist, media.Duration, songFile, cancel.Token);
            if (found is null && songFile is not null && !cancel.IsCancellationRequested)
            {
                found = await LyricsService.ReadLocalAsync(songFile);
            }

            if (cancel.IsCancellationRequested || !_shown || media.TrackKey != _lyricsKey)
            {
                return;
            }

            _lyrics = found;
            ShowLyric(force: true);
        }
        catch (Exception ex)
        {
            Log.Warn("Now playing widget: lyrics could not be looked up.", ex);
        }
    }

    private void DropLyrics()
    {
        _lyricsCancel?.Cancel();
        _lyricsCancel?.Dispose();
        _lyricsCancel = null;
        _lyrics = null;
        _lyricsKey = string.Empty;
        _lyricIndex = -2;
    }

    /// <summary>The line being sung (and the next one); the lyrics show only while the song has them.</summary>
    private void ShowLyric(bool force)
    {
        if (_lyricArea is null || _lyricNow is null || _lyricNext is null)
        {
            return;
        }

        MediaSnapshot media = _media?.Current ?? MediaSnapshot.Nothing;
        SyncedLyrics? lyrics = media.HasMedia && media.TrackKey == _lyricsKey ? _lyrics : null;
        _lyricArea.Visibility = lyrics is null ? Visibility.Collapsed : Visibility.Visible;
        if (lyrics is null)
        {
            _lyricIndex = -2;
            return;
        }

        int index = lyrics.IndexAt(media.PositionNow + LyricLead);
        if (index == _lyricIndex && !force)
        {
            return;
        }

        _lyricIndex = index;
        Set(_lyricNow, index >= 0 ? lyrics.Lines[index].Text : string.Empty);
        string next = string.Empty;
        for (int i = index + 1; i < lyrics.Lines.Count; i++)
        {
            if (lyrics.Lines[i].Text.Length > 0)
            {
                next = lyrics.Lines[i].Text;
                break;
            }
        }

        Set(_lyricNext, next);
    }

    /// <summary>The cover from the app (or the music glyph when it gives none).</summary>
    private async Task LoadArtAsync(IRandomAccessStreamReference? art)
    {
        int version = ++_artVersion;
        try
        {
            BitmapImage? bitmap = null;
            if (art is not null)
            {
                bitmap = new BitmapImage { DecodePixelWidth = 144 };
                using IRandomAccessStreamWithContentType stream = await art.OpenReadAsync();
                await bitmap.SetSourceAsync(stream);
            }

            if (version != _artVersion || _art is null || _artBrush is null || _noArt is null)
            {
                return;
            }

            _artBrush.ImageSource = bitmap;
            _art.Background = bitmap is not null ? _artBrush : null;
            _noArt.Visibility = bitmap is null ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            if (version == _artVersion && _art is not null && _noArt is not null)
            {
                _art.Background = null;
                _noArt.Visibility = Visibility.Visible;
            }

            Log.Warn("Now playing widget: a cover could not be read.", ex);
        }
    }

    private static void Set(TextBlock? block, string text)
    {
        if (block is not null && block.Text != text)
        {
            block.Text = text;
        }
    }
}
