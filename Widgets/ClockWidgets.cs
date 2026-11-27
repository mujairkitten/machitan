using System;
using System.Globalization;
using Machitan.Shell;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Color = Windows.UI.Color;

namespace Machitan.Widgets;

/// <summary>
/// The Clock widget: the time and today's date (Sam's list: "time and date (clock only)"),
/// checked once a second while it is shown. Its choices (Customize; Sam: "centered text, time
/// only, analog, etc"): a digital or analog face; time and date, time only or date only; left,
/// centre or right; small, medium or large; seconds; Windows' hour format, 12-hour or 24-hour;
/// a long or short date. Formats and names follow Windows' language.
/// </summary>
internal class ClockWidget : WidgetView
{
    private const string FaceKey = "Face";
    private const string ShowKey = "Show";
    private const string SecondsKey = "Seconds";
    private const string HoursKey = "Hours";
    private const string DateKey = "DateStyle";

    /// <summary>The analog face's drawing size (DIP); it is scaled to the chosen size.</summary>
    private const double FaceDip = 100;

    private readonly DispatcherQueueTimer _tick;
    private readonly SolidColorBrush _ink = new(Colors.White);
    private StackPanel? _clock;
    private TextBlock? _time;
    private TextBlock? _date;
    private Viewbox? _analog;
    private RotateTransform? _hourHand;
    private RotateTransform? _minuteHand;
    private RotateTransform? _secondHand;
    private Line? _secondLine;
    private DateTime _day;

    internal ClockWidget(ShellHost host, WidgetItem item)
        : base(host, item)
    {
        _tick = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _tick.Interval = TimeSpan.FromSeconds(1);
        _tick.IsRepeating = true;
        _tick.Tick += (_, _) => UpdateClock();
    }

    internal override double DesktopHeightDip => 150;

    internal override bool HasOptions => true;

    internal override FrameworkElement Build()
    {
        StackPanel clock = MakeClock();
        clock.VerticalAlignment = VerticalAlignment.Center;
        return clock;
    }

    internal override void SetShown(bool shown)
    {
        if (shown)
        {
            UpdateClock();
            _tick.Start();
        }
        else
        {
            _tick.Stop();
        }
    }

    internal override FrameworkElement BuildOptions()
    {
        return OptionsPanel(
            Choice(Strings.WgOptFace, FaceKey, "Digital", ("Digital", Strings.WgOptDigital), ("Analog", Strings.WgOptAnalog)),
            Choice(Strings.WgOptShow, ShowKey, "Both", ("Both", Strings.WgOptTimeDate), ("Time", Strings.WgOptTimeOnly), ("Date", Strings.WgOptDateOnly)),
            AlignChoice(withRight: true),
            SizeChoice(Strings.WgOptSize),
            Switch(Strings.WgOptSeconds, SecondsKey, fallback: false),
            Choice(Strings.WgOptHours, HoursKey, "System", ("System", Strings.WgOptSystem), ("12", Strings.WgOpt12), ("24", Strings.WgOpt24)),
            Choice(Strings.WgOptDate, DateKey, "Long", ("Long", Strings.WgOptLong), ("Short", Strings.WgOptShort)));
    }

    /// <summary>The digital time's size for small, medium and large (DIP).</summary>
    private protected virtual double TimeSize => BySize(28, 40, 56);

    /// <summary>The analog face's size for small, medium and large (DIP).</summary>
    private protected virtual double FaceSize => BySize(80, 120, 160);

    /// <summary>The face (digital time or analog), with the date under it.</summary>
    private protected StackPanel MakeClock()
    {
        _time = new TextBlock
        {
            FontWeight = FontWeights.SemiLight,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _date = new TextBlock
        {
            Opacity = 0.8,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
        };
        _analog = MakeAnalog();
        _clock = new StackPanel { Spacing = 4 };
        _clock.Children.Add(_time);
        _clock.Children.Add(_analog);
        _clock.Children.Add(_date);

        // The face's lines follow the theme (white on dark, black on light).
        _clock.ActualThemeChanged += (_, _) => UpdateInk();
        _clock.Loaded += (_, _) => UpdateInk();
        ApplyOptions();
        return _clock;
    }

    private protected override void ApplyOptions()
    {
        if (_clock is null || _time is null || _date is null || _analog is null)
        {
            return;
        }

        string show = Option(ShowKey, "Both");
        bool analog = Option(FaceKey, "Digital") == "Analog";
        _time.Visibility = !analog && show != "Date" ? Visibility.Visible : Visibility.Collapsed;
        _analog.Visibility = analog && show != "Date" ? Visibility.Visible : Visibility.Collapsed;
        _date.Visibility = show != "Time" ? Visibility.Visible : Visibility.Collapsed;

        _time.FontSize = TimeSize;
        _date.FontSize = show == "Date" ? Math.Round(TimeSize * 0.6) : 14;
        _date.FontWeight = show == "Date" ? FontWeights.SemiLight : FontWeights.Normal;
        _date.Opacity = show == "Date" ? 1 : 0.8;
        _analog.Width = FaceSize;
        _analog.Height = FaceSize;

        _clock.HorizontalAlignment = Across;
        _time.TextAlignment = TextAcross;
        _date.TextAlignment = TextAcross;
        _analog.HorizontalAlignment = Across;
        OnOptionsApplied();
        UpdateClock();
    }

    /// <summary>A subclass lines up its own parts with the chosen alignment.</summary>
    private protected virtual void OnOptionsApplied()
    {
    }

    /// <summary>A new day: the calendar moves on to it.</summary>
    private protected virtual void OnNewDay(DateTime today)
    {
    }

    /// <summary>The analog face: a ring, twelve hour marks, the hands; the second hand and the centre in the accent colour.</summary>
    private Viewbox MakeAnalog()
    {
        var canvas = new Canvas { Width = FaceDip, Height = FaceDip };
        var ring = new Ellipse { Width = FaceDip - 4, Height = FaceDip - 4, Stroke = _ink, StrokeThickness = 2 };
        Canvas.SetLeft(ring, 2);
        Canvas.SetTop(ring, 2);
        canvas.Children.Add(ring);
        for (int i = 0; i < 12; i++)
        {
            bool quarter = i % 3 == 0;
            canvas.Children.Add(new Line
            {
                X1 = FaceDip / 2,
                Y1 = 8,
                X2 = FaceDip / 2,
                Y2 = quarter ? 17 : 13,
                Stroke = _ink,
                StrokeThickness = quarter ? 3 : 1.5,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                RenderTransform = new RotateTransform { CenterX = FaceDip / 2, CenterY = FaceDip / 2, Angle = i * 30 },
            });
        }

        var accent = new SolidColorBrush(AccentColor());
        _hourHand = AddHand(canvas, 28, 4, _ink, out _);
        _minuteHand = AddHand(canvas, 16, 3, _ink, out _);
        _secondHand = AddHand(canvas, 12, 1.5, accent, out Line secondLine);
        _secondLine = secondLine;
        var centre = new Ellipse { Width = 7, Height = 7, Fill = accent };
        Canvas.SetLeft(centre, (FaceDip / 2) - 3.5);
        Canvas.SetTop(centre, (FaceDip / 2) - 3.5);
        canvas.Children.Add(centre);

        var face = new Viewbox { Child = canvas, Stretch = Stretch.Uniform };
        AutomationProperties.SetName(face, Strings.WgOptAnalog);
        return face;
    }

    private static RotateTransform AddHand(Canvas canvas, double tip, double thickness, Brush brush, out Line line)
    {
        var turn = new RotateTransform { CenterX = FaceDip / 2, CenterY = FaceDip / 2 };
        line = new Line
        {
            X1 = FaceDip / 2,
            Y1 = (FaceDip / 2) + 6,
            X2 = FaceDip / 2,
            Y2 = tip,
            Stroke = brush,
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            RenderTransform = turn,
        };
        canvas.Children.Add(line);
        return turn;
    }

    private static Color AccentColor()
    {
        return Application.Current.Resources.TryGetValue("SystemAccentColor", out object? value) && value is Color color
            ? color
            : ColorHelper.FromArgb(0xFF, 0x00, 0x78, 0xD4);
    }

    private void UpdateInk()
    {
        if (_clock is not null)
        {
            _ink.Color = _clock.ActualTheme == ElementTheme.Light ? Colors.Black : Colors.White;
        }
    }

    /// <summary>"Tue, Nov 17" in English; the order and names follow Windows' language (as the info line).</summary>
    private static string ShortDate(DateTime now)
    {
        DateTimeFormatInfo format = CultureInfo.CurrentCulture.DateTimeFormat;
        string monthDay = format.MonthDayPattern.Replace("MMMM", "MMM", StringComparison.Ordinal);
        return now.ToString("ddd", CultureInfo.CurrentCulture) + ", " + now.ToString(monthDay, CultureInfo.CurrentCulture);
    }

    private void UpdateClock()
    {
        DateTime now = DateTime.Now;
        bool seconds = OptionOn(SecondsKey, fallback: false);

        // As Windows ("T" with seconds, "t" without), or always 12 or 24 hours.
        string pattern = Option(HoursKey, "System") switch
        {
            "12" => seconds ? "h:mm:ss tt" : "h:mm tt",
            "24" => seconds ? "HH:mm:ss" : "HH:mm",
            _ => seconds ? "T" : "t",
        };
        string time = now.ToString(pattern, CultureInfo.CurrentCulture).Trim();
        string date = Option(DateKey, "Long") == "Short" ? ShortDate(now) : now.ToString("D", CultureInfo.CurrentCulture);
        if (_time is not null && _time.Text != time)
        {
            _time.Text = time;
            AutomationProperties.SetName(_time, time);
        }

        if (_date is not null && _date.Text != date)
        {
            _date.Text = date;
        }

        if (_hourHand is not null && _minuteHand is not null && _secondHand is not null && _secondLine is not null)
        {
            _hourHand.Angle = ((now.Hour % 12) + (now.Minute / 60.0)) * 30;
            _minuteHand.Angle = (now.Minute + (now.Second / 60.0)) * 6;
            _secondHand.Angle = now.Second * 6;
            _secondLine.Visibility = seconds ? Visibility.Visible : Visibility.Collapsed;
        }

        if (now.Date != _day)
        {
            bool first = _day == default;
            _day = now.Date;
            if (!first)
            {
                OnNewDay(_day);
            }
        }
    }
}

/// <summary>
/// The Clock and calendar widget: the clock (with the same choices) above a month calendar
/// (Windows' own CalendarView, as in the Win+N calendar), today marked. The calendar keeps its
/// own size and gets smaller to fit a narrow or short card (never bigger), so it always shows
/// whole; it lines up with the clock's alignment.
/// </summary>
internal sealed class ClockCalendarWidget : ClockWidget
{
    /// <summary>The calendar's own width: three-letter day names fit (as the Win+N calendar).</summary>
    private const double CalendarWidthDip = 336;

    private CalendarView? _calendar;
    private Viewbox? _fit;

    internal ClockCalendarWidget(ShellHost host, WidgetItem item)
        : base(host, item)
    {
    }

    internal override double DesktopHeightDip => 380;

    private protected override double TimeSize => BySize(22, 28, 36);

    private protected override double FaceSize => BySize(64, 90, 120);

    internal override FrameworkElement Build()
    {
        _calendar = new CalendarView
        {
            Width = CalendarWidthDip,
            DayOfWeekFormat = CalendarDayNames.Format,
            SelectionMode = CalendarViewSelectionMode.None,
            IsTodayHighlighted = true,
            IsOutOfScopeEnabled = true,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        AutomationProperties.SetName(_calendar, Strings.CalCalendar);

        // Its day names stay on one line ("Mon", not "Mo / n").
        CalendarDayNames.Fit(_calendar);
        _fit = new Viewbox
        {
            Child = _calendar,
            Stretch = Stretch.Uniform,
            StretchDirection = StretchDirection.DownOnly,
            VerticalAlignment = VerticalAlignment.Top,
        };

        StackPanel clock = MakeClock();
        var panel = new Grid { RowSpacing = 8 };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panel.Children.Add(clock);
        Grid.SetRow(_fit, 1);
        panel.Children.Add(_fit);
        OnOptionsApplied();
        return panel;
    }

    private protected override void OnOptionsApplied()
    {
        if (_fit is not null)
        {
            _fit.HorizontalAlignment = Across;
        }
    }

    private protected override void OnNewDay(DateTime today)
    {
        _calendar?.SetDisplayDate(new DateTimeOffset(today));
    }
}
