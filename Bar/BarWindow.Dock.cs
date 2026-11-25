using System;
using System.Collections.Generic;
using Machitan.Config;
using Machitan.Core;
using Machitan.Custom;
using Machitan.Interop;
using Machitan.Shell;
using Machitan.Visuals;
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
/// The app buttons, in their bubbles (the apps bubble and app groups; see BarLayoutStore):
/// apps placed by dragging in their places, then pinned apps (in pin order), then other
/// running apps. Any app, pinned or not, can be dragged to another place or bubble
/// (BarWindow.Drag.cs). Ctrl+click selects apps; right-click > Group apps puts them in a
/// new bubble.
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
    private const double SelectionRingDip = 2;

    // Motion (Visuals/Motion.cs): a new app grows into its place, and the indicator under an
    // app stretches when it appears or changes width (running <-> in front).
    private const float PopInFromScale = 0.5f;
    private const int PopInMs = 300;
    private const float IndicatorAppearFrom = 0.1f;
    private const int IndicatorMs = 250;
    private const double LabelMaxWidthDip = 120;
    private const double LabelPadLeftDip = 10;
    private const double LabelPadRightDip = 12;

    /// <summary>Apps picked with Ctrl+click, by the name the bar layout keeps them under.</summary>
    private readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What a dock button is made of, kept on the button so it can be updated in place.</summary>
    private sealed class DockButtonParts
    {
        internal DockButtonParts(DockItem item, Grid layout, Border indicator, Border selection)
        {
            Item = item;
            Layout = layout;
            Indicator = indicator;
            Selection = selection;
        }

        internal DockItem Item { get; set; }

        internal Grid Layout { get; }

        internal Border Indicator { get; }

        /// <summary>The accent ring shown while the app is picked with Ctrl+click.</summary>
        internal Border Selection { get; }

        /// <summary>The app's name beside its icon (the "Icons with labels" variant), or null.</summary>
        internal TextBlock? Label { get; set; }

        internal ImageSource? IconSource { get; set; }
    }

    /// <summary>
    /// Makes the bubbles match pins, running apps and the bar layout. When a bubble shows up or
    /// goes (or the order changed), the whole bar is built again; otherwise each bubble's
    /// buttons are matched in place, so hover and focus are not disturbed.
    /// </summary>
    private void SyncDock(bool rebuild)
    {
        try
        {
            Dictionary<string, List<DockItem>> arranged = ArrangeDock();
            List<string> shown = ShownBubbles(arranged);
            if (!SameIds(shown, _builtIds) || !SameIds(TopBubbles(arranged), _builtTopIds))
            {
                BuildLayout();
                return;
            }

            FillBubbles(arranged, rebuild);
        }
        catch (Exception ex)
        {
            Log.Error("The dock buttons could not be updated.", ex);
        }
    }

    private static bool SameIds(List<string> a, List<string> b)
    {
        if (a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (!string.Equals(a[i], b[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>Puts each bubble's apps in its panel (in place when they are the same, in the same order).</summary>
    private void FillBubbles(Dictionary<string, List<DockItem>> arranged, bool rebuild)
    {
        // The apps already on the bar (in any bubble), so only really new ones grow in. A
        // rebuild (new settings, a new layout) shows everything at once.
        HashSet<string>? shown = null;
        if (!rebuild && Motion.IsOn(_host.Settings.Current))
        {
            shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Button button in DockButtons())
            {
                if (button.Tag is DockButtonParts parts)
                {
                    shown.Add(parts.Item.Key);
                    if (parts.Item.PinKey is string pinKey)
                    {
                        shown.Add(pinKey);
                    }
                }
            }
        }

        foreach (AppBubble bubble in _bubbles)
        {
            List<DockItem> items = arranged.TryGetValue(bubble.Id, out List<DockItem>? list) ? list : new List<DockItem>();
            if (bubble.PinnedPanel is StackPanel pinnedPanel)
            {
                // Split: the pinned apps on top (running or not), the open apps below (pinned or
                // not); a pinned app that is open shows in both.
                FillPanel(pinnedPanel, items.FindAll(item => item.IsPinned), rebuild, shown, pinnedRow: true);
                FillPanel(bubble.Panel, items.FindAll(item => item.IsRunning), rebuild, shown, pinnedRow: false);
            }
            else
            {
                FillPanel(bubble.Panel, items, rebuild, shown, pinnedRow: false);
            }
        }

        // The tray icons' order is kept in the same layout file.
        FillTrayBubble();
        UpdateDockPresence();
    }

    /// <summary>
    /// Puts the items' buttons in the panel, reusing them when nothing moved. Buttons for keys
    /// not in <paramref name="shown"/> (when given) grow into place. A split bar's pinned row
    /// (<paramref name="pinnedRow"/>) matches its buttons by pin, so a pinned app that starts or
    /// closes keeps its button there (only its running mark changes).
    /// </summary>
    private void FillPanel(StackPanel panel, List<DockItem> items, bool rebuild, HashSet<string>? shown, bool pinnedRow)
    {
        if (!rebuild && panel.Children.Count == items.Count)
        {
            bool sameItems = true;
            for (int i = 0; i < items.Count; i++)
            {
                if (panel.Children[i] is not Button { Tag: DockButtonParts parts }
                    || !string.Equals(MatchKey(parts.Item, pinnedRow), MatchKey(items[i], pinnedRow), StringComparison.OrdinalIgnoreCase))
                {
                    sameItems = false;
                    break;
                }
            }

            if (sameItems)
            {
                for (int i = 0; i < items.Count; i++)
                {
                    if (panel.Children[i] is Button { Tag: DockButtonParts parts } button)
                    {
                        UpdateDockButton(button, parts, items[i]);
                    }
                }

                return;
            }
        }

        panel.Children.Clear();
        foreach (DockItem item in items)
        {
            Button button = CreateDockButton(item);
            panel.Children.Add(button);
            if (shown is not null && !shown.Contains(MatchKey(item, pinnedRow)))
            {
                Motion.PopIn(button, PopInFromScale, PopInMs);
            }
        }
    }

    /// <summary>What a button stands for in its row: the app, or (a split bar's pinned row) the pin.</summary>
    private static string MatchKey(DockItem item, bool pinnedRow)
    {
        return pinnedRow && item.PinKey is string pinKey ? pinKey : item.Key;
    }

    /// <summary>Every app button on the bar, from the left (split: each bubble's pinned row, then its open row).</summary>
    private IEnumerable<Button> DockButtons()
    {
        foreach (AppBubble bubble in _bubbles)
        {
            if (bubble.PinnedPanel is StackPanel pinnedPanel)
            {
                foreach (UIElement child in pinnedPanel.Children)
                {
                    if (child is Button { Tag: DockButtonParts } pinnedButton)
                    {
                        yield return pinnedButton;
                    }
                }
            }

            foreach (UIElement child in bubble.Panel.Children)
            {
                if (child is Button { Tag: DockButtonParts } button)
                {
                    yield return button;
                }
            }
        }
    }

    /// <summary>True when <paramref name="panel"/> is a split bar's pinned row.</summary>
    private bool IsPinnedRow(StackPanel? panel)
    {
        if (panel is null)
        {
            return false;
        }

        foreach (AppBubble bubble in _bubbles)
        {
            if (ReferenceEquals(bubble.PinnedPanel, panel))
            {
                return true;
            }
        }

        return false;
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

        bool labels = settings.DockLabels;
        var layout = new Grid
        {
            Width = labels ? double.NaN : buttonWidth,
            MinWidth = buttonWidth,
            Height = buttonHeight,
        };

        // On top of the icon, see-through: an accent ring with a faint tint.
        var selection = new Border
        {
            CornerRadius = new CornerRadius(ButtonRadius(buttonHeight, buttonWidth)),
            BorderThickness = new Thickness(SelectionRingDip),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };

        var parts = new DockButtonParts(item, layout, indicator, selection);
        layout.Children.Add(CreateIconVisual(item, parts));
        layout.Children.Add(indicator);
        layout.Children.Add(selection);

        if (labels)
        {
            // Icon, then the name; the indicator and the ring span both.
            layout.Padding = new Thickness(LabelPadLeftDip, 0, LabelPadRightDip, 0);
            layout.ColumnSpacing = 8;
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var label = new TextBlock
            {
                Text = item.DisplayName,
                FontSize = 12,
                MaxWidth = LabelMaxWidthDip,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                IsHitTestVisible = false,
            };
            Grid.SetColumn(label, 1);
            Grid.SetColumnSpan(indicator, 2);
            Grid.SetColumnSpan(selection, 2);
            selection.Margin = new Thickness(-LabelPadLeftDip, 0, -LabelPadRightDip, 0);
            layout.Children.Add(label);
            parts.Label = label;
        }

        // Pressed, only the icon shrinks (not its name, indicator or ring).
        Button button = MakeBarButton(
            layout,
            item.DisplayName,
            () => parts.Layout.Children.Count > 0 ? parts.Layout.Children[0] : null);
        button.Height = buttonHeight;
        if (labels)
        {
            button.Width = double.NaN;
            button.MinWidth = buttonWidth;
        }

        button.Tag = parts;
        button.Click += OnDockButtonClick;
        button.ContextFlyout = CreateDockMenu(parts);

        // Window previews. The button handles some pointer events itself, so listen to all.
        button.AddHandler(UIElement.PointerEnteredEvent, new PointerEventHandler(OnDockButtonPointerEntered), true);
        button.AddHandler(UIElement.PointerExitedEvent, new PointerEventHandler(OnDockButtonPointerExited), true);
        AttachDrag(button, DragKind.App);

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
        if (parts.Label is TextBlock label && label.Text != item.DisplayName)
        {
            label.Text = item.DisplayName;
        }

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

        // What the indicator was, for the stretch below. A button just made has no width yet.
        double oldWidth = parts.Indicator.Visibility == Visibility.Visible ? parts.Indicator.Width : 0;

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

            StretchIndicator(parts.Indicator, oldWidth);
        }

        bool selected = IsSelected(item);
        if (selected)
        {
            Windows.UI.Color accent = ReadAccentColor();
            parts.Selection.BorderBrush = new SolidColorBrush(accent);
            parts.Selection.Background = new SolidColorBrush(ColorHelper.FromArgb(40, accent.R, accent.G, accent.B));
        }

        parts.Selection.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;

        string name = item.DisplayName;
        if (item.IsPinned)
        {
            name += ", " + Strings.DockPinned;
        }

        if (selected)
        {
            name += ", " + Strings.DockSelected;
        }

        if (group is null)
        {
            name += ", " + Strings.DockNotRunning;
        }
        else
        {
            if (group.Windows.Count > 1)
            {
                name += ", " + Fmt.F(Strings.DockWindows, group.Windows.Count);
            }

            if (group.IsFocused)
            {
                name += ", " + Strings.DockFocused;
            }
        }

        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, item.DisplayName);
    }

    /// <summary>The indicator stretches from its old width (0 when it was hidden) to its new one.</summary>
    private void StretchIndicator(Border indicator, double oldWidth)
    {
        double newWidth = indicator.Width;
        if (double.IsNaN(oldWidth) || double.IsNaN(newWidth) || newWidth <= 0
            || Math.Abs(oldWidth - newWidth) < 0.5 || !Motion.IsOn(_host.Settings.Current))
        {
            return;
        }

        float from = oldWidth <= 0 ? IndicatorAppearFrom : (float)(oldWidth / newWidth);
        Motion.StretchX(indicator, from, IndicatorMs);
    }

    private void OnDockButtonClick(object sender, RoutedEventArgs e)
    {
        _host.HidePreview();
        if (sender is not Button { Tag: DockButtonParts parts } clicked)
        {
            return;
        }

        DockItem item = parts.Item;
        if (_host.Custom.IsOn)
        {
            // Custom mode: a click picks the app's bubble instead of opening the app.
            _host.Custom.Select(TargetOf(_host.BarLayout.BubbleOf(item)));
            return;
        }

        if ((User32.GetAsyncKeyState(User32.VK_CONTROL) & 0x8000) != 0)
        {
            // Ctrl+click picks the app (for Group apps) instead of opening it.
            string key = BarLayoutStore.KeyOf(item);
            if (!_selected.Remove(key))
            {
                _selected.Add(key);
            }

            ApplyDockState(clicked, parts);

            // A split bar shows a pinned app that is open in both rows: both show the pick.
            foreach (Button other in DockButtons())
            {
                if (!ReferenceEquals(other, clicked) && other.Tag is DockButtonParts otherParts
                    && string.Equals(otherParts.Item.Key, item.Key, StringComparison.OrdinalIgnoreCase))
                {
                    ApplyDockState(other, otherParts);
                }
            }

            return;
        }

        ClearSelection();
        if (item.Group is TaskGroup group)
        {
            _host.Tasks.ActivateOrMinimize(group.Key);
        }
        else if (item.LaunchTarget is string target)
        {
            Launcher.Start(target);
        }

        // Like Windows' own taskbar: picking an app on the bar closes Start. Closed after the
        // app is switched to, so the app Start covered is not taken for the one in front (and
        // minimised) when Start goes away first.
        _host.StartMenu?.HideMenu();
    }

    /// <summary>
    /// Right-click menu: pin or unpin (one pin list serves the bar and the Start menu), close.
    /// Built when it opens, from the button's current item.
    /// </summary>
    private void OnDockButtonPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button { Tag: DockButtonParts parts } button && !IsAutoHidden && !IsDragging && button.XamlRoot is not null)
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

            if (_host.Custom.IsOn)
            {
                // Custom mode: the black menu for the app's bubble.
                CustomMenus.Fill(menu, _host.Custom, TargetOf(_host.BarLayout.BubbleOf(item)));
                NoteMenuOpening();
                return;
            }

            CustomMenus.MakePlain(menu);

            if (item.PinKey is string pinKey)
            {
                var unpin = new MenuFlyoutItem { Text = Strings.UnpinFromTaskbar };
                unpin.Click += (_, _) => _host.Pins.Unpin(pinKey);
                menu.Items.Add(unpin);
            }
            else if (item.CanPinByPath && item.Path is string path)
            {
                var pin = new MenuFlyoutItem { Text = Strings.PinToTaskbar };
                pin.Click += (_, _) => _host.Pins.Pin(path, item.DisplayName);
                menu.Items.Add(pin);
            }

            if (item.Group is TaskGroup group)
            {
                var close = new MenuFlyoutItem { Text = group.Windows.Count > 1 ? Strings.CloseAllWindows : Strings.CloseWindow };
                close.Click += (_, _) => _host.Tasks.CloseWindows(group);
                menu.Items.Add(close);

                // Ends the app's processes at once (for an app that does not respond to Close).
                var end = new MenuFlyoutItem { Text = Strings.EndTask };
                end.Click += (_, _) => _host.Tasks.EndTask(group);
                menu.Items.Add(end);
            }

            AddGroupItems(menu, item);

            if (menu.Items.Count == 0)
            {
                menu.Items.Add(new MenuFlyoutItem { Text = Strings.NoActions, IsEnabled = false });
            }

            NoteMenuOpening();
        };
        menu.Closed += OnMenuClosed;

        return menu;
    }

    // ---------------------------------------------------------------- picking and grouping

    private bool IsSelected(DockItem item)
    {
        return _selected.Contains(BarLayoutStore.KeyOf(item)) || _selected.Contains(item.Key);
    }

    /// <summary>Drops the Ctrl+click picks and their rings.</summary>
    private void ClearSelection()
    {
        if (_selected.Count == 0)
        {
            return;
        }

        _selected.Clear();
        foreach (Button button in DockButtons())
        {
            if (button.Tag is DockButtonParts parts)
            {
                ApplyDockState(button, parts);
            }
        }
    }

    /// <summary>The picked apps shown on the bar, plus <paramref name="also"/>, from the left.</summary>
    private List<DockItem> PickedWith(DockItem also)
    {
        var picked = new List<DockItem>();
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Button button in DockButtons())
        {
            // A split bar shows a pinned app that is open twice; it is picked once.
            if (button.Tag is DockButtonParts parts
                && (IsSelected(parts.Item) || string.Equals(parts.Item.Key, also.Key, StringComparison.OrdinalIgnoreCase))
                && keys.Add(parts.Item.Key))
            {
                picked.Add(parts.Item);
            }
        }

        return picked;
    }

    /// <summary>
    /// The menu's grouping part: Group apps (the Ctrl+click picks and this app), or Move to a new
    /// group (this app alone); Ungroup for an app in a group; Clear selection while apps are picked.
    /// </summary>
    private void AddGroupItems(MenuFlyout menu, DockItem item)
    {
        string bubble = _host.BarLayout.BubbleOf(item);
        bool inGroup = BarLayoutStore.IsGroup(bubble);
        List<DockItem> picked = PickedWith(item);
        var entries = new List<MenuFlyoutItemBase>();

        if (picked.Count >= 2)
        {
            var group = new MenuFlyoutItem { Text = Fmt.F(Strings.GroupApps, picked.Count) };
            group.Click += (_, _) => GroupApps(picked, bubble);
            entries.Add(group);
        }
        else if (!(inGroup && CountIn(bubble) <= 1))
        {
            var alone = new MenuFlyoutItem { Text = Strings.MoveToNewGroup };
            alone.Click += (_, _) => GroupApps(new List<DockItem> { item }, bubble);
            entries.Add(alone);
        }

        if (inGroup)
        {
            var ungroup = new MenuFlyoutItem { Text = Strings.Ungroup };
            ungroup.Click += (_, _) =>
            {
                ClearSelection();
                _host.BarLayout.Ungroup(bubble);
                Log.Info("Bar: a group was ended; its apps went back to the apps bubble.");
            };
            entries.Add(ungroup);
        }

        if (_selected.Count > 0)
        {
            var clear = new MenuFlyoutItem { Text = Strings.ClearSelection };
            clear.Click += (_, _) => ClearSelection();
            entries.Add(clear);
        }

        if (entries.Count == 0)
        {
            return;
        }

        if (menu.Items.Count > 0)
        {
            menu.Items.Add(new MenuFlyoutSeparator());
        }

        foreach (MenuFlyoutItemBase entry in entries)
        {
            menu.Items.Add(entry);
        }
    }

    private int CountIn(string bubbleId)
    {
        if (BubbleById(bubbleId) is not AppBubble bubble)
        {
            return 0;
        }

        if (bubble.PinnedPanel is not StackPanel pinnedPanel)
        {
            return bubble.Panel.Children.Count;
        }

        // A split bar: each app once, whichever rows it shows in.
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (StackPanel row in new[] { pinnedPanel, bubble.Panel })
        {
            foreach (UIElement child in row.Children)
            {
                if (child is Button { Tag: DockButtonParts parts })
                {
                    keys.Add(parts.Item.Key);
                }
            }
        }

        return keys.Count;
    }

    private void GroupApps(List<DockItem> apps, string after)
    {
        _selected.Clear();
        string id = _host.BarLayout.MakeGroup(apps, after);
        Log.Info("Bar: " + apps.Count + " app(s) moved to a new bubble (" + id + ").");
    }

    /// <summary>Icon size on screen in physical pixels.</summary>
    private int IconPixelSize()
    {
        return (int)Math.Ceiling(_host.Settings.Current.IconSize * Scale());
    }
}
