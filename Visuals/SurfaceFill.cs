using System;
using Machitan.Config;
using Machitan.Core;
using Microsoft.UI;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Machitan.Visuals;

/// <summary>
/// The fill behind a floating surface (Start menu boxes): acrylic when it is on and supported,
/// else a translucent colour at the Opacity setting. Used by the bar's segments and Start's boxes.
/// A fill that is replaced must be passed to <see cref="Release"/> first.
/// </summary>
internal static class SurfaceFill
{
    internal static FrameworkElement Create(ShellSettings settings, bool dark, double radiusDip)
    {
        bool acrylic = false;
        try
        {
            acrylic = settings.EnableAcrylic && DesktopAcrylicController.IsSupported();
        }
        catch (Exception ex)
        {
            Log.Warn("Could not ask Windows whether acrylic is supported; using a plain fill.", ex);
        }

        if (acrylic)
        {
            // One backdrop instance per element: each drives its own controller.
            return new SystemBackdropElement
            {
                CornerRadius = new CornerRadius(radiusDip),
                SystemBackdrop = new PersistentAcrylicBackdrop
                {
                    UseDarkTint = dark,
                    TintStrength = (float)settings.Opacity,
                },
            };
        }

        byte alpha = (byte)Math.Round(settings.Opacity * 255);
        return new Border
        {
            CornerRadius = new CornerRadius(radiusDip),
            Background = new SolidColorBrush(dark
                ? ColorHelper.FromArgb(alpha, 32, 32, 32)
                : ColorHelper.FromArgb(alpha, 243, 243, 243)),
        };
    }

    /// <summary>
    /// Detaches an acrylic fill's backdrop now, on the UI thread, before the fill is thrown
    /// away, so its controller is closed here rather than left for .NET to clean up later.
    /// Does nothing for a plain fill.
    /// </summary>
    internal static void Release(FrameworkElement? fill)
    {
        if (fill is not SystemBackdropElement element)
        {
            return;
        }

        try
        {
            if (element.SystemBackdrop is not null)
            {
                element.SystemBackdrop = null;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("An acrylic fill could not be detached.", ex);
        }
    }
}
