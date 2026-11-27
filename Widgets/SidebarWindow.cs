using System;
using System.Collections.Generic;
using Machitan.Config;
using Machitan.Core;
using Machitan.Flyouts;
using Machitan.Interop;
using Machitan.Shell;
using Machitan.Visuals;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace Machitan.Widgets;

/// <summary>
/// The widget sidebar (Sam, 2026-11-26: "Make sidebar so i can place widgets"; "Both, as a
/// setting"): a tall panel at the right (or left) edge of the bar's screen, in the bar's fill,
/// holding the widgets the user added (WidgetStore), each in a card with a menu to move it or
/// take it off. Off by default (Settings > Widgets).
///
/// Slides in (the default): hidden until the pointer rests at the screen's edge for a moment
/// (not near the top or bottom, so a maximised window's close button and the bar stay safe;
/// never while a mouse button is held), or "Show the sidebar" in Settings. Opened by pointing it
/// does not take the keyboard; it hides once the pointer has left it for a moment, unless it was
/// clicked: then a click elsewhere hides it, as a flyout. Escape hides it.
/// Docked: always shown, and the edge is kept free for it (SideStrip, an AppBar), so maximised
/// windows stop beside it; it steps aside while a full-screen app is in front. Its height (Sam,
/// 2026-11-26: "either it's full vertically side-to-side, or leave some room for
/// Indicator(s)"): full (the default), from the top of the screen's free height to its bottom,
/// over Machitan's own top (or bottom) strip too, with the indicators of its side's corners
/// beside it; or leaving room for them: they keep the screen's corners (above or below it), and
/// it starts below (or ends above) them.
/// Cards are dragged by their name row (Sam, 2026-11-26: "make the widgets draggable (just
/// swapping positions and in to/out from the sidebar)"): let go on another card and the two
/// change places; let go past the sidebar's inner edge and the widget goes on the desktop
/// there. A desktop widget dragged onto the shown sidebar goes in before the card under the
/// pointer (or at the end); held at the screen's edge, a sliding sidebar slides in for it.
/// Its width follows Settings, or a drag of its inner edge (Sam: "make the sidebar resizable");
/// a double-click there sets it back. Its menus stay inside its own window: Windows keeps a
/// menu window inside the screen's free area, and the docked sidebar is outside it (Sam's
/// screenshots: the menus opened beside the sidebar).
/// </summary>
public sealed partial class SidebarWindow : FlyoutWindow
{
    /// <summary>The width of the strip at its inner edge that resizes it (DIP).</summary>
    private const double GripDip = 6;

    /// <summary>From this width (DIP) the widgets sit two side by side (Sam, 2026-11-26: "at certain width, make the sidebar has more rows").</summary>
    private const double TwoColumnsFromDip = 560;
    private const double CardGapDip = 12;

    /// <summary>Space between the panel and the screen's edges (DIP).</summary>
    private const double EdgeGapDip = 8;

    /// <summary>No opening by pointing this close to the top or bottom of the free area (DIP).</summary>
    private const double KeepOutDip = 96;

    private const int PollMs = 120;
    private const long HoverDwellMs = 400;
    private const long LeaveHideMs = 600;

    private const int AddGlyph = 0xE710;
    private const int PinGlyph = 0xE718;
    private const int UnpinGlyph = 0xE77A;
    private const int SettingsGlyph = 0xE713;
    private const int MoreGlyph = 0xE712;
    private const int UpGlyph = 0xE74A;
    private const int DownGlyph = 0xE74B;
    private const int RemoveGlyph = 0xE74D;
    private const int DesktopGlyph = 0xE7F4;
    private const int CustomizeGlyph = 0xE771;

    /// <summary>A press on a card's name row becomes a drag once the pointer has moved this far (DIP).</summary>
    private const double DragStartDip = 6;

    /// <summary>A card let go this far past the sidebar's inner edge (DIP) goes on the desktop.</summary>
    private const double ToDesktopDip = 24;

    /// <summary>While a card is dragged near the list's top or bottom, the list scrolls this much (DIP) every <see cref="DragStepMs"/>.</summary>
    private const double ScrollZoneDip = 40;
    private const double ScrollStepDip = 24;
    private const int DragStepMs = 50;

    private readonly StackPanel _list;
    private readonly ScrollViewer _scroller;

    /// <summary>The cards, in their order: one column, or two when the sidebar is wide, each filling on its own (no gaps).</summary>
    private readonly MasonryPanel _cardGrid = new() { Spacing = CardGapDip };
    private int _columns;
    private readonly TextBlock _empty;
    private readonly TextBlock _readOnly;
    private readonly Button _add;
    private readonly Button _pin;
    private readonly SizeGrip _grip;
    private readonly List<WidgetView> _views = new();
    private readonly Dictionary<string, (WidgetView View, Border Card)> _cards = new(StringComparer.Ordinal);

    /// <summary>Each card's mark (an accent edge over it), shown while a dragged widget would land on it.</summary>
    private readonly Dictionary<string, Border> _cardMarks = new(StringComparer.Ordinal);
    private readonly SolidColorBrush _markStroke = new(Colors.Transparent);
    private readonly SolidColorBrush _markFill = new(Colors.Transparent);
    private readonly DispatcherQueueTimer _dragTimer;
    private readonly DispatcherQueueTimer _poll;
    private readonly DispatcherQueueTimer _heal;
    private readonly SolidColorBrush _cardFill = new(Colors.Transparent);
    private readonly SolidColorBrush _cardStroke = new(Colors.Transparent);
    private readonly SolidColorBrush _tipFill = new(Colors.Transparent);
    private readonly SolidColorBrush _tipStroke = new(Colors.Transparent);
    private readonly Popup _tip;
    private readonly Border _tipBox;
    private readonly TextBlock _tipText;
    private readonly DispatcherQueueTimer _tipTimer;

    private SideStrip? _strip;
    private bool _started;
    private bool _active;
    private bool _openedByPointing;

    /// <summary>False once the sidebar hid while the pointer was still at the edge: it must leave the edge before it opens again.</summary>
    private bool _armed = true;
    private long _edgeSince;
    private long _outsideSince;

    /// <summary>Where the window was last put (physical pixels).</summary>
    private RectInt32 _shownAt;

    /// <summary>The width (DIP) while its edge is being dragged; null otherwise.</summary>
    private double? _dragWidth;

    /// <summary>While a card's bottom edge is dragged: its widget, its content's box, its height and the pointer's when it started (DIP).</summary>
    private (WidgetView View, Grid Slot, double From, double PointerFrom)? _cardResize;

    /// <summary>The button the pointer rests on, for its tip.</summary>
    private Control? _tipFor;

    /// <summary>While a card's name row is pressed (and once it moves, dragged).</summary>
    private CardDrag? _cardDrag;

    /// <summary>The card a dragged widget would land on, marked; null: none.</summary>
    private string? _dropTargetId;

    /// <summary>A desktop widget is being dragged: a sliding sidebar may slide in for it.</summary>
    private bool _desktopDragging;

    /// <summary>A card being dragged by its name row.</summary>
    private sealed class CardDrag
    {
        internal CardDrag(string id, Border card, POINT from, int grabX, int grabY, double offsetFrom)
        {
            Id = id;
            Card = card;
            From = from;
            GrabX = grabX;
            GrabY = grabY;
            OffsetFrom = offsetFrom;
        }

        internal string Id { get; }

        internal Border Card { get; }

        /// <summary>Where the pointer was pressed (physical pixels).</summary>
        internal POINT From { get; }

        /// <summary>Where in the card it was pressed (physical pixels from its top-left).</summary>
        internal int GrabX { get; }

        internal int GrabY { get; }

        /// <summary>The list's scroll when it was pressed (DIP).</summary>
        internal double OffsetFrom { get; }

        internal bool Moving { get; set; }

        internal TranslateTransform Shift { get; } = new();
    }

    internal SidebarWindow(ShellHost host)
        : base(host, "Sidebar")
    {
        _tipText = new TextBlock { FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 240 };
        _tipBox = new Border
        {
            Child = _tipText,
            Padding = new Thickness(8, 5, 8, 7),
            CornerRadius = new CornerRadius(Radii.Small),
            BorderThickness = new Thickness(1),
            Background = _tipFill,
            BorderBrush = _tipStroke,
            IsHitTestVisible = false,
        };
        _tip = new Popup { Child = _tipBox, ShouldConstrainToRootBounds = true, IsHitTestVisible = false };
        _tipTimer = DispatcherQueue.CreateTimer();
        _tipTimer.Interval = TimeSpan.FromMilliseconds(500);
        _tipTimer.IsRepeating = false;
        _tipTimer.Tick += (_, _) =>
        {
            if (_tipFor is Control target)
            {
                ShowTip(target);
            }
        };

        var title = new TextBlock
        {
            Text = Strings.SetPageBoard,
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
        };
        _add = MakeHeaderButton(AddGlyph, Strings.SbAdd);
        _add.Flyout = MakeAddMenu();
        _pin = MakeHeaderButton(PinGlyph, Strings.SbDock);
        _pin.Click += (_, _) => Host.Settings.Update(s => s.SidebarDocked = !s.SidebarDocked, "sidebar");
        Button settings = MakeHeaderButton(SettingsGlyph, Strings.SbSettings);
        settings.Click += (_, _) =>
        {
            if (!Host.Settings.Current.SidebarDocked)
            {
                HideFlyout();
            }

            Host.OpenSettings(Machitan.SettingsUi.SettingsPages.Board);
        };

        var header = new Grid { Height = 40, Padding = new Thickness(12, 4, 8, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(title);
        Grid.SetColumn(_add, 1);
        header.Children.Add(_add);
        Grid.SetColumn(_pin, 2);
        header.Children.Add(_pin);
        Grid.SetColumn(settings, 3);
        header.Children.Add(settings);

        _readOnly = new TextBlock
        {
            Text = Strings.SbReadOnly,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.8,
            Visibility = Visibility.Collapsed,
        };
        _empty = new TextBlock
        {
            Text = Strings.SbEmpty,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Opacity = 0.8,
            Margin = new Thickness(12, 24, 12, 0),
        };
        _list = new StackPanel { Spacing = 12, Padding = new Thickness(12, 4, 12, 12) };
        _list.Children.Add(_readOnly);
        _list.Children.Add(_empty);
        _list.Children.Add(_cardGrid);
        _scroller = new ScrollViewer
        {
            Content = _list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        var content = new Grid();
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.Children.Add(header);
        Grid.SetRow(_scroller, 1);
        content.Children.Add(_scroller);
        Root.Children.Add(MakeBox(content, BoxRadius));
        _grip = MakeGrip();
        Root.Children.Add(_grip);
        Restyle();

        _poll = DispatcherQueue.CreateTimer();
        _poll.Interval = TimeSpan.FromMilliseconds(PollMs);
        _poll.IsRepeating = true;
        _poll.Tick += (_, _) => Poll();

        // Docked: back in place now and then (screens and free areas change).
        _heal = DispatcherQueue.CreateTimer();
        _heal.Interval = TimeSpan.FromSeconds(2);
        _heal.IsRepeating = true;
        _heal.Tick += (_, _) => KeepDocked();

        // While a card is dragged: the list scrolls at its top and bottom, and the card follows.
        _dragTimer = DispatcherQueue.CreateTimer();
        _dragTimer.Interval = TimeSpan.FromMilliseconds(DragStepMs);
        _dragTimer.IsRepeating = true;
        _dragTimer.Tick += (_, _) => DragStep();

        Activated += (_, e) => _active = e.WindowActivationState != WindowActivationState.Deactivated;
        Host.WidgetItems.Changed += OnWidgetsChanged;
        Host.FullScreenChanged += OnFullScreenChanged;
        Closed += (_, _) =>
        {
            _poll.Stop();
            _heal.Stop();
            _dragTimer.Stop();
            Host.WidgetItems.Changed -= OnWidgetsChanged;
            Host.FullScreenChanged -= OnFullScreenChanged;
            _strip?.Dispose();
            _strip = null;
        };

        BuildWidgets();
    }

    private protected override bool KeepOpenWhenDeactivated => Host.Settings.Current.SidebarDocked;

    /// <summary>The panel's width (DIP): as it is being dragged, else the setting.</summary>
    private double PanelWidthDip => _dragWidth ?? Host.Settings.Current.SidebarWidth;

    /// <summary>Starts following the settings (shows the docked sidebar, or waits at the edge).</summary>
    internal void Start()
    {
        _started = true;
        ApplySettings();
    }

    /// <summary>For good (shutdown): saves the widgets, hides, gives the edge back.</summary>
    internal void Stop()
    {
        _started = false;
        FlushWidgets();
        ApplySettings();
    }

    /// <summary>"Show the sidebar" (Settings): shows it with the keyboard.</summary>
    internal void ShowFromCommand()
    {
        if (!_started || !Host.Settings.Current.SidebarOn)
        {
            return;
        }

        _openedByPointing = false;
        ShowSidebar(activate: true);
    }

    private protected override void OnSettingsApplied(ShellSettings settings)
    {
        Restyle();
        ApplySettings();
    }

    private protected override void OnPreviewKey(KeyRoutedEventArgs e)
    {
        // Docked, Escape does not hide it (it lives there).
        if (e.Key == Windows.System.VirtualKey.Escape && Host.Settings.Current.SidebarDocked)
        {
            e.Handled = true;
        }
    }

    private protected override void OnHiding()
    {
        HideTip();
        FlushWidgets();
        _openedByPointing = false;
        _outsideSince = 0;
        _edgeSince = 0;
        _armed = false;
    }

    private protected override void OnOpenChanged(bool open)
    {
        foreach (WidgetView view in _views)
        {
            try
            {
                view.SetShown(open);
            }
            catch (Exception ex)
            {
                Log.Error("Sidebar: a widget (" + view.Item.Kind + ") could not start or stop.", ex);
            }
        }
    }

    // ---------------------------------------------------------------- showing and hiding

    /// <summary>Shows, hides, docks or undocks to match the settings.</summary>
    private void ApplySettings()
    {
        ShellSettings settings = Host.Settings.Current;
        LayoutCards();
        _grip.HorizontalAlignment = settings.SidebarLeft ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        _pin.Content = MakeGlyph(settings.SidebarDocked ? UnpinGlyph : PinGlyph, 14);
        string pinName = settings.SidebarDocked ? Strings.SbUndock : Strings.SbDock;
        AutomationProperties.SetName(_pin, pinName);

        if (!_started || !settings.SidebarOn)
        {
            _poll.Stop();
            _heal.Stop();
            HideFlyout();
            _strip?.Release();
            return;
        }

        if (settings.SidebarDocked)
        {
            _poll.Stop();
            _openedByPointing = false;
            KeepDocked();
            _heal.Start();
            return;
        }

        _heal.Stop();
        _strip?.Release();
        _poll.Start();
        if (IsOpen && WantedRect() is RectInt32 rect)
        {
            // Another side, or no longer docked: where a sliding sidebar goes; it hides once the
            // pointer is away from it (or, when it has the keyboard, at a click elsewhere).
            _openedByPointing = true;
            _shownAt = rect;
            PresentAt(rect, activate: false);
        }
    }

    /// <summary>Docked: the edge kept free, and the sidebar shown on it (hidden while a full-screen app is in front).</summary>
    private void KeepDocked()
    {
        ShellSettings settings = Host.Settings.Current;
        if (!_started || !settings.SidebarOn || !settings.SidebarDocked || _dragWidth is not null)
        {
            return;
        }

        nint bar = Host.BarHandle;
        if (bar != 0 && User32.TryGetMonitorRect(bar, out RECT monitor))
        {
            _strip ??= NewStrip();
            _strip.Set(monitor, (int)Math.Round((PanelWidthDip + (2 * EdgeGapDip)) * ScaleOf(bar)), settings.SidebarLeft);
        }

        if (Host.FullScreenAppInFront)
        {
            HideFlyout();
        }
        else if (WantedRect() is RectInt32 rect && !(IsOpen && Same(rect, _shownAt)))
        {
            _shownAt = rect;
            PresentAt(rect, activate: false);
        }
    }

    /// <summary>The indicators moved, came or went (ShellHost.LayoutCorners): a docked sidebar follows them.</summary>
    internal void FollowCorners()
    {
        if (_started && Host.Settings.Current.SidebarOn && Host.Settings.Current.SidebarDocked)
        {
            KeepDocked();
        }
    }

    /// <summary>
    /// Docked on this side and leaving room for the indicators: the screen's edge past the
    /// sidebar (its strip's outer edge), where the indicators of this side's corners keep their
    /// place; null otherwise (they stand beside the sidebar, at the free area's edge).
    /// </summary>
    internal int? RoomEdge(bool left)
    {
        ShellSettings settings = Host.Settings.Current;
        if (!_started || !settings.SidebarOn || !settings.SidebarDocked || !settings.SidebarLeaveRoom
            || settings.SidebarLeft != left || _strip?.Granted is not RECT strip)
        {
            return null;
        }

        return left ? strip.Left : strip.Right;
    }

    /// <summary>Back on top of the other always-on-top windows (the top strip's cover came), while docked and shown.</summary>
    internal void KeepOnTop()
    {
        if (_started && IsOpen && Host.Settings.Current.SidebarDocked)
        {
            User32.SetWindowPos(Hwnd, User32.HWND_TOPMOST, 0, 0, 0, 0,
                User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
        }
    }

    private static bool Same(RectInt32 a, RectInt32 b)
    {
        return a.X == b.X && a.Y == b.Y && a.Width == b.Width && a.Height == b.Height;
    }

    private SideStrip NewStrip()
    {
        var strip = new SideStrip();
        strip.Moved += () => DispatcherQueue.TryEnqueue(KeepDocked);
        return strip;
    }

    private void ShowSidebar(bool activate)
    {
        if (WantedRect() is not RectInt32 rect)
        {
            return;
        }

        bool wasOpen = IsOpen;
        _outsideSince = 0;
        _shownAt = rect;
        PresentAt(rect, activate);
        if (activate && wasOpen)
        {
            Activate();
            WindowActivator.BringToFront(Hwnd);
        }
    }

    /// <summary>
    /// Where the window goes (physical pixels, its 4 DIP padding included): along the free area's
    /// full height, 8 DIP from the screen's edges; docked, on the strip Windows kept for it, and
    /// over Machitan's own top (or bottom) strip too, unless it leaves room for the indicators
    /// in its side's corners (then 8 DIP past their row).
    /// </summary>
    private RectInt32? WantedRect()
    {
        ShellSettings settings = Host.Settings.Current;
        nint bar = Host.BarHandle;
        nint at = bar != 0 ? bar : Hwnd;
        if (!User32.TryGetWorkArea(at, out RECT work))
        {
            return null;
        }

        double scale = ScaleOf(at);
        int pad = (int)Math.Round(PadDip * scale);
        int gap = (int)Math.Round(EdgeGapDip * scale);
        int width = (int)Math.Round((PanelWidthDip + (2 * PadDip)) * scale);
        int upper = work.Top;
        int lower = work.Bottom;
        if (settings.SidebarDocked)
        {
            upper = Host.StripOn(top: true)?.Top ?? work.Top;
            lower = Host.StripOn(top: false)?.Bottom ?? work.Bottom;
            if (settings.SidebarLeaveRoom)
            {
                bool left = settings.SidebarLeft;
                if (Host.CornerRowRect(left ? ScreenCorner.TopLeft : ScreenCorner.TopRight) is RECT above)
                {
                    upper = Math.Max(upper, above.Bottom);
                }

                if (Host.CornerRowRect(left ? ScreenCorner.BottomLeft : ScreenCorner.BottomRight) is RECT below)
                {
                    lower = Math.Min(lower, below.Top);
                }
            }
        }

        int top = upper + gap - pad;
        int height = lower - upper - (2 * gap) + (2 * pad);

        // The outer edge (at the screen's edge) stays where it is; the width grows inward.
        int outerLeft = work.Left;
        int outerRight = work.Right;
        if (settings.SidebarDocked && _strip?.Granted is RECT strip)
        {
            outerLeft = strip.Left;
            outerRight = strip.Right;
        }

        int x = settings.SidebarLeft ? outerLeft + gap - pad : outerRight - gap + pad - width;

        return height > 0 ? new RectInt32(x, top, width, height) : null;
    }

    private static double ScaleOf(nint hwnd)
    {
        uint dpi = User32.GetDpiForWindow(hwnd);
        return (dpi == 0 ? 96 : dpi) / 96.0;
    }

    /// <summary>Every 120 ms while it slides in: opens it when the pointer rests at the edge; hides it once the pointer left it.</summary>
    private void Poll()
    {
        ShellSettings settings = Host.Settings.Current;
        if (!_started || !settings.SidebarOn || settings.SidebarDocked || !User32.GetCursorPos(out POINT pointer))
        {
            return;
        }

        long now = Environment.TickCount64;
        bool atEdge = AtEdge(pointer, settings.SidebarLeft);
        if (IsOpen)
        {
            _edgeSince = 0;

            // Clicked: a click elsewhere hides it (FlyoutWindow). Shown from Settings: the same.
            if (_active || !_openedByPointing)
            {
                _outsideSince = 0;
                return;
            }

            bool inside = User32.GetWindowRect(Hwnd, out RECT window) && window.Contains(pointer, (int)Math.Round(EdgeGapDip * ScaleOf(Hwnd)));
            if (inside || atEdge || ButtonHeld())
            {
                _outsideSince = 0;
                return;
            }

            if (_outsideSince == 0)
            {
                _outsideSince = now;
            }
            else if (now - _outsideSince >= LeaveHideMs)
            {
                HideFlyout();
            }

            return;
        }

        if (!atEdge)
        {
            _edgeSince = 0;
            _armed = true;
            return;
        }

        // A desktop widget held at the edge: the sidebar slides in for it (even with pointing off).
        bool forDrop = _desktopDragging && ButtonHeld();
        if ((!forDrop && (!settings.SidebarEdgeHover || !_armed || ButtonHeld())) || Host.FullScreenAppInFront)
        {
            _edgeSince = 0;
            return;
        }

        if (_edgeSince == 0)
        {
            _edgeSince = now;
        }
        else if (now - _edgeSince >= HoverDwellMs)
        {
            _edgeSince = 0;
            _armed = false;
            _openedByPointing = true;
            Log.Info(forDrop ? "Sidebar: opened for a widget being dragged." : "Sidebar: opened by pointing at the edge.");
            ShowSidebar(activate: false);
        }
    }

    /// <summary>The pointer is at the sidebar's edge of the bar's screen, away from its top and bottom.</summary>
    private bool AtEdge(POINT pointer, bool left)
    {
        nint bar = Host.BarHandle;
        if (bar == 0 || !User32.TryGetMonitorRect(bar, out RECT monitor) || !User32.TryGetWorkArea(bar, out RECT work))
        {
            return false;
        }

        bool onEdge = left ? pointer.X <= monitor.Left : pointer.X >= monitor.Right - 1;
        int keepOut = (int)Math.Round(KeepOutDip * ScaleOf(bar));
        return onEdge && pointer.Y >= work.Top + keepOut && pointer.Y <= work.Bottom - keepOut;
    }

    private static bool ButtonHeld()
    {
        return (User32.GetAsyncKeyState(User32.VK_LBUTTON) & 0x8000) != 0
            || (User32.GetAsyncKeyState(User32.VK_RBUTTON) & 0x8000) != 0;
    }

    private void OnFullScreenChanged()
    {
        if (!_started)
        {
            return;
        }

        if (Host.Settings.Current.SidebarDocked)
        {
            KeepDocked();
        }
        else if (Host.FullScreenAppInFront && IsOpen)
        {
            HideFlyout();
        }
    }

    // ---------------------------------------------------------------- the widgets

    private void OnWidgetsChanged()
    {
        BuildWidgets();
    }

    /// <summary>Makes the cards again from the store (after an add, a removal or a move).</summary>
    private void BuildWidgets()
    {
        foreach (WidgetView old in _views)
        {
            try
            {
                old.Flush();
                old.SetShown(false);
            }
            catch (Exception ex)
            {
                Log.Error("Sidebar: a widget (" + old.Item.Kind + ") could not be put away.", ex);
            }
        }

        if (_cardDrag is not null)
        {
            CancelCardDrag();
        }

        _dropTargetId = null;
        _views.Clear();
        _cards.Clear();
        _cardMarks.Clear();
        _cardGrid.Children.Clear();

        foreach (WidgetItem item in Host.WidgetItems.Items)
        {
            // Those on the desktop have windows of their own (DesktopWidgets).
            WidgetView? view = item.OnDesktop ? null : WidgetView.Create(Host, item);
            if (view is null)
            {
                continue;
            }

            FrameworkElement content;
            try
            {
                content = view.Build();
            }
            catch (Exception ex)
            {
                Log.Error("Sidebar: a widget (" + item.Kind + ") could not be made.", ex);
                continue;
            }

            Border card = MakeCard(view, content);
            _views.Add(view);
            _cards[item.Id] = (view, card);
            _cardGrid.Children.Add(card);
            if (IsOpen)
            {
                view.SetShown(true);
            }
        }

        _columns = 0;
        LayoutCards();
        _empty.Visibility = _views.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _readOnly.Visibility = Host.WidgetItems.IsReadOnlyBecauseOfBadFile ? Visibility.Visible : Visibility.Collapsed;
        _add.IsEnabled = !Host.WidgetItems.IsFull && !Host.WidgetItems.IsReadOnlyBecauseOfBadFile;
    }

    /// <summary>
    /// The cards in one column, or in two from <see cref="TwoColumnsFromDip"/> on: each card, in
    /// order, under the shorter column (MasonryPanel). Also while the width is dragged.
    /// </summary>
    private void LayoutCards()
    {
        int columns = PanelWidthDip >= TwoColumnsFromDip ? 2 : 1;
        if (columns == _columns)
        {
            return;
        }

        if (_columns != 0)
        {
            Log.Info("Sidebar: " + (columns == 2 ? "two columns of widgets." : "one column of widgets."));
        }

        _columns = columns;
        _cardGrid.Columns = columns;
    }

    private void FlushWidgets()
    {
        foreach (WidgetView view in _views)
        {
            try
            {
                view.Flush();
            }
            catch (Exception ex)
            {
                Log.Error("Sidebar: a widget (" + view.Item.Kind + ") could not be saved.", ex);
            }
        }
    }

    /// <summary>
    /// A widget in a card: its icon, name and menu above it. Its bottom edge can be dragged to
    /// set the content's height (Sam, 2026-11-26: "make the widgets resizable"; the width
    /// follows the sidebar's); a double-click there makes it as tall as its content again.
    /// </summary>
    private Border MakeCard(WidgetView view, FrameworkElement content)
    {
        FontIcon icon = MakeGlyph(view.Glyph, 14);
        icon.VerticalAlignment = VerticalAlignment.Center;
        var name = new TextBlock
        {
            Text = view.Title,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Button more = MakeHeaderButton(MoreGlyph, Fmt.F(Strings.WgOptions, view.Title));
        MenuFlyout menu = MakeMenu();
        menu.Opening += (_, _) => FillCardMenu(menu, view, more);
        more.Flyout = menu;

        // The name row: also where the card is dragged by (its background takes the pointer).
        var top = new Grid { ColumnSpacing = 10, Background = new SolidColorBrush(Colors.Transparent) };
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.Children.Add(icon);
        Grid.SetColumn(name, 1);
        top.Children.Add(name);
        Grid.SetColumn(more, 2);
        top.Children.Add(more);
        AutomationProperties.SetName(top, Fmt.F(Strings.WgDragHint, view.Title));
        string id = view.Item.Id;
        top.PointerPressed += (_, e) => OnCardHeaderPressed(id, top, e);
        top.PointerMoved += (_, e) =>
        {
            if (_cardDrag is not null)
            {
                DragStep();
                e.Handled = true;
            }
        };
        top.PointerReleased += (_, e) => OnCardHeaderReleased(top, e);
        top.PointerCaptureLost += (_, _) => CancelCardDrag();

        // The content in a box of its own: as tall as it wants, or the height it was given.
        var slot = new Grid();
        content.VerticalAlignment = VerticalAlignment.Stretch;
        slot.Children.Add(content);
        ApplyCardHeight(view, slot, view.Item.Height);

        var body = new StackPanel { Spacing = 8, Margin = new Thickness(12, 6, 6, 12) };
        body.Children.Add(top);
        body.Children.Add(slot);

        // The mark shown while a dragged widget would land here: over the card, not in its layout.
        var mark = new Border
        {
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(Math.Max(0, Radii.Medium - 1)),
            BorderBrush = _markStroke,
            Background = _markFill,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        _cardMarks[id] = mark;

        var inside = new Grid();
        inside.Children.Add(body);
        inside.Children.Add(MakeCardGrip(view, slot));
        inside.Children.Add(mark);
        return new Border
        {
            Child = inside,
            CornerRadius = new CornerRadius(Radii.Medium),
            Background = _cardFill,
            BorderBrush = _cardStroke,
            BorderThickness = new Thickness(1),
        };
    }

    private static void ApplyCardHeight(WidgetView view, Grid slot, double? height)
    {
        slot.Height = height ?? double.NaN;
        view.SetFixedHeight(height is not null);
    }

    /// <summary>A card's bottom edge: drag to set its content's height; double-click for as tall as its content.</summary>
    private SizeGrip MakeCardGrip(WidgetView view, Grid slot)
    {
        var grip = new SizeGrip(InputSystemCursorShape.SizeNorthSouth)
        {
            Height = GripDip,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(Radii.Medium, 0, Radii.Medium, 0),
            Background = new SolidColorBrush(Colors.Transparent),
        };
        AutomationProperties.SetName(grip, Fmt.F(Strings.WgResizeHeight, view.Title));
        grip.PointerPressed += (_, e) =>
        {
            if (_cardResize is null && e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed && grip.CapturePointer(e.Pointer))
            {
                _cardResize = (view, slot, slot.ActualHeight, e.GetCurrentPoint(null).Position.Y);
                e.Handled = true;
            }
        };
        grip.PointerMoved += (_, e) =>
        {
            if (_cardResize.HasValue && ReferenceEquals(_cardResize.Value.Slot, slot))
            {
                double height = Math.Clamp(
                    Math.Round(_cardResize.Value.From + e.GetCurrentPoint(null).Position.Y - _cardResize.Value.PointerFrom),
                    WidgetStore.MinHeight,
                    WidgetStore.MaxHeight);
                ApplyCardHeight(view, slot, height);
                e.Handled = true;
            }
        };
        grip.PointerReleased += (_, e) =>
        {
            grip.ReleasePointerCapture(e.Pointer);
            EndCardResize();
            e.Handled = true;
        };
        grip.PointerCaptureLost += (_, _) => EndCardResize();
        grip.DoubleTapped += (_, e) =>
        {
            e.Handled = true;
            _cardResize = null;
            ApplyCardHeight(view, slot, null);
            Host.WidgetItems.SetSize(view.Item.Id, view.Item.Width, null);
            Log.Info("Sidebar: a widget (" + view.Item.Kind + ") is as tall as its content again.");
        };
        return grip;
    }

    /// <summary>A card's bottom edge let go: its content's height is kept.</summary>
    private void EndCardResize()
    {
        if (!_cardResize.HasValue)
        {
            return;
        }

        WidgetView view = _cardResize.Value.View;
        Grid slot = _cardResize.Value.Slot;
        _cardResize = null;
        if (double.IsNaN(slot.Height))
        {
            return;
        }

        int height = (int)Math.Round(slot.Height);
        Host.WidgetItems.SetSize(view.Item.Id, view.Item.Width, height);
        Log.Info("Sidebar: a widget (" + view.Item.Kind + ") resized to " + height.ToString(System.Globalization.CultureInfo.InvariantCulture) + " DIP tall.");
    }

    // ---------------------------------------------------------------- dragging the widgets

    /// <summary>A press on a card's name row: a drag once the pointer moves (a click does nothing).</summary>
    private void OnCardHeaderPressed(string id, UIElement header, PointerRoutedEventArgs e)
    {
        if (_cardDrag is not null || _cardResize.HasValue || Host.WidgetItems.IsReadOnlyBecauseOfBadFile
            || !e.GetCurrentPoint(header).Properties.IsLeftButtonPressed
            || !_cards.TryGetValue(id, out (WidgetView View, Border Card) shown)
            || !User32.GetCursorPos(out POINT at))
        {
            return;
        }

        Windows.Foundation.Point inCard = e.GetCurrentPoint(shown.Card).Position;
        double scale = ScaleOf(Hwnd);
        var drag = new CardDrag(id, shown.Card, at, (int)Math.Round(inCard.X * scale), (int)Math.Round(inCard.Y * scale), _scroller.VerticalOffset);
        if (!header.CapturePointer(e.Pointer))
        {
            return;
        }

        _cardDrag = drag;
        e.Handled = true;
    }

    /// <summary>
    /// The pointer moved (or a moment passed) while a card is held: past a few DIP it is
    /// dragged; the card follows the pointer, the list scrolls at its top and bottom, and the
    /// card it would land on is marked (none past the inner edge: there it goes on the desktop).
    /// </summary>
    private void DragStep()
    {
        if (_cardDrag is not CardDrag drag || !User32.GetCursorPos(out POINT at) || !User32.GetWindowRect(Hwnd, out RECT window))
        {
            _dragTimer.Stop();
            return;
        }

        double scale = ScaleOf(Hwnd);
        double dx = (at.X - drag.From.X) / scale;
        double dy = (at.Y - drag.From.Y) / scale;
        if (!drag.Moving)
        {
            if (Math.Abs(dx) < DragStartDip && Math.Abs(dy) < DragStartDip)
            {
                return;
            }

            drag.Moving = true;
            HideTip();
            drag.Card.RenderTransform = drag.Shift;
            Canvas.SetZIndex(drag.Card, 1);
            _dragTimer.Start();
        }

        bool toDesktop = PastInnerEdge(at, window, scale);
        var point = new Windows.Foundation.Point((at.X - window.Left) / scale, (at.Y - window.Top) / scale);
        Windows.Foundation.Rect list = BoundsOf(_scroller);
        if (!toDesktop && point.X >= list.X && point.X <= list.X + list.Width && _scroller.ScrollableHeight > 0)
        {
            double step = 0;
            if (point.Y < list.Y + ScrollZoneDip)
            {
                step = -ScrollStepDip;
            }
            else if (point.Y > list.Y + list.Height - ScrollZoneDip)
            {
                step = ScrollStepDip;
            }

            if (step != 0)
            {
                double offset = Math.Clamp(_scroller.VerticalOffset + step, 0, _scroller.ScrollableHeight);
                _scroller.ChangeView(null, offset, null, disableAnimation: true);
            }
        }

        // The card moves with the list as it scrolls: that is made up, so it stays under the pointer.
        drag.Shift.X = dx;
        drag.Shift.Y = dy + (_scroller.VerticalOffset - drag.OffsetFrom);
        drag.Card.Opacity = toDesktop ? 0.45 : 0.8;
        SetDropTarget(toDesktop ? null : CardAt(point, drag.Id));
    }

    /// <summary>Let go: onto another card, the two change places; past the inner edge, it goes on the desktop where it was let go.</summary>
    private void OnCardHeaderReleased(UIElement header, PointerRoutedEventArgs e)
    {
        CardDrag? drag = _cardDrag;
        if (drag is null)
        {
            return;
        }

        e.Handled = true;
        string? target = _dropTargetId;
        bool toDesktop = false;
        int x = 0;
        int y = 0;
        if (drag.Moving && User32.GetCursorPos(out POINT at) && User32.GetWindowRect(Hwnd, out RECT window))
        {
            double scale = ScaleOf(Hwnd);
            toDesktop = PastInnerEdge(at, window, scale);

            // The desktop widget's window has the same padding round its card as this one.
            int pad = (int)Math.Round(PadDip * scale);
            x = at.X - drag.GrabX - pad;
            y = at.Y - drag.GrabY - pad;
        }

        bool moved = drag.Moving;
        EndCardDrag(drag);
        header.ReleasePointerCapture(e.Pointer);
        if (!moved)
        {
            return;
        }

        string id = drag.Id;
        if (toDesktop)
        {
            Log.Info("Sidebar: a widget was dragged out to the desktop.");
            DispatcherQueue.TryEnqueue(() =>
            {
                FlushWidgets();
                Host.WidgetItems.SetPlace(id, desktop: true, x: x, y: y);
            });
        }
        else if (target is string other)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                FlushWidgets();
                Host.WidgetItems.Swap(id, other);
            });
        }
    }

    /// <summary>The drag stopped without a drop (the pointer was taken away): the card goes back.</summary>
    private void CancelCardDrag()
    {
        if (_cardDrag is CardDrag drag)
        {
            EndCardDrag(drag);
        }
    }

    private void EndCardDrag(CardDrag drag)
    {
        _cardDrag = null;
        _dragTimer.Stop();
        drag.Card.RenderTransform = null;
        drag.Card.Opacity = 1;
        Canvas.SetZIndex(drag.Card, 0);
        SetDropTarget(null);
    }

    /// <summary>The pointer is past the sidebar's inner edge (the side facing the screen's middle) by <see cref="ToDesktopDip"/>.</summary>
    private bool PastInnerEdge(POINT at, RECT window, double scale)
    {
        int margin = (int)Math.Round(ToDesktopDip * scale);
        return Host.Settings.Current.SidebarLeft ? at.X > window.Right + margin : at.X < window.Left - margin;
    }

    /// <summary>The card under a point of the window (DIP), in the list's visible part; null: none (or only <paramref name="except"/>).</summary>
    private string? CardAt(Windows.Foundation.Point point, string? except)
    {
        if (!Inside(BoundsOf(_scroller), point))
        {
            return null;
        }

        foreach (KeyValuePair<string, (WidgetView View, Border Card)> pair in _cards)
        {
            if (pair.Key != except && Inside(BoundsOf(pair.Value.Card), point))
            {
                return pair.Key;
            }
        }

        return null;
    }

    /// <summary>Where an element is in the window (DIP); empty when it is not laid out.</summary>
    private static Windows.Foundation.Rect BoundsOf(FrameworkElement element)
    {
        try
        {
            if (element.ActualWidth <= 0 || element.ActualHeight <= 0)
            {
                return default;
            }

            return element.TransformToVisual(null).TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
        }
        catch (ArgumentException)
        {
            // Not in the window (being taken out).
            return default;
        }
    }

    private static bool Inside(Windows.Foundation.Rect rect, Windows.Foundation.Point point)
    {
        return rect.Width > 0 && rect.Height > 0
            && point.X >= rect.X && point.X < rect.X + rect.Width
            && point.Y >= rect.Y && point.Y < rect.Y + rect.Height;
    }

    /// <summary>Marks the card a dragged widget would land on (the accent edge and a light tint), or none.</summary>
    private void SetDropTarget(string? id)
    {
        if (id == _dropTargetId)
        {
            return;
        }

        if (_dropTargetId is string old && _cardMarks.TryGetValue(old, out Border? oldMark))
        {
            oldMark.Visibility = Visibility.Collapsed;
        }

        _dropTargetId = null;
        if (id is not null && _cardMarks.TryGetValue(id, out Border? mark))
        {
            Windows.UI.Color accent = AccentColor();
            _markStroke.Color = accent;
            _markFill.Color = ColorHelper.FromArgb(0x26, accent.R, accent.G, accent.B);
            mark.Visibility = Visibility.Visible;
            _dropTargetId = id;
        }
    }

    private static Windows.UI.Color AccentColor()
    {
        return Application.Current.Resources.TryGetValue("SystemAccentColor", out object? value) && value is Windows.UI.Color color
            ? color
            : ColorHelper.FromArgb(0xFF, 0x00, 0x78, 0xD4);
    }

    /// <summary>
    /// A desktop widget is dragged and the pointer is here (physical pixels): over the shown
    /// sidebar, the card it would go before is marked; held at the screen's edge, a sliding
    /// sidebar slides in for it (Poll).
    /// </summary>
    internal void DesktopDragOver(POINT at)
    {
        if (!_started || !Host.Settings.Current.SidebarOn || Host.WidgetItems.IsReadOnlyBecauseOfBadFile)
        {
            return;
        }

        _desktopDragging = true;
        SetDropTarget(IsOpen && User32.GetWindowRect(Hwnd, out RECT window) && window.Contains(at, 0) ? CardAtScreen(at, window) : null);
    }

    /// <summary>The desktop widget was let go (wherever): no more marks.</summary>
    internal void DesktopDragEnded()
    {
        _desktopDragging = false;
        SetDropTarget(null);
    }

    /// <summary>
    /// A desktop widget let go here (physical pixels): true when that is on the shown sidebar;
    /// then <paramref name="before"/> is the card it goes before (null: at the end).
    /// </summary>
    internal bool DesktopDropAt(POINT at, out string? before)
    {
        before = null;
        if (!_started || !IsOpen || !Host.Settings.Current.SidebarOn || Host.WidgetItems.IsReadOnlyBecauseOfBadFile
            || !User32.GetWindowRect(Hwnd, out RECT window) || !window.Contains(at, 0))
        {
            return false;
        }

        before = CardAtScreen(at, window);
        return true;
    }

    private string? CardAtScreen(POINT at, RECT window)
    {
        double scale = ScaleOf(Hwnd);
        return CardAt(new Windows.Foundation.Point((at.X - window.Left) / scale, (at.Y - window.Top) / scale), except: null);
    }

    private void FillCardMenu(MenuFlyout menu, WidgetView view, Button more)
    {
        menu.Items.Clear();
        string id = view.Item.Id;
        bool canChange = !Host.WidgetItems.IsReadOnlyBecauseOfBadFile;

        var up = new MenuFlyoutItem { Text = Strings.WgMoveUp, Icon = MakeGlyph(UpGlyph, 16), IsEnabled = canChange && Host.WidgetItems.CanMove(id, -1) };
        up.Click += (_, _) => MoveWidget(id, -1);
        var down = new MenuFlyoutItem { Text = Strings.WgMoveDown, Icon = MakeGlyph(DownGlyph, 16), IsEnabled = canChange && Host.WidgetItems.CanMove(id, 1) };
        down.Click += (_, _) => MoveWidget(id, 1);
        var remove = new MenuFlyoutItem { Text = Strings.WgRemove, Icon = MakeGlyph(RemoveGlyph, 16), IsEnabled = canChange };
        remove.Click += (_, _) =>
        {
            if (view.HasUserContent)
            {
                // What was typed goes with it: asked first (after the menu has closed).
                DispatcherQueue.TryEnqueue(() => AskRemove(view, more));
            }
            else
            {
                Host.WidgetItems.Remove(id);
            }
        };

        var toDesktop = new MenuFlyoutItem { Text = Strings.WgToDesktop, Icon = MakeGlyph(DesktopGlyph, 16), IsEnabled = canChange };
        toDesktop.Click += (_, _) =>
        {
            FlushWidgets();
            Host.WidgetItems.SetPlace(id, desktop: true);
        };

        if (view.HasOptions)
        {
            var customize = new MenuFlyoutItem { Text = Strings.WgCustomize, Icon = MakeGlyph(CustomizeGlyph, 16), IsEnabled = canChange };
            customize.Click += (_, _) => DispatcherQueue.TryEnqueue(() => ShowOptions(view, more));
            menu.Items.Add(customize);
            menu.Items.Add(new MenuFlyoutSeparator());
        }

        menu.Items.Add(up);
        menu.Items.Add(down);
        menu.Items.Add(toDesktop);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(remove);
    }

    private void MoveWidget(string id, int step)
    {
        FlushWidgets();
        Host.WidgetItems.Move(id, step);
    }

    /// <summary>
    /// A widget's choices (Customize), beside its card on the screen's open side (left of a
    /// sidebar at the right), so the widget stays in view while it changes (Sam, 2026-11-26:
    /// "make the 'customize' flyout doesn't cover the widget"). A popup window of its own: there
    /// it is in the screen's free area, which a docked sidebar is not in. In the sidebar's theme.
    /// </summary>
    private void ShowOptions(WidgetView view, Button more)
    {
        FrameworkElement options = view.BuildOptions();
        options.RequestedTheme = IsDark ? ElementTheme.Dark : ElementTheme.Light;
        FrameworkElement anchor = _cards.TryGetValue(view.Item.Id, out (WidgetView View, Border Card) shown) ? shown.Card : more;
        var flyout = new Flyout
        {
            ShouldConstrainToRootBounds = false,
            Placement = Host.Settings.Current.SidebarLeft ? FlyoutPlacementMode.RightEdgeAlignedTop : FlyoutPlacementMode.LeftEdgeAlignedTop,
            FlyoutPresenterStyle = SolidStyle(typeof(FlyoutPresenter)),
            Content = new ScrollViewer
            {
                Content = options,
                MaxHeight = 480,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            },
        };
        flyout.ShowAt(anchor);
    }

    /// <summary>"Remove this note? Its text is deleted." with Remove, by the card's menu button.</summary>
    private void AskRemove(WidgetView view, Button more)
    {
        var flyout = new Flyout
        {
            ShouldConstrainToRootBounds = true,
            Placement = FlyoutPlacementMode.BottomEdgeAlignedRight,
            FlyoutPresenterStyle = SolidStyle(typeof(FlyoutPresenter)),
        };
        var text = new TextBlock { Text = Strings.WgRemoveAsk, TextWrapping = TextWrapping.Wrap, MaxWidth = 240 };
        var confirm = new Button { Content = Strings.WgRemove, HorizontalAlignment = HorizontalAlignment.Right };
        if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out object? style) && style is Style accent)
        {
            confirm.Style = accent;
        }

        AutomationProperties.SetName(confirm, Strings.WgRemove);
        confirm.Click += (_, _) =>
        {
            flyout.Hide();
            Host.WidgetItems.Remove(view.Item.Id);
        };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(text);
        panel.Children.Add(confirm);
        flyout.Content = panel;
        flyout.ShowAt(more);
    }

    /// <summary>
    /// A menu kept inside the sidebar's window, under its button's right edge. As a window of its
    /// own Windows would keep it inside the screen's free area, which the docked sidebar is not in.
    /// Inside the window its see-through background showed the cards through it (Sam's screenshot,
    /// 2026-11-26), so it gets a solid one, the tip's (the theme's menu colour).
    /// </summary>
    private MenuFlyout MakeMenu()
    {
        return new MenuFlyout
        {
            ShouldConstrainToRootBounds = true,
            Placement = FlyoutPlacementMode.BottomEdgeAlignedRight,
            MenuFlyoutPresenterStyle = SolidStyle(typeof(MenuFlyoutPresenter)),
        };
    }

    /// <summary>A popup's look with a solid background and the tip's edge, which follow the theme.</summary>
    private Style SolidStyle(Type presenter)
    {
        var style = new Style(presenter);
        style.Setters.Add(new Setter(Control.BackgroundProperty, _tipFill));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, _tipStroke));
        return style;
    }

    private MenuFlyout MakeAddMenu()
    {
        MenuFlyout menu = MakeMenu();
        foreach (string kind in WidgetKinds.Known)
        {
            string adding = kind;
            var item = new MenuFlyoutItem { Text = WidgetView.NameOf(kind), Icon = MakeGlyph(WidgetView.GlyphOf(kind), 16) };
            item.Click += (_, _) =>
            {
                FlushWidgets();

                // The store's change builds the cards again; then the new one comes into view with the keyboard.
                if (Host.WidgetItems.Add(adding) is WidgetItem added && _cards.TryGetValue(added.Id, out (WidgetView View, Border Card) shown))
                {
                    // After the menu has closed (it gives the keyboard back to the + as it goes).
                    DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
                    {
                        shown.Card.StartBringIntoView();
                        shown.View.FocusFirst();
                    });
                }
            };
            menu.Items.Add(item);
        }

        return menu;
    }

    // ---------------------------------------------------------------- the width

    /// <summary>The inner edge: drag to change the width; double-click for the standard width.</summary>
    private SizeGrip MakeGrip()
    {
        var grip = new SizeGrip
        {
            Width = GripDip,
            Background = new SolidColorBrush(Colors.Transparent),
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(grip, Strings.SbResize);
        grip.PointerPressed += (_, e) =>
        {
            if (e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed && grip.CapturePointer(e.Pointer))
            {
                _dragWidth = PanelWidthDip;
                e.Handled = true;
            }
        };
        grip.PointerMoved += (_, e) =>
        {
            if (_dragWidth is not null)
            {
                DragTo();
                e.Handled = true;
            }
        };
        grip.PointerReleased += (_, e) =>
        {
            grip.ReleasePointerCapture(e.Pointer);
            EndDrag();
            e.Handled = true;
        };
        grip.PointerCaptureLost += (_, _) => EndDrag();
        grip.DoubleTapped += (_, e) =>
        {
            e.Handled = true;
            Host.Settings.Update(s => s.SidebarWidth = ShellSettings.DefaultSidebarWidth, "sidebar");
        };
        return grip;
    }

    /// <summary>While dragging: the panel's inner edge follows the pointer; its outer edge stays.</summary>
    private void DragTo()
    {
        if (!User32.GetCursorPos(out POINT pointer) || !User32.GetWindowRect(Hwnd, out RECT window))
        {
            return;
        }

        double scale = ScaleOf(Hwnd);
        int pad = (int)Math.Round(PadDip * scale);
        double widthPx = Host.Settings.Current.SidebarLeft ? pointer.X - (window.Left + pad) : window.Right - pad - pointer.X;
        double width = Math.Clamp(Math.Round(widthPx / scale), ShellSettings.MinSidebarWidth, ShellSettings.MaxSidebarWidth);
        if (_dragWidth == width)
        {
            return;
        }

        _dragWidth = width;
        LayoutCards();
        if (IsOpen && WantedRect() is RectInt32 rect)
        {
            _shownAt = rect;
            PresentAt(rect, activate: false);
        }
    }

    /// <summary>The drag ended: the width is kept (docked, the strip is made that wide).</summary>
    private void EndDrag()
    {
        if (_dragWidth is not double width)
        {
            return;
        }

        _dragWidth = null;
        int kept = (int)Math.Round(width);
        Log.Info("Sidebar: resized to " + kept.ToString(System.Globalization.CultureInfo.InvariantCulture) + " DIP.");
        if (kept == Host.Settings.Current.SidebarWidth)
        {
            return;
        }

        Host.Settings.Update(s => s.SidebarWidth = kept, "sidebar");
    }

    private Button MakeHeaderButton(int glyph, string name)
    {
        var button = new Button
        {
            Content = MakeGlyph(glyph, 14),
            Width = 32,
            Height = 32,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(Radii.Small),
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(button, name);
        AddTip(button);
        return button;
    }

    // ---------------------------------------------------------------- tips

    /// <summary>
    /// A tip under <paramref name="target"/> with its name (Sam's screenshot: a WinUI tooltip opened
    /// beside the docked sidebar, as its menus did, since it is a window of its own). This one is
    /// drawn inside the sidebar's window: after half a second of pointing, or at once when the
    /// keyboard reaches the button.
    /// </summary>
    private void AddTip(Control target)
    {
        target.PointerEntered += (_, _) =>
        {
            _tipFor = target;
            _tipTimer.Stop();
            _tipTimer.Start();
        };
        target.PointerExited += (_, _) => HideTip();
        target.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) => HideTip()), handledEventsToo: true);
        target.GotFocus += (_, _) =>
        {
            if (target.FocusState == FocusState.Keyboard)
            {
                ShowTip(target);
            }
        };
        target.LostFocus += (_, _) => HideTip();
    }

    private void ShowTip(Control target)
    {
        _tipTimer.Stop();
        string text = AutomationProperties.GetName(target);
        if (target.XamlRoot is not XamlRoot root || string.IsNullOrEmpty(text) || !IsOpen)
        {
            return;
        }

        _tipText.Text = text;
        _tip.XamlRoot = root;
        _tipBox.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        Windows.Foundation.Size size = _tipBox.DesiredSize;
        Windows.Foundation.Point at = target.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));

        // Under the button, its right edge with the button's, inside the window.
        double x = at.X + target.ActualWidth - size.Width;
        x = Math.Clamp(x, 8, Math.Max(8, root.Size.Width - size.Width - 8));
        _tip.HorizontalOffset = x;
        _tip.VerticalOffset = at.Y + target.ActualHeight + 4;
        _tip.IsOpen = true;
    }

    private void HideTip()
    {
        _tipTimer.Stop();
        _tipFor = null;
        _tip.IsOpen = false;
    }

    /// <summary>The cards' fill and edge, a shade lighter than the panel, for the dark or light look.</summary>
    private void Restyle()
    {
        _cardFill.Color = IsDark ? ColorHelper.FromArgb(0x12, 0xFF, 0xFF, 0xFF) : ColorHelper.FromArgb(0x99, 0xFF, 0xFF, 0xFF);
        _cardStroke.Color = IsDark ? ColorHelper.FromArgb(0x14, 0xFF, 0xFF, 0xFF) : ColorHelper.FromArgb(0x0F, 0x00, 0x00, 0x00);
        _tipFill.Color = IsDark ? ColorHelper.FromArgb(0xFF, 0x2C, 0x2C, 0x2C) : ColorHelper.FromArgb(0xFF, 0xF9, 0xF9, 0xF9);
        _tipStroke.Color = IsDark ? ColorHelper.FromArgb(0x33, 0x00, 0x00, 0x00) : ColorHelper.FromArgb(0x0F, 0x00, 0x00, 0x00);
        _tipText.Foreground = new SolidColorBrush(IsDark ? Colors.White : ColorHelper.FromArgb(0xE4, 0x00, 0x00, 0x00));
    }
}
