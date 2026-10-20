using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;
using Machitan.Native;
using Windows.Graphics;

namespace Machitan.StartMenu;

public sealed partial class StartMenuWindow : Window
{
    private const int WidthDip = 700;
    private const int HeightDip = 610;

    private readonly MachitanSettings _settings;
    private readonly ObservableCollection<PinnedApp> _pinned = new();
    private readonly List<PinnedApp> _allApps = new();
    private readonly IntPtr _hwnd;
    private bool _activatedOnce;

    private static List<PinnedApp>? _cachedAllApps;
    private static bool _isLoadingAllApps;

    public StartMenuWindow(MachitanSettings settings, string? initialSearch = null)
    {
        _settings = settings;
        this.InitializeComponent();

        _hwnd = WindowNative.GetWindowHandle(this);
        SystemBackdrop = new DesktopAcrylicBackdrop();

        WindowChrome.MakeBorderless(_hwnd, topmost: true);
        WindowChrome.SetCornerPreference(_hwnd, NativeMethods.DWMWCP_DONOTROUND);

        RecentlyUsedRepeater.ItemsSource = _pinned;
        RootGrid.RequestedTheme = settings.Theme == ShellTheme.Dark ? ElementTheme.Dark : ElementTheme.Light;

        LoadPinnedApps();
        LoadAllApps();
        ApplyFilter(initialSearch ?? "");

        RootGrid.Loaded += (_, _) =>
        {
            if (SearchBox != null)
            {
                SearchBox.Text = initialSearch ?? "";
                SearchBox.Focus(FocusState.Programmatic);
            }
        };

        Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated)
                _activatedOnce = true;
            else if (_activatedOnce)
                Close();
        };

        PositionWindow();
    }

    private void PositionWindow()
    {
        uint dpi = NativeMethods.GetDpiForWindow(_hwnd);
        double scale = dpi == 0 ? 1 : dpi / 96d;

        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary);
        var work = area.WorkArea;

        int width = Math.Min((int)Math.Round(WidthDip * scale), work.Width - 16);
        int height = Math.Min((int)Math.Round(HeightDip * scale), work.Height - 16);
        int x = work.X + (work.Width - width) / 2;
        int y = _settings.DockEdge == DockEdge.Bottom
            ? work.Y + work.Height - height - (int)Math.Round(12 * scale)
            : work.Y + (int)Math.Round(12 * scale);

        AppWindow.MoveAndResize(new RectInt32(x, y, width, height));
        WindowChrome.ApplyRoundedRegion(_hwnd, (int)Math.Round(20 * scale));
    }

    private void LoadPinnedApps()
    {
        _pinned.Clear();
        foreach (var app in PinnedStore.Load())
        {
            app.IconSource = IconLoader.Load(app.Path);
            _pinned.Add(app);
        }

        var userName = Environment.UserName;
        AvatarInitial.Text = string.IsNullOrEmpty(userName)
            ? "U"
            : char.ToUpperInvariant(userName[0]).ToString();
    }

    private void LoadAllApps()
    {
        _allApps.Clear();

        if (_cachedAllApps != null)
        {
            _allApps.AddRange(_cachedAllApps);
            return;
        }

        var scanned = AppScanner.GetAllApps();
        _cachedAllApps = new List<PinnedApp>(scanned.Count);

        foreach (var item in scanned)
        {
            var app = new PinnedApp
            {
                Name = item.Name,
                Path = item.LinkPath,
            };
            _cachedAllApps.Add(app);
        }

        _allApps.AddRange(_cachedAllApps);

        if (!_isLoadingAllApps)
        {
            _isLoadingAllApps = true;
            var queue = DispatcherQueue;
            _ = System.Threading.Tasks.Task.Run(() =>
            {
                foreach (var app in _cachedAllApps)
                {
                    if (app.IconSource == null)
                    {
                        var icon = IconLoader.Load(app.Path);
                        if (icon != null)
                        {
                            queue.TryEnqueue(() => app.IconSource = icon);
                        }
                    }
                }
            });
        }
    }

    private void ApplyFilter(string query)
    {
        query = (query ?? "").Trim();

        if (query.Length > 0)
        {
            StartScrollViewer.Visibility = Visibility.Collapsed;
            AllAppsView.Visibility = Visibility.Visible;
            NavStartIndicator.Visibility = Visibility.Collapsed;
            NavAppsIndicator.Visibility = Visibility.Visible;
        }
        else
        {
            StartScrollViewer.Visibility = Visibility.Visible;
            AllAppsView.Visibility = Visibility.Collapsed;
            NavStartIndicator.Visibility = Visibility.Visible;
            NavAppsIndicator.Visibility = Visibility.Collapsed;
        }

        var filtered = new List<PinnedApp>();
        foreach (var app in _allApps)
        {
            if (query.Length == 0 || app.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                filtered.Add(app);
            }
        }

        AllAppsList.ItemsSource = null;
        AllAppsList.ItemsSource = filtered;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter(SearchBox.Text);

    private void NavStart_Click(object sender, RoutedEventArgs e)
    {
        StartScrollViewer.Visibility = Visibility.Visible;
        AllAppsView.Visibility = Visibility.Collapsed;
        NavStartIndicator.Visibility = Visibility.Visible;
        NavAppsIndicator.Visibility = Visibility.Collapsed;
    }

    private void NavApps_Click(object sender, RoutedEventArgs e)
    {
        StartScrollViewer.Visibility = Visibility.Collapsed;
        AllAppsView.Visibility = Visibility.Visible;
        NavStartIndicator.Visibility = Visibility.Collapsed;
        NavAppsIndicator.Visibility = Visibility.Visible;
    }

    private void NavCreate_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo { FileName = "notepad.exe", UseShellExecute = true }); } catch { }
        Close();
    }

    private void Documents_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            Process.Start(new ProcessStartInfo { FileName = "explorer.exe", Arguments = docs, UseShellExecute = true });
        }
        catch { }
        Close();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo { FileName = "ms-settings:", UseShellExecute = true }); } catch { }
        Close();
    }

    private void Reminder_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo { FileName = "ms-clock:", UseShellExecute = true }); } catch { }
        Close();
    }

    private void PowerPoint_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo { FileName = "powerpnt.exe", UseShellExecute = true }); } catch { }
        Close();
    }

    private void LaunchFolderApp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string target })
        {
            try { Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true }); } catch { }
            Close();
        }
    }

    private void PowerSleep_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo { FileName = "rundll32.exe", Arguments = "powrprof.dll,SetSuspendState 0,1,0", UseShellExecute = true }); } catch { }
        Close();
    }

    private void PowerRestart_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo { FileName = "shutdown.exe", Arguments = "/r /t 0", UseShellExecute = true }); } catch { }
        Close();
    }

    private void PowerShutdown_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo { FileName = "shutdown.exe", Arguments = "/s /t 0", UseShellExecute = true }); } catch { }
        Close();
    }

    private void PowerSignOut_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo { FileName = "shutdown.exe", Arguments = "/l", UseShellExecute = true }); } catch { }
        Close();
    }

    private void PinnedTile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PinnedApp app })
            LaunchAndClose(app.Path);
    }

    private void AllAppsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PinnedApp app)
            LaunchAndClose(app.Path);
    }

    private void LaunchAndClose(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch
        {
            // Ignore broken shortcuts.
        }

        Close();
    }
}
