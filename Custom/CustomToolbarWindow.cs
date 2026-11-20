using System;
using Machitan.Core;
using Machitan.Interop;
using Machitan.Shell;
using Machitan.Visuals;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Machitan.Custom;

/// <summary>
/// Custom mode's toolbar at the top right of the bar's screen: Back, Auto-arrange, Reset, Add
/// and Done, white glyphs on a black capsule. A see-through window cut to the capsule; it
/// never takes the keyboard (Settings > Taskbar > Customize opens the picker, which does).
/// </summary>
public sealed partial class CustomToolbarWindow : Window
{
    private const double Pad = 4;
    private const double ButtonDip = 40;
    private const double TopGapDip = 76;
    private const double SideGapDip = 16;

    private readonly ShellHost _host;
    private readonly CustomMode _mode;
    private readonly nint _hwnd;
    private readonly Grid _root;
    private readonly Border _capsule;
    private readonly Button _add;
    private readonly WindowCutter _cutter;
    private WindowSubclass? _subclass;
    private bool _shown;
    private bool _closed;

    internal CustomToolbarWindow(ShellHost host, CustomMode mode)
    {
        _host = host;
        _mode = mode;
        Title = "Machitan customize";

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        row.Children.Add(MakeButton("\uE72B", Strings.CmBack, Strings.CmBackTip, () => _mode.Back()));
        row.Children.Add(MakeButton("\uE8CB", Strings.CmAutoArrange, Strings.CmAutoArrangeTip, () => _mode.AutoArrange()));
        row.Children.Add(MakeButton("\uE72C", Strings.CmReset, Strings.CmResetTip, () => _mode.Reset()));
        _add = MakeButton("\uE710", Strings.CmAdd, Strings.CmAddTip, () => _mode.Add());
        row.Children.Add(_add);
        row.Children.Add(MakeButton("\uE8FB", Strings.CmDone, Strings.CmDoneTip, () => _mode.Done()));

        _capsule = new Border
        {
            Background = new SolidColorBrush(ColorHelper.FromArgb(240, 0, 0, 0)),
            BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(60, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius((ButtonDip + 10) / 2),
            Padding = new Thickness(4),
            Child = row,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };

        _root = new Grid
        {
            Padding = new Thickness(Pad),
            Background = new SolidColorBrush(Colors.Transparent),
            RequestedTheme = ElementTheme.Dark,
        };
        _root.Children.Add(_capsule);
        Content = _root;

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        SeeThrough.Prepare(this, _hwnd, alwaysOnTop: true);
        long exStyle = User32.GetWindowLongPtrW(_hwnd, User32.GWL_EXSTYLE);
        User32.SetWindowLongPtrW(_hwnd, User32.GWL_EXSTYLE, (nint)(exStyle | User32.WS_EX_NOACTIVATE));
        _subclass = new WindowSubclass(_hwnd, (uint message, nint wParam, nint lParam, out nint result) =>
            SeeThrough.TryHandleErase(_hwnd, message, wParam, out result));

        _cutter = new WindowCutter(_hwnd, _root);
        _cutter.Add(_capsule, (ButtonDip + 10) / 2);
        _root.LayoutUpdated += (_, _) => _cutter.Update();

        _mode.Changed += UpdateAdd;
        _host.Settings.Changed += UpdateAdd;
        Closed += (_, _) =>
        {
            _closed = true;
            _shown = false;
            _mode.Changed -= UpdateAdd;
            _host.Settings.Changed -= UpdateAdd;
            _subclass?.Dispose();
            _subclass = null;
        };
    }

    internal void ShowToolbar()
    {
        if (_closed)
        {
            return;
        }

        UpdateAdd();
        _capsule.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        Windows.Foundation.Size size = _capsule.DesiredSize;
        if (size.Width <= 0 || size.Height <= 0)
        {
            // Not measurable before the window first shows: the capsule's known size.
            size = new Windows.Foundation.Size((5 * ButtonDip) + (4 * 2) + 10, ButtonDip + 10);
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
        int x = monitor.Right - width - (int)Math.Round(SideGapDip * scale);
        int y = monitor.Top + (int)Math.Round(TopGapDip * scale);

        User32.SetWindowPos(_hwnd, User32.HWND_TOPMOST, x, y, width, height, User32.SWP_NOACTIVATE);
        _cutter.Invalidate();
        if (!_shown)
        {
            _shown = true;
            AppWindow.Show(false);
        }

        KeepOnTop();
    }

    internal void HideToolbar()
    {
        if (_shown && !_closed)
        {
            _shown = false;
            User32.ShowWindow(_hwnd, User32.SW_HIDE);
        }
    }

    internal void KeepOnTop()
    {
        if (_shown && !_closed)
        {
            User32.SetWindowPos(_hwnd, User32.HWND_TOPMOST, 0, 0, 0, 0, User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
        }
    }

    /// <summary>Add works only when something is removed or off.</summary>
    private void UpdateAdd()
    {
        if (_closed)
        {
            return;
        }

        bool can = _mode.FirstRemoved() is not null;
        _add.IsEnabled = can;
        _add.Opacity = can ? 1 : 0.4;
        ToolTipService.SetToolTip(_add, can ? Strings.CmAddTip : Strings.CmNothingRemoved);
    }

    private static Button MakeButton(string glyph, string name, string tip, Action click)
    {
        var button = new Button
        {
            Content = new FontIcon
            {
                Glyph = glyph,
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 16,
                Foreground = new SolidColorBrush(Colors.White),
            },
            Width = ButtonDip,
            Height = ButtonDip,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(ButtonDip / 2),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.Transparent),
        };
        AutomationProperties.SetName(button, name);
        ToolTipService.SetToolTip(button, tip);
        button.Click += (_, _) =>
        {
            try
            {
                click();
            }
            catch (Exception ex)
            {
                Log.Error("Custom mode: " + name + " failed.", ex);
            }
        };
        return button;
    }
}
