using System;
using System.Numerics;
using Machitan.Config;
using Machitan.Core;
using Machitan.Custom;
using Machitan.Interop;
using Machitan.Shell;
using Machitan.Visuals;
using Microsoft.UI.Composition;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Windows.Graphics;

namespace Machitan.Bar;

/// <summary>
/// The bar window: borderless, always on top, never takes focus, docked to a screen edge.
///
/// The window is see-through (proven by Batch 2's see-through test): a backdrop that draws
/// nothing, Windows alpha (DWM blur-behind with a region off the window), and a window
/// region cut to the segments, 2 px wider than each. So the gaps between segments show the
/// desktop and pass clicks through, and segment edges stay smooth.
///
/// This file: window plumbing (setup, placement, region, timers, auto-hide, messages).
/// BarWindow.Layout.cs: segments, Start group, apps group, Show Desktop.
/// BarWindow.Dock.cs: the app dock buttons.
/// BarWindow.Drag.cs: dragging groups, search and apps on the bar.
///
/// Sizes: the bar strip is BarSize DIP thick; the visible pill is BarSize - 14 DIP,
/// centred in the strip; the bar keeps 16 DIP away from the side edges of the screen.
/// </summary>
public sealed partial class BarWindow : Window
{
    private const int PillInsetDip = 14;
    private const int EdgeMarginDip = 16;
    private const int RegionSlackPx = 2;

    /// <summary>
    /// Empty space between the window's edge and the segments. Windows 11 draws a thin outline
    /// along a window's edge; with this space, the edge lies outside the region cut, so the
    /// outline is cut away with the rest of the empty area.
    /// </summary>
    private const double WindowPadDip = 4;
    private const uint AppBarCallbackMessage = User32.WM_APP + 0x51;

    private const int AutoHideDelayMs = 800;
    private const int StartupGraceMs = 3000;
    private const int RevealZonePx = 2;
    private const int HoverSlackPx = 6;
    private const int SlideMs = 180;

    private readonly ShellHost _host;
    private readonly nint _hwnd;
    private readonly DispatcherQueueTimer _healTimer;
    private readonly DispatcherQueueTimer _fastTimer;

    private WindowSubclass? _subclass;
    private AppBarHost? _appBar;

    private RECT _monitor;
    private RECT _barRect;
    private string _regionSignature = string.Empty;
    private bool _regionLogged;
    private nint _lastNotedFront;
    private string _lastVisibilityProblem = string.Empty;
    private uint _shellHookMessage;
    private long _lastInsideTick;

    private SlideState _slide = SlideState.Shown;
    private bool _fullscreenApp;
    private bool _menuOpen;
    private bool _placing;
    private bool _closing;
    private string _barLook = string.Empty;

    internal BarWindow(ShellHost host)
    {
        _host = host;
        InitializeComponent();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        // Unconditional timers: neither depends on the shell hook working.
        _healTimer = DispatcherQueue.CreateTimer();
        _healTimer.Interval = TimeSpan.FromSeconds(2);
        _healTimer.Tick += (_, _) => OnHealTick();

        _fastTimer = DispatcherQueue.CreateTimer();
        _fastTimer.Interval = TimeSpan.FromMilliseconds(250);
        _fastTimer.Tick += (_, _) => OnFastTick();

        Closed += OnClosed;
        Root.Padding = new Thickness(WindowPadDip);

        // Every move on the bar, even over buttons that keep their pointer events (Show Desktop's
        // line shows only near it).
        Root.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler(OnRootPointerMoved), handledEventsToo: true);

        // The first real layout pass can change the measured width of the content.
        Root.Loaded += (_, _) => ApplyPlacement(forceReservation: false);

        // Segment sizes change with the dock; keep the window region in step.
        BarHost.LayoutUpdated += (_, _) => UpdateRegion();
    }

    private enum SlideState
    {
        Shown,
        Hiding,
        Hidden,
        Showing,
    }

    /// <summary>Where the visible bar is on screen, in physical pixels.</summary>
    internal RECT BarScreenRect => _barRect;

    /// <summary>The bar's window, so pop-ups can keep it above a window brought forward for a moment (Peek).</summary>
    internal nint Handle => _hwnd;

    /// <summary>True while the bar is hidden or on its way out.</summary>
    private bool IsAutoHidden => _slide is SlideState.Hidden or SlideState.Hiding;

    /// <summary>Builds the window, hooks everything up and shows the bar.</summary>
    internal void Start()
    {
        ConfigureWindow();

        _subclass = new WindowSubclass(_hwnd, OnWindowMessage);
        _appBar = new AppBarHost(_hwnd, AppBarCallbackMessage);
        ElementCompositionPreview.SetIsTranslationEnabled(Root, true);

        _host.Tasks.AttachShellHook(_hwnd, _subclass.IsInstalled);
        _shellHookMessage = _host.Tasks.ShellHookMessage;
        Activated += OnBarActivated;
        _host.Tasks.Changed += OnTasksChanged;
        _host.Settings.Changed += OnSettingsChanged;
        _host.Accent.Changed += OnAccentChanged;
        _host.Pins.Changed += OnPinsChanged;
        _host.BarLayout.Changed += OnBarLayoutChanged;
        _host.Custom.Changed += OnCustomChanged;
        _host.Tray.Changed += OnTrayChanged;
        Root.Tapped += OnRootTapped;
        _host.Icons.Changed += OnIconsChanged;
        _host.Desktop.Changed += OnDesktopChanged;
        Machitan.Start.StartMenuWindow.OpenChanged += OnStartMenuOpenChanged;
        Machitan.Flyouts.TrayBoxWindow.OpenChanged += OnTrayBoxOpenChanged;

        _host.Tasks.Refresh();
        PrepareDragging();
        _barLook = BarLook(_host.Settings.Current);
        BuildLayout();
        ApplyPlacement(forceReservation: false);

        // First show. Later shows never take focus.
        Activate();
        ApplyPlacement(forceReservation: false);

        _lastInsideTick = Environment.TickCount64 + StartupGraceMs;
        _healTimer.Start();
        _fastTimer.Start();

        Log.Info("Bar window shown (" + _host.Settings.Current.TaskbarMode + " mode).");
    }

    /// <summary>Stops timers and hooks and closes the window. Called once, on shutdown.</summary>
    internal void PrepareForExit()
    {
        if (_closing)
        {
            return;
        }

        CancelDrag();
        StopDesktopPeek();

        // A peek from the previews too: no window may stay see-through after Machitan.
        LivePeek.End();
        _closing = true;
        _healTimer.Stop();
        _fastTimer.Stop();
        _dragTimer?.Stop();

        _host.Tasks.Changed -= OnTasksChanged;
        _host.Settings.Changed -= OnSettingsChanged;
        _host.Accent.Changed -= OnAccentChanged;
        _host.Pins.Changed -= OnPinsChanged;
        _host.BarLayout.Changed -= OnBarLayoutChanged;
        _host.Custom.Changed -= OnCustomChanged;
        _host.Tray.Changed -= OnTrayChanged;
        _host.Icons.Changed -= OnIconsChanged;
        _host.Desktop.Changed -= OnDesktopChanged;
        Machitan.Start.StartMenuWindow.OpenChanged -= OnStartMenuOpenChanged;
        Machitan.Flyouts.TrayBoxWindow.OpenChanged -= OnTrayBoxOpenChanged;
        _host.Tasks.DetachShellHook();

        _appBar?.Release();
        _subclass?.Dispose();
        _subclass = null;

        Close();
    }

    // ---------------------------------------------------------------- window setup

    private void ConfigureWindow()
    {
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }
        else
        {
            Log.Warn("The bar window has no overlapped presenter; border and title bar stay as they are.");
        }

        // Tool window: no Alt+Tab entry and no button on our own bar.
        // No-activate: clicking the bar must not take focus away from the app in front.
        long exStyle = User32.GetWindowLongPtrW(_hwnd, User32.GWL_EXSTYLE);
        exStyle |= User32.WS_EX_TOOLWINDOW | User32.WS_EX_NOACTIVATE;
        exStyle &= ~User32.WS_EX_APPWINDOW;
        User32.SetWindowLongPtrW(_hwnd, User32.GWL_EXSTYLE, (nint)exStyle);

        // A plain popup window: no caption, no resize edge, no border. Windows 11 draws its
        // thin outline and rounded corners only for framed windows; WinUI keeps some frame
        // styles even with border and title bar switched off.
        long style = User32.GetWindowLongPtrW(_hwnd, User32.GWL_STYLE);
        style &= ~(User32.WS_BORDER | User32.WS_DLGFRAME | User32.WS_THICKFRAME
            | User32.WS_SYSMENU | User32.WS_MINIMIZEBOX | User32.WS_MAXIMIZEBOX);
        style |= User32.WS_POPUP;
        User32.SetWindowLongPtrW(_hwnd, User32.GWL_STYLE, (nint)style);
        User32.SetWindowPos(
            _hwnd,
            0,
            0,
            0,
            0,
            0,
            User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOZORDER | User32.SWP_NOACTIVATE | User32.SWP_FRAMECHANGED);

        int none = Dwmapi.DWMWA_COLOR_NONE;
        Dwmapi.DwmSetWindowAttribute(_hwnd, Dwmapi.DWMWA_BORDER_COLOR, ref none, sizeof(int));
        int square = Dwmapi.DWMWCP_DONOTROUND;
        Dwmapi.DwmSetWindowAttribute(_hwnd, Dwmapi.DWMWA_WINDOW_CORNER_PREFERENCE, ref square, sizeof(int));

        // See-through, step 1: a backdrop that draws nothing, so WinUI drops its solid fill.
        SystemBackdrop = new TransparentBackdrop();

        // See-through, step 2: Windows alpha. Blur-behind with a region off the window blurs
        // nothing, but makes DWM show what is behind fully transparent pixels.
        nint offWindow = Gdi32.CreateRectRgn(-2, -2, -1, -1);
        try
        {
            var blur = new DWM_BLURBEHIND
            {
                dwFlags = Dwmapi.DWM_BB_ENABLE | Dwmapi.DWM_BB_BLURREGION,
                fEnable = 1,
                hRgnBlur = offWindow,
            };

            int hr = Dwmapi.DwmEnableBlurBehindWindow(_hwnd, ref blur);
            if (hr != 0)
            {
                Log.Warn("Windows refused the see-through request for the bar (0x" + hr.ToString("X8") + "); gaps may show black.");
            }
        }
        finally
        {
            // DWM keeps its own copy; the region stays ours to free.
            Gdi32.DeleteObject(offWindow);
        }

        // Step 3 (the region cut to the segments) happens in UpdateRegion after each layout.
    }

    // ---------------------------------------------------------------- events from the shell

    private void OnTasksChanged()
    {
        if (_closing)
        {
            return;
        }

        _host.Desktop.OnTasksChanged(_host.Tasks.Groups);
        if (IsDragging)
        {
            // The bar does not change under a drag; the dock catches up when it ends.
            _dockSyncPending = true;
            return;
        }

        SyncDock(rebuild: false);
        ApplyPlacement(forceReservation: false);
    }

    /// <summary>Tray icons came, went or changed: the tray bubble follows (and comes or goes with its first or last icon).</summary>
    private void OnTrayChanged()
    {
        if (_closing || !_host.Settings.Current.TrayOnBar)
        {
            return;
        }

        if (IsDragging)
        {
            _dockSyncPending = true;
            return;
        }

        SyncDock(rebuild: false);
        FillTrayBubble();
        ApplyPlacement(forceReservation: false);
    }

    /// <summary>Bubbles moved, made or ended, or an app moved between them.</summary>
    private void OnBarLayoutChanged()
    {
        if (_closing)
        {
            return;
        }

        if (IsDragging)
        {
            _dockSyncPending = true;
            return;
        }

        SyncDock(rebuild: true);
        ApplyPlacement(forceReservation: false);
    }

    private void OnPinsChanged()
    {
        if (_closing)
        {
            return;
        }

        if (IsDragging)
        {
            _dockSyncPending = true;
            return;
        }

        SyncDock(rebuild: true);
        ApplyPlacement(forceReservation: false);
    }

    private void OnIconsChanged()
    {
        if (_closing)
        {
            return;
        }

        if (IsDragging)
        {
            _dockSyncPending = true;
            return;
        }

        SyncDock(rebuild: false);
    }

    private void OnDesktopChanged()
    {
        if (_closing)
        {
            return;
        }

        UpdateDesktopStrip();
    }

    /// <summary>The accent colour changed (a new wallpaper, or Settings): the focused app's line and Show Desktop.</summary>
    private void OnAccentChanged()
    {
        SyncDock(rebuild: false);
        UpdateDesktopStrip();
    }

    private void OnSettingsChanged()
    {
        if (_closing)
        {
            return;
        }

        // Only the bar's own settings matter here: a change elsewhere (the calendar, the
        // corner widgets) leaves the bar as it is instead of rebuilding it.
        string look = BarLook(_host.Settings.Current);
        if (look == _barLook)
        {
            return;
        }

        _barLook = look;
        BuildLayout();

        if (!_host.Settings.Current.AutoHideBar && IsAutoHidden)
        {
            Reveal();
        }

        ApplyPlacement(forceReservation: true);
    }

    /// <summary>Every setting the bar's look or place depends on, in one string to compare.</summary>
    private static string BarLook(ShellSettings settings)
    {
        return string.Join(
            "|",
            settings.Theme,
            settings.TaskbarMode,
            settings.DockEdge,
            settings.DockAlignment,
            settings.BarSize,
            settings.IconSize,
            settings.Opacity,
            settings.EnableAcrylic,
            settings.SearchMode,
            settings.SearchPlace,
            settings.DockLabels,
            settings.ShowStartOnBar,
            settings.ShowAppsOnBar,
            settings.TrayOnBar,
            settings.AutoHideBar,
            settings.CustomStartButton,
            settings.StartButtonPicture,
            settings.StartButtonPictureHover,
            settings.StartButtonPicturePressed);
    }

    // ---------------------------------------------------------------- placement

    /// <summary>
    /// Works out where the bar belongs and moves the window there if it is anywhere else.
    /// Safe to call often: it does nothing when everything is already in place.
    /// </summary>
    private void ApplyPlacement(bool forceReservation)
    {
        // During a drag the bar stays put (the drag works from where things were); it is
        // placed again when the drag ends.
        if (_closing || _placing || IsDragging)
        {
            return;
        }

        _placing = true;
        try
        {
            ShellSettings settings = _host.Settings.Current;

            RectInt32 outer = DisplayArea.Primary.OuterBounds;
            _monitor = new RECT
            {
                Left = outer.X,
                Top = outer.Y,
                Right = outer.X + outer.Width,
                Bottom = outer.Y + outer.Height,
            };

            double scale = Scale();
            bool top = settings.DockEdge == DockEdge.Top;

            int strip = (int)Math.Round(settings.BarSize * scale);
            int pillHeight = (int)Math.Round(PillHeightDip() * scale);
            int margin = (int)Math.Round(EdgeMarginDip * scale);
            int pad = (int)Math.Round(WindowPadDip * scale);
            int maxWidth = Math.Max(_monitor.Width - (2 * margin), pillHeight);

            int barWidth;
            if (settings.TaskbarMode == TaskbarMode.Classic)
            {
                barWidth = maxWidth;
            }
            else
            {
                BarHost.Measure(new Windows.Foundation.Size(double.PositiveInfinity, PillHeightDip()));
                barWidth = (int)Math.Ceiling(BarHost.DesiredSize.Width * scale);
                barWidth = Math.Clamp(barWidth, Math.Min(pillHeight, maxWidth), maxWidth);
            }

            // The strip the bar sits in: reserved through the AppBar system unless auto-hide is on.
            RECT stripRect = _monitor;
            if (top)
            {
                stripRect.Bottom = _monitor.Top + strip;
            }
            else
            {
                stripRect.Top = _monitor.Bottom - strip;
            }

            if (_appBar is not null)
            {
                if (settings.AutoHideBar)
                {
                    _appBar.Release();
                }
                else
                {
                    RECT? granted = _appBar.Reserve(top ? Shell32.ABE_TOP : Shell32.ABE_BOTTOM, _monitor, strip, forceReservation);
                    if (granted is RECT grantedRect)
                    {
                        stripRect = grantedRect;
                    }
                }
            }

            DockAlignment alignment = settings.TaskbarMode == TaskbarMode.Classic ? DockAlignment.Center : settings.DockAlignment;
            int x = alignment switch
            {
                DockAlignment.Left => _monitor.Left + margin,
                DockAlignment.Right => _monitor.Right - margin - barWidth,
                _ => _monitor.Left + ((_monitor.Width - barWidth) / 2),
            };
            int y = stripRect.Top + ((stripRect.Height - pillHeight) / 2);

            // The visible bar; the window is the same rectangle grown by the pad on every side.
            RECT previous = _barRect;
            _barRect = new RECT { Left = x, Top = y, Right = x + barWidth, Bottom = y + pillHeight };
            if (!previous.SameAs(_barRect))
            {
                // The corner widgets and Dynaland place themselves around the bar.
                _host.NoteBarMoved();
            }
            var windowRect = new RECT
            {
                Left = x - pad,
                Top = y - pad,
                Right = x + barWidth + pad,
                Bottom = y + pillHeight + pad,
            };

            if (_slide == SlideState.Hidden || _fullscreenApp)
            {
                // Hidden on purpose; the rectangle is remembered for the next reveal.
                return;
            }

            bool inPlace = User32.GetWindowRect(_hwnd, out RECT current) && current.SameAs(windowRect);
            if (!inPlace)
            {
                User32.SetWindowPos(
                    _hwnd,
                    User32.HWND_TOPMOST,
                    windowRect.Left,
                    windowRect.Top,
                    windowRect.Width,
                    windowRect.Height,
                    User32.SWP_NOACTIVATE);
            }
        }
        catch (Exception ex)
        {
            Log.Error("The bar could not be placed.", ex);
        }
        finally
        {
            _placing = false;
        }
    }

    private double PillHeightDip()
    {
        return Math.Max(_host.Settings.Current.BarSize - PillInsetDip, 16);
    }

    private double Scale()
    {
        uint dpi = User32.GetDpiForWindow(_hwnd);
        return (dpi == 0 ? 96 : dpi) / 96.0;
    }

    /// <summary>
    /// Cuts the window to the segments, each 2 px wider than drawn, so smooth edges stay inside
    /// and the gaps between segments are not part of the window (clicks there go through).
    /// Skips the work when nothing changed.
    /// </summary>
    private void UpdateRegion()
    {
        if (_closing || Root.XamlRoot is null || _shapes.Count == 0)
        {
            return;
        }

        try
        {
            double scale = Root.XamlRoot.RasterizationScale;
            var rects = new System.Collections.Generic.List<(int Left, int Top, int Right, int Bottom, int Ellipse)>(_shapes.Count);
            var signature = new System.Text.StringBuilder();

            for (int i = 0; i < _shapes.Count; i++)
            {
                (FrameworkElement element, double radiusDip) = _shapes[i];
                if (element.Visibility != Visibility.Visible)
                {
                    // A pill that is hidden (the apps pill with no apps) is not part of the window.
                    continue;
                }

                if (element.ActualWidth <= 0 || element.ActualHeight <= 0)
                {
                    // Not laid out yet; the next layout pass cuts.
                    return;
                }

                Windows.Foundation.Rect bounds = element.TransformToVisual(Root)
                    .TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));

                int left = (int)Math.Floor(bounds.Left * scale) - RegionSlackPx;
                int top = (int)Math.Floor(bounds.Top * scale) - RegionSlackPx;
                int right = (int)Math.Ceiling(bounds.Right * scale) + RegionSlackPx + 1;
                int bottom = (int)Math.Ceiling(bounds.Bottom * scale) + RegionSlackPx + 1;
                int ellipse = (int)Math.Round(((radiusDip * scale) + RegionSlackPx) * 2);

                rects.Add((left, top, right, bottom, ellipse));
                signature.Append(left).Append(',').Append(top).Append(',').Append(right).Append(',')
                    .Append(bottom).Append(',').Append(ellipse).Append(';');
            }

            string newSignature = signature.ToString();
            if (rects.Count == 0 || newSignature == _regionSignature)
            {
                return;
            }

            nint total = Gdi32.CreateRectRgn(0, 0, 0, 0);
            if (total == 0)
            {
                return;
            }

            foreach ((int left, int top, int right, int bottom, int ellipse) in rects)
            {
                nint shape = Gdi32.CreateRoundRectRgn(left, top, right, bottom, ellipse, ellipse);
                if (shape != 0)
                {
                    Gdi32.CombineRgn(total, total, shape, Gdi32.RGN_OR);
                    Gdi32.DeleteObject(shape);
                }
            }

            if (User32.SetWindowRgn(_hwnd, total, true) == 0)
            {
                // The system took no ownership, so the region is still ours to free.
                Gdi32.DeleteObject(total);
                Log.Warn("The bar's window region was refused by Windows.");
                return;
            }

            if (_regionSignature.Length == 0 && !_regionLogged)
            {
                Log.Info("Bar window cut to " + _shapes.Count + " segment(s).");
                _regionLogged = true;
            }

            _regionSignature = newSignature;
        }
        catch (Exception ex)
        {
            Log.Error("The bar's window region could not be updated.", ex);
        }
    }

    // ---------------------------------------------------------------- timers

    /// <summary>
    /// Every 2 seconds, always: put the bar back if something minimised, hid or moved it.
    /// Also polls the task list, but only when the shell hook is not working.
    /// </summary>
    private void OnHealTick()
    {
        if (_closing)
        {
            return;
        }

        try
        {
            if (_host.Tasks.UsesPolling)
            {
                _host.Tasks.Refresh();
            }

            if (!IsDragging && _dragLayer is { IsShown: true } layer)
            {
                // Never left over a strip of the screen after a drag.
                Log.Warn("Bar drag: the drag layer was still shown with no drag running; hiding it.");
                layer.HideLayer();
            }

            if (_slide != SlideState.Shown || _fullscreenApp || IsDragging)
            {
                return;
            }

            if (User32.IsIconic(_hwnd))
            {
                Log.Info("Self-heal: the bar was minimised; restoring it.");
                User32.ShowWindow(_hwnd, User32.SW_SHOWNOACTIVATE);
                if (User32.IsIconic(_hwnd))
                {
                    User32.ShowWindow(_hwnd, User32.SW_RESTORE);
                }
            }

            if (!User32.IsWindowVisible(_hwnd))
            {
                Log.Info("Self-heal: the bar was hidden; showing it.");
                User32.ShowWindow(_hwnd, User32.SW_SHOWNOACTIVATE);
            }

            ApplyPlacement(forceReservation: false);

            // Something (a style or size change inside WinUI) can drop the window region.
            if (_shapes.Count > 0 && User32.GetWindowRgnBox(_hwnd, out _) == 0)
            {
                Log.Info("Self-heal: the bar's window region was gone; cutting it again.");
                _regionSignature = string.Empty;
                UpdateRegion();
            }

            User32.SetWindowPos(
                _hwnd,
                User32.HWND_TOPMOST,
                0,
                0,
                0,
                0,
                User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
        }
        catch (Exception ex)
        {
            Log.Error("Bar self-heal failed.", ex);
        }
    }

    /// <summary>
    /// Four times a second, always: keep Explorer's taskbar hidden and run auto-hide.
    /// Auto-hide: the bar slides away shortly after the pointer leaves it and slides back
    /// when the pointer touches the docked screen edge.
    /// </summary>
    private void OnFastTick()
    {
        if (_closing)
        {
            return;
        }

        try
        {
            ExplorerTaskbar.EnsureHidden();

            CheckFullScreen();
            NoteCoveringFrontWindow();
            if (_fullscreenApp)
            {
                return;
            }

            WatchBarVisibility();

            long now = Environment.TickCount64;

            if (!_host.Settings.Current.AutoHideBar)
            {
                if (IsAutoHidden)
                {
                    Reveal();
                }

                return;
            }

            if (!User32.GetCursorPos(out POINT cursor))
            {
                return;
            }

            bool top = _host.Settings.Current.DockEdge == DockEdge.Top;
            bool withinMonitorSides = cursor.X >= _monitor.Left && cursor.X < _monitor.Right;
            bool atEdge = withinMonitorSides && (top
                ? cursor.Y <= _monitor.Top + RevealZonePx - 1 && cursor.Y >= _monitor.Top
                : cursor.Y >= _monitor.Bottom - RevealZonePx && cursor.Y < _monitor.Bottom);

            if (IsAutoHidden)
            {
                if (atEdge)
                {
                    Log.Info("Auto-hide: pointer at the edge (" + cursor.X + ", " + cursor.Y + "); sliding in.");
                    Reveal();
                }

                return;
            }

            bool overBar = _barRect.Contains(cursor, HoverSlackPx);
            bool startOpen = _host.StartMenu?.IsOpen ?? false;
            if (overBar || atEdge || _menuOpen || startOpen || _host.PreviewShown || _typing || IsDragging || _host.Custom.IsOn)
            {
                if (now > _lastInsideTick)
                {
                    _lastInsideTick = now;
                }

                return;
            }

            if (now - _lastInsideTick > AutoHideDelayMs)
            {
                Log.Info(
                    "Auto-hide: pointer away (" + cursor.X + ", " + cursor.Y + "; bar " + _barRect.Left + "," + _barRect.Top
                    + " to " + _barRect.Right + "," + _barRect.Bottom + "; screen bottom " + _monitor.Bottom + "); sliding out.");
                BeginHide();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Bar auto-hide failed.", ex);
        }
    }

    // ---------------------------------------------------------------- auto-hide slide

    /// <summary>
    /// Slides the bar's content out of its window (Composition, no window resizing), then
    /// hides the window.
    /// </summary>
    private void BeginHide()
    {
        if (IsAutoHidden)
        {
            return;
        }

        _host.HidePreview();

        _slide = SlideState.Hiding;
        AnimateSlide(SlideOutOffset(), () =>
        {
            if (_slide == SlideState.Hiding)
            {
                User32.ShowWindow(_hwnd, User32.SW_HIDE);
                _slide = SlideState.Hidden;
            }
        });
    }

    /// <summary>Shows the window with the content just out of view, then slides it in.</summary>
    private void Reveal()
    {
        if (_slide is SlideState.Shown or SlideState.Showing)
        {
            return;
        }

        bool wasHidden = _slide == SlideState.Hidden;
        _slide = SlideState.Showing;
        _lastInsideTick = Environment.TickCount64;

        if (wasHidden)
        {
            SetSlide(SlideOutOffset());
            User32.ShowWindow(_hwnd, User32.SW_SHOWNOACTIVATE);
            ApplyPlacement(forceReservation: false);
        }

        AnimateSlide(0f, () =>
        {
            if (_slide == SlideState.Showing)
            {
                _slide = SlideState.Shown;
            }
        });
    }

    /// <summary>How far the content moves to leave the window: down for a bottom bar, up for a top bar.</summary>
    private float SlideOutOffset()
    {
        float distance = (float)(PillHeightDip() + PillInsetDip);
        return _host.Settings.Current.DockEdge == DockEdge.Top ? -distance : distance;
    }

    private void SetSlide(float offset)
    {
        Visual visual = ElementCompositionPreview.GetElementVisual(Root);
        visual.StopAnimation("Translation.Y");
        visual.Properties.InsertVector3("Translation", new Vector3(0f, offset, 0f));
    }

    private void AnimateSlide(float to, Action completed)
    {
        // Animations turned off (here or in Windows): the bar simply appears or goes.
        if (!Motion.IsOn(_host.Settings.Current))
        {
            SetSlide(to);
            completed();
            return;
        }

        try
        {
            Visual visual = ElementCompositionPreview.GetElementVisual(Root);
            Compositor compositor = visual.Compositor;

            ScalarKeyFrameAnimation animation = compositor.CreateScalarKeyFrameAnimation();
            animation.InsertKeyFrame(
                1f,
                to,
                compositor.CreateCubicBezierEasingFunction(new Vector2(0.1f, 0.9f), new Vector2(0.2f, 1f)));
            animation.Duration = TimeSpan.FromMilliseconds(SlideMs);

            CompositionScopedBatch batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
            visual.StartAnimation("Translation.Y", animation);
            batch.End();

            // Also raised when a newer animation replaces this one; the state checks in the
            // callbacks make that harmless.
            batch.Completed += (_, _) => DispatcherQueue.TryEnqueue(() => completed());
        }
        catch (Exception ex)
        {
            // No animation: jump straight to the end state.
            Log.Warn("The bar slide could not be animated.", ex);
            SetSlide(to);
            completed();
        }
    }

    // ---------------------------------------------------------------- window messages

    private bool OnWindowMessage(uint message, nint wParam, nint lParam, out nint result)
    {
        result = 0;
        if (_closing)
        {
            return false;
        }

        if (_shellHookMessage != 0 && message == _shellHookMessage)
        {
            _host.Tasks.OnShellHookMessage(wParam, lParam);
            return false;
        }

        switch (message)
        {
            case User32.WM_ERASEBKGND:
                // Erase to black: under Windows alpha, black with zero alpha is see-through.
                if (User32.GetClientRect(_hwnd, out RECT client))
                {
                    User32.FillRect(wParam, ref client, Gdi32.GetStockObject(Gdi32.BLACK_BRUSH));
                    result = 1;
                    return true;
                }

                return false;

            case AppBarCallbackMessage:
                OnAppBarCallback((uint)wParam, lParam);
                return true;

            case User32.WM_DISPLAYCHANGE:
            case User32.WM_DPICHANGED:
                QueuePlacement(forceReservation: true);
                return false;

            case User32.WM_SETTINGCHANGE:
                if (wParam == (nint)User32.SPI_SETWORKAREA)
                {
                    QueuePlacement(forceReservation: false);
                }

                return false;

            case User32.WM_ENDSESSION:
                if (wParam != 0)
                {
                    // Signing out or shutting down: nobody gets another chance to put the taskbar back.
                    ExplorerTaskbar.Restore();
                }

                return false;

            case User32.WM_SYSCOMMAND:
                // Swallow minimise requests; the bar is never meant to be minimised.
                return ((long)wParam & 0xFFF0) == User32.SC_MINIMIZE;

            case User32.WM_SIZE:
                if (wParam == (nint)User32.SIZE_MINIMIZED)
                {
                    DispatcherQueue.TryEnqueue(OnHealTick);
                }

                return false;
        }

        return false;
    }

    private void OnAppBarCallback(uint notification, nint lParam)
    {
        switch (notification)
        {
            case Shell32.ABN_POSCHANGED:
                // Another app bar or the work area changed. Ignore the echo of our own change.
                if (_appBar is not null && !_appBar.ChangedJustNow)
                {
                    QueuePlacement(forceReservation: true);
                }

                break;

            case Shell32.ABN_FULLSCREENAPP:
                // Windows' own signal also fires for full-screen overlays (screen recorders,
                // capture tools). Only use it as a hint to check the front window now.
                DispatcherQueue.TryEnqueue(CheckFullScreen);
                break;
        }
    }

    /// <summary>
    /// Steps the bar aside while a real full-screen app is in front (a game, a video, a browser
    /// in F11), and brings it back afterwards. Runs four times a second.
    /// </summary>
    private void CheckFullScreen()
    {
        if (_closing)
        {
            return;
        }

        bool fullScreen = IsFrontWindowFullScreen(out string what);
        if (fullScreen == _fullscreenApp)
        {
            return;
        }

        _fullscreenApp = fullScreen;
        _host.NoteFullScreen(fullScreen);
        if (fullScreen)
        {
            Log.Info("Full-screen app in front (" + what + "); the bar steps aside.");
            User32.ShowWindow(_hwnd, User32.SW_HIDE);
        }
        else
        {
            Log.Info("Full-screen app gone; the bar is back.");
            if (_slide != SlideState.Hidden)
            {
                User32.ShowWindow(_hwnd, User32.SW_SHOWNOACTIVATE);
                ApplyPlacement(forceReservation: false);
            }
        }
    }

    /// <summary>
    /// A window counts as full-screen only when it is in front, covers the whole screen the bar
    /// is on, and is a real app window: not maximised (a maximised window is not full-screen),
    /// not the desktop, not a click-through or no-activate overlay, not a tool window, not
    /// hidden by the system. Screen recorders and capture tools put up full-screen overlays
    /// that are one of those; they must not hide the bar.
    /// </summary>
    private bool IsFrontWindowFullScreen(out string what)
    {
        what = string.Empty;

        nint front = User32.GetForegroundWindow();
        if (front == 0 || front == _hwnd || User32.IsZoomed(front) || WindowInfo.IsCloaked(front))
        {
            return false;
        }

        long exStyle = User32.GetWindowLongPtrW(front, User32.GWL_EXSTYLE);
        if ((exStyle & (User32.WS_EX_TRANSPARENT | User32.WS_EX_NOACTIVATE | User32.WS_EX_TOOLWINDOW)) != 0)
        {
            return false;
        }

        string className = WindowInfo.GetClassName(front);
        switch (className)
        {
            case "Progman":
            case "WorkerW":
            case "Shell_TrayWnd":
            case "Shell_SecondaryTrayWnd":
            case "XamlExplorerHostIslandWindow":
                return false;
        }

        if (!User32.GetWindowRect(front, out RECT rect)
            || rect.Left > _monitor.Left
            || rect.Top > _monitor.Top
            || rect.Right < _monitor.Right
            || rect.Bottom < _monitor.Bottom)
        {
            return false;
        }

        User32.GetWindowThreadProcessId(front, out uint processId);
        string? path = WindowInfo.GetProcessPath(processId);
        what = (path is null ? "unknown app" : System.IO.Path.GetFileName(path)) + ", window class " + className;
        return true;
    }

    /// <summary>
    /// Diagnostics: when a window that covers the whole screen comes to the front but is not
    /// treated as full-screen (an overlay), log what it is, once per window.
    /// </summary>
    private void NoteCoveringFrontWindow()
    {
        nint front = User32.GetForegroundWindow();
        if (front == 0 || front == _lastNotedFront || front == _hwnd)
        {
            return;
        }

        _lastNotedFront = front;
        string frontClass = WindowInfo.GetClassName(front);
        if (frontClass is "Progman" or "WorkerW")
        {
            // The desktop itself always covers the screen; not worth a log line.
            return;
        }

        if (_fullscreenApp
            || !User32.GetWindowRect(front, out RECT rect)
            || rect.Left > _monitor.Left
            || rect.Top > _monitor.Top
            || rect.Right < _monitor.Right
            || rect.Bottom < _monitor.Bottom)
        {
            return;
        }

        Log.Info("Window covering the screen came to the front, not treated as full-screen: " + DescribeWindow(front) + ".");
    }

    /// <summary>
    /// The bar should be on screen right now (not auto-hidden, no full-screen app). If it is not
    /// visible, is minimised, or is hidden by the system, log what is in front and put it back.
    /// Logs only when the problem changes, so a stuck state is one line, not four a second.
    /// </summary>
    private void WatchBarVisibility()
    {
        if (_slide != SlideState.Shown)
        {
            _lastVisibilityProblem = string.Empty;
            return;
        }

        bool visible = User32.IsWindowVisible(_hwnd);
        bool minimized = User32.IsIconic(_hwnd);
        bool cloaked = WindowInfo.IsCloaked(_hwnd);

        string problem = !visible ? "not visible"
            : minimized ? "minimised"
            : cloaked ? "hidden by the system (cloaked)"
            : string.Empty;

        if (problem != _lastVisibilityProblem)
        {
            if (problem.Length > 0)
            {
                Log.Warn("The bar is " + problem + " although it should be shown. In front: "
                    + DescribeWindow(User32.GetForegroundWindow()) + ".");
            }
            else if (_lastVisibilityProblem.Length > 0)
            {
                Log.Info("The bar is shown again.");
            }

            _lastVisibilityProblem = problem;
        }

        if (!visible || minimized)
        {
            User32.ShowWindow(_hwnd, minimized ? User32.SW_RESTORE : User32.SW_SHOWNOACTIVATE);
            User32.SetWindowPos(
                _hwnd,
                User32.HWND_TOPMOST,
                0,
                0,
                0,
                0,
                User32.SWP_NOMOVE | User32.SWP_NOSIZE | User32.SWP_NOACTIVATE);
        }
    }

    /// <summary>For the log: the app's file name, window class, extended style, and state flags.</summary>
    private static string DescribeWindow(nint hwnd)
    {
        if (hwnd == 0)
        {
            return "nothing";
        }

        User32.GetWindowThreadProcessId(hwnd, out uint processId);
        string? path = WindowInfo.GetProcessPath(processId);
        long exStyle = User32.GetWindowLongPtrW(hwnd, User32.GWL_EXSTYLE);

        return (path is null ? "unknown app" : System.IO.Path.GetFileName(path))
            + ", window class " + WindowInfo.GetClassName(hwnd)
            + ", extended style 0x" + exStyle.ToString("X8")
            + (User32.IsZoomed(hwnd) ? ", maximised" : string.Empty)
            + (WindowInfo.IsCloaked(hwnd) ? ", cloaked" : string.Empty);
    }

    private void QueuePlacement(bool forceReservation)
    {
        DispatcherQueue.TryEnqueue(() => ApplyPlacement(forceReservation));
    }

    /// <summary>The shell's top strip came, went or changed: the bar takes its place again.</summary>
    internal void RequestPlacement()
    {
        QueuePlacement(forceReservation: true);
    }

    // ---------------------------------------------------------------- pointer, menus, closing

    private void OnRootPointerEntered(object sender, PointerRoutedEventArgs e)
    {
        _barHovered = true;
        UpdateDesktopStrip();
    }

    private void OnRootPointerExited(object sender, PointerRoutedEventArgs e)
    {
        _barHovered = false;
        _desktopNear = false;
        UpdateDesktopStrip();
    }

    private void OnRootPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        bool near = IsNearDesktopStrip(e);
        if (near != _desktopNear)
        {
            _desktopNear = near;
            UpdateDesktopStrip();
        }
    }

    /// <summary>The bar's own right-click menu: filled when it opens.</summary>
    private void OnMenuOpening(object? sender, object e)
    {
        if (sender is Microsoft.UI.Xaml.Controls.MenuFlyout menu)
        {
            FillBarMenu(menu);
        }

        NoteMenuOpening();
    }

    /// <summary>Any bar menu (the bar's own or an app's) is opening.</summary>
    private void NoteMenuOpening()
    {
        _menuOpen = true;
        Log.Info("Bar menu opened.");

        // As on Windows' taskbar: a right-click on the bar closes Start.
        if (_host.StartMenu is { IsOpen: true } start)
        {
            start.HideMenu();
        }

        // The bar never takes focus by itself. Take it for the menu, so that a click
        // anywhere else closes the menu again.
        User32.SetForegroundWindow(_hwnd);
    }

    private void OnMenuClosed(object? sender, object e)
    {
        _menuOpen = false;
        _lastInsideTick = Environment.TickCount64;
    }

    private void OnSettingsClicked(object sender, RoutedEventArgs e)
    {
        _host.OpenSettings();
    }

    /// <summary>
    /// The bar's own menu: Customize, Settings, Exit; in custom mode the black menu for the
    /// bubble under the pointer (Settings / Remove) and Done.
    /// </summary>
    private void FillBarMenu(Microsoft.UI.Xaml.Controls.MenuFlyout menu)
    {
        if (_host.Custom.IsOn)
        {
            CustomMenus.Fill(menu, _host.Custom, BubbleUnderCursor());
            return;
        }

        CustomMenus.MakePlain(menu);
        menu.Items.Clear();
        var customize = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem { Text = Strings.BarCustomize };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(customize, Strings.BarCustomizeName);
        customize.Click += (_, _) => _host.Custom.Enter();
        var settings = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem { Text = Strings.BarSettings };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(settings, Strings.FolderMachitan);
        settings.Click += OnSettingsClicked;
        var exit = new Microsoft.UI.Xaml.Controls.MenuFlyoutItem { Text = Strings.BarExit };
        exit.Click += OnExitClicked;
        menu.Items.Add(customize);
        menu.Items.Add(settings);
        menu.Items.Add(new Microsoft.UI.Xaml.Controls.MenuFlyoutSeparator());
        menu.Items.Add(exit);
    }

    /// <summary>In custom mode, a tap on a bubble's empty space picks the bubble.</summary>
    private void OnRootTapped(object sender, TappedRoutedEventArgs e)
    {
        if (_host.Custom.IsOn && !IsDragging)
        {
            _host.Custom.Select(BubbleUnderCursor());
        }
    }

    private void OnCustomChanged()
    {
        if (_closing)
        {
            return;
        }

        if (_host.Custom.IsOn)
        {
            // Ctrl+click picks belong to normal use; custom mode picks whole bubbles.
            ClearSelection();
            _host.HidePreview();
            if (IsAutoHidden)
            {
                QueuePlacement(forceReservation: false);
            }
        }
    }

    private void OnExitClicked(object sender, RoutedEventArgs e)
    {
        _host.Shutdown();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        if (!_closing)
        {
            // Closed by something other than our own shutdown path: leave cleanly anyway.
            Log.Warn("The bar window was closed from outside; shutting the shell down.");
            _host.Shutdown();
        }
    }
}
