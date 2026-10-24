using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Machitan.Native;

namespace Machitan;

/// <summary>
/// The small pop-up behind the tray chevron: a grid of the apps' notification
/// icons, each with its tooltip. Clicks go to the owning app
/// (see <see cref="TrayClicks"/>):
///   left click / Enter / Space  - the app's main action; the pop-up closes.
///   right click / Menu key      - the app's context menu; the pop-up stays open.
/// Each icon is a real button, so it can be reached with Tab, has a name for
/// screen readers, and shows a focus mark.
/// The content is built fresh each time the pop-up opens.
/// </summary>
internal static class TrayFlyout
{
    private const int Columns = 5;
    private const double CellSize = 40;
    private const double IconSize = 16; // tray icons are 16x16: draw them 1:1 so they stay sharp

    // How long the pop-up refuses to close after a right click: the app's menu
    // takes the keyboard focus, which would otherwise dismiss the pop-up.
    private static readonly TimeSpan KeepOpenAfterRightClick = TimeSpan.FromSeconds(1.5);

    private sealed class PopupState
    {
        public DateTime KeepOpenUntil = DateTime.MinValue;
    }

    // Which mouse button went down on a cell and has not come up yet. A release
    // without a matching press on the same cell (the press started on the
    // pop-up background) is not a click.
    private sealed class CellState
    {
        public TrayMouse? Pressed;
    }

    public static void Show(FrameworkElement anchor, DockEdge edge)
    {
        var icons = TrayIconStore.GetVisible();
        var queue = DispatcherQueue.GetForCurrentThread();
        var state = new PopupState();

        var root = new StackPanel
        {
            Spacing = 4,
            RequestedTheme = anchor.ActualTheme,
        };

        if (icons.Count == 0)
        {
            root.Children.Add(new TextBlock
            {
                Text = "No tray icons",
                Margin = new Thickness(8),
                Opacity = 0.7,
            });
        }

        // The cells need the flyout (to close it after a click), and the flyout
        // needs the cells: create it first, fill its content after.
        var flyout = new Flyout
        {
            Content = root,
            Placement = edge == DockEdge.Bottom ? FlyoutPlacementMode.Top : FlyoutPlacementMode.Bottom,

            // A flyout stays inside its window by default, and the bar window is
            // only as tall as the bar, so the pop-up was squeezed over the bar.
            // False = it gets its own window and opens above/below the bar
            // (Windows App SDK 1.4+; must be set before the first ShowAt).
            ShouldConstrainToRootBounds = false,
        };

        // Stay open for a moment after a right click (see KeepOpenAfterRightClick).
        flyout.Closing += (_, args) =>
        {
            if (DateTime.UtcNow < state.KeepOpenUntil)
                args.Cancel = true;
        };

        StackPanel? row = null;
        for (int i = 0; i < icons.Count; i++)
        {
            if (i % Columns == 0)
            {
                row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                root.Children.Add(row);
            }

            row!.Children.Add(BuildCell(icons[i], flyout, queue, state));
        }

        flyout.ShowAt(anchor);
    }

    private static FrameworkElement BuildCell(TrayIconEntry entry, Flyout flyout, DispatcherQueue queue, PopupState state)
    {
        string label = string.IsNullOrWhiteSpace(entry.Tip) ? entry.Owner : entry.Tip;

        // Screen readers: a short name, and the full tooltip as the description.
        string accessibleName = label.Length <= 48 ? label : entry.Owner;
        var cellState = new CellState();

        var cell = new Button
        {
            Width = CellSize,
            Height = CellSize,
            Padding = new Thickness(0),
            BorderThickness = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            // Transparent, not null: the button still takes part in hit-testing,
            // and the standard theme hover / pressed colors still apply.
            Background = new SolidColorBrush(Colors.Transparent),
            Content = new Image
            {
                Width = IconSize,
                Height = IconSize,
                Stretch = Stretch.Uniform,
                Source = entry.GetImage(),
            },
        };

        ToolTipService.SetToolTip(cell, label);
        AutomationProperties.SetName(cell, accessibleName);
        if (!string.Equals(accessibleName, label, StringComparison.Ordinal))
            AutomationProperties.SetHelpText(cell, label);

        // A Button handles pointer events itself and marks them handled, so ask
        // to hear them anyway. (A Button also captures the pointer, so a press
        // and its release always reach the same icon.)
        cell.AddHandler(UIElement.PointerPressedEvent,
            new PointerEventHandler((_, e) => OnPointerPressed(entry, cell, cellState, e)), true);
        cell.AddHandler(UIElement.PointerReleasedEvent,
            new PointerEventHandler((_, e) => OnPointerReleased(entry, cell, cellState, flyout, queue, state, e)), true);

        // Keyboard: Enter or Space = left click.
        cell.KeyDown += (_, e) =>
        {
            if (e.Key != Windows.System.VirtualKey.Enter && e.Key != Windows.System.VirtualKey.Space)
                return;

            // Holding the key repeats KeyDown: act on the first one only.
            if (e.KeyStatus.WasKeyDown)
            {
                e.Handled = true;
                return;
            }

            e.Handled = true;
            TrayClicks.MouseDown(entry, TrayMouse.Left);
            FinishClick(entry, TrayMouse.Left, true, flyout, queue, state);
        };

        // Keyboard: Menu key or Shift+F10 = right click. A mouse right click
        // raises this event too, but the pointer handlers already did the work.
        cell.ContextRequested += (_, e) =>
        {
            if (e.TryGetPosition(cell, out var mousePosition))
                return;

            e.Handled = true;
            TrayClicks.MouseDown(entry, TrayMouse.Right);
            FinishClick(entry, TrayMouse.Right, true, flyout, queue, state);
        };

        return cell;
    }

    private static void OnPointerPressed(TrayIconEntry entry, Button cell, CellState cellState, PointerRoutedEventArgs e)
    {
        TrayMouse? button = ButtonOf(e.GetCurrentPoint(cell).Properties.PointerUpdateKind, pressed: true);
        if (button == null)
            return;

        cellState.Pressed = button;
        TrayClicks.MouseDown(entry, button.Value);
    }

    private static void OnPointerReleased(TrayIconEntry entry, Button cell, CellState cellState, Flyout flyout,
        DispatcherQueue queue, PopupState state, PointerRoutedEventArgs e)
    {
        PointerPoint point = e.GetCurrentPoint(cell);
        TrayMouse? button = ButtonOf(point.Properties.PointerUpdateKind, pressed: false);
        if (button == null)
            return;

        // No matching press on this cell: the press began elsewhere, so this is not our click.
        if (cellState.Pressed != button)
            return;
        cellState.Pressed = null;

        // Released away from the icon: the app still gets its "up", but it is not a click.
        bool inside = point.Position.X >= 0 && point.Position.Y >= 0
            && point.Position.X <= cell.ActualWidth && point.Position.Y <= cell.ActualHeight;

        FinishClick(entry, button.Value, inside, flyout, queue, state);
    }

    private static void FinishClick(TrayIconEntry entry, TrayMouse button, bool complete,
        Flyout flyout, DispatcherQueue queue, PopupState state)
    {
        if (complete)
        {
            if (button == TrayMouse.Right)
            {
                // The app is about to open its menu and take the focus: do not let that close us.
                state.KeepOpenUntil = DateTime.UtcNow + KeepOpenAfterRightClick;
            }
            else
            {
                // Close first so the app's window can come to the front.
                state.KeepOpenUntil = DateTime.MinValue;
                flyout.Hide();
            }
        }

        queue.TryEnqueue(() => TrayClicks.MouseUp(entry, button, complete));
    }

    private static TrayMouse? ButtonOf(PointerUpdateKind kind, bool pressed) => kind switch
    {
        PointerUpdateKind.LeftButtonPressed when pressed => TrayMouse.Left,
        PointerUpdateKind.LeftButtonReleased when !pressed => TrayMouse.Left,
        PointerUpdateKind.RightButtonPressed when pressed => TrayMouse.Right,
        PointerUpdateKind.RightButtonReleased when !pressed => TrayMouse.Right,
        PointerUpdateKind.MiddleButtonPressed when pressed => TrayMouse.Middle,
        PointerUpdateKind.MiddleButtonReleased when !pressed => TrayMouse.Middle,
        _ => null,
    };
}
