using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Linq;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;
using Machitan.Native;
using Windows.Graphics;

namespace Machitan.StartMenu;

public sealed partial class StartMenuWindow : Window
{
    private const int WidthDip = 760;
    private const int HeightDip = 700;

    private readonly MachitanSettings _settings;
    private readonly ObservableCollection<PinnedApp> _pinned = new();
    private readonly ObservableCollection<PinnedApp> _recents = new();
    private readonly List<PinnedApp> _allApps = new();
    private readonly IntPtr _hwnd;
    private bool _activatedOnce;
    private bool _showAllApps;

    public StartMenuWindow(MachitanSettings settings, string? initialSearch = null)
    {
        _settings = settings;
        this.InitializeComponent();

        _hwnd = WindowNative.GetWindowHandle(this);
        ApplyBackdrop();

        WindowChrome.MakeBorderless(_hwnd, topmost: true);
        WindowChrome.SetCornerPreference(_hwnd, NativeMethods.DWMWCP_ROUND);

        PinnedRepeater.ItemsSource = _pinned;
        RecentlyUsedRepeater.ItemsSource = _recents;
        RootGrid.RequestedTheme = settings.Theme == ShellTheme.Dark ? ElementTheme.Dark : ElementTheme.Light;

        LoadData();
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

    private void ApplyBackdrop()
    {
        if (_settings.EnableAcrylic)
        {
            SystemBackdrop = new DesktopAcrylicBackdrop();
            byte alpha = (byte)Math.Clamp(Math.Round(180 * _settings.Opacity), 0, 255);
            var tint = _settings.Theme == ShellTheme.Dark
                ? new Windows.UI.Color { A = alpha, R = 21, G = 24, B = 28 }
                : new Windows.UI.Color { A = alpha, R = 245, G = 245, B = 247 };
            RootGrid.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(tint);
        }
        else
        {
            SystemBackdrop = null;
            var solid = _settings.Theme == ShellTheme.Dark
                ? new Windows.UI.Color { A = 255, R = 32, G = 32, B = 32 }
                : new Windows.UI.Color { A = 255, R = 243, G = 243, B = 243 };
            RootGrid.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(solid);
        }
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
    }

    private void LoadData()
    {
        var userName = Environment.UserName;
        UserNameText.Text = userName;
        AvatarInitial.Text = string.IsNullOrEmpty(userName)
            ? "U"
            : char.ToUpperInvariant(userName[0]).ToString();

        _pinned.Clear();
        foreach (var app in PinnedStore.Load())
        {
            app.IconSource = IconLoader.Load(app.Path);
            _pinned.Add(app);
        }

        _recents.Clear();
        foreach (var app in RecentStore.Load())
        {
            app.IconSource = IconLoader.Load(app.Path);
            _recents.Add(app);
        }

        _allApps.Clear();
        foreach (var scanned in AppScanner.GetAllApps())
            _allApps.Add(new PinnedApp { Name = scanned.Name, Path = scanned.LinkPath });

        UpdateRecentsRepeater();
        UpdatePinnedRepeater();
    }

    private void UpdateRecentsRepeater()
    {
        RecentlyUsedRepeater.ItemsSource = _recents.Count > 0 ? _recents : null;
    }

    private void UpdatePinnedRepeater()
    {
        PinnedRepeater.ItemsSource = _pinned.Count > 0 ? _pinned : null;
    }

    private void ApplyFilter(string query)
    {
        query = (query ?? "").Trim();

        if (query.Length == 0)
        {
            StartViewVisibility();
            return;
        }

        var filtered = _allApps
            .Where(a => a.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        _pinned.Clear();
        foreach (var app in filtered)
        {
            app.IconSource = IconLoader.Load(app.Path);
            _pinned.Add(app);
        }
        UpdatePinnedRepeater();
    }

    private void StartViewVisibility()
    {
        UpdateRecentsRepeater();
        UpdatePinnedRepeater();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter(SearchBox.Text);

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            if (_pinned.Count > 0)
            {
                LaunchAndClose(_pinned[0]);
                e.Handled = true;
            }
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            if (SearchBox.Text.Length > 0)
            {
                SearchBox.Text = "";
                e.Handled = true;
            }
        }
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            Close();
            e.Handled = true;
        }
    }

    private void PinnedTile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PinnedApp app })
            LaunchAndClose(app);
    }

    private void RecentTile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PinnedApp app })
            LaunchAndClose(app);
    }

    private void AppRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { DataContext: PinnedApp app })
            LaunchAndClose(app);
    }

    private void AppList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PinnedApp app)
            LaunchAndClose(app);
    }

    private void App_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is Button { DataContext: PinnedApp app } button)
        {
            ShowAppFlyout(app, button, fromRecents: false);
            e.Handled = true;
        }
    }

    private void Recent_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (sender is Button { DataContext: PinnedApp app } button)
        {
            ShowAppFlyout(app, button, fromRecents: true);
            e.Handled = true;
        }
    }

    private static PinnedApp? FindAppContext(object source)
    {
        var element = source as FrameworkElement;
        while (element != null)
        {
            if (element.DataContext is PinnedApp app)
                return app;
            element = element.Parent as FrameworkElement;
        }
        return null;
    }

    private void ShowAppFlyout(PinnedApp app, FrameworkElement anchor, bool fromRecents)
    {
        var flyout = new MenuFlyout();

        var launch = new MenuFlyoutItem { Text = "Launch" };
        launch.Click += (_, _) => LaunchAndClose(app);
        flyout.Items.Add(launch);

        bool pinned = IsPinned(app.Path);
        var pin = new MenuFlyoutItem { Text = pinned ? "Unpin from Start" : "Pin to Start" };
        pin.Click += (_, _) =>
        {
            if (pinned) UnpinApp(app.Path);
            else PinApp(app);
        };
        flyout.Items.Add(pin);

        if (fromRecents)
        {
            var remove = new MenuFlyoutItem { Text = "Remove from list" };
            remove.Click += (_, _) => RemoveRecent(app);
            flyout.Items.Add(remove);
        }

        flyout.ShowAt(anchor);
    }

    private bool IsPinned(string path) =>
        _pinned.Any(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));

    private void PinApp(PinnedApp app)
    {
        if (IsPinned(app.Path))
            return;

        var pin = new PinnedApp { Name = app.Name, Path = app.Path, IconSource = app.IconSource ?? IconLoader.Load(app.Path) };
        _pinned.Add(pin);
        PinnedStore.Save(_pinned.ToList());
        UpdatePinnedRepeater();
    }

    private void UnpinApp(string path)
    {
        for (int i = _pinned.Count - 1; i >= 0; i--)
        {
            if (string.Equals(_pinned[i].Path, path, StringComparison.OrdinalIgnoreCase))
                _pinned.RemoveAt(i);
        }
        PinnedStore.Save(_pinned.ToList());
        UpdatePinnedRepeater();
    }

    private void RemoveRecent(PinnedApp app)
    {
        _recents.Remove(app);
        RecentStore.Save(_recents.ToList());
        UpdateRecentsRepeater();
    }

    private void LaunchAndClose(PinnedApp app)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = app.Path, UseShellExecute = true });
            RecentStore.Record(app.Name, app.Path);
        }
        catch
        {
            // Ignore broken shortcuts.
        }

        Close();
    }

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        App.MainWindow?.OpenSettings();
        Close();
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Application.Current.Exit();

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

    private void StartNavButton_Click(object sender, RoutedEventArgs e)
    {
        // Start button clicked - refresh pinned list
        _pinned.Clear();
        foreach (var app in PinnedStore.Load())
        {
            app.IconSource = IconLoader.Load(app.Path);
            _pinned.Add(app);
        }
        UpdatePinnedRepeater();
    }

    private void AllAppsNavButton_Click(object sender, RoutedEventArgs e)
    {
        _showAllApps = !_showAllApps;
        if (_showAllApps)
        {
            _pinned.Clear();
            foreach (var app in _allApps)
            {
                app.IconSource = app.IconSource ?? IconLoader.Load(app.Path);
                _pinned.Add(app);
            }
            UpdatePinnedRepeater();
        }
        else
        {
            _pinned.Clear();
            foreach (var app in PinnedStore.Load())
            {
                app.IconSource = IconLoader.Load(app.Path);
                _pinned.Add(app);
            }
            UpdatePinnedRepeater();
        }
    }
}