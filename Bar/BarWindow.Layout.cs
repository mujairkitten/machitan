using System;
using System.Collections.Generic;
using Machitan.Config;
using Machitan.Core;
using Machitan.Interop;
using Machitan.Visuals;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace Machitan.Bar;

/// <summary>
/// The bar's layout: segments per mode, the Start cluster and the Show Desktop strip.
///
/// Segmented: Start cluster and App dock are separate pills with a see-through gap.
/// Joined: one pill holding both, with a thin divider.
/// Classic: one full-width bar (16 DIP corners), Start cluster left, dock placed per the
///          alignment setting, Show Desktop at the far right.
/// Compact: not built yet (optional per the plan); shown as Segmented.
/// </summary>
public sealed partial class BarWindow
{
    private const double SegmentGapDip = 8;
    private const double SegmentPaddingDip = 6;
    private const double ItemSpacingDip = 2;
    private const double ClassicRadiusDip = 16;
    private const double SearchBoxWidthDip = 180;

    private const double DesktopLineWidthDip = 2;
    private const double DesktopLineHeightRatio = 0.38;
    private const double DesktopHitWidthDip = 12;

    private readonly List<(FrameworkElement Element, double RadiusDip)> _shapes = new();
    private readonly List<FrameworkElement> _fills = new();
    private readonly bool _hasTouch = User32.GetSystemMetrics(User32.SM_MAXIMUMTOUCHES) > 0;

    private StackPanel _dockPanel = new();
    private Button? _desktopButton;
    private Border? _desktopLine;
    private bool _barHovered;
    private bool _desktopHovered;
    private bool _compactNoted;

    /// <summary>Builds the whole bar from the current settings.</summary>
    private void BuildLayout()
    {
        try
        {
            ShellSettings settings = _host.Settings.Current;

            Root.RequestedTheme = settings.Theme switch
            {
                ThemeMode.Dark => ElementTheme.Dark,
                ThemeMode.Light => ElementTheme.Light,
                _ => ElementTheme.Default,
            };

            _host.Icons.DarkBar = IsDarkTheme();

            // Old acrylic fills are detached here, on the UI thread, before they are dropped.
            foreach (FrameworkElement oldFill in _fills)
            {
                SurfaceFill.Release(oldFill);
            }

            _fills.Clear();
            BarHost.Children.Clear();
            _shapes.Clear();
            _regionSignature = string.Empty;

            double pillRadius = PillHeightDip() / 2;

            FrameworkElement start = BuildStartCluster();
            _dockPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = ItemSpacingDip,
                VerticalAlignment = VerticalAlignment.Center,
            };
            FrameworkElement desktopStrip = BuildDesktopStrip();

            TaskbarMode mode = settings.TaskbarMode;
            if (mode == TaskbarMode.Compact)
            {
                if (!_compactNoted)
                {
                    Log.Info("Compact mode is not built yet; showing Segmented instead.");
                    _compactNoted = true;
                }

                mode = TaskbarMode.Segmented;
            }

            switch (mode)
            {
                case TaskbarMode.Joined:
                    BarHost.Children.Add(MakeSegment(
                        Row(ItemSpacingDip, start, MakeDivider(), _dockPanel, desktopStrip),
                        pillRadius,
                        stretch: false));
                    break;

                case TaskbarMode.Classic:
                    var grid = new Grid();
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    _dockPanel.HorizontalAlignment = settings.DockAlignment switch
                    {
                        DockAlignment.Left => HorizontalAlignment.Left,
                        DockAlignment.Right => HorizontalAlignment.Right,
                        _ => HorizontalAlignment.Center,
                    };

                    Grid.SetColumn(start, 0);
                    Grid.SetColumn(_dockPanel, 1);
                    Grid.SetColumn(desktopStrip, 2);
                    grid.Children.Add(start);
                    grid.Children.Add(_dockPanel);
                    grid.Children.Add(desktopStrip);

                    BarHost.Children.Add(MakeSegment(grid, ClassicRadiusDip, stretch: true));
                    break;

                default:
                    StackPanel segments = Row(SegmentGapDip);
                    segments.HorizontalAlignment = HorizontalAlignment.Left;
                    segments.Children.Add(MakeSegment(start, pillRadius, stretch: false));
                    segments.Children.Add(MakeSegment(Row(ItemSpacingDip, _dockPanel, desktopStrip), pillRadius, stretch: false));
                    BarHost.Children.Add(segments);
                    break;
            }

            SyncDock(rebuild: true);
            UpdateDesktopStrip();
        }
        catch (Exception ex)
        {
            Log.Error("The bar layout could not be built.", ex);
        }
    }

    private static StackPanel Row(double spacing, params UIElement[] children)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = spacing,
            VerticalAlignment = VerticalAlignment.Center,
        };

        foreach (UIElement child in children)
        {
            row.Children.Add(child);
        }

        return row;
    }

    /// <summary>A segment: its fill (acrylic or a translucent colour) with the content on top.</summary>
    private Grid MakeSegment(UIElement content, double radiusDip, bool stretch)
    {
        var segment = new Grid
        {
            Height = PillHeightDip(),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = stretch ? HorizontalAlignment.Stretch : HorizontalAlignment.Left,
        };

        segment.Children.Add(CreateSegmentFill(radiusDip));
        segment.Children.Add(new Border
        {
            Padding = new Thickness(SegmentPaddingDip, 0, SegmentPaddingDip, 0),
            Child = content,
        });

        _shapes.Add((segment, radiusDip));
        return segment;
    }

    private FrameworkElement CreateSegmentFill(double radiusDip)
    {
        FrameworkElement fill = SurfaceFill.Create(_host.Settings.Current, IsDarkTheme(), radiusDip);
        _fills.Add(fill);
        return fill;
    }

    private Border MakeDivider()
    {
        return new Border
        {
            Width = 1,
            Height = PillHeightDip() * 0.45,
            Margin = new Thickness(4, 0, 4, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Background = new SolidColorBrush(IsDarkTheme()
                ? ColorHelper.FromArgb(50, 255, 255, 255)
                : ColorHelper.FromArgb(50, 0, 0, 0)),
            IsHitTestVisible = false,
        };
    }

    private bool IsDarkTheme()
    {
        return Root.ActualTheme == ElementTheme.Dark
            || (Root.ActualTheme == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Dark);
    }

    // ---------------------------------------------------------------- Start cluster

    /// <summary>
    /// Start button plus search (icon, box or hidden). Start opens and closes Machitan's Start
    /// menu; search opens it with the search box focused. Only if the Start menu could not be
    /// created do they hand over to Windows' own (Ctrl+Esc, Win+S).
    /// </summary>
    private FrameworkElement BuildStartCluster()
    {
        ShellSettings settings = _host.Settings.Current;
        StackPanel row = Row(ItemSpacingDip);

        Button start = MakeBarButton(BuildStartGlyph(settings.IconSize, IsDarkTheme()), "Start");
        start.Click += (_, _) =>
        {
            if (_host.StartMenu is { } menu)
            {
                menu.Toggle();
            }
            else if (!KeyChord.SendCtrlEsc())
            {
                Log.Warn("Start: Windows did not accept the Ctrl+Esc key press.");
            }
        };
        row.Children.Add(start);

        switch (settings.SearchMode)
        {
            case SearchMode.Icon:
                var glyph = new FontIcon
                {
                    Glyph = "\uE721",
                    FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                    FontSize = settings.IconSize * 0.8,
                };
                Button search = MakeBarButton(glyph, "Search");
                search.Click += (_, _) => OpenSearch();
                row.Children.Add(search);
                break;

            case SearchMode.Box:
                row.Children.Add(BuildSearchBox());
                break;
        }

        return row;
    }

    /// <summary>
    /// Box mode: a plain TextBox, 180 DIP wide, acting as a launcher, not a field: a click opens
    /// Start with its search box focused, where the typing goes. The bar never takes keyboard
    /// focus (it must not steal it from the app in front), so the box itself stays empty.
    /// </summary>
    private TextBox BuildSearchBox()
    {
        var box = new TextBox
        {
            Width = SearchBoxWidthDip,
            Height = Math.Max(PillHeightDip() - 14, 28),
            PlaceholderText = "Search",
            IsReadOnly = true,
            IsTabStop = false,
            VerticalAlignment = VerticalAlignment.Center,
            CornerRadius = new CornerRadius(Math.Max(PillHeightDip() - 14, 28) / 2),
        };

        AutomationProperties.SetName(box, "Search");
        box.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => OpenSearch()), handledEventsToo: true);
        return box;
    }

    private void OpenSearch()
    {
        if (_host.StartMenu is { } menu)
        {
            menu.Open();
        }
        else if (!KeyChord.SendWin('S'))
        {
            Log.Warn("Search: Windows did not accept the Win+S key press.");
        }
    }

    /// <summary>A neutral four-tile grid, drawn here (no image files, no brand logo).</summary>
    private static FrameworkElement BuildStartGlyph(double iconSize, bool dark)
    {
        var fill = new SolidColorBrush(dark
            ? ColorHelper.FromArgb(240, 255, 255, 255)
            : ColorHelper.FromArgb(230, 0, 0, 0));

        double gap = Math.Max(Math.Round(iconSize / 12), 2);
        double tile = (iconSize - gap) / 2;

        var grid = new Grid
        {
            Width = iconSize,
            Height = iconSize,
            ColumnSpacing = gap,
            RowSpacing = gap,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(tile) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(tile) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(tile) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(tile) });

        for (int i = 0; i < 4; i++)
        {
            var square = new Border
            {
                CornerRadius = new CornerRadius(Math.Max(tile * 0.25, 1)),
                Background = fill,
            };
            Grid.SetColumn(square, i % 2);
            Grid.SetRow(square, i / 2);
            grid.Children.Add(square);
        }

        return grid;
    }

    /// <summary>A bare bar button: content at the exact centre, no tile at rest, a background only on hover or focus.</summary>
    private Button MakeBarButton(UIElement content, string name)
    {
        ShellSettings settings = _host.Settings.Current;
        double height = Math.Max(PillHeightDip() - 8, settings.IconSize + 14);

        var button = new Button
        {
            Content = content,
            Width = settings.IconSize + 20,
            Height = height,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(10),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
        return button;
    }

    // ---------------------------------------------------------------- Show Desktop strip

    /// <summary>
    /// A 2 DIP line, 38% of the bar height, rounded ends, at the far right end of the bar on the
    /// segment's normal padding. The button around it is wider than the line (easier to hit),
    /// has its own padding zeroed (otherwise it renders about 12 px wide), and shows no
    /// hover tile of its own.
    /// </summary>
    private FrameworkElement BuildDesktopStrip()
    {
        double lineHeight = _host.Settings.Current.BarSize * DesktopLineHeightRatio;

        _desktopLine = new Border
        {
            Width = DesktopLineWidthDip,
            Height = lineHeight,
            CornerRadius = new CornerRadius(DesktopLineWidthDip / 2),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };

        var transparent = new SolidColorBrush(Colors.Transparent);
        var button = new Button
        {
            Content = _desktopLine,
            Width = DesktopHitWidthDip,
            Height = Math.Max(PillHeightDip() - 8, lineHeight),
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            Background = transparent,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            UseSystemFocusVisuals = true,
        };

        // No hover or press tile: only the line itself reacts.
        button.Resources["ButtonBackgroundPointerOver"] = transparent;
        button.Resources["ButtonBackgroundPressed"] = transparent;
        button.Resources["ButtonBorderBrushPointerOver"] = transparent;
        button.Resources["ButtonBorderBrushPressed"] = transparent;

        AutomationProperties.SetName(button, "Show desktop");
        ToolTipService.SetToolTip(button, "Show desktop");

        button.PointerEntered += (_, _) =>
        {
            _desktopHovered = true;
            UpdateDesktopStrip();
        };
        button.PointerExited += (_, _) =>
        {
            _desktopHovered = false;
            UpdateDesktopStrip();
        };
        button.Click += (_, _) => _host.Desktop.Toggle(_host.Tasks.Groups);

        _desktopButton = button;
        return button;
    }

    /// <summary>
    /// Hover-only: invisible at rest; shown while the bar or the strip is hovered, while the
    /// desktop is shown, and always on touch devices. Translucent, brighter on hover, accent
    /// while the desktop is shown.
    /// </summary>
    private void UpdateDesktopStrip()
    {
        if (_desktopButton is null || _desktopLine is null)
        {
            return;
        }

        bool shown = _host.Desktop.IsDesktopShown;
        _desktopButton.Opacity = shown || _barHovered || _desktopHovered || _hasTouch ? 1 : 0;

        bool dark = IsDarkTheme();
        Windows.UI.Color color;
        if (shown)
        {
            color = ReadAccentColor();
        }
        else if (_desktopHovered)
        {
            color = dark ? ColorHelper.FromArgb(230, 255, 255, 255) : ColorHelper.FromArgb(200, 0, 0, 0);
        }
        else
        {
            color = dark ? ColorHelper.FromArgb(110, 255, 255, 255) : ColorHelper.FromArgb(100, 0, 0, 0);
        }

        _desktopLine.Background = new SolidColorBrush(color);
    }

    /// <summary>The Windows accent colour. (The wallpaper-based accent replaces this in Batch 7.)</summary>
    private Windows.UI.Color ReadAccentColor()
    {
        try
        {
            return _uiSettings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent);
        }
        catch (Exception)
        {
            return ColorHelper.FromArgb(255, 76, 194, 255);
        }
    }
}
