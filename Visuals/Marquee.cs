using System;
using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;

namespace Machitan.Visuals;

/// <summary>
/// One line of text that scrolls sideways when it does not fit, and stands still when it does.
/// The text is drawn twice with a gap, and the pair slides left by one text-plus-gap, forever,
/// so the loop is seamless; it waits 1.5 s before it starts moving. The sliding runs in
/// Composition (Translation.X), not in layout. Two widths: a cap (the line is as wide as its
/// text, up to the cap) or the width its parent gives it (pass NaN).
/// </summary>
internal sealed class Marquee
{
    private const double Gap = 40;

    /// <summary>DIP per second.</summary>
    private const double Speed = 32;

    private readonly Grid _root;
    private readonly Canvas _canvas;
    private readonly StackPanel _strip;
    private readonly TextBlock _first;
    private readonly TextBlock _second;

    /// <summary>Not in the window: used only to measure the text's full width.</summary>
    private readonly TextBlock _measure;
    private readonly double _maxWidth;
    private readonly Visual _visual;
    private readonly CompositionEasingFunction _linear;
    private readonly ScalarKeyFrameAnimation _scroll;
    private string _text = string.Empty;
    private double _textWidth;
    private bool _scrolling;

    /// <summary>The text changed and the sliding has not been set up for it yet.</summary>
    private bool _dirty;

    internal Marquee(double fontSize, Windows.UI.Text.FontWeight weight, Brush foreground, double maxWidth = double.NaN)
    {
        _maxWidth = maxWidth;
        _first = MakeText(fontSize, weight, foreground);
        _second = MakeText(fontSize, weight, foreground);
        _second.Visibility = Visibility.Collapsed;
        _measure = MakeText(fontSize, weight, foreground);

        _strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = Gap };
        _strip.Children.Add(_first);
        _strip.Children.Add(_second);

        // A canvas lets the strip be wider than the line without being cut by layout.
        _canvas = new Canvas();
        _canvas.Children.Add(_strip);

        _root = new Grid
        {
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = double.IsNaN(maxWidth) ? HorizontalAlignment.Stretch : HorizontalAlignment.Left,
        };
        _root.Children.Add(_canvas);
        _root.SizeChanged += (_, e) =>
        {
            _root.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
            Update(force: false);
        };

        ElementCompositionPreview.SetIsTranslationEnabled(_strip, true);
        _visual = ElementCompositionPreview.GetElementVisual(_strip);
        Compositor compositor = _visual.Compositor;
        _linear = compositor.CreateLinearEasingFunction();
        _scroll = compositor.CreateScalarKeyFrameAnimation();
    }

    /// <summary>The element to put in the layout.</summary>
    internal FrameworkElement Element => _root;

    internal string Text
    {
        get => _text;
        set
        {
            value ??= string.Empty;
            if (value == _text)
            {
                return;
            }

            _text = value;
            _first.Text = value;
            _second.Text = value;
            MeasureText();
            _dirty = true;
            Update(force: true);
        }
    }

    private static TextBlock MakeText(double fontSize, Windows.UI.Text.FontWeight weight, Brush foreground)
    {
        return new TextBlock
        {
            FontSize = fontSize,
            FontWeight = weight,
            Foreground = foreground,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.None,
        };
    }

    private void MeasureText()
    {
        // Measured with the text at least one character long, so an empty line keeps its height.
        _measure.Text = _text.Length > 0 ? _text : " ";
        _measure.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        _textWidth = _text.Length > 0 ? Math.Ceiling(_measure.DesiredSize.Width) : 0;
        double height = Math.Ceiling(_measure.DesiredSize.Height);
        _root.Height = height;
        _canvas.Height = height;
        if (!double.IsNaN(_maxWidth))
        {
            // One pixel of room for rounding, so a line that fits is never clipped at its end.
            _root.Width = Math.Min(_textWidth + 1, _maxWidth);
        }
    }

    /// <summary>Starts or stops the sliding to match the room the line has.</summary>
    private void Update(bool force)
    {
        double available = double.IsNaN(_maxWidth) ? _root.ActualWidth : _maxWidth;
        if (available <= 0)
        {
            // Not laid out yet: SizeChanged calls again.
            return;
        }

        bool scroll = _textWidth > available + 0.5;
        if (!force && !_dirty && scroll == _scrolling)
        {
            return;
        }

        _dirty = false;
        _scrolling = scroll;
        _visual.StopAnimation("Translation.X");
        _visual.Properties.InsertVector3("Translation", Vector3.Zero);
        _second.Visibility = scroll ? Visibility.Visible : Visibility.Collapsed;
        if (!scroll)
        {
            return;
        }

        double distance = _textWidth + Gap;
        _scroll.InsertKeyFrame(0f, 0f);
        _scroll.InsertKeyFrame(1f, (float)-distance, _linear);
        _scroll.Duration = TimeSpan.FromSeconds(distance / Speed);
        _scroll.DelayTime = TimeSpan.FromSeconds(1.5);
        _scroll.IterationBehavior = AnimationIterationBehavior.Forever;
        _visual.StartAnimation("Translation.X", _scroll);
    }
}
