using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Machitan.Config;
using Machitan.Core;
using Machitan.Interop;
using Machitan.Shell;
using Machitan.Visuals;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Storage.Streams;
using VirtualKey = Windows.System.VirtualKey;

namespace Machitan.Start;

/// <summary>
/// The Start menu: separate floating boxes on a see-through window, height capped at 400 DIP.
/// Left: search (focused on open, filters both lists) over the Pinned grid (unpin on hover).
/// Right: All apps, as tall as the left column; each row's pin button shows on hover (always
/// on touch devices). Below: a slim user strip with avatar, name and power button.
/// No recently used apps, no recent items, no widgets.
///
/// Search scopes: "apps:" (the default) and "files:". There is deliberately no web scope.
/// Everything listed comes from Windows: Start menu shortcuts, store apps, the user's files.
/// </summary>
public sealed partial class StartMenuWindow : Window
{
    private const double ColumnsWidthDip = 300 + 8 + 340;
    private const double MenuHeightDip = 400;
    private const double PadDip = 4;
    private const double GapToBarDip = 8;

    // Radius scale: 16 large surfaces, 10 medium controls, 8 small inner buttons; search is a capsule.
    private const double LargeRadius = 16;
    private const double MediumRadius = 10;
    private const double SmallRadius = 8;
    private const double SearchRadius = 20;

    private const double RowIconDip = 24;
    private const double TileIconDip = 32;
    private const int MaxFileResults = 40;

    private static readonly string PinGlyph = ((char)0xE718).ToString();
    private static readonly string UnpinGlyph = ((char)0xE77A).ToString();
    private static readonly string FileGlyph = ((char)0xE8A5).ToString();
    private static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    private readonly ShellHost _host;
    private readonly nint _hwnd;
    private readonly WindowCutter _cutter;
    private readonly DispatcherQueueTimer _fileTimer;
    private readonly bool _hasTouch;
    private readonly Dictionary<string, ImageSource> _icons = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<AppRow> _appRows = new();
    private readonly List<(PinnedApp Pin, FrameworkElement Tile)> _pinTiles = new();

    private WindowSubclass? _subclass;
    private CancellationTokenSource? _fileSearch;
    private bool _open;
    private bool _dark = true;
    private string _fillLook = string.Empty;
    private long _closedByFocusLossAt;
    private bool _filesMode;
    private string _query = string.Empty;

    internal StartMenuWindow(ShellHost host)
    {
        _host = host;
        InitializeComponent();
        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        _hasTouch = User32.GetSystemMetrics(User32.SM_MAXIMUMTOUCHES) > 0;

        SeeThrough.Prepare(this, _hwnd, alwaysOnTop: true);
        _subclass = new WindowSubclass(_hwnd, OnWindowMessage);

        _cutter = new WindowCutter(_hwnd, Root);
        _cutter.Add(SearchBox, SearchRadius);
        _cutter.Add(PinnedBox, LargeRadius);
        _cutter.Add(AppsBox, LargeRadius);
        _cutter.Add(UserStrip, LargeRadius);
        Layout.LayoutUpdated += (_, _) => _cutter.Update();

        // The search box is a bare field inside its capsule: no fill or border of its own.
        var clear = new SolidColorBrush(Colors.Transparent);
        foreach (string key in new[]
                 {
                     "TextControlBackground", "TextControlBackgroundPointerOver", "TextControlBackgroundFocused",
                     "TextControlBorderBrush", "TextControlBorderBrushPointerOver", "TextControlBorderBrushFocused",
                 })
        {
            SearchInput.Resources[key] = clear;
        }

        _fileTimer = DispatcherQueue.CreateTimer();
        _fileTimer.Interval = TimeSpan.FromMilliseconds(250);
        _fileTimer.IsRepeating = false;
        _fileTimer.Tick += (_, _) => _ = RunFileSearchAsync();

        Activated += OnActivated;
        Closed += (_, _) =>
        {
            _subclass?.Dispose();
            _subclass = null;
        };

        _host.Catalog.Changed += OnCatalogChanged;
        _host.Pins.Changed += OnPinsChanged;
        _host.Settings.Changed += OnSettingsChanged;

        ApplyLook();
        LoadUser();
        RebuildApps();
    }

    private sealed class AppRow
    {
        internal AppRow(AppEntry app, Grid row, Button pin, FontIcon pinGlyph)
        {
            App = app;
            Row = row;
            Pin = pin;
            PinGlyph = pinGlyph;
        }

        internal AppEntry App { get; }

        internal Grid Row { get; }

        internal Button Pin { get; }

        internal FontIcon PinGlyph { get; }
    }

    internal bool IsOpen => _open;

    /// <summary>Icon size for the app list, in physical pixels (also used to build the catalog).</summary>
    internal int ListIconPixels => IconService.PickSize((int)Math.Ceiling(TileIconDip * Scale()));

    internal bool IsDark => _dark;

    /// <summary>
    /// The Start button. Pressing it while the menu is open first moves focus to the bar,
    /// which closes the menu (see OnActivated) before the click arrives. A click that comes
    /// right after such a close is that same click: it means "close", so the menu stays shut.
    /// </summary>
    internal void Toggle()
    {
        const long SameClickMs = 500;

        if (_open)
        {
            HideMenu();
        }
        else if (Environment.TickCount64 - _closedByFocusLossAt < SameClickMs)
        {
            _closedByFocusLossAt = 0;
        }
        else
        {
            Open();
        }
    }

    /// <summary>The Win key: open or close, with no click to account for (see <see cref="Toggle"/>).</summary>
    internal void ToggleFromKeyboard()
    {
        if (_open)
        {
            HideMenu();
        }
        else
        {
            Open();
        }
    }

    /// <summary>Opens the menu above the bar with the search box focused. Does nothing extra when already open.</summary>
    internal void Open()
    {
        try
        {
            if (_open)
            {
                SearchInput.Focus(FocusState.Programmatic);
                return;
            }

            _host.Catalog.RefreshIfStale(ListIconPixels, _dark);

            SearchInput.Text = string.Empty;
            ParseQuery();
            ApplyFilter();
            ResetPowerPanel();
            PlaceAboveBar();
            _cutter.Invalidate();

            User32.ShowWindow(_hwnd, User32.SW_SHOW);
            Activate();

            // Opened by the Win key, Windows may not hand over the keyboard by itself.
            WindowActivator.BringToFront(_hwnd);
            _open = true;
            Log.Info("Start menu opened.");

            AppsScroller.ChangeView(null, 0, null, true);
            DispatcherQueue.TryEnqueue(() => SearchInput.Focus(FocusState.Programmatic));
        }
        catch (Exception ex)
        {
            Log.Error("The Start menu could not be opened.", ex);
        }
    }

    internal void HideMenu()
    {
        if (!_open)
        {
            return;
        }

        _open = false;
        _fileSearch?.Cancel();
        User32.ShowWindow(_hwnd, User32.SW_HIDE);
        Log.Info("Start menu closed.");
    }

    // ---------------------------------------------------------------- window plumbing

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        // A click anywhere else closes the menu, like Windows' own Start.
        if (args.WindowActivationState == WindowActivationState.Deactivated && _open)
        {
            HideMenu();
            _closedByFocusLossAt = Environment.TickCount64;
        }
    }

    private bool OnWindowMessage(uint message, nint wParam, nint lParam, out nint result)
    {
        return SeeThrough.TryHandleErase(_hwnd, message, wParam, out result);
    }

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape)
        {
            return;
        }

        e.Handled = true;
        if (PowerPanel.Children.Count > 0)
        {
            ResetPowerPanel();
        }
        else
        {
            HideMenu();
        }
    }

    /// <summary>
    /// Lines the boxes up with the bar the way the bar is aligned, 8 DIP above it (below a
    /// top bar). The same rule places the calendar and quick settings (PopupPlacement).
    /// </summary>
    private void PlaceAboveBar()
    {
        double scale = Scale();
        int pad = (int)Math.Round(PadDip * scale);
        int gap = (int)Math.Round(GapToBarDip * scale);
        int width = (int)Math.Round((ColumnsWidthDip + (2 * PadDip)) * scale);
        int height = (int)Math.Round((MenuHeightDip + (2 * PadDip)) * scale);
        AppWindow.MoveAndResize(PopupPlacement.BesideBar(_host, width, height, pad, gap));
    }

    private double Scale()
    {
        uint dpi = User32.GetDpiForWindow(_hwnd);
        return (dpi == 0 ? 96 : dpi) / 96.0;
    }

    // ---------------------------------------------------------------- look

    private void OnSettingsChanged()
    {
        ApplyLook();
    }

    private void ApplyLook()
    {
        ShellSettings settings = _host.Settings.Current;
        Root.RequestedTheme = settings.Theme switch
        {
            ThemeMode.Dark => ElementTheme.Dark,
            ThemeMode.Light => ElementTheme.Light,
            _ => ElementTheme.Default,
        };

        _dark = settings.Theme switch
        {
            ThemeMode.Dark => true,
            ThemeMode.Light => false,
            _ => Application.Current.RequestedTheme == ApplicationTheme.Dark,
        };

        // Replace the fills only when their look changes, not on every settings change.
        string fillLook = settings.EnableAcrylic + "|" + settings.Opacity.ToString(CultureInfo.InvariantCulture) + "|" + _dark;
        if (fillLook == _fillLook)
        {
            return;
        }

        _fillLook = fillLook;
        SetFill(SearchBox, settings, SearchRadius);
        SetFill(PinnedBox, settings, LargeRadius);
        SetFill(AppsBox, settings, LargeRadius);
        SetFill(UserStrip, settings, LargeRadius);
    }

    /// <summary>Puts a fresh fill behind the box's content (replacing an earlier one).</summary>
    private void SetFill(Grid box, ShellSettings settings, double radius)
    {
        if (box.Children.Count > 0 && box.Children[0] is FrameworkElement { Tag: "fill" } old)
        {
            // Detached on the UI thread before it is dropped.
            SurfaceFill.Release(old);
            box.Children.RemoveAt(0);
        }

        FrameworkElement fill = SurfaceFill.Create(settings, _dark, radius);
        fill.Tag = "fill";
        box.Children.Insert(0, fill);
    }

    // ---------------------------------------------------------------- user strip and power

    private void LoadUser()
    {
        string name = UserInfo.DisplayName();
        UserName.Text = name;
        Avatar.DisplayName = name;
        AutomationProperties.SetName(Avatar, name);
        _ = LoadAvatarAsync();
    }

    private async Task LoadAvatarAsync()
    {
        try
        {
            string? path = await Task.Run(() => UserInfo.AccountPicturePath());
            if (path is null)
            {
                // No picture set: PersonPicture shows the name's initials.
                return;
            }

            Avatar.ProfilePicture = await LoadBitmapAsync(await File.ReadAllBytesAsync(path), 64);
        }
        catch (Exception ex)
        {
            Log.Warn("The account picture could not be shown.", ex);
        }
    }

    private void OnPowerClicked(object sender, RoutedEventArgs e)
    {
        if (PowerPanel.Children.Count > 0)
        {
            ResetPowerPanel();
            return;
        }

        PowerPanel.Children.Add(MakeTextButton("Restart", () => ConfirmPower("Restart now?", "Restart", "/r /t 0")));
        PowerPanel.Children.Add(MakeTextButton("Shut down", () => ConfirmPower("Shut down now?", "Shut down", "/s /t 0")));
    }

    /// <summary>Inline confirmation: the strip asks once more before anything happens.</summary>
    private void ConfirmPower(string question, string action, string arguments)
    {
        PowerPanel.Children.Clear();
        PowerPanel.Children.Add(new TextBlock { Text = question, VerticalAlignment = VerticalAlignment.Center });

        Button confirm = MakeTextButton(action, () => RunShutdown(action, arguments));
        if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out object? style) && style is Style accent)
        {
            confirm.Style = accent;
        }

        PowerPanel.Children.Add(confirm);
        PowerPanel.Children.Add(MakeTextButton("Cancel", ResetPowerPanel));
        confirm.Focus(FocusState.Programmatic);
    }

    private void ResetPowerPanel()
    {
        PowerPanel.Children.Clear();
    }

    private static void RunShutdown(string action, string arguments)
    {
        try
        {
            Log.Info("Power: " + action + " confirmed.");
            using Process? process = Process.Start(new ProcessStartInfo("shutdown.exe", arguments)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex)
        {
            Log.Error("Power: " + action + " failed.", ex);
        }
    }

    private static Button MakeTextButton(string text, Action onClick)
    {
        var button = new Button
        {
            Content = text,
            Height = 30,
            Padding = new Thickness(12, 0, 12, 0),
            CornerRadius = new CornerRadius(SmallRadius),
            VerticalAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(button, text);
        button.Click += (_, _) => onClick();
        return button;
    }

    // ---------------------------------------------------------------- search

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        ParseQuery();
        ApplyFilter();

        if (_filesMode)
        {
            _fileTimer.Stop();
            _fileTimer.Start();
        }
        else
        {
            _fileSearch?.Cancel();
        }
    }

    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter)
        {
            return;
        }

        e.Handled = true;
        StackPanel panel = _filesMode ? FilesPanel : AppsPanel;
        foreach (UIElement child in panel.Children)
        {
            if (child.Visibility == Visibility.Visible && child is FrameworkElement { Tag: string target })
            {
                LaunchAndClose(target);
                return;
            }
        }
    }

    private void ParseQuery()
    {
        string text = SearchInput.Text.Trim();
        if (text.StartsWith("files:", StringComparison.OrdinalIgnoreCase))
        {
            _filesMode = true;
            _query = text.Substring("files:".Length).Trim();
        }
        else if (text.StartsWith("apps:", StringComparison.OrdinalIgnoreCase))
        {
            _filesMode = false;
            _query = text.Substring("apps:".Length).Trim();
        }
        else
        {
            _filesMode = false;
            _query = text;
        }
    }

    private const int NoMatch = SearchRank.NoMatch;

    /// <summary>How well a name fits what was typed (see SearchRank; shared with the bar's search box).</summary>
    private int Rank(string name) => SearchRank.Of(name, _query);

    /// <summary>Best fit first; equal fits keep their usual order (pin order, or A to Z).</summary>
    private static int ByRank((int Rank, int Index) a, (int Rank, int Index) b)
    {
        return a.Rank != b.Rank ? a.Rank.CompareTo(b.Rank) : a.Index.CompareTo(b.Index);
    }

    /// <summary>Shows only matching pins and apps (or the file results), with honest empty states.</summary>
    private void ApplyFilter()
    {
        PinnedPanel.Children.Clear();
        var pins = new List<(int Rank, int Index)>();
        for (int i = 0; i < _pinTiles.Count; i++)
        {
            int rank = Rank(_pinTiles[i].Pin.Name);
            if (rank != NoMatch)
            {
                pins.Add((rank, i));
            }
        }

        pins.Sort(ByRank);
        foreach (var (_, index) in pins)
        {
            PinnedPanel.Children.Add(_pinTiles[index].Tile);
        }

        if (_pinTiles.Count == 0)
        {
            ShowNote(PinnedEmpty, "No pinned apps yet. Use the pin button next to an app in All apps.");
        }
        else if (PinnedPanel.Children.Count == 0)
        {
            ShowNote(PinnedEmpty, "No pinned app matches.");
        }
        else
        {
            PinnedEmpty.Visibility = Visibility.Collapsed;
        }

        if (_filesMode)
        {
            AppsHeader.Text = "Files";
            AppsPanel.Visibility = Visibility.Collapsed;
            FilesPanel.Visibility = Visibility.Visible;
            if (_query.Length < 2)
            {
                FilesPanel.Children.Clear();
                ShowNote(AppsEmpty, "Type at least two letters after files: to search your Desktop, Documents, Downloads, Pictures, Music and Videos.");
            }

            return;
        }

        AppsHeader.Text = _query.Length == 0 ? "All apps" : "Results";
        AppsPanel.Visibility = Visibility.Visible;
        FilesPanel.Visibility = Visibility.Collapsed;

        int shown = 0;
        var order = new List<(int Rank, int Index)>(_appRows.Count);
        for (int i = 0; i < _appRows.Count; i++)
        {
            AppRow row = _appRows[i];
            int rank = Rank(row.App.Name);
            bool match = rank != NoMatch;
            row.Row.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
            if (match)
            {
                shown++;
            }

            order.Add((rank, i));
        }

        // Best fit at the top (Enter opens the first one); rows are moved only when the order changes.
        order.Sort(ByRank);
        bool sameOrder = AppsPanel.Children.Count == order.Count;
        for (int i = 0; sameOrder && i < order.Count; i++)
        {
            sameOrder = ReferenceEquals(AppsPanel.Children[i], _appRows[order[i].Index].Row);
        }

        if (!sameOrder)
        {
            AppsPanel.Children.Clear();
            foreach (var (_, index) in order)
            {
                AppsPanel.Children.Add(_appRows[index].Row);
            }
        }

        if (!_host.Catalog.IsReady)
        {
            ShowNote(AppsEmpty, "Reading your apps...");
        }
        else if (shown == 0)
        {
            ShowNote(AppsEmpty, _appRows.Count == 0 ? "No apps found." : "No app matches.");
        }
        else
        {
            AppsEmpty.Visibility = Visibility.Collapsed;
        }
    }

    private static void ShowNote(TextBlock note, string text)
    {
        note.Text = text;
        note.Visibility = Visibility.Visible;
    }

    private async Task RunFileSearchAsync()
    {
        _fileSearch?.Cancel();
        if (!_filesMode || _query.Length < 2)
        {
            return;
        }

        var cancel = new CancellationTokenSource();
        _fileSearch = cancel;
        string query = _query;

        FilesPanel.Children.Clear();
        ShowNote(AppsEmpty, "Searching your files...");

        List<string> files;
        try
        {
            files = await Task.Run(() => FileSearch.Find(query, MaxFileResults, cancel.Token));
        }
        catch (Exception ex)
        {
            Log.Warn("File search failed.", ex);
            files = new List<string>();
        }

        // A newer search, or the menu closing, replaces this one.
        if (cancel.IsCancellationRequested || !_filesMode || query != _query)
        {
            return;
        }

        foreach (string file in files)
        {
            FilesPanel.Children.Add(CreateFileRow(file));
        }

        if (files.Count == 0)
        {
            ShowNote(AppsEmpty, "No file matches in your folders.");
        }
        else
        {
            AppsEmpty.Visibility = Visibility.Collapsed;
        }
    }

    private Button CreateFileRow(string path)
    {
        var glyph = new FontIcon { Glyph = FileGlyph, FontFamily = IconFont, FontSize = 18, Width = RowIconDip };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = Path.GetFileName(path), TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock
        {
            Text = Path.GetDirectoryName(path) ?? string.Empty,
            FontSize = 11,
            Opacity = 0.6,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        content.Children.Add(glyph);
        content.Children.Add(text);

        Button row = MakeRowButton(content, Path.GetFileName(path), 48);
        row.Tag = path;
        row.Click += (_, _) => LaunchAndClose(path);
        return row;
    }

    // ---------------------------------------------------------------- All apps

    private void OnCatalogChanged()
    {
        RebuildApps();
    }

    private void RebuildApps()
    {
        try
        {
            AppsPanel.Children.Clear();
            _appRows.Clear();

            foreach (AppEntry app in _host.Catalog.Apps)
            {
                AppRow row = CreateAppRow(app);
                _appRows.Add(row);
                AppsPanel.Children.Add(row.Row);
            }

            RebuildPins();
        }
        catch (Exception ex)
        {
            Log.Error("The All apps list could not be built.", ex);
        }
    }

    private AppRow CreateAppRow(AppEntry app)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        content.Children.Add(CreateIcon(app.Key, app, null, RowIconDip));
        content.Children.Add(new TextBlock
        {
            Text = app.Name,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        Button main = MakeRowButton(content, app.Name, 40);
        main.Click += (_, _) => LaunchAndClose(app.LaunchTarget);

        var pinGlyph = new FontIcon { FontFamily = IconFont, FontSize = 14 };
        var pin = new Button
        {
            Content = pinGlyph,
            Width = 32,
            Height = 32,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(SmallRadius),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = _hasTouch ? 1 : 0,
        };
        pin.Click += (_, _) => TogglePin(app);

        var row = new Grid { Tag = app.LaunchTarget };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(pin, 1);
        row.Children.Add(main);
        row.Children.Add(pin);

        // Pin button: hover only (and while it has keyboard focus); always on touch devices.
        row.PointerEntered += (_, _) => pin.Opacity = 1;
        row.PointerExited += (_, _) => pin.Opacity = _hasTouch || pin.FocusState != FocusState.Unfocused ? 1 : 0;
        pin.GotFocus += (_, _) => pin.Opacity = 1;
        pin.LostFocus += (_, _) => pin.Opacity = _hasTouch ? 1 : 0;

        var appRow = new AppRow(app, row, pin, pinGlyph);
        UpdatePinButton(appRow);
        return appRow;
    }

    private void UpdatePinButton(AppRow row)
    {
        bool pinned = _host.Pins.IsPinned(row.App.ToPin().Path);
        row.PinGlyph.Glyph = pinned ? UnpinGlyph : PinGlyph;
        string label = (pinned ? "Unpin " : "Pin ") + row.App.Name;
        AutomationProperties.SetName(row.Pin, label);
        ToolTipService.SetToolTip(row.Pin, pinned ? "Unpin" : "Pin");
    }

    private void TogglePin(AppEntry app)
    {
        PinnedApp pin = app.ToPin();
        if (_host.Pins.IsPinned(pin.Path))
        {
            _host.Pins.Unpin(pin.Path);
        }
        else
        {
            _host.Pins.Pin(pin);
        }
    }

    /// <summary>A full-width row: content on the left, no tile at rest, a background on hover.</summary>
    private static Button MakeRowButton(UIElement content, string name, double height)
    {
        var button = new Button
        {
            Content = content,
            Height = height,
            MinHeight = 0,
            Padding = new Thickness(8, 0, 8, 0),
            CornerRadius = new CornerRadius(MediumRadius),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        AutomationProperties.SetName(button, name);
        return button;
    }

    // ---------------------------------------------------------------- Pinned

    private void OnPinsChanged()
    {
        RebuildPins();
        foreach (AppRow row in _appRows)
        {
            UpdatePinButton(row);
        }
    }

    private void RebuildPins()
    {
        _pinTiles.Clear();

        var byPinPath = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (AppEntry app in _host.Catalog.Apps)
        {
            byPinPath.TryAdd(app.ToPin().Path, app);
        }

        foreach (PinnedApp pin in _host.Pins.Pins)
        {
            byPinPath.TryGetValue(pin.Path, out AppEntry? app);
            _pinTiles.Add((pin, CreatePinTile(pin, app)));
        }

        ApplyFilter();
    }

    private Grid CreatePinTile(PinnedApp pin, AppEntry? app)
    {
        var content = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Center };
        content.Children.Add(CreateIcon(app?.Key ?? pin.Path, app, pin.AppId is null ? pin.Path : null, TileIconDip));
        content.Children.Add(new TextBlock
        {
            Text = pin.Name,
            FontSize = 11,
            MaxLines = 2,
            TextWrapping = TextWrapping.Wrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        });

        var main = new Button
        {
            Content = content,
            Width = 64,
            Height = 72,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(2, 6, 2, 2),
            CornerRadius = new CornerRadius(MediumRadius),
            Background = new SolidColorBrush(Colors.Transparent),
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Top,
        };
        AutomationProperties.SetName(main, pin.Name);
        main.Click += (_, _) => LaunchAndClose(pin.LaunchTarget);

        var unpin = new Button
        {
            Content = new FontIcon { Glyph = UnpinGlyph, FontFamily = IconFont, FontSize = 10 },
            Width = 20,
            Height = 20,
            MinWidth = 0,
            MinHeight = 0,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(SmallRadius),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Opacity = _hasTouch ? 1 : 0,
        };
        AutomationProperties.SetName(unpin, "Unpin " + pin.Name);
        ToolTipService.SetToolTip(unpin, "Unpin");
        unpin.Click += (_, _) => _host.Pins.Unpin(pin.Path);

        var tile = new Grid { Width = 64, Height = 72 };
        tile.Children.Add(main);
        tile.Children.Add(unpin);

        // Unpin button: hover only (and while it has keyboard focus); always on touch devices.
        tile.PointerEntered += (_, _) => unpin.Opacity = 1;
        tile.PointerExited += (_, _) => unpin.Opacity = _hasTouch || unpin.FocusState != FocusState.Unfocused ? 1 : 0;
        unpin.GotFocus += (_, _) => unpin.Opacity = 1;
        unpin.LostFocus += (_, _) => unpin.Opacity = _hasTouch ? 1 : 0;

        return tile;
    }

    // ---------------------------------------------------------------- icons

    /// <summary>
    /// The app's real icon: from the catalog (shortcut icon or store logo), else from the pinned
    /// exe, else the first letter of the name. Images are cached for the run.
    /// </summary>
    private FrameworkElement CreateIcon(string cacheKey, AppEntry? app, string? exePath, double sizeDip)
    {
        var image = new Image
        {
            Width = sizeDip,
            Height = sizeDip,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        if (_icons.TryGetValue(cacheKey, out ImageSource? cached))
        {
            image.Source = cached;
            return image;
        }

        try
        {
            if (app?.Icon is IconPixels pixels)
            {
                image.Source = Remember(cacheKey, ToBitmap(pixels));
                return image;
            }

            if (app?.LogoBytes is byte[] bytes)
            {
                _ = SetLogoAsync(image, cacheKey, bytes);
                return image;
            }

            if (exePath is not null && File.Exists(exePath)
                && IconReader.FromExecutable(exePath, ListIconPixels) is IconPixels exeIcon)
            {
                image.Source = Remember(cacheKey, ToBitmap(exeIcon));
                return image;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("An app icon could not be shown.", ex);
        }

        string name = app?.Name ?? Path.GetFileNameWithoutExtension(cacheKey);
        return new TextBlock
        {
            Text = name.Length > 0 ? name.Substring(0, 1).ToUpperInvariant() : "?",
            Width = sizeDip,
            FontSize = sizeDip * 0.62,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
    }

    private async Task SetLogoAsync(Image image, string cacheKey, byte[] bytes)
    {
        try
        {
            if (!_icons.TryGetValue(cacheKey, out ImageSource? source))
            {
                source = Remember(cacheKey, await LoadBitmapAsync(bytes, ListIconPixels));
            }

            image.Source = source;
        }
        catch (Exception ex)
        {
            Log.Warn("A store-app logo could not be shown.", ex);
        }
    }

    private ImageSource Remember(string cacheKey, ImageSource source)
    {
        _icons[cacheKey] = source;
        return source;
    }

    internal static async Task<BitmapImage> LoadBitmapAsync(byte[] bytes, int decodePixels)
    {
        var bitmap = new BitmapImage
        {
            DecodePixelWidth = decodePixels,
            DecodePixelType = DecodePixelType.Physical,
        };

        using var memory = new InMemoryRandomAccessStream();
        await memory.WriteAsync(bytes.AsBuffer());
        memory.Seek(0);
        await bitmap.SetSourceAsync(memory);
        return bitmap;
    }

    internal static WriteableBitmap ToBitmap(IconPixels pixels)
    {
        var bitmap = new WriteableBitmap(pixels.Width, pixels.Height);
        using (Stream stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(pixels.Bgra, 0, pixels.Bgra.Length);
        }

        bitmap.Invalidate();
        return bitmap;
    }

    // ---------------------------------------------------------------- launching

    private void LaunchAndClose(string target)
    {
        HideMenu();
        Launcher.Start(target);
    }
}
