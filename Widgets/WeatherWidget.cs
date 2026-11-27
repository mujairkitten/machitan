using System;
using System.Globalization;
using Machitan.Config;
using Machitan.Core;
using Machitan.Shell;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Machitan.Widgets;

/// <summary>
/// The Weather widget: the weather now from Open-Meteo, as the info line has it (one shared
/// WeatherService, so it is asked once for both): its symbol, temperature and description,
/// today's highest and lowest when Open-Meteo gave them, the place (a chosen one, or Windows'
/// location), and when it was read. Uses the weather settings (Settings > Indicators). Without
/// a real reading from the last two hours it says so, with a button to those settings; nothing
/// is made up. Runs the weather only while it is shown. Its choices (Customize): full or
/// compact (the symbol, the temperature and the description only), Celsius or Fahrenheit,
/// left or centre, and whether the place and today's high and low show.
/// </summary>
internal sealed class WeatherWidget : WidgetView
{
    private readonly DispatcherQueueTimer _tick;
    private TextBlock? _symbol;
    private TextBlock? _temperature;
    private TextBlock? _description;
    private TextBlock? _range;
    private TextBlock? _place;
    private TextBlock? _source;
    private StackPanel? _reading;
    private StackPanel? _none;
    private StackPanel? _now;
    private bool _shown;

    private const string LayoutKey = "Layout";
    private const string UnitKey = "Unit";
    private const string PlaceKey = "Place";
    private const string RangeKey = "Range";

    internal WeatherWidget(ShellHost host, WidgetItem item)
        : base(host, item)
    {
        // Once a minute: a reading that grew too old is taken away.
        _tick = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _tick.Interval = TimeSpan.FromMinutes(1);
        _tick.IsRepeating = true;
        _tick.Tick += (_, _) => Show();
    }

    internal override double DesktopHeightDip => 130;

    internal override bool HasOptions => true;

    internal override FrameworkElement BuildOptions()
    {
        return OptionsPanel(
            Choice(Strings.WgOptLayout, LayoutKey, "Full", ("Full", Strings.WgOptFull), ("Compact", Strings.WgOptCompact)),
            Choice(Strings.WgOptUnit, UnitKey, "C", ("C", Strings.WgOptCelsius), ("F", Strings.WgOptFahrenheit)),
            AlignChoice(withRight: false),
            Switch(Strings.WgOptPlace, PlaceKey, fallback: true),
            Switch(Strings.WgOptRange, RangeKey, fallback: true));
    }

    private protected override void ApplyOptions()
    {
        Show();
    }

    internal override FrameworkElement Build()
    {
        _symbol = new TextBlock
        {
            FontFamily = new FontFamily("Segoe UI Symbol"),
            FontSize = 36,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _temperature = new TextBlock
        {
            FontSize = 36,
            FontWeight = FontWeights.SemiLight,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var now = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        now.Children.Add(_symbol);
        now.Children.Add(_temperature);
        _now = now;

        _description = Line(14, 1);
        _range = Line(12, 0.8);
        _place = Line(12, 0.8);
        _source = Line(11, 0.6);
        _reading = new StackPanel { Spacing = 2, Visibility = Visibility.Collapsed };
        _reading.Children.Add(now);
        _reading.Children.Add(_description);
        _reading.Children.Add(_range);
        _reading.Children.Add(_place);
        _reading.Children.Add(_source);

        var why = new TextBlock { Text = Strings.WgWeatherNone, TextWrapping = TextWrapping.Wrap, Opacity = 0.8 };
        var settings = new Button { Content = Strings.WgWeatherSettings };
        settings.Click += (_, _) => Host.OpenSettings(Machitan.SettingsUi.SettingsPages.Widgets);
        _none = new StackPanel { Spacing = 10 };
        _none.Children.Add(why);
        _none.Children.Add(settings);

        var panel = new Grid { VerticalAlignment = VerticalAlignment.Top };
        panel.Children.Add(_reading);
        panel.Children.Add(_none);
        Show();
        return panel;
    }

    internal override void SetShown(bool shown)
    {
        if (shown == _shown)
        {
            return;
        }

        _shown = shown;
        WeatherService weather = Host.Weather;
        if (shown)
        {
            weather.Changed += Show;
            Host.Settings.Changed += OnSettingsChanged;
            OnSettingsChanged();
            _tick.Start();
        }
        else
        {
            _tick.Stop();
            weather.Changed -= Show;
            Host.Settings.Changed -= OnSettingsChanged;
            weather.SetWanted(this, false);
        }
    }

    private static TextBlock Line(double size, double opacity)
    {
        return new TextBlock
        {
            FontSize = size,
            Opacity = opacity,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
    }

    /// <summary>The weather follows the place in Settings (as the info line does).</summary>
    private void OnSettingsChanged()
    {
        ShellSettings settings = Host.Settings.Current;
        Host.Weather.UsePlace(settings.WeatherUseManualPlace, settings.WeatherLatitude, settings.WeatherLongitude);
        Host.Weather.SetWanted(this, true);
        Show();
    }

    private void Show()
    {
        if (_reading is null || _none is null)
        {
            return;
        }

        WeatherReading? reading = Host.Weather.Current is { IsFresh: true } fresh ? fresh : null;
        _reading.Visibility = reading is not null ? Visibility.Visible : Visibility.Collapsed;
        _none.Visibility = reading is null ? Visibility.Visible : Visibility.Collapsed;
        if (reading is null)
        {
            return;
        }

        ShellSettings settings = Host.Settings.Current;
        bool full = Option(LayoutKey, "Full") == "Full";
        bool fahrenheit = Option(UnitKey, "C") == "F";
        Set(_symbol, reading.Symbol);
        Set(_temperature, Degrees(reading.TemperatureC, fahrenheit) + (fahrenheit ? "F" : "C"));
        Set(_description, reading.Description);
        bool range = full && OptionOn(RangeKey, fallback: true) && reading.TodayMaxC is double && reading.TodayMinC is double;
        if (range)
        {
            Set(_range, Fmt.F(Strings.WgWeatherRange, Degrees(reading.TodayMaxC!.Value, fahrenheit), Degrees(reading.TodayMinC!.Value, fahrenheit)));
        }

        Set(_place, settings.WeatherUseManualPlace && settings.WeatherPlaceName.Length > 0
            ? settings.WeatherPlaceName
            : Strings.WgWeatherHere);
        Set(_source, Fmt.F(Strings.WgWeatherSource, reading.FetchedAt.ToString("t", CultureInfo.CurrentCulture)));
        AutomationProperties.SetName(_reading, _temperature!.Text + ", " + reading.Description + ", " + _place!.Text);

        // Compact: the symbol, the temperature and the description.
        _range!.Visibility = range ? Visibility.Visible : Visibility.Collapsed;
        _place!.Visibility = OptionOn(PlaceKey, fallback: true) ? Visibility.Visible : Visibility.Collapsed;
        _source!.Visibility = full ? Visibility.Visible : Visibility.Collapsed;
        HorizontalAlignment across = Across;
        TextAlignment text = TextAcross;
        _reading.HorizontalAlignment = across;
        _none.HorizontalAlignment = across;
        if (_now is not null)
        {
            _now.HorizontalAlignment = across;
        }

        foreach (TextBlock? line in new[] { _description, _range, _place, _source })
        {
            if (line is not null)
            {
                line.TextAlignment = text;
                line.HorizontalAlignment = across;
            }
        }
    }

    /// <summary>A temperature rounded, with the degree sign, in Celsius or Fahrenheit (C x 9/5 + 32).</summary>
    private static string Degrees(double celsius, bool fahrenheit)
    {
        double value = fahrenheit ? (celsius * 9 / 5) + 32 : celsius;
        return Math.Round(value).ToString("0", CultureInfo.CurrentCulture) + "\u00B0";
    }

    private static void Set(TextBlock? block, string text)
    {
        if (block is not null && block.Text != text)
        {
            block.Text = text;
        }
    }
}
