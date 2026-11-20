using System;
using System.Collections.Generic;
using Machitan.Bar;
using Machitan.Config;
using Machitan.Core;
using Machitan.Custom;
using Machitan.Flyouts;
using Machitan.Interop;
using Machitan.SettingsUi;
using Machitan.Start;
using Machitan.Visuals;
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
    private SystemUiWatcher? _systemUi;
    private CalendarFlyout? _calendar;
    private QuickSettingsFlyout? _quickSettings;
    private SwitcherWindow? _switcher;
    private PreviewWindow? _preview;
    private SearchResultsFlyout? _searchResults;
    private TrayBoxWindow? _trayBox;
    private WinXMenuWindow? _winX;
    private InfoLineWindow? _infoLine;
    private DuoRingWindow? _duoRing;
    private DynalandWindow? _dynaland;
    private TopStrip? _topStrip;
    private DispatcherQueueTimer? _fitTimer;
    private int _fitPasses;
    private WindowChangeWatch? _windowWatch;
    private TopStripCoverWindow? _cover;
    private bool _coverFailed;
    private bool _layingOut;
    private DispatcherQueue? _queue;
    private bool _shuttingDown;

    /// <summary>Create on the UI thread.</summary>
    internal ShellHost()
    {
        Tasks = new TaskService(DispatcherQueue.GetForCurrentThread());
        Tasks.ShortcutAppIds = program => Catalog.ShortcutAppIdsFor(program);
        Accent = new AccentService(DispatcherQueue.GetForCurrentThread());
        Custom = new CustomMode(this);
        Tray = new TrayIconService(DispatcherQueue.GetForCurrentThread());
    }

    /// <summary>
    /// The tray icons, received straight from the apps (Batch 6): shown in Dynaland's general
    /// page, or in the bar's tray bubble. Runs while either is on (once started, until Machitan stops).
    /// </summary>
    internal TrayIconService Tray { get; }

    /// <summary>Starts the tray receiver when Dynaland or the bar's tray bubble needs it.</summary>
    private void StartTrayWhenWanted()
    {
        ShellSettings s = Settings.Current;
        if (!_shuttingDown && (s.EnableDynaland || s.TrayOnBar))
        {
            Tray.Start();
        }
    }

    /// <summary>Custom mode (Batch 7): picking, removing and choosing variants of the bar's elements.</summary>
    internal CustomMode Custom { get; }

    /// <summary>
    /// The apps in the order the bar shows them, from the left: the shown bubbles in their order,
    /// each bubble's apps in its order (what dragging changes). Win+1..9 count these; built
    /// fresh from the pins and running apps each time.
    /// </summary>
    internal List<DockItem> BarOrderedApps()
    {
        List<DockItem> items = DockModel.Build(Pins.Pins, Tasks.Groups);
        Dictionary<string, List<DockItem>> arranged = BarLayout.Arrange(items);
        IReadOnlyList<string> order = _bar is BarWindow bar && bar.ShownBubbleIds.Count > 0 ? bar.ShownBubbleIds : BarLayout.Order;
        var ordered = new List<DockItem>(items.Count);
        foreach (string id in order)
        {
            if (arranged.TryGetValue(id, out List<DockItem>? list))
            {
                ordered.AddRange(list);
            }
        }

        return ordered;
    }

    /// <summary>How many bubbles the bar shows now (custom mode keeps at least one).</summary>
    internal int ShownBubbleCount => _bar?.ShownBubbleCount ?? 0;

    /// <summary>The title of what plays now, as Dynaland shows it, or null.</summary>
    internal string? NowPlayingTitle => _dynaland?.NowPlayingTitle;

    /// <summary>Where a custom-mode piece is on screen (physical pixels), or null when it is not shown.</summary>
    internal RECT? ElementRect(CustomTarget target)
    {
        return target.Piece switch
        {
            CustomPiece.Start => _bar?.BubbleScreenRect(BarLayoutStore.StartId),
            CustomPiece.Apps => _bar?.BubbleScreenRect(BarLayoutStore.AppsId),
            CustomPiece.Group => target.GroupId is string id ? _bar?.BubbleScreenRect(id) : null,
            CustomPiece.Tray => _bar?.BubbleScreenRect(BarLayoutStore.TrayId),
            CustomPiece.Dynaland => _dynaland?.BadgeRect,
            CustomPiece.InfoLine => _infoLine?.ShapeScreenRect(),
            CustomPiece.DuoRing => _duoRing?.ShapeScreenRect(),
            _ => null,
        };
    }

    internal SettingsStore Settings { get; } = new();

    /// <summary>Apps pinned to the bar ("Pin to taskbar").</summary>
    internal PinStore Pins { get; } = new(AppPaths.PinsFile, "Pins");

    /// <summary>Apps pinned to Start's Pinned box ("Pin to Start"); first a copy of the bar's list.</summary>
    internal PinStore StartPins { get; } = new(AppPaths.StartPinsFile, "Start pins", AppPaths.PinsFile);

    /// <summary>The bar's bubbles and which apps sit where.</summary>
    internal BarLayoutStore BarLayout { get; } = new();

    internal TaskService Tasks { get; }

    internal IconService Icons { get; } = new();

    internal DesktopToggle Desktop { get; } = new();

    /// <summary>Machitan's accent colour: the wallpaper's strongest colour, or Windows' accent (Batch 7).</summary>
    internal AccentService Accent { get; }

    internal AppCatalog Catalog { get; } = new();

    /// <summary>Machitan's Start menu, or null when it could not be created (Start then hands over to Windows').</summary>
    internal StartMenuWindow? StartMenu { get; private set; }

    /// <summary>True while a real full-screen app (a game, a video) is in front; the bar and the corner widgets step aside.</summary>
    internal bool FullScreenAppInFront { get; private set; }

    /// <summary>Raised on the UI thread when <see cref="FullScreenAppInFront"/> changes.</summary>
    internal event Action? FullScreenChanged;

    /// <summary>The "System status" text in Settings: the Windows key hook and each shortcut, as they really are.</summary>
    internal string SystemStatus =>
        _keyboard?.Describe() ?? Strings.KbNotStarted;

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

    /// <summary>Win+X: Machitan's Win+X menu above the Start button. Made on first use.</summary>
    internal void ToggleWinXMenu()
    {
        if (_shuttingDown)
        {
            return;
        }

        try
        {
            _winX ??= new WinXMenuWindow(this);
            if (!_winX.IsOpen)
            {
                CloseOtherPopups(_winX);
            }

            _winX.Toggle();
        }
        catch (Exception ex)
        {
            Log.Error("The Win+X menu could not be opened.", ex);
        }
    }

    /// <summary>The bar's window (0 before it exists).</summary>
    internal nint BarHandle => _bar?.Handle ?? 0;

    /// <summary>True while a window preview shows above the bar (the bar then stays up).</summary>
    internal bool PreviewShown => _preview?.IsOpen ?? false;

    /// <summary>The results of the bar's search box; made when first needed.</summary>
    internal SearchResultsFlyout SearchResults => _searchResults ??= new SearchResultsFlyout(this);

    /// <summary>Closes the bar search's results, if they are open.</summary>
    internal void HideSearchResults()
    {
        _searchResults?.HideFlyout();
    }

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
    /// <summary>Closes Start and every flyout except <paramref name="keep"/>.</summary>
    internal void CloseOtherPopups(FlyoutWindow keep)
    {
        StartMenu?.HideMenu();
        foreach (FlyoutWindow? flyout in new FlyoutWindow?[] { _calendar, _quickSettings, _switcher, _preview, _searchResults, _trayBox, _winX })
        {
            if (flyout is not null && !ReferenceEquals(flyout, keep))
            {
                flyout.HideFlyout();
            }
        }
    }

    /// <summary>True when the shortcuts-in-admin-apps option is on for this copy of Machitan.exe.</summary>
    internal bool AdminAppShortcutsOn
    {
        get
        {
            ShellSettings s = Settings.Current;
            return s.AdminAppShortcuts && string.Equals(s.AdminAppShortcutsFor, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>At start: the admin-keys helper, when its option is on for this copy.</summary>
    private void StartAdminKeysIfOn()
    {
        ShellSettings s = Settings.Current;
        if (!s.AdminAppShortcuts || _keyboard is null)
        {
            return;
        }

        if (!AdminAppShortcutsOn)
        {
            Log.Warn("Admin keys: the option was turned on for another copy of Machitan (" + s.AdminAppShortcutsFor + "); turn it on again in Settings > Shortcuts for this one.");
            return;
        }

        try
        {
            StartMenuWindow? menu = StartMenu;
            AdminKeys.Start(_keyboard, menu is not null ? menu.ToggleFromKeyboard : null);
        }
        catch (Exception ex)
        {
            Log.Error("Admin keys: the helper could not be started.", ex);
        }
    }

    /// <summary>
    /// Settings > Shortcuts: turns the shortcuts-in-admin-apps option on (Windows asks to make
    /// the task) or off (the helper ends; Windows asks to remove the task). True when it took.
    /// </summary>
    internal async System.Threading.Tasks.Task<bool> SetAdminAppShortcutsAsync(bool on)
    {
        if (on)
        {
            if (!await AdminKeys.RegisterTaskAsync())
            {
                return false;
            }

            string exe = Environment.ProcessPath ?? string.Empty;
            Settings.Update(s =>
            {
                s.AdminAppShortcuts = true;
                s.AdminAppShortcutsFor = exe;
            });
            StartAdminKeysIfOn();
            return true;
        }

        Settings.Update(s =>
        {
            s.AdminAppShortcuts = false;
            s.AdminAppShortcutsFor = string.Empty;
        });
        AdminKeys.Current?.Dispose();
        await AdminKeys.RemoveTaskAsync();
        return true;
    }

    /// <summary>The bar's ^ button: opens or closes the tray box by it (its place on screen, physical pixels).</summary>
    internal void ToggleTrayBox(RECT anchor)
    {
        if (_shuttingDown)
        {
            return;
        }

        try
        {
            _trayBox ??= new TrayBoxWindow(this);
            if (!_trayBox.IsOpen)
            {
                CloseOtherPopups(_trayBox);
            }

            _trayBox.Toggle(anchor);
        }
        catch (Exception ex)
        {
            Log.Error("The tray box could not be opened.", ex);
        }
    }

    /// <summary>An icon dragged out of the tray box: the bar's drag takes it over.</summary>
    internal void StartTrayDragFromBox(TrayIcon icon)
    {
        if (!_shuttingDown)
        {
            _bar?.BeginTrayDragFromBox(icon);
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
        UpdateCover();
    }

    /// <summary>True when the bar is placed and docked on that edge of the screen (top or bottom).</summary>
    internal bool BarOnEdge(bool top)
    {
        return BarRect.Width > 0 && (Settings.Current.DockEdge == DockEdge.Top) == top;
    }

    /// <summary>
    /// The widgets in one corner stand side by side, the info line nearest the corner (or the
    /// bar), then the ring. This is the room (DIP) the ones before <paramref name="widget"/>
    /// take, each with the gap after it.
    /// </summary>
    internal double WidthBefore(CornerWidgetWindow widget)
    {
        double before = 0;
        foreach (CornerWidgetWindow? other in new CornerWidgetWindow?[] { _infoLine, _duoRing })
        {
            if (ReferenceEquals(other, widget))
            {
                break;
            }

            if (other is not null && other.IsShown && other.Corner == widget.Corner)
            {
                before += other.VisibleWidthDip + CornerWidgetWindow.StackGapDip;
            }
        }

        return before;
    }

    /// <summary>The whole row of shown widgets in a corner (DIP): their widths and the gaps between.</summary>
    internal double CornerRowWidth(ScreenCorner corner)
    {
        double row = 0;
        int count = 0;
        foreach (CornerWidgetWindow? widget in new CornerWidgetWindow?[] { _infoLine, _duoRing })
        {
            if (widget is not null && widget.IsShown && widget.Corner == corner)
            {
                row += widget.VisibleWidthDip;
                count++;
            }
        }

        return row + (Math.Max(count - 1, 0) * CornerWidgetWindow.StackGapDip);
    }

    /// <summary>
    /// Dynaland's edge: the chosen one, or with Auto the edge without the bar (the top while
    /// the bar is at the bottom, the bottom while it is at the top).
    /// </summary>
    internal bool DynalandOnTop()
    {
        ShellSettings settings = Settings.Current;
        return settings.DynalandPosition switch
        {
            DynalandPosition.Top => true,
            DynalandPosition.Bottom => false,
            _ => settings.DockEdge != DockEdge.Top,
        };
    }

    /// <summary>
    /// The span (physical pixels) Dynaland's small pill can take when it sits in the bar's own
    /// band (same edge as the bar, the bar aligned to a side); null otherwise. Widgets that
    /// continue the bar's row stop before it.
    /// </summary>
    internal (int Left, int Right)? DynalandZoneInBarBand => _dynaland?.ZoneInBarBand;

    /// <summary>The bar moved or changed size: the widgets and Dynaland follow it.</summary>
    internal void NoteBarMoved()
    {
        QueueCornerLayout();
    }

    /// <summary>Places the corner widgets and Dynaland again, soon (a widget's width changed, say).</summary>
    internal void QueueCornerLayout()
    {
        _queue?.TryEnqueue(LayoutCorners);
    }

    /// <summary>
    /// The strip Machitan keeps at the free edge (physical pixels), when it is on the given
    /// edge; null when none is kept there.
    /// </summary>
    internal RECT? StripOn(bool top)
    {
        return _topStrip?.Granted is RECT strip && _topStrip.OnTop == top ? strip : null;
    }

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
            UpdateCover();

            // Dynaland first: widgets continuing the bar's row stop before it.
            _dynaland?.Place();
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
    /// Keeps the strip at the free edge (see KeepTopStrip), and places the bar again when the
    /// strip came, went or changed.
    /// </summary>
    private void UpdateTopStrip()
    {
        RECT? before = _topStrip?.Granted;
        KeepTopStrip();
        RECT? after = _topStrip?.Granted;
        bool same = before is RECT was ? after is RECT now && now.SameAs(was) : after is null;
        if (!same)
        {
            // The bar's own reservation sits next to the strip; when the strip comes, goes or
            // changes, the bar is placed again (otherwise a bar moved to the top could stay
            // below a strip that no longer exists).
            _bar?.RequestPlacement();
        }
    }

    /// <summary>
    /// The strip at the free edge (the one without the bar): one row (8 + the tallest widget's
    /// height + 8 DIP; 32 at standard size) when a widget is in a corner of that edge (widgets in one corner stand side by side), and at
    /// least the small Dynaland pill plus a margin while Dynaland is on that edge. Nothing when
    /// neither is there, when the setting is off, or while shutting down.
    /// </summary>
    private void KeepTopStrip()
    {
        ShellSettings settings = Settings.Current;
        bool stripTop = settings.DockEdge != DockEdge.Top;
        double dip = 0;
        if (settings.ReserveTopStrip && !_shuttingDown)
        {
            foreach (CornerWidgetWindow? widget in new CornerWidgetWindow?[] { _infoLine, _duoRing })
            {
                bool widgetTop = widget is not null && widget.Corner is ScreenCorner.TopLeft or ScreenCorner.TopRight;
                if (widget is not null && widget.WantsSpace && widgetTop == stripTop)
                {
                    // As tall as the tallest widget there (each has its own size setting).
                    dip = Math.Max(dip, (2 * CornerWidgetWindow.EdgeMarginDip) + widget.LineHeightNowDip);
                }
            }

            if (_dynaland is { WantsSpace: true } dynaland && DynalandOnTop() == stripTop)
            {
                dip = Math.Max(dip, dynaland.StripNeedDip);
            }
        }

        nint bar = BarHandle;
        if (dip <= 0 || bar == 0 || !User32.TryGetMonitorRect(bar, out RECT monitor))
        {
            _topStrip?.Release();
            StopWindowWatch();
            return;
        }

        uint dpi = User32.GetDpiForWindow(bar);
        double scale = (dpi == 0 ? 96 : dpi) / 96.0;

        if (_topStrip is null)
        {
            _topStrip = new TopStrip();
            _topStrip.Moved += () => _queue?.TryEnqueue(LayoutCorners);
            _topStrip.Applied += FitMaximizedSoon;
        }

        _topStrip.Set(monitor, (int)Math.Round(dip * scale), stripTop);
    }

    /// <summary>
    /// A moment after the strip is taken (Windows' own re-fit comes first), moves windows that
    /// fill the screen and still reach into it (see MaximizedFit); from then on, windows that
    /// appear, move or change size are checked one by one (WindowChangeWatch).
    /// </summary>
    private void FitMaximizedSoon()
    {
        if (_queue is null)
        {
            return;
        }

        if (_windowWatch is null)
        {
            try
            {
                _windowWatch = new WindowChangeWatch(_queue, CheckStripFor);
            }
            catch (Exception ex)
            {
                _windowWatch = null;
                Log.Warn("Strip: windows that move or change size later cannot be watched this run.", ex);
            }
        }

        if (_fitTimer is null)
        {
            // Twice: Windows may still re-fit windows itself after the first look (Explorer's
            // taskbar going to auto-hide at start changes the work area again).
            _fitTimer = _queue.CreateTimer();
            _fitTimer.Interval = TimeSpan.FromMilliseconds(800);
            _fitTimer.IsRepeating = true;
            _fitTimer.Tick += (_, _) =>
            {
                _fitPasses++;
                if (_fitPasses >= 2)
                {
                    _fitTimer?.Stop();
                }
                else if (_fitTimer is not null)
                {
                    _fitTimer.Interval = TimeSpan.FromMilliseconds(2000);
                }

                if (!_shuttingDown && BarHandle != 0 && _topStrip?.Granted is RECT strip)
                {
                    MaximizedFit.Run(BarHandle, strip, _topStrip.OnTop, _fitPasses >= 2 ? "2.8 s after the strip was taken" : "0.8 s after the strip was taken");
                }
            };
        }

        _fitTimer.Stop();
        _fitPasses = 0;
        _fitTimer.Interval = TimeSpan.FromMilliseconds(800);
        _fitTimer.Start();
    }

    /// <summary>A window appeared, moved or changed size: keeps it out of the strip if it fills the screen.</summary>
    private void CheckStripFor(nint hwnd)
    {
        if (!_shuttingDown && BarHandle != 0 && _topStrip?.Granted is RECT strip)
        {
            MaximizedFit.Check(hwnd, BarHandle, strip, _topStrip.OnTop);
        }
    }

    private void StopWindowWatch()
    {
        _windowWatch?.Dispose();
        _windowWatch = null;
    }

    /// <summary>
    /// The optional cover over the top strip: shown while the setting is on, the strip is kept
    /// and no full-screen app is in front; the widgets and Dynaland go back above it when it moves.
    /// </summary>
    private void UpdateCover()
    {
        CoverTint? before = CoverTintNow;
        try
        {
            ShowOrHideCover();
        }
        finally
        {
            if (CoverTintNow != before)
            {
                CoverChanged?.Invoke();
            }
        }
    }

    /// <summary>Raised when the cover appears, goes, or changes colour or opacity.</summary>
    internal event Action? CoverChanged;

    private CoverTint? CoverTintNow => _cover is { IsShown: true } cover ? cover.Tint : null;

    /// <summary>
    /// The cover's colour over <paramref name="region"/> (physical pixels), or null when no cover
    /// is shown there. The status ring blends it with the wallpaper to choose its ink.
    /// </summary>
    internal CoverTint? CoverOver(RECT region)
    {
        if (_cover is not { IsShown: true } cover || _topStrip?.Granted is not RECT strip)
        {
            return null;
        }

        bool overlaps = region.Left < strip.Right && region.Right > strip.Left && region.Top < strip.Bottom && region.Bottom > strip.Top;
        return overlaps ? cover.Tint : null;
    }

    private void ShowOrHideCover()
    {
        ShellSettings settings = Settings.Current;
        if (!settings.TopStripCover || _shuttingDown || FullScreenAppInFront || _topStrip?.Granted is not RECT strip || _coverFailed)
        {
            _cover?.HideCover();
            return;
        }

        try
        {
            _cover ??= new TopStripCoverWindow();
            _cover.AccentColour = Accent.Current;
            nint bar = BarHandle;
            uint dpi = bar != 0 ? User32.GetDpiForWindow(bar) : 96;
            if (_cover.ShowOver(strip, settings, (dpi == 0 ? 96 : dpi) / 96.0))
            {
                _infoLine?.KeepOnTop();
                _duoRing?.KeepOnTop();
                _dynaland?.KeepOnTop();
            }
        }
        catch (Exception ex)
        {
            _coverFailed = true;
            Log.Error("The top strip cover could not be shown; it stays off this run.", ex);
        }
    }

    /// <summary>Where the bar is on screen, in physical pixels (empty before it is placed).</summary>
    internal RECT BarRect => _bar?.BarScreenRect ?? default;

    internal void Start()
    {
        DispatcherQueue queue = DispatcherQueue.GetForCurrentThread();

        _queue = queue;
        Settings.Load();

        // Before any window is made: its text comes in this language.
        Languages.Apply(Settings.Current.Language);
        Settings.StartWatching(queue);
        Pins.Load();
        StartPins.Load();
        BarLayout.Load();

        Accent.Start(Settings.Current.AccentSource);
        Settings.Changed += () => Accent.SetSource(Settings.Current.AccentSource);
        Settings.Changed += StartTrayWhenWanted;
        Accent.Changed += () =>
        {
            // The strip cover's accent colour (the bar and Start follow the accent themselves).
            if (!_shuttingDown)
            {
                LayoutCorners();
            }
        };

        _bar = new BarWindow(this);
        _bar.Start();
        StartTrayWhenWanted();

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

        StartAdminKeysIfOn();

        try
        {
            _systemUi = new SystemUiWatcher(this);
        }
        catch (Exception ex)
        {
            _systemUi = null;
            Log.Error("Windows' Start cannot be watched this run.", ex);
        }

        StartCornerWidgets();

        // The listener keeps itself alive on its own background thread.
        _ = new ExitSignal(queue, Shutdown);
        Log.Info("Shell started.");
    }

    /// <summary>The info line, the status ring and Dynaland; each one that fails stays off for this run.</summary>
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

        try
        {
            _dynaland = new DynalandWindow(this);
            _dynaland.Start();
        }
        catch (Exception ex)
        {
            _dynaland = null;
            Log.Error("Dynaland could not be created; it stays off this run.", ex);
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
        Accent.Dispose();

        try
        {
            // First, so no key press reaches a window that is closing.
            _keyboard?.Stop();
            AdminKeys.Current?.Dispose();
            _systemUi?.Dispose();
            _systemUi = null;
        }
        catch (Exception ex)
        {
            Log.Error("The keyboard hook did not stop cleanly.", ex);
        }

        try
        {
            _bar?.CancelDrag();
            Custom.Done();
            StartMenu?.HideMenu();
            foreach (FlyoutWindow? flyout in new FlyoutWindow?[] { _calendar, _quickSettings, _switcher, _preview, _searchResults, _trayBox, _winX })
            {
                flyout?.HideFlyout();
            }

            _infoLine?.Stop();
            _duoRing?.Stop();
            _dynaland?.Stop();
            _cover?.HideCover();

            // Maximised windows get the top of the screen back at once.
            StopWindowWatch();
            _fitTimer?.Stop();
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
                     ("the search results", _searchResults),
                     ("the tray box", _trayBox),
                     ("the Win+X menu", _winX),
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

        if (_dynaland is DynalandWindow dynaland)
        {
            steps.Enqueue(("Dynaland", dynaland.Close));
        }

        if (_cover is TopStripCoverWindow cover)
        {
            steps.Enqueue(("the top strip cover", cover.Close));
        }

        if (_bar?.DragLayer is BarDragLayer dragLayer)
        {
            steps.Enqueue(("the bar's drag layer", dragLayer.Close));
        }

        foreach (Window custom in Custom.OpenWindows)
        {
            steps.Enqueue(("a custom mode window", custom.Close));
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
            // The tray receiver steps aside, so the apps reach Explorer's tray again.
            Tray.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error("The tray receiver did not stop cleanly.", ex);
        }

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
