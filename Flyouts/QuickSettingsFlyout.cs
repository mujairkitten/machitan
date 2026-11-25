using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Machitan.Core;
using Machitan.Interop;
using Machitan.Shell;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace Machitan.Flyouts;

/// <summary>
/// Win+A: the action center's switches on top (Wi-Fi, Bluetooth, airplane mode, mobile hotspot,
/// live captions, each as Windows reports it, see Shell/QuickToggles.cs; and Project and Cast,
/// which open Windows' own Win+P and Win+K flyouts), then volume (Windows' default speakers,
/// through Core Audio) and screen brightness (the laptop's own screen through WMI, external
/// monitors over DDC/CI); next to the status ring while it is shown (below it, in a top corner),
/// else lined up with the bar like Start. Each control is there only when Windows can really do
/// it: no speakers, no volume; no screen whose brightness can be set, no brightness; no Bluetooth
/// radio, no Bluetooth switch. A switch shows what Windows says, never what was asked: after a
/// click it waits (greyed) until Windows reports the new state, or gives up after a few seconds.
/// </summary>
public sealed partial class QuickSettingsFlyout : FlyoutWindow
{
    private const double WidthDip = 360;
    private const double InnerPadDip = 16;
    private const double RowHeightDip = 68;
    private const double NoteHeightDip = 40;
    private const double SpacingDip = 12;

    private const double TileButtonDip = 48;
    private const double TileLabelDip = 18;
    private const double TileGapDip = 4;
    private const double TileRowSpacingDip = 12;
    private const double TileColumnSpacingDip = 8;
    private const int TileColumns = 3;

    /// <summary>After a switch is clicked, how long it waits for Windows to report the new state.</summary>
    private const long SwitchWaitMs = 6000;

    private const int WifiGlyph = 0xE701;
    private const int BluetoothGlyph = 0xE702;
    private const int AirplaneGlyph = 0xE709;
    private const int HotspotGlyph = 0xE88A;
    private const int CaptionsGlyph = 0xE7F0;
    private const int ProjectGlyph = 0xEBC6;
    private const int CastGlyph = 0xE703;

    private const int VolumeGlyph = 0xE767;
    private const int MutedGlyph = 0xE74F;
    private const int BrightnessGlyph = 0xE706;

    /// <summary>After the user moves a control, Windows' value is not read back for this long.</summary>
    private const long UserTouchQuietMs = 800;

    private readonly VolumeService _volume = new();
    private readonly BrightnessService _brightness = new();
    private readonly StackPanel _rows;
    private readonly DispatcherQueueTimer _poll;
    private readonly DispatcherQueueTimer _brightnessPoll;
    private readonly List<(BrightnessDisplay Display, Slider Slider, TextBlock Value)> _brightnessRows = new();
    private readonly QuickToggles _toggles = new();
    private readonly DispatcherQueueTimer _togglePoll;
    private readonly Dictionary<QuickToggle, (ToggleButton Button, TextBlock Label)> _tiles = new();
    private readonly HashSet<QuickToggle> _busy = new();

    /// <summary>Switches clicked whose new state Windows has not reported yet: the state asked for, and until when to wait.</summary>
    private readonly Dictionary<QuickToggle, (bool On, long Until)> _waiting = new();

    private Grid? _tileGrid;
    private int _tileRows;
    private bool _checkingToggles;

    private Slider? _volumeSlider;
    private FontIcon? _volumeIcon;
    private TextBlock? _volumeValue;
    private bool _muted;
    private bool _updating;
    private bool _checkingScreens;
    private bool _screensCheckedOnce;
    private long _touchedAt;
    private long _brightnessTouchedAt;
    private bool _following;

    internal QuickSettingsFlyout(ShellHost host)
        : base(host, "Quick settings")
    {
        _rows = new StackPanel
        {
            Padding = new Thickness(InnerPadDip),
            Spacing = SpacingDip,
        };
        Root.Children.Add(MakeBox(_rows, BoxRadius));

        _poll = DispatcherQueue.CreateTimer();
        _poll.Interval = TimeSpan.FromMilliseconds(300);
        _poll.IsRepeating = true;
        _poll.Tick += (_, _) => ReadVolume();

        // The laptop's brightness keys change the level outside Machitan; follow them.
        _brightnessPoll = DispatcherQueue.CreateTimer();
        _brightnessPoll.Interval = TimeSpan.FromSeconds(1);
        _brightnessPoll.IsRepeating = true;
        _brightnessPoll.Tick += (_, _) => _ = FollowBrightnessAsync();

        // The switches follow changes made elsewhere too (a keyboard key, Windows' own quick settings).
        _togglePoll = DispatcherQueue.CreateTimer();
        _togglePoll.Interval = TimeSpan.FromSeconds(1);
        _togglePoll.IsRepeating = true;
        _togglePoll.Tick += (_, _) => ShowToggles();
    }

    /// <summary>Win+A: open, or close when already open.</summary>
    internal void Toggle()
    {
        if (IsOpen)
        {
            HideFlyout();
            return;
        }

        _volume.Connect();
        _checkingScreens = true;
        Rebuild();
        ReadVolume();
        _poll.Start();
        _brightnessPoll.Start();
        _togglePoll.Start();
        ShowSized();
        _ = CheckScreensAsync();
        _ = CheckTogglesAsync();
    }

    /// <summary>Opens by the status ring while it is shown (the ring's own click still goes to Windows).</summary>
    private protected override Machitan.Interop.RECT? WidgetAnchor => Host.StatusRingRect();

    private protected override void OnHiding()
    {
        _poll.Stop();
        _brightnessPoll.Stop();
        _togglePoll.Stop();
    }

    private void ShowSized()
    {
        int rows = _rows.Children.Count;
        double height = 2 * InnerPadDip;
        foreach (UIElement row in _rows.Children)
        {
            height += ReferenceEquals(row, _tileGrid) ? TilesHeightDip()
                : row is TextBlock ? NoteHeightDip
                : RowHeightDip;
        }

        height += Math.Max(0, rows - 1) * SpacingDip;
        Present(WidthDip, height, FlyoutPlacement.BesideBar);
    }

    private async Task CheckScreensAsync()
    {
        IReadOnlyList<BrightnessDisplay> displays;
        try
        {
            displays = await _brightness.CheckAsync();
        }
        catch (Exception ex)
        {
            Log.Error("Quick settings: checking the screens failed.", ex);
            displays = Array.Empty<BrightnessDisplay>();
        }

        // Back on the UI thread (the await resumes where it started).
        _checkingScreens = false;
        _screensCheckedOnce = true;
        if (!IsOpen)
        {
            return;
        }

        Rebuild();
        ReadVolume();
        ShowSized();
    }

    private void Rebuild()
    {
        _rows.Children.Clear();
        _brightnessRows.Clear();
        _volumeSlider = null;
        _volumeIcon = null;
        _volumeValue = null;

        _tileGrid = MakeTiles();
        _rows.Children.Add(_tileGrid);

        if (_volume.IsAvailable)
        {
            _rows.Children.Add(MakeVolumeRow());
        }

        IReadOnlyList<BrightnessDisplay> displays = _brightness.Displays;
        foreach (BrightnessDisplay display in displays)
        {
            _rows.Children.Add(MakeBrightnessRow(display, displays.Count > 1));
        }

        // Only the very first check is shown; later ones refresh the rows quietly.
        if (_checkingScreens && !_screensCheckedOnce && displays.Count == 0)
        {
            _rows.Children.Add(MakeNote(Strings.QsChecking));
        }
        else if (!_volume.IsAvailable && displays.Count == 0)
        {
            _rows.Children.Add(MakeNote(Strings.QsNothing));
        }
    }

    // ---------------------------------------------------------------- the action center

    /// <summary>
    /// The switches this PC has, then Project and Cast, three to a row like Windows' own: a
    /// button with the icon (filled with the accent while on), and its name below.
    /// </summary>
    private Grid MakeTiles()
    {
        _tiles.Clear();
        var tiles = new List<FrameworkElement>();
        foreach (QuickToggle toggle in _toggles.Available)
        {
            tiles.Add(MakeToggleTile(toggle));
        }

        tiles.Add(MakeActionTile(ProjectGlyph, Strings.QsProject, 'P'));
        tiles.Add(MakeActionTile(CastGlyph, Strings.QsCast, 'K'));

        var grid = new Grid
        {
            ColumnSpacing = TileColumnSpacingDip,
            RowSpacing = TileRowSpacingDip,
        };

        for (int column = 0; column < TileColumns; column++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }

        _tileRows = (tiles.Count + TileColumns - 1) / TileColumns;
        for (int row = 0; row < _tileRows; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        for (int i = 0; i < tiles.Count; i++)
        {
            Grid.SetRow(tiles[i], i / TileColumns);
            Grid.SetColumn(tiles[i], i % TileColumns);
            grid.Children.Add(tiles[i]);
        }

        return grid;
    }

    private double TilesHeightDip()
    {
        return (_tileRows * (TileButtonDip + TileGapDip + TileLabelDip)) + (Math.Max(0, _tileRows - 1) * TileRowSpacingDip);
    }

    private static StackPanel MakeTile(ButtonBase button, int glyph, string label, out TextBlock text)
    {
        button.Content = MakeGlyph(glyph, 16);
        button.Height = TileButtonDip;
        button.Padding = new Thickness(0);
        button.HorizontalAlignment = HorizontalAlignment.Stretch;
        button.HorizontalContentAlignment = HorizontalAlignment.Center;
        button.VerticalContentAlignment = VerticalAlignment.Center;
        button.CornerRadius = new CornerRadius(SmallRadius);
        AutomationProperties.SetName(button, label);

        text = new TextBlock
        {
            Text = label,
            FontSize = 12,
            Height = TileLabelDip,
            MaxLines = 1,
            TextAlignment = TextAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var tile = new StackPanel { Spacing = TileGapDip };
        tile.Children.Add(button);
        tile.Children.Add(text);
        return tile;
    }

    private FrameworkElement MakeToggleTile(QuickToggle toggle)
    {
        (int glyph, string label) = toggle switch
        {
            QuickToggle.WiFi => (WifiGlyph, Strings.QsWifi),
            QuickToggle.Bluetooth => (BluetoothGlyph, Strings.QsBluetooth),
            QuickToggle.AirplaneMode => (AirplaneGlyph, Strings.QsAirplaneMode),
            QuickToggle.MobileHotspot => (HotspotGlyph, Strings.QsHotspot),
            _ => (CaptionsGlyph, Strings.QsLiveCaptions),
        };

        var button = new ToggleButton();
        StackPanel tile = MakeTile(button, glyph, label, out TextBlock text);
        button.Click += (_, _) => _ = SwitchAsync(toggle, button);
        _tiles[toggle] = (button, text);
        ShowToggle(toggle);
        return tile;
    }

    /// <summary>Project (Win+P) and Cast (Win+K): Windows' own flyouts, which open in the same corner, so this one goes first.</summary>
    private StackPanel MakeActionTile(int glyph, string label, char key)
    {
        var button = new Button();
        StackPanel tile = MakeTile(button, glyph, label, out _);
        button.Click += (_, _) =>
        {
            HideFlyout();
            if (!KeyChord.SendWin(key))
            {
                Log.Warn("Quick settings: Windows did not accept the Win+" + key + " key press.");
            }
        };
        return tile;
    }

    /// <summary>
    /// A switch was clicked. The click already flipped the button; it goes back to what Windows
    /// says at once, greyed, and follows Windows from there (see ShowToggle).
    /// </summary>
    private async Task SwitchAsync(QuickToggle toggle, ToggleButton button)
    {
        bool on = button.IsChecked == true;
        if (!_busy.Add(toggle))
        {
            ShowToggle(toggle);
            return;
        }

        ShowToggle(toggle);
        bool done = false;
        try
        {
            done = await _toggles.SetAsync(toggle, on);
            Log.Info("Quick settings: " + toggle + (on ? " on" : " off") + (done ? " asked of Windows." : " not done."));
        }
        finally
        {
            _busy.Remove(toggle);
            if (done)
            {
                _waiting[toggle] = (on, Environment.TickCount64 + SwitchWaitMs);
            }

            ShowToggle(toggle);
        }
    }

    /// <summary>Asks Windows which switches this PC has; rebuilds the flyout when that changed.</summary>
    private async Task CheckTogglesAsync()
    {
        if (_checkingToggles)
        {
            return;
        }

        _checkingToggles = true;
        try
        {
            var before = new List<QuickToggle>(_toggles.Available);
            await _toggles.CheckAsync();

            // Back on the UI thread.
            if (!IsOpen)
            {
                return;
            }

            bool same = before.Count == _toggles.Available.Count;
            for (int i = 0; same && i < before.Count; i++)
            {
                same = before[i] == _toggles.Available[i];
            }

            if (same)
            {
                ShowToggles();
            }
            else
            {
                Rebuild();
                ReadVolume();
                ShowSized();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Quick settings: the switches could not be checked.", ex);
        }
        finally
        {
            _checkingToggles = false;
        }
    }

    private void ShowToggles()
    {
        foreach (QuickToggle toggle in _tiles.Keys)
        {
            ShowToggle(toggle);
        }
    }

    /// <summary>A switch as Windows has it now; greyed while Windows cannot do it, or is still doing what was asked.</summary>
    private void ShowToggle(QuickToggle toggle)
    {
        if (!_tiles.TryGetValue(toggle, out (ToggleButton Button, TextBlock Label) tile))
        {
            return;
        }

        SwitchState state = _toggles.StateOf(toggle);
        bool waiting = _busy.Contains(toggle);
        if (_waiting.TryGetValue(toggle, out (bool On, long Until) asked))
        {
            if ((state == SwitchState.On) == asked.On || Environment.TickCount64 > asked.Until)
            {
                _waiting.Remove(toggle);
            }
            else
            {
                waiting = true;
            }
        }

        bool on = state == SwitchState.On;
        if (tile.Button.IsChecked != on)
        {
            tile.Button.IsChecked = on;
        }

        tile.Button.IsEnabled = state != SwitchState.Unavailable && !waiting;
        if (toggle == QuickToggle.WiFi)
        {
            string label = on && QuickToggles.WifiName() is string network ? network : Strings.QsWifi;
            if (tile.Label.Text != label)
            {
                tile.Label.Text = label;
                ToolTipService.SetToolTip(tile.Button, label);
            }
        }
    }

    // ---------------------------------------------------------------- volume and brightness

    private FrameworkElement MakeVolumeRow()
    {
        string label = _volume.DeviceName.Length > 0 ? Fmt.F(Strings.QsVolumeOf, _volume.DeviceName) : Strings.QsVolume;

        _volumeIcon = MakeGlyph(VolumeGlyph, 16);
        var mute = new Button
        {
            Content = _volumeIcon,
            Width = 40,
            Height = 36,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(SmallRadius),
        };
        AutomationProperties.SetName(mute, Strings.QsMute);
        mute.Click += (_, _) =>
        {
            _touchedAt = Environment.TickCount64;
            _muted = !_muted;
            _volume.SetMute(_muted);
            ShowMute();
        };

        Slider slider = MakeSlider(Strings.QsVolume);
        TextBlock value = MakeValueText();
        slider.ValueChanged += (_, e) =>
        {
            if (_updating)
            {
                return;
            }

            _touchedAt = Environment.TickCount64;
            _volume.SetLevel((float)(e.NewValue / 100.0));
            ShowValue(value, e.NewValue);
        };

        _volumeSlider = slider;
        _volumeValue = value;
        return MakeRow(label, mute, slider, value);
    }

    private FrameworkElement MakeBrightnessRow(BrightnessDisplay display, bool several)
    {
        string label = !several
            ? Strings.QsBrightness
            : display.Kind == BrightnessKind.BuiltIn ? Strings.QsBrightnessBuiltIn : Fmt.F(Strings.QsBrightnessDisplay, display.Number);
        var icon = new Border
        {
            Width = 40,
            Height = 36,
            Child = MakeGlyph(BrightnessGlyph, 16),
        };

        Slider slider = MakeSlider(label);
        TextBlock value = MakeValueText();
        _updating = true;
        slider.Value = Math.Round(display.CurrentPercent);
        _updating = false;
        ShowValue(value, slider.Value);

        slider.ValueChanged += (_, e) =>
        {
            if (_updating)
            {
                return;
            }

            _brightnessTouchedAt = Environment.TickCount64;
            _brightness.Set(display, e.NewValue);
            ShowValue(value, e.NewValue);
        };

        _brightnessRows.Add((display, slider, value));
        return MakeRow(label, icon, slider, value);
    }

    private static Grid MakeRow(string label, FrameworkElement lead, Slider slider, TextBlock value)
    {
        var title = new TextBlock
        {
            Text = label,
            FontSize = 12,
            Opacity = 0.8,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var controls = new Grid { ColumnSpacing = 8 };
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(36) });
        controls.Children.Add(lead);
        Grid.SetColumn(slider, 1);
        controls.Children.Add(slider);
        Grid.SetColumn(value, 2);
        controls.Children.Add(value);

        var row = new Grid { Height = RowHeightDip, RowSpacing = 4 };
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(title);
        Grid.SetRow(controls, 1);
        row.Children.Add(controls);
        return row;
    }

    private static Slider MakeSlider(string name)
    {
        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            StepFrequency = 1,
            SmallChange = 1,
            LargeChange = 10,
            IsThumbToolTipEnabled = false,
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(slider, name);
        return slider;
    }

    private static TextBlock MakeValueText()
    {
        return new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Right,
            FontSize = 13,
        };
    }

    private static TextBlock MakeNote(string text)
    {
        return new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8,
            VerticalAlignment = VerticalAlignment.Center,
            Height = NoteHeightDip,
        };
    }

    private static void ShowValue(TextBlock? text, double value)
    {
        if (text is not null)
        {
            text.Text = Math.Round(value).ToString(CultureInfo.CurrentCulture);
        }
    }

    /// <summary>Follows the built-in screen's level when it changes elsewhere (brightness keys, Windows' slider).</summary>
    private async Task FollowBrightnessAsync()
    {
        if (_following || !IsOpen || _brightnessRows.Count == 0)
        {
            return;
        }

        _following = true;
        try
        {
            IReadOnlyList<(BrightnessDisplay Display, double Percent)> levels = await _brightness.ReadBuiltInAsync();

            // Back on the UI thread. A level the user is setting right now wins.
            if (!IsOpen || Environment.TickCount64 - _brightnessTouchedAt < 2 * UserTouchQuietMs)
            {
                return;
            }

            foreach ((BrightnessDisplay display, double percent) in levels)
            {
                foreach ((BrightnessDisplay rowDisplay, Slider slider, TextBlock value) in _brightnessRows)
                {
                    if (!ReferenceEquals(rowDisplay, display) || Math.Abs(slider.Value - Math.Round(percent)) < 1)
                    {
                        continue;
                    }

                    _updating = true;
                    slider.Value = Math.Round(percent);
                    _updating = false;
                    ShowValue(value, slider.Value);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Quick settings: the screen's brightness could not be read back.", ex);
        }
        finally
        {
            _following = false;
        }
    }

    /// <summary>Follows changes made elsewhere (keyboard volume keys, Windows' own slider).</summary>
    private void ReadVolume()
    {
        if (_volumeSlider is null || Environment.TickCount64 - _touchedAt < UserTouchQuietMs)
        {
            return;
        }

        if (!_volume.TryRead(out float level, out bool muted))
        {
            return;
        }

        double percent = Math.Round(level * 100);
        if (Math.Abs(_volumeSlider.Value - percent) >= 1)
        {
            _updating = true;
            _volumeSlider.Value = percent;
            _updating = false;
        }

        ShowValue(_volumeValue, _volumeSlider.Value);
        if (muted != _muted)
        {
            _muted = muted;
        }

        ShowMute();
    }

    private void ShowMute()
    {
        if (_volumeIcon is not null)
        {
            _volumeIcon.Glyph = ((char)(_muted ? MutedGlyph : VolumeGlyph)).ToString();
        }

        if (_volumeSlider is not null)
        {
            _volumeSlider.Opacity = _muted ? 0.5 : 1.0;
        }
    }
}
