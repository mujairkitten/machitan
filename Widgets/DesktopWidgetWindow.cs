using System;
using System.Globalization;
using System.Runtime.InteropServices;
using Machitan.Core;
using Machitan.Flyouts;
using Machitan.Interop;
using Machitan.Shell;
using Microsoft.UI;
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
/// One widget on the desktop (Sam, 2026-11-26: "made widgets also placed at anywhere in
/// desktop"; asked: "On the desktop" = behind the windows): a card in the bar's fill, with the
/// widget's icon, name and menu (Move to the sidebar, Remove) on top, kept behind every other
/// window (it shows when the desktop does, e.g. after Win+D, which minimises the apps). Drag it
/// by its name row; where it is let go is kept in widgets.json. Clicking in it gives it the
/// keyboard (to type a note) without bringing it in front of the other windows.
/// Resizable (Sam, 2026-11-26: "make the widgets resizable"): drag any edge or corner (from
/// 200 to 1000 DIP wide, its content 60 to 1000 DIP tall); its size is kept in widgets.json;
/// a double-click on an edge or corner goes back to the standard size.
/// Let go on the shown sidebar, it goes into it (Sam, 2026-11-26: "make the widgets draggable
/// (just swapping positions and in to/out from the sidebar)"): before the card under the
/// pointer, or at the end; held at the screen's edge, a sliding sidebar slides in for it.
/// </summary>
public sealed partial class DesktopWidgetWindow : FlyoutWindow
{
    private const double WidthDip = 320;
    private const double HeaderDip = 32;
    private const double CardPadDip = 12;

    /// <summary>The card around its content: the top padding, the name row and its gap, the bottom padding (DIP).</summary>
    private const double ChromeDip = 4 + HeaderDip + 6 + CardPadDip;

    /// <summary>The resize strips along the edges, and the squares at the corners (DIP, inside the card).</summary>
    private const double EdgeGripDip = 6;
    private const double CornerGripDip = 10;

    /// <summary>Where a new one goes: this far in from the free area's top-right corner, each next one a step further.</summary>
    private const double FirstSpotDip = 24;
    private const double CascadeDip = 28;

    private const uint WmWindowPosChanging = 0x0046;
    private const nint HwndBottom = 1;

    private const int MoreGlyph = 0xE712;
    private const int SidebarGlyph = 0xE8A0;
    private const int RemoveGlyph = 0xE74D;
    private const int CustomizeGlyph = 0xE771;

    private readonly WidgetView _view;
    private readonly Button _more;

    /// <summary>The card itself, which the choices open beside.</summary>
    private readonly Grid _box;

    /// <summary>The choices panel is about this wide (DIP, with its edges): beside the card when there is room on its right.</summary>
    private const double OptionsWidthDip = 340;
    private bool _dragging;
    private POINT _dragFrom;
    private PointInt32 _windowFrom;
    private bool _checkedDepth;

    /// <summary>While resizing: the edges being dragged, where the pointer and the window were.</summary>
    private Edges _resizing;
    private POINT _resizeFrom;
    private RectInt32 _rectFrom;

    [Flags]
    private enum Edges
    {
        None = 0,
        Left = 1,
        Top = 2,
        Right = 4,
        Bottom = 8,
    }

    internal DesktopWidgetWindow(ShellHost host, WidgetItem item, WidgetView view)
        : base(host, "Desktop widget", takesFocus: true, onTop: false)
    {
        Item = item;
        _view = view;

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
        _more = new Button
        {
            Content = MakeGlyph(MoreGlyph, 14),
            Width = 32,
            Height = 28,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(SmallRadius),
            VerticalAlignment = VerticalAlignment.Center,
        };
        string options = Fmt.F(Strings.WgOptions, view.Title);
        AutomationProperties.SetName(_more, options);
        ToolTipService.SetToolTip(_more, options);
        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        menu.Opening += (_, _) => FillMenu(menu);
        _more.Flyout = menu;

        // The name row: also where it is dragged by (its background takes the pointer).
        var header = new Grid
        {
            ColumnSpacing = 10,
            Height = HeaderDip,
            Background = new SolidColorBrush(Colors.Transparent),
        };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(icon);
        Grid.SetColumn(name, 1);
        header.Children.Add(name);
        Grid.SetColumn(_more, 2);
        header.Children.Add(_more);
        AutomationProperties.SetName(header, Fmt.F(Strings.WgDragHint, view.Title));
        header.PointerPressed += OnHeaderPressed;
        header.PointerMoved += OnHeaderMoved;
        header.PointerReleased += OnHeaderReleased;
        header.PointerCaptureLost += (_, _) => EndDrag(letGo: false);

        FrameworkElement content = view.Build();
        content.VerticalAlignment = VerticalAlignment.Stretch;
        view.SetFixedHeight(true);
        var card = new Grid { RowSpacing = 6, Padding = new Thickness(CardPadDip, 4, 6, CardPadDip) };
        card.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        card.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        card.Children.Add(header);
        Grid.SetRow(content, 1);
        card.Children.Add(content);
        _box = MakeBox(card, BoxRadius);
        Root.Children.Add(_box);
        AddGrips(view.Title);
    }

    internal WidgetItem Item { get; }

    private protected override bool KeepOpenWhenDeactivated => true;

    /// <summary>Puts it on the desktop: at its kept spot (inside a screen's free area), else at the next new spot.</summary>
    internal void ShowWidget(int newIndex)
    {
        nint bar = Host.BarHandle;
        double scale = ScaleOf(bar != 0 ? bar : Hwnd);
        (int width, int height) = SizeFor(scale);
        int x;
        int y;
        if (Item.X is int keptX && Item.Y is int keptY)
        {
            x = keptX;
            y = keptY;
        }
        else
        {
            RECT work = default;
            if (bar == 0 || !User32.TryGetWorkArea(bar, out work))
            {
                User32.TryGetWorkArea(Hwnd, out work);
            }

            int first = (int)Math.Round(FirstSpotDip * scale);
            int step = (int)Math.Round(CascadeDip * scale) * newIndex;
            int sidebar = Host.Settings.Current.SidebarOn && !Host.Settings.Current.SidebarLeft
                ? (int)Math.Round((Host.Settings.Current.SidebarWidth + 24) * scale)
                : 0;
            x = work.Right - first - width - sidebar - step;
            y = work.Top + first + step;
        }

        PresentAt(new RectInt32(x, y, width, height), activate: false);

        // Its own screen's scale and free area (it may be on another screen than the bar).
        double own = ScaleOf(Hwnd);
        if (Math.Abs(own - scale) > 0.01)
        {
            (width, height) = SizeFor(own);
        }

        if (User32.TryGetWorkArea(Hwnd, out RECT area))
        {
            x = Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - width));
            y = Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - height));
        }

        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
        Host.WidgetItems.SetPosition(Item.Id, x, y);
        KeepBehind();
        _view.SetShown(true);
    }

    /// <summary>For good: saves what was typed and closes the window.</summary>
    internal void RemoveWidget()
    {
        try
        {
            _view.Flush();
            _view.SetShown(false);
        }
        catch (Exception ex)
        {
            Log.Error("Desktop widget (" + Item.Kind + "): could not be put away.", ex);
        }

        HideFlyout();
        Close();
    }

    internal void Flush()
    {
        _view.Flush();
    }

    private protected override void OnPreviewKey(KeyRoutedEventArgs e)
    {
        // It lives on the desktop: Escape does not take it away.
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
        }
    }

    private protected override void OnHiding()
    {
        _view.Flush();
    }

    /// <summary>Whatever moves it in the stacking order (a click that gives it the keyboard), it stays behind the other windows.</summary>
    private protected override bool OnRawMessage(uint message, nint wParam, nint lParam, out nint result)
    {
        result = 0;
        if (message == WmWindowPosChanging && lParam != 0)
        {
            // WINDOWPOS: hwnd, hwndInsertAfter, x, y, cx, cy, flags.
            int flagsAt = (2 * IntPtr.Size) + 16;
            uint flags = (uint)Marshal.ReadInt32(lParam, flagsAt);
            if ((flags & User32.SWP_NOZORDER) == 0)
            {
                Marshal.WriteIntPtr(lParam, IntPtr.Size, HwndBottom);
            }
        }

        return false;
    }

    private static double ScaleOf(nint hwnd)
    {
        uint dpi = User32.GetDpiForWindow(hwnd);
        return (dpi == 0 ? 96 : dpi) / 96.0;
    }

    /// <summary>The window's size (physical pixels): its kept size, else the standard one.</summary>
    private (int Width, int Height) SizeFor(double scale)
    {
        double widthDip = Item.Width ?? WidthDip;
        double heightDip = ChromeDip + (Item.Height ?? _view.DesktopHeightDip);
        return ((int)Math.Round((widthDip + (2 * PadDip)) * scale), (int)Math.Round((heightDip + (2 * PadDip)) * scale));
    }

    /// <summary>
    /// Behind every other window, in front of Windows' desktop. The first time, checks that it
    /// really is in front of the desktop window and logs it (a test line, for Sam's log).
    /// </summary>
    private void KeepBehind()
    {
        User32.SetWindowPos(Hwnd, HwndBottom, 0, 0, 0, 0,
            User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE | User32.SWP_NOOWNERZORDER);
        if (_checkedDepth)
        {
            return;
        }

        _checkedDepth = true;
        nint desktop = User32.GetShellWindow();
        bool inFront = false;
        nint below = User32.GetWindow(Hwnd, User32.GW_HWNDNEXT);
        for (int steps = 0; below != 0 && steps < 4000; steps++)
        {
            if (below == desktop)
            {
                inFront = true;
                break;
            }

            below = User32.GetWindow(below, User32.GW_HWNDNEXT);
        }

        Log.Info("Desktop widget (test): " + (desktop == 0
            ? "Windows reports no desktop window."
            : inFront ? "in front of Windows' desktop window, behind the others." : "NOT in front of Windows' desktop window; it may be hidden."));
    }

    private void FillMenu(MenuFlyout menu)
    {
        menu.Items.Clear();
        bool canChange = !Host.WidgetItems.IsReadOnlyBecauseOfBadFile;
        var toSidebar = new MenuFlyoutItem
        {
            Text = Strings.WgToSidebar,
            Icon = MakeGlyph(SidebarGlyph, 16),
            IsEnabled = canChange && Host.Settings.Current.SidebarOn,
        };
        toSidebar.Click += (_, _) =>
        {
            _view.Flush();
            Host.WidgetItems.SetPlace(Item.Id, desktop: false);
        };
        var remove = new MenuFlyoutItem { Text = Strings.WgRemove, Icon = MakeGlyph(RemoveGlyph, 16), IsEnabled = canChange };
        remove.Click += (_, _) =>
        {
            if (_view.HasUserContent)
            {
                DispatcherQueue.TryEnqueue(AskRemove);
            }
            else
            {
                Host.WidgetItems.Remove(Item.Id);
            }
        };
        if (_view.HasOptions)
        {
            var customize = new MenuFlyoutItem { Text = Strings.WgCustomize, Icon = MakeGlyph(CustomizeGlyph, 16), IsEnabled = canChange };
            customize.Click += (_, _) => DispatcherQueue.TryEnqueue(ShowOptions);
            menu.Items.Add(customize);
            menu.Items.Add(new MenuFlyoutSeparator());
        }

        menu.Items.Add(toSidebar);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(remove);
    }

    /// <summary>
    /// The widget's choices (Customize), in a popup window of its own beside the card (Sam,
    /// 2026-11-26: "don't put the flyout inside the widget itself", and not over it): on its
    /// right when the screen has room there, else on its left. In its theme.
    /// </summary>
    private void ShowOptions()
    {
        FrameworkElement options = _view.BuildOptions();
        options.RequestedTheme = IsDark ? ElementTheme.Dark : ElementTheme.Light;
        bool right = true;
        if (User32.GetWindowRect(Hwnd, out RECT window) && User32.TryGetWorkArea(Hwnd, out RECT area))
        {
            right = area.Right - window.Right >= OptionsWidthDip * ScaleOf(Hwnd);
        }

        var flyout = new Flyout
        {
            ShouldConstrainToRootBounds = false,
            Placement = right ? FlyoutPlacementMode.RightEdgeAlignedTop : FlyoutPlacementMode.LeftEdgeAlignedTop,
            Content = new ScrollViewer
            {
                Content = options,
                MaxHeight = 480,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            },
        };
        flyout.ShowAt(_box);
    }

    private void AskRemove()
    {
        // A popup window of its own (a Flyout is otherwise kept inside the widget).
        var flyout = new Flyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight, ShouldConstrainToRootBounds = false };
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
            Host.WidgetItems.Remove(Item.Id);
        };
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(text);
        panel.Children.Add(confirm);
        flyout.Content = panel;
        flyout.ShowAt(_more);
    }

    // ---------------------------------------------------------------- dragging

    private void OnHeaderPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_resizing != Edges.None || sender is not UIElement header || !e.GetCurrentPoint(header).Properties.IsLeftButtonPressed
            || !User32.GetCursorPos(out POINT at) || !header.CapturePointer(e.Pointer))
        {
            return;
        }

        _dragging = true;
        _dragFrom = at;
        _windowFrom = AppWindow.Position;
        e.Handled = true;
    }

    private void OnHeaderMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || !User32.GetCursorPos(out POINT at))
        {
            return;
        }

        AppWindow.Move(new PointInt32(_windowFrom.X + at.X - _dragFrom.X, _windowFrom.Y + at.Y - _dragFrom.Y));
        Host.SidebarDragOver(at);
        e.Handled = true;
    }

    private void OnHeaderReleased(object sender, PointerRoutedEventArgs e)
    {
        // Ended first, so the lost capture that the release brings finds nothing to end.
        EndDrag(letGo: true);
        if (sender is UIElement header)
        {
            header.ReleasePointerCapture(e.Pointer);
        }

        e.Handled = true;
    }

    /// <summary>
    /// Let go: on the shown sidebar, it goes into it; elsewhere it is kept inside its screen's
    /// free area, and where it is is saved. The pointer taken away (not let go): only the latter.
    /// </summary>
    private void EndDrag(bool letGo)
    {
        if (!_dragging)
        {
            return;
        }

        _dragging = false;
        if (letGo && User32.GetCursorPos(out POINT at) && Host.SidebarDropAt(at, out string? before))
        {
            Host.SidebarDragEnded();
            Log.Info("Desktop widget (" + Item.Kind + "): dropped on the sidebar.");
            string id = Item.Id;

            // After this event: the move closes this window.
            DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    _view.Flush();
                }
                catch (Exception ex)
                {
                    Log.Error("Desktop widget (" + Item.Kind + "): could not be saved before moving to the sidebar.", ex);
                }

                Host.WidgetItems.SetPlace(id, desktop: false, before: before, atEnd: before is null);
            });
            return;
        }

        Host.SidebarDragEnded();
        KeepOnScreen();
        PointInt32 now = AppWindow.Position;
        Log.Info("Desktop widget (" + Item.Kind + "): moved to " + now.X.ToString(CultureInfo.InvariantCulture) + ", " + now.Y.ToString(CultureInfo.InvariantCulture) + ".");
    }

    // ---------------------------------------------------------------- resizing

    /// <summary>The resize strips and corners, inside the card (the window is cut to it), on top of everything.</summary>
    private void AddGrips(string title)
    {
        string name = Fmt.F(Strings.WgResize, title);
        double edge = EdgeGripDip;
        double corner = CornerGripDip;
        AddGrip(Edges.Left, InputSystemCursorShape.SizeWestEast, name, edge, double.NaN, HorizontalAlignment.Left, VerticalAlignment.Stretch, new Thickness(0, corner, 0, corner));
        AddGrip(Edges.Right, InputSystemCursorShape.SizeWestEast, name, edge, double.NaN, HorizontalAlignment.Right, VerticalAlignment.Stretch, new Thickness(0, corner, 0, corner));
        AddGrip(Edges.Top, InputSystemCursorShape.SizeNorthSouth, name, double.NaN, edge, HorizontalAlignment.Stretch, VerticalAlignment.Top, new Thickness(corner, 0, corner, 0));
        AddGrip(Edges.Bottom, InputSystemCursorShape.SizeNorthSouth, name, double.NaN, edge, HorizontalAlignment.Stretch, VerticalAlignment.Bottom, new Thickness(corner, 0, corner, 0));
        AddGrip(Edges.Left | Edges.Top, InputSystemCursorShape.SizeNorthwestSoutheast, name, corner, corner, HorizontalAlignment.Left, VerticalAlignment.Top, new Thickness(0));
        AddGrip(Edges.Right | Edges.Bottom, InputSystemCursorShape.SizeNorthwestSoutheast, name, corner, corner, HorizontalAlignment.Right, VerticalAlignment.Bottom, new Thickness(0));
        AddGrip(Edges.Right | Edges.Top, InputSystemCursorShape.SizeNortheastSouthwest, name, corner, corner, HorizontalAlignment.Right, VerticalAlignment.Top, new Thickness(0));
        AddGrip(Edges.Left | Edges.Bottom, InputSystemCursorShape.SizeNortheastSouthwest, name, corner, corner, HorizontalAlignment.Left, VerticalAlignment.Bottom, new Thickness(0));
    }

    private void AddGrip(Edges edges, InputSystemCursorShape shape, string name, double width, double height, HorizontalAlignment across, VerticalAlignment down, Thickness margin)
    {
        var grip = new SizeGrip(shape)
        {
            Width = width,
            Height = height,
            HorizontalAlignment = across,
            VerticalAlignment = down,
            Margin = margin,
            Background = new SolidColorBrush(Colors.Transparent),
        };
        AutomationProperties.SetName(grip, name);
        grip.PointerPressed += (_, e) =>
        {
            if (_dragging || _resizing != Edges.None || !e.GetCurrentPoint(grip).Properties.IsLeftButtonPressed
                || !User32.GetCursorPos(out POINT at) || !grip.CapturePointer(e.Pointer))
            {
                return;
            }

            _resizing = edges;
            _resizeFrom = at;
            _rectFrom = new RectInt32(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);
            e.Handled = true;
        };
        grip.PointerMoved += (_, e) =>
        {
            if (_resizing != Edges.None)
            {
                ResizeTo();
                e.Handled = true;
            }
        };
        grip.PointerReleased += (_, e) =>
        {
            grip.ReleasePointerCapture(e.Pointer);
            EndResize();
            e.Handled = true;
        };
        grip.PointerCaptureLost += (_, _) => EndResize();
        grip.DoubleTapped += (_, e) =>
        {
            e.Handled = true;
            ResetSize();
        };
        Root.Children.Add(grip);
    }

    /// <summary>While resizing: the dragged edges follow the pointer, the others stay; kept within the size limits.</summary>
    private void ResizeTo()
    {
        if (!User32.GetCursorPos(out POINT at))
        {
            return;
        }

        int dx = at.X - _resizeFrom.X;
        int dy = at.Y - _resizeFrom.Y;
        int left = _rectFrom.X;
        int top = _rectFrom.Y;
        int right = _rectFrom.X + _rectFrom.Width;
        int bottom = _rectFrom.Y + _rectFrom.Height;
        left += (_resizing & Edges.Left) != 0 ? dx : 0;
        right += (_resizing & Edges.Right) != 0 ? dx : 0;
        top += (_resizing & Edges.Top) != 0 ? dy : 0;
        bottom += (_resizing & Edges.Bottom) != 0 ? dy : 0;

        double scale = ScaleOf(Hwnd);
        int pad = 2 * (int)Math.Round(PadDip * scale);
        int minWidth = (int)Math.Round(WidgetStore.MinWidth * scale) + pad;
        int maxWidth = (int)Math.Round(WidgetStore.MaxWidth * scale) + pad;
        int minHeight = (int)Math.Round((ChromeDip + WidgetStore.MinHeight) * scale) + pad;
        int maxHeight = (int)Math.Round((ChromeDip + WidgetStore.MaxHeight) * scale) + pad;
        Fit(ref left, ref right, minWidth, maxWidth, (_resizing & Edges.Left) != 0);
        Fit(ref top, ref bottom, minHeight, maxHeight, (_resizing & Edges.Top) != 0);
        AppWindow.MoveAndResize(new RectInt32(left, top, right - left, bottom - top));
    }

    /// <summary>One direction's span kept within its limits: the dragged side gives way, the other stays.</summary>
    private static void Fit(ref int start, ref int end, int min, int max, bool startDragged)
    {
        int size = Math.Clamp(end - start, min, Math.Max(min, max));
        if (startDragged)
        {
            start = end - size;
        }
        else
        {
            end = start + size;
        }
    }

    /// <summary>Let go: its size is kept (DIP), and its spot (kept inside its screen's free area).</summary>
    private void EndResize()
    {
        if (_resizing == Edges.None)
        {
            return;
        }

        _resizing = Edges.None;
        double scale = ScaleOf(Hwnd);
        SizeInt32 size = AppWindow.Size;
        int width = (int)Math.Round((size.Width / scale) - (2 * PadDip));
        int height = (int)Math.Round((size.Height / scale) - (2 * PadDip) - ChromeDip);
        Host.WidgetItems.SetSize(Item.Id, width, height);
        KeepOnScreen();
        Log.Info("Desktop widget (" + Item.Kind + "): resized to " + width.ToString(CultureInfo.InvariantCulture) + " x " + height.ToString(CultureInfo.InvariantCulture) + " DIP.");
    }

    /// <summary>A double-click on an edge or corner: the standard size again, its top-left corner where it was.</summary>
    private void ResetSize()
    {
        _resizing = Edges.None;
        Host.WidgetItems.SetSize(Item.Id, null, null);
        (int width, int height) = SizeFor(ScaleOf(Hwnd));
        AppWindow.Resize(new SizeInt32(width, height));
        KeepOnScreen();
        Log.Info("Desktop widget (" + Item.Kind + "): back to the standard size.");
    }

    /// <summary>Kept inside its screen's free area, and where it is is saved.</summary>
    private void KeepOnScreen()
    {
        PointInt32 at = AppWindow.Position;
        SizeInt32 size = AppWindow.Size;
        int x = at.X;
        int y = at.Y;
        if (User32.TryGetWorkArea(Hwnd, out RECT area))
        {
            x = Math.Clamp(x, area.Left, Math.Max(area.Left, area.Right - size.Width));
            y = Math.Clamp(y, area.Top, Math.Max(area.Top, area.Bottom - size.Height));
            if (x != at.X || y != at.Y)
            {
                AppWindow.Move(new PointInt32(x, y));
            }
        }

        Host.WidgetItems.SetPosition(Item.Id, x, y);
    }
}
