using System;
using System.Collections.Generic;
using Machitan.Config;
using Machitan.Core;
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

namespace Machitan.Custom;

/// <summary>
/// Custom mode's picker, in the middle of the bar's screen: one element's variants as a 2x2
/// grid, the one in use outlined in the accent; '&lt; 1 of N &gt;' goes through the elements
/// (Start, Apps, Tray, Dynaland, Corner widgets). Choosing a variant applies it at once. The previews
/// show real things only: the bar's own Start mark, the apps on the bar now with their icons
/// and names, the track playing now.
/// It takes the keyboard (Tab, Enter, arrows for the carousel, Esc to close), so custom mode
/// can be used without a mouse from Settings > Taskbar > Customize.
/// </summary>
public sealed partial class VariantPickerWindow : Window
{
    private const double Pad = 4;
    private const double CardWidthDip = 214;
    private const double CardHeightDip = 128;
    private const double PreviewHeightDip = 52;

    private static readonly PickerPage[] Pages =
    {
        PickerPage.StartCluster,
        PickerPage.AppDock,
        PickerPage.Tray,
        PickerPage.Dynaland,
        PickerPage.CornerWidgets,
    };

    private readonly ShellHost _host;
    private readonly CustomMode _mode;
    private readonly nint _hwnd;
    private readonly Grid _root;
    private readonly Border _card;
    private readonly TextBlock _title;
    private readonly Grid _grid;
    private readonly WindowCutter _cutter;
    private WindowSubclass? _subclass;
    private PickerPage _page;
    private bool _shown;
    private bool _closed;

    internal VariantPickerWindow(ShellHost host, CustomMode mode)
    {
        _host = host;
        _mode = mode;
        Title = "Machitan customize: variants";

        _title = new TextBlock
        {
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Colors.White),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var header = new Grid { ColumnSpacing = 4 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Button previous = HeaderButton("\uE76B", Strings.CmPrevious, () => Step(-1));
        Button next = HeaderButton("\uE76C", Strings.CmNext, () => Step(1));
        var done = new Button
        {
            Content = Strings.CmDone,
            MinWidth = 0,
            Padding = new Thickness(12, 4, 12, 4),
            CornerRadius = new CornerRadius(Radii.Small),
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(done, Strings.CmDoneCustomizing);
        ToolTipService.SetToolTip(done, Strings.CmDoneCustomizingTip);
        done.Click += (_, _) => _mode.Done();
        Button close = HeaderButton("\uE711", Strings.CmClosePicker, HidePicker);
        Grid.SetColumn(_title, 1);
        Grid.SetColumn(next, 2);
        Grid.SetColumn(done, 3);
        Grid.SetColumn(close, 4);
        header.Children.Add(previous);
        header.Children.Add(_title);
        header.Children.Add(next);
        header.Children.Add(done);
        header.Children.Add(close);

        _grid = new Grid { ColumnSpacing = 10, RowSpacing = 10 };
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(CardWidthDip) });
        _grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(CardWidthDip) });
        _grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(CardHeightDip) });
        _grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(CardHeightDip) });

        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(header);
        body.Children.Add(_grid);

        _card = new Border
        {
            Background = new SolidColorBrush(ColorHelper.FromArgb(245, 0, 0, 0)),
            BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(60, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Radii.Large),
            Padding = new Thickness(16),
            Child = body,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };

        _root = new Grid
        {
            Padding = new Thickness(Pad),
            Background = new SolidColorBrush(Colors.Transparent),
            RequestedTheme = ElementTheme.Dark,
        };
        _root.Children.Add(_card);
        _root.KeyDown += OnKeyDown;
        Content = _root;

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        SeeThrough.Prepare(this, _hwnd, alwaysOnTop: true);
        _subclass = new WindowSubclass(_hwnd, (uint message, nint wParam, nint lParam, out nint result) =>
            SeeThrough.TryHandleErase(_hwnd, message, wParam, out result));

        _cutter = new WindowCutter(_hwnd, _root);
        _cutter.Add(_card, Radii.Large);
        _root.LayoutUpdated += (_, _) => _cutter.Update();

        _host.Settings.Changed += OnSettingsChanged;
        Closed += (_, _) =>
        {
            _closed = true;
            _shown = false;
            _host.Settings.Changed -= OnSettingsChanged;
            _subclass?.Dispose();
            _subclass = null;
        };
    }

    /// <summary>Shows one element's variants, in the middle of the bar's screen, with the keyboard.</summary>
    internal void ShowPage(PickerPage page)
    {
        if (_closed)
        {
            return;
        }

        _page = page;
        Fill();

        _card.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        Windows.Foundation.Size size = _card.DesiredSize;
        if (size.Width <= 0 || size.Height <= 0)
        {
            // Not measurable before the window first shows: the card's known size.
            size = new Windows.Foundation.Size((2 * CardWidthDip) + 10 + 34, 34 + 12 + (2 * CardHeightDip) + 10 + 34);
        }
        nint near = _host.BarHandle != 0 ? _host.BarHandle : _hwnd;
        if (!User32.TryGetMonitorRect(near, out RECT monitor))
        {
            return;
        }

        uint dpi = User32.GetDpiForWindow(near);
        double scale = (dpi == 0 ? 96 : dpi) / 96.0;
        int width = (int)Math.Ceiling((size.Width + (2 * Pad)) * scale);
        int height = (int)Math.Ceiling((size.Height + (2 * Pad)) * scale);
        int x = monitor.Left + ((monitor.Width - width) / 2);
        int y = monitor.Top + ((monitor.Height - height) / 2);
        User32.SetWindowPos(_hwnd, User32.HWND_TOPMOST, x, y, width, height, 0);
        _cutter.Invalidate();

        if (!_shown)
        {
            _shown = true;
            Activate();
        }
        else
        {
            User32.SetForegroundWindow(_hwnd);
        }

        Log.Info("Custom mode: picker for " + CustomMode.NameOf(page) + ".");
    }

    internal void HidePicker()
    {
        if (_shown && !_closed)
        {
            _shown = false;
            User32.ShowWindow(_hwnd, User32.SW_HIDE);
        }
    }

    private void OnSettingsChanged()
    {
        if (_shown && !_closed)
        {
            // The variant in use (and the previews) follow every change, from here or elsewhere.
            Fill();
        }
    }

    private void Step(int by)
    {
        int at = Array.IndexOf(Pages, _page);
        _page = Pages[(at + by + Pages.Length) % Pages.Length];
        Fill();
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Escape:
                HidePicker();
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.PageUp:
                Step(-1);
                e.Handled = true;
                break;
            case Windows.System.VirtualKey.PageDown:
                Step(1);
                e.Handled = true;
                break;
        }
    }

    private void Fill()
    {
        try
        {
            int at = Array.IndexOf(Pages, _page);
            _title.Text = CustomMode.NameOf(_page) + "   " + Fmt.F(Strings.CmPageOf, at + 1, Pages.Length);

            _grid.Children.Clear();
            List<Variant> variants = _mode.VariantsOf(_page);
            Windows.UI.Color accent = _host.Accent.Current;
            for (int i = 0; i < variants.Count && i < 4; i++)
            {
                Button card = MakeCard(variants[i], i, accent);
                Grid.SetColumn(card, i % 2);
                Grid.SetRow(card, i / 2);
                _grid.Children.Add(card);
            }
        }
        catch (Exception ex)
        {
            Log.Error("Custom mode: the picker could not be filled.", ex);
        }
    }

    private Button MakeCard(Variant variant, int index, Windows.UI.Color accent)
    {
        var name = new TextBlock
        {
            Text = variant.Name,
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Colors.White),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        var about = new TextBlock
        {
            Text = variant.About,
            FontSize = 12,
            Foreground = new SolidColorBrush(ColorHelper.FromArgb(170, 255, 255, 255)),
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
        };

        var content = new StackPanel { Spacing = 4, Width = CardWidthDip - 24 };
        content.Children.Add(Preview(index));
        content.Children.Add(name);
        content.Children.Add(about);

        var card = new Button
        {
            Content = content,
            Width = CardWidthDip,
            Height = CardHeightDip,
            Padding = new Thickness(12, 10, 12, 10),
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Top,
            CornerRadius = new CornerRadius(Radii.Medium),
            Background = new SolidColorBrush(ColorHelper.FromArgb(variant.Current ? (byte)40 : (byte)18, 255, 255, 255)),
            BorderBrush = new SolidColorBrush(variant.Current ? accent : ColorHelper.FromArgb(50, 255, 255, 255)),
            BorderThickness = new Thickness(variant.Current ? 2 : 1),
        };
        AutomationProperties.SetName(card, variant.Name + (variant.Current ? ", " + Strings.CmInUse : string.Empty) + ". " + variant.About);
        card.Click += (_, _) =>
        {
            try
            {
                variant.Apply();
                Log.Info("Custom mode: " + CustomMode.NameOf(_page) + " set to " + variant.Name + ".");
            }
            catch (Exception ex)
            {
                Log.Error("Custom mode: the variant could not be applied.", ex);
            }
        };
        return card;
    }

    // ---------------------------------------------------------------- previews (real things only)

    private FrameworkElement Preview(int index)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Height = PreviewHeightDip,
            VerticalAlignment = VerticalAlignment.Center,
        };

        switch (_page)
        {
            case PickerPage.StartCluster:
                row.Children.Add(StartMark(22));
                if (index == 0)
                {
                    row.Children.Add(Glyph("\uE721", 18));
                }
                else if (index == 1)
                {
                    row.Children.Add(new Border
                    {
                        Width = 104,
                        Height = 28,
                        CornerRadius = new CornerRadius(14),
                        BorderThickness = new Thickness(1),
                        BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(70, 255, 255, 255)),
                        VerticalAlignment = VerticalAlignment.Center,
                        Padding = new Thickness(10, 0, 10, 0),
                        Child = new TextBlock
                        {
                            Text = Strings.BarSearchPlaceholder,
                            FontSize = 12,
                            Foreground = new SolidColorBrush(ColorHelper.FromArgb(150, 255, 255, 255)),
                            VerticalAlignment = VerticalAlignment.Center,
                        },
                    });
                }

                break;

            case PickerPage.AppDock:
                AddApps(row, labels: index == 1);
                break;

            case PickerPage.Tray:
                if (index == 1)
                {
                    AddTrayIcons(row);
                }
                else
                {
                    row.Children.Add(Glyph("\uE8D8", 18, dim: true));
                }

                break;

            case PickerPage.Dynaland:
                if (index == 0)
                {
                    string playing = _host.NowPlayingTitle ?? Strings.CmDynalandSample;
                    var pill = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                    pill.Children.Add(Glyph("\uE8D6", 12));
                    pill.Children.Add(new TextBlock
                    {
                        Text = playing,
                        FontSize = 12,
                        Foreground = new SolidColorBrush(Colors.White),
                        MaxWidth = 140,
                        TextTrimming = TextTrimming.CharacterEllipsis,
                        VerticalAlignment = VerticalAlignment.Center,
                    });
                    row.Children.Add(new Border
                    {
                        Height = 28,
                        CornerRadius = new CornerRadius(14),
                        Background = new SolidColorBrush(Colors.Black),
                        BorderThickness = new Thickness(1),
                        BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(60, 255, 255, 255)),
                        Padding = new Thickness(12, 0, 12, 0),
                        VerticalAlignment = VerticalAlignment.Center,
                        Child = pill,
                    });
                }
                else
                {
                    row.Children.Add(Glyph("\uE8D8", 18, dim: true));
                }

                break;

            default:
                if (index == 0 || index == 1)
                {
                    row.Children.Add(Glyph("\uE787", 18));
                }

                if (index == 0 || index == 2)
                {
                    row.Children.Add(Glyph("\uE83F", 18));
                }

                if (index == 3)
                {
                    row.Children.Add(Glyph("\uE8D8", 18, dim: true));
                }

                break;
        }

        return row;
    }

    /// <summary>The first tray icons now, with their own pictures, in the kept order.</summary>
    private void AddTrayIcons(StackPanel row)
    {
        List<TrayIcon> icons = _host.BarLayout.ArrangeTray(_host.Tray.Icons);
        int added = 0;
        foreach (TrayIcon icon in icons)
        {
            if (added == 5)
            {
                break;
            }

            if (icon.Picture is IconPixels pixels)
            {
                try
                {
                    row.Children.Add(new Image { Source = Machitan.Start.StartMenuWindow.ToBitmap(pixels), Width = 16, Height = 16 });
                    added++;
                }
                catch (Exception ex)
                {
                    Log.Warn("Custom mode: a tray picture could not be shown in the picker.", ex);
                }
            }
        }

        if (added == 0)
        {
            row.Children.Add(new TextBlock
            {
                Text = Strings.CmNoTray,
                FontSize = 12,
                Foreground = new SolidColorBrush(ColorHelper.FromArgb(150, 255, 255, 255)),
                VerticalAlignment = VerticalAlignment.Center,
            });
        }
    }

    /// <summary>The first apps on the bar now, with their own icons (and names).</summary>
    private void AddApps(StackPanel row, bool labels)
    {
        List<DockItem> items = DockModel.Build(_host.Pins.Pins, _host.Tasks.Groups);
        if (items.Count == 0)
        {
            row.Children.Add(new TextBlock
            {
                Text = Strings.CmNoApps,
                FontSize = 12,
                Foreground = new SolidColorBrush(ColorHelper.FromArgb(150, 255, 255, 255)),
                VerticalAlignment = VerticalAlignment.Center,
            });
            return;
        }

        uint dpi = User32.GetDpiForWindow(_hwnd);
        int pixels = (int)Math.Ceiling(20 * ((dpi == 0 ? 96 : dpi) / 96.0));
        int shown = labels ? 2 : 4;
        for (int i = 0; i < items.Count && i < shown; i++)
        {
            DockItem item = items[i];
            var one = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
            if (_host.Icons.GetIcon(item, pixels) is ImageSource icon)
            {
                one.Children.Add(new Image { Source = icon, Width = 20, Height = 20 });
            }
            else
            {
                one.Children.Add(Glyph("\uE71D", 16));
            }

            if (labels)
            {
                one.Children.Add(new TextBlock
                {
                    Text = item.DisplayName,
                    FontSize = 11,
                    MaxWidth = 64,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Foreground = new SolidColorBrush(Colors.White),
                    VerticalAlignment = VerticalAlignment.Center,
                });
            }

            row.Children.Add(one);
        }
    }

    private static FontIcon Glyph(string glyph, double size, bool dim = false)
    {
        return new FontIcon
        {
            Glyph = glyph,
            FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
            FontSize = size,
            Foreground = new SolidColorBrush(dim ? ColorHelper.FromArgb(110, 255, 255, 255) : Colors.White),
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    /// <summary>The bar's own Start mark: four rounded tiles (drawn, no image or logo).</summary>
    private static FrameworkElement StartMark(double size)
    {
        double gap = Math.Max(Math.Round(size / 12), 2);
        double tile = (size - gap) / 2;
        var grid = new Grid
        {
            Width = size,
            Height = size,
            ColumnSpacing = gap,
            RowSpacing = gap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(tile) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(tile) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(tile) });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(tile) });
        var fill = new SolidColorBrush(ColorHelper.FromArgb(240, 255, 255, 255));
        for (int i = 0; i < 4; i++)
        {
            var square = new Border { CornerRadius = new CornerRadius(Math.Max(tile * 0.25, 1)), Background = fill };
            Grid.SetColumn(square, i % 2);
            Grid.SetRow(square, i / 2);
            grid.Children.Add(square);
        }

        return grid;
    }

    private static Button HeaderButton(string glyph, string name, Action click)
    {
        var button = new Button
        {
            Content = Glyph(glyph, 14),
            Width = 34,
            Height = 34,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(17),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
        };
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, name);
        button.Click += (_, _) => click();
        return button;
    }
}
