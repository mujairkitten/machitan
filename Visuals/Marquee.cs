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
/// With a cap, Show can also let a long line get smaller (in half steps, down to a smallest
/// size) before it slides, and make it slide once instead of round and round: it shows its
/// start, slides to its end within a given time, and stays there (Dynaland's lyric lines).
/// </summary>
internal sealed class Marquee
{
    private const double Gap = 40;

    /// <summary>DIP per second.</summary>
    private const double Speed = 32;

    /// <summary>A line that may get smaller does so in steps this big.</summary>
    private const double SizeStep = 0.5;

    private readonly Grid _root;
    private readonly Canvas _canvas;
    private readonly StackPanel _strip;
    private readonly TextBlock _first;
    private readonly TextBlock _second;

    /// <summary>Not in the window: used only to measure the text's full width.</summary>
    private readonly TextBlock _measure;
    private double _maxWidth;
    private readonly Visual _visual;
    private readonly CompositionEasingFunction _linear;
    private readonly ScalarKeyFrameAnimation _scroll;
    private string _text = string.Empty;
    private double _textWidth;

    /// <summary>The line's own size, and the smallest it may get when it does not fit (its own size: never smaller).</summary>
    private readonly double _baseSize;
    private double _smallestSize;

    /// <summary>Set: a line that still does not fit slides once, to its end within this time; null: round and round.</summary>
    private TimeSpan? _slideOnceWithin;
    private bool _scrolling;
    private bool _paused;

    /// <summary>The text changed and the sliding has not been set up for it yet.</summary>
    private bool _dirty;

    internal Marquee(double fontSize, Windows.UI.Text.FontWeight weight, Brush foreground, double maxWidth = double.NaN)
    {
        _maxWidth = maxWidth;
        _baseSize = fontSize;
        _smallestSize = fontSize;
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

    /// <summary>Paused: the sliding stops where it is (music paused) and goes on from there when unpaused.</summary>
    internal bool Paused
    {
        get => _paused;
        set
        {
            if (_paused == value)
            {
                return;
            }

            _paused = value;
            ApplyPause();
        }
    }

    private void ApplyPause()
    {
        if (!_scrolling)
        {
            return;
        }

        AnimationController? controller = _visual.TryGetAnimationController("Translation.X");
        if (controller is null)
        {
            return;
        }

        if (_paused)
        {
            controller.Pause();
        }
        else
        {
            controller.Resume();
        }
    }

    /// <summary>The line, at its own size, sliding round and round when it does not fit.</summary>
    internal string Text
    {
        get => _text;
        set => Show(value, _baseSize, null, again: false);
    }

    /// <summary>
    /// Shows a line. With a cap, a line too long for it gets smaller, down to
    /// <paramref name="smallestSize"/> (the line's own size: never smaller). If it still does not
    /// fit, it slides: once, to its end within <paramref name="slideOnceWithin"/>, or (null) round
    /// and round. The same line again changes nothing, unless <paramref name="again"/> starts
    /// its sliding over (the same words as a new lyric line).
    /// </summary>
    internal void Show(string text, double smallestSize, TimeSpan? slideOnceWithin, bool again)
    {
        text ??= string.Empty;
        double smallest = Math.Clamp(smallestSize, 1, _baseSize);
        if (!again
            && text == _text
            && Math.Abs(smallest - _smallestSize) < 0.01
            && slideOnceWithin.HasValue == _slideOnceWithin.HasValue)
        {
            return;
        }

        _text = text;
        _first.Text = text;
        _second.Text = text;
        _smallestSize = smallest;
        _slideOnceWithin = slideOnceWithin;
        MeasureText();
        _dirty = true;
        Update(force: true);
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
        double size = _baseSize;
        double width = MeasureAt(size);

        // Too long for the cap: smaller, a half step at a time, down to the smallest allowed.
        if (!double.IsNaN(_maxWidth) && _text.Length > 0)
        {
            while (Math.Ceiling(width) > _maxWidth + 0.5 && size - SizeStep >= _smallestSize - 0.001)
            {
                size -= SizeStep;
                width = MeasureAt(size);
            }
        }

        _first.FontSize = size;
        _second.FontSize = size;
        _textWidth = _text.Length > 0 ? Math.Ceiling(width) : 0;
        double height = Math.Ceiling(_measure.DesiredSize.Height);
        _root.Height = height;
        _canvas.Height = height;
        if (!double.IsNaN(_maxWidth))
        {
            // One pixel of room for rounding, so a line that fits is never clipped at its end.
            _root.Width = Math.Min(_textWidth + 1, _maxWidth);
        }
    }

    /// <summary>The text's width at this size (the measuring copy keeps the size, for its height).</summary>
    private double MeasureAt(double size)
    {
        _measure.FontSize = size;
        _measure.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        return _measure.DesiredSize.Width;
    }

    /// <summary>
    /// A new longest width, for a line made with one (a line that fills its place follows the
    /// place instead).
    /// </summary>
    internal void SetMaxWidth(double maxWidth)
    {
        if (double.IsNaN(_maxWidth) || double.IsNaN(maxWidth) || Math.Abs(maxWidth - _maxWidth) < 0.5)
        {
            return;
        }

        _maxWidth = maxWidth;
        MeasureText();
        Update(force: true);
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
        bool once = _slideOnceWithin.HasValue;
        _second.Visibility = scroll && !once ? Visibility.Visible : Visibility.Collapsed;
        if (!scroll)
        {
            return;
        }

        if (_slideOnceWithin is TimeSpan within)
        {
            // Once: its start for a fifth of the time, the slide to its end, then its end stays.
            double seconds = Math.Clamp(within.TotalSeconds, 0.5, 30);
            _scroll.InsertKeyFrame(0f, 0f);
            _scroll.InsertKeyFrame(1f, (float)-(_textWidth - available), _linear);
            _scroll.Duration = TimeSpan.FromSeconds(seconds * 0.6);
            _scroll.DelayTime = TimeSpan.FromSeconds(seconds * 0.2);
            _scroll.IterationBehavior = AnimationIterationBehavior.Count;
            _scroll.IterationCount = 1;
            _visual.StartAnimation("Translation.X", _scroll);
            if (_paused)
            {
                ApplyPause();
            }

            return;
        }

        double distance = _textWidth + Gap;
        _scroll.InsertKeyFrame(0f, 0f);
        _scroll.InsertKeyFrame(1f, (float)-distance, _linear);
        _scroll.Duration = TimeSpan.FromSeconds(distance / Speed);
        _scroll.DelayTime = TimeSpan.FromSeconds(1.5);
        _scroll.IterationBehavior = AnimationIterationBehavior.Forever;
        _visual.StartAnimation("Translation.X", _scroll);
        if (_paused)
        {
            ApplyPause();
        }
    }
}
