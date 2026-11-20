using System;
using System.IO;
using Machitan.Config;
using Machitan.Core;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Machitan.Bar;

/// <summary>
/// The Start button's own pictures (Settings > Start > Start button, off by default): one for
/// the button at rest, one while the pointer is on it, one while it is pressed and for as long
/// as the Start menu is open (Sam, 2026-11-20). They take the logo's place and size (the
/// picture is fitted into it, its shape kept). A state without a picture shows the one before
/// it (pressed: the hovered one, else the normal one), with the bar's usual hover or press tile
/// behind it; a state with its own picture shows no tile (the picture is the feedback).
/// Without a normal picture (none chosen, or the file is gone), Machitan's logo stays.
/// </summary>
internal sealed class StartButtonPictures
{
    private readonly Image _image;
    private readonly ImageSource _normal;
    private readonly ImageSource? _hovered;
    private readonly ImageSource? _pressed;
    private bool _over;
    private bool _down;
    private bool _held;

    private StartButtonPictures(ImageSource normal, ImageSource? hovered, ImageSource? pressed, double width, double height)
    {
        _normal = normal;
        _hovered = hovered;
        _pressed = pressed;
        _image = new Image
        {
            Source = normal,
            Width = width,
            Height = height,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            IsHitTestVisible = false,
        };
    }

    /// <summary>What the button shows.</summary>
    internal UIElement Content => _image;

    /// <summary>The pictures from the settings, fitted into width x height (DIP); null when there is no normal picture.</summary>
    internal static StartButtonPictures? Load(ShellSettings settings, double width, double height)
    {
        ImageSource? normal = Open(settings.StartButtonPicture, "normal");
        if (normal is null)
        {
            return null;
        }

        return new StartButtonPictures(
            normal,
            Open(settings.StartButtonPictureHover, "hovered"),
            Open(settings.StartButtonPicturePressed, "pressed"),
            width,
            height);
    }

    /// <summary>Follows the pointer on the button.</summary>
    internal void Attach(Button button)
    {
        var transparent = new SolidColorBrush(Colors.Transparent);
        if (_hovered is not null)
        {
            button.Resources["ButtonBackgroundPointerOver"] = transparent;
            button.Resources["ButtonBorderBrushPointerOver"] = transparent;
        }

        if (_pressed is not null)
        {
            button.Resources["ButtonBackgroundPressed"] = transparent;
            button.Resources["ButtonBorderBrushPressed"] = transparent;
        }

        button.PointerEntered += (_, _) =>
        {
            _over = true;
            Show();
        };
        button.PointerExited += (_, _) =>
        {
            _over = false;
            _down = false;
            Show();
        };
        button.PointerCaptureLost += (_, _) =>
        {
            _down = false;
            Show();
        };

        // The button handles presses itself; these still see them.
        button.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, _) =>
        {
            _down = true;
            Show();
        }), handledEventsToo: true);
        button.AddHandler(UIElement.PointerReleasedEvent, new PointerEventHandler((_, _) =>
        {
            _down = false;
            Show();
        }), handledEventsToo: true);
    }

    /// <summary>The Start menu is open (true) or closed: the pressed picture stays while it is open.</summary>
    internal void SetHeld(bool held)
    {
        if (_held != held)
        {
            _held = held;
            Show();
        }
    }

    private void Show()
    {
        bool pressed = _down || _held;
        ImageSource source = pressed && _pressed is not null
            ? _pressed
            : (_over || pressed) && _hovered is not null
                ? _hovered
                : _normal;
        if (!ReferenceEquals(_image.Source, source))
        {
            _image.Source = source;
        }
    }

    /// <summary>A picture file as an image; null when none is set or it cannot be used (logged).</summary>
    private static ImageSource? Open(string path, string which)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            if (!File.Exists(path))
            {
                Log.Warn("Start button: the " + which + " picture is not there any more (" + path + ").");
                return null;
            }

            // Read again each time: a picture changed under the same name shows the new one.
            var picture = new BitmapImage
            {
                CreateOptions = BitmapCreateOptions.IgnoreImageCache,
                UriSource = new Uri(path),
            };
            picture.ImageFailed += (_, e) => Log.Warn("Start button: the " + which + " picture could not be shown (" + e.ErrorMessage + ").");
            return picture;
        }
        catch (Exception ex)
        {
            Log.Warn("Start button: the " + which + " picture could not be opened (" + path + ").", ex);
            return null;
        }
    }
}
