using System;
using System.Collections.Generic;
using System.Text;
using Machitan.Core;
using Machitan.Interop;
using Microsoft.UI.Xaml;

namespace Machitan.Visuals;

/// <summary>
/// Cuts a window to a set of rounded shapes, each 2 px wider than drawn: smooth edges stay
/// inside, gaps between shapes are not part of the window (clicks there go through).
/// Call <see cref="Update"/> after layout; it does nothing when the shapes did not move.
/// </summary>
internal sealed class WindowCutter
{
    private const int SlackPx = 2;

    private readonly nint _hwnd;
    private readonly FrameworkElement _root;
    private readonly List<(FrameworkElement Element, double RadiusDip)> _shapes = new();
    private string _signature = string.Empty;

    internal WindowCutter(nint hwnd, FrameworkElement root)
    {
        _hwnd = hwnd;
        _root = root;
    }

    internal void Add(FrameworkElement element, double radiusDip)
    {
        _shapes.Add((element, radiusDip));
        _signature = string.Empty;
    }

    /// <summary>Forget the last cut, so the next Update applies it again.</summary>
    internal void Invalidate()
    {
        _signature = string.Empty;
    }

    internal void Update()
    {
        if (_root.XamlRoot is null || _shapes.Count == 0)
        {
            return;
        }

        try
        {
            double scale = _root.XamlRoot.RasterizationScale;
            var rects = new List<(int Left, int Top, int Right, int Bottom, int Ellipse)>(_shapes.Count);
            var signature = new StringBuilder();

            foreach ((FrameworkElement element, double radiusDip) in _shapes)
            {
                if (element.Visibility != Visibility.Visible || element.ActualWidth <= 0 || element.ActualHeight <= 0)
                {
                    continue;
                }

                Windows.Foundation.Rect bounds = element.TransformToVisual(_root)
                    .TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));

                int left = (int)Math.Floor(bounds.Left * scale) - SlackPx;
                int top = (int)Math.Floor(bounds.Top * scale) - SlackPx;
                int right = (int)Math.Ceiling(bounds.Right * scale) + SlackPx + 1;
                int bottom = (int)Math.Ceiling(bounds.Bottom * scale) + SlackPx + 1;
                int ellipse = (int)Math.Round(((radiusDip * scale) + SlackPx) * 2);

                rects.Add((left, top, right, bottom, ellipse));
                signature.Append(left).Append(',').Append(top).Append(',').Append(right).Append(',')
                    .Append(bottom).Append(',').Append(ellipse).Append(';');
            }

            if (rects.Count == 0)
            {
                return;
            }

            string newSignature = signature.ToString();
            if (newSignature == _signature)
            {
                return;
            }

            nint total = Gdi32.CreateRectRgn(0, 0, 0, 0);
            if (total == 0)
            {
                return;
            }

            foreach ((int left, int top, int right, int bottom, int ellipse) in rects)
            {
                nint shape = Gdi32.CreateRoundRectRgn(left, top, right, bottom, ellipse, ellipse);
                if (shape != 0)
                {
                    Gdi32.CombineRgn(total, total, shape, Gdi32.RGN_OR);
                    Gdi32.DeleteObject(shape);
                }
            }

            if (User32.SetWindowRgn(_hwnd, total, true) == 0)
            {
                // The system took no ownership, so the region is still ours to free.
                Gdi32.DeleteObject(total);
                Log.Warn("A window region was refused by Windows.");
                return;
            }

            _signature = newSignature;
        }
        catch (Exception ex)
        {
            Log.Error("A window region could not be updated.", ex);
        }
    }
}
