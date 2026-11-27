using System;
using Machitan.Config;
using Machitan.Core;
using Machitan.Custom;
using Machitan.Interop;
using Machitan.Shell;
using Machitan.Visuals;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace Machitan.Widgets;

/// <summary>
/// The shared base of the corner widgets (the info line and the status ring): a see-through,
/// always-on-top window in one corner of the bar's screen that never takes focus and never
/// shows in Alt+Tab or on the bar. It keeps itself in place (and on top) every 2 seconds,
/// follows screen and work-area changes, steps aside while a full-screen app is in front,
/// and leaves the bottom bar's screen reservation alone (it only reads the work area).
/// The window has a fixed size; the visible shape inside it sits against the corner and the
/// window is cut to that shape, so the rest of the window lets clicks through.
/// </summary>
public abstract partial class CornerWidgetWindow : Window
{
    /// <summary>
    /// Every widget is one line this tall (DIP): the same as Dynaland's small pill, so a widget
    /// in a top corner lines up with the island-shaped pill (Sam, 2026-11-18).
    /// </summary>
    internal const double LineHeightDip = 32;

    /// <summary>Space between a widget and the screen's top or bottom edge (DIP): the island's gap.</summary>
    internal const double EdgeMarginDip = 8;

    /// <summary>
    /// Space between a widget and the screen's left or right side (DIP): the bar's, so a widget
    /// and a bar aligned to the same side line up (Sam, 2026-11-18).
    /// </summary>
    internal const double SideMarginDip = 16;

    /// <summary>Space between two widgets in one corner (DIP).</summary>
    internal const double StackGapDip = 8;

    private protected const double PadDip = 4;

    private readonly string _name;
    private readonly double _widthDip;
    private readonly WindowCutter _cutter;

    /// <summary>The window's content: the widget at its standard size, scaled as a whole (size setting).</summary>
    private readonly Canvas _canvas;
    private readonly ScaleTransform _zoom = new();
    private double _size = 1;
    private readonly DispatcherQueueTimer _heal;
    private WindowSubclass? _subclass;
    private RECT _placed;
    private bool _started;
    private bool _closed;

    /// <summary>Custom mode: on top of the widget, takes its clicks (pick) and right-clicks (black menu).</summary>
    private Border? _shield;
    private MenuFlyout? _customMenu;
    private bool _onLeft = true;
    private bool _onTop = true;

    /// <param name="widthDip">The window's fixed content width; the widget's shape is never wider.</param>
    private protected CornerWidgetWindow(ShellHost host, string name, double widthDip)
    {
        Host = host;
        _name = name;
        _widthDip = widthDip;
        Title = "Machitan " + name;

        // The widget is laid out at its standard size and scaled as a whole by its size
        // setting; the canvas lets the scaled widget fill the window without layout clipping.
        Root = new Grid
        {
            Padding = new Thickness(PadDip),
            Background = new SolidColorBrush(Colors.Transparent),
            Width = widthDip + (2 * PadDip),
            Height = LineHeightDip + (2 * PadDip),
            RenderTransform = _zoom,
        };
        _canvas = new Canvas { Background = new SolidColorBrush(Colors.Transparent) };
        _canvas.Children.Add(Root);
        Content = _canvas;

        Hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        SeeThrough.Prepare(this, Hwnd, alwaysOnTop: true);

        // Clicks work, but the window never becomes the active one (the system panels a
        // click opens get the keyboard, not this window).
        long exStyle = User32.GetWindowLongPtrW(Hwnd, User32.GWL_EXSTYLE);
        User32.SetWindowLongPtrW(Hwnd, User32.GWL_EXSTYLE, (nint)(exStyle | User32.WS_EX_NOACTIVATE));

        _subclass = new WindowSubclass(Hwnd, OnWindowMessage);
        _cutter = new WindowCutter(Hwnd, _canvas);
        Root.LayoutUpdated += (_, _) => _cutter.Update();

        _heal = DispatcherQueue.CreateTimer();
        _heal.Interval = TimeSpan.FromSeconds(2);
        _heal.Tick += (_, _) => Heal();

        Host.Settings.Changed += OnSettingsChanged;
        Host.FullScreenChanged += OnFullScreenChanged;
        Host.Custom.Changed += OnCustomChanged;
        Closed += (_, _) =>
        {
            _closed = true;
            _heal.Stop();
            Host.Settings.Changed -= OnSettingsChanged;
            Host.FullScreenChanged -= OnFullScreenChanged;
            Host.Custom.Changed -= OnCustomChanged;
            _subclass?.Dispose();
            _subclass = null;
            try
            {
                OnClosing();
            }
            catch (Exception ex)
            {
                Log.Error(_name + ": cleaning up failed.", ex);
            }
        };
    }

    /// <summary>True while the widget is on screen.</summary>
    internal bool IsShown { get; private set; }

    /// <summary>Which custom-mode piece this widget is.</summary>
    private protected abstract CustomPiece Piece { get; }

    /// <summary>Where the widget's shape is on screen (physical pixels), for custom mode's badge; null when hidden.</summary>
    internal RECT? ShapeScreenRect()
    {
        if (!IsShown || _closed || !User32.GetWindowRect(Hwnd, out RECT window))
        {
            return null;
        }

        uint dpi = User32.GetDpiForWindow(Hwnd);
        double scale = (dpi == 0 ? 96 : dpi) / 96.0;
        int pad = (int)Math.Round(PadDip * scale * _size);
        int width = (int)Math.Round(VisibleWidthDip * scale);
        int height = (int)Math.Round(LineHeightNowDip * scale);
        bool left = Corner is ScreenCorner.TopLeft or ScreenCorner.BottomLeft;
        int x = left ? window.Left + pad : window.Right - pad - width;
        return new RECT { Left = x, Top = window.Top + pad, Right = x + width, Bottom = window.Top + pad + height };
    }

    /// <summary>Custom mode on: a see-through cover takes the widget's clicks; off: it goes.</summary>
    private void OnCustomChanged()
    {
        if (_closed)
        {
            return;
        }

        bool on = Host.Custom.IsOn;
        if (on && _shield is null)
        {
            var shield = new Border { Background = new SolidColorBrush(Colors.Transparent) };
            Grid.SetRowSpan(shield, 16);
            Grid.SetColumnSpan(shield, 16);
            shield.Tapped += (_, e) =>
            {
                e.Handled = true;
                Host.Custom.Select(new CustomTarget(Piece));
            };
            shield.RightTapped += (_, e) =>
            {
                e.Handled = true;
                ShowCustomMenu(shield, e.GetPosition(shield));
            };
            _shield = shield;
            Root.Children.Add(shield);
        }
        else if (!on && _shield is not null)
        {
            Root.Children.Remove(_shield);
            _shield = null;
        }
    }

    private void ShowCustomMenu(FrameworkElement at, Windows.Foundation.Point point)
    {
        try
        {
            _customMenu ??= new MenuFlyout { ShouldConstrainToRootBounds = false };
            CustomMenus.Fill(_customMenu, Host.Custom, new CustomTarget(Piece));

            // The widget never takes focus by itself; for the menu it must, so a click elsewhere closes it.
            User32.SetForegroundWindow(Hwnd);
            _customMenu.ShowAt(at, new FlyoutShowOptions { Position = point });
        }
        catch (Exception ex)
        {
            Log.Error(_name + ": the custom mode menu could not be shown.", ex);
        }
    }

    /// <summary>
    /// True when the widget is meant to be on screen: on, with something to show. Unlike
    /// <see cref="IsShown"/> it stays true while a full-screen app hides it, so the top strip
    /// does not come and go with every video.
    /// </summary>
    internal bool WantsSpace => _started && !_closed && IsEnabled(Host.Settings.Current) && HasContent;

    /// <summary>The corner the settings put this widget in.</summary>
    internal ScreenCorner Corner => CornerOf(Host.Settings.Current);

    /// <summary>The widget's visible width now (DIP), for standing widgets side by side.</summary>
    internal double VisibleWidthDip => ShapeWidthDip() * _size;

    /// <summary>The widget's height on screen (DIP): the standard line times its size setting.</summary>
    internal double LineHeightNowDip => LineHeightDip * _size;

    /// <summary>The size setting as a factor (1 = standard).</summary>
    private protected double SizeFactor => _size;

    /// <summary>The visible shape's width (DIP); the whole window unless the widget says otherwise.</summary>
    private protected virtual double ShapeWidthDip() => _widthDip;

    /// <summary>The visible width changed: the widgets beside this one move.</summary>
    private protected void NoteShapeWidthChanged()
    {
        Host.QueueCornerLayout();
    }

    private protected ShellHost Host { get; }

    private protected nint Hwnd { get; }

    private protected Grid Root { get; }

    /// <summary>True when the dark look applies (from the theme setting, or Windows' when "system").</summary>
    private protected bool IsDark { get; private set; } = true;

    /// <summary>True when the corner is on the left (the shape then sits against the left edge).</summary>
    private protected bool OnLeft => _onLeft;

    /// <summary>The window's place on screen (physical pixels; empty before it is placed).</summary>
    private protected RECT PlacedRect => _placed;

    /// <summary>Starts the widget: shows it when its settings say so.</summary>
    internal void Start()
    {
        if (_started || _closed)
        {
            return;
        }

        _started = true;
        ApplySettings();
        _heal.Start();
    }

    /// <summary>Hides the widget for good (shutdown). Close it afterwards.</summary>
    internal void Stop()
    {
        _started = false;
        _heal.Stop();
        UpdateVisibility();
    }

    /// <summary>
    /// Puts the window in its corner of the bar's screen: 8 DIP from the top or bottom edge and
    /// 16 DIP (the bar's margin) from the side; widgets sharing a corner stand side by side, 8 DIP
    /// apart. At the free edge they sit in Machitan's strip when one is kept. On the bar's own
    /// edge they sit in the bar's band, centred on the bar: in the corner when it is clear of
    /// the bar, or right after the bar when the bar is aligned to that side; with no room
    /// beside the bar, just past it (8 DIP). Moves it only when needed.
    /// </summary>
    internal void Place()
    {
        if (!IsShown || _closed)
        {
            return;
        }

        try
        {
            nint bar = Host.BarHandle;
            if (!User32.TryGetWorkArea(bar != 0 ? bar : Hwnd, out RECT work))
            {
                return;
            }

            uint dpi = User32.GetDpiForWindow(bar != 0 ? bar : Hwnd);
            double scale = (dpi == 0 ? 96 : dpi) / 96.0;

            ScreenCorner corner = Corner;
            bool left = corner is ScreenCorner.TopLeft or ScreenCorner.BottomLeft;
            bool top = corner is ScreenCorner.TopLeft or ScreenCorner.TopRight;

            // The widget's own sizes scale with its size setting; the margins do not.
            double sized = scale * _size;
            int pad = (int)Math.Round(PadDip * sized);
            int width = (int)Math.Round((_widthDip + (2 * PadDip)) * sized);
            int height = (int)Math.Round((LineHeightDip + (2 * PadDip)) * sized);
            int line = (int)Math.Round(LineHeightDip * sized);
            int edge = (int)Math.Round(EdgeMarginDip * scale);
            int side = (int)Math.Round(SideMarginDip * scale);
            int gap = (int)Math.Round(StackGapDip * scale);

            // The widgets of a corner stand side by side in one row: "start" is where the row
            // begins (its left end in a left corner, its right end in a right corner), "offset"
            // how far along the row this widget is, "row" the whole row's width.
            int offset = (int)Math.Round(Host.WidthBefore(this) * scale);
            int row = (int)Math.Round(Host.CornerRowWidth(corner) * scale);
            // Beside a docked sidebar on this side, or at the screen's edge past it while it
            // leaves room for the indicators (ShellHost.CornerSideEdge).
            int start = left ? Host.CornerSideEdge(true, work.Left) + side : Host.CornerSideEdge(false, work.Right) - side;

            // Visible top of the widgets' row (the window starts one pad above it).
            int lineTop;
            RECT barRect = Host.BarRect;
            if (Host.BarOnEdge(top))
            {
                int bandTop = ((barRect.Top + barRect.Bottom) / 2) - (line / 2);
                bool clear = left ? start + row + gap <= barRect.Left : start - row - gap >= barRect.Right;
                if (clear)
                {
                    // The corner is clear of the bar: in the bar's band, centred on it.
                    lineTop = bandTop;
                }
                else if (FitsAfterBar(left, barRect, start, row, gap, work, side, out int afterBar))
                {
                    // The bar is aligned to this side: the row continues the bar's own row.
                    lineTop = bandTop;
                    start = afterBar;
                }
                else
                {
                    // No room beside the bar (Classic, or a very wide bar): just past it.
                    lineTop = top ? barRect.Bottom + gap : barRect.Top - gap - line;
                }
            }
            else
            {
                // The free edge: in Machitan's strip when one is kept there, else the work area.
                RECT? strip = Host.StripOn(top);
                lineTop = top
                    ? (strip?.Top ?? work.Top) + edge
                    : (strip?.Bottom ?? work.Bottom) - edge - line;
            }

            // The shape sits against the corner's side of its window (see OnCornerChanged).
            int x = left ? start + offset - pad : start - offset + pad - width;
            int y = lineTop - pad;
            var rect = new RECT { Left = x, Top = y, Right = x + width, Bottom = y + height };

            if (left != _onLeft || top != _onTop)
            {
                _onLeft = left;
                _onTop = top;
                OnCornerChanged(left);
            }

            if (rect.SameAs(_placed))
            {
                return;
            }

            _placed = rect;
            User32.SetWindowPos(Hwnd, User32.HWND_TOPMOST, x, y, width, height, User32.SWP_NOACTIVATE);
            _cutter.Invalidate();
            OnPlaced();
        }
        catch (Exception ex)
        {
            Log.Error(_name + ": placing it failed.", ex);
        }
    }

    /// <summary>
    /// True when the bar is aligned to this corner's side and the corner's row fits after it in
    /// the bar's band: before Dynaland when Dynaland sits in that band too, else before the
    /// other side's margin. <paramref name="afterBar"/> is where the row then begins.
    /// </summary>
    private bool FitsAfterBar(bool left, RECT bar, int corner, int row, int gap, RECT work, int side, out int afterBar)
    {
        afterBar = left ? bar.Right + gap : bar.Left - gap;
        bool barOnThisSide = left ? bar.Left <= corner + gap : bar.Right >= corner - gap;
        if (!barOnThisSide)
        {
            return false;
        }

        if (Host.DynalandZoneInBarBand is (int zoneLeft, int zoneRight))
        {
            return left ? afterBar + row + gap <= zoneLeft : afterBar - row - gap >= zoneRight;
        }

        return left ? afterBar + row <= work.Right - side : afterBar - row >= work.Left + side;
    }

    /// <summary>Back on top of the other always-on-top windows (above the top strip cover).</summary>
    internal void KeepOnTop()
    {
        if (IsShown && !_closed)
        {
            User32.SetWindowPos(Hwnd, User32.HWND_TOPMOST, 0, 0, 0, 0,
                User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
        }
    }

    /// <summary>Adds a shape the window is cut to (the visible widget).</summary>
    private protected void AddShape(FrameworkElement shape, double radiusDip)
    {
        _cutter.Add(shape, radiusDip);
    }

    /// <summary>Shows or hides the widget to match its settings, content and full-screen apps.</summary>
    private protected void UpdateVisibility()
    {
        ShellSettings settings = Host.Settings.Current;
        bool want = _started && !_closed && IsEnabled(settings) && HasContent && !Host.FullScreenAppInFront;
        if (want == IsShown)
        {
            return;
        }

        IsShown = want;
        if (want)
        {
            _placed = default;
            Place();
            AppWindow.Show(false);
            User32.SetWindowPos(Hwnd, User32.HWND_TOPMOST, 0, 0, 0, 0,
                User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
        }
        else
        {
            User32.ShowWindow(Hwnd, User32.SW_HIDE);
        }

        try
        {
            OnShownChanged(want);
        }
        catch (Exception ex)
        {
            Log.Error(_name + ": starting or stopping its updates failed.", ex);
        }

        // A widget sharing the corner moves up or down.
        Host.LayoutCorners();
    }

    /// <summary>The widget's on/off setting.</summary>
    private protected abstract bool IsEnabled(ShellSettings settings);

    /// <summary>The widget's corner setting.</summary>
    private protected abstract ScreenCorner CornerOf(ShellSettings settings);

    /// <summary>The widget's size setting as a factor (1 = standard).</summary>
    private protected virtual double SizeOf(ShellSettings settings) => 1;

    /// <summary>False when there is nothing to show (the widget then hides).</summary>
    private protected virtual bool HasContent => true;

    /// <summary>After any settings change, with <see cref="IsDark"/> already updated.</summary>
    private protected virtual void OnSettingsApplied(ShellSettings settings)
    {
    }

    /// <summary>The widget appeared or disappeared: start or stop its updates.</summary>
    private protected virtual void OnShownChanged(bool shown)
    {
    }

    /// <summary>The corner moved between the left and the right side.</summary>
    private protected virtual void OnCornerChanged(bool left)
    {
    }

    /// <summary>The window moved or changed size on screen (see <see cref="PlacedRect"/>).</summary>
    private protected virtual void OnPlaced()
    {
    }

    /// <summary>Windows reported a new wallpaper.</summary>
    private protected virtual void OnWallpaperChanged()
    {
    }

    /// <summary>The scale of the window's screen (1 at 100 %).</summary>
    private protected double Scale()
    {
        uint dpi = User32.GetDpiForWindow(Hwnd);
        return (dpi == 0 ? 96 : dpi) / 96.0;
    }

    /// <summary>The window closed: release what the widget holds.</summary>
    private protected virtual void OnClosing()
    {
    }

    private void ApplySettings()
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

        double size = SizeOf(settings);
        if (size != _size)
        {
            _size = size;
            _zoom.ScaleX = size;
            _zoom.ScaleY = size;
            _cutter.RadiusScale = size;
            _cutter.Invalidate();
            _placed = default;
        }

        try
        {
            OnSettingsApplied(settings);
        }
        catch (Exception ex)
        {
            Log.Error(_name + ": a settings change could not be applied.", ex);
        }

        UpdateVisibility();
        Host.LayoutCorners();
    }

    private void OnSettingsChanged()
    {
        if (!_closed)
        {
            ApplySettings();
        }
    }

    private void OnFullScreenChanged()
    {
        if (!_closed)
        {
            UpdateVisibility();
        }
    }

    /// <summary>Every 2 seconds: back in place and back on top (other always-on-top windows can cover it).</summary>
    private void Heal()
    {
        if (_closed)
        {
            return;
        }

        UpdateVisibility();

        // Also keeps the top strip right (a widget can want space while a full-screen app hides it).
        Host.LayoutCorners();
        if (!IsShown)
        {
            return;
        }

        User32.SetWindowPos(Hwnd, User32.HWND_TOPMOST, 0, 0, 0, 0,
            User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
    }

    private bool OnWindowMessage(uint message, nint wParam, nint lParam, out nint result)
    {
        if (message == User32.WM_DISPLAYCHANGE
            || message == User32.WM_DPICHANGED
            || (message == User32.WM_SETTINGCHANGE && wParam == (nint)User32.SPI_SETWORKAREA))
        {
            // The screen or its free area changed: place again once Windows is done.
            DispatcherQueue.TryEnqueue(() =>
            {
                _placed = default;
                Place();
            });
        }
        else if (message == User32.WM_SETTINGCHANGE && wParam == (nint)User32.SPI_SETDESKWALLPAPER)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_closed)
                {
                    try
                    {
                        OnWallpaperChanged();
                    }
                    catch (Exception ex)
                    {
                        Log.Error(_name + ": following the new wallpaper failed.", ex);
                    }
                }
            });
        }

        return SeeThrough.TryHandleErase(Hwnd, message, wParam, out result);
    }
}
