using System;
using Machitan.Config;
using Machitan.Core;
using Machitan.Interop;
using Machitan.Shell;
using Machitan.Visuals;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
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
    /// <summary>Every widget is one line this tall (DIP), per the spec.</summary>
    internal const double LineHeightDip = 36;

    /// <summary>Space between a widget and the screen's edge (DIP).</summary>
    internal const double EdgeMarginDip = 10;

    /// <summary>Space between two widgets in one corner (DIP).</summary>
    internal const double StackGapDip = 8;

    private protected const double PadDip = 4;

    private readonly string _name;
    private readonly double _widthDip;
    private readonly WindowCutter _cutter;
    private readonly DispatcherQueueTimer _heal;
    private WindowSubclass? _subclass;
    private RECT _placed;
    private bool _started;
    private bool _closed;
    private bool _onLeft = true;
    private bool _onTop = true;

    /// <param name="widthDip">The window's fixed content width; the widget's shape is never wider.</param>
    private protected CornerWidgetWindow(ShellHost host, string name, double widthDip)
    {
        Host = host;
        _name = name;
        _widthDip = widthDip;
        Title = "Machitan " + name;

        Root = new Grid
        {
            Padding = new Thickness(PadDip),
            Background = new SolidColorBrush(Colors.Transparent),
        };
        Content = Root;

        Hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        SeeThrough.Prepare(this, Hwnd, alwaysOnTop: true);

        // Clicks work, but the window never becomes the active one (the system panels a
        // click opens get the keyboard, not this window).
        long exStyle = User32.GetWindowLongPtrW(Hwnd, User32.GWL_EXSTYLE);
        User32.SetWindowLongPtrW(Hwnd, User32.GWL_EXSTYLE, (nint)(exStyle | User32.WS_EX_NOACTIVATE));

        _subclass = new WindowSubclass(Hwnd, OnWindowMessage);
        _cutter = new WindowCutter(Hwnd, Root);
        Root.LayoutUpdated += (_, _) => _cutter.Update();

        _heal = DispatcherQueue.CreateTimer();
        _heal.Interval = TimeSpan.FromSeconds(2);
        _heal.Tick += (_, _) => Heal();

        Host.Settings.Changed += OnSettingsChanged;
        Host.FullScreenChanged += OnFullScreenChanged;
        Closed += (_, _) =>
        {
            _closed = true;
            _heal.Stop();
            Host.Settings.Changed -= OnSettingsChanged;
            Host.FullScreenChanged -= OnFullScreenChanged;
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

    /// <summary>
    /// True when the widget is meant to be on screen: on, with something to show. Unlike
    /// <see cref="IsShown"/> it stays true while a full-screen app hides it, so the top strip
    /// does not come and go with every video.
    /// </summary>
    internal bool WantsSpace => _started && !_closed && IsEnabled(Host.Settings.Current) && HasContent;

    /// <summary>The corner the settings put this widget in.</summary>
    internal ScreenCorner Corner => CornerOf(Host.Settings.Current);

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
    /// Puts the window in its corner: in the work area of the bar's screen, 10 DIP from the
    /// edges, below (or above) any widget that shares the corner. A top corner sits inside the
    /// top strip when Machitan keeps one. Moves it only when needed.
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
            int slot = Host.CornerSlotOf(this);

            int width = (int)Math.Round((_widthDip + (2 * PadDip)) * scale);
            int height = (int)Math.Round((LineHeightDip + (2 * PadDip)) * scale);
            int margin = (int)Math.Round((EdgeMarginDip - PadDip) * scale);
            int stack = (int)Math.Round(slot * (LineHeightDip + StackGapDip) * scale);

            // The top strip is part of the screen's edge, not of the work area below it.
            int topEdge = top && Host.TopStripTop is int stripTop ? stripTop : work.Top;
            int x = left ? work.Left + margin : work.Right - margin - width;
            int y = top ? topEdge + margin + stack : work.Bottom - margin - height - stack;
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
