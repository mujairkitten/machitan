using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Machitan.Native;

namespace Machitan;

/// <summary>
/// The small pop-up behind the tray chevron: a grid of the apps' notification
/// icons, each with its tooltip. i8 only shows them; clicks arrive in i9.
/// The content is built fresh each time the pop-up opens.
/// </summary>
internal static class TrayFlyout
{
    private const int Columns = 5;
    private const double CellSize = 40;
    private const double IconSize = 20;

    public static void Show(FrameworkElement anchor, DockEdge edge)
    {
        var icons = TrayIconStore.GetVisible();

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

        StackPanel? row = null;
        for (int i = 0; i < icons.Count; i++)
        {
            if (i % Columns == 0)
            {
                row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
                root.Children.Add(row);
            }

            row!.Children.Add(BuildCell(icons[i]));
        }

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
        flyout.ShowAt(anchor);
    }

    private static FrameworkElement BuildCell(TrayIconEntry entry)
    {
        var image = new Image
        {
            Width = IconSize,
            Height = IconSize,
            Stretch = Stretch.Uniform,
            Source = entry.GetImage(),
        };

        var cell = new Border
        {
            Width = CellSize,
            Height = CellSize,
            CornerRadius = new CornerRadius(8),
            // A transparent (not null) brush, so the cell takes part in hit-testing and shows its tooltip.
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            Child = image,
        };

        ToolTipService.SetToolTip(cell, string.IsNullOrWhiteSpace(entry.Tip) ? entry.Owner : entry.Tip);
        return cell;
    }
}