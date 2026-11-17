using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Machitan.Core;
using Machitan.Shell;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace Machitan.Flyouts;

/// <summary>
/// Win+A: volume (Windows' default speakers, through Core Audio) and screen brightness
/// (the laptop's own screen through WMI, external monitors over DDC/CI), lined up with the bar
/// like Start. Each control is there only when Windows can really do it: no speakers, no
/// volume; no screen whose brightness can be set, no brightness.
/// </summary>
public sealed partial class QuickSettingsFlyout : FlyoutWindow
{
    private const double WidthDip = 360;
    private const double InnerPadDip = 16;
    private const double RowHeightDip = 68;
    private const double NoteHeightDip = 40;
    private const double SpacingDip = 12;

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
        ShowSized();
        _ = CheckScreensAsync();
    }

    private protected override void OnHiding()
    {
        _poll.Stop();
        _brightnessPoll.Stop();
    }

    private void ShowSized()
    {
        int rows = _rows.Children.Count;
        double height = 2 * InnerPadDip;
        foreach (UIElement row in _rows.Children)
        {
            height += row is TextBlock ? NoteHeightDip : RowHeightDip;
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
            _rows.Children.Add(MakeNote("Checking which screens can change brightness..."));
        }
        else if (!_volume.IsAvailable && displays.Count == 0)
        {
            _rows.Children.Add(MakeNote("Windows offers no volume or brightness control on this PC right now."));
        }
    }

    private FrameworkElement MakeVolumeRow()
    {
        string label = _volume.DeviceName.Length > 0 ? "Volume - " + _volume.DeviceName : "Volume";

        _volumeIcon = MakeGlyph(VolumeGlyph, 16);
        var mute = new Button
        {
            Content = _volumeIcon,
            Width = 40,
            Height = 36,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(SmallRadius),
        };
        AutomationProperties.SetName(mute, "Mute");
        mute.Click += (_, _) =>
        {
            _touchedAt = Environment.TickCount64;
            _muted = !_muted;
            _volume.SetMute(_muted);
            ShowMute();
        };

        Slider slider = MakeSlider("Volume");
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
            ? "Brightness"
            : display.Kind == BrightnessKind.BuiltIn ? "Brightness - built-in screen" : "Brightness - display " + display.Number;
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
