using System;
using Machitan.Config;
using Machitan.Interop;
using Microsoft.UI.Windowing;
using Windows.Graphics;

namespace Machitan.Shell;

/// <summary>
/// Where Start and the flyouts open, so they all line up with the bar the same way.
/// All values in physical pixels. The windows are their visible boxes plus "pad" on each
/// side (the see-through margin), which is why pad appears in the sums.
/// </summary>
internal static class PopupPlacement
{
    /// <summary>
    /// Next to the bar (above it, or below a top bar, with "gap" between), lined up the way
    /// the bar is aligned: left edges, centres, or right edges. Classic is full width with
    /// Start at its left end, so pop-ups sit at the left there.
    /// </summary>
    internal static RectInt32 BesideBar(ShellHost host, int width, int height, int pad, int gap)
    {
        RectInt32 screen = DisplayArea.Primary.OuterBounds;
        RECT bar = host.BarRect;
        ShellSettings settings = host.Settings.Current;
        bool top = settings.DockEdge == DockEdge.Top;

        int x;
        int y;
        if (bar.Width > 0)
        {
            DockAlignment alignment = settings.TaskbarMode == TaskbarMode.Classic ? DockAlignment.Left : settings.DockAlignment;
            x = alignment switch
            {
                DockAlignment.Center => ((bar.Left + bar.Right) / 2) - (width / 2),
                DockAlignment.Right => bar.Right + pad - width,
                _ => bar.Left - pad,
            };
            y = top ? bar.Bottom + gap - pad : bar.Top - gap - height + pad;
        }
        else
        {
            x = screen.X + ((screen.Width - width) / 2);
            y = screen.Y + screen.Height - height - gap;
        }

        return Clamp(screen, x, y, width, height);
    }

    /// <summary>
    /// Next to the bar like <see cref="BesideBar"/>, but with the window's left edge at
    /// <paramref name="left"/> (physical pixels, padding included), e.g. lined up with a box on the bar.
    /// </summary>
    internal static RectInt32 BesideBarFrom(ShellHost host, int left, int width, int height, int pad, int gap)
    {
        RectInt32 screen = DisplayArea.Primary.OuterBounds;
        RECT bar = host.BarRect;
        bool top = host.Settings.Current.DockEdge == DockEdge.Top;
        int y = bar.Width > 0
            ? (top ? bar.Bottom + gap - pad : bar.Top - gap - height + pad)
            : screen.Y + screen.Height - height - gap;
        return Clamp(screen, left, y, width, height);
    }

    /// <summary>In the middle of the screen.</summary>
    internal static RectInt32 ScreenCentre(int width, int height)
    {
        RectInt32 screen = DisplayArea.Primary.OuterBounds;
        int x = screen.X + ((screen.Width - width) / 2);
        int y = screen.Y + ((screen.Height - height) / 2);
        return Clamp(screen, x, y, width, height);
    }

    private static RectInt32 Clamp(RectInt32 screen, int x, int y, int width, int height)
    {
        x = Math.Clamp(x, screen.X, Math.Max(screen.X, screen.X + screen.Width - width));
        y = Math.Clamp(y, screen.Y, Math.Max(screen.Y, screen.Y + screen.Height - height));
        return new RectInt32(x, y, width, height);
    }
}
