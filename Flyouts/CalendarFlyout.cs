using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Machitan.Config;
using Machitan.Core;
using Machitan.Shell;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Globalization.DateTimeFormatting;

namespace Machitan.Flyouts;

/// <summary>
/// Win+N: the time, today's date and a month calendar (Windows' own CalendarView), lined up
/// with the bar like Start. The calendar folds away with the arrow button (remembered in
/// settings), and the clock can show seconds (Settings, off by default). There is no
/// notification list: Machitan keeps no notification history and shows none.
/// </summary>
public sealed partial class CalendarFlyout : FlyoutWindow
{
    private const double InnerPadDip = 16;
    /// <summary>Wide enough for three-letter day names (48 DIP a column).</summary>
    private const double CalendarWidthDip = 336;
    private const double HeaderHeightDip = 60;
    private const double SpacingDip = 12;

    private const string DayNameFormat = "{dayofweek.abbreviated(3)}";
    private const double SmallestDayNameFontSize = 10;

    private const int FoldGlyph = 0xE70D;
    private const int UnfoldGlyph = 0xE70E;

    private readonly TextBlock _time;
    private readonly TextBlock _date;
    private readonly Button _today;
    private readonly Button _fold;
    private readonly FontIcon _foldIcon;
    private readonly CalendarView _calendar;
    private readonly DispatcherQueueTimer _clock;
    private bool _collapsed;

    /// <summary>
    /// The calendar's own height for six weeks; a first guess until it has laid itself out
    /// once, then what it really needs (so no half row shows and none is cut off).
    /// </summary>
    private double _calendarHeightDip = 330;

    /// <summary>The calendar's own day-name labels (Sun, Mon, ...), once found.</summary>
    private readonly List<TextBlock> _dayNameLabels = new();
    private bool _dayNamesDone;
    private int _dayNameSearches;

    internal CalendarFlyout(ShellHost host)
        : base(host, "Calendar")
    {
        _time = new TextBlock
        {
            FontSize = 28,
            FontWeight = FontWeights.SemiLight,
        };
        _date = new TextBlock
        {
            FontSize = 14,
            Opacity = 0.8,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var clockText = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        clockText.Children.Add(_time);
        clockText.Children.Add(_date);

        _today = MakeTextButton("Today", ShowToday);

        _foldIcon = MakeGlyph(FoldGlyph, 12);
        _fold = new Button
        {
            Content = _foldIcon,
            Width = 36,
            Height = 30,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(SmallRadius),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _fold.Click += (_, _) => SetCollapsed(!_collapsed, save: true);

        var header = new Grid { Height = HeaderHeightDip, ColumnSpacing = 8 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(clockText);
        Grid.SetColumn(_today, 1);
        header.Children.Add(_today);
        Grid.SetColumn(_fold, 2);
        header.Children.Add(_fold);

        _calendar = new CalendarView
        {
            Width = CalendarWidthDip,
            DayOfWeekFormat = DayNameFormat,
            SelectionMode = CalendarViewSelectionMode.None,
            IsTodayHighlighted = true,
            IsOutOfScopeEnabled = true,
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        AutomationProperties.SetName(_calendar, "Calendar");
        _calendar.LayoutUpdated += OnCalendarLayoutUpdated;
        _calendar.SizeChanged += (_, e) => OnCalendarSized(e.NewSize.Height);

        var body = new StackPanel
        {
            Padding = new Thickness(InnerPadDip),
            Spacing = SpacingDip,
        };
        body.Children.Add(header);
        body.Children.Add(_calendar);

        Root.Children.Add(MakeBox(body, BoxRadius));

        _clock = DispatcherQueue.CreateTimer();
        _clock.Interval = TimeSpan.FromSeconds(1);
        _clock.IsRepeating = true;
        _clock.Tick += (_, _) => UpdateClock();

        SetCollapsed(Host.Settings.Current.CalendarCollapsed, save: false);
    }

    private static double ContentWidthDip => CalendarWidthDip + (2 * InnerPadDip);

    private double ContentHeightDip => _collapsed
        ? HeaderHeightDip + (2 * InnerPadDip)
        : HeaderHeightDip + SpacingDip + _calendarHeightDip + (2 * InnerPadDip);

    /// <summary>Win+N: open, or close when already open.</summary>
    internal void Toggle()
    {
        if (IsOpen)
        {
            HideFlyout();
            return;
        }

        UpdateClock();
        ShowToday();
        _clock.Start();
        Present(ContentWidthDip, ContentHeightDip, FlyoutPlacement.BesideBar);
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_collapsed)
            {
                _fold.Focus(FocusState.Programmatic);
            }
            else
            {
                _calendar.Focus(FocusState.Programmatic);
            }
        });
    }

    private protected override void OnHiding()
    {
        _clock.Stop();
    }

    /// <summary>Follows the seconds and fold settings, also when settings.json is edited by hand.</summary>
    private protected override void OnSettingsApplied(ShellSettings settings)
    {
        if (settings.CalendarCollapsed != _collapsed)
        {
            SetCollapsed(settings.CalendarCollapsed, save: false);
        }

        UpdateClock();
    }

    private void SetCollapsed(bool collapsed, bool save)
    {
        _collapsed = collapsed;
        _calendar.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        _today.Visibility = collapsed ? Visibility.Collapsed : Visibility.Visible;
        _foldIcon.Glyph = ((char)(collapsed ? UnfoldGlyph : FoldGlyph)).ToString();

        string name = collapsed ? "Show the calendar" : "Hide the calendar";
        AutomationProperties.SetName(_fold, name);
        ToolTipService.SetToolTip(_fold, name);

        if (IsOpen)
        {
            // Resize in place; the edge next to the bar stays where it is.
            Present(ContentWidthDip, ContentHeightDip, FlyoutPlacement.BesideBar);
        }

        if (save)
        {
            Host.Settings.Update(s => s.CalendarCollapsed = collapsed, "the calendar");
        }
    }

    /// <summary>The calendar laid itself out: size the flyout to it (once; the size is then kept).</summary>
    private void OnCalendarSized(double height)
    {
        if (height < 100 || Math.Abs(height - _calendarHeightDip) < 0.5)
        {
            return;
        }

        _calendarHeightDip = height;
        if (IsOpen && !_collapsed)
        {
            Present(ContentWidthDip, ContentHeightDip, FlyoutPlacement.BesideBar);
        }
    }

    /// <summary>
    /// The calendar's day names wrap or get cut ("M" over "o", "M...") because its built-in
    /// header labels leave too little room for wide names. After a layout: find the labels by
    /// their text (Windows formats them the same way here), keep each on one line with no
    /// margin, and if a name still does not fit, make the labels' font one step smaller until
    /// all fit. Logged once, so the outcome can be checked.
    /// </summary>
    private void OnCalendarLayoutUpdated(object? sender, object e)
    {
        if (_dayNamesDone)
        {
            return;
        }

        try
        {
            if (_dayNameLabels.Count == 0)
            {
                if (!IsOpen || _collapsed)
                {
                    return;
                }

                FindDayNameLabels(_calendar, DayNames(_calendar.Language), _dayNameLabels);
                if (_dayNameLabels.Count == 0)
                {
                    // Give the calendar a few layouts to build itself; then record its parts once.
                    _dayNameSearches++;
                    if (_dayNameSearches == 30)
                    {
                        _dayNamesDone = true;
                        var parts = new StringBuilder();
                        int lines = 0;
                        DescribeParts(_calendar, 0, parts, ref lines);
                        Log.Warn("Calendar: the day-name labels were not found (language '" + _calendar.Language + "'). The calendar's parts:" + Environment.NewLine + parts);
                    }

                    return;
                }

                TextBlock first = _dayNameLabels[0];
                string parent = VisualTreeHelper.GetParent(first) is FrameworkElement holder
                    ? holder.GetType().Name + (holder.Name.Length > 0 ? " '" + holder.Name + "'" : string.Empty) + " " + Math.Round(holder.ActualWidth) + " wide"
                    : "unknown";
                Log.Info("Calendar: " + _dayNameLabels.Count + " day-name labels found (first: '" + first.Text + "'"
                    + (first.Name.Length > 0 ? ", named " + first.Name : string.Empty)
                    + ", " + Math.Round(first.ActualWidth) + " wide, margin " + first.Margin.Left + "," + first.Margin.Top + "," + first.Margin.Right + "," + first.Margin.Bottom
                    + ", font " + first.FontSize + ", in " + parent + ").");

                foreach (TextBlock label in _dayNameLabels)
                {
                    label.TextWrapping = TextWrapping.NoWrap;
                    label.TextTrimming = TextTrimming.None;
                    label.Margin = new Thickness(0);
                    label.Padding = new Thickness(0);
                    label.MinWidth = 0;
                    label.MaxWidth = double.PositiveInfinity;
                    label.Width = double.NaN;
                    label.HorizontalAlignment = HorizontalAlignment.Center;
                }

                // Check the fit after the next layout.
                return;
            }

            bool tooNarrow = false;
            string widest = string.Empty;
            double widestNeeds = 0;
            double widestHas = 0;
            foreach (TextBlock label in _dayNameLabels)
            {
                // The room is the label's column (its layout slot), not the label itself: a
                // centred label is only as wide as its text.
                double needs = NaturalWidth(label);
                double room = LayoutInformation.GetLayoutSlot(label).Width - label.Margin.Left - label.Margin.Right;
                if (needs > widestNeeds)
                {
                    widest = label.Text;
                    widestNeeds = needs;
                    widestHas = room;
                }

                if (room > 0 && needs > room + 0.5)
                {
                    tooNarrow = true;
                }
            }

            double fontSize = _dayNameLabels[0].FontSize;
            if (!tooNarrow || fontSize - 1 < SmallestDayNameFontSize)
            {
                _dayNamesDone = true;
                Log.Info("Calendar: day names " + (tooNarrow ? "still do not fit at the smallest size" : "fit")
                    + ", font " + fontSize + " (widest '" + widest + "' needs " + Math.Round(widestNeeds, 1) + ", has " + Math.Round(widestHas, 1) + ").");
                return;
            }

            foreach (TextBlock label in _dayNameLabels)
            {
                label.FontSize = fontSize - 1;
            }
        }
        catch (Exception ex)
        {
            _dayNamesDone = true;
            Log.Warn("Calendar: the day names could not be adjusted.", ex);
        }
    }

    /// <summary>The seven day names as this calendar shows them (same format, the calendar's own language).</summary>
    private static HashSet<string> DayNames(string language)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        DateTimeFormatter formatter = string.IsNullOrEmpty(language)
            ? new DateTimeFormatter(DayNameFormat)
            : new DateTimeFormatter(DayNameFormat, new[] { language });
        var day = new DateTimeOffset(2026, 1, 4, 12, 0, 0, TimeSpan.Zero);
        for (int i = 0; i < 7; i++)
        {
            names.Add(formatter.Format(day.AddDays(i)));
        }

        return names;
    }

    /// <summary>
    /// The day-name row: a panel holding exactly seven text labels side by side; failing that,
    /// labels whose text is a day name, or that the template names WeekDay1..7.
    /// </summary>
    private static void FindDayNameLabels(DependencyObject parent, HashSet<string> names, List<TextBlock> found)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        var labels = new List<TextBlock>();
        for (int i = 0; i < count; i++)
        {
            if (VisualTreeHelper.GetChild(parent, i) is TextBlock label)
            {
                labels.Add(label);
            }
        }

        if (labels.Count == 7)
        {
            found.AddRange(labels);
            return;
        }

        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (IsCalendarItem(child))
            {
                // Day, month and year cells: numbers and month names, not day names.
                continue;
            }

            if (child is TextBlock text && (names.Contains(text.Text.Trim()) || text.Name.StartsWith("WeekDay", StringComparison.Ordinal)))
            {
                found.Add(text);
                continue;
            }

            FindDayNameLabels(child, names, found);
        }
    }

    private static bool IsCalendarItem(DependencyObject element)
    {
        string type = element.GetType().Name;
        return type.StartsWith("CalendarView", StringComparison.Ordinal) && type.EndsWith("Item", StringComparison.Ordinal);
    }

    /// <summary>One line per part (type, name, text), skipping the many day cells. For the log.</summary>
    private static void DescribeParts(DependencyObject parent, int depth, StringBuilder text, ref int lines)
    {
        int count = VisualTreeHelper.GetChildrenCount(parent);
        int cells = 0;
        for (int i = 0; i < count && lines < 120; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(parent, i);
            if (IsCalendarItem(child))
            {
                cells++;
                continue;
            }

            text.Append(' ', 2 * depth).Append(child.GetType().Name);
            if (child is FrameworkElement element)
            {
                if (element.Name.Length > 0)
                {
                    text.Append(" '").Append(element.Name).Append('\'');
                }

                text.Append(' ').Append(Math.Round(element.ActualWidth)).Append('x').Append(Math.Round(element.ActualHeight));
                if (element.Visibility != Visibility.Visible)
                {
                    text.Append(" hidden");
                }
            }

            if (child is TextBlock label)
            {
                text.Append(" text=\"").Append(label.Text).Append('"');
            }

            text.AppendLine();
            lines++;
            if (depth < 14)
            {
                DescribeParts(child, depth + 1, text, ref lines);
            }
        }

        if (cells > 0)
        {
            text.Append(' ', 2 * depth).Append('(').Append(cells).AppendLine(" calendar cells)");
            lines++;
        }
    }

    /// <summary>How wide the label's text is with unlimited room.</summary>
    private static double NaturalWidth(TextBlock label)
    {
        var probe = new TextBlock
        {
            Text = label.Text,
            FontSize = label.FontSize,
            FontFamily = label.FontFamily,
            FontWeight = label.FontWeight,
            FontStyle = label.FontStyle,
            FontStretch = label.FontStretch,
            CharacterSpacing = label.CharacterSpacing,
        };
        probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return probe.DesiredSize.Width;
    }

    private void ShowToday()
    {
        _calendar.SetDisplayDate(DateTimeOffset.Now);
    }

    /// <summary>The time and date in the user's own format; seconds when chosen in Settings.</summary>
    private void UpdateClock()
    {
        DateTime now = DateTime.Now;
        string timeFormat = Host.Settings.Current.CalendarShowSeconds ? "T" : "t";
        _time.Text = now.ToString(timeFormat, CultureInfo.CurrentCulture);
        _date.Text = now.ToString("D", CultureInfo.CurrentCulture);
    }
}
