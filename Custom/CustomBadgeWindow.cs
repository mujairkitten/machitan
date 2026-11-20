using System;
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
/// The white x badge on the picked element's top-right corner, in custom mode: clicking it
/// removes the element. A small see-through window cut to the circle, above everything.
/// </summary>
public sealed partial class CustomBadgeWindow : Window
{
    private const double Pad = 4;
    private const double BadgeDip = 26;
    private const double InsetDip = 3;

    private readonly CustomMode _mode;
    private readonly nint _hwnd;
    private readonly Grid _root;
    private readonly Button _badge;
    private readonly WindowCutter _cutter;
    private WindowSubclass? _subclass;
    private RECT _placed;
    private bool _shown;
    private bool _closed;

    internal CustomBadgeWindow(CustomMode mode)
    {
        _mode = mode;
        Title = "Machitan remove badge";

        _badge = new Button
        {
            Content = new FontIcon
            {
                Glyph = "\uE711",
                FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),
                FontSize = 11,
                Foreground = new SolidColorBrush(Colors.Black),
            },
            Width = BadgeDip,
            Height = BadgeDip,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(BadgeDip / 2),
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Colors.White),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        AutomationProperties.SetName(_badge, Strings.CmRemove);
        _badge.Click += (_, _) => _mode.RemoveSelected();

        _root = new Grid
        {
            Padding = new Thickness(Pad),
            Background = new SolidColorBrush(Colors.Transparent),
            RequestedTheme = ElementTheme.Light,
        };
        _root.Children.Add(_badge);
        Content = _root;

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        SeeThrough.Prepare(this, _hwnd, alwaysOnTop: true);
        long exStyle = User32.GetWindowLongPtrW(_hwnd, User32.GWL_EXSTYLE);
        User32.SetWindowLongPtrW(_hwnd, User32.GWL_EXSTYLE, (nint)(exStyle | User32.WS_EX_NOACTIVATE));
        _subclass = new WindowSubclass(_hwnd, (uint message, nint wParam, nint lParam, out nint result) =>
            SeeThrough.TryHandleErase(_hwnd, message, wParam, out result));

        _cutter = new WindowCutter(_hwnd, _root);
        _cutter.Add(_badge, BadgeDip / 2);
        _root.LayoutUpdated += (_, _) => _cutter.Update();

        Closed += (_, _) =>
        {
            _closed = true;
            _shown = false;
            _subclass?.Dispose();
            _subclass = null;
        };
    }

    /// <summary>On the top-right corner of <paramref name="element"/> (physical pixels), kept on its screen.</summary>
    internal void ShowAt(RECT element, bool canRemove)
    {
        if (_closed)
        {
            return;
        }

        _badge.IsEnabled = canRemove;
        _badge.Opacity = canRemove ? 1 : 0.5;
        ToolTipService.SetToolTip(_badge, canRemove ? Strings.CmRemove : Strings.CmKeepOne);

        uint dpi = User32.GetDpiForWindow(_hwnd);
        double scale = (dpi == 0 ? 96 : dpi) / 96.0;
        int size = (int)Math.Ceiling((BadgeDip + (2 * Pad)) * scale);
        int half = size / 2;
        int inset = (int)Math.Round(InsetDip * scale);
        int x = element.Right - half - inset;
        int y = element.Top - half + inset;

        var centre = new POINT { X = element.Right, Y = element.Top };
        if (User32.TryGetMonitorRectAt(centre, out RECT monitor))
        {
            x = Math.Clamp(x, monitor.Left, Math.Max(monitor.Left, monitor.Right - size));
            y = Math.Clamp(y, monitor.Top, Math.Max(monitor.Top, monitor.Bottom - size));
        }

        var rect = new RECT { Left = x, Top = y, Right = x + size, Bottom = y + size };
        if (!_shown || !rect.SameAs(_placed))
        {
            _placed = rect;
            User32.SetWindowPos(_hwnd, User32.HWND_TOPMOST, x, y, size, size, User32.SWP_NOACTIVATE);
            _cutter.Invalidate();
        }

        if (!_shown)
        {
            _shown = true;
            AppWindow.Show(false);
        }

        User32.SetWindowPos(_hwnd, User32.HWND_TOPMOST, 0, 0, 0, 0, User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
    }

    internal void HideBadge()
    {
        if (_shown && !_closed)
        {
            _shown = false;
            User32.ShowWindow(_hwnd, User32.SW_HIDE);
        }
    }
}
