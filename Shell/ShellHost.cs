using System;
using System.Collections.Generic;
using Machitan.Bar;
using Machitan.Config;
using Machitan.Core;
using Machitan.Flyouts;
using Machitan.Interop;
using Machitan.SettingsUi;
using Machitan.Start;
using Machitan.Widgets;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace Machitan.Shell;

/// <summary>Owns the shell's long-lived parts and starts and stops them in order.</summary>
internal sealed class ShellHost
{
    private BarWindow? _bar;
    private SettingsWindow? _settingsWindow;
    private KeyboardService? _keyboard;
    private CalendarFlyout? _calendar;
    private QuickSettingsFlyout? _quickSettings;
    private SwitcherWindow? _switcher;
    private PreviewWindow? _preview;
    private InfoLineWindow? _infoLine;
    private DuoRingWindow? _duoRing;
    private TopStrip? _topStrip;
    private bool _layingOut;
    private bool _topEdgeNoted;
    private DispatcherQueue? _queue;
    private bool _shuttingDown;

    /// <summary>Create on the UI thread.</summary>
    internal ShellHost()
    {
        Tasks = new TaskService(DispatcherQueue.GetForCurrentThread());
    }

    internal SettingsStore Settings { get; } = new();

    internal PinStore Pins { get; } = new();

    internal TaskService Tasks { get; }

    internal IconService Icons { get; } = new();

    internal DesktopToggle Desktop { get; } = new();

    internal AppCatalog Catalog { get; } = new();

    /// <summary>Machitan's Start menu, or null when it could not be created (Start then hands over to Windows').</summary>
    internal StartMenuWindow? StartMenu { get; private set; }

    /// <summary>True while a real full-screen app (a game, a video) is in front; the bar and the corner widgets step aside.</summary>
    internal bool FullScreenAppInFront { get; private set; }

    /// <summary>Raised on the UI thread when <see cref="FullScreenAppInFront"/> changes.</summary>
    internal event Action? FullScreenChanged;

    /// <summary>The "System status" text in Settings: the Windows key hook and each shortcut, as they really are.</summary>
    internal string SystemStatus =>
        _keyboard?.Describe() ?? "Windows key hook and shortcuts: not started (see diagnostics.log).";

    /// <summary>Opens the Settings window, or brings it forward when it is already open.</summary>
    internal void OpenSettings()
    {
        if (_shuttingDown)
        {
            return;
        }

        try
        {
            if (_settingsWindow is null)
            {
                _settingsWindow = new SettingsWindow(this);
                _settingsWindow.Closed += (_, _) =>
                {
                    _settingsWindow = null;
                    Log.Info("Settings window closed.");
                };
                Log.Info("Settings window opened.");
            }
            else
            {
                _settingsWindow.RefreshStatus();
                Log.Info("Settings window was already open; brought to the front.");
            }

            _settingsWindow.Activate();
        }
        catch (Exception ex)
        {
            Log.Error("The Settings window could not be opened.", ex);
        }
    }

    /// <summary>Win+N: the calendar flyout. Made on first use.</summary>
    internal void ToggleCalendar()
    {
        if (_shuttingDown)
        {
            return;
        }

        try
        {
            _calendar ??= new CalendarFlyout(this);
            if (!_calendar.IsOpen)
            {
                CloseOtherPopups(_calendar);
            }

            _calendar.Toggle();
        }
        catch (Exception ex)
        {
            Log.Error("The calendar could not be opened.", ex);
        }
    }

    /// <summary>Win+A: the quick settings flyout. Made on first use.</summary>
    internal void ToggleQuickSettings()
    {
        if (_shuttingDown)
        {
            return;
        }

        try
        {
            _quickSettings ??= new QuickSettingsFlyout(this);
            if (!_quickSettings.IsOpen)
            {
                CloseOtherPopups(_quickSettings);
            }

            _quickSettings.Toggle();
        }
        catch (Exception ex)
        {
            Log.Error("Quick settings could not be opened.", ex);
        }
    }

    /// <summary>Win+Tab and Win+`: the window switcher. Made on first use.</summary>
    internal void ShowSwitcher()
    {
        if (_shuttingDown)
        {
            return;
        }

        try
        {
            _switcher ??= new SwitcherWindow(this);
            if (!_switcher.IsOpen)
            {
                CloseOtherPopups(_switcher);
            }

            _switcher.ShowOrAdvance();
        }
        catch (Exception ex)
        {
            Log.Error("The switcher could not be opened.", ex);
        }
    }

    /// <summary>The bar's window (0 before it exists).</summary>
    internal nint BarHandle => _bar?.Handle ?? 0;

    /// <summary>True while a window preview shows above the bar (the bar then stays up).</summary>
    internal bool PreviewShown => _preview?.IsOpen ?? false;

    /// <summary>The pointer rests on a dock button; <paramref name="button"/> is its place on screen (physical pixels).</summary>
    internal void HoverDockItem(DockItem item, RECT button)
    {
        if (_shuttingDown)
        {
            return;
        }

        try
        {
            if (item.Group is null || item.Group.Windows.Count == 0)
            {
                // Not running: no preview to make; an open one goes.
                _preview?.HoverStart(item, button);
                return;
            }

            _preview ??= new PreviewWindow(this);
            _preview.HoverStart(item, button);
        }
        catch (Exception ex)
        {
            Log.Error("The window preview could not be shown.", ex);
        }
    }

    /// <summary>The pointer left a dock button.</summary>
    internal void EndDockHover()
    {
        _preview?.HoverEnd();
    }

    /// <summary>Closes the window preview at once (a click on the dock, the bar sliding away).</summary>
    internal void HidePreview()
    {
        _preview?.HideFlyout();
    }

    /// <summary>One pop-up at a time: Start and the other flyouts close.</summary>
    private void CloseOtherPopups(FlyoutWindow keep)
    {
        StartMenu?.HideMenu();
        foreach (FlyoutWindow? flyout in new FlyoutWindow?[] { _calendar, _quickSettings, _switcher, _preview })
        {
            if (flyout is not null && !ReferenceEquals(flyout, keep))
            {
                flyout.HideFlyout();
            }
        }
    }

    /// <summary>The bar tells the shell when a full-screen app comes to the front or leaves.</summary>
    internal void NoteFullScreen(bool fullScreen)
    {
        if (fullScreen == FullScreenAppInFront)
        {
            return;
        }

        FullScreenAppInFront = fullScreen;
        FullScreenChanged?.Invoke();
    }

    /// <summary>
    /// Where a corner widget goes when another one shares its corner: 0 nearest the corner,
    /// 1 next to it. The info line comes first.
    /// </summary>
    internal int CornerSlotOf(CornerWidgetWindow widget)
    {
        int slot = 0;
        foreach (CornerWidgetWindow? other in new CornerWidgetWindow?[] { _infoLine, _duoRing })
        {
            if (ReferenceEquals(other, widget))
            {
                break;
            }

            if (other is not null && other.IsShown && other.Corner == widget.Corner)
            {
                slot++;
            }
        }

        return slot;
    }

    /// <summary>The top of the strip kept for the top widgets (physical pixels), or null when none is kept.</summary>
    internal int? TopStripTop => _topStrip?.Granted is RECT strip ? strip.Top : null;

    /// <summary>
    /// Keeps the top strip as tall as the top widgets need, then places every corner widget
    /// again (one appeared, went, or moved corner). Safe to call often.
    /// </summary>
    internal void LayoutCorners()
    {
        if (_layingOut)
        {
            return;
        }

        _layingOut = true;
        try
        {
            UpdateTopStrip();
            _infoLine?.Place();
            _duoRing?.Place();
        }
        catch (Exception ex)
        {
            Log.Error("The corner widgets could not be laid out.", ex);
        }
        finally
        {
            _layingOut = false;
        }
    }

    /// <summary>
    /// One row (10 + 36 + 10 DIP) when a top corner has one widget, two rows when both share a
    /// top corner; nothing when no widget is meant to be in a top corner, when the setting is
    /// off, while shutting down, or when the bar itself is docked at the top (its own strip
    /// already keeps maximised windows below, and the two must not compete).
    /// </summary>
    private void UpdateTopStrip()
    {
        ShellSettings settings = Settings.Current;
        int rows = 0;
        if (settings.ReserveTopStrip && !_shuttingDown)
        {
            foreach (ScreenCorner corner in new[] { ScreenCorner.TopLeft, ScreenCorner.TopRight })
            {
                int inCorner = 0;
                foreach (CornerWidgetWindow? widget in new CornerWidgetWindow?[] { _infoLine, _duoRing })
                {
                    if (widget is not null && widget.WantsSpace && widget.Corner == corner)
                    {
                        inCorner++;
                    }
                }

                rows = Math.Max(rows, inCorner);
            }
        }

        if (rows > 0 && settings.DockEdge == DockEdge.Top)
        {
            if (!_topEdgeNoted)
            {
                _topEdgeNoted = true;
                Log.Info("Top strip: not kept while the bar is docked at the top.");
            }

            rows = 0;
        }

        nint bar = BarHandle;
        if (rows == 0 || bar == 0 || !User32.TryGetMonitorRect(bar, out RECT monitor))
        {
            _topStrip?.Release();
            return;
        }

        uint dpi = User32.GetDpiForWindow(bar);
        double scale = (dpi == 0 ? 96 : dpi) / 96.0;
        double dip = (2 * CornerWidgetWindow.EdgeMarginDip)
            + (rows * CornerWidgetWindow.LineHeightDip)
            + ((rows - 1) * CornerWidgetWindow.StackGapDip);

        if (_topStrip is null)
        {
            _topStrip = new TopStrip();
            _topStrip.Moved += () => _queue?.TryEnqueue(LayoutCorners);
        }

        _topStrip.Set(monitor, (int)Math.Round(dip * scale));
    }

    /// <summary>Where the bar is on screen, in physical pixels (empty before it is placed).</summary>
    internal RECT BarRect => _bar?.BarScreenRect ?? default;

    internal void Start()
    {
        DispatcherQueue queue = DispatcherQueue.GetForCurrentThread();

        _queue = queue;
        Settings.Load();
        Settings.StartWatching(queue);
        Pins.Load();

        _bar = new BarWindow(this);
        _bar.Start();

        // Only now that Machitan's bar is on screen: no moment without any taskbar.
        // (Restored in Main's finally block, on sign-out, and by the guardian.)
        ExplorerTaskbar.Hide();

        try
        {
            StartMenu = new StartMenuWindow(this);
            Catalog.RefreshIfStale(StartMenu.ListIconPixels, StartMenu.IsDark);
        }
        catch (Exception ex)
        {
            StartMenu = null;
            Log.Error("The Start menu could not be created; Start hands over to Windows' own.", ex);
        }

        try
        {
            StartMenuWindow? menu = StartMenu;
            _keyboard = KeyboardService.Start(
                queue,
                menu is not null ? menu.ToggleFromKeyboard : null,
                HotkeyActions.Build(this));
        }
        catch (Exception ex)
        {
            _keyboard = null;
            Log.Error("The keyboard hook and shortcuts could not be started; Windows handles the keys as usual.", ex);
        }

        StartCornerWidgets();

        // The listener keeps itself alive on its own background thread.
        _ = new ExitSignal(queue, Shutdown);
        Log.Info("Shell started.");
    }

    /// <summary>The info line and the status ring; each one that fails stays off for this run.</summary>
    private void StartCornerWidgets()
    {
        try
        {
            _infoLine = new InfoLineWindow(this);
            _infoLine.Start();
        }
        catch (Exception ex)
        {
            _infoLine = null;
            Log.Error("The info line could not be created; it stays off this run.", ex);
        }

        try
        {
            _duoRing = new DuoRingWindow(this);
            _duoRing.Start();
        }
        catch (Exception ex)
        {
            _duoRing = null;
            Log.Error("The status ring could not be created; it stays off this run.", ex);
        }
    }

    /// <summary>
    /// Closes the shell cleanly and puts Explorer's taskbar back.
    ///
    /// Done in steps. Right away: the keyboard hook stops and every pop-up is hidden. Then,
    /// once whatever asked for the exit has finished (a click on "Exit Machitan" is still
    /// closing its menu at that moment), the windows close one at a time, each in a turn of
    /// its own, and last the bar closes, the taskbar comes back and the app ends. Closing
    /// windows underneath a menu that is still closing once ended in an internal Windows App
    /// SDK error (0xC000027B). Each step is logged, so a failure shows where it happened.
    /// </summary>
    internal void Shutdown()
    {
        if (_shuttingDown)
        {
            return;
        }

        _shuttingDown = true;
        Log.Info("Shutdown requested.");

        try
        {
            // First, so no key press reaches a window that is closing.
            _keyboard?.Stop();
        }
        catch (Exception ex)
        {
            Log.Error("The keyboard hook did not stop cleanly.", ex);
        }

        try
        {
            StartMenu?.HideMenu();
            foreach (FlyoutWindow? flyout in new FlyoutWindow?[] { _calendar, _quickSettings, _switcher, _preview })
            {
                flyout?.HideFlyout();
            }

            _infoLine?.Stop();
            _duoRing?.Stop();

            // Maximised windows get the top of the screen back at once.
            _topStrip?.Dispose();
            _topStrip = null;
        }
        catch (Exception ex)
        {
            Log.Error("Start, a flyout or a corner widget could not be hidden.", ex);
        }

        var steps = new Queue<(string Name, Action Run)>();
        if (_settingsWindow is SettingsWindow settings)
        {
            steps.Enqueue(("the Settings window", settings.Close));
        }

        if (StartMenu is StartMenuWindow start)
        {
            steps.Enqueue(("the Start menu", start.Close));

            // From here on the Start button and Win do nothing, so Start cannot reopen while closing.
            StartMenu = null;
        }

        foreach ((string name, FlyoutWindow? flyout) in new (string, FlyoutWindow?)[]
                 {
                     ("the calendar", _calendar),
                     ("quick settings", _quickSettings),
                     ("the switcher", _switcher),
                     ("the window preview", _preview),
                 })
        {
            if (flyout is not null)
            {
                steps.Enqueue((name, flyout.Close));
            }
        }

        if (_infoLine is InfoLineWindow infoLine)
        {
            steps.Enqueue(("the info line", infoLine.Close));
        }

        if (_duoRing is DuoRingWindow duoRing)
        {
            steps.Enqueue(("the status ring", duoRing.Close));
        }

        if (_queue is null || !_queue.TryEnqueue(DispatcherQueuePriority.Low, () => RunShutdownSteps(steps)))
        {
            // No dispatcher to wait for: do it all now.
            while (steps.Count > 0)
            {
                RunShutdownStep(steps.Dequeue());
            }

            FinishShutdown();
        }
    }

    /// <summary>One window per turn; the bar and the app's end come last.</summary>
    private void RunShutdownSteps(Queue<(string Name, Action Run)> steps)
    {
        if (steps.Count == 0)
        {
            FinishShutdown();
            return;
        }

        RunShutdownStep(steps.Dequeue());
        if (_queue is null || !_queue.TryEnqueue(DispatcherQueuePriority.Low, () => RunShutdownSteps(steps)))
        {
            RunShutdownSteps(steps);
        }
    }

    private static void RunShutdownStep((string Name, Action Run) step)
    {
        try
        {
            Log.Info("Shutdown: closing " + step.Name + ".");
            step.Run();
        }
        catch (Exception ex)
        {
            Log.Error("Shutdown: " + step.Name + " did not close cleanly.", ex);
        }
    }

    /// <summary>The bar, then Explorer's taskbar comes back, then the app ends.</summary>
    private void FinishShutdown()
    {
        try
        {
            Log.Info("Shutdown: closing the bar.");
            _bar?.PrepareForExit();
        }
        catch (Exception ex)
        {
            Log.Error("The bar did not close cleanly.", ex);
        }

        // Also done in Main's finally block and by the guardian; doing it here first means
        // the taskbar is back before the process starts to unwind.
        ExplorerTaskbar.Restore();

        try
        {
            Application.Current.Exit();
        }
        catch (Exception ex)
        {
            Log.Error("Application exit failed.", ex);
        }
    }
}
