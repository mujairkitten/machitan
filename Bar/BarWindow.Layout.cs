using System;
using System.Collections.Generic;
using Machitan.Config;
using Machitan.Core;
using Machitan.Custom;
using Machitan.Interop;
using Machitan.Shell;
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
/// The bar's layout: its bubbles per mode (the Start group, the apps, and app groups made with
/// "Group apps"), and the Show Desktop strip.
///
/// Segmented: every bubble is its own pill, with a see-through gap between.
/// Joined: one pill holding them all, with thin dividers.
/// Classic: one full-width bar (16 DIP corners): Start at the left end (or the right end when
///          it is not first), the app bubbles side by side placed per the alignment setting,
///          Show Desktop at the far right.
/// The bubbles' order and which apps sit in which bubble are in bar-layout.json
/// (BarLayoutStore); where search sits (with Start, or before or after the apps) is a
/// setting. Dragging on the bar sets them (BarWindow.Drag.cs).
/// Show Desktop is always at the bar's far right end. In that pill, an empty space as wide as
/// the strip sits at the other end, so the pill stays symmetrical. A bubble with no apps is not
/// shown (the apps bubble stays while search sits in it); with no app on the bar at all,
/// Show Desktop goes too.
/// Compact: not built yet (optional per the plan); shown as Segmented.
///
/// Split (Settings > Taskbar, off by default): each app bubble's pinned apps on top and its open
/// apps below; a pinned app that is open shows in both. Two rows in one bar: every segment is
/// two rows tall, the bubbles hold both rows, Start, search, the tray and Show Desktop sit in the
/// middle of the height. Two stacked bars: the bar as it is with only the open apps, and above
/// it a second bar with the pinned apps (a pill per bubble in Segmented, one pill in Joined, full
/// width in Classic), lined up the same way.
/// </summary>
public sealed partial class BarWindow
{
    private const double SegmentGapDip = 8;

    /// <summary>Two rows in one bar (Settings > Taskbar): the space between the pinned row and the open row.</summary>
    private const double SplitRowGapDip = 2;
    private const double SegmentPaddingDip = 6;
    private const double ItemSpacingDip = 2;
    private const double ClassicRadiusDip = Radii.Large;
    private const double SearchBoxWidthDip = 180;

    private const double DesktopLineWidthDip = 2;
    private const double DesktopLineHeightRatio = 0.38;
    private const double DesktopHitWidthDip = 12;

    /// <summary>The faint Show Desktop line shows while the pointer is this close to it on the bar.</summary>
    private const double DesktopNearDip = 96;

    /// <summary>The tray box's ^ turns this long as the box opens and closes.</summary>
    private const int ChevronTurnMs = 200;

    /// <summary>Peek at the desktop (Settings): the pointer rests this long on Show Desktop first.</summary>
    private const int DesktopPeekDelayMs = 500;

    private readonly List<(FrameworkElement Element, double RadiusDip)> _shapes = new();
    private readonly List<FrameworkElement> _fills = new();
    private readonly bool _hasTouch = User32.GetSystemMetrics(User32.SM_MAXIMUMTOUCHES) > 0;

    /// <summary>The app bubbles as built, from the left.</summary>
    private readonly List<AppBubble> _bubbles = new();

    /// <summary>The bubbles shown, from the left (Start included), as built. Two stacked bars: those of the lower bar.</summary>
    private List<string> _builtIds = new();

    /// <summary>Two stacked bars: the bubbles of the upper bar (those with a pinned app), from the left; else empty.</summary>
    private List<string> _builtTopIds = new();

    /// <summary>Every bubble shown anywhere on the bar, in the bar's order (the two lists above together).</summary>
    private List<string> _shownIds = new();

    private FrameworkElement? _startGroup;
    private Grid? _startSegment;

    /// <summary>The tray bubble's row of icons (Settings: tray on the bar), and its own pill (Segmented).</summary>
    private StackPanel? _trayRow;
    private Grid? _traySegment;
    private readonly Dictionary<string, Button> _trayButtons = new(StringComparer.Ordinal);

    /// <summary>The tray bubble's ^ button, which opens the tray box (null while the box is empty).</summary>
    private Button? _trayChevron;
    private string? _trayLastKey;
    private long _trayLastAt;
    private Button? _startButton;
    private bool _searchInApps;
    private FrameworkElement? _desktopSpacer;
    private Button? _desktopButton;
    private Border? _desktopLine;
    private bool _barHovered;
    private bool _desktopNear;
    private bool _desktopHovered;
    private bool _trayBoxOpen;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _desktopPeekTimer;
    private bool _desktopPeeking;

    // The Start button's own pictures, when they are on (null: Machitan's logo).
    private StartButtonPictures? _startPictures;
    private bool _compactNoted;

    /// <summary>One app bubble on the bar: the apps (with search when it sits there) or a group.</summary>
    private sealed class AppBubble
    {
        internal AppBubble(string id, StackPanel panel, StackPanel row)
        {
            Id = id;
            Panel = panel;
            Row = row;
        }

        internal string Id { get; }

        /// <summary>The app buttons (split: the open apps only).</summary>
        internal StackPanel Panel { get; }

        /// <summary>The buttons' panel, with search before or after it (the apps bubble). Two rows in one bar: both rows.</summary>
        internal StackPanel Row { get; }

        /// <summary>Its own pill (Segmented).</summary>
        internal Grid? Pill { get; set; }

        /// <summary>Split (Settings > Taskbar): the pinned apps' buttons, above the open ones; else null.</summary>
        internal StackPanel? PinnedPanel { get; init; }

        /// <summary>Two stacked bars, Segmented: the pinned apps' own pill in the upper bar.</summary>
        internal Grid? PinnedPill { get; set; }

        /// <summary>Two stacked bars: the bubble is in the lower bar (it has an open app, or search sits in it).</summary>
        internal bool InMain { get; init; } = true;

        /// <summary>Two stacked bars: the bubble is in the upper bar (it has a pinned app).</summary>
        internal bool InTop { get; init; }
    }

    /// <summary>Builds the whole bar from the current settings.</summary>
    private void BuildLayout()
    {
        try
        {
            ShellSettings settings = _host.Settings.Current;

            // Everything is rebuilt below: a drag or a search in progress ends with it.
            CancelDrag();
            StopDesktopPeek();
            EndTyping(restoreFocus: false);
            _searchBox = null;

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

            SearchPlace place = settings.SearchMode == SearchMode.Hidden ? SearchPlace.WithStart : settings.SearchPlace;
            _searchInApps = place != SearchPlace.WithStart;

            FrameworkElement start = BuildStartCluster(withSearch: !_searchInApps);
            _startGroup = start;
            _startSegment = null;
            _desktopSpacer = null;

            // Which bubbles show, from the left, and their apps. Two stacked bars: the upper bar's
            // bubbles too; an app bubble may sit in either bar or both.
            Dictionary<string, List<DockItem>> arranged = ArrangeDock();
            TaskbarSplit split = settings.TaskbarSplit;
            _builtIds = ShownBubbles(arranged);
            _builtTopIds = TopBubbles(arranged);
            _shownIds = Union(_builtIds, _builtTopIds);
            _bubbles.Clear();
            _trayButtons.Clear();
            _trayChevron = null;
            _trayRow = null;
            _traySegment = null;
            foreach (string id in _shownIds)
            {
                if (id == BarLayoutStore.TrayId)
                {
                    _trayRow = Row(ItemSpacingDip);
                }
                else if (id != BarLayoutStore.StartId)
                {
                    _bubbles.Add(MakeBubble(id, place, split, _builtIds.Contains(id), _builtTopIds.Contains(id)));
                }
            }

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

            // The bar (two stacked bars: the lower one, with the open apps).
            FrameworkElement main;
            switch (mode)
            {
                case TaskbarMode.Joined:
                {
                    // One pill: the bubbles in order, a divider between each two.
                    _desktopSpacer = MakeDesktopSpacer();
                    StackPanel row = Row(ItemSpacingDip, _desktopSpacer);
                    bool first = true;
                    foreach (string id in _builtIds)
                    {
                        if (!first)
                        {
                            row.Children.Add(MakeDivider());
                        }

                        row.Children.Add(PartOf(id, start));
                        first = false;
                    }

                    row.Children.Add(desktopStrip);
                    main = MakeSegment(row, pillRadius, stretch: false);
                    break;
                }

                case TaskbarMode.Classic:
                {
                    // Full width: Start at the left end, or at the right end when it is not first;
                    // the app bubbles side by side, placed per the alignment.
                    bool startLeft = _builtIds.Count == 0 || _builtIds[0] == BarLayoutStore.StartId;
                    var grid = new Grid();
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = startLeft ? GridLength.Auto : new GridLength(1, GridUnitType.Star) });
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = startLeft ? new GridLength(1, GridUnitType.Star) : GridLength.Auto });
                    grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                    StackPanel apps = Row(ItemSpacingDip);
                    foreach (AppBubble bubble in _bubbles)
                    {
                        if (!bubble.InMain)
                        {
                            continue;
                        }

                        if (apps.Children.Count > 0)
                        {
                            apps.Children.Add(MakeDivider());
                        }

                        apps.Children.Add(bubble.Row);
                    }

                    apps.HorizontalAlignment = AlignmentOf(settings.DockAlignment);

                    // The tray sits at the right end, before Show Desktop.
                    StackPanel end = Row(ItemSpacingDip);
                    if (_trayRow is not null)
                    {
                        end.Children.Add(_trayRow);
                    }

                    end.Children.Add(desktopStrip);
                    Grid.SetColumn(start, startLeft ? 0 : 1);
                    Grid.SetColumn(apps, startLeft ? 1 : 0);
                    Grid.SetColumn(end, 2);
                    if (_builtIds.Contains(BarLayoutStore.StartId))
                    {
                        grid.Children.Add(start);
                    }

                    grid.Children.Add(apps);
                    grid.Children.Add(end);

                    main = MakeSegment(grid, ClassicRadiusDip, stretch: true);
                    break;
                }

                default:
                {
                    // A pill per bubble. Show Desktop goes in the right-hand pill, with its balancing space.
                    StackPanel segments = Row(SegmentGapDip);
                    segments.HorizontalAlignment = split == TaskbarSplit.Stacked ? AlignmentOf(settings.DockAlignment) : HorizontalAlignment.Left;
                    _desktopSpacer = MakeDesktopSpacer();
                    for (int i = 0; i < _builtIds.Count; i++)
                    {
                        string id = _builtIds[i];
                        bool last = i == _builtIds.Count - 1;
                        UIElement content = PartOf(id, start);
                        Grid pill = MakeSegment(
                            last ? Row(ItemSpacingDip, _desktopSpacer, content, desktopStrip) : content,
                            pillRadius,
                            stretch: false);
                        pill.Tag = id;
                        if (id == BarLayoutStore.StartId)
                        {
                            _startSegment = pill;
                        }
                        else if (id == BarLayoutStore.TrayId)
                        {
                            // The tray bubble drags by its empty space too.
                            _traySegment = pill;
                            AttachDrag(pill, DragKind.Bubble);
                        }
                        else
                        {
                            // An app bubble drags by its empty space.
                            BubbleById(id)!.Pill = pill;
                            AttachDrag(pill, DragKind.Bubble);
                        }

                        segments.Children.Add(pill);
                    }

                    main = segments;
                    break;
                }
            }

            if (split == TaskbarSplit.Stacked)
            {
                // The pinned apps' bar above the bar, lined up the same way. Its height stays
                // while it is empty, so the lower bar keeps its place at the screen's edge.
                FrameworkElement pinnedLine = BuildPinnedLine(mode, pillRadius);
                pinnedLine.HorizontalAlignment = mode == TaskbarMode.Classic ? HorizontalAlignment.Stretch : AlignmentOf(settings.DockAlignment);
                var top = new Grid { Height = PillHeightDip() };
                top.Children.Add(pinnedLine);
                main.HorizontalAlignment = mode == TaskbarMode.Classic ? HorizontalAlignment.Stretch : AlignmentOf(settings.DockAlignment);
                var lines = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    Spacing = SegmentGapDip,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                lines.Children.Add(top);
                lines.Children.Add(main);
                BarHost.Children.Add(lines);
            }
            else
            {
                BarHost.Children.Add(main);
            }

            FillBubbles(arranged, rebuild: true);
            FillTrayBubble();
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

    private static HorizontalAlignment AlignmentOf(DockAlignment alignment)
    {
        return alignment switch
        {
            DockAlignment.Left => HorizontalAlignment.Left,
            DockAlignment.Right => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Center,
        };
    }

    /// <summary>The bubbles of both bars (two stacked bars) in the bar's order; else a copy of <paramref name="main"/>.</summary>
    private List<string> Union(List<string> main, List<string> top)
    {
        if (top.Count == 0)
        {
            return new List<string>(main);
        }

        var all = new List<string>(main.Count + top.Count);
        foreach (string id in _host.BarLayout.Order)
        {
            if (main.Contains(id) || top.Contains(id))
            {
                all.Add(id);
            }
        }

        foreach (string id in main)
        {
            if (!all.Contains(id))
            {
                all.Add(id);
            }
        }

        return all;
    }

    /// <summary>A segment: its fill (acrylic or a translucent colour) with the content on top.</summary>
    private Grid MakeSegment(UIElement content, double radiusDip, bool stretch)
    {
        // A see-through background, so a press anywhere on the pill (its empty space too)
        // reaches the pill itself: the apps pill drags by it. Two rows in one bar: two rows tall.
        var segment = new Grid
        {
            Height = SegmentHeightDip(),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = stretch ? HorizontalAlignment.Stretch : HorizontalAlignment.Left,
            Background = new SolidColorBrush(Colors.Transparent),
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
            Height = SegmentHeightDip() * 0.45,
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

    // ---------------------------------------------------------------- Start group

    /// <summary>
    /// The Start button, plus search when it sits here. Start opens and closes Machitan's Start
    /// menu. Only if the Start menu could not be created does it hand over to Windows' own
    /// (Ctrl+Esc). Dragging Start moves the whole group.
    /// </summary>
    private FrameworkElement BuildStartCluster(bool withSearch)
    {
        ShellSettings settings = _host.Settings.Current;
        StackPanel row = Row(ItemSpacingDip);

        // Machitan's logo, 3:2 (36 x 24 at the default icon size); the button widens to fit it.
        // Settings > Start > Start button: the user's own pictures in the same place instead.
        double logoHeight = settings.IconSize;
        double logoWidth = Math.Round(logoHeight * 1.5);
        StartButtonPictures? pictures = settings.CustomStartButton
            ? StartButtonPictures.Load(settings, logoWidth, logoHeight)
            : null;
        UIElement face = pictures is not null
            ? pictures.Content
            : new Machitan.Controls.MachitanLogo
            {
                Width = logoWidth,
                Height = logoHeight,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        Button start = MakeBarButton(face, Strings.BarStart);
        if (start.Width < logoWidth + 12)
        {
            start.Width = logoWidth + 12;
            start.CornerRadius = new CornerRadius(ButtonRadius(start.Height, start.Width));
        }

        pictures?.Attach(start);
        _startPictures = pictures;

        // Pressed while Start is open (also when the bar is rebuilt with Start open).
        pictures?.SetHeld(_host.StartMenu?.IsOpen == true);
        start.Click += (_, _) =>
        {
            if (_host.Custom.IsOn)
            {
                _host.Custom.Select(new CustomTarget(CustomPiece.Start));
                return;
            }

            ClearSelection();
            if (_host.StartMenu is { } menu)
            {
                menu.Toggle();
            }
            else if (!KeyChord.SendCtrlEsc())
            {
                Log.Warn("Start: Windows did not accept the Ctrl+Esc key press.");
            }
        };
        AttachDrag(start, DragKind.StartGroup);
        _startButton = start;
        row.Children.Add(start);

        if (withSearch && BuildSearch() is FrameworkElement search)
        {
            row.Children.Add(search);
        }

        return row;
    }

    /// <summary>
    /// Search, as the settings say: an icon (Windows' search; it can be dragged to another
    /// place), a box (typing on the bar), or nothing.
    /// </summary>
    private FrameworkElement? BuildSearch()
    {
        ShellSettings settings = _host.Settings.Current;
        switch (settings.SearchMode)
        {
            case SearchMode.Icon:
                var glyph = new FontIcon
                {
                    Glyph = "\uE721",
                    FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                    FontSize = settings.IconSize * 0.8,
                };
                Button search = MakeBarButton(glyph, Strings.BarSearch);
                search.Click += (_, _) =>
                {
                    if (_host.Custom.IsOn)
                    {
                        _host.Custom.Select(TargetOf(_searchInApps ? BarLayoutStore.AppsId : BarLayoutStore.StartId));
                        return;
                    }

                    // Like Windows' own taskbar: an open Start closes first, then search opens.
                    _host.StartMenu?.HideMenu();
                    OpenWindowsSearch();
                };
                AttachDrag(search, DragKind.Search);
                return search;

            case SearchMode.Box:
                return BuildSearchBox();

            default:
                return null;
        }
    }

    /// <summary>The search icon: Windows' own search flyout, as Win+S opens it.</summary>
    private static void OpenWindowsSearch()
    {
        if (!KeyChord.SendWin('S'))
        {
            Log.Warn("Search: Windows did not accept the Win+S key press.");
        }
    }

    /// <summary>
    /// Start with its search box focused (Win+S when Start is off): the fallback when Windows
    /// does not let the bar's own search box take the typing.
    /// </summary>
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

    /// <summary>
    /// A bar button's corner radius: concentric with the pill it sits in (the pill's radius minus
    /// the gap between the pill's edge and the button), so the hover box follows the bar's curve
    /// at any bar size: nearly round in a capsule pill, gently rounded in Classic.
    /// </summary>
    private double ButtonRadius(double buttonHeight, double buttonWidth)
    {
        double pill = PillHeightDip();
        double pillRadius = _host.Settings.Current.TaskbarMode == TaskbarMode.Classic ? ClassicRadiusDip : pill / 2;
        double gap = Math.Max((pill - buttonHeight) / 2, 0);
        double most = Math.Min(buttonHeight, buttonWidth) / 2;
        return Math.Max(Math.Min(pillRadius - gap, most), 4);
    }

    /// <summary>A bare bar button: content at the exact centre, no tile at rest, a background only on hover or focus.</summary>
    /// <summary>
    /// A bar button. Pressed, <paramref name="pressTarget"/> (its content when not given)
    /// shrinks a little and springs back when let go, unless animations are off.
    /// </summary>
    private Button MakeBarButton(UIElement content, string name, Func<UIElement?>? pressTarget = null)
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
            CornerRadius = new CornerRadius(ButtonRadius(height, settings.IconSize + 20)),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
        AttachPress(button, pressTarget ?? (() => button.Content as UIElement));
        return button;
    }

    /// <summary>
    /// The press squish. The button handles its pointer events itself, so this listens to all
    /// of them; a press that ends any way (let go, dragged off, taken by a drag) springs back.
    /// </summary>
    private void AttachPress(Button button, Func<UIElement?> target)
    {
        UIElement? pressed = null;

        void Release()
        {
            if (pressed is not null)
            {
                Motion.Press(pressed, down: false);
                pressed = null;
            }
        }

        button.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            if (!Motion.IsOn(_host.Settings.Current)
                || !e.GetCurrentPoint(button).Properties.IsLeftButtonPressed)
            {
                return;
            }

            Release();
            pressed = target();
            if (pressed is not null)
            {
                Motion.Press(pressed, down: true);
            }
        }), true);

        PointerEventHandler letGo = (_, _) => Release();
        button.AddHandler(UIElement.PointerReleasedEvent, letGo, true);
        button.AddHandler(UIElement.PointerCaptureLostEvent, letGo, true);
        button.AddHandler(UIElement.PointerCanceledEvent, letGo, true);
        button.AddHandler(UIElement.PointerExitedEvent, letGo, true);
    }

    // ---------------------------------------------------------------- Show Desktop strip

    /// <summary>The empty space at a pill's left end that balances the Show Desktop strip at its right end.</summary>
    private static Border MakeDesktopSpacer()
    {
        return new Border
        {
            Width = DesktopHitWidthDip,
            IsHitTestVisible = false,
        };
    }

    // ---------------------------------------------------------------- app bubbles

    /// <summary>The dock's apps (pins and running apps), sorted into the bubbles.</summary>
    private Dictionary<string, List<DockItem>> ArrangeDock()
    {
        List<DockItem> items = DockModel.Build(_host.Pins.Pins, _host.Tasks.Groups);
        return _host.BarLayout.Arrange(items);
    }

    /// <summary>
    /// The bubbles to show, from the left: Start always, the apps while they have an app (or
    /// search sits there), a group while it has an app. Two stacked bars: the lower bar's, where
    /// only open apps count.
    /// </summary>
    private List<string> ShownBubbles(Dictionary<string, List<DockItem>> arranged)
    {
        ShellSettings settings = _host.Settings.Current;
        bool openOnly = settings.TaskbarSplit == TaskbarSplit.Stacked;
        var shown = new List<string>();
        foreach (string id in _host.BarLayout.Order)
        {
            bool hasApps = arranged.TryGetValue(id, out List<DockItem>? items)
                && (openOnly ? items.Exists(item => item.IsRunning) : items.Count > 0);
            bool show = id == BarLayoutStore.StartId ? settings.ShowStartOnBar
                : id == BarLayoutStore.AppsId ? settings.ShowAppsOnBar && (hasApps || _searchInApps)
                : id == BarLayoutStore.TrayId ? settings.TrayOnBar && _host.Tray.Icons.Count > 0
                : hasApps;
            if (show)
            {
                shown.Add(id);
            }
        }

        // Custom mode never leaves the bar empty; should the settings say so anyway, Start stays.
        if (shown.Count == 0)
        {
            shown.Add(BarLayoutStore.StartId);
        }

        return shown;
    }

    /// <summary>Two stacked bars: the app bubbles with a pinned app, from the left (the upper bar). Else empty.</summary>
    private List<string> TopBubbles(Dictionary<string, List<DockItem>> arranged)
    {
        ShellSettings settings = _host.Settings.Current;
        var top = new List<string>();
        if (settings.TaskbarSplit != TaskbarSplit.Stacked)
        {
            return top;
        }

        foreach (string id in _host.BarLayout.Order)
        {
            if (id == BarLayoutStore.StartId || id == BarLayoutStore.TrayId
                || (id == BarLayoutStore.AppsId && !settings.ShowAppsOnBar))
            {
                continue;
            }

            if (arranged.TryGetValue(id, out List<DockItem>? items) && items.Exists(item => item.IsPinned))
            {
                top.Add(id);
            }
        }

        return top;
    }

    /// <summary>
    /// An app bubble. Split: a second panel for the pinned apps; two rows in one bar puts it
    /// above the open apps' panel in the bubble's row (each row keeps its height while empty, so
    /// the icons never jump), two stacked bars puts it in the upper bar.
    /// </summary>
    private AppBubble MakeBubble(string id, SearchPlace place, TaskbarSplit split, bool inMain, bool inTop)
    {
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = ItemSpacingDip,
            VerticalAlignment = VerticalAlignment.Center,
        };

        StackPanel? pinned = null;
        UIElement apps = panel;
        if (split != TaskbarSplit.Off)
        {
            pinned = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = ItemSpacingDip,
                VerticalAlignment = VerticalAlignment.Center,
            };

            if (split == TaskbarSplit.Rows)
            {
                double rowHeight = Math.Max(PillHeightDip() - 8, _host.Settings.Current.IconSize + 14);
                panel.Height = rowHeight;
                pinned.Height = rowHeight;
                panel.HorizontalAlignment = HorizontalAlignment.Left;
                pinned.HorizontalAlignment = HorizontalAlignment.Left;
                var rows = new StackPanel
                {
                    Orientation = Orientation.Vertical,
                    Spacing = SplitRowGapDip,
                    VerticalAlignment = VerticalAlignment.Center,
                };
                rows.Children.Add(pinned);
                rows.Children.Add(panel);
                apps = rows;
            }
        }

        StackPanel row = Row(ItemSpacingDip);
        bool appsBubble = id == BarLayoutStore.AppsId;
        if (appsBubble && place == SearchPlace.BeforeApps && BuildSearch() is FrameworkElement before)
        {
            row.Children.Add(before);
        }

        row.Children.Add(apps);
        if (appsBubble && place == SearchPlace.AfterApps && BuildSearch() is FrameworkElement after)
        {
            row.Children.Add(after);
        }

        return new AppBubble(id, panel, row)
        {
            PinnedPanel = pinned,
            InMain = split != TaskbarSplit.Stacked || inMain,
            InTop = split == TaskbarSplit.Stacked && inTop,
        };
    }

    /// <summary>
    /// Two stacked bars: the upper bar, with each bubble's pinned apps: a pill per bubble
    /// (Segmented), one pill with dividers (Joined), or full width (Classic).
    /// </summary>
    private FrameworkElement BuildPinnedLine(TaskbarMode mode, double pillRadius)
    {
        var bubbles = new List<AppBubble>();
        foreach (AppBubble bubble in _bubbles)
        {
            if (bubble.InTop && bubble.PinnedPanel is not null)
            {
                bubbles.Add(bubble);
            }
        }

        if (mode == TaskbarMode.Segmented)
        {
            StackPanel pills = Row(SegmentGapDip);
            foreach (AppBubble bubble in bubbles)
            {
                Grid pill = MakeSegment(bubble.PinnedPanel!, pillRadius, stretch: false);
                pill.Tag = bubble.Id;
                bubble.PinnedPill = pill;
                pills.Children.Add(pill);
            }

            return pills;
        }

        if (bubbles.Count == 0)
        {
            // No pinned app: no empty pill either.
            return new Grid();
        }

        StackPanel row = Row(ItemSpacingDip);
        foreach (AppBubble bubble in bubbles)
        {
            if (row.Children.Count > 0)
            {
                row.Children.Add(MakeDivider());
            }

            row.Children.Add(bubble.PinnedPanel!);
        }

        if (mode == TaskbarMode.Classic)
        {
            row.HorizontalAlignment = AlignmentOf(_host.Settings.Current.DockAlignment);
            return MakeSegment(row, ClassicRadiusDip, stretch: true);
        }

        return MakeSegment(row, pillRadius, stretch: false);
    }

    /// <summary>How many bubbles the bar shows now (Start included; both bars when they are stacked).</summary>
    internal int ShownBubbleCount => _shownIds.Count;

    /// <summary>The bubbles the bar shows now, from the left (Start and the tray included; both bars when they are stacked).</summary>
    internal IReadOnlyList<string> ShownBubbleIds => _shownIds;

    /// <summary>
    /// Where a shown bubble is on screen (its pill; in Joined and Classic its part of the bar), or
    /// null. Two stacked bars: around both its parts when it is in both bars.
    /// </summary>
    internal RECT? BubbleScreenRect(string id)
    {
        List<RECT> parts = BubbleParts(id);
        if (parts.Count == 0)
        {
            return null;
        }

        RECT rect = parts[0];
        for (int i = 1; i < parts.Count; i++)
        {
            rect = Around(rect, parts[i]);
        }

        return rect;
    }

    /// <summary>A shown bubble's parts on screen: one, or (two stacked bars) one in each bar.</summary>
    private List<RECT> BubbleParts(string id)
    {
        bool segmented = EffectiveMode(_host.Settings.Current) == TaskbarMode.Segmented;
        var elements = new List<FrameworkElement?>(2);
        if (string.Equals(id, BarLayoutStore.StartId, StringComparison.OrdinalIgnoreCase))
        {
            elements.Add(_builtIds.Contains(BarLayoutStore.StartId) ? (segmented ? _startSegment : _startGroup) : null);
        }
        else if (string.Equals(id, BarLayoutStore.TrayId, StringComparison.OrdinalIgnoreCase))
        {
            elements.Add(segmented ? _traySegment : _trayRow);
        }
        else if (BubbleById(id) is AppBubble bubble)
        {
            elements.Add(!bubble.InMain ? null : segmented ? bubble.Pill : bubble.Row);
            elements.Add(!bubble.InTop ? null : segmented ? bubble.PinnedPill : bubble.PinnedPanel);
        }

        var parts = new List<RECT>(2);
        foreach (FrameworkElement? element in elements)
        {
            if (element is not null && IsOnBar(element))
            {
                RECT rect = ScreenRectOf(element);
                parts.Add(segmented ? rect : Grow(rect, (int)Math.Round(4 * Scale())));
            }
        }

        return parts;
    }

    private static RECT Around(RECT a, RECT b)
    {
        return new RECT
        {
            Left = Math.Min(a.Left, b.Left),
            Top = Math.Min(a.Top, b.Top),
            Right = Math.Max(a.Right, b.Right),
            Bottom = Math.Max(a.Bottom, b.Bottom),
        };
    }

    /// <summary>The bubble under the pointer, as a custom-mode pick, or null.</summary>
    private CustomTarget? BubbleUnderCursor()
    {
        if (!User32.GetCursorPos(out POINT cursor))
        {
            return null;
        }

        foreach (string id in _shownIds)
        {
            foreach (RECT rect in BubbleParts(id))
            {
                if (cursor.X >= rect.Left && cursor.X < rect.Right && cursor.Y >= rect.Top && cursor.Y < rect.Bottom)
                {
                    return TargetOf(id);
                }
            }
        }

        return null;
    }

    /// <summary>A bubble's ID as a custom-mode pick.</summary>
    private static CustomTarget TargetOf(string id)
    {
        if (string.Equals(id, BarLayoutStore.StartId, StringComparison.OrdinalIgnoreCase))
        {
            return new CustomTarget(CustomPiece.Start);
        }

        if (string.Equals(id, BarLayoutStore.TrayId, StringComparison.OrdinalIgnoreCase))
        {
            return new CustomTarget(CustomPiece.Tray);
        }

        return BarLayoutStore.IsGroup(id) ? new CustomTarget(CustomPiece.Group, id) : new CustomTarget(CustomPiece.Apps);
    }

    /// <summary>What a shown bubble puts on the bar: Start's group, the tray's row, or an app bubble's row.</summary>
    private UIElement PartOf(string id, FrameworkElement start)
    {
        if (id == BarLayoutStore.StartId)
        {
            return start;
        }

        if (id == BarLayoutStore.TrayId && _trayRow is not null)
        {
            return _trayRow;
        }

        return BubbleById(id)!.Row;
    }

    // ---------------------------------------------------------------- the tray bubble

    /// <summary>What a tray button shows, kept on it to update in place.</summary>
    private sealed class TrayTileTag
    {
        internal TrayTileTag(TrayIcon icon)
        {
            Icon = icon;
        }

        internal TrayIcon Icon { get; set; }

        internal int Stamp { get; set; } = -1;

        internal string Name { get; set; } = string.Empty;
    }

    /// <summary>
    /// The tray bubble: the ^ button for the tray box (while the box holds icons), then the icons
    /// taken out onto the bar, in the kept order (the same as Dynaland's); buttons reused per icon.
    /// </summary>
    private void FillTrayBubble()
    {
        if (_trayRow is null)
        {
            return;
        }

        try
        {
            var outside = new List<TrayIcon>();
            int boxed = 0;
            foreach (TrayIcon icon in _host.BarLayout.ArrangeTray(_host.Tray.Icons))
            {
                if (_host.BarLayout.IsTrayIconOut(icon))
                {
                    outside.Add(icon);
                }
                else
                {
                    boxed++;
                }
            }

            var alive = new HashSet<string>(StringComparer.Ordinal);
            foreach (TrayIcon icon in outside)
            {
                alive.Add(icon.Key);
            }

            foreach (string key in new List<string>(_trayButtons.Keys))
            {
                if (!alive.Contains(key))
                {
                    _trayButtons.Remove(key);
                }
            }

            var wanted = new List<UIElement>(outside.Count + 1);
            if (boxed > 0)
            {
                wanted.Add(TrayChevron());
            }

            foreach (TrayIcon icon in outside)
            {
                wanted.Add(TrayButtonFor(icon));
            }

            bool same = _trayRow.Children.Count == wanted.Count;
            for (int i = 0; same && i < wanted.Count; i++)
            {
                same = ReferenceEquals(_trayRow.Children[i], wanted[i]);
            }

            if (!same)
            {
                _trayRow.Children.Clear();
                foreach (UIElement element in wanted)
                {
                    _trayRow.Children.Add(element);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Bar: the tray bubble could not be filled.", ex);
        }
    }

    /// <summary>
    /// The ^ button ("Show hidden icons"): opens the tray box above it (v and below for a top
    /// bar). While the box is open it is turned upside down, as Windows' own is.
    /// </summary>
    private Button TrayChevron()
    {
        bool top = _host.Settings.Current.DockEdge == DockEdge.Top;
        int glyph = top ? 0xE70D : 0xE70E;
        if (_trayChevron is null)
        {
            Button chevron = MakeBarButton(new Border(), Strings.ShowHiddenIcons);
            chevron.Width = Math.Max(28, _host.Settings.Current.IconSize + 4);

            // Rounded for its own (narrower) width, so its hover background is not an oval.
            chevron.CornerRadius = new CornerRadius(ButtonRadius(chevron.Height, chevron.Width));
            chevron.Click += (_, _) =>
            {
                if (_host.Custom.IsOn)
                {
                    _host.Custom.Select(new CustomTarget(CustomPiece.Tray));
                    return;
                }

                _host.ToggleTrayBox(ScreenRectOf(chevron));
            };
            _trayChevron = chevron;
        }

        if (_trayChevron.Content is not FontIcon { Glyph: string shown } || shown != ((char)glyph).ToString())
        {
            var arrow = new FontIcon
            {
                Glyph = ((char)glyph).ToString(),
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 12,
            };
            _trayChevron.Content = arrow;
            Motion.Turn(arrow, _trayBoxOpen ? 180f : 0f, 0, animate: false);
        }

        return _trayChevron;
    }

    /// <summary>The tray box opened or closed: the ^ turns over (back again when it closes).</summary>
    private void OnTrayBoxOpenChanged(bool open)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => OnTrayBoxOpenChanged(open));
            return;
        }

        if (_closing || open == _trayBoxOpen)
        {
            return;
        }

        _trayBoxOpen = open;
        if (_trayChevron?.Content is FontIcon arrow)
        {
            Motion.Turn(arrow, open ? 180f : 0f, ChevronTurnMs, Motion.IsOn(_host.Settings.Current));
        }
    }

    private Button TrayButtonFor(TrayIcon icon)
    {
        if (!_trayButtons.TryGetValue(icon.Key, out Button? button))
        {
            button = MakeBarButton(new Border(), Strings.TrayIcon);
            button.Width = Math.Max(28, _host.Settings.Current.IconSize + 8);
            button.CornerRadius = new CornerRadius(ButtonRadius(button.Height, button.Width));
            button.Tag = new TrayTileTag(icon);
            string key = icon.Key;
            button.Click += (_, _) => OnTrayButtonClick(key);
            button.RightTapped += (_, e) =>
            {
                e.Handled = true;
                if (_host.Custom.IsOn)
                {
                    return;
                }

                if (_trayButtons.TryGetValue(key, out Button? b) && b.Tag is TrayTileTag t)
                {
                    _trayLastKey = null;
                    _host.Tray.Click(t.Icon, TrayClick.Right);
                }
            };
            AttachDrag(button, DragKind.TrayIcon);
            _trayButtons[key] = button;
        }

        var tag = (TrayTileTag)button.Tag;
        tag.Icon = icon;
        string name = icon.Name.Length > 0 ? icon.Name : Strings.TrayIcon;
        if (tag.Stamp != icon.PictureStamp || !string.Equals(tag.Name, name, StringComparison.Ordinal))
        {
            tag.Stamp = icon.PictureStamp;
            tag.Name = name;
            button.Content = TrayPicture(icon, name);
            ToolTipService.SetToolTip(button, name);
            AutomationProperties.SetName(button, name.Replace("\n", ", ", StringComparison.Ordinal));
        }

        return button;
    }

    /// <summary>The app's own tray picture at Windows' tray size (16), else its name's first letter.</summary>
    private static FrameworkElement TrayPicture(TrayIcon icon, string name)
    {
        if (icon.Picture is IconPixels pixels)
        {
            try
            {
                return new Image
                {
                    Width = 16,
                    Height = 16,
                    Stretch = Stretch.Uniform,
                    Source = Machitan.Start.StartMenuWindow.ToBitmap(pixels),
                };
            }
            catch (Exception ex)
            {
                Log.Warn("Bar: a tray icon's picture could not be shown.", ex);
            }
        }

        return new TextBlock
        {
            Text = name.Substring(0, 1).ToUpperInvariant(),
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    /// <summary>A click goes to the icon's app as Explorer sends it; a second one in Windows' double-click time is a double click.</summary>
    private void OnTrayButtonClick(string key)
    {
        if (_host.Custom.IsOn)
        {
            _host.Custom.Select(new CustomTarget(CustomPiece.Tray));
            return;
        }

        if (!_trayButtons.TryGetValue(key, out Button? button) || button.Tag is not TrayTileTag tag)
        {
            return;
        }

        long now = Environment.TickCount64;
        long doubleMs = Math.Clamp((long)User32.GetDoubleClickTime(), 200L, 900L);
        if (string.Equals(_trayLastKey, key, StringComparison.Ordinal) && now - _trayLastAt <= doubleMs)
        {
            _trayLastKey = null;
            _host.Tray.Click(tag.Icon, TrayClick.Double);
            return;
        }

        _trayLastKey = key;
        _trayLastAt = now;
        _host.Tray.Click(tag.Icon, TrayClick.Left);
    }

    private AppBubble? BubbleById(string id)
    {
        foreach (AppBubble bubble in _bubbles)
        {
            if (string.Equals(bubble.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return bubble;
            }
        }

        return null;
    }

    /// <summary>
    /// No app on the bar at all (none pinned, none running): Show Desktop goes (there is nothing
    /// to show the desktop from); back with the first app. The apps bubble's buttons panel is
    /// hidden while empty (search alone keeps the bubble). Two rows in one bar: both rows stay,
    /// empty or not, so neither moves when the other empties.
    /// </summary>
    private void UpdateDockPresence()
    {
        bool rows = _host.Settings.Current.TaskbarSplit == TaskbarSplit.Rows;
        bool any = false;
        foreach (AppBubble bubble in _bubbles)
        {
            bool has = bubble.Panel.Children.Count > 0;
            bool hasPinned = bubble.PinnedPanel is StackPanel pinned && pinned.Children.Count > 0;
            SetShown(bubble.Panel, has || rows);
            SetShown(bubble.PinnedPanel, hasPinned || rows);
            any |= has || hasPinned;
        }

        SetShown(_desktopSpacer, any);
        SetShown(_desktopButton, any);
    }

    private static void SetShown(FrameworkElement? element, bool shown)
    {
        Visibility wanted = shown ? Visibility.Visible : Visibility.Collapsed;
        if (element is not null && element.Visibility != wanted)
        {
            element.Visibility = wanted;
        }
    }

    /// <summary>
    /// A 2 DIP line, 38% of the bar height, rounded ends, at the far right end of the bar on the
    /// segment's normal padding. The button around it is wider than the line (easier to hit),
    /// has its own padding zeroed (otherwise it renders about 12 px wide), and shows no
    /// hover tile of its own.
    /// </summary>
    private FrameworkElement BuildDesktopStrip()
    {
        // As tall as the segment it sits in (two rows in one bar: both rows).
        double lineHeight = (SegmentHeightDip() + PillInsetDip) * DesktopLineHeightRatio;

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
            Height = Math.Max(SegmentHeightDip() - 8, lineHeight),
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

        AutomationProperties.SetName(button, Strings.ShowDesktop);
        ToolTipService.SetToolTip(button, Strings.ShowDesktop);

        button.PointerEntered += (_, _) =>
        {
            _desktopHovered = true;
            UpdateDesktopStrip();
            ArmDesktopPeek();
        };
        button.PointerExited += (_, _) =>
        {
            _desktopHovered = false;
            UpdateDesktopStrip();
            StopDesktopPeek();
        };
        button.Click += (_, _) =>
        {
            StopDesktopPeek();
            if (!_host.Custom.IsOn)
            {
                _host.Desktop.Toggle(_host.Tasks.Groups);
            }
        };

        _desktopButton = button;
        return button;
    }

    /// <summary>
    /// Hover-only: invisible at rest; shown while the pointer is on the bar close to the strip
    /// (DesktopNearDip) or on it, while the desktop is shown, and always on touch devices.
    /// Translucent, brighter on hover, accent while the desktop is shown.
    /// </summary>
    private void UpdateDesktopStrip()
    {
        if (_desktopButton is null || _desktopLine is null)
        {
            return;
        }

        bool shown = _host.Desktop.IsDesktopShown;
        _desktopButton.Opacity = shown || (_barHovered && _desktopNear) || _desktopHovered || _hasTouch ? 1 : 0;

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

    /// <summary>
    /// True when the pointer (moving on the bar) is within DesktopNearDip of the Show Desktop
    /// button, measured from the button's edges.
    /// </summary>
    private bool IsNearDesktopStrip(PointerRoutedEventArgs e)
    {
        if (_desktopButton is not { IsLoaded: true } button || button.Visibility != Visibility.Visible)
        {
            return false;
        }

        Windows.Foundation.Point at = e.GetCurrentPoint(button).Position;
        double width = button.ActualWidth;
        double height = button.ActualHeight;
        double dx = at.X < 0 ? -at.X : Math.Max(0, at.X - width);
        double dy = at.Y < 0 ? -at.Y : Math.Max(0, at.Y - height);
        return (dx * dx) + (dy * dy) <= DesktopNearDip * DesktopNearDip;
    }

    /// <summary>
    /// Peek at the desktop (Settings > Taskbar, off by default): the pointer resting on Show
    /// Desktop for half a second turns every window see-through (Windows' own peek, aimed at
    /// the desktop window), as Windows 7 and 10 did; moving away or clicking ends it. Nothing
    /// is minimised. Not while the desktop is shown already or in custom mode.
    /// </summary>
    private void ArmDesktopPeek()
    {
        if (!_host.Settings.Current.PeekAtDesktop || _host.Custom.IsOn || _host.Desktop.IsDesktopShown || _desktopPeeking)
        {
            return;
        }

        if (_desktopPeekTimer is null)
        {
            _desktopPeekTimer = DispatcherQueue.CreateTimer();
            _desktopPeekTimer.Interval = TimeSpan.FromMilliseconds(DesktopPeekDelayMs);
            _desktopPeekTimer.IsRepeating = false;
            _desktopPeekTimer.Tick += (_, _) => StartDesktopPeek();
        }

        _desktopPeekTimer.Stop();
        _desktopPeekTimer.Start();
    }

    private void StartDesktopPeek()
    {
        if (!_desktopHovered || _closing || _desktopPeeking || _host.Desktop.IsDesktopShown)
        {
            return;
        }

        // The desktop window ("Program Manager"): shown as it is, every app window see-through.
        nint desktopWindow = User32.FindWindow("Progman", null);
        if (desktopWindow == 0)
        {
            Log.Warn("Peek at the desktop: the desktop window was not found.");
            return;
        }

        _desktopPeeking = LivePeek.Start(desktopWindow, _hwnd, desktop: true, "the desktop");
    }

    /// <summary>Start opened or closed: the Start button's own pressed picture stays while it is open.</summary>
    private void OnStartMenuOpenChanged(bool open)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            _startPictures?.SetHeld(open);
        }
        else
        {
            DispatcherQueue.TryEnqueue(() => _startPictures?.SetHeld(_host.StartMenu?.IsOpen == true));
        }
    }

    /// <summary>Ends a desktop peek (or one about to start).</summary>
    private void StopDesktopPeek()
    {
        _desktopPeekTimer?.Stop();
        if (_desktopPeeking)
        {
            _desktopPeeking = false;
            LivePeek.End();
        }
    }

    /// <summary>Machitan's accent colour: the wallpaper's strongest colour, or Windows' accent (see AccentService).</summary>
    private Windows.UI.Color ReadAccentColor()
    {
        return _host.Accent.Current;
    }
}
