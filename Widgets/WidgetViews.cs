using System;
using Machitan.Shell;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Machitan.Widgets;

/// <summary>
/// A widget's look and behaviour, made for one widget (WidgetItem) while it is in the sidebar
/// or on the desktop. The sidebar puts it in a card with its icon, its name and a menu (move
/// up, move down, remove). Every widget is one the user added (Settings has none on by
/// default). Its content's height can be set (dragged, Sam 2026-11-26: "make the widgets
/// resizable"); it then fills that height.
/// </summary>
internal abstract class WidgetView
{
    private const int NoteGlyph = 0xE70B;
    private const int CalendarGlyph = 0xE787;
    private const int ClockGlyph = 0xE917;
    private const int WeatherGlyph = 0xE753;
    private const int MusicGlyph = 0xEC4F;
    private const int UnknownGlyph = 0xE74C;

    private protected WidgetView(ShellHost host, WidgetItem item)
    {
        Host = host;
        Item = item;
    }

    internal WidgetItem Item { get; }

    /// <summary>The card's name.</summary>
    internal string Title => NameOf(Item.Kind);

    /// <summary>The card's icon (Segoe Fluent Icons).</summary>
    internal int Glyph => GlyphOf(Item.Kind);

    /// <summary>True when removing it loses something the user typed (the sidebar asks first).</summary>
    internal virtual bool HasUserContent => false;

    /// <summary>On the desktop: the height of its content (DIP; the card's name row comes on top).</summary>
    internal virtual double DesktopHeightDip => 200;

    private protected ShellHost Host { get; }

    /// <summary>A view for the widget, or null for a kind this version does not have.</summary>
    internal static WidgetView? Create(ShellHost host, WidgetItem item)
    {
        return item.Kind switch
        {
            WidgetKinds.Note => new NoteWidget(host, item),
            WidgetKinds.ClockCalendar => new ClockCalendarWidget(host, item),
            WidgetKinds.Clock => new ClockWidget(host, item),
            WidgetKinds.Weather => new WeatherWidget(host, item),
            WidgetKinds.NowPlaying => new NowPlayingWidget(host, item),
            _ => null,
        };
    }

    /// <summary>A kind's name, as the Add menu and the card show it.</summary>
    internal static string NameOf(string kind)
    {
        return kind switch
        {
            WidgetKinds.Note => Strings.WgNote,
            WidgetKinds.ClockCalendar => Strings.WgClockCalendar,
            WidgetKinds.Clock => Strings.WgClock,
            WidgetKinds.Weather => Strings.WgWeather,
            WidgetKinds.NowPlaying => Strings.WgNowPlaying,
            _ => kind,
        };
    }

    internal static int GlyphOf(string kind)
    {
        return kind switch
        {
            WidgetKinds.Note => NoteGlyph,
            WidgetKinds.ClockCalendar => CalendarGlyph,
            WidgetKinds.Clock => ClockGlyph,
            WidgetKinds.Weather => WeatherGlyph,
            WidgetKinds.NowPlaying => MusicGlyph,
            _ => UnknownGlyph,
        };
    }

    /// <summary>Makes the widget's content (once).</summary>
    internal abstract FrameworkElement Build();

    /// <summary>The sidebar opened or closed (or a desktop widget was shown or put away): start or stop the widget's updates.</summary>
    internal virtual void SetShown(bool shown)
    {
    }

    /// <summary>Saves anything not saved yet (the sidebar hides, a widget is moved, Machitan exits).</summary>
    internal virtual void Flush()
    {
    }

    /// <summary>Gives the keyboard to the widget's first box (just after it was added).</summary>
    internal virtual void FocusFirst()
    {
    }

    /// <summary>True: its content is given a height to fill (on the desktop, or a sidebar card resized); false: as tall as it wants.</summary>
    internal virtual void SetFixedHeight(bool fixedHeight)
    {
    }

    // ---------------------------------------------------------------- options
    // Sam, 2026-11-26: "for widgets like clock and weather, give styling options, like centered
    // text, time only, analog, etc". Each widget's own choices, kept with it in widgets.json
    // (its Data), shown by the card menu's "Customize" as buttons and switches (no drop-down
    // list: its popup would show outside a docked sidebar). A change shows at once.

    /// <summary>True when the widget has choices ("Customize" in its menu).</summary>
    internal virtual bool HasOptions => false;

    /// <summary>The widget's choices as they are now (made each time "Customize" opens).</summary>
    internal virtual FrameworkElement BuildOptions() => new StackPanel();

    /// <summary>The choices were changed: the widget shows them.</summary>
    private protected virtual void ApplyOptions()
    {
    }

    /// <summary>A kept choice, or <paramref name="fallback"/> when none was made.</summary>
    private protected string Option(string key, string fallback)
    {
        return Item.Data.TryGetValue(key, out string? value) && value.Length > 0 ? value : fallback;
    }

    private protected bool OptionOn(string key, bool fallback)
    {
        return Option(key, fallback ? "1" : "0") == "1";
    }

    /// <summary>The options panel: its rows one under the other.</summary>
    private protected static StackPanel OptionsPanel(params UIElement[] rows)
    {
        var panel = new StackPanel { Spacing = 12, Width = 300 };
        foreach (UIElement row in rows)
        {
            panel.Children.Add(row);
        }

        return panel;
    }

    /// <summary>One choice among a few, side by side; kept under <paramref name="key"/>.</summary>
    private protected RadioButtons Choice(string header, string key, string fallback, params (string Value, string Label)[] choices)
    {
        // Side by side when the labels are short (two always fit; three when each is short), else one under the other.
        bool short3 = true;
        foreach ((string Value, string Label) choice in choices)
        {
            short3 &= choice.Label.Length <= 8;
        }

        var buttons = new RadioButtons { Header = header, MaxColumns = choices.Length <= 2 || short3 ? choices.Length : 1 };
        string now = Option(key, fallback);
        for (int i = 0; i < choices.Length; i++)
        {
            buttons.Items.Add(new RadioButton { Content = choices[i].Label, MinWidth = 0 });
            if (choices[i].Value == now)
            {
                buttons.SelectedIndex = i;
            }
        }

        AutomationProperties.SetName(buttons, header);
        buttons.SelectionChanged += (_, _) =>
        {
            int picked = buttons.SelectedIndex;
            if (picked >= 0 && picked < choices.Length)
            {
                Keep(key, choices[picked].Value);
            }
        };
        return buttons;
    }

    /// <summary>A choice that is on or off; kept under <paramref name="key"/>.</summary>
    private protected ToggleSwitch Switch(string header, string key, bool fallback)
    {
        var toggle = new ToggleSwitch { Header = header, IsOn = OptionOn(key, fallback) };
        AutomationProperties.SetName(toggle, header);
        toggle.Toggled += (_, _) => Keep(key, toggle.IsOn ? "1" : "0");
        return toggle;
    }

    /// <summary>The usual left, centre (and right) choice.</summary>
    private protected RadioButtons AlignChoice(bool withRight)
    {
        return withRight
            ? Choice(Strings.WgOptAlign, AlignKey, "Left", ("Left", Strings.WgOptLeft), ("Center", Strings.WgOptCenter), ("Right", Strings.WgOptRight))
            : Choice(Strings.WgOptAlign, AlignKey, "Left", ("Left", Strings.WgOptLeft), ("Center", Strings.WgOptCenter));
    }

    /// <summary>The usual small, medium, large choice.</summary>
    private protected RadioButtons SizeChoice(string header)
    {
        return Choice(header, SizeKey, "Medium", ("Small", Strings.WgOptSmall), ("Medium", Strings.WgOptMedium), ("Large", Strings.WgOptLarge));
    }

    /// <summary>The kept alignment for a panel, and for text.</summary>
    private protected HorizontalAlignment Across => Option(AlignKey, "Left") switch
    {
        "Center" => HorizontalAlignment.Center,
        "Right" => HorizontalAlignment.Right,
        _ => HorizontalAlignment.Left,
    };

    private protected TextAlignment TextAcross => Option(AlignKey, "Left") switch
    {
        "Center" => TextAlignment.Center,
        "Right" => TextAlignment.Right,
        _ => TextAlignment.Left,
    };

    /// <summary>Small, medium or large as one of three numbers.</summary>
    private protected double BySize(double small, double medium, double large) => Option(SizeKey, "Medium") switch
    {
        "Small" => small,
        "Large" => large,
        _ => medium,
    };

    private protected const string AlignKey = "Align";
    private protected const string SizeKey = "Size";

    private void Keep(string key, string value)
    {
        Host.WidgetItems.SetData(Item.Id, key, value);
        ApplyOptions();
    }
}

/// <summary>
/// The text note: a box to type in, kept in widgets.json as it is typed (a moment after the
/// last key, when the box loses the keyboard, and when the sidebar hides or Machitan exits).
/// </summary>
internal sealed class NoteWidget : WidgetView
{
    private const string TextKey = "Text";

    /// <summary>How long after the last key the note is saved.</summary>
    private const int SaveAfterMs = 800;

    /// <summary>Its box's height while it is as tall as it wants (in a sidebar card not resized).</summary>
    private const double AutoMinHeight = 120;
    private const double AutoMaxHeight = 400;

    private readonly DispatcherQueueTimer _saveTimer;
    private TextBox? _box;
    private bool _dirty;

    internal NoteWidget(ShellHost host, WidgetItem item)
        : base(host, item)
    {
        _saveTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _saveTimer.Interval = TimeSpan.FromMilliseconds(SaveAfterMs);
        _saveTimer.IsRepeating = false;
        _saveTimer.Tick += (_, _) => Flush();
    }

    internal override bool HasUserContent => (_box?.Text ?? Item.Get(TextKey)).Length > 0;

    internal override FrameworkElement Build()
    {
        var box = new TextBox
        {
            Text = Item.Get(TextKey),
            PlaceholderText = Strings.WgNoteHint,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MaxLength = WidgetStore.MaxValueLength,
            MinHeight = AutoMinHeight,
            MaxHeight = AutoMaxHeight,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(box, ScrollBarVisibility.Auto);
        AutomationProperties.SetName(box, Strings.WgNote);

        // Its own cut, copy and paste menus, kept inside the sidebar's window (see SidebarWindow).
        box.ContextFlyout = new TextCommandBarFlyout { ShouldConstrainToRootBounds = true };
        box.SelectionFlyout = new TextCommandBarFlyout { ShouldConstrainToRootBounds = true };
        box.TextChanged += (_, _) =>
        {
            _dirty = true;
            _saveTimer.Stop();
            _saveTimer.Start();
        };
        box.LostFocus += (_, _) => Flush();
        _box = box;
        ApplyOptions();
        return box;
    }

    internal override void Flush()
    {
        _saveTimer.Stop();
        if (!_dirty || _box is null)
        {
            return;
        }

        _dirty = false;
        Host.WidgetItems.SetData(Item.Id, TextKey, _box.Text);
    }

    internal override bool HasOptions => true;

    internal override FrameworkElement BuildOptions()
    {
        return OptionsPanel(SizeChoice(Strings.WgOptTextSize), AlignChoice(withRight: false));
    }

    /// <summary>The note's text size (12, 14 or 18) and alignment.</summary>
    private protected override void ApplyOptions()
    {
        if (_box is null)
        {
            return;
        }

        _box.FontSize = BySize(12, 14, 18);
        _box.TextAlignment = TextAcross;
    }

    internal override void SetFixedHeight(bool fixedHeight)
    {
        if (_box is null)
        {
            return;
        }

        _box.MinHeight = fixedHeight ? 0 : AutoMinHeight;
        _box.MaxHeight = fixedHeight ? double.PositiveInfinity : AutoMaxHeight;
    }

    internal override void FocusFirst()
    {
        TextBox? box = _box;
        if (box is null)
        {
            return;
        }

        if (box.IsLoaded)
        {
            box.Focus(FocusState.Programmatic);
            return;
        }

        // Just made: once it is on screen.
        void OnLoaded(object sender, RoutedEventArgs e)
        {
            box.Loaded -= OnLoaded;
            box.Focus(FocusState.Programmatic);
        }

        box.Loaded += OnLoaded;
    }
}
