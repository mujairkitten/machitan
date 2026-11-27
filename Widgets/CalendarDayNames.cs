using System;
using System.Collections.Generic;
using Machitan.Core;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Globalization.DateTimeFormatting;

namespace Machitan.Widgets;

/// <summary>
/// Keeps a CalendarView's day names (Sun, Mon, ...) on one line: its own template gives the
/// labels margins and a width that make "Mon" and "Wed" break in two (Sam's screenshot of the
/// Clock and calendar widget, 2026-11-26: "Mo / n", "We / d"). Once the calendar has laid
/// itself out, the seven labels are found and set to one line without margins; if one still
/// does not fit its column, they get a point smaller, down to 10. The same fix as the Win+N
/// calendar's (CalendarFlyout). Then it stops listening.
/// </summary>
internal sealed class CalendarDayNames
{
    /// <summary>The day names' format: three letters.</summary>
    internal const string Format = "{dayofweek.abbreviated(3)}";

    private const double SmallestFontSize = 10;

    /// <summary>Layouts to wait for the labels before giving up.</summary>
    private const int MostSearches = 30;

    private readonly CalendarView _calendar;
    private readonly List<TextBlock> _labels = new();
    private int _searches;
    private bool _done;

    private CalendarDayNames(CalendarView calendar)
    {
        _calendar = calendar;
        _calendar.LayoutUpdated += OnLayoutUpdated;
    }

    /// <summary>Starts looking after <paramref name="calendar"/> (it keeps the helper while it needs it).</summary>
    internal static void Fit(CalendarView calendar)
    {
        _ = new CalendarDayNames(calendar);
    }

    private void Stop()
    {
        _done = true;
        _calendar.LayoutUpdated -= OnLayoutUpdated;
    }

    private void OnLayoutUpdated(object? sender, object e)
    {
        if (_done)
        {
            return;
        }

        try
        {
            if (_labels.Count == 0)
            {
                if (_calendar.ActualWidth <= 0)
                {
                    return;
                }

                FindLabels(_calendar, DayNames(_calendar.Language), _labels);
                if (_labels.Count == 0)
                {
                    if (++_searches >= MostSearches)
                    {
                        Stop();
                        Log.Warn("Calendar widget: the day-name labels were not found; the day names may break in two.");
                    }

                    return;
                }

                foreach (TextBlock label in _labels)
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

                // The fit is checked after the next layout.
                return;
            }

            bool tooNarrow = false;
            foreach (TextBlock label in _labels)
            {
                // The room is the label's column (its layout slot), not the centred label itself.
                double room = LayoutInformation.GetLayoutSlot(label).Width;
                if (room > 0 && NaturalWidth(label) > room + 0.5)
                {
                    tooNarrow = true;
                    break;
                }
            }

            double size = _labels[0].FontSize;
            if (!tooNarrow || size - 1 < SmallestFontSize)
            {
                Stop();
                return;
            }

            foreach (TextBlock label in _labels)
            {
                label.FontSize = size - 1;
            }
        }
        catch (Exception ex)
        {
            Stop();
            Log.Warn("Calendar widget: the day names could not be adjusted.", ex);
        }
    }

    /// <summary>The seven day names as the calendar shows them (same format, its own language).</summary>
    private static HashSet<string> DayNames(string language)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        DateTimeFormatter formatter = string.IsNullOrEmpty(language)
            ? new DateTimeFormatter(Format)
            : new DateTimeFormatter(Format, new[] { language });
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
    private static void FindLabels(DependencyObject parent, HashSet<string> names, List<TextBlock> found)
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
            string type = child.GetType().Name;
            if (type.StartsWith("CalendarView", StringComparison.Ordinal) && type.EndsWith("Item", StringComparison.Ordinal))
            {
                // Day, month and year cells: numbers and month names, not day names.
                continue;
            }

            if (child is TextBlock text && (names.Contains(text.Text.Trim()) || text.Name.StartsWith("WeekDay", StringComparison.Ordinal)))
            {
                found.Add(text);
                continue;
            }

            FindLabels(child, names, found);
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
}
