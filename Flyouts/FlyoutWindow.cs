using System;
using System.Collections.Generic;
using System.Globalization;
using Machitan.Config;
using Machitan.Core;
using Machitan.Interop;
using Machitan.Shell;
using Machitan.Visuals;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using VirtualKey = Windows.System.VirtualKey;

namespace Machitan.Flyouts;

/// <summary>Where a flyout opens.</summary>
internal enum FlyoutPlacement
{
    /// <summary>Next to the bar, lined up with it exactly like Start (see PopupPlacement).</summary>
    BesideBar,

    /// <summary>In the middle of the screen.</summary>
    ScreenCentre,
}

/// <summary>
/// The shared base of Machitan's flyouts: a see-through window (like Start) showing one or
/// more rounded boxes, each with the same fill as the bar (acrylic or a translucent colour).
/// It closes on Esc and when it loses focus (a click anywhere else), follows the theme
/// setting, and is never shown in Alt+Tab or on the bar.
/// </summary>
public abstract partial class FlyoutWindow : Window
{
    private protected const double PadDip = 4;
    private protected const double GapToBarDip = 8;
    private protected const double BoxRadius = 16;
    private protected const double SmallRadius = 8;

    private protected static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private readonly WindowCutter _cutter;
    private readonly List<(Grid Box, double Radius)> _boxes = new();
    private readonly string _name;
    private WindowSubclass? _subclass;
    private string _fillLook = string.Empty;

    private protected FlyoutWindow(ShellHost host, string name)
    {
        Host = host;
        _name = name;
        Title = "Machitan " + name;

        // The 4 DIP padding keeps the window's edge outside the cut, like the bar and Start.
        Root = new Grid
        {
            Padding = new Thickness(PadDip),
            Background = new SolidColorBrush(Colors.Transparent),
        };
        Content = Root;

        Hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        SeeThrough.Prepare(this, Hwnd, alwaysOnTop: true);
        _subclass = new WindowSubclass(Hwnd, OnWindowMessage);

        _cutter = new WindowCutter(Hwnd, Root);
        Root.LayoutUpdated += (_, _) => _cutter.Update();
        Root.PreviewKeyDown += OnRootPreviewKeyDown;

        Activated += OnActivated;
        Host.Settings.Changed += OnSettingsChanged;
        Closed += (_, _) =>
        {
            Host.Settings.Changed -= OnSettingsChanged;
            _subclass?.Dispose();
            _subclass = null;
        };
    }

    internal bool IsOpen { get; private set; }

    private protected ShellHost Host { get; }

    private protected nint Hwnd { get; }

    /// <summary>The window's content; derived flyouts put their boxes in it.</summary>
    private protected Grid Root { get; }

    /// <summary>True when the dark look applies (from the theme setting, or Windows' when "system").</summary>
    private protected bool IsDark { get; private set; } = true;

    /// <summary>Closes the flyout (does nothing when it is already closed).</summary>
    internal void HideFlyout()
    {
        if (!IsOpen)
        {
            return;
        }

        IsOpen = false;
        try
        {
            OnHiding();
        }
        catch (Exception ex)
        {
            Log.Error(_name + ": closing failed.", ex);
        }

        User32.ShowWindow(Hwnd, User32.SW_HIDE);
        Log.Info(_name + " closed.");
    }

    /// <summary>
    /// A rounded box with the bar's fill behind <paramref name="content"/>. The window is cut
    /// to every box made here; the space between boxes shows the wallpaper.
    /// </summary>
    private protected Grid MakeBox(UIElement content, double radius)
    {
        var box = new Grid { CornerRadius = new CornerRadius(radius) };
        box.Children.Add(content);
        _boxes.Add((box, radius));
        _cutter.Add(box, radius);
        ApplyLook(force: true);
        return box;
    }

    /// <summary>
    /// Sizes and places the window for content of the given size (in DIP, without the
    /// padding), then shows it and gives it the keyboard. Also used to resize an open flyout.
    /// </summary>
    private protected void Present(double contentWidthDip, double contentHeightDip, FlyoutPlacement placement)
    {
        double scale = Scale();
        int pad = (int)Math.Round(PadDip * scale);
        int gap = (int)Math.Round(GapToBarDip * scale);
        int width = (int)Math.Round((contentWidthDip + (2 * PadDip)) * scale);
        int height = (int)Math.Round((contentHeightDip + (2 * PadDip)) * scale);

        AppWindow.MoveAndResize(placement == FlyoutPlacement.BesideBar
            ? PopupPlacement.BesideBar(Host, width, height, pad, gap)
            : PopupPlacement.ScreenCentre(width, height));
        _cutter.Invalidate();

        if (IsOpen)
        {
            return;
        }

        User32.ShowWindow(Hwnd, User32.SW_SHOW);
        Activate();

        // Opened by a shortcut caught in the keyboard hook, Windows may not hand over the
        // keyboard by itself.
        WindowActivator.BringToFront(Hwnd);
        IsOpen = true;
        Log.Info(_name + " opened.");
    }

    /// <summary>The largest content height (DIP) that fits on the screen with room to spare.</summary>
    private protected double ScreenHeightDip(double fraction)
    {
        RectInt32 screen = DisplayArea.Primary.OuterBounds;
        return screen.Height / Scale() * fraction;
    }

    private protected double Scale()
    {
        uint dpi = User32.GetDpiForWindow(Hwnd);
        return (dpi == 0 ? 96 : dpi) / 96.0;
    }

    /// <summary>A key pressed anywhere in the flyout, before its controls see it. Set Handled to keep it.</summary>
    private protected virtual void OnPreviewKey(KeyRoutedEventArgs e)
    {
    }

    /// <summary>Runs as the flyout closes (stop timers and the like).</summary>
    private protected virtual void OnHiding()
    {
    }

    /// <summary>Runs after any settings change (from Settings, a flyout, or a hand edit), once theme and fills are applied.</summary>
    private protected virtual void OnSettingsApplied(ShellSettings settings)
    {
    }

    private protected static Button MakeTextButton(string text, Action onClick)
    {
        var button = new Button
        {
            Content = text,
            Height = 30,
            Padding = new Thickness(12, 0, 12, 0),
            CornerRadius = new CornerRadius(SmallRadius),
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(button, text);
        button.Click += (_, _) => onClick();
        return button;
    }

    private protected static FontIcon MakeGlyph(int codePoint, double size)
    {
        return new FontIcon
        {
            Glyph = ((char)codePoint).ToString(),
            FontFamily = IconFont,
            FontSize = size,
        };
    }

    private void OnRootPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        try
        {
            OnPreviewKey(e);
        }
        catch (Exception ex)
        {
            Log.Error(_name + ": a key could not be handled.", ex);
        }

        if (!e.Handled && e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            HideFlyout();
        }
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        // A click anywhere else closes the flyout.
        if (args.WindowActivationState == WindowActivationState.Deactivated && IsOpen)
        {
            HideFlyout();
        }
    }

    private bool OnWindowMessage(uint message, nint wParam, nint lParam, out nint result)
    {
        return SeeThrough.TryHandleErase(Hwnd, message, wParam, out result);
    }

    private void OnSettingsChanged()
    {
        ApplyLook(force: false);
        try
        {
            OnSettingsApplied(Host.Settings.Current);
        }
        catch (Exception ex)
        {
            Log.Error(_name + ": a settings change could not be applied.", ex);
        }
    }

    /// <summary>Theme and fills. Fills are replaced only when their look changes (see SurfaceFill.Release).</summary>
    private void ApplyLook(bool force)
    {
        ShellSettings settings = Host.Settings.Current;
        Root.RequestedTheme = settings.Theme switch
        {
            ThemeMode.Dark => ElementTheme.Dark,
            ThemeMode.Light => ElementTheme.Light,
            _ => ElementTheme.Default,
        };

        IsDark = settings.Theme switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,
            _ => Application.Current.RequestedTheme == ApplicationTheme.Dark,
        };

        string fillLook = settings.EnableAcrylic + "|" + settings.Opacity.ToString(CultureInfo.InvariantCulture) + "|" + IsDark;
        if (!force && fillLook == _fillLook)
        {
            return;
        }

        _fillLook = fillLook;
        foreach ((Grid box, double radius) in _boxes)
        {
            bool hasFill = box.Children.Count > 0 && box.Children[0] is FrameworkElement { Tag: "fill" };
            if (force && hasFill)
            {
                // A new box joined; the others already have the current fill.
                continue;
            }

            if (hasFill && box.Children[0] is FrameworkElement old)
            {
                // Detached on the UI thread before it is dropped.
                SurfaceFill.Release(old);
                box.Children.RemoveAt(0);
            }

            FrameworkElement fill = SurfaceFill.Create(settings, IsDark, radius);
            fill.Tag = "fill";
            box.Children.Insert(0, fill);
        }
    }
}
