using System;
using System.Globalization;
using Machitan.Config;
using Machitan.Core;
using Machitan.Custom;
using Machitan.Interop;
using Machitan.Shell;
using Machitan.Visuals;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Machitan.Widgets;

/// <summary>
/// The corner info line: one capsule with the date, the time (seconds optional) and the
/// weather (each can be turned off; the line hides when nothing is left). Same fill as the
/// bar. The weather is for Windows' location or a place chosen in Settings. A click opens
/// Machitan's own Win+N flyout (the calendar) next to the line, and closes it again.
/// </summary>
public sealed partial class InfoLineWindow : CornerWidgetWindow
{
    private const double WindowWidthDip = 420;
    private const double CapsuleRadius = LineHeightDip / 2;
    private const string Separator = "\u00B7";

    private static readonly FontFamily SymbolFont = new("Segoe UI Symbol");

    private readonly Grid _capsule;
    private readonly Border _hover;
    private readonly StackPanel _row;
    private readonly TextBlock _date;
    private readonly TextBlock _dateDot;
    private readonly TextBlock _time;
    private readonly TextBlock _weatherDot;
    private readonly TextBlock _weatherSymbol;
    private readonly TextBlock _temperature;
    private readonly DispatcherQueueTimer _clock;
    private readonly WeatherService _weather;
    private FrameworkElement? _fill;
    private string _fillLook = string.Empty;
    private bool _clickedOnce;

    internal InfoLineWindow(ShellHost host)
        : base(host, "info line", WindowWidthDip)
    {
        _date = MakeText(semiBold: false);
        _dateDot = MakeText(semiBold: false, dim: true);
        _dateDot.Text = Separator;
        _time = MakeText(semiBold: true);
        _weatherDot = MakeText(semiBold: false, dim: true);
        _weatherDot.Text = Separator;
        _weatherSymbol = new TextBlock
        {
            FontFamily = SymbolFont,
            FontSize = 13,
            IsColorFontEnabled = false,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _temperature = MakeText(semiBold: false);

        _row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Padding = new Thickness(14, 0, 14, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _row.Children.Add(_date);
        _row.Children.Add(_dateDot);
        _row.Children.Add(_time);
        _row.Children.Add(_weatherDot);
        _row.Children.Add(_weatherSymbol);
        _row.Children.Add(_temperature);

        _hover = new Border
        {
            CornerRadius = new CornerRadius(CapsuleRadius),
            Background = new SolidColorBrush(Colors.Transparent),
        };

        _capsule = new Grid
        {
            Height = LineHeightDip,
            CornerRadius = new CornerRadius(CapsuleRadius),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Background = new SolidColorBrush(Colors.Transparent),
        };
        _capsule.Children.Add(_hover);
        _capsule.Children.Add(_row);
        AutomationProperties.SetName(_capsule, Strings.InfoLineName);
        _capsule.Tapped += (_, _) => OnClicked();
        _capsule.PointerEntered += (_, _) => SetHover(true);
        _capsule.PointerExited += (_, _) => SetHover(false);
        _capsule.PointerCanceled += (_, _) => SetHover(false);

        Root.Children.Add(_capsule);
        AddShape(_capsule, CapsuleRadius);

        // The widgets beside the info line move when its text makes it wider or narrower.
        _capsule.SizeChanged += (_, e) =>
        {
            if (Math.Abs(e.NewSize.Width - e.PreviousSize.Width) >= 0.5)
            {
                NoteShapeWidthChanged();
            }
        };

        // Shared with the Weather widgets (one request to Open-Meteo for all).
        _weather = host.Weather;
        _weather.Changed += OnWeatherChanged;

        // Once a second, so a new minute shows on time; text is only touched when it changes.
        _clock = DispatcherQueue.CreateTimer();
        _clock.Interval = TimeSpan.FromSeconds(1);
        _clock.Tick += (_, _) => UpdateText();
    }

    private protected override bool HasContent
    {
        get
        {
            ShellSettings settings = Host.Settings.Current;
            return settings.InfoLineShowDate
                || settings.InfoLineShowTime
                || (settings.InfoLineShowWeather && _weather.Current is { IsFresh: true });
        }
    }

    private protected override CustomPiece Piece => CustomPiece.InfoLine;

    private protected override bool IsEnabled(ShellSettings settings) => settings.InfoLineEnabled;

    /// <summary>The capsule's width (it follows its text), for the widgets beside it.</summary>
    private protected override double ShapeWidthDip() => _capsule.ActualWidth > 0 ? _capsule.ActualWidth : _capsule.DesiredSize.Width;

    private protected override ScreenCorner CornerOf(ShellSettings settings) => settings.InfoLineCorner;

    private protected override double SizeOf(ShellSettings settings) => settings.InfoLineSize / 100.0;

    private protected override void OnSettingsApplied(ShellSettings settings)
    {
        string fillLook = settings.EnableAcrylic + "|" + settings.Opacity.ToString(CultureInfo.InvariantCulture) + "|" + IsDark;
        if (fillLook != _fillLook)
        {
            _fillLook = fillLook;
            if (_fill is not null)
            {
                // Detached on the UI thread before it is dropped.
                SurfaceFill.Release(_fill);
                _capsule.Children.Remove(_fill);
            }

            _fill = SurfaceFill.Create(settings, IsDark, CapsuleRadius);
            _capsule.Children.Insert(0, _fill);
        }

        // Weather runs only while it can be shown, for Windows' location or the chosen place.
        _weather.UsePlace(settings.WeatherUseManualPlace, settings.WeatherLatitude, settings.WeatherLongitude);
        _weather.SetWanted(this, settings.InfoLineEnabled && settings.InfoLineShowWeather);
        UpdateText();
    }

    private protected override void OnShownChanged(bool shown)
    {
        if (shown)
        {
            UpdateText();
            _clock.Start();
        }
        else
        {
            _clock.Stop();
        }
    }

    private protected override void OnCornerChanged(bool left)
    {
        _capsule.HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
    }

    private protected override void OnClosing()
    {
        _clock.Stop();
        _weather.SetWanted(this, false);
        _weather.Changed -= OnWeatherChanged;
        SurfaceFill.Release(_fill);
    }

    private static TextBlock MakeText(bool semiBold, bool dim = false)
    {
        return new TextBlock
        {
            FontSize = 12,
            FontWeight = semiBold ? FontWeights.SemiBold : FontWeights.Normal,
            Opacity = dim ? 0.6 : 1,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private static void Set(TextBlock block, string text)
    {
        if (block.Text != text)
        {
            block.Text = text;
        }
    }

    private static void Show(UIElement element, bool show)
    {
        Visibility visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (element.Visibility != visibility)
        {
            element.Visibility = visibility;
        }
    }

    /// <summary>"Tue, Nov 17" in English; the order and names follow Windows' language.</summary>
    private static string ShortDate(DateTime now)
    {
        DateTimeFormatInfo format = CultureInfo.CurrentCulture.DateTimeFormat;
        string monthDay = format.MonthDayPattern.Replace("MMMM", "MMM", StringComparison.Ordinal);
        return now.ToString("ddd", CultureInfo.CurrentCulture) + ", " + now.ToString(monthDay, CultureInfo.CurrentCulture);
    }

    private void UpdateText()
    {
        ShellSettings settings = Host.Settings.Current;
        DateTime now = DateTime.Now;
        WeatherReading? reading = _weather.Current is { IsFresh: true } fresh ? fresh : null;

        bool date = settings.InfoLineShowDate;
        bool time = settings.InfoLineShowTime;
        bool weather = settings.InfoLineShowWeather && reading is not null;

        if (date)
        {
            Set(_date, ShortDate(now));
        }

        if (time)
        {
            // "T" is Windows' long time (with seconds), "t" its short time.
            Set(_time, now.ToString(settings.InfoLineShowSeconds ? "T" : "t", CultureInfo.CurrentCulture));
        }

        if (reading is not null)
        {
            Set(_weatherSymbol, reading.Symbol);
            Set(_temperature, Math.Round(reading.TemperatureC).ToString("0", CultureInfo.CurrentCulture) + "\u00B0");
        }

        Show(_date, date);
        Show(_dateDot, date && (time || weather));
        Show(_time, time);
        Show(_weatherDot, weather && (date || time));
        Show(_weatherSymbol, weather);
        Show(_temperature, weather);

        string tip = now.ToString("D", CultureInfo.CurrentCulture);
        if (reading is not null && settings.InfoLineShowWeather)
        {
            string place = settings.WeatherUseManualPlace && settings.WeatherPlaceName.Length > 0
                ? " " + Fmt.F(Strings.InfoLineIn, settings.WeatherPlaceName)
                : string.Empty;
            tip += "\n" + reading.Description + ", "
                + reading.TemperatureC.ToString("0.#", CultureInfo.CurrentCulture) + " \u00B0C" + place + " (Open-Meteo, "
                + reading.FetchedAt.ToString("t", CultureInfo.CurrentCulture) + ")";
        }

        if (ToolTipService.GetToolTip(_capsule) as string != tip)
        {
            ToolTipService.SetToolTip(_capsule, tip);
        }

        // Weather can appear or go stale; with only weather on, the line comes and goes with it.
        UpdateVisibility();
    }

    private void OnWeatherChanged()
    {
        UpdateText();
    }

    private void SetHover(bool on)
    {
        if (_hover.Background is SolidColorBrush brush)
        {
            brush.Color = !on
                ? Colors.Transparent
                : IsDark
                    ? ColorHelper.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)
                    : ColorHelper.FromArgb(0x12, 0x00, 0x00, 0x00);
        }
    }

    private void OnClicked()
    {
        // Machitan's own Win+N flyout, by the line (see CalendarFlyout.WidgetAnchor).
        if (!_clickedOnce)
        {
            _clickedOnce = true;
            Log.Info("Info line clicked: Machitan's Win+N flyout (the calendar) opens or closes by it.");
        }

        Host.ToggleCalendarFrom(ShapeScreenRect());
    }
}
