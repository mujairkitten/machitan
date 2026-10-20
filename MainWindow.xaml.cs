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
    private const int PillRadiusDip = 20;

    public static double CurrentIconSize { get; private set; } = 28;
    public static double GetTaskIconSize() => CurrentIconSize;
    public static double GetTaskButtonWidth() => Math.Max(40, CurrentIconSize + 14);
    public static double GetTaskButtonHeight() => Math.Max(42, CurrentIconSize + 16);

    private MachitanSettings _settings = MachitanSettingsStore.Load();
    private readonly ObservableCollection<TaskWindowItem> _tasks = new();
    private readonly Dictionary<string, ImageSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);

    private IntPtr _hwnd;
    private StartMenuWindow? _menu;
    private DateTime _menuClosedAt = DateTime.MinValue;
    private SettingsWindow? _settingsWindow;

    private readonly DispatcherTimer _clockTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly DispatcherTimer _taskTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly DispatcherTimer _repositionTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };

    public MainWindow()
    {
        DiagnosticsLog.Write("MainWindow ctor: before InitializeComponent");
        this.InitializeComponent();
        DiagnosticsLog.Write("MainWindow ctor: after InitializeComponent");

        TaskRepeater.ItemsSource = _tasks;

        _hwnd = WindowNative.GetWindowHandle(this);
        ApplyWindowStyle();
        DiagnosticsLog.Write("MainWindow ctor: after ApplyWindowStyle");

        _clockTimer.Tick += (_, _) => Safe("clock tick", UpdateClock);
        _taskTimer.Tick += (_, _) => Safe("task tick", RefreshTasks);
        _repositionTimer.Tick += (_, _) => Safe("reposition tick", EnsurePosition);

        UpdateClock();
        RefreshTasks();
        DiagnosticsLog.Write("MainWindow ctor: after initial refresh");

        _clockTimer.Start();
        _taskTimer.Start();
        _repositionTimer.Start();

        // Position after the window has been shown by Activate().
        DispatcherQueue.TryEnqueue(() =>
        {
            Safe("initial reposition", () =>
            {
                AppBarController.Register(_hwnd, BarHeightPx(), _settings.DockEdge);
                AppBarController.Reposition(_hwnd, BarHeightPx(), _settings.DockEdge, ComputeDesiredWidthDip(), _settings.DockAlignment, _settings.TaskbarMode);
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

    private int BarHeightPx() => (int)Math.Round(_settings.BarSize);

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
        WindowChrome.SetCornerPreference(_hwnd, NativeMethods.DWMWCP_DONOTROUND);

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

        int leftWidth = 44 + 10 + 230; // StartButton + Spacing + SearchBorder = 284
        int rightWidth = 88 + 4 + 40 + 4 + 40 + 4 + 40 + 20; // Clock + spacing + 3 buttons = 240
        double buttonW = GetTaskButtonWidth();
        int tasksWidth = (int)Math.Round(_tasks.Count * (buttonW + 6));
        int paddingAndGaps = 60;

        int total = leftWidth + rightWidth + tasksWidth + paddingAndGaps;
        return Math.Clamp(total, 640, 1920);
    }

    private void EnsurePosition()
    {
        int desiredWidth = ComputeDesiredWidthDip();
        if (!AppBarController.IsAtDesiredPosition(_hwnd, BarHeightPx(), _settings.DockEdge, desiredWidth, _settings.DockAlignment, _settings.TaskbarMode))
        {
            AppBarController.Reposition(_hwnd, BarHeightPx(), _settings.DockEdge, desiredWidth, _settings.DockAlignment, _settings.TaskbarMode);
        }
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        DateText.Text = now.ToString("ddd MM/dd");
        ClockText.Text = now.ToString("HH:mm");
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

    private void SearchBorder_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        SearchBox.Focus(FocusState.Programmatic);
    }

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            var query = SearchBox.Text.Trim();
            ShowStartMenu(string.IsNullOrWhiteSpace(query) ? null : query);
            e.Handled = true;
        }
    }

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

    private void Calendar_Click(object sender, RoutedEventArgs e)
    {
        // TODO: Show a custom calendar flyout.
    }

    private void QuickSettings_Click(object sender, RoutedEventArgs e)
    {
        // TODO: Show a custom quick-settings flyout.
    }

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
                // App is active -> minimize it!
                foreach (var wnd in item.Windows)
                {
                    if (NativeMethods.IsWindow(wnd))
                        NativeMethods.ShowWindow(wnd, NativeMethods.SW_MINIMIZE);
                }
                item.IsActive = false;
            }
            else
            {
                var target = item.Hwnd;
                if (!NativeMethods.IsWindow(target) && item.Windows.Count > 0)
                    target = item.Windows[0];

                if (NativeMethods.IsWindow(target))
                {
                    if (NativeMethods.IsIconic(target))
                        NativeMethods.ShowWindow(target, NativeMethods.SW_RESTORE);

                    NativeMethods.BringWindowToTop(target);
                    NativeMethods.SwitchToThisWindow(target, true);
                    NativeMethods.SetForegroundWindow(target);
                }
            }

            RefreshTasks();
        }
    }

    public void ApplySettings(MachitanSettings settings)
    {
        _settings = settings.Clone();
        CurrentIconSize = _settings.IconSize;

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

        AppBarController.Reposition(_hwnd, BarHeightPx(), _settings.DockEdge, ComputeDesiredWidthDip(), _settings.DockAlignment, _settings.TaskbarMode);
    }

    public void CloseStartMenu() => _menu?.Close();

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _clockTimer.Stop();
        _taskTimer.Stop();
        _repositionTimer.Stop();

        try { _menu?.Close(); } catch { }
        AppBarController.Unregister(_hwnd);
    }
}
