using System;
using System.Collections.Generic;
using Machitan.Interop;
using Machitan.Shell;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using VirtualKey = Windows.System.VirtualKey;

namespace Machitan.Flyouts;

/// <summary>
/// Win+Tab and Win+`: every open window, most recently used first, in the middle of the
/// screen. The list is as tall as it needs to be, up to most of the screen, and scrolls
/// beyond that (no fixed-height cutoff). It starts on the window used before the current
/// one, so the shortcut then Enter flips between two windows.
///
/// Keys: the shortcut again, Tab, Down or Right move on; Shift+Tab, Up or Left move back;
/// Enter or a click switches; Esc or a click elsewhere closes.
/// </summary>
public sealed partial class SwitcherWindow : FlyoutWindow
{
    private const double WidthDip = 520;
    private const double InnerPadDip = 12;
    private const double HeaderHeightDip = 28;
    private const double RowHeightDip = 52;
    private const double IconDip = 24;
    private const double MaxScreenFraction = 0.7;

    private readonly ListView _list;
    private readonly TextBlock _header;
    private readonly List<nint> _handles = new();

    internal SwitcherWindow(ShellHost host)
        : base(host, "Switcher")
    {
        _header = new TextBlock
        {
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Height = HeaderHeightDip,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
        };

        _list = new ListView
        {
            SelectionMode = ListViewSelectionMode.Single,
            IsItemClickEnabled = true,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        AutomationProperties.SetName(_list, "Open windows");
        _list.ItemClick += OnItemClick;

        var body = new Grid { Padding = new Thickness(InnerPadDip) };
        body.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        body.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        body.Children.Add(_header);
        Grid.SetRow(_list, 1);
        body.Children.Add(_list);

        Root.Children.Add(MakeBox(body, BoxRadius));
    }

    /// <summary>The shortcut: open, or step to the next window when already open.</summary>
    internal void ShowOrAdvance()
    {
        if (IsOpen)
        {
            Move(+1);
            return;
        }

        Fill();
        int count = _handles.Count;
        double listHeight = Math.Max(1, count) * RowHeightDip;
        double maxList = ScreenHeightDip(MaxScreenFraction) - HeaderHeightDip - (2 * InnerPadDip) - (2 * PadDip);
        listHeight = Math.Min(listHeight, Math.Max(RowHeightDip, maxList));
        _list.Height = listHeight;

        Present(WidthDip, HeaderHeightDip + listHeight + (2 * InnerPadDip), FlyoutPlacement.ScreenCentre);

        if (count > 0)
        {
            _list.SelectedIndex = count > 1 ? 1 : 0;
            _list.ScrollIntoView(_list.SelectedItem);
        }

        DispatcherQueue.TryEnqueue(FocusSelected);
    }

    private protected override void OnPreviewKey(KeyRoutedEventArgs e)
    {
        bool shift = (User32.GetAsyncKeyState(User32.VK_SHIFT) & 0x8000) != 0;
        switch (e.Key)
        {
            case VirtualKey.Enter:
                e.Handled = true;
                SwitchTo(_list.SelectedIndex);
                break;
            case VirtualKey.Tab:
                e.Handled = true;
                Move(shift ? -1 : +1);
                break;
            case VirtualKey.Down:
            case VirtualKey.Right:
                e.Handled = true;
                Move(+1);
                break;
            case VirtualKey.Up:
            case VirtualKey.Left:
                e.Handled = true;
                Move(-1);
                break;
        }
    }

    /// <summary>Lists the bar's windows in Windows' front-to-back order (most recently used first).</summary>
    private void Fill()
    {
        _list.Items.Clear();
        _handles.Clear();

        var byHandle = new Dictionary<nint, (TaskWindow Window, DockItem Item)>();
        foreach (DockItem item in DockModel.Build(Host.Pins.Pins, Host.Tasks.Groups))
        {
            if (item.Group is null)
            {
                continue;
            }

            foreach (TaskWindow window in item.Group.Windows)
            {
                byHandle[window.Handle] = (window, item);
            }
        }

        int iconPixels = IconService.PickSize((int)Math.Ceiling(IconDip * Scale()));
        foreach (nint hwnd in WindowInfo.EnumerateTopLevel())
        {
            if (!byHandle.TryGetValue(hwnd, out (TaskWindow Window, DockItem Item) entry))
            {
                continue;
            }

            byHandle.Remove(hwnd);
            _handles.Add(hwnd);
            _list.Items.Add(MakeRow(entry.Window, entry.Item, iconPixels));
        }

        _header.Text = _handles.Count switch
        {
            0 => "No open windows",
            1 => "1 open window",
            _ => _handles.Count + " open windows",
        };
    }

    private Grid MakeRow(TaskWindow window, DockItem item, int iconPixels)
    {
        var row = new Grid
        {
            Height = RowHeightDip - 8,
            ColumnSpacing = 12,
        };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(IconDip) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        ImageSource? icon = Host.Icons.GetIcon(item, iconPixels);
        if (icon is not null)
        {
            row.Children.Add(new Image
            {
                Source = icon,
                Width = IconDip,
                Height = IconDip,
                VerticalAlignment = VerticalAlignment.Center,
            });
        }

        string title = window.Title.Length > 0 ? window.Title : item.DisplayName;
        string detail = item.DisplayName + (window.IsMinimized ? " - minimised" : string.Empty);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 14,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (!string.Equals(title, detail, StringComparison.Ordinal))
        {
            text.Children.Add(new TextBlock
            {
                Text = detail,
                FontSize = 12,
                Opacity = 0.7,
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
        }

        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        AutomationProperties.SetName(row, title + ", " + detail);
        return row;
    }

    private void Move(int step)
    {
        int count = _handles.Count;
        if (count == 0)
        {
            return;
        }

        int index = _list.SelectedIndex < 0 ? 0 : _list.SelectedIndex;
        _list.SelectedIndex = ((index + step) % count + count) % count;
        _list.ScrollIntoView(_list.SelectedItem);
        FocusSelected();
    }

    private void FocusSelected()
    {
        if (_list.SelectedIndex >= 0 && _list.ContainerFromIndex(_list.SelectedIndex) is ListViewItem container)
        {
            container.Focus(FocusState.Keyboard);
        }
        else
        {
            _list.Focus(FocusState.Programmatic);
        }
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        SwitchTo(_list.Items.IndexOf(e.ClickedItem));
    }

    private void SwitchTo(int index)
    {
        if (index < 0 || index >= _handles.Count)
        {
            return;
        }

        nint target = _handles[index];

        // Close first, so the switcher hands the front over rather than fighting for it.
        HideFlyout();
        WindowActivator.BringToFront(target);
    }
}
