using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;
using Machitan.Native;
using Machitan.StartMenu;

namespace Machitan;

public sealed partial class MainWindow : Window
{
    private const int PillRadiusDip = 8;

    public static double CurrentIconSize { get; private set; } = 28;
    public static double GetTaskIconSize() => CurrentIconSize;
    public static double GetTaskButtonWidth() => Math.Max(40, CurrentIconSize + 14);
    public static double GetTaskButtonHeight() => Math.Max(42, CurrentIconSize + 16);

    private MachitanSettings _settings = MachitanSettingsStore.Load();
    private readonly ObservableCollection<TaskWindowItem> _tasks = new();
    private readonly Dictionary<string, ImageSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);

    private IntPtr _hwnd;
    private StartMenuWindow? _menu;
    private SegmentWindow? _widgetsSegment;
    private SegmentWindow? _traySegment;
    private bool _widgetsShown;
    private bool _trayShown;
    private bool _segmentsFailed;
    private WeatherInfo? _lastWeather;
    private int _lastBatteryPercent = -1;
    private bool _lastBatteryCharging;
    private bool _hasBattery;
    private DateTime _menuClosedAt = DateTime.MinValue;
    private SettingsWindow? _settingsWindow;

    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _taskTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _repositionTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private readonly DispatcherTimer _weatherTimer = new() { Interval = TimeSpan.FromMinutes(30) };

    private bool _touchMode;
    private bool _touchDetectionFailed;

    public MainWindow()
    {
        DiagnosticsLog.Write("MainWindow ctor: before InitializeComponent");
        this.InitializeComponent();
        DiagnosticsLog.Write("MainWindow ctor: after InitializeComponent");

        CurrentIconSize = _settings.IconSize;
        TaskRepeater.ItemsSource = _tasks;

        _hwnd = WindowNative.GetWindowHandle(this);
        ApplyWindowStyle();
        DiagnosticsLog.Write("MainWindow ctor: after ApplyWindowStyle");

        // If Explorer restarts while Machitan runs, its new taskbar appears
        // next to ours — hide it again and re-register the appbar.
        ShellEvents.InstallTaskbarCreatedHook(_hwnd, () => Safe("taskbar created", () =>
        {
            ExplorerTaskbarController.Rehide();
            AppBarController.Register(_hwnd, BarHeightPx(), _settings.DockEdge);
            EnsurePosition();
        }));

        // Apply the saved settings once at startup: before this, the saved
        // icon size and Classic-mode chrome only took effect after the user
        // changed something in the settings window.
        ApplySettings(_settings);

        UpdateBattery();
        _ = RefreshWeatherAsync();

        _clockTimer.Tick += (_, _) => { Safe("clock tick", UpdateClock); Safe("battery tick", UpdateBattery); };
        _weatherTimer.Tick += (_, _) => Safe("weather tick", () => _ = RefreshWeatherAsync());
        _taskTimer.Tick += (_, _) => Safe("task tick", RefreshTasks);
        _repositionTimer.Tick += (_, _) => Safe("reposition tick", EnsurePosition);

        UpdateClock();
        RefreshTasks();
        DiagnosticsLog.Write("MainWindow ctor: after initial refresh");

        _clockTimer.Start();
        _taskTimer.Start();
        _repositionTimer.Start();
        _weatherTimer.Start();

        // Position after the window has been shown by Activate().
        DispatcherQueue.TryEnqueue(() =>
        {
            Safe("initial reposition", () =>
            {
                AppBarController.Register(_hwnd, BarHeightPx(), _settings.DockEdge);
                AppBarController.Reposition(_hwnd, BarHeightPx(), _settings.DockEdge, ComputeDesiredWidthDip(), _settings.DockAlignment, _settings.TaskbarMode);
                PositionSegments();
            });
            DiagnosticsLog.Write("MainWindow ctor: after initial reposition");
        });

        Closed += OnClosed;
    }

    private void Safe(string context, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            DiagnosticsLog.WriteException(context, ex);
        }
    }

    private int BarHeightPx()
    {
        double size = _settings.TaskbarMode == TaskbarMode.Compact
            ? _settings.BarSize * 0.8
            : _settings.BarSize;
        return (int)Math.Round(size) + (_touchMode ? 10 : 0);
    }

    // The icon size actually rendered: Compact steps it down, and a crowded
    // bar steps it down further instead of overflowing.
    private double EffectiveIconSize()
    {
        double size = _settings.IconSize;
        if (_settings.TaskbarMode == TaskbarMode.Compact)
            size -= 6;
        if (_tasks.Count > 10)
            size -= Math.Min(8, _tasks.Count - 10);
        return Math.Max(18, size);
    }

    // Touch interaction mode gets a slightly taller bar (the reference
    // video's tablet behavior). Detection is best-effort: if the API is
    // unavailable unpackaged, it is tried once and never again.
    private void UpdateTouchMode()
    {
        if (_touchDetectionFailed)
            return;

        try
        {
            _touchMode = Windows.UI.ViewManagement.UIViewSettings.GetForCurrentView()
                .UserInteractionMode == Windows.UI.ViewManagement.UserInteractionMode.Touch;
        }
        catch
        {
            _touchDetectionFailed = true;
        }
    }

    private double DpiScale()
    {
        uint dpi = NativeMethods.GetDpiForWindow(_hwnd);
        return dpi == 0 ? 1 : dpi / 96d;
    }

    private void ApplyWindowStyle()
    {
        DiagnosticsLog.Write("ApplyWindowStyle: backdrop");
        ApplyBackdrop();
        DiagnosticsLog.Write("ApplyWindowStyle: borderless");
        WindowChrome.MakeBorderless(_hwnd, topmost: true);
        WindowChrome.SetCornerPreference(_hwnd, NativeMethods.DWMWCP_ROUND);

        RootGrid.RequestedTheme = _settings.Theme == ShellTheme.Dark ? ElementTheme.Dark : ElementTheme.Light;

        try
        {
            AppWindow.IsShownInSwitchers = false;
        }
        catch
        {
            // Older Windows builds.
        }
    }

    private void ApplyBackdrop()
    {
        if (_settings.EnableAcrylic)
        {
            SystemBackdrop = new DesktopAcrylicBackdrop();
            // Tint layer over acrylic — opacity controls how much desktop shows through.
            byte alpha = (byte)Math.Clamp(Math.Round(180 * _settings.Opacity), 0, 255);
            var tint = _settings.Theme == ShellTheme.Dark
                ? new Windows.UI.Color { A = alpha, R = 21, G = 24, B = 28 }
                : new Windows.UI.Color { A = alpha, R = 243, G = 243, B = 243 };
            PillBorder.Background = new SolidColorBrush(tint);
        }
        else
        {
            SystemBackdrop = null;
            var solid = _settings.Theme == ShellTheme.Dark
                ? new Windows.UI.Color { A = 255, R = 32, G = 32, B = 32 }
                : new Windows.UI.Color { A = 255, R = 243, G = 243, B = 243 };
            PillBorder.Background = new SolidColorBrush(solid);
        }
        DiagnosticsLog.Write($"ApplyBackdrop: acrylic={_settings.EnableAcrylic} opacity={_settings.Opacity:F2}");
    }

    private int ComputeDesiredWidthDip()
    {
        if (_settings.TaskbarMode == TaskbarMode.Classic)
            return 9999;

        double taskButtonW = GetTaskButtonWidth();
        int tasksOnlyWidth = (int)Math.Round(_tasks.Count * (taskButtonW + 6));
        int centerChromeOnly = 44 * 3 + 6 * 3 + 8;

        if (_settings.TaskbarMode == TaskbarMode.Default)
        {
            // Center segment only: the weather capsule and tray live in
            // their own floating segment windows.
            return Math.Clamp(centerChromeOnly + tasksOnlyWidth + 28, 320, 1920);
        }

        // Joined / Compact: all zones inside the single pill.
        // Zones: weather (left) | Start/Search/TaskView + tasks (center) |
        // quick settings + battery + clock + bell + settings (right).
        int leftWidth = WeatherCluster.Visibility != Visibility.Visible
            ? 0
            : WeatherTextStack.Visibility == Visibility.Visible ? 128 : 40;
        int centerChrome = 44 * 3 + 6 * 3 + 8;
        double buttonW = GetTaskButtonWidth();
        int tasksWidth = (int)Math.Round(_tasks.Count * (buttonW + 6));
        int rightWidth = 76 + 4 + 88 + 4 + 44 + 4 + 44 + 10;
        if (BatteryButton.Visibility == Visibility.Visible)
            rightWidth += 70;
        int paddingAndGaps = 40;

        int total = leftWidth + centerChrome + tasksWidth + rightWidth + paddingAndGaps;
        // (centerChrome/tasksWidth above already include the center zone)
        double scale = DpiScale();
        int screenDip = scale > 0
            ? (int)Math.Round(NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN) / scale)
            : 1920;
        int maxWidth = Math.Max(720, (int)Math.Round(screenDip * 0.96));
        return Math.Clamp(total, 720, maxWidth);
    }

    private void EnsurePosition()
    {
        UpdateTouchMode();
        int desiredWidth = ComputeDesiredWidthDip();
        if (!AppBarController.IsAtDesiredPosition(_hwnd, BarHeightPx(), _settings.DockEdge, desiredWidth, _settings.DockAlignment, _settings.TaskbarMode))
        {
            AppBarController.Reposition(_hwnd, BarHeightPx(), _settings.DockEdge, desiredWidth, _settings.DockAlignment, _settings.TaskbarMode);
        }

        PositionSegments();
    }

    // Default mode's side segments: same top/height as the main pill,
    // widgets pinned to the left screen edge, tray to the right.
    private void PositionSegments()
    {
        if (_settings.TaskbarMode != TaskbarMode.Default)
            return;

        // Self-heal: if a wanted segment window was closed, recreate it.
        bool wantWidgets = _settings.ShowWeather;
        if (((wantWidgets && _widgetsSegment == null) || _traySegment == null) && !_segmentsFailed)
            UpdateSegmentMode();

        if (!NativeMethods.GetWindowRect(_hwnd, out NativeMethods.RECT main))
            return;

        int height = main.Bottom - main.Top;
        if (height <= 0)
            return;

        double scale = DpiScale();
        int margin = (int)Math.Round(12 * scale);
        int screenW = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);

        if (_widgetsSegment != null && wantWidgets)
        {
            int widgetsW = (int)Math.Round(_widgetsSegment.WidthDip * scale);
            NativeMethods.MoveWindow(_widgetsSegment.Hwnd, margin, main.Top, widgetsW, height, true);
        }

        if (_traySegment != null)
        {
            int trayW = (int)Math.Round(_traySegment.WidthDip * scale);
            NativeMethods.MoveWindow(_traySegment.Hwnd, screenW - margin - trayW, main.Top, trayW, height, true);
        }
    }

    private void UpdateSegmentMode()
    {
        bool wantTray = _settings.TaskbarMode == TaskbarMode.Default;
        bool wantWidgets = wantTray && _settings.ShowWeather;

        if (wantTray || wantWidgets)
        {
            // Segment creation must never take the shell down with it: on
            // failure the affected cluster simply stays in the center pill,
            // and creation is retried on the next settings change.
            if (wantWidgets && _widgetsSegment == null && !_segmentsFailed)
            {
                try
                {
                    _widgetsSegment = new SegmentWindow(SegmentKind.Widgets, _settings);
                    _widgetsSegment.Closed += (_, _) => { _widgetsSegment = null; _widgetsShown = false; };
                    if (_lastWeather != null)
                        _widgetsSegment.SetWeather(_lastWeather);
                }
                catch (Exception ex)
                {
                    DiagnosticsLog.WriteException("widgets segment create", ex);
                    _segmentsFailed = true;
                }
            }

            if (wantTray && _traySegment == null && !_segmentsFailed)
            {
                try
                {
                    _traySegment = new SegmentWindow(SegmentKind.Tray, _settings);
                    _traySegment.Closed += (_, _) => { _traySegment = null; _trayShown = false; };
                    _traySegment.SetClock(DateTime.Now);
                    _traySegment.SetBattery(_lastBatteryPercent, _lastBatteryCharging, _hasBattery);
                }
                catch (Exception ex)
                {
                    DiagnosticsLog.WriteException("tray segment create", ex);
                    _segmentsFailed = true;
                }
            }

            PositionSegments();

            if (wantWidgets && _widgetsSegment != null && !_widgetsShown)
            {
                _widgetsSegment.Activate();
                _widgetsShown = true;
            }

            if (wantTray && _traySegment != null && !_trayShown)
            {
                _traySegment.Activate();
                _trayShown = true;
            }
        }

        if (!wantWidgets && _widgetsShown)
        {
            _widgetsSegment?.AppWindow.Hide();
            _widgetsShown = false;
        }

        if (!wantTray && _trayShown)
        {
            _traySegment?.AppWindow.Hide();
            _trayShown = false;
        }
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        ClockText.Text = now.ToString("t");
        DateText.Text = now.ToString("ddd d MMM");
        _traySegment?.SetClock(now);
    }

    private void RefreshTasks()
    {
        var foreground = NativeMethods.GetForegroundWindow();
        var menuHwnd = _menu != null ? WindowNative.GetWindowHandle(_menu) : IntPtr.Zero;
        var settingsHwnd = _settingsWindow != null ? WindowNative.GetWindowHandle(_settingsWindow) : IntPtr.Zero;

        var grouped = new Dictionary<string, TaskWindowItem>(StringComparer.OrdinalIgnoreCase);

        NativeMethods.EnumWindows((h, _) =>
        {
            if (h == _hwnd || h == menuHwnd || h == settingsHwnd) return true;
            if (!NativeMethods.IsWindowVisible(h)) return true;
            if (NativeMethods.GetWindowTextLength(h) == 0) return true;

            // Filter cloaked windows (UWP apps on other desktops / suspended)
            if (NativeMethods.DwmGetWindowAttribute(h, NativeMethods.DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0)
                return true;

            long style = NativeMethods.GetWindowLongPtr(h, NativeMethods.GWL_STYLE).ToInt64();
            if ((style & NativeMethods.WS_CHILD) != 0) return true;

            long exStyle = NativeMethods.GetWindowLongPtr(h, NativeMethods.GWL_EXSTYLE).ToInt64();
            if ((exStyle & NativeMethods.WS_EX_TOOLWINDOW) != 0) return true;
            if (NativeMethods.GetWindow(h, NativeMethods.GW_OWNER) != IntPtr.Zero &&
                (exStyle & NativeMethods.WS_EX_APPWINDOW) == 0) return true;

            var cls = new StringBuilder(128);
            NativeMethods.GetClassName(h, cls, cls.Capacity);
            var className = cls.ToString();
            if (className is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "Progman" or "WorkerW" or
                "Windows.UI.Core.CoreWindow" or "ForegroundStaging" or "ApplicationFrameTitleBarWindow")
                return true;

            if (NativeMethods.GetWindowRect(h, out NativeMethods.RECT r))
            {
                if (r.Right <= r.Left || r.Bottom <= r.Top)
                    return true;
            }

            var sb = new StringBuilder(512);
            NativeMethods.GetWindowText(h, sb, sb.Capacity);
            var title = sb.ToString();

            string? iconPath = null;
            uint pid = 0;
            try
            {
                NativeMethods.GetWindowThreadProcessId(h, out pid);
                using var proc = Process.GetProcessById((int)pid);
                iconPath = proc.MainModule?.FileName;
            }
            catch
            {
                // Access denied for elevated processes.
            }

            string appKey = !string.IsNullOrWhiteSpace(iconPath)
                ? iconPath.ToLowerInvariant()
                : $"cls_{className}_{pid}";

            bool isWinActive = (h == foreground);

            if (grouped.TryGetValue(appKey, out var existing))
            {
                existing.Windows.Add(h);
                if (isWinActive)
                {
                    existing.Hwnd = h;
                    existing.Title = title;
                    existing.IsActive = true;
                }
            }
            else
            {
                var item = new TaskWindowItem
                {
                    Hwnd = h,
                    AppKey = appKey,
                    Title = title,
                    IconPath = iconPath,
                    IconSource = GetTaskIcon(iconPath),
                    IsActive = isWinActive,
                };
                item.Windows.Add(h);
                grouped[appKey] = item;
            }

            return true;
        }, IntPtr.Zero);

        var foundKeys = new HashSet<string>(grouped.Keys, StringComparer.OrdinalIgnoreCase);

        for (int i = _tasks.Count - 1; i >= 0; i--)
        {
            if (!foundKeys.Contains(_tasks[i].AppKey))
                _tasks.RemoveAt(i);
        }

        foreach (var kvp in grouped)
        {
            var item = kvp.Value;
            TaskWindowItem? existing = null;
            foreach (var task in _tasks)
            {
                if (string.Equals(task.AppKey, item.AppKey, StringComparison.OrdinalIgnoreCase))
                {
                    existing = task;
                    break;
                }
            }

            if (existing == null)
            {
                _tasks.Add(item);
            }
            else
            {
                existing.Hwnd = item.Hwnd;
                existing.Title = item.Title;
                existing.IsActive = item.IsActive;
                existing.Windows.Clear();
                existing.Windows.AddRange(item.Windows);
                if (existing.IconSource == null)
                    existing.IconSource = GetTaskIcon(item.IconPath);
            }
        }

        double effectiveIconSize = EffectiveIconSize();
        if (Math.Abs(effectiveIconSize - CurrentIconSize) > 0.01)
        {
            CurrentIconSize = effectiveIconSize;
            TaskRepeater.ItemsSource = null;
            TaskRepeater.ItemsSource = _tasks;
        }

        EnsurePosition();
    }

    private ImageSource? GetTaskIcon(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;

        if (_iconCache.TryGetValue(path, out var cached))
            return cached;

        var icon = IconLoader.Load(path);
        _iconCache[path] = icon;
        return icon;
    }

    private void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_menu != null)
        {
            _menu.Close();
            return;
        }

        if ((DateTime.UtcNow - _menuClosedAt).TotalMilliseconds < 350)
            return;

        ShowStartMenu(null);
    }

    private void SearchButton_Click(object sender, RoutedEventArgs e) => ShowStartMenu(null);

    private void TaskViewButton_Click(object sender, RoutedEventArgs e) => ShellPanels.OpenTaskView();

    private void Notification_Click(object sender, RoutedEventArgs e) => ShellPanels.OpenNotifications();

    private void ShowStartMenu(string? initialSearch)
    {
        _menu = new StartMenuWindow(_settings, initialSearch);
        _menu.Closed += (_, _) =>
        {
            _menu = null;
            _menuClosedAt = DateTime.UtcNow;
        };
        _menu.Activate();
    }

    private void Calendar_Click(object sender, RoutedEventArgs e) => ShellPanels.OpenNotifications();

    private void QuickSettings_Click(object sender, RoutedEventArgs e) => ShellPanels.OpenQuickSettings();

    private void TaskManager_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo { FileName = "taskmgr.exe", UseShellExecute = true }); } catch { }
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        Application.Current.Exit();
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        if (_settingsWindow != null)
        {
            var hwnd = WindowNative.GetWindowHandle(_settingsWindow);
            if (NativeMethods.IsWindow(hwnd))
            {
                if (NativeMethods.IsIconic(hwnd))
                    NativeMethods.ShowWindow(hwnd, NativeMethods.SW_RESTORE);
                NativeMethods.BringWindowToTop(hwnd);
                NativeMethods.SetForegroundWindow(hwnd);
                return;
            }
        }

        _settingsWindow = new SettingsWindow(_settings);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Activate();
    }

    private void TaskButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: TaskWindowItem item })
        {
            if (item.IsActive)
            {
                // Minimize just the foreground window of the group, like the
                // real taskbar — not every window the app has open.
                var target = item.Hwnd;
                if (!NativeMethods.IsWindow(target) && item.Windows.Count > 0)
                    target = item.Windows[0];

                if (NativeMethods.IsWindow(target))
                    NativeMethods.ShowWindow(target, NativeMethods.SW_MINIMIZE);
                item.IsActive = false;
            }
            else
            {
                var target = item.Hwnd;
                if (!NativeMethods.IsWindow(target) && item.Windows.Count > 0)
                    target = item.Windows[0];

                ActivateWindow(target);
            }

            RefreshTasks();
        }
    }

    private void TaskButton_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is not Button { DataContext: TaskWindowItem item } button)
            return;

        var flyout = new MenuFlyout();

        foreach (var wnd in item.Windows)
        {
            if (!NativeMethods.IsWindow(wnd))
                continue;

            var sb = new StringBuilder(512);
            NativeMethods.GetWindowText(wnd, sb, sb.Capacity);
            var windowItem = new MenuFlyoutItem { Text = sb.Length > 0 ? sb.ToString() : item.Title };
            var captured = wnd;
            windowItem.Click += (_, _) => ActivateWindow(captured);
            flyout.Items.Add(windowItem);
        }

        if (item.Windows.Count > 1)
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            var closeAll = new MenuFlyoutItem { Text = "Close all windows" };
            closeAll.Click += (_, _) =>
            {
                foreach (var wnd in item.Windows)
                {
                    if (NativeMethods.IsWindow(wnd))
                        NativeMethods.PostMessage(wnd, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                }
            };
            flyout.Items.Add(closeAll);
        }

        if (!string.IsNullOrWhiteSpace(item.IconPath))
        {
            flyout.Items.Add(new MenuFlyoutSeparator());
            var pinItem = new MenuFlyoutItem { Text = IsAppPinned(item.IconPath) ? "Unpin from Start" : "Pin to Start" };
            pinItem.Click += (_, _) => TogglePin(item);
            flyout.Items.Add(pinItem);
        }

        flyout.ShowAt(button);
        e.Handled = true;
    }

    private void ActivateWindow(IntPtr target)
    {
        if (!NativeMethods.IsWindow(target))
            return;

        if (NativeMethods.IsIconic(target))
            NativeMethods.ShowWindow(target, NativeMethods.SW_RESTORE);

        NativeMethods.BringWindowToTop(target);
        NativeMethods.SwitchToThisWindow(target, true);
        NativeMethods.SetForegroundWindow(target);
    }

    private static bool IsAppPinned(string path) =>
        PinnedStore.Load().Exists(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));

    private void TogglePin(TaskWindowItem item)
    {
        var pins = PinnedStore.Load();
        if (pins.Exists(p => string.Equals(p.Path, item.IconPath, StringComparison.OrdinalIgnoreCase)))
            pins.RemoveAll(p => string.Equals(p.Path, item.IconPath, StringComparison.OrdinalIgnoreCase));
        else
            pins.Add(new PinnedApp { Name = item.Title, Path = item.IconPath! });
        PinnedStore.Save(pins);
    }

    private void UpdateBattery()
    {
        if (BatteryStatus.TryGet(out int percent, out bool charging, out bool hasBattery) && hasBattery)
        {
            _lastBatteryPercent = percent;
            _lastBatteryCharging = charging;
            _hasBattery = true;
            _traySegment?.SetBattery(percent, charging, true);

            BatteryButton.Visibility = Visibility.Visible;
            int bucket = percent < 0 ? 5 : Math.Clamp(percent / 10, 0, 10);
            BatteryIcon.Glyph = ((char)(0xE850 + bucket)).ToString();
            BatteryText.Text = percent >= 0 ? $"{percent}%" : "";
            ToolTipService.SetToolTip(BatteryButton,
                percent >= 0
                    ? (charging ? $"Battery {percent}% (charging)" : $"Battery {percent}%")
                    : "Battery");
        }
        else
        {
            _hasBattery = false;
            _traySegment?.SetBattery(0, false, false);
            BatteryButton.Visibility = Visibility.Collapsed;
        }
    }

    private async System.Threading.Tasks.Task RefreshWeatherAsync()
    {
        if (!_settings.ShowWeather)
            return;

        var info = await WeatherService.TryGetCurrentAsync();
        if (info == null)
            return;

        _lastWeather = info;
        DispatcherQueue.TryEnqueue(() =>
        {
            WeatherIcon.Glyph = info.Glyph;
            WeatherText.Text = info.TemperatureText;
            WeatherConditionText.Text = info.ConditionLabel;
            WeatherTextStack.Visibility = Visibility.Visible;
            _widgetsSegment?.SetWeather(info);
        });
    }

    // Public entry so the start menu's footer can open settings too.
    public void OpenSettings() => SettingsButton_Click(this, new RoutedEventArgs());

    // Public entry so segment windows can offer Task Manager too.
    public void ShowTaskManager() => TaskManager_Click(this, new RoutedEventArgs());

    public void ApplySettings(MachitanSettings settings)
    {
        _settings = settings.Clone();
        bool iconSizeChanged = Math.Abs(CurrentIconSize - EffectiveIconSize()) > 0.01;
        CurrentIconSize = EffectiveIconSize();

        RootGrid.RequestedTheme = _settings.Theme == ShellTheme.Dark ? ElementTheme.Dark : ElementTheme.Light;
        ApplyBackdrop();

        if (_settings.TaskbarMode == TaskbarMode.Classic)
        {
            PillBorder.CornerRadius = new CornerRadius(0);
            PillBorder.BorderThickness = new Thickness(0, 1, 0, 0);
        }
        else
        {
            PillBorder.CornerRadius = new CornerRadius(PillRadiusDip);
            PillBorder.BorderThickness = new Thickness(1);
        }

        if (iconSizeChanged)
        {
            // Task-button sizes are OneTime bindings: re-realize the
            // repeater so existing buttons pick up the new size.
            TaskRepeater.ItemsSource = null;
            TaskRepeater.ItemsSource = _tasks;
        }

        _widgetsSegment?.ApplySettings(_settings);
        _traySegment?.ApplySettings(_settings);
        _segmentsFailed = false;
        UpdateSegmentMode();

        // In Default mode the weather/tray clusters move out into their own
        // segment windows; in the joined modes they live in this pill. Only
        // collapse them here when the segments actually exist.
        bool traySegmented = _settings.TaskbarMode == TaskbarMode.Default && _traySegment != null;
        bool widgetsSegmented = _settings.TaskbarMode == TaskbarMode.Default
            && _widgetsSegment != null && _settings.ShowWeather;
        TrayCluster.Visibility = traySegmented ? Visibility.Collapsed : Visibility.Visible;
        WeatherCluster.Visibility = (!_settings.ShowWeather || widgetsSegmented)
            ? Visibility.Collapsed
            : Visibility.Visible;

        AppBarController.Reposition(_hwnd, BarHeightPx(), _settings.DockEdge, ComputeDesiredWidthDip(), _settings.DockAlignment, _settings.TaskbarMode);
        PositionSegments();
    }

    public void CloseStartMenu() => _menu?.Close();

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _clockTimer.Stop();
        _taskTimer.Stop();
        _repositionTimer.Stop();
        _weatherTimer.Stop();

        try { _menu?.Close(); } catch { }
        try { _widgetsSegment?.Close(); } catch { }
        try { _traySegment?.Close(); } catch { }
        AppBarController.Unregister(_hwnd);
    }
}
