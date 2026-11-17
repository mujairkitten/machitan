using System;
using Machitan.Bar;
using Machitan.Config;
using Machitan.Core;
using Machitan.Flyouts;
using Machitan.Interop;
using Machitan.SettingsUi;
using Machitan.Start;
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

    /// <summary>The "System status" text in Settings: the Windows key hook and each shortcut, as they really are.</summary>
    internal string SystemStatus =>
        _keyboard?.Describe() ?? "Windows key hook and shortcuts: not started (see diagnostics.log).";

    /// <summary>Opens the Settings window, or brings it forward when it is already open.</summary>
    internal void OpenSettings()
    {
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

    /// <summary>One pop-up at a time: Start and the other flyouts close.</summary>
    private void CloseOtherPopups(FlyoutWindow keep)
    {
        StartMenu?.HideMenu();
        foreach (FlyoutWindow? flyout in new FlyoutWindow?[] { _calendar, _quickSettings, _switcher })
        {
            if (flyout is not null && !ReferenceEquals(flyout, keep))
            {
                flyout.HideFlyout();
            }
        }
    }

    /// <summary>Where the bar is on screen, in physical pixels (empty before it is placed).</summary>
    internal RECT BarRect => _bar?.BarScreenRect ?? default;

    internal void Start()
    {
        DispatcherQueue queue = DispatcherQueue.GetForCurrentThread();

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

        // The listener keeps itself alive on its own background thread.
        _ = new ExitSignal(queue, Shutdown);
        Log.Info("Shell started.");
    }

    /// <summary>Closes the shell cleanly and puts Explorer's taskbar back.</summary>
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
            _settingsWindow?.Close();
            StartMenu?.HideMenu();
            StartMenu?.Close();
            foreach (FlyoutWindow? flyout in new FlyoutWindow?[] { _calendar, _quickSettings, _switcher })
            {
                flyout?.HideFlyout();
                flyout?.Close();
            }
        }
        catch (Exception ex)
        {
            Log.Error("Start or a flyout did not close cleanly.", ex);
        }

        try
        {
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
