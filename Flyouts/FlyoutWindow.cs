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
    /// <summary>
    /// Next to the bar, lined up with it exactly like Start (see PopupPlacement); or, for a
    /// flyout that belongs to a corner widget (WidgetAnchor) while that widget is shown, next to the widget.
    /// </summary>
    BesideBar,

    /// <summary>In the middle of the screen.</summary>
    ScreenCentre,
}

/// <summary>
/// The shared base of Machitan's flyouts: a see-through window (like Start) showing one or
/// more rounded boxes, each with the same fill as the bar (acrylic or a translucent colour).
/// It closes on Esc and when it loses focus (a click anywhere else), follows the theme
/// setting, and is never shown in Alt+Tab or on the bar.
/// A flyout made with takesFocus false (the window previews) never takes the keyboard or
/// becomes the active window: it shows without activating and is closed by its owner.
/// </summary>
public abstract partial class FlyoutWindow : Window
{
    private protected const double PadDip = 4;
    private protected const double GapToBarDip = 8;
    private protected const double BoxRadius = Radii.Large;
    private protected const double SmallRadius = Radii.Small;

    private protected static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    // Opening and closing, for flyouts that name what moves (MotionTarget): a short rise from
    // the bar's side with a fade, and a quicker fade back towards it (Visuals/Motion.cs).
    private const float OpenFromDip = 16;
    private const int OpenMs = 200;
    private const float CloseToDip = 8;
    private const int CloseMs = 120;

    /// <summary>A click on the opener this soon after a click elsewhere closed the flyout is that same click.</summary>
    private const long SameClickMs = 500;

    private readonly WindowCutter _cutter;
    private readonly List<(Grid Box, double Radius)> _boxes = new();
    private readonly string _name;
    private readonly bool _takesFocus;
    private WindowSubclass? _subclass;
    private string _fillLook = string.Empty;
    private long _closedByFocusLossAt;
    private POINT _closedByFocusLossWhere;

    /// <summary>True: opened below a widget in the top half (it moves from above); false: from below; null: by the bar.</summary>
    private bool? _fromTop;

    private protected FlyoutWindow(ShellHost host, string name, bool takesFocus = true)
    {
        Host = host;
        _name = name;
        _takesFocus = takesFocus;
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
        if (!takesFocus)
        {
            // Clicks work, but the window never becomes the active one.
            long exStyle = User32.GetWindowLongPtrW(Hwnd, User32.GWL_EXSTYLE);
            User32.SetWindowLongPtrW(Hwnd, User32.GWL_EXSTYLE, (nint)(exStyle | User32.WS_EX_NOACTIVATE));
        }

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

    /// <summary>
    /// True once, just after a click on <paramref name="opener"/> (its place on screen, physical
    /// pixels) closed the flyout. A press on the thing that opens it (the bar's ^, a widget)
    /// takes the focus first, which closes the flyout, and its click arrives after: that click
    /// means "close", so the caller leaves the flyout shut.
    /// </summary>
    internal bool TakeClosingClick(RECT opener)
    {
        if (_closedByFocusLossAt == 0 || Environment.TickCount64 - _closedByFocusLossAt >= SameClickMs
            || !opener.Contains(_closedByFocusLossWhere, 2))
        {
            return false;
        }

        _closedByFocusLossAt = 0;
        return true;
    }

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

        NoteOpenChanged(false);

        UIElement? target = MotionTarget;
        if (target is not null && Motion.IsOn(Host.Settings.Current) && User32.IsWindowVisible(Hwnd))
        {
            // Hidden once it has faded; nothing in it takes a click meanwhile.
            target.IsHitTestVisible = false;
            _cutter.Suspend();
            Motion.Leave(target, FromBarSide(CloseToDip), CloseMs, () =>
            {
                if (!IsOpen)
                {
                    User32.ShowWindow(Hwnd, User32.SW_HIDE);
                }
            });
        }
        else
        {
            User32.ShowWindow(Hwnd, User32.SW_HIDE);
        }

        if (_takesFocus)
        {
            Log.Info(_name + " closed.");
        }
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

        if (placement == FlyoutPlacement.BesideBar && WidgetAnchor is RECT widget && widget.Width > 0 && widget.Height > 0)
        {
            _fromTop = PopupPlacement.IsInTopHalf(widget);
            PresentAt(PopupPlacement.NextToWidget(widget, width, height, pad, gap));
            return;
        }

        _fromTop = null;
        PresentAt(placement == FlyoutPlacement.BesideBar
            ? PopupPlacement.BesideBar(Host, width, height, pad, gap)
            : PopupPlacement.ScreenCentre(width, height));
    }

    /// <summary>Places the window at an exact spot (physical pixels, padding included) and shows it.</summary>
    private protected void PresentAt(RectInt32 windowRect)
    {
        AppWindow.MoveAndResize(windowRect);
        _cutter.Invalidate();

        if (IsOpen)
        {
            return;
        }

        // Animated: the cut is lifted while the content moves (else its edges would clip),
        // and the motion starts before the window shows, so its first frame is already the
        // start of it.
        UIElement? target = MotionTarget;
        if (target is not null)
        {
            target.IsHitTestVisible = true;
            if (Motion.IsOn(Host.Settings.Current))
            {
                _cutter.Suspend();
                Action settled = () =>
                {
                    if (IsOpen)
                    {
                        _cutter.Resume();
                    }
                };

                // Still on screen means it was closing: it turns back from where it is.
                if (User32.IsWindowVisible(Hwnd))
                {
                    Motion.Return(target, OpenMs, settled);
                }
                else
                {
                    Motion.Arrive(target, FromBarSide(OpenFromDip), OpenMs, settled);
                }
            }
            else
            {
                Motion.Reset(target);
                _cutter.Resume();
            }
        }

        if (_takesFocus)
        {
            User32.ShowWindow(Hwnd, User32.SW_SHOW);
            Activate();

            // Opened by a shortcut caught in the keyboard hook, Windows may not hand over the
            // keyboard by itself.
            WindowActivator.BringToFront(Hwnd);
        }
        else
        {
            AppWindow.Show(false);
        }

        IsOpen = true;
        _closedByFocusLossAt = 0;
        if (_takesFocus)
        {
            Log.Info(_name + " opened.");
        }

        NoteOpenChanged(true);
    }

    /// <summary>
    /// What moves as the flyout opens and closes; null (the default) for none. Settings >
    /// General > Turn off animations (or Windows' own setting) stops it.
    /// </summary>
    private protected virtual UIElement? MotionTarget => null;

    /// <summary>
    /// The corner widget this flyout opens by (its shape on screen, physical pixels), or null
    /// (the default) to open by the bar. Asked each time the flyout is placed.
    /// </summary>
    private protected virtual RECT? WidgetAnchor => null;

    /// <summary>
    /// A distance towards where the flyout opened from: the bar (down for a bottom bar, up for
    /// a top bar), or the widget it opened by (up for one in the top half).
    /// </summary>
    private float FromBarSide(float distanceDip)
    {
        bool fromTop = _fromTop ?? Host.Settings.Current.DockEdge == DockEdge.Top;
        return fromTop ? -distanceDip : distanceDip;
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

    /// <summary>Runs once the flyout has opened, and as it closes (after OnHiding).</summary>
    private protected virtual void OnOpenChanged(bool open)
    {
    }

    /// <summary>
    /// True while the flyout stays open although another window took the focus (the tray box
    /// while an app's menu from it is open); it then closes itself.
    /// </summary>
    private protected virtual bool KeepOpenWhenDeactivated => false;

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
        if (_takesFocus && args.WindowActivationState == WindowActivationState.Deactivated && IsOpen && !KeepOpenWhenDeactivated)
        {
            HideFlyout();
            if (User32.GetCursorPos(out POINT at))
            {
                _closedByFocusLossAt = Environment.TickCount64;
                _closedByFocusLossWhere = at;
            }
        }
    }

    private void NoteOpenChanged(bool open)
    {
        try
        {
            OnOpenChanged(open);
        }
        catch (Exception ex)
        {
            Log.Warn(_name + ": a part that follows it opening and closing failed.", ex);
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
