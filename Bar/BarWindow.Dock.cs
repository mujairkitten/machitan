using System;
using System.Collections.Generic;
using Machitan.Config;
using Machitan.Core;
using Machitan.Interop;
using Machitan.Shell;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.Graphics;

namespace Machitan.Bar;

/// <summary>
/// The app dock: pinned apps first (in pin order), then other running apps.
///
/// Each button per the spec: a bare icon at the exact centre (padding zeroed), no tile behind
/// it (a background only on hover or keyboard focus), and the running indicator placed from
/// the centre: its top edge is half the icon + 4 DIP below the centre. 3 DIP tall, rounded;
/// 14 DIP in accent for the focused app, 6 DIP translucent for running apps, none when the
/// app is pinned but not running.
/// </summary>
public sealed partial class BarWindow
{
    private const double IndicatorHeightDip = 3;
    private const double IndicatorGapDip = 4;
    private const double FocusedIndicatorWidthDip = 14;
    private const double RunningIndicatorWidthDip = 6;

    /// <summary>What a dock button is made of, kept on the button so it can be updated in place.</summary>
    private sealed class DockButtonParts
    {
        internal DockButtonParts(DockItem item, Grid layout, Border indicator)
        {
            Item = item;
            Layout = layout;
            Indicator = indicator;
        }

        internal DockItem Item { get; set; }

        internal Grid Layout { get; }

        internal Border Indicator { get; }

        internal ImageSource? IconSource { get; set; }
    }

    /// <summary>
    /// Makes the dock match pins and running apps. When the same items are shown in the same
    /// order, the existing buttons are only updated, so hover and focus are not disturbed.
    /// </summary>
    private void SyncDock(bool rebuild)
    {
        try
        {
            List<DockItem> items = DockModel.Build(_host.Pins.Pins, _host.Tasks.Groups);

            if (!rebuild && _dockPanel.Children.Count == items.Count)
            {
                bool sameItems = true;
                for (int i = 0; i < items.Count; i++)
                {
                    if (_dockPanel.Children[i] is not Button { Tag: DockButtonParts parts }
                        || !string.Equals(parts.Item.Key, items[i].Key, StringComparison.OrdinalIgnoreCase))
                    {
                        sameItems = false;
                        break;
                    }
                }

                if (sameItems)
                {
                    for (int i = 0; i < items.Count; i++)
                    {
                        if (_dockPanel.Children[i] is Button { Tag: DockButtonParts parts } button)
                        {
                            UpdateDockButton(button, parts, items[i]);
                        }
                    }

                    return;
                }
            }

            _dockPanel.Children.Clear();
            foreach (DockItem item in items)
            {
                _dockPanel.Children.Add(CreateDockButton(item));
            }
        }
        catch (Exception ex)
        {
            Log.Error("The dock buttons could not be updated.", ex);
        }
    }

    private Button CreateDockButton(DockItem item)
    {
        ShellSettings settings = _host.Settings.Current;
        double iconSize = settings.IconSize;
        double buttonHeight = Math.Max(PillHeightDip() - 8, iconSize + 14);
        double buttonWidth = iconSize + 20;

        var indicator = new Border
        {
            Height = IndicatorHeightDip,
            CornerRadius = new CornerRadius(IndicatorHeightDip / 2),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
            RenderTransform = new TranslateTransform
            {
                // Centre of the indicator = centre + half icon + gap + half indicator.
                Y = (iconSize / 2) + IndicatorGapDip + (IndicatorHeightDip / 2),
            },
        };

        var layout = new Grid
        {
            Width = buttonWidth,
            Height = buttonHeight,
        };

        var parts = new DockButtonParts(item, layout, indicator);
        layout.Children.Add(CreateIconVisual(item, parts));
        layout.Children.Add(indicator);

        Button button = MakeBarButton(layout, item.DisplayName);
        button.Height = buttonHeight;
        button.Tag = parts;
        button.Click += OnDockButtonClick;
        button.ContextFlyout = CreateDockMenu(parts);

        // Window previews. The button handles some pointer events itself, so listen to all.
        button.AddHandler(UIElement.PointerEnteredEvent, new PointerEventHandler(OnDockButtonPointerEntered), true);
        button.AddHandler(UIElement.PointerExitedEvent, new PointerEventHandler(OnDockButtonPointerExited), true);

        ApplyDockState(button, parts);
        return button;
    }

    /// <summary>The app's real icon, or - when Windows has none to give - its first letter.</summary>
    private FrameworkElement CreateIconVisual(DockItem item, DockButtonParts parts)
    {
        double iconSize = _host.Settings.Current.IconSize;

        ImageSource? icon = _host.Icons.GetIcon(item, IconPixelSize());
        parts.IconSource = icon;

        if (icon is not null)
        {
            return new Image
            {
                Source = icon,
                Width = iconSize,
                Height = iconSize,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
        }

        string letter = item.DisplayName.Length > 0
            ? item.DisplayName.Substring(0, 1).ToUpperInvariant()
            : "?";

        return new TextBlock
        {
            Text = letter,
            FontSize = Math.Max(iconSize * 0.62, 10),
            FontWeight = FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private void UpdateDockButton(Button button, DockButtonParts parts, DockItem item)
    {
        parts.Item = item;

        // A better icon may have arrived (a store-app logo, or one that was missing earlier).
        ImageSource? icon = _host.Icons.GetIcon(item, IconPixelSize());
        if (!ReferenceEquals(icon, parts.IconSource) && parts.Layout.Children.Count > 0)
        {
            parts.Layout.Children[0] = CreateIconVisual(item, parts);
        }

        ApplyDockState(button, parts);
    }

    private void ApplyDockState(Button button, DockButtonParts parts)
    {
        DockItem item = parts.Item;
        TaskGroup? group = item.Group;

        if (group is null)
        {
            parts.Indicator.Visibility = Visibility.Collapsed;
        }
        else
        {
            parts.Indicator.Visibility = Visibility.Visible;
            if (group.IsFocused)
            {
                parts.Indicator.Width = FocusedIndicatorWidthDip;
                parts.Indicator.Background = new SolidColorBrush(ReadAccentColor());
            }
            else
            {
                parts.Indicator.Width = RunningIndicatorWidthDip;
                parts.Indicator.Background = new SolidColorBrush(IsDarkTheme()
                    ? ColorHelper.FromArgb(115, 255, 255, 255)
                    : ColorHelper.FromArgb(115, 0, 0, 0));
            }
        }

        string name = item.DisplayName;
        if (item.IsPinned)
        {
            name += ", pinned";
        }

        if (group is null)
        {
            name += ", not running";
        }
        else
        {
            if (group.Windows.Count > 1)
            {
                name += ", " + group.Windows.Count + " windows";
            }

            if (group.IsFocused)
            {
                name += ", focused";
            }
        }

        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, item.DisplayName);
    }

    private void OnDockButtonClick(object sender, RoutedEventArgs e)
    {
        _host.HidePreview();
        if (sender is not Button { Tag: DockButtonParts parts })
        {
            return;
        }

        DockItem item = parts.Item;
        if (item.Group is TaskGroup group)
        {
            _host.Tasks.ActivateOrMinimize(group.Key);
        }
        else if (item.LaunchTarget is string target)
        {
            Launcher.Start(target);
        }
    }

    /// <summary>
    /// Right-click menu: pin or unpin (one pin list serves the bar and the Start menu), close.
    /// Built when it opens, from the button's current item.
    /// </summary>
    private void OnDockButtonPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button { Tag: DockButtonParts parts } button && !IsAutoHidden && button.XamlRoot is not null)
        {
            _host.HoverDockItem(parts.Item, ScreenRectOf(button));
        }
    }

    private void OnDockButtonPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _host.EndDockHover();
    }

    /// <summary>Where an element of the bar is on screen, in physical pixels (the bar window has no frame).</summary>
    private RECT ScreenRectOf(FrameworkElement element)
    {
        Point origin = element.TransformToVisual(null).TransformPoint(new Point(0, 0));
        double scale = element.XamlRoot?.RasterizationScale ?? 1.0;
        PointInt32 window = AppWindow.Position;
        int left = window.X + (int)Math.Round(origin.X * scale);
        int top = window.Y + (int)Math.Round(origin.Y * scale);
        return new RECT
        {
            Left = left,
            Top = top,
            Right = left + (int)Math.Round(element.ActualWidth * scale),
            Bottom = top + (int)Math.Round(element.ActualHeight * scale),
        };
    }

    private MenuFlyout CreateDockMenu(DockButtonParts parts)
    {
        var menu = new MenuFlyout { ShouldConstrainToRootBounds = false };

        menu.Opening += (sender, e) =>
        {
            _host.HidePreview();
            menu.Items.Clear();
            DockItem item = parts.Item;

            if (item.PinKey is string pinKey)
            {
                var unpin = new MenuFlyoutItem { Text = "Unpin" };
                unpin.Click += (_, _) => _host.Pins.Unpin(pinKey);
                menu.Items.Add(unpin);
            }
            else if (item.CanPinByPath && item.Path is string path)
            {
                var pin = new MenuFlyoutItem { Text = "Pin" };
                pin.Click += (_, _) => _host.Pins.Pin(path, item.DisplayName);
                menu.Items.Add(pin);
            }

            if (item.Group is TaskGroup group)
            {
                var close = new MenuFlyoutItem { Text = group.Windows.Count > 1 ? "Close all windows" : "Close window" };
                close.Click += (_, _) => _host.Tasks.CloseWindows(group);
                menu.Items.Add(close);
            }

            if (menu.Items.Count == 0)
            {
                menu.Items.Add(new MenuFlyoutItem { Text = "No actions for this app", IsEnabled = false });
            }

            OnMenuOpening(sender, e);
        };
        menu.Closed += OnMenuClosed;

        return menu;
    }

    /// <summary>Icon size on screen in physical pixels.</summary>
    private int IconPixelSize()
    {
        return (int)Math.Ceiling(_host.Settings.Current.IconSize * Scale());
    }
}
