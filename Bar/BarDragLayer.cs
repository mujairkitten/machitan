using System;
using Machitan.Core;
using Machitan.Interop;
using Machitan.Shell;
using Machitan.Visuals;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Color = Windows.UI.Color;

namespace Machitan.Bar;

/// <summary>
/// What a drag on the bar draws above it: the dragged piece following the pointer, an outline
/// around the group it would go into, and an accent line at the drop place. A see-through
/// window over a band along the bar, the whole screen wide, shown only while a drag runs.
/// The bar keeps the pointer the whole time, so this window never takes a click.
/// All positions come in physical pixels on screen.
/// </summary>
public sealed partial class BarDragLayer : Window
{
    private const double Pad = 4;
    private const double MarkerWidthDip = 3;
    private const double MarkerHeightRatio = 0.62;
    private const double OutlineDip = 2;
    private const double NoTargetOpacity = 0.55;

    private readonly nint _hwnd;
    private readonly Grid _root;
    private readonly Canvas _canvas;
    private readonly Grid _ghost;
    private readonly Image _ghostImage;
    private readonly Border _outline;
    private readonly Border _marker;
    private readonly SolidColorBrush _outlineBrush = new(Colors.Transparent);
    private readonly SolidColorBrush _markerBrush = new(Colors.Transparent);
    private readonly WindowCutter _cutter;
    private WindowSubclass? _subclass;
    private FrameworkElement? _ghostFill;
    private double _scale = 1;
    private double _originX;
    private double _originY;
    private bool _shown;
    private bool _closed;

    internal BarDragLayer()
    {
        Title = "Machitan bar drag";

        _canvas = new Canvas { IsHitTestVisible = false };
        _root = new Grid
        {
            Padding = new Thickness(Pad),
            Background = new SolidColorBrush(Colors.Transparent),
            IsHitTestVisible = false,
        };
        _root.Children.Add(_canvas);
        Content = _root;

        _outline = new Border
        {
            BorderThickness = new Thickness(OutlineDip),
            BorderBrush = _outlineBrush,
            Visibility = Visibility.Collapsed,
        };

        _ghostImage = new Image
        {
            Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
        _ghost = new Grid { Visibility = Visibility.Collapsed };
        _ghost.Children.Add(_ghostImage);

        _marker = new Border
        {
            Width = MarkerWidthDip,
            CornerRadius = new CornerRadius(MarkerWidthDip / 2),
            Background = _markerBrush,
            Visibility = Visibility.Collapsed,
        };

        // The marker last, so the piece never covers it.
        _canvas.Children.Add(_outline);
        _canvas.Children.Add(_ghost);
        _canvas.Children.Add(_marker);

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        SeeThrough.Prepare(this, _hwnd, alwaysOnTop: true);
        long exStyle = User32.GetWindowLongPtrW(_hwnd, User32.GWL_EXSTYLE);
        User32.SetWindowLongPtrW(_hwnd, User32.GWL_EXSTYLE, (nint)(exStyle | User32.WS_EX_NOACTIVATE));
        _subclass = new WindowSubclass(_hwnd, (uint message, nint wParam, nint lParam, out nint result) =>
            SeeThrough.TryHandleErase(_hwnd, message, wParam, out result));

        // One cut: the whole band inside the pad, so the window's own edge stays outside it.
        _cutter = new WindowCutter(_hwnd, _root);
        _cutter.Add(_canvas, 0);
        _root.LayoutUpdated += (_, _) => _cutter.Update();

        Closed += (_, _) =>
        {
            _closed = true;
            _shown = false;
            _subclass?.Dispose();
            _subclass = null;
        };
    }

    internal bool IsShown => _shown;

    /// <summary>Shows the layer, empty, over <paramref name="band"/>.</summary>
    internal void ShowBand(RECT band, double scale)
    {
        if (_closed)
        {
            return;
        }

        _scale = scale > 0 ? scale : 1;
        int pad = (int)Math.Round(Pad * _scale);
        var window = new RECT
        {
            Left = band.Left - pad,
            Top = band.Top - pad,
            Right = band.Right + pad,
            Bottom = band.Bottom + pad,
        };

        // Where the canvas starts on screen: the window's corner plus the pad, in exact DIP.
        _originX = window.Left + (Pad * _scale);
        _originY = window.Top + (Pad * _scale);

        User32.SetWindowPos(_hwnd, User32.HWND_TOPMOST, window.Left, window.Top, window.Width, window.Height, User32.SWP_NOACTIVATE);
        _cutter.Invalidate();
        if (!_shown)
        {
            _shown = true;
            AppWindow.Show(false);
        }

        // Above the bar, which is always on top too.
        User32.SetWindowPos(
            _hwnd,
            User32.HWND_TOPMOST,
            0,
            0,
            0,
            0,
            User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
    }

    /// <summary>The piece: <paramref name="fill"/> behind it, sized in DIP; its picture comes later.</summary>
    internal void SetGhost(FrameworkElement fill, double widthDip, double heightDip)
    {
        if (_closed)
        {
            return;
        }

        RemoveGhostFill();
        _ghostFill = fill;
        _ghost.Children.Insert(0, fill);
        _ghost.Width = widthDip;
        _ghost.Height = heightDip;
        _ghostImage.Source = null;
        _ghost.Opacity = 1;
        _ghost.Visibility = Visibility.Visible;
    }

    /// <summary>The piece's picture, placed inside it at the given offset and size (DIP).</summary>
    internal void SetGhostImage(ImageSource image, double leftDip, double topDip, double widthDip, double heightDip)
    {
        if (_closed)
        {
            return;
        }

        _ghostImage.Margin = new Thickness(leftDip, topDip, 0, 0);
        _ghostImage.Width = widthDip;
        _ghostImage.Height = heightDip;
        _ghostImage.Source = image;
    }

    /// <summary>Puts the piece's top-left corner at the given screen point; faded when it has nowhere to go.</summary>
    internal void MoveGhost(int left, int top, bool hasTarget)
    {
        if (_closed)
        {
            return;
        }

        Canvas.SetLeft(_ghost, ToDipX(left));
        Canvas.SetTop(_ghost, ToDipY(top));
        _ghost.Opacity = hasTarget ? 1 : NoTargetOpacity;
    }

    /// <summary>An outline just outside <paramref name="rect"/>, following its corners.</summary>
    internal void ShowOutline(RECT rect, double radiusDip, Color ink)
    {
        if (_closed)
        {
            return;
        }

        _outlineBrush.Color = ink;
        _outline.Width = (rect.Width / _scale) + (2 * OutlineDip);
        _outline.Height = (rect.Height / _scale) + (2 * OutlineDip);
        _outline.CornerRadius = new CornerRadius(radiusDip + OutlineDip);
        Canvas.SetLeft(_outline, ToDipX(rect.Left) - OutlineDip);
        Canvas.SetTop(_outline, ToDipY(rect.Top) - OutlineDip);
        _outline.Visibility = Visibility.Visible;
    }

    internal void HideOutline()
    {
        _outline.Visibility = Visibility.Collapsed;
    }

    /// <summary>A short accent line centred on <paramref name="x"/>, centred on the bar's row.</summary>
    internal void ShowMarker(int x, RECT row, Color accent)
    {
        if (_closed)
        {
            return;
        }

        double rowHeight = row.Height / _scale;
        double height = Math.Max(rowHeight * MarkerHeightRatio, 12);
        _markerBrush.Color = accent;
        _marker.Height = height;
        Canvas.SetLeft(_marker, ToDipX(x) - (MarkerWidthDip / 2));
        Canvas.SetTop(_marker, ToDipY(row.Top) + ((rowHeight - height) / 2));
        _marker.Visibility = Visibility.Visible;
    }

    internal void HideMarker()
    {
        _marker.Visibility = Visibility.Collapsed;
    }

    /// <summary>Empties and hides the layer. Safe to call at any time.</summary>
    internal void HideLayer()
    {
        _outline.Visibility = Visibility.Collapsed;
        _marker.Visibility = Visibility.Collapsed;
        _ghost.Visibility = Visibility.Collapsed;
        _ghostImage.Source = null;
        RemoveGhostFill();

        if (_shown && !_closed)
        {
            _shown = false;
            User32.ShowWindow(_hwnd, User32.SW_HIDE);
        }
    }

    private void RemoveGhostFill()
    {
        if (_ghostFill is null)
        {
            return;
        }

        try
        {
            _ghost.Children.Remove(_ghostFill);
            SurfaceFill.Release(_ghostFill);
        }
        catch (Exception ex)
        {
            Log.Warn("Bar drag: the dragged piece's fill could not be removed.", ex);
        }

        _ghostFill = null;
    }

    private double ToDipX(double screenX)
    {
        return (screenX - _originX) / _scale;
    }

    private double ToDipY(double screenY)
    {
        return (screenY - _originY) / _scale;
    }
}
