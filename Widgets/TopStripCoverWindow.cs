using System;
using Machitan.Config;
using Machitan.Core;
using Machitan.Interop;
using Machitan.Shell;
using Machitan.Visuals;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Color = Windows.UI.Color;

namespace Machitan.Widgets;

/// <summary>
/// The top strip cover (optional, off by default): one flat colour over the strip Machitan
/// keeps at the top of the screen, like a menu bar's background, in dark, light or Windows'
/// accent colour, as solid as the opacity setting says. Shown only while the strip is kept
/// and no full-screen app is in front. Always on top, but the corner widgets and Dynaland
/// are put back above it whenever it moves; it never takes focus. See-through window like
/// the others (no acrylic): the colour's transparency shows what is behind.
/// </summary>
public sealed partial class TopStripCoverWindow : Window
{
    private const double Pad = 4;

    private readonly nint _hwnd;
    private readonly Grid _root;
    private readonly SolidColorBrush _brush = new(Colors.Transparent);
    private readonly WindowCutter _cutter;
    private readonly Windows.UI.ViewManagement.UISettings _ui = new();
    private WindowSubclass? _subclass;
    private RECT _placed;
    private bool _shown;

    internal TopStripCoverWindow()
    {
        Title = "Machitan top strip cover";

        // The 4 DIP pad keeps the window's own edge outside the cut, like the other windows.
        _root = new Grid
        {
            Padding = new Thickness(Pad),
            Background = new SolidColorBrush(Colors.Transparent),
        };
        Content = _root;

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        SeeThrough.Prepare(this, _hwnd, alwaysOnTop: true);
        long exStyle = User32.GetWindowLongPtrW(_hwnd, User32.GWL_EXSTYLE);
        User32.SetWindowLongPtrW(_hwnd, User32.GWL_EXSTYLE, (nint)(exStyle | User32.WS_EX_NOACTIVATE));
        _subclass = new WindowSubclass(_hwnd, (uint message, nint wParam, nint lParam, out nint result) =>
            SeeThrough.TryHandleErase(_hwnd, message, wParam, out result));

        var fill = new Border { Background = _brush };
        _root.Children.Add(fill);

        _cutter = new WindowCutter(_hwnd, _root);
        _cutter.Add(fill, 0);
        _root.LayoutUpdated += (_, _) => _cutter.Update();

        Closed += (_, _) =>
        {
            _subclass?.Dispose();
            _subclass = null;
        };
    }

    internal bool IsShown => _shown;

    /// <summary>The colour on screen now, for the widgets that choose their ink by what is behind them.</summary>
    internal CoverTint Tint => new(_brush.Color.R, _brush.Color.G, _brush.Color.B, _brush.Color.A / 255.0);

    /// <summary>
    /// Shows the cover over <paramref name="strip"/> (physical pixels) in the settings' colour.
    /// Returns true when it appeared or moved, so the widgets can be put back above it.
    /// </summary>
    internal bool ShowOver(RECT strip, ShellSettings settings, double scale)
    {
        Color colour = settings.TopStripCoverColour switch
        {
            StripCoverColour.Light => Colors.White,
            StripCoverColour.Accent => Accent(),
            _ => Colors.Black,
        };
        byte alpha = (byte)Math.Round(Math.Clamp(settings.TopStripCoverOpacity, 0, 1) * 255);
        Color wanted = ColorHelper.FromArgb(alpha, colour.R, colour.G, colour.B);
        if (!_brush.Color.Equals(wanted))
        {
            _brush.Color = wanted;
        }

        int pad = (int)Math.Round(Pad * scale);
        var window = new RECT
        {
            Left = strip.Left - pad,
            Top = strip.Top - pad,
            Right = strip.Right + pad,
            Bottom = strip.Bottom + pad,
        };

        bool changed = false;
        if (!window.SameAs(_placed))
        {
            _placed = window;
            User32.SetWindowPos(_hwnd, User32.HWND_TOPMOST, window.Left, window.Top, window.Width, window.Height, User32.SWP_NOACTIVATE);
            _cutter.Invalidate();
            changed = true;
        }

        if (!_shown)
        {
            _shown = true;
            AppWindow.Show(false);
            Log.Info("Top strip cover shown.");
            changed = true;
        }

        return changed;
    }

    internal void HideCover()
    {
        if (!_shown)
        {
            return;
        }

        _shown = false;
        User32.ShowWindow(_hwnd, User32.SW_HIDE);
    }

    private Color Accent()
    {
        try
        {
            return _ui.GetColorValue(Windows.UI.ViewManagement.UIColorType.Accent);
        }
        catch (Exception)
        {
            return ColorHelper.FromArgb(255, 76, 194, 255);
        }
    }
}
